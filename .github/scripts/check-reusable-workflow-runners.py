#!/usr/bin/env python3
"""check-reusable-workflow-runners.py — every job of a REUSABLE workflow picks its runner through the caller's org runner variables.

WHY THIS EXISTS (maintainer, 2026-09-17: "we still incur cost for github actions. wtf? please see that
it goes to 0" · "disable for any private repo" · "and when free capacity gone => defer to our infra")
----------------------------------------------------------------------------------------------------
This repository is PUBLIC, so its own GitHub-hosted minutes are free. But the `workflow_call` lanes it
publishes (`node-repo-*.yml`, `auto-arm.yml`, …) are called by PRIVATE repositories, and GitHub's rule
is that "billing for GitHub-hosted runners is always associated with the caller". Measured on
2026-09-17: Actions Linux cost for the Systemorph org was $4,174 for September 1–17, and the residual
bill was thousands of SMALL jobs on `ubuntu-latest` — each billed at least one minute — a large share
of them jobs INSIDE these lanes, charged to whichever private satellite called them.

THE SWITCH is two org variables of visibility `private`:

  * `MW_RUNNER`        → `aks-silos`       — any job that does NOT need a Docker daemon;
  * `MW_RUNNER_DOCKER` → `aks-silos-dind`  — any job that does (docker build/run/pull/cp/login, buildx,
                                              `services:`, `container:`, Testcontainers).

A called workflow's `vars` context resolves from the CALLER. A private caller therefore sees both
variables and the job lands on the self-hosted ARC scale set; this public repository sees NEITHER
(visibility `private`), so its own calls fall back to the free hosted `ubuntu-latest`. One value flip
at org level moves the whole fleet; one edit rolls it back.

THE RULE
--------
In every workflow under `.github/workflows/` that is triggered by `workflow_call`, every job either

  * `uses:` another reusable workflow — exempt here (that lane is checked in its own file); or
  * has a `runs-on:` that is ONE expression `${{ A || B || … || 'ubuntu-latest' }}` where
      - the LAST operand is the literal `'ubuntu-latest'` — the fallback for a repository that cannot
        see the variables, i.e. this PUBLIC one — or an `inputs.<name>` whose non-empty DEFAULT ends
        in it (`runs-on: ${{ inputs.runner }}`). Any other literal is refused: `'aks-silos'` would put
        a public repository's own runs on self-hosted runners, and no fallback at all gives a public
        run an EMPTY label, which GitHub refuses before a job is scheduled;
      - every other operand is `vars.MW_RUNNER`, `vars.MW_RUNNER_DOCKER` or `inputs.<name>`;
      - at least one `vars.MW_RUNNER`/`vars.MW_RUNNER_DOCKER` is reachable — directly, or through the
        DEFAULT of an `inputs.<name>` operand, which must itself obey this same rule (an expression
        in a `workflow_call` input default may read `vars`, and it reads the caller's). A bare
        `inputs.runner` whose default is `ubuntu-latest` is exactly the bill this exists to stop.

A literal label (`ubuntu-latest`, `ubuntu-24.04`, `aks-silos`, a list, a group mapping), a matrix- or
needs-derived label, and a selected-visibility variable (`MW_RUNNER_HEAVY`, `MW_RUNNER_GATE` — a
repository added to their selection would silently change where THIS repository's own runs go) are
all refused, each by name.

USAGE
-----
  check-reusable-workflow-runners.py [--root DIR]            gate the tree at DIR (default: cwd)
  check-reusable-workflow-runners.py --self-test [--root DIR] prove the gate fires and stays silent;
                                                              with --root, also MUTATE every real
                                                              runner expression back to a literal and
                                                              demand a fire for each one

Exit 1 on any violation; every violation is a `::error` annotation carrying the file and the job id.
Doc: Doc/Architecture/SelfHostedRunners ("Where jobs run — GitHub Actions cost is zero").
"""
from __future__ import annotations

