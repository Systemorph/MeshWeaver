#!/usr/bin/env python3
"""Every CI job carries a hard wall-clock cap, and the cap is at most 45 minutes.

WHY THIS EXISTS (maintainer, 2026-09-02: "hard cut ci runs after 45min — we pay all this")
------------------------------------------------------------------------------------------
GitHub's default `timeout-minutes` is 360. A job with no explicit cap therefore runs for SIX
HOURS when it hangs, and it bills a runner the whole time. Measured the morning this was written:
the reusable module-pack lane's `pack` job had no cap; `dotnet test MeshWeaver.Mcp.Test` wedged
before its first test on EVERY run in MeshWeaver.Plugins; 19 runs sat `in_progress` at once, each
holding a runner up to 360 minutes, and — because the repo's required checks `needs:` that job —
no PR could merge and `main` never reached publish-bake, which starved every satellite of a sealed
publication. One missing line, fleet-wide consequence, paid by the minute.

The cap is also the doctrine: a job that runs past 45 minutes is STUCK, not slow (AGENTS.md — over
budget means find what is not completing, never raise the bound). Measured on 2026-09-02, the
longest honest jobs in the fleet were the Plugins portal-host shards (25–30 min), release-images
(36–41 min) and the Education install shards (~27 min); everything else finishes well under 20.

THE RULE
--------
Every job in every workflow under `.github/workflows/` either

  * `uses:` a reusable workflow — exempt HERE, because GitHub ignores `timeout-minutes` on such a
    job; the cap lives on the jobs INSIDE the reusable workflow, which this same check gates in the
    repository that defines it (Systemorph/MeshWeaver's `node-repo-*.yml` lanes run this script
    on themselves); or
  * declares `timeout-minutes: <literal integer>` with `1 <= value <= MAX` (default 45).

An EXPRESSION (`${{ … }}`) is refused: a cap that can only be evaluated at run time cannot be
proven to hold, and the whole point is that the bound is provable by reading the file.

USAGE
-----
  check-workflow-timeouts.py [--root DIR] [--max 45]      gate the tree at DIR (default: cwd)
  check-workflow-timeouts.py --self-test                  prove the gate fires and stays silent

Node repos do not copy this file: their `validate` lane (node-repo-validate.yml) fetches it from
the platform at the pinned platform ref and runs it against the caller's tree — the same
centralization as compile-check.py. Exit 1 on any violation; every violation is a `::error`
annotation carrying the file and the job id.
"""
from __future__ import annotations

import argparse
import os
import sys
import tempfile
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - the CI step installs PyYAML; locally `pip install pyyaml`
    print("::error::check-workflow-timeouts.py needs PyYAML (pip install pyyaml)")
    sys.exit(2)

DEFAULT_MAX = 45


def workflow_files(root: Path) -> list[Path]:
    wf = root / ".github" / "workflows"
    if not wf.is_dir():
        return []
    return sorted(p for p in wf.iterdir() if p.suffix in (".yml", ".yaml") and p.is_file())


