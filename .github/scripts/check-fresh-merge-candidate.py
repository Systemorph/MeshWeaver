#!/usr/bin/env python3
"""check-fresh-merge-candidate.py — a job that SELECTS what a run tests reads the same fresh merge the tests read.

WHY (policy `suites-test-fresh-merge`, Doc/Architecture/FreshMergeUnderTest → "One candidate")
---------------------------------------------------------------------------------------------
A pull request's suites test the head merged onto the CURRENT base tip, computed in the job
(`.github/actions/fresh-merge`). A lane that fans out has a planning job that decides WHAT the
shards test — the affected package closure, read from the tree's `requires` edges — and shards that
test it. When the planner only RESOLVES the tip (`resolve-only: 'true'`) and selects on the event's
checkout, it selects on GitHub's `refs/pull/<n>/merge`, built on an OLDER main, while the shards
merge onto the current tip. The two then read different candidates, and the moment main gains a
`requires` edge in between, the shards stage a set that lacks the new dependency.

Measured (MeshWeaver.Plugins#4043, run 39580565444, job 119167574507): Plugins#4039 added
`AI requires Training@^1.3.0` plus a `Training/Tour` node; the gate's plan computed the closure on
the stale merge ref (no edge), the shard tested the fresh merge (edge present), staged 23 packages
without Training, and failed "Install of 'AI' failed: NodeType(s) not registered: Training/Tour".

THE RULE (over every workflow that uses the fresh-merge action)
---------------------------------------------------------------
  A. A job that runs a SELECTOR (`affected-modules.py`, or any `--range` diff) applies the fresh
     merge — a fresh-merge step WITHOUT `resolve-only: 'true'` — BEFORE its first selector step.
  B. A job that `needs:` such a selecting job and applies the fresh merge takes its `main-sha` from
     that selecting job's outputs (`needs.<selector>.outputs.…`), so it merges onto the SAME tip and
     arrives at the SAME tree. Its own resolution, a caller input or an empty value is a second
     candidate.
  C. Non-vacuity: a tree whose fresh-merge workflows contain no selecting job at all is RED — the
     check would otherwise pass having checked nothing.

USAGE
-----
  check-fresh-merge-candidate.py [--root DIR]     gate the workflows under DIR/.github/workflows
  check-fresh-merge-candidate.py --self-test      prove every rule fires on its defect and not on its fix
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
    print("::error::check-fresh-merge-candidate.py needs PyYAML (pip install pyyaml)")
    sys.exit(2)

SELECTOR = re.compile(r"affected-modules\.py|--range\b")


def is_fresh(step: dict) -> bool:
    return isinstance(step.get("uses"), str) and "actions/fresh-merge" in step["uses"]


def applies_merge(step: dict) -> bool:
    """True only when the step is PROVABLY an applying merge: `resolve-only` omitted or a literal false.
    Anything else — a literal true, or an expression (`${{ 'true' }}`, `${{ inputs.x }}`) that this
    file cannot evaluate — may leave the checkout untouched, so it is never counted as a merge."""
    w = step.get("with") or {}
    if "resolve-only" not in w:
        return True
    v = w["resolve-only"]
    if v is False:
        return True
    return isinstance(v, str) and v.strip().strip("'\"").lower() == "false"


def is_selector(step: dict) -> bool:
    return isinstance(step.get("run"), str) and bool(SELECTOR.search(step["run"]))


def needs_of(job: dict) -> list[str]:
    n = job.get("needs") or []
    return [n] if isinstance(n, str) else list(n)


def check_tree(root: Path) -> tuple[list[str], int, int]:
    """Return (violations, selecting_jobs, consuming_jobs)."""
    violations: list[str] = []
    selecting_total = consuming_total = 0
    wf = root / ".github" / "workflows"
    files = sorted(p for p in wf.iterdir() if p.suffix in (".yml", ".yaml")) if wf.is_dir() else []
    if not files:
        return [f"::error::{wf} holds no workflow files — nothing to check is a failure, not a pass"], 0, 0
    for path in files:
        rel = path.relative_to(root)
        try:
            doc = yaml.safe_load(path.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            violations.append(f"::error file={rel}::not valid YAML: {e}")
            continue
        jobs = (doc or {}).get("jobs") if isinstance(doc, dict) else None
        if not isinstance(jobs, dict):
            continue
        steps_of = {jid: [s for s in (j.get("steps") or []) if isinstance(s, dict)]
                    for jid, j in jobs.items() if isinstance(j, dict)}
        if not any(is_fresh(s) for steps in steps_of.values() for s in steps):
            continue  # not a fresh-merge lane: nothing here tests a pull request's merged tree
        selecting: set[str] = set()
        for jid, steps in steps_of.items():
            first_sel = next((i for i, s in enumerate(steps) if is_selector(s)), None)
            if first_sel is None:
                continue
            selecting.add(jid)
            applying = [i for i, s in enumerate(steps) if is_fresh(s) and applies_merge(s)]
            if not applying or min(applying) > first_sel:
                how = ("does not provably merge (resolve-only is true, or an expression this check cannot evaluate)"
                       if any(is_fresh(s) for s in steps)
                       else "applies no fresh merge at all")
                violations.append(
                    f"::error file={rel}::job '{jid}' selects what the run tests (step "
                    f"'{steps[first_sel].get('name', first_sel)}') but {how} before it — it selects on GitHub's "
                    f"merge ref (an OLDER main) while the tests read the fresh merge onto the CURRENT tip. "
                    f"Apply the fresh merge before the selector (MeshWeaver.Plugins#4043)")
        selecting_total += len(selecting)
        for jid, steps in steps_of.items():
            if jid in selecting:
                continue
            planners = [n for n in needs_of(jobs[jid]) if n in selecting]
            if not planners:
                continue
            for s in steps:
                if not is_fresh(s):
                    continue
                consuming_total += 1
                if not applies_merge(s):
                    violations.append(
                        f"::error file={rel}::job '{jid}' tests what '{'/'.join(planners)}' selected but its fresh-merge "
                        f"step does not provably merge (resolve-only is {(s.get('with') or {}).get('resolve-only')!r}) — "
                        f"it keeps GitHub's merge ref while the selector read the fresh merge. Omit resolve-only")
                    continue
                main = str((s.get("with") or {}).get("main-sha", ""))
                if not any(f"needs.{p}.outputs." in main for p in planners):
                    violations.append(
                        f"::error file={rel}::job '{jid}' tests what '{'/'.join(planners)}' selected but merges onto "
                        f"main-sha {main!r}, not that job's output — a second candidate. Take main-sha from "
                        f"needs.{planners[0]}.outputs.* so both read one tree")
    if selecting_total == 0:
        violations.append("::error::no fresh-merge workflow carries a selecting job — the check found nothing to "
                          "check; its selector markers no longer match the lanes")
    return violations, selecting_total, consuming_total


GOOD = """
on: pull_request
jobs:
  plan:
    runs-on: ubuntu-latest
    timeout-minutes: 5
    outputs: {main: "${{ steps.fresh.outputs.main-sha }}"}
    steps:
      - uses: actions/checkout@v7
      - id: fresh
        uses: Systemorph/MeshWeaver/.github/actions/fresh-merge@main
        with: {main-sha: "${{ inputs.merge-main-sha }}"}
      - name: select
        run: python3 scripts/affected-modules.py --range "$M...HEAD" --json
  gate:
    needs: plan
    runs-on: ubuntu-latest
    timeout-minutes: 5
    steps:
      - uses: actions/checkout@v7
      - uses: Systemorph/MeshWeaver/.github/actions/fresh-merge@main
        with: {main-sha: "${{ needs.plan.outputs.main }}"}