import argparse
import re
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - the CI step installs PyYAML; locally `pip install pyyaml`
    print("::error::check-reusable-workflow-runners.py needs PyYAML (pip install pyyaml)")
    sys.exit(2)

RUNNER_VARS = ("MW_RUNNER", "MW_RUNNER_DOCKER")
PUBLIC_FALLBACK = "'ubuntu-latest'"
EXPR = re.compile(r"^\$\{\{(.*)\}\}$", re.S)
OPERAND_VAR = re.compile(r"^vars\.([A-Za-z0-9_]+)$")
OPERAND_INPUT = re.compile(r"^inputs\.([A-Za-z0-9_-]+)$")
OPERAND_LITERAL = re.compile(r"^'[^']*'$")

LIGHT = "${{ vars.MW_RUNNER || 'ubuntu-latest' }}"
DOCKER = "${{ vars.MW_RUNNER_DOCKER || 'ubuntu-latest' }}"
HOW = (f"use `runs-on: {LIGHT}` (no Docker daemon needed) or `runs-on: {DOCKER}` (docker/buildx/"
       "services/container/Testcontainers)")


def workflow_files(root: Path) -> list[Path]:
    wf = root / ".github" / "workflows"
    if not wf.is_dir():
        return []
    return sorted(p for p in wf.iterdir() if p.suffix in (".yml", ".yaml") and p.is_file())


def triggers(doc: dict) -> object:
    # PyYAML (YAML 1.1) reads the bare key `on` as the boolean True.
    return doc.get("on", doc.get(True))


def is_reusable(doc: object) -> bool:
    if not isinstance(doc, dict):
        return False
    on = triggers(doc)
    if isinstance(on, str):
        return on == "workflow_call"
    if isinstance(on, list):
        return "workflow_call" in on
    if isinstance(on, dict):
        return "workflow_call" in on
    return False


def declared_inputs(doc: dict) -> dict:
    on = triggers(doc)
    if isinstance(on, dict) and isinstance(on.get("workflow_call"), dict):
        inputs = on["workflow_call"].get("inputs")
        return inputs if isinstance(inputs, dict) else {}
    return {}


