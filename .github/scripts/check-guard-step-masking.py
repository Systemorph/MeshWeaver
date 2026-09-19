#!/usr/bin/env python3
"""check-guard-step-masking.py — in a one-job lane full of independent guards, a failing guard must
never SKIP the guards after it.

WHY THIS EXISTS (MeshWeaver#4784)
---------------------------------
`node-repo-validate.yml`'s `validate` job runs ~36 independent guards as consecutive steps. GitHub's
implicit step condition is `success()`, so the FIRST failing step skips every step behind it — and a
`skipped` step publishes no failure. Measured on MeshWeaver.SocialMedia#210 (2026-09-19 08:22Z): the
vendored-resolver drift check failed with `32 code line(s) differ` and **16 later steps reported
`skipped`**, among them `check-pr-secret-preflight.py`, the manifest-lock currency check, the
module-version check, the duplicate-key guard, the pin-drift guard and the no-op parity check.

`validate / Validate node repos` is a REQUIRED status-check context in all five satellites, and a
required context that reported `skipped` counts as SATISFIED under both classic protection and
rulesets. So while any satellite's vendored resolver was behind the canonical — which, because
`platform-ref` defaults to `main`, is every satellite from the instant a canonical change merges —
every pull request in that repository was unguarded by all six of those checks, and the only visible
symptom was one red about an unrelated file.

That is the skip-trapdoor AGENTS.md legislates against ("a gate NEVER tests its own inputs … GitHub
paints a skipped job the same colour as a passed one"), except the trapdoor here is made by STEP
ORDERING rather than by a `continue-on-error:` or an `if:`. Nothing in the workflow looks wrong on
inspection, which is why it needs a gate rather than a comment.

THE RULE
--------
For each (workflow, job) in SUBJECTS:

  * the declared PREREQUISITES are a PREFIX of the step list — a step that is allowed to mask what
    follows it may only be at the front, because a prerequisite in the middle masks every guard
    after it and that is the defect under a different name;
  * every step after that prefix carries an `if:` containing a status-check function that survives
    an earlier failure (`!cancelled()` or `always()`), so it REPORTS its own verdict instead of
    inheriting the previous step's;
  * every declared prerequisite matches a step that exists. A prerequisite naming a step that was
    renamed or removed is STALE and fails — a guard whose subject moved out from under it and that
    answers green has checked nothing.

A prerequisite is a step whose failure genuinely leaves the later steps nothing to say: the
checkout, the history fetch it needs, the Python it runs on. A FETCH of one guard is NOT a
prerequisite — it is that guard's own input, and its failure must not silence the other guards. Its
consumers then run and fail naming the file they could not open, which is a second red rather than a
silent skip; read the `::error::` from the fetch, which is the root.

USAGE
-----
  check-guard-step-masking.py [--root DIR]   gate the tree at DIR (default: cwd)
  check-guard-step-masking.py --self-test    prove the gate fires and stays silent

Exit 1 on any violation; every violation is an `::error::` annotation naming the workflow, the job
and the step.
"""
from __future__ import annotations

import argparse
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - CI installs PyYAML; locally `pip install pyyaml`
    print("::error::check-guard-step-masking.py needs PyYAML (pip install pyyaml)")
    sys.exit(2)

# The status-check functions that DROP the implicit `success()`, so the step runs after a failure.
# `failure()` and a bare `success()` deliberately are not here: neither makes a guard report on a run
# where an earlier guard failed AND this one would have passed.
SURVIVES_A_FAILURE = ("!cancelled()", "! cancelled()", "always()")

# (workflow file) -> (job id) -> the ordered PREFIX of steps allowed to mask what follows them.
# A step is matched by an exact `name:`, or by `uses:<prefix>` for a nameless action step.
SUBJECTS: dict[str, dict[str, tuple[str, ...]]] = {
    "node-repo-validate.yml": {
        "validate": (
            "uses:actions/checkout",
            "Full history and tags, quietly",
            "uses:actions/setup-python",
        ),
    },
}


