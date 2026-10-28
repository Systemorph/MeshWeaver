"""Read ONE job or ONE step out of a GitHub Actions workflow, for the harnesses that EXECUTE a
lane's own shell rather than a copy of it.

Six harnesses (`test-cd-steps.py`, `test-gate-shard-plan.py`, `test-module-asset-landing.py`,
`test-module-publication.py`, `test-compile-check-reference-set.py`,
`test-release-availability-refusals.py`) each carried a private `extract_step`, and the copies had
drifted apart in what they REFUSED — which is the whole value of an extractor, because a harness
that silently extracts the wrong body, or nothing, reports a pass over a step the lane does not
run. This module keeps the strictest answer each copy gave, for all of them:

* the workflow file must exist (an absent lane is a refusal, never an empty pass);
* a step id must name EXACTLY ONE step — across every job, or within the one job the caller
  names. GitHub forbids duplicate ids only within a job, so two jobs may legally share one, and an
  extractor that took the first would hand the harness whichever it met first;
* the step must carry a non-empty `run:`;
* every `${{ }}` expression in it must be one the caller supplies deliberately; any other is a
  refusal, because a harness that has to guess a value is no longer executing the lane.

Errors are raised as `WorkflowYamlError`; each harness reports them in its own voice.

Imported by sibling path (`sys.path.insert(0, <this directory>)`); every harness that uses it runs
from a checkout of this repository (`dotnet-test.yml`), never as a single fetched file.
"""

from __future__ import annotations

import re
from pathlib import Path
from typing import Any, Mapping

_EXPRESSION = re.compile(r"\$\{\{([^}]*)\}\}")


class WorkflowYamlError(Exception):
    """The workflow does not have the shape the harness extracts from — never a pass."""


def load(workflow: Path) -> dict:
    """The parsed workflow. Refuses an absent file (MeshWeaver#3878)."""
    import yaml  # noqa: PLC0415 — the harnesses' own dependency, imported where they used it

    if not workflow.is_file():
        raise WorkflowYamlError(
            f"{workflow} does not exist — the lane this harness executes is missing, so there is "
            "nothing to prove. A harness must never report a pass for a lane it could not find.")
    doc = yaml.safe_load(workflow.read_text(encoding="utf-8"))
    if not isinstance(doc, dict):
        raise WorkflowYamlError(f"{workflow.name} does not parse to a mapping.")
    return doc


def jobs(doc: dict) -> dict[str, dict]:
    """The `jobs:` map, every value a mapping."""
    return {name: job for name, job in (doc.get("jobs") or {}).items() if isinstance(job, dict)}


def job(doc: dict, name: str, workflow_name: str = "the workflow") -> dict:
    """ONE job by its exact key — a key, never a prefix."""
    found = jobs(doc).get(name)
    if found is None:
        raise WorkflowYamlError(f"no `{name}` job in {workflow_name}.")
    return found


def step(workflow: Path, step_id: str, *, job_name: str | None = None) -> dict:
    """The ONE step with `id: <step_id>` — in `job_name` when given, otherwise across every job."""
    doc = load(workflow)
    scope = {job_name: job(doc, job_name, workflow.name)} if job_name else jobs(doc)
    owners = [(name, s) for name, j in scope.items()
              for s in (j.get("steps") or [])
              if isinstance(s, dict) and s.get("id") == step_id]
    # A YAML alias (`steps: *gate-steps` — node-repo-gate.yml's `bake` job runs the shard's steps)
    # makes two jobs hold the SAME step object: that is one step, not two, so it is counted once.
    # Two distinct steps that merely share an id still refuse below.
    seen: set[int] = set()
    owners = [(n, s) for n, s in owners if not (id(s) in seen or seen.add(id(s)))]
    where = f"job `{job_name}` of {workflow.name}" if job_name else workflow.name
    if not owners:
        raise WorkflowYamlError(
            f"no step with `id: {step_id}` in {where}. It was renamed, moved or deleted — and this "
            "harness will not report a pass for a step it could not find.")
    if len(owners) > 1:
        raise WorkflowYamlError(
            f"step id `{step_id}` appears in {len(owners)} steps of {where} "
            f"(jobs: {', '.join(n for n, _ in owners)}) — the harness cannot tell which one it is "
            "testing. Give each step a distinct id, or name the job.")
    return owners[0][1]


def fill_expressions(text: str, known: Mapping[str, str], what: str) -> str:
    """`text` with every `${{ expr }}` replaced from `known` (keyed by the stripped expression).
    Any expression not in `known` is a refusal — pass it through `env:`, or supply it on purpose."""

    def sub(match: re.Match) -> str:
        expr = match.group(1).strip()
        if expr not in known:
            raise WorkflowYamlError(
                f"{what} contains a ${{{{ {expr} }}}} expression this harness cannot supply. It "
                "refuses to execute a body it would have to rewrite — pass the value through `env:` "
                "instead, or supply it deliberately.")
        return known[expr]

    filled = _EXPRESSION.sub(sub, text)
    if "${{" in filled:  # an unterminated or nested opener the pattern did not match
        raise WorkflowYamlError(f"{what} still contains a `${{{{` this harness could not read as an expression.")
    return filled


def step_run(workflow: Path, step_id: str, *, job_name: str | None = None,
             expressions: Mapping[str, str] | None = None) -> str:
    """The step's `run:` body, every expression filled from `expressions` (none by default)."""
    found = step(workflow, step_id, job_name=job_name)
    body = found.get("run")
    if not body:
        raise WorkflowYamlError(f"step id `{step_id}` in {workflow.name} has no `run:` — nothing to execute.")
    return fill_expressions(body, expressions or {}, f"step id `{step_id}`'s `run:`")


def step_env(workflow: Path, step_id: str, *, job_name: str | None = None,
             expressions: Mapping[str, str] | None = None) -> dict[str, str]:
    """The step's `env:` map as strings, every expression filled exactly as `step_run` fills."""
    found: dict[str, Any] = step(workflow, step_id, job_name=job_name)
    return {k: fill_expressions(str(v), expressions or {}, f"step id `{step_id}`'s env `{k}`")
            for k, v in (found.get("env") or {}).items()}
