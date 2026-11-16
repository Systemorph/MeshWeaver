#!/usr/bin/env python3
"""The core test-shard weight table: measured, refreshed by script, and checked.

`.github/scripts/shard-weights.txt` is the table `shard-assign.sh` balances the eight `Run tests
(shard k)` jobs with (greedy LPT, heaviest unit first onto the least-loaded shard). It used to be a
heredoc inside `shard-assign.sh`, re-measured by hand from log markers whenever somebody noticed a
long pole. It drifted every time: the LPT loop balances the NUMBERS, so a stale table keeps
printing eight even loads while one runner carries the long pole. Measured on run 43742571208
(2026-11-15): the printed loads were 389-396 s on every shard while the shards ran 4.8-9.6 min
(Doc/Architecture/CiProfile2026-11, section 4).

Modes:

  refresh [--runs N] [--repo O/R] [--out FILE]
      Reads the newest green `MeshWeaver Build and Test` runs over REST (pull requests and pushes
      alike), takes every `Run tests (shard k)` job that succeeded, and pairs each unit's
      `▶ <label> starting` line with the `[CI] … elapsed=<s>s` marker that ends it. A unit's weight
      is the MAX of its newest three exit=0 observations (size for the bad case: the per-unit
      headroom guard reads the same numbers). Split parts are measured PER PART, because the
      every-Nth class split is uneven and a total ÷ N weight lets two heavy parts stack on one
      shard. Writes the table with its provenance (the runs it read and when).

  check-assignment [--shards N]
      Runs `shard-assign.sh` for every shard under TEST_SELECTION=all and under an incremental
      selection, and fails unless every discovered test project (every part of a split one) is
      assigned EXACTLY once — nothing dropped, nothing duplicated. Prints the predicted loads.

  check-current [--runs N] [--repo O/R]
      Re-measures (as `refresh` does, without writing) and fails when a unit's measured weight
      drifted from the committed table by >= 25 % AND > 30 s, when a measured unit has no row in
      the table, or when the window measured nothing at all. A scheduled unit the window did not
      observe (a selection skipped it) is not compared; the run prints how many units it compared. Run by the weekly `Shard weights stay measured` workflow — never by a pull
      request, because a green shard must not go red over a project that merely got slower.

  --self-test
      Proves the parser, the aggregation, the table round-trip and both checks can fail.

Only REST is used (`gh api`), never GraphQL.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from collections import defaultdict

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TABLE = os.path.join(ROOT, ".github", "scripts", "shard-weights.txt")
ASSIGN = os.path.join(ROOT, ".github", "scripts", "shard-assign.sh")
WORKFLOW = os.path.join(ROOT, ".github", "workflows", "dotnet-test.yml")
WORKFLOW_FILE = "dotnet-test.yml"
SHARD_JOB = re.compile(r"^Run tests \(shard (\d+)\)$")
# `[CI] 21:03:04 ▶ MeshWeaver.Graph.Test (part 2/4) starting (52 class arg(s))`
START = re.compile(r"\[CI\] \d\d:\d\d:\d\d ▶ (?P<name>[A-Za-z0-9_.]+)(?: \(part (?P<p>\d+)/(?P<n>\d+)\))? starting")
# `[CI] MeshWeaver.Graph.Test exit=0 elapsed=221s` (a failing unit carries its label and a reason)
END = re.compile(r"\[CI\] (?P<name>[A-Za-z0-9_.]+)(?: \(part \d+/\d+\))? exit=(?P<rc>\d+)\b.*? elapsed=(?P<s>\d+)s")
ANSI = re.compile(r"\x1b\[[0-9;]*m")
KEEP = 3                # observations per unit the weight is the max of
DRIFT_PCT, DRIFT_ABS = 25, 30
DEFAULT_RUNS = 40


# ─────────────────────────────── parsing ────────────────────────────────────────────

def parse_log(text: str) -> list[tuple[str, int, int]]:
    """[(unit, rc, seconds)] in log order; unit is `Name` or `Name#p/n`."""
    out: list[tuple[str, int, int]] = []
    pending: str | None = None
    for raw in text.splitlines():
        line = ANSI.sub("", raw)
        m = START.search(line)
        if m:
            pending = m["name"] + (f"#{m['p']}/{m['n']}" if m["n"] and int(m["n"]) > 1 else "")
            continue
        m = END.search(line)
        if m and pending and pending.split("#", 1)[0] == m["name"]:
            out.append((pending, int(m["rc"]), int(m["s"])))
            pending = None
    return out


def aggregate(observations: list[tuple[str, int, int]]) -> dict[str, int]:
    """Newest-first observations → {unit: max of its newest KEEP exit=0 seconds}."""
    seen: dict[str, list[int]] = defaultdict(list)
    for unit, rc, secs in observations:
        if rc == 0 and len(seen[unit]) < KEEP:
            seen[unit].append(secs)
    return {u: max(v) for u, v in seen.items() if v}


# ─────────────────────────────── the table ──────────────────────────────────────────

def read_table(path: str) -> tuple[dict[str, int], dict[str, int], dict[str, int]]:
    """→ (project totals, project parts, part weights)."""
    totals: dict[str, int] = {}
    parts: dict[str, int] = {}
    partw: dict[str, int] = {}
    with open(path, encoding="utf-8") as fh:
        for lineno, line in enumerate(fh, 1):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            f = line.split()
            try:
                if len(f) < 2:
                    raise ValueError("expected '<seconds> <project>[#k/n] [parts]'")
                if "#" in f[1]:
                    partw[f[1]] = int(f[0])
                else:
                    totals[f[1]] = int(f[0])
                    parts[f[1]] = int(f[2]) if len(f) >= 3 else 1
            except ValueError as e:
                raise SystemExit(f"shard-weights: {path}:{lineno}: malformed row {line!r}: {e}")
    return totals, parts, partw


def unit_weights(totals: dict[str, int], parts: dict[str, int], partw: dict[str, int]) -> dict[str, int]:
    """Every schedulable unit with the weight shard-assign.sh gives it."""
    out: dict[str, int] = {}
    for name, total in totals.items():
        n = parts.get(name, 1)
        if n <= 1:
            out[name] = total
        else:
            for i in range(1, n + 1):
                out[f"{name}#{i}/{n}"] = partw.get(f"{name}#{i}/{n}", total // n)
    return out


def render_table(measured: dict[str, int], current_parts: dict[str, int], runs: list[str],
                 now: str, carried: dict[str, int], line: str = "main") -> str:
    """The committed file. A project keeps its declared parts count (splitting is a decision, see
    shard-assign.sh's SPLIT RULE); parts measured under a DIFFERENT count are dropped, so a re-split
    project falls back to total ÷ N until it has been measured under the new count."""
    totals: dict[str, int] = {}
    partrows: dict[str, int] = {}
    names = {u.split("#", 1)[0] for u in measured} | set(current_parts) | set(carried)
    for name in sorted(names):
        n = current_parts.get(name, 1)
        if n <= 1:
            if name in measured:
                totals[name] = measured[name]
            elif name in carried:
                totals[name] = carried[name]
            continue
        got = {i: measured[f"{name}#{i}/{n}"] for i in range(1, n + 1) if f"{name}#{i}/{n}" in measured}
        if len(got) == n:
            for i, s in got.items():
                partrows[f"{name}#{i}/{n}"] = s
            totals[name] = sum(got.values())
        elif name in carried:
            totals[name] = carried[name]
    lines = [
        "# Core test-shard weights — wall-clock SECONDS per schedulable unit.",
        "# GENERATED by `.github/scripts/shard-weights.py refresh`; read by shard-assign.sh (LPT balance)",
        "# and by ShardWeightHeadroomGuard (per-unit headroom against the 8m cap).",
        "# Format: `<seconds> <project> [parts]` for a project (its TOTAL; the third column splits it),",
        "#         `<seconds> <project>#<k>/<parts>` for one measured part of a split project.",
        "# A unit's weight is the MAX of its newest three exit=0 observations. Change a project's parts",
        "# count by hand (the SPLIT RULE in shard-assign.sh), then refresh once the new parts have run.",
        f"# measured-at: {now}",
        f"# source-runs ({line or 'every line'}): {' '.join(runs) if runs else '(none)'}",
    ]
    for name in sorted(totals, key=lambda k: (-totals[k], k)):
        n = current_parts.get(name, 1)
        lines.append(f"{totals[name]} {name}" + (f" {n}" if n > 1 else ""))
        for i in range(1, n + 1):
            key = f"{name}#{i}/{n}"
            if key in partrows:
                lines.append(f"{partrows[key]} {key}")
    return "\n".join(lines) + "\n"


# ─────────────────────────────── REST ───────────────────────────────────────────────

def gh(path: str) -> str:
    p = subprocess.run(["gh", "api", path], capture_output=True, text=True)
    if p.returncode != 0:
        raise SystemExit(f"shard-weights: gh api {path} failed (exit {p.returncode}): {p.stderr.strip()[-300:]}")
    return p.stdout


def on_line(run: dict, line: str, base_of_branch=lambda branch: None) -> bool:
    """A run of this delivery line: a push to it, or a pull request into it. A pull-request run's
    `pull_requests` is EMPTY once its pull request has merged or closed (most of the window), so the base
    is then asked of `base_of_branch(head_branch)` (the pull request for that head branch)."""
    if not line:
        return True
    if run.get("event") != "pull_request":
        return run.get("head_branch") == line
    bases = [(p.get("base") or {}).get("ref") for p in run.get("pull_requests") or []]
    if not bases:
        bases = [base_of_branch(run.get("head_branch") or "")]
    return line in bases


def measure(repo: str, runs: int, line: str = "") -> tuple[dict[str, int], list[str]]:
    owner = repo.split("/")[0]
    bases: dict[str, str | None] = {}

    def base_of_branch(branch: str) -> str | None:
        """The base of the newest pull request whose head is this branch (one REST call per branch)."""
        if branch and branch not in bases:
            prs = json.loads(gh(f"repos/{repo}/pulls?head={owner}:{branch}&state=all&per_page=1"))
            bases[branch] = (prs[0].get("base") or {}).get("ref") if prs else None
        return bases.get(branch)

    candidates: list[dict] = []
    for page in range(1, 6):
        listing = json.loads(gh(f"repos/{repo}/actions/workflows/{WORKFLOW_FILE}/runs?status=success&per_page=100&page={page}"))
        batch = listing.get("workflow_runs", [])
        candidates += [r for r in batch if on_line(r, line, base_of_branch)]
        if len(candidates) >= runs or len(batch) < 100:
            break
    observations: list[tuple[str, int, int]] = []
    used: list[str] = []
    for run in candidates[:runs]:
        jobs = json.loads(gh(f"repos/{repo}/actions/runs/{run['id']}/jobs?filter=latest&per_page=100"))
        shard_jobs = [j for j in jobs.get("jobs", []) if SHARD_JOB.match(j["name"]) and j.get("conclusion") == "success"]
        got = False
        for j in sorted(shard_jobs, key=lambda j: j["name"]):
            obs = parse_log(gh(f"repos/{repo}/actions/jobs/{j['id']}/logs"))
            observations.extend(obs)
            got = got or bool(obs)
        if got:
            used.append(str(run["id"]))
    return aggregate(observations), used


# ─────────────────────────────── checks ─────────────────────────────────────────────

def discovered_projects(root: str) -> list[str]:
    """What shard-assign.sh schedules: `find test -name '*.csproj' ! -path '*/bin/*'`."""
    out = []
    for d, _, files in os.walk(os.path.join(root, "test")):
        for f in files:
            rel = os.path.relpath(os.path.join(d, f), root).replace("\\", "/")
            if f.endswith(".csproj") and "/bin/" not in "/" + rel:
                out.append(rel)
    return sorted(out)


def run_assign(root: str, script: str, index: int, total: int, env_extra: dict[str, str]) -> tuple[list[str], str]:
    env = dict(os.environ, SHARD_INDEX=str(index), SHARD_TOTAL=str(total), **env_extra)
    p = subprocess.run(["bash", script], cwd=root, env=env, capture_output=True, text=True)
    if p.returncode != 0:
        raise SystemExit(f"shard-weights: shard-assign.sh failed for {index}/{total}: {p.stderr.strip()[-400:]}")
    return [l.strip() for l in p.stdout.splitlines() if l.strip()], p.stderr


def check_assignment(root: str, script: str, table: str, total: int) -> list[str]:
    """Problems (empty = every project assigned exactly once, under both selections)."""
    _, parts, _ = read_table(table)
    projects = discovered_projects(root)
    problems: list[str] = []
    names = [os.path.splitext(os.path.basename(p))[0] for p in projects]
    subset = sorted(set(names[::2]))
    for label, extra, owed in (
        ("all", {"TEST_SELECTION": "all"}, projects),
        ("incremental", {"TEST_SELECTION": "incremental", "TEST_PROJECTS": ",".join(subset)},
         [p for p in projects if os.path.splitext(os.path.basename(p))[0] in subset]),
    ):
        expected: list[str] = []
        for p in owed:
            n = parts.get(os.path.splitext(os.path.basename(p))[0], 1)
            expected += [p] if n <= 1 else [f"{p}#{i}/{n}" for i in range(1, n + 1)]
        got: list[str] = []
        for i in range(total):
            units, _ = run_assign(root, script, i, total, dict(extra, SHARD_WEIGHTS_FILE=table))
            got += units
        dup = sorted({u for u in got if got.count(u) > 1})
        missing = sorted(set(expected) - set(got))
        extra_units = sorted(set(got) - set(expected))
        if not expected:
            problems.append(f"[{label}] discovered no test project — the check would pass on nothing")
        for u in dup:
            problems.append(f"[{label}] assigned more than once: {u}")
        for u in missing:
            problems.append(f"[{label}] dropped (on no shard): {u}")
        for u in extra_units:
            problems.append(f"[{label}] assigned but not owed: {u}")
    return problems


def default_weight(script: str = ASSIGN) -> int:
    """shard-assign.sh's DEFAULT_WEIGHT — the weight it gives an unlisted project. Read, never restated."""
    m = re.search(r"^DEFAULT_WEIGHT=(\d+)$", open(script, encoding="utf-8").read(), re.M)
    if not m:
        raise SystemExit(f"shard-weights: DEFAULT_WEIGHT not found in {script}")
    return int(m.group(1))


def predicted_loads(root: str, table: str, total: int, script: str = ASSIGN) -> list[int]:
    totals, parts, partw = read_table(table)
    weights = unit_weights(totals, parts, partw)
    dflt = default_weight(script)
    units = []
    for p in discovered_projects(root):
        name = os.path.splitext(os.path.basename(p))[0]
        n = parts.get(name, 1)
        for u in ([name] if n <= 1 else [f"{name}#{i}/{n}" for i in range(1, n + 1)]):
            units.append((weights.get(u, dflt), u))
    loads = [0] * total
    for w, _ in sorted(units, key=lambda x: (-x[0], x[1])):
        loads[loads.index(min(loads))] += w
    return loads


def drift(table: str, measured: dict[str, int], scheduled: list[str]) -> list[str]:
    totals, parts, partw = read_table(table)
    weights = unit_weights(totals, parts, partw)
    problems = []
    for unit in sorted(set(scheduled)):
        if unit not in measured:
            continue  # not observed in the window (a selection skipped it) — nothing to compare
        if unit not in weights:
            problems.append(f"{unit}: measured {measured[unit]} s, no row in the table")
            continue
        t, m = weights[unit], measured[unit]
        if abs(m - t) > DRIFT_ABS and abs(m - t) * 100 >= DRIFT_PCT * max(t, 1):
            problems.append(f"{unit}: table {t} s, measured {m} s")
    return problems


def scheduled_units(root: str, table: str) -> list[str]:
    _, parts, _ = read_table(table)
    out = []
    for p in discovered_projects(root):
        name = os.path.splitext(os.path.basename(p))[0]
        n = parts.get(name, 1)
        out += [name] if n <= 1 else [f"{name}#{i}/{n}" for i in range(1, n + 1)]
    return out


def shard_total() -> int:
    m = re.search(r"\n\s*CI_SHARD_TOTAL:\s*(\d+)", open(WORKFLOW, encoding="utf-8").read())
    if not m:
        raise SystemExit("shard-weights: CI_SHARD_TOTAL not found in dotnet-test.yml")
    return int(m.group(1))


# ─────────────────────────────── self-test ──────────────────────────────────────────

def self_test() -> int:
    fails = 0

    def expect(cond: bool, what: str) -> None:
        nonlocal fails
        print(("  ok   " if cond else "  FAIL ") + what)
        fails += 0 if cond else 1

    log = "\n".join([
        "2026-11-15T17:50:01Z [CI] 17:50:01 ▶ A.Test starting (0 class arg(s))",
        "2026-11-15T17:50:31Z \x1b[0m[CI] A.Test exit=0 elapsed=30s",
        "2026-11-15T17:50:32Z [CI] 17:50:32 ▶ B.Test (part 2/3) starting (4 class arg(s))",
        "2026-11-15T17:52:32Z [CI] B.Test exit=0 elapsed=120s",
        "2026-11-15T17:52:33Z [CI] 17:52:33 ▶ C.Test starting (0 class arg(s))",
        "2026-11-15T18:00:33Z [CI] C.Test exit=124 TIMEOUT (8m wall-clock cap hit) elapsed=480s",
        "2026-11-15T18:00:34Z [CI] D.Test exit=0 elapsed=9s",  # no start line: not attributable
    ])
    obs = parse_log(log)
    expect(obs == [("A.Test", 0, 30), ("B.Test#2/3", 0, 120), ("C.Test", 124, 480)],
           f"a log yields each unit with its part and exit code, unpaired markers dropped ({obs})")
    agg = aggregate([("A.Test", 0, 40), ("A.Test", 0, 30), ("A.Test", 0, 35), ("A.Test", 0, 99),
                     ("C.Test", 124, 480), ("C.Test", 0, 60)])
    expect(agg == {"A.Test": 40, "C.Test": 60},
           f"the weight is the max of the newest three GREEN observations ({agg})")

    expect(on_line({"event": "push", "head_branch": "main"}, "main")
           and on_line({"event": "pull_request", "head_branch": "feat/x", "pull_requests": [{"base": {"ref": "main"}}]}, "main")
           and not on_line({"event": "pull_request", "head_branch": "feat/x", "pull_requests": [{"base": {"ref": "main3"}}]}, "main")
           and not on_line({"event": "push", "head_branch": "main3"}, "main")
           and on_line({"event": "push", "head_branch": "main3"}, "")
           and on_line({"event": "pull_request", "head_branch": "fix/y", "pull_requests": []}, "main", lambda b: "main")
           and not on_line({"event": "pull_request", "head_branch": "fix/y", "pull_requests": []}, "main", lambda b: "main3"),
           "a run belongs to a line by its push branch or its pull request's base; another line's runs are not measured")
    with tempfile.TemporaryDirectory() as t:
        txt = render_table({"A.Test": 40, "B.Test#1/2": 100, "B.Test#2/2": 150, "C.Test#1/3": 70},
                           {"B.Test": 2, "C.Test": 2}, ["1", "2"], "2026-11-15T00:00:00Z", {"C.Test": 200})
        path = os.path.join(t, "w.txt")
        open(path, "w").write(txt)
        totals, parts, partw = read_table(path)
        expect(totals == {"B.Test": 250, "A.Test": 40, "C.Test": 200} and parts["B.Test"] == 2,
               "the table round-trips; a split total is the sum of its parts; a part measured under "
               "another parts count is dropped and the old total carried")
        expect(unit_weights(totals, parts, partw) == {"B.Test#1/2": 100, "B.Test#2/2": 150, "A.Test": 40,
                                                      "C.Test#1/2": 100, "C.Test#2/2": 100},
               "a measured part keeps its own weight; an unmeasured split falls back to total / N")
        expect(drift(path, {"A.Test": 80, "B.Test#1/2": 110}, ["A.Test", "B.Test#1/2"]) == ["A.Test: table 40 s, measured 80 s"],
               "drift fires on >=25 % and >30 s, and not on a small move")
        expect(drift(path, {"New.Test": 50}, ["New.Test"]) == ["New.Test: measured 50 s, no row in the table"],
               "a measured unit with no row is drift")

        # A synthetic repository for the assignment check, with the REAL shard-assign.sh.
        repo = os.path.join(t, "repo")
        for name in ("A.Test", "B.Test", "C.Test", "D.Test", "E.Test"):
            os.makedirs(os.path.join(repo, "test", name, "bin", "Release"))
            open(os.path.join(repo, "test", name, f"{name}.csproj"), "w").write("<Project/>")
        open(os.path.join(repo, "test", "A.Test", "bin", "Release", "Stale.csproj"), "w").write("")
        os.makedirs(os.path.join(repo, ".github", "scripts"))
        script = os.path.join(repo, ".github", "scripts", "shard-assign.sh")
        shutil.copy(ASSIGN, script)
        tbl = os.path.join(repo, ".github", "scripts", "shard-weights.txt")
        open(tbl, "w").write("# c\n300 B.Test 3\n80 B.Test#1/3\n200 B.Test#2/3\n20 B.Test#3/3\n50 A.Test\n")
        expect(check_assignment(repo, script, tbl, 3) == [],
               "the real shard-assign.sh assigns every project and every part exactly once (positive control)")
        units, err = run_assign(repo, script, 0, 3, {"TEST_SELECTION": "all", "SHARD_WEIGHTS_FILE": tbl})
        expect(units == ["test/B.Test/B.Test.csproj#2/3"] and "200" in err,
               f"a measured heavy part takes a shard of its own, at its measured weight ({units}, {err.strip()})")
        loads = predicted_loads(repo, tbl, 3, script)
        expect(loads == [200, 90, 90], f"the predicted loads use the per-part weights ({loads})")

        # Negative controls: a script that drops a unit and one that duplicates one must both fail.
        dropping = os.path.join(t, "drop.sh")
        open(dropping, "w").write(open(ASSIGN).read().replace('if (best == idx) print $3', 'if (best == idx && $3 !~ /C\\.Test/) print $3'))
        expect(any("dropped" in p and "C.Test" in p for p in check_assignment(repo, dropping, tbl, 3)),
               "negative control: a dropped project is named")
        dup = os.path.join(t, "dup.sh")
        open(dup, "w").write(open(ASSIGN).read().replace('if (best == idx) print $3', 'if (best == idx || $3 ~ /A\\.Test/) print $3'))
        expect(any("more than once" in p and "A.Test" in p for p in check_assignment(repo, dup, tbl, 3)),
               "negative control: a duplicated project is named")
        empty = os.path.join(t, "empty")
        os.makedirs(os.path.join(empty, "test"))
        expect(any("discovered no test project" in p for p in check_assignment(empty, script, tbl, 3)),
               "negative control: a tree with no test project is not a pass")

    print("self-test: " + ("PASS" if fails == 0 else f"{fails} FAILED"))
    return 0 if fails == 0 else 1


# ─────────────────────────────── main ───────────────────────────────────────────────

def main(argv: list[str]) -> int:
    if argv[:1] == ["--self-test"]:
        return self_test()
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="mode", required=True)
    for name in ("refresh", "check-current"):
        s = sub.add_parser(name)
        s.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "Systemorph/MeshWeaver"))
        s.add_argument("--runs", type=int, default=DEFAULT_RUNS)
        s.add_argument("--line", default="main",
                       help="the delivery line whose runs are measured (pushes to it, pull requests into it); '' for every run")
        if name == "refresh":
            s.add_argument("--out", default=TABLE)
    s = sub.add_parser("check-assignment")
    s.add_argument("--shards", type=int, default=0)
    a = ap.parse_args(argv)

    if a.mode == "check-assignment":
        total = a.shards or shard_total()
        problems = check_assignment(ROOT, ASSIGN, TABLE, total)
        for p in problems:
            print(f"::error::shard assignment: {p}")
        loads = predicted_loads(ROOT, TABLE, total)
        print(f"shard-weights: {len(discovered_projects(ROOT))} projects over {total} shards; "
              f"predicted loads (s): {' '.join(map(str, loads))}; spread {max(loads) - min(loads)} s")
        if problems:
            return 1
        print("shard-weights: every test project (every part) is assigned exactly once, under both selections")
        return 0

    measured, runs = measure(a.repo, a.runs, a.line)
    if not measured:
        print(f"::error::shard-weights: read {a.runs} runs and measured NOTHING — refusing to treat that as current")
        return 1
    if a.mode == "refresh":
        totals, parts, _ = read_table(a.out) if os.path.exists(a.out) else ({}, {}, {})
        now = dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        with open(a.out, "w", encoding="utf-8") as fh:
            fh.write(render_table(measured, parts, runs, now, totals, a.line))
        print(f"shard-weights: wrote {a.out} from {len(measured)} units over {len(runs)} runs")
        return 0
    problems = drift(TABLE, measured, scheduled_units(ROOT, TABLE))
    for p in problems:
        print(f"::error::shard weight drift: {p}")
    print(f"shard-weights: compared {len(measured)} measured units over {len(runs)} runs; {len(problems)} drifted")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