def _step_id(step: dict) -> str:
    name = step.get("name")
    if isinstance(name, str) and name.strip():
        return name.strip()
    uses = step.get("uses")
    if isinstance(uses, str):
        return "uses:" + uses.split("@", 1)[0]
    return "<unnamed step>"


def _matches(step_id: str, declared: str) -> bool:
    if declared.startswith("uses:"):
        return step_id.startswith(declared)
    return step_id == declared


def check_tree(root: Path) -> tuple[list[str], int, int]:
    violations: list[str] = []
    steps_checked = 0
    jobs_checked = 0
    wf_dir = root / ".github" / "workflows"
    for wf_name, jobs in SUBJECTS.items():
        path = wf_dir / wf_name
        if not path.is_file():
            violations.append(
                f"::error::check-guard-step-masking: {wf_name} is not at .github/workflows/ under {root} — "
                f"this gate's subject moved; a gate that cannot read its subject must not pass"
            )
            continue
        try:
            doc = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
        except yaml.YAMLError as exc:
            violations.append(f"::error file=.github/workflows/{wf_name}::is not parseable YAML: {exc}")
            continue
        all_jobs = doc.get("jobs") or {}
        for job_id, prerequisites in jobs.items():
            job = all_jobs.get(job_id)
            if not isinstance(job, dict):
                violations.append(
                    f"::error file=.github/workflows/{wf_name}::job '{job_id}' is not declared — this gate's "
                    f"subject was renamed or removed, so it would answer green having checked nothing"
                )
                continue
            steps = job.get("steps") or []
            if not steps:
                violations.append(
                    f"::error file=.github/workflows/{wf_name}::job '{job_id}' declares no steps — refusing to "
                    f"pass vacuously"
                )
                continue
            jobs_checked += 1
            ids = [_step_id(s) for s in steps]
            # The prerequisites must be the PREFIX, in order, and each must exist.
            for offset, declared in enumerate(prerequisites):
                if offset >= len(ids):
                    violations.append(
                        f"::error file=.github/workflows/{wf_name}::job '{job_id}' declares prerequisite "
                        f"'{declared}' at position {offset} but the job has only {len(ids)} step(s) — the "
                        f"declaration is STALE"
                    )
                    continue
                if not _matches(ids[offset], declared):
                    violations.append(
                        f"::error file=.github/workflows/{wf_name}::job '{job_id}' step {offset} is "
                        f"'{ids[offset]}' but the declared prerequisite prefix expects '{declared}'. Either the "
                        f"step moved (a prerequisite must be at the FRONT — one in the middle masks every guard "
                        f"after it) or this declaration is stale"
                    )
            for index in range(len(prerequisites), len(steps)):
                steps_checked += 1
                condition = steps[index].get("if")
                text = "" if condition is None else str(condition)
                if not any(token in text for token in SURVIVES_A_FAILURE):
                    violations.append(
                        f"::error file=.github/workflows/{wf_name}::job '{job_id}' step {index} "
                        f"('{ids[index]}') has no `!cancelled()` in its `if:` — GitHub's implicit condition is "
                        f"`success()`, so an earlier guard's failure SKIPS this one, and a skipped step "
                        f"publishes no failure while a skipped required context counts as satisfied. Add "
                        f"`if: ${{{{ !cancelled() }}}}` (and `&&` it with any condition already there)"
                    )
    return violations, steps_checked, jobs_checked


_PREFIX = (
    "on: {workflow_call: {}}\n"
    "jobs:\n"
    "  validate:\n"
    "    runs-on: ubuntu-latest\n"
    "    timeout-minutes: 10\n"
    "    steps:\n"
    "      - uses: actions/checkout@v7\n"
    "      - name: Full history and tags, quietly\n"
    "        run: echo\n"
    "      - uses: actions/setup-python@v7\n"
)


