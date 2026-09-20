#!/usr/bin/env python3
"""Refuse a skip-trapdoor made by STEP ORDERING in a reusable validation lane.

GitHub skips every step after a failed one, and a skipped step publishes no failure. In a job that
is a required status-check context, that turns one red into "every guard behind it silently did not
run" — and a required context that reported `skipped` counts as SATISFIED under both classic
protection and rulesets.

Measured on MeshWeaver.SocialMedia#210 (2026-09-19 08:22Z): the vendored-resolver drift check failed
mid-job and **16 steps were skipped behind it**, including the PR-secret preflight, the stale-lock
check, the module-version check, the duplicate-key guard, the pin-comment guard and the no-op parity
guard. `validate / Validate node repos` is required in all five satellites, so every pull request in
those repositories was unguarded by all six while one unrelated file was red (Systemorph/MeshWeaver#4784).

AGENTS.md already legislates the class — "A gate NEVER tests its own inputs — no skip-trapdoors …
GitHub paints a skipped job the same colour as a passed one" — but the trapdoors it names are
`continue-on-error:` and an `if:` that asks whether a secret is set. This one is made by ORDERING,
so nothing in the file looks wrong on inspection and no existing guard can see it.

The rule: in a guarded job, every step must carry `if:` containing `!cancelled()` so that it reports
its own verdict regardless of what failed before it. The job still fails — a failed step fails the
job whether or not later steps run — so this trades nothing away. Only the PROLOGUE is exempt: the
steps that establish the workspace every later step reads, listed by name below, because running a
guard against a checkout that did not happen produces a confusing red rather than a verdict.
"""
from __future__ import annotations

import argparse
import pathlib
import sys

import yaml

# (workflow file, job id) pairs this rule is in force for: the reusable lanes whose job IS a
# required status-check context in the satellites, where a silent skip is indistinguishable from a
# pass. Add a lane here when it becomes required somewhere.
GUARDED_JOBS = [("node-repo-validate.yml", "validate")]

# The prologue. These establish the workspace every later step reads, so they are the one place
# where "if this failed, the rest cannot mean anything" is true. Named explicitly: a NEW setup step
# has to be added here deliberately, which is the point of a ratchet.
PROLOGUE_NAMES = {"full history and tags, quietly"}
PROLOGUE_USES = {"actions/checkout", "actions/setup-python"}


def _is_prologue(step: dict) -> bool:
    name = str(step.get("name", "")).strip().lower()
    if name in PROLOGUE_NAMES:
        return True
    uses = str(step.get("uses", ""))
    return any(uses.startswith(u) for u in PROLOGUE_USES)


def offenders(doc: dict, job_id: str) -> list[str]:
    job = (doc.get("jobs") or {}).get(job_id)
    if job is None:
        return [f"job '{job_id}' is not in this workflow — the rule names a job that no longer exists"]
    bad = []
    for i, step in enumerate(job.get("steps") or []):
        if _is_prologue(step):
            continue
        if "!cancelled()" not in str(step.get("if", "")):
            label = step.get("name") or step.get("uses") or f"step #{i + 1}"
            bad.append(str(label))
    return bad


def check(root: pathlib.Path) -> int:
    failed = 0
    for filename, job_id in GUARDED_JOBS:
        path = root / ".github" / "workflows" / filename
        if not path.exists():
            print(f"::error::{path} is missing — this rule's subject is gone, which is not a pass")
            failed = 1
            continue
        doc = yaml.safe_load(path.read_text(encoding="utf-8"))
        bad = offenders(doc, job_id)
        if not bad:
            print(f"OK {filename} :: {job_id} — every non-prologue step reports its own verdict")
            continue
        failed = 1
        print(
            f"::error::{filename} :: {job_id} — {len(bad)} step(s) would be SKIPPED behind an "
            f"earlier failure, and a skipped step publishes no failure. Add "
            f"`if: ${{{{ !cancelled() }}}}` (AND it with any existing condition):"
        )
        for label in bad:
            print(f"  - {label}")
    return failed


def self_test() -> int:
    """The control, in both directions — a rule that cannot fail is not a rule."""
    masked = yaml.safe_load(
        "jobs:\n"
        "  validate:\n"
        "    steps:\n"
        "      - uses: actions/checkout@v4\n"
        "      - name: a guard\n"
        "        run: python3 check.py\n"
    )
    reporting = yaml.safe_load(
        "jobs:\n"
        "  validate:\n"
        "    steps:\n"
        "      - uses: actions/checkout@v4\n"
        "      - name: a guard\n"
        "        if: ${{ !cancelled() }}\n"
        "        run: python3 check.py\n"
        "      - name: a conditional guard\n"
        "        if: ${{ !cancelled() && (inputs.thing) }}\n"
        "        run: python3 check.py\n"
    )
    problems = []
    if offenders(masked, "validate") != ["a guard"]:
        problems.append("a step with no `if:` was NOT named — the guard cannot see the defect it exists for")
    if offenders(reporting, "validate"):
        problems.append("a step that DOES carry !cancelled() was named — the guard would red a correct lane")
    if not offenders({"jobs": {}}, "validate"):
        problems.append("a missing job read as a pass — an absent subject is not a clean one")
    for p in problems:
        print(f"::error::self-test: {p}")
    print("self-test: OK" if not problems else "self-test: FAILED")
    return 1 if problems else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--root", default=".")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    return self_test() if args.self_test else check(pathlib.Path(args.root))


if __name__ == "__main__":
    sys.exit(main())