def judge(value: object, inputs: dict, where: str, allow_inputs: bool = True) -> str | None:
    """None when `value` obeys the rule; otherwise the reason it does not."""
    if not isinstance(value, str):
        return (f"{where} is {value!r} — a literal list or group mapping names a runner the caller "
                f"pays for; {HOW}")
    m = EXPR.match(value.strip())
    if not m:
        return (f"{where} is the literal `{value}` — called from a PRIVATE repository this bills the "
                f"caller (a literal self-hosted label would put this PUBLIC repository's own runs on "
                f"self-hosted runners); {HOW}")
    operands = [o.strip() for o in m.group(1).split("||")]
    if any(not o for o in operands) or any(tok in m.group(1) for tok in ("&&", "(", ")", "==", "!=")):
        return f"{where} `{value}` is not a plain `a || b || 'ubuntu-latest'` chain; {HOW}"
    last = operands[-1]
    tail_input = OPERAND_INPUT.match(last) if allow_inputs else None
    if last != PUBLIC_FALLBACK:
        # The fallback may also live in the DEFAULT of a trailing input (`${{ inputs.runner }}`
        # whose default is `${{ vars.MW_RUNNER || 'ubuntu-latest' }}`) — judged below, and that
        # default must be non-empty, or a caller passing nothing gets an empty label.
        if not tail_input:
            return (f"{where} `{value}` does not end in the fallback {PUBLIC_FALLBACK} — the org runner "
                    f"variables are invisible to this PUBLIC repository, so its own runs need the free "
                    f"hosted label (none, or a self-hosted one, is refused); {HOW}")
        name = tail_input.group(1)
        if name in inputs and isinstance(inputs[name], dict) and inputs[name].get("default") in (None, ""):
            return (f"{where} `{value}` ends in inputs.{name}, whose default is empty — a caller that "
                    f"passes nothing gets no runner label at all; end the chain in {PUBLIC_FALLBACK} "
                    f"or give the input a default that does")
    operands_to_judge = operands if last != PUBLIC_FALLBACK else operands[:-1]
    reaches_var = False
    for o in operands_to_judge:
        if OPERAND_LITERAL.match(o):
            return f"{where} `{value}` has the literal {o} before the fallback — nothing after it can apply; {HOW}"
        v = OPERAND_VAR.match(o)
        if v:
            if v.group(1) not in RUNNER_VARS:
                return (f"{where} `{value}` reads vars.{v.group(1)} — only {', '.join('vars.' + n for n in RUNNER_VARS)} "
                        f"are the fleet's private-visibility runner switch; a selected-visibility variable "
                        f"changes where THIS public repository runs the moment it is selected; {HOW}")
            reaches_var = True
            continue
        i = OPERAND_INPUT.match(o)
        if i and allow_inputs:
            name = i.group(1)
            if name not in inputs or not isinstance(inputs[name], dict):
                return f"{where} `{value}` reads inputs.{name}, which this workflow_call does not declare"
            default = inputs[name].get("default")
            if default in (None, ""):
                continue  # an empty default contributes nothing; a later operand must reach a variable
            why = judge(default, inputs, f"the default of input `{name}`", allow_inputs=False)
            if why:
                return (f"{where} `{value}` reads inputs.{name}, and {why}")
            reaches_var = True
            continue
        return (f"{where} `{value}` has the operand `{o}` — only vars.MW_RUNNER, vars.MW_RUNNER_DOCKER"
                f"{' and inputs.<name>' if allow_inputs else ''} may choose a runner; {HOW}")
    if not reaches_var:
        return (f"{where} `{value}` never reaches vars.MW_RUNNER / vars.MW_RUNNER_DOCKER — a caller that "
                f"passes nothing lands on the hosted label and pays for it; {HOW}")
    return None


def check_file(path: Path, rel: str) -> tuple[list[str], int, int, bool]:
    """Return (violations, jobs_checked, jobs_exempt, is_reusable)."""
    try:
        doc = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as e:
        return [f"::error file={rel}::not valid YAML: {e}"], 0, 0, False
    if not is_reusable(doc):
        return [], 0, 0, False
    violations: list[str] = []
    checked = exempt = 0
    jobs = doc.get("jobs")
    if not isinstance(jobs, dict) or not jobs:
        return [f"::error file={rel}::a workflow_call workflow with no jobs"], 0, 0, True
    inputs = declared_inputs(doc)
    for job_id, job in jobs.items():
        if not isinstance(job, dict):
            violations.append(f"::error file={rel}::job '{job_id}' is not a mapping")
            continue
        if "uses" in job:
            exempt += 1
            continue
        checked += 1
        if "runs-on" not in job:
            violations.append(f"::error file={rel}::job '{job_id}' has no runs-on; {HOW}")
            continue
        why = judge(job["runs-on"], inputs, f"job '{job_id}' runs-on")
        if why:
            violations.append(f"::error file={rel}::{why}")
    return violations, checked, exempt, True


def check_tree(root: Path) -> tuple[list[str], int, int, int]:
    """Return (violations, reusable_files, jobs_checked, jobs_exempt)."""
    files = workflow_files(root)
    if not files:
        return [f"::error::{root}/.github/workflows has no workflow files — nothing to gate is a failure, not a pass"], 0, 0, 0
    violations: list[str] = []
    reusable = checked = exempt = 0
    for path in files:
        v, c, e, r = check_file(path, str(path.relative_to(root)))
        violations += v
        checked += c
        exempt += e
        reusable += 1 if r else 0
    if reusable == 0:
        violations.append(f"::error::{root}/.github/workflows holds no workflow_call workflow — this gate is "
                          f"core's, where the fleet's lanes live; nothing to gate is a failure, not a pass")
    return violations, reusable, checked, exempt


