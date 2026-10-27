#!/usr/bin/env python3
"""check-scheduled-lane-reporters.py — a workflow with a `schedule:` trigger reports its red somewhere durable.

WHY (MeshWeaver#7416)
---------------------
A scheduled run is attached to no pull request, no reviewer and no check list. Its red updates
nothing and pages nobody: red in an empty room, which from outside is indistinguishable from a gate
that cannot fail. Measured: core's `lock-pinned-digests.yml` was red on every run from 2026-09-15 to
2026-09-21 (runs 25-34) and nobody read it for six days, while its green was the stated precondition
for re-enabling the registry purge. The lane's own header even said so ("a scheduled-only red also
has no durable reporter"), and the guard that looked at it (`pinned-digests.yml`) asserted its
CONFIGURATION — correct on all ten failing days — never its OUTCOME.

The fleet already has the reporter; the defect was that nothing required a scheduled lane to use it.

THE RULE
--------
Every workflow under `.github/workflows/` whose `on:` carries `schedule` has ONE of:

  * the ci-failure LEDGER PAIR — a job calling `node-repo-ci-failure.yml` with `outcome: failure`
    AND one with `outcome: success`, under the SAME label (a failure-only reporter never clears a
    stale alert, and a stale alert is how the next real one gets skimmed past); or
  * a STANDING SENTINEL job — a job whose step runs `sentinel-issue.py find` (issue-taxonomy.yml,
    issue-text-names.yml), which opens, updates and closes one labelled issue itself;

and in both shapes the reporter job (the failure half, for the pair) must

  * `needs:` — directly or transitively — EVERY other job of the workflow except the reporters
    themselves. A job missing from the list fails without anyone hearing, which is the failure mode
    the reporter exists to remove; and
  * carry an `if:` with a status function that survives a failure (`always()`, `failure()` or
    `!cancelled()`). Without one GitHub applies the implicit `success()` and the reporter is SKIPPED
    on exactly the runs it exists for.

— OR it is listed in `.github/scheduled-lane-reporters.allow` as `<workflow file>  — <reason>`. That
file is the known debt and only SHRINKS: an entry for a workflow that no longer exists, no longer has
a schedule, or now HAS a reporter is stale and reds, and an entry without a reason reds.

USAGE
-----
  check-scheduled-lane-reporters.py [--root DIR] [--allow FILE]
  check-scheduled-lane-reporters.py --self-test

Exit 1 on any violation, each an `::error` annotation naming the file. Prints the denominator:
scheduled workflows found, how many report, how many are allow-listed.
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
    print("::error::check-scheduled-lane-reporters.py needs PyYAML (pip install pyyaml)")
    sys.exit(2)

LEDGER_LANE = re.compile(r"(?:^|/)node-repo-ci-failure\.ya?ml(?:@.*)?$")
SENTINEL_RUN = re.compile(r"sentinel-issue\.py\s+find\b")
SURVIVES = ("always()", "failure()", "!cancelled()", "! cancelled()")
DEFAULT_LEDGER_LABEL = "ci-main-red"


def triggers(doc: dict) -> dict:
    # PyYAML reads the bare key `on` as the boolean True.
    on = doc.get("on", doc.get(True))
    if isinstance(on, str):
        return {on: None}
    if isinstance(on, list):
        return {k: None for k in on}
    return on if isinstance(on, dict) else {}


def needs_of(job: dict) -> list[str]:
    n = job.get("needs") or []
    return [n] if isinstance(n, str) else [x for x in n if isinstance(x, str)]


def closure(jobs: dict, start: str) -> set[str]:
    seen: set[str] = set()
    stack = list(needs_of(jobs.get(start) or {}))
    while stack:
        j = stack.pop()
        if j in seen:
            continue
        seen.add(j)
        stack.extend(needs_of(jobs.get(j) or {}))
    return seen


def survives_failure(job: dict) -> bool:
    cond = str(job.get("if") or "").replace(" ", "")
    return any(s.replace(" ", "") in cond for s in SURVIVES)


def reporter_problems(jobs: dict) -> tuple[bool, list[str]]:
    """(has a reporter shape at all, the problems with it). Pure."""
    ledger: dict[str, list[tuple[str, str]]] = {}     # outcome -> [(job id, label)]
    sentinels: list[str] = []
    for jid, job in jobs.items():
        if not isinstance(job, dict):
            continue
        uses = str(job.get("uses") or "")
        if LEDGER_LANE.search(uses):
            w = job.get("with") or {}
            outcome = str(w.get("outcome", "")).strip()
            label = str(w.get("label") or DEFAULT_LEDGER_LABEL).strip()
            ledger.setdefault(outcome, []).append((jid, label))
        for step in job.get("steps") or []:
            if isinstance(step, dict) and SENTINEL_RUN.search(str(step.get("run") or "")):
                sentinels.append(jid)
                break
    reporters = {j for pairs in ledger.values() for j, _ in pairs} | set(sentinels)
    if not reporters:
        return False, []
    problems: list[str] = []
    failing: list[str] = []
    if ledger:
        fail_labels = {l for _, l in ledger.get("failure", [])}
        ok_labels = {l for _, l in ledger.get("success", [])}
        if not fail_labels:
            problems.append("calls node-repo-ci-failure.yml but never with `outcome: failure` — nothing files the red")
        for label in sorted(fail_labels - ok_labels):
            problems.append(f"files reds under `{label}` but no `outcome: success` job closes that ledger — "
                            "a failure-only reporter never clears a stale alert")
        failing.extend(j for j, _ in ledger.get("failure", []))
    failing.extend(sentinels)
    others = {j for j in jobs if j not in reporters}
    for jid in failing:
        missing = sorted(others - closure(jobs, jid))
        if missing:
            problems.append(f"reporter job '{jid}' does not `needs:` {', '.join(missing)} — a red there is never reported")
        if not survives_failure(jobs[jid]):
            problems.append(f"reporter job '{jid}' has no `if:` with always()/failure()/!cancelled() — the implicit "
                            "success() SKIPS it on every red it exists to report")
    return True, problems


def read_allow(path: Path) -> tuple[dict[str, str], list[str]]:
    entries: dict[str, str] = {}
    problems: list[str] = []
    if not path.is_file():
        return entries, problems
    for n, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        m = re.match(r"^(\S+)\s+—\s+(.+)$", line)
        if not m or not m.group(2).strip():
            problems.append(f"::error file={path.name},line={n}::`{line}` — an entry is `<workflow file>  — <reason>`; "
                            "an exemption without a reason is a skip nobody can audit")
            continue
        entries[m.group(1)] = m.group(2).strip()
    return entries, problems


def check_tree(root: Path, allow_path: Path) -> tuple[list[str], dict]:
    violations: list[str] = []
    stats = {"scheduled": 0, "reporting": 0, "allowed": 0}
    wf = root / ".github" / "workflows"
    files = sorted(p for p in wf.iterdir() if p.suffix in (".yml", ".yaml") and p.is_file()) if wf.is_dir() else []
    if not files:
        return [f"::error::{wf} holds no workflow files — nothing checked is a failure, not a pass"], stats
    allow, allow_problems = read_allow(allow_path)
    violations.extend(allow_problems)
    scheduled_names: set[str] = set()
    reporting_names: set[str] = set()
    for path in files:
        try:
            doc = yaml.safe_load(path.read_text(encoding="utf-8"))
        except yaml.YAMLError as e:
            violations.append(f"::error file=.github/workflows/{path.name}::not valid YAML: {e}")
            continue
        if not isinstance(doc, dict) or "schedule" not in triggers(doc):
            continue
        stats["scheduled"] += 1
        scheduled_names.add(path.name)
        jobs = doc.get("jobs") if isinstance(doc.get("jobs"), dict) else {}
        has, problems = reporter_problems(jobs)
        if has:
            reporting_names.add(path.name)
            for p in problems:
                violations.append(f"::error file=.github/workflows/{path.name}::{p}")
            if not problems:
                stats["reporting"] += 1
            continue
        if path.name in allow:
            stats["allowed"] += 1
            continue
        violations.append(
            f"::error file=.github/workflows/{path.name}::scheduled (`on: schedule`) with no durable reporter — its red "
            "would reach nobody (#7416). Add the ci-failure ledger pair (node-repo-ci-failure.yml, `outcome: failure` "
            "+ `outcome: success`, under the lane's own label — lock-pinned-digests.yml is the template) or a "
            "sentinel-issue.py report job; listing it in .github/scheduled-lane-reporters.allow is for known debt only")
    for name, reason in sorted(allow.items()):
        if name not in {p.name for p in files}:
            violations.append(f"::error file={allow_path.name}::stale entry `{name}` — no such workflow; delete the line")
        elif name not in scheduled_names:
            violations.append(f"::error file={allow_path.name}::stale entry `{name}` — it has no schedule any more; delete the line")
        elif name in reporting_names:
            violations.append(f"::error file={allow_path.name}::stale entry `{name}` — it reports now; delete the line (the list only shrinks)")
    return violations, stats


def self_test() -> int:
    pair = """