"""


def self_test() -> int:
    cases: list[tuple[str, str, bool]] = [
        ("fixed-shape", GOOD, False),
        # The defect this exists for: the planner only names the tip and selects on the merge ref.
        ("plan-resolve-only", GOOD.replace('with: {main-sha: "${{ inputs.merge-main-sha }}"}',
                                           'with: {resolve-only: \'true\', main-sha: "${{ inputs.merge-main-sha }}"}'), True),
        # The merge exists but runs AFTER the selector — the selection still read the merge ref.
        ("merge-after-selector", GOOD.replace(
            """      - id: fresh
        uses: Systemorph/MeshWeaver/.github/actions/fresh-merge@main
        with: {main-sha: "${{ inputs.merge-main-sha }}"}
      - name: select
        run: python3 scripts/affected-modules.py --range "$M...HEAD" --json
""",
            """      - name: select
        run: python3 scripts/affected-modules.py --range "$M...HEAD" --json
      - id: fresh
        uses: Systemorph/MeshWeaver/.github/actions/fresh-merge@main
        with: {main-sha: "${{ inputs.merge-main-sha }}"}
"""), True),
        # A shard that resolves its own tip (or reads the caller's input) is a second candidate.
        ("shard-own-tip", GOOD.replace('with: {main-sha: "${{ needs.plan.outputs.main }}"}', 'with: {main-sha: ""}'), True),
        ("shard-input-tip", GOOD.replace('with: {main-sha: "${{ needs.plan.outputs.main }}"}',
                                         'with: {main-sha: "${{ inputs.merge-main-sha }}"}'), True),
        # A selecting job with no fresh merge at all, in a lane whose shards do merge.
        ("plan-no-merge", GOOD.replace(
            """      - id: fresh
        uses: Systemorph/MeshWeaver/.github/actions/fresh-merge@main
        with: {main-sha: "${{ inputs.merge-main-sha }}"}
      - name: select""", "      - name: select"), True),
        # An expression this check cannot evaluate is not a provable merge (#7028 review).
        ("plan-resolve-only-expression", GOOD.replace('with: {main-sha: "${{ inputs.merge-main-sha }}"}',
                                                      'with: {resolve-only: "${{ \'true\' }}", main-sha: "${{ inputs.merge-main-sha }}"}'), True),
        # A literal false IS a merge — the rule must not over-fire.
        ("plan-resolve-only-false", GOOD.replace('with: {main-sha: "${{ inputs.merge-main-sha }}"}',
                                                 'with: {resolve-only: \'false\', main-sha: "${{ inputs.merge-main-sha }}"}'), False),
        # A consuming shard that only resolves keeps the merge ref: a second candidate (#7028 review).
        ("shard-resolve-only", GOOD.replace('with: {main-sha: "${{ needs.plan.outputs.main }}"}',
                                            'with: {resolve-only: \'true\', main-sha: "${{ needs.plan.outputs.main }}"}'), True),
        # Non-vacuity: a fresh-merge lane with no selector at all.
        ("no-selector", GOOD.replace("python3 scripts/affected-modules.py --range \"$M...HEAD\" --json", "echo hi"), True),
    ]
    failures = 0
    for name, body, expect in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github" / "workflows").mkdir(parents=True)
            (root / ".github" / "workflows" / "t.yml").write_text(body, encoding="utf-8")
            violations, _, _ = check_tree(root)
            fired = bool(violations)
            ok = fired == expect
            failures += 0 if ok else 1
            print(f"self-test {'ok' if ok else 'FAIL':4} {name:28} expected={'fire' if expect else 'silent'} "
                  f"got={'fire' if fired else 'silent'}" + ("" if ok else f" {violations}"))
    if failures:
        print(f"::error::check-fresh-merge-candidate.py self-test: {failures} case(s) did not behave — the gate is not proven")
        return 1
    print("self-test: every rule fired on its defect and stayed silent on its fix")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--root", default=".")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    root = Path(args.root).resolve()
    violations, selecting, consuming = check_tree(root)
    for v in violations:
        print(v)
    print(f"check-fresh-merge-candidate: {selecting} selecting job(s), {consuming} consuming fresh-merge step(s), "
          f"{len(violations)} violation(s), root={root}")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