def _unit_cases() -> list[tuple[str, bool]]:
    head = "on:\n  workflow_call:\n    inputs:\n      runner:\n        type: string\n        default: {default}\njobs:\n"

    def wf(runs_on: str, default: str = "''", extra: str = "") -> str:
        return head.replace("{default}", default) + f"  a:\n    runs-on: {runs_on}\n    timeout-minutes: 5\n    steps: [{{run: echo}}]\n" + extra

    cases: list[tuple[str, str, bool]] = [
        # (name, workflow body, expect_violation)
        ("light-var", wf(LIGHT), False),
        ("docker-var", wf(DOCKER), False),
        ("no-spaces", wf("${{vars.MW_RUNNER||'ubuntu-latest'}}"), False),
        ("input-default-var", wf("${{ inputs.runner }}", default=DOCKER), False),
        ("input-then-var", wf("${{ inputs.runner || vars.MW_RUNNER_DOCKER || 'ubuntu-latest' }}"), False),
        ("var-then-input-default-var", wf("${{ vars.MW_RUNNER_DOCKER || inputs.runner }}", default=LIGHT), False),
        ("uses-exempt", head.replace("{default}", "''") + "  a:\n    uses: ./.github/workflows/x.yml\n", False),
        ("not-reusable-out-of-scope", "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", False),
        # 🚨 THE REGRESSION this gate exists for — a bare hosted label inside a lane a private repo calls.
        ("bare-ubuntu-latest", wf("ubuntu-latest"), True),
        ("bare-ubuntu-24.04", wf("ubuntu-24.04"), True),
        ("literal-self-hosted", wf("aks-silos"), True),
        ("label-list", wf("[self-hosted, linux]"), True),
        ("group-mapping", wf("{group: arc}"), True),
        ("selected-visibility-var", wf("${{ vars.MW_RUNNER_HEAVY || 'ubuntu-latest' }}"), True),
        ("self-hosted-fallback", wf("${{ vars.MW_RUNNER || 'aks-silos' }}"), True),
        ("no-fallback", wf("${{ vars.MW_RUNNER }}"), True),
        ("matrix-label", wf("${{ matrix.os }}"), True),
        ("literal-before-fallback", wf("${{ 'ubuntu-22.04' || 'ubuntu-latest' }}"), True),
        ("expression-in-text", wf("x-${{ vars.MW_RUNNER || 'ubuntu-latest' }}"), True),
        ("and-expression", wf("${{ vars.MW_RUNNER && vars.MW_RUNNER || 'ubuntu-latest' }}"), True),
        ("input-default-hosted", wf("${{ inputs.runner }}", default="ubuntu-latest"), True),
        ("input-default-empty-only", wf("${{ inputs.runner || 'ubuntu-latest' }}"), True),
        ("tail-input-default-empty", wf("${{ vars.MW_RUNNER || inputs.runner }}"), True),
        ("tail-input-default-self-hosted", wf("${{ inputs.runner }}", default="${{ vars.MW_RUNNER || 'aks-silos' }}"), True),
        ("input-undeclared", wf("${{ inputs.other || vars.MW_RUNNER || 'ubuntu-latest' }}"), True),
        ("input-default-reads-input", wf("${{ inputs.runner }}", default="${{ inputs.runner || vars.MW_RUNNER || 'ubuntu-latest' }}"), True),
        ("missing-runs-on", head.replace("{default}", "''") + "  a:\n    timeout-minutes: 5\n    steps: [{run: echo}]\n", True),
        ("second-job-bare", wf(LIGHT, extra="  b:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n"), True),
        ("trigger-string-form", "on: workflow_call\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", True),
        ("trigger-list-form", "on: [workflow_call, push]\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", True),
        ("not-yaml", "on: workflow_call\njobs: [unclosed\n  - ::\n", True),
    ]
    results: list[tuple[str, bool]] = []
    for name, body, expect in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github" / "workflows").mkdir(parents=True)
            (root / ".github" / "workflows" / "t.yml").write_text(body, encoding="utf-8")
            # A second, clean reusable file so "no workflow_call at all" never decides a case.
            (root / ".github" / "workflows" / "clean.yml").write_text(wf(LIGHT), encoding="utf-8")
            violations, _, _, _ = check_tree(root)
            fired = bool(violations)
            results.append((f"{name}: expected {'fire' if expect else 'silent'}, got {'fire' if fired else 'silent'}", fired == expect))
    with tempfile.TemporaryDirectory() as tmp:  # no workflow_call workflow anywhere must fail, never pass vacuously
        root = Path(tmp)
        (root / ".github" / "workflows").mkdir(parents=True)
        (root / ".github" / "workflows" / "t.yml").write_text("on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n", encoding="utf-8")
        fired = bool(check_tree(root)[0])
        results.append((f"no-reusable-workflow-in-tree: expected fire, got {'fire' if fired else 'silent'}", fired))
    return results