on: {schedule: [{cron: '0 1 * * *'}], push: {branches: [main]}}
jobs:
  a: {runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}
  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}
  red:
    needs: [b]
    if: always() && needs.b.result != 'success'
    uses: ./.github/workflows/node-repo-ci-failure.yml
    with: {outcome: failure, label: lane-x}
  green:
    needs: [b]
    if: always() && needs.b.result == 'success'
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-ci-failure.yml@main
    with: {outcome: success, label: lane-x}
"""
    sentinel = """
on: {schedule: [{cron: '0 1 * * *'}]}
jobs:
  a: {runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}
  report: {needs: [a], if: '${{ !cancelled() }}', runs-on: x, timeout-minutes: 5,
           steps: [{run: 'python3 .github/scripts/sentinel-issue.py find --repo r --label l'}]}
"""
    bare = "on: {schedule: [{cron: '0 1 * * *'}]}\njobs:\n  a: {runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}\n"
    unscheduled = "on: {push: {branches: [main]}}\njobs:\n  a: {runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}\n"
    cases = [
        # (name, workflow, allow file text, expect violation)
        ("ledger pair, every job covered", pair, "", False),
        ("standing sentinel", sentinel, "", False),
        ("THE INCIDENT: scheduled, no reporter", bare, "", True),
        ("scheduled, no reporter, allow-listed with a reason", bare, "t.yml  — known debt, tracked\n", False),
        ("allow entry without a reason", bare, "t.yml\n", True),
        ("not scheduled: nothing asked", unscheduled, "", False),
        ("stale allow: workflow has no schedule", unscheduled, "t.yml  — x\n", True),
        ("stale allow: workflow reports now", pair, "t.yml  — x\n", True),
        ("stale allow: no such workflow", pair, "gone.yml  — x\n", True),
        ("failure-only reporter (never closes)", pair.replace("outcome: success", "outcome: failure"), "", True),
        ("success-only reporter (never files)", pair.replace("outcome: failure", "outcome: success"), "", True),
        ("pair under two different labels", pair.replace("{outcome: success, label: lane-x}", "{outcome: success, label: lane-y}"), "", True),
        ("reporter misses a job in needs", pair.replace("  red:\n    needs: [b]", "  red:\n    needs: [a]"), "", True),
        ("reporter skipped on red (implicit success())", pair.replace("    if: always() && needs.b.result != 'success'\n", ""), "", True),
        ("sentinel with no failure-surviving if", sentinel.replace("if: '${{ !cancelled() }}', ", ""), "", True),
        ("`on:` as a list with schedule, no reporter", "on: [schedule, push]\njobs:\n  a: {runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}\n", "", True),
    ]
    failures = 0
    for name, body, allow, expect in cases:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / ".github" / "workflows").mkdir(parents=True)
            (root / ".github" / "workflows" / "t.yml").write_text(body, encoding="utf-8")
            allow_path = root / ".github" / "scheduled-lane-reporters.allow"
            allow_path.write_text(allow, encoding="utf-8")
            violations, _ = check_tree(root, allow_path)
            fired = bool(violations)
            ok = fired == expect
            failures += 0 if ok else 1
            print(f"self-test {'ok' if ok else 'FAIL':4} {name:52} expected={'fire' if expect else 'silent'} "
                  f"got={'fire' if fired else 'silent'}" + ("" if ok else f" {violations}"))
    with tempfile.TemporaryDirectory() as tmp:
        violations, _ = check_tree(Path(tmp), Path(tmp) / "x.allow")
        ok = bool(violations)
        failures += 0 if ok else 1
        print(f"self-test {'ok' if ok else 'FAIL':4} {'no workflows dir':52} expected=fire got={'fire' if ok else 'silent'}")
    if failures:
        print(f"::error::check-scheduled-lane-reporters.py self-test: {failures} case(s) did not behave — the gate is not proven")
        return 1
    print("self-test: every case fired on its defect and stayed silent on its fix")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--root", default=".")
    ap.add_argument("--allow", default=None, help="default: <root>/.github/scheduled-lane-reporters.allow")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    root = Path(a.root).resolve()
    allow = Path(a.allow) if a.allow else root / ".github" / "scheduled-lane-reporters.allow"
    violations, stats = check_tree(root, allow)
    for v in violations:
        print(v)
    print(f"check-scheduled-lane-reporters: {stats['scheduled']} scheduled workflow(s); {stats['reporting']} report "
          f"their red durably; {stats['allowed']} allow-listed as known debt; {len(violations)} violation(s); root={root}")
    return 1 if violations else 0


if __name__ == "__main__":
    sys.exit(main())