def self_test() -> int:
    """Every check must FIRE on its defect and stay SILENT on its fix, or the gate is vacuous."""
    guarded = "      - name: guard A\n        if: ${{ !cancelled() }}\n        run: echo\n"
    guarded_b = "      - name: guard B\n        if: ${{ !cancelled() && inputs.x }}\n        run: echo\n"
    cases: list[tuple[str, str, bool]] = [
        ("every-guard-reports", _PREFIX + guarded + guarded_b, False),
        ("always-is-accepted", _PREFIX + "      - name: guard A\n        if: always()\n        run: echo\n", False),
        ("folded-scalar-accepted", _PREFIX + "      - name: guard A\n        if: >\n          !cancelled() && inputs.x\n        run: echo\n", False),
        # the defect itself, in each of the three shapes it arrives in
        ("bare-guard-masks", _PREFIX + guarded + "      - name: guard B\n        run: echo\n", True),
        ("condition-without-cancelled", _PREFIX + guarded + "      - name: guard B\n        if: ${{ inputs.x }}\n        run: echo\n", True),
        ("success-only-is-not-enough", _PREFIX + guarded + "      - name: guard B\n        if: ${{ success() }}\n        run: echo\n", True),
        ("failure-only-is-not-enough", _PREFIX + guarded + "      - name: guard B\n        if: ${{ failure() }}\n        run: echo\n", True),
        # a prerequisite that drifted out of the prefix, which is the same defect wearing a name
        ("prerequisite-not-at-front", (
            "on: {workflow_call: {}}\njobs:\n  validate:\n    runs-on: ubuntu-latest\n    steps:\n"
            "      - uses: actions/checkout@v7\n"
            "      - uses: actions/setup-python@v7\n"
            "      - name: Full history and tags, quietly\n        run: echo\n"
        ), True),
        ("prerequisite-renamed", (
            "on: {workflow_call: {}}\njobs:\n  validate:\n    runs-on: ubuntu-latest\n    steps:\n"
            "      - uses: actions/checkout@v7\n"
            "      - name: Full history and tags\n        run: echo\n"
            "      - uses: actions/setup-python@v7\n"
        ), True),
        ("job-renamed", "on: {workflow_call: {}}\njobs:\n  validate-repos:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", True),
        ("no-steps", "on: {workflow_call: {}}\njobs:\n  validate:\n    runs-on: ubuntu-latest\n    steps: []\n", True),
        ("not-yaml", "jobs: [unclosed\n  - ::\n", True),
    ]
    failures = 0
    for name, body, expect in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github" / "workflows").mkdir(parents=True)
            (root / ".github" / "workflows" / "node-repo-validate.yml").write_text(body, encoding="utf-8")
            violations, _, _ = check_tree(root)
            fired = bool(violations)
            if fired != expect:
                failures += 1
            print(f"self-test {'ok' if fired == expect else 'FAIL':4} {name:30} "
                  f"expected={'fire' if expect else 'silent'} got={'fire' if fired else 'silent'}")
    with tempfile.TemporaryDirectory() as tmp:  # the subject absent must FIRE, never pass vacuously
        violations, _, _ = check_tree(Path(tmp))
        fired = bool(violations)
        print(f"self-test {'ok' if fired else 'FAIL':4} {'subject-workflow-absent':30} expected=fire "
              f"got={'fire' if fired else 'silent'}")
        failures += 0 if fired else 1
    if failures:
        print(f"::error::check-guard-step-masking.py self-test: {failures} case(s) did not behave — the gate is not proven")
        return 1
    print("self-test: every case fired on its defect and stayed silent on its fix")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--root", default=".", help="repository root holding .github/workflows (default: cwd)")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is non-vacuous and exit")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    root = Path(args.root).resolve()
    violations, steps_checked, jobs_checked = check_tree(root)
    for v in violations:
        print(v)
    print(f"check-guard-step-masking: {jobs_checked} job(s), {steps_checked} independent guard step(s) checked, "
          f"{len(violations)} violation(s), root={root}")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