def check_tree(root: Path, max_minutes: int) -> tuple[list[str], int, int]:
    """Return (violations, jobs_checked, jobs_exempt)."""
    violations: list[str] = []
    checked = exempt = 0
    files = workflow_files(root)
    if not files:
        violations.append(f"::error::{root}/.github/workflows has no workflow files — nothing to gate is a failure, not a pass")
        return violations, 0, 0
    for path in files:
        rel = path.relative_to(root)
        try:
            doc = yaml.safe_load(path.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:  # a workflow that does not parse cannot be proven capped
            violations.append(f"::error file={rel}::not valid YAML: {e}")
            continue
        jobs = (doc or {}).get("jobs") if isinstance(doc, dict) else None
        if not isinstance(jobs, dict) or not jobs:
            violations.append(f"::error file={rel}::declares no jobs — a workflow file with nothing to cap is not a workflow")
            continue
        for job_id, job in jobs.items():
            if not isinstance(job, dict):
                violations.append(f"::error file={rel}::job '{job_id}' is not a mapping")
                continue
            if "uses" in job:
                exempt += 1
                continue
            checked += 1
            if "timeout-minutes" not in job:
                violations.append(
                    f"::error file={rel}::job '{job_id}' has no timeout-minutes — GitHub's default is 360; "
                    f"the fleet cap is {max_minutes}. Add `timeout-minutes: {max_minutes}` (or lower) under runs-on:"
                )
                continue
            value = job["timeout-minutes"]
            if isinstance(value, bool) or not isinstance(value, int):
                violations.append(
                    f"::error file={rel}::job '{job_id}' timeout-minutes is {value!r} — it must be a literal integer "
                    f"<= {max_minutes}; an expression or a string cannot be proven to hold by reading the file"
                )
                continue
            if value < 1 or value > max_minutes:
                violations.append(
                    f"::error file={rel}::job '{job_id}' timeout-minutes is {value} — the fleet cap is {max_minutes}. "
                    f"A job that needs longer is stuck, not slow: find what is not completing, never raise the bound"
                )
    return violations, checked, exempt


def self_test() -> int:
    """Every check must FIRE on its defect and stay SILENT on its fix, or the gate is vacuous."""
    cases: list[tuple[str, str, bool]] = [
        # (name, workflow yaml, expect_violation)
        ("ok-at-cap", "jobs:\n  a:\n    runs-on: ubuntu-latest\n    timeout-minutes: 45\n    steps: [{run: echo}]\n", False),
        ("ok-below-cap", "jobs:\n  a:\n    runs-on: ubuntu-latest\n    timeout-minutes: 10\n    steps: [{run: echo}]\n", False),
        ("missing", "jobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", True),
        ("over-cap", "jobs:\n  a:\n    runs-on: ubuntu-latest\n    timeout-minutes: 60\n    steps: [{run: echo}]\n", True),
        ("expression", "jobs:\n  a:\n    runs-on: ubuntu-latest\n    timeout-minutes: ${{ inputs.t }}\n    steps: [{run: echo}]\n", True),
        ("string", "jobs:\n  a:\n    runs-on: ubuntu-latest\n    timeout-minutes: '45'\n    steps: [{run: echo}]\n", True),
        ("zero", "jobs:\n  a:\n    runs-on: ubuntu-latest\n    timeout-minutes: 0\n    steps: [{run: echo}]\n", True),
        ("uses-exempt", "jobs:\n  a:\n    uses: Systemorph/MeshWeaver/.github/workflows/x.yml@abc\n    with: {a: 1}\n", False),
        ("second-job-missing", "jobs:\n  a:\n    runs-on: ubuntu-latest\n    timeout-minutes: 5\n    steps: [{run: echo}]\n  b:\n    runs-on: ubuntu-latest\n    steps: [{run: echo}]\n", True),
        ("no-jobs", "name: x\non: push\n", True),
        ("not-yaml", "jobs: [unclosed\n  - ::\n", True),
    ]
    failures = 0
    for name, body, expect in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github" / "workflows").mkdir(parents=True)
            (root / ".github" / "workflows" / "t.yml").write_text(body, encoding="utf-8")
            violations, _, _ = check_tree(root, DEFAULT_MAX)
            fired = bool(violations)
            verdict = "ok" if fired == expect else "FAIL"
            if fired != expect:
                failures += 1
            print(f"self-test {verdict:4} {name:20} expected={'fire' if expect else 'silent'} got={'fire' if fired else 'silent'}")
    with tempfile.TemporaryDirectory() as tmp:  # a tree with no workflows dir must fail, never pass vacuously
        violations, _, _ = check_tree(Path(tmp), DEFAULT_MAX)
        fired = bool(violations)
        print(f"self-test {'ok' if fired else 'FAIL':4} {'no-workflows-dir':20} expected=fire got={'fire' if fired else 'silent'}")
        failures += 0 if fired else 1
    if failures:
        print(f"::error::check-workflow-timeouts.py self-test: {failures} case(s) did not behave — the gate is not proven")
        return 1
    print("self-test: every case fired on its defect and stayed silent on its fix")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--root", default=".", help="repository root holding .github/workflows (default: cwd)")
    ap.add_argument("--max", type=int, default=DEFAULT_MAX, help=f"the cap in minutes (default {DEFAULT_MAX})")
    ap.add_argument("--self-test", action="store_true", help="prove the gate is non-vacuous and exit")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    root = Path(args.root).resolve()
    violations, checked, exempt = check_tree(root, args.max)
    for v in violations:
        print(v)
    print(f"check-workflow-timeouts: {checked} job(s) checked, {exempt} reusable-call job(s) exempt, "
          f"{len(violations)} violation(s), cap={args.max} min, root={root}")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