def _real_tree_cases(root: Path) -> list[tuple[str, bool]]:
    """The controls that cannot drift from the tree: the real lanes pass as committed, and turning ANY
    one of their runner expressions back into a literal label fires."""
    results: list[tuple[str, bool]] = []
    violations, reusable, checked, _ = check_tree(root)
    results.append((f"real tree as committed: {reusable} reusable workflow(s), {checked} job(s), "
                    f"{len(violations)} violation(s) — expected silent", not violations and reusable > 0))
    pattern = re.compile(r"^(\s*(?:runs-on|default):\s*)\$\{\{[^}]*vars\.MW_RUNNER(?:_DOCKER)?\b[^}]*\}\}\s*$")
    mutants = 0
    for path in workflow_files(root):
        text = path.read_text(encoding="utf-8")
        if not is_reusable(yaml.safe_load(text)):
            continue
        lines = text.split("\n")
        for n, line in enumerate(lines):
            m = pattern.match(line)
            if not m:
                continue
            mutants += 1
            mutated = lines[:n] + [m.group(1) + "ubuntu-latest"] + lines[n + 1:]
            with tempfile.TemporaryDirectory() as tmp:
                target = Path(tmp) / path.name
                target.write_text("\n".join(mutated), encoding="utf-8")
                fired = bool(check_file(target, path.name)[0])
            results.append((f"mutant {path.name}:{n + 1} `{line.strip()}` → literal ubuntu-latest: "
                            f"expected fire, got {'fire' if fired else 'silent'}", fired))
    results.append((f"the tree carries runner expressions to mutate: {mutants} found — expected > 0", mutants > 0))
    return results


def self_test(root: str | None) -> int:
    cases = _unit_cases()
    if root:
        print(f"  MODE: unit cases + real-tree controls over {Path(root).resolve()}")
        cases += _real_tree_cases(Path(root).resolve())
    else:
        print("  NOTE: no --root given — only the unit cases ran; pass --root to mutate the real lanes too.")
    for name, ok in cases:
        print(f"  {'ok  ' if ok else 'FAIL'} {name}")
    bad = [n for n, ok in cases if not ok]
    print(f"  {len(cases) - len(bad)}/{len(cases)} control(s) passed")
    if bad:
        print(f"::error::check-reusable-workflow-runners.py self-test: {len(bad)} control(s) did not behave — the gate is not proven")
        return 1
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--root", default=None, help="repository root holding .github/workflows (default: cwd)")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is non-vacuous and exit")
    args = ap.parse_args()
    if args.self_test:
        return self_test(args.root)
    root = Path(args.root or ".").resolve()
    violations, reusable, checked, exempt = check_tree(root)
    for v in violations:
        print(v)
    print(f"check-reusable-workflow-runners: {reusable} reusable workflow(s), {checked} job(s) checked, "
          f"{exempt} nested-call job(s) exempt, {len(violations)} violation(s), root={root}")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
