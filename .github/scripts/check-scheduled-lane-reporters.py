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
    the reporter exists to remove. "Transitively" counts only through an intermediate that keeps the
    implicit success() — no status function, or `success()` as a required positive `&&` conjunct:
    one whose `if:` carries always()/!cancelled()/failure(), `success() || …` or `!success()` can
    succeed after its own need failed, so that need must be listed directly; and
  * carry an `if:` with a status function that survives a failure (`always()`, `failure()` or
    `!cancelled()`). Without one GitHub applies the implicit `success()` and the reporter is SKIPPED
    on exactly the runs it exists for; and
  * FIRE on every combination of its direct needs' results other than all-green — each need
    'success', 'failure' or (unless its `if:` is bare always()/!cancelled() or it has no needs and no
    `if:`) 'skipped', evaluated JOINTLY (#7458): a predicate that reads only 'failure' misses a need
    SKIPPED behind a red ancestor, and one checked a single red at a time misses two reds at once.

The SUCCESS half of the pair closes the alert, so a wrong one is a false green. It must `needs:` the
same closure, and its `if:` must run on a scheduled green and never on a red: every top-level `&&`
conjunct is `success()`, `always()`/`!cancelled()` (then `needs.<n>.result == 'success'` for EVERY
direct need), `needs.<n>.result == 'success'`, a `needs.<n>.outputs.<o>` comparison, or an event/ref
guard that is TRUE for a scheduled run on main. Anything else is refused as unsupported.

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
import itertools
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
# `find` only LOOKS UP the standing issue (Copilot on #7422): a sentinel job counts only when it also
# OPENS one and CLOSES it — the writes are what report.
SENTINEL_OPENS = re.compile(r"gh\s+issue\s+create\b|/issues\"?\s+(?:[^\n]*\s)?-f\s+title=")
SENTINEL_CLOSES = re.compile(r"state=closed|gh\s+issue\s+close\b")
SURVIVES = ("always()", "failure()", "!cancelled()")
# A status function can appear and still be defeated (Copilot on #7422): `always() && success()`
# re-adds the skip, `!failure()` is its negation. Refused outright.
DEFEATS = ("success()", "!failure()")
DEFAULT_LEDGER_LABEL = "ci-main-red"
DEFAULT_LEDGER_TITLE = "ci-main-red: main is red"   # node-repo-ci-failure.yml's `issue-title` default


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


def if_text(job: dict) -> str:
    """The job's `if:` as text. YAML reads a bare `if: false` as the boolean False, which `or ""` would
    turn into an ABSENT condition (Copilot on #7422) — keep the value, spelled the way GitHub reads it."""
    v = job.get("if")
    if v is None:
        return ""
    if isinstance(v, bool):
        return "true" if v else "false"
    return str(v)


# A status function in an `if:` replaces the implicit success(), so the job can run — and SUCCEED —
# after one of its own needs failed.
_STATUS_OVERRIDE = re.compile(r"\b(?:always|failure|cancelled)\(\)")


def propagates_failure(job: dict) -> bool:
    """True when a red ancestor is guaranteed to SKIP the job, so its result is never 'success' on a
    red anywhere upstream and a check on it covers its own needs. That holds for exactly two shapes:
    no status function at all (the implicit success()), or an explicit `success()` that is a required
    POSITIVE top-level `&&` conjunct. Anything else replaces the implicit success() and can run — and
    succeed — after its own need failed: always()/failure()/cancelled(), `success() || <guard>`
    (Copilot on #7422), and `!success()` (#7458 — no `||`, yet it runs precisely BECAUSE a need
    failed). An expression this guard cannot read counts as an override: the reporter must then need
    that job's own needs directly, which is the conservative reading."""
    cond = if_text(job).strip()
    if cond.startswith("${{") and cond.endswith("}}"):
        cond = cond[3:-2]
    if not re.search(r"\b(?:always|failure|cancelled|success)\(\)", cond):
        return True
    if _STATUS_OVERRIDE.search(cond):
        return False
    tokens = _tokens(cond)
    if tokens is None:
        return False
    return any("".join(conj) == "success()" for conj in _conjuncts(tokens))


def closure(jobs: dict, start: str) -> set[str]:
    """The jobs whose red reaches `start` through its `needs.<n>.result` checks: every direct need, and
    transitively the needs of an intermediate job only while that intermediate PROPAGATES a failure.
    An intermediate with `always()`/`!cancelled()`/`failure()` can succeed after an ancestor failed, so
    `a → b(if: always()) → reporter` does NOT cover `a` (Copilot on #7422) — the reporter must need `a`
    directly."""
    seen: set[str] = set()
    stack = list(needs_of(jobs.get(start) or {}))
    while stack:
        j = stack.pop()
        if j in seen:
            continue
        seen.add(j)
        job = jobs.get(j) or {}
        if propagates_failure(job):
            stack.extend(needs_of(job))
    return seen


def survives_failure(job: dict) -> bool:
    """The `if:` LEADS with a failure-surviving status function and never re-adds or negates the skip.
    Deliberately a restriction, not an evaluator: the supported forms are `always() && …`,
    `failure() …` and `!cancelled() && …`, which is every reporter in the tree."""
    cond = re.sub(r"\s+", "", if_text(job))
    if cond.startswith("${{") and cond.endswith("}}"):
        cond = cond[3:-2]
    cond = cond.lstrip("(")
    return cond.startswith(SURVIVES) and not any(d in cond for d in DEFEATS)


_TOKEN = re.compile(r"\s*(?:(?P<str>'(?:[^']|'')*')|(?P<op>==|!=|&&|\|\||!|\(|\))|(?P<id>[A-Za-z_][\w.\-]*))")
# A guard conjunct may read only these: what a scheduled run's context fixes (Copilot on #7422).
_GUARD_IDENT = re.compile(r"^(?:github\.event_name|github\.ref|github\.event\.inputs\.[\w\-]+|true|false|null)$")


def _tokens(cond: str) -> list[str] | None:
    out: list[str] = []
    i = 0
    while i < len(cond):
        if cond[i:].strip() == "":
            break
        m = _TOKEN.match(cond, i)
        if not m:
            return None
        out.append(m.group(m.lastgroup))
        i = m.end()
    return out


def _conjuncts(tokens: list[str]) -> list[list[str]]:
    """Split on top-level `&&`, after dropping parentheses that enclose the whole expression. `&&` binds
    tighter than `||`, so an expression with a top-level `||` is a DISJUNCTION and has no required
    conjuncts: it is returned whole, as ONE part (Copilot on #7512 — `success() && true || <guard>` was
    split into a required `success()`, although the guard's branch runs it after a red)."""
    def wrapped(t: list[str]) -> bool:
        if len(t) < 2 or t[0] != "(" or t[-1] != ")":
            return False
        depth = 0
        for k, tok in enumerate(t):
            depth += tok == "("
            depth -= tok == ")"
            if depth == 0 and k < len(t) - 1:
                return False
        return True
    while wrapped(tokens):
        tokens = tokens[1:-1]
    depth = 0
    for tok in tokens:
        depth += tok == "("
        depth -= tok == ")"
        if tok == "||" and depth == 0:
            return [list(tokens)]
    parts: list[list[str]] = [[]]
    depth = 0
    for tok in tokens:
        depth += tok == "("
        depth -= tok == ")"
        if tok == "&&" and depth == 0:
            parts.append([])
        else:
            parts[-1].append(tok)
    return [p for p in parts]


def _eval_guard(tokens: list[str], ctx: dict, ident: re.Pattern = _GUARD_IDENT):
    """Evaluate a guard conjunct (==, !=, !, &&, ||, parentheses over _GUARD_IDENT and string literals)
    against a scheduled run's context. Raises ValueError on anything outside that subset."""
    pos = 0

    def peek():
        return tokens[pos] if pos < len(tokens) else None

    def take():
        nonlocal pos
        pos += 1
        return tokens[pos - 1]

    def atom():
        tok = take() if peek() is not None else None
        if tok == "(":
            v = orx()
            if take() != ")":
                raise ValueError("unbalanced")
            return v
        if tok == "!":
            return not atom()
        if tok and tok.startswith("'"):
            return tok[1:-1].replace("''", "'")
        if tok and ident.match(tok):
            return {"true": True, "false": False, "null": None}.get(tok, ctx.get(tok))
        raise ValueError(f"unsupported token {tok!r}")

    def cmp():
        left = atom()
        while peek() in ("==", "!="):
            op = take()
            right = atom()
            norm = lambda v: v.lower() if isinstance(v, str) else v
            left = (norm(left) == norm(right)) if op == "==" else (norm(left) != norm(right))
        return left

    def andx():
        v = cmp()
        while peek() == "&&":
            take()
            r = cmp()
            v = bool(v) and bool(r)
        return v

    def orx():
        v = andx()
        while peek() == "||":
            take()
            r = andx()
            v = bool(v) or bool(r)
        return v

    v = orx()
    if pos != len(tokens):
        raise ValueError("trailing tokens")
    return bool(v)


SCHEDULED = {"github.event_name": "schedule", "github.ref": "refs/heads/main"}


def success_condition_problem(job: dict) -> str | None:
    """The close-the-ledger half must run on a scheduled GREEN and never on a RED (Copilot on #7422).
    Deliberately a restriction, not a general evaluator. Every top-level `&&` conjunct is one of:
    `success()`; `always()` / `!cancelled()` (then EVERY direct need carries `needs.<n>.result == 'success'`);
    `needs.<n>.result == 'success'` for a direct need; `needs.<n>.outputs.<o> == '…'` for a direct need;
    or an event/ref guard that evaluates TRUE for a scheduled run on main."""
    raw = if_text(job).strip()
    if raw.startswith("${{") and raw.endswith("}}"):
        raw = raw[3:-2]
    if not raw.strip():
        return None  # implicit success(): runs only when every need succeeded
    tokens = _tokens(raw)
    if tokens is None:
        return f"`if: {raw.strip()}` is not an expression this guard can read"
    direct = set(needs_of(job))
    overrides = False
    has_success = False
    confirmed: set[str] = set()
    for conj in _conjuncts(tokens):
        text = "".join(conj)
        if text == "success()":
            has_success = True
            continue
        if text in ("always()", "!cancelled()"):
            overrides = True
            continue
        m = re.fullmatch(r"needs\.([\w\-]+)\.result=='success'", text)
        if m and m.group(1) in direct:
            confirmed.add(m.group(1))
            continue
        m = re.fullmatch(r"needs\.([\w\-]+)\.outputs\.[\w\-]+(?:==|!=)'[^']*'", text)
        if m and m.group(1) in direct:
            continue  # a narrowing conjunct: it can only make the job run LESS, never on a red
        try:
            ok = _eval_guard(conj, SCHEDULED)
        except ValueError:
            return (f"conjunct `{' '.join(conj)}` of its `if:` is not a supported form (success(), always()/!cancelled() "
                    "with needs.<n>.result == 'success' per direct need, or an event/ref guard) — a status function "
                    "like failure() or an `||` over needs lets it close the ledger on a red")
        if not ok:
            return (f"conjunct `{' '.join(conj)}` of its `if:` is false for a scheduled run on main — it never runs on "
                    "the green that should clear the alert")
    if overrides and not has_success:
        missing = sorted(direct - confirmed)
        if missing:
            return (f"its `if:` overrides the implicit success() but does not require needs.<n>.result == 'success' for "
                    f"{', '.join(missing)} — it runs, and closes the ledger, on a red there")
    return None


# What a failure reporter's predicate may read: the guard identifiers plus its needs' results.
_REPORTER_IDENT = re.compile(_GUARD_IDENT.pattern[:-2] + r"|needs\.[\w\-]+\.result)$")


# A reporter's direct needs are enumerated JOINTLY over every result a red run can give them; past this
# many the 3^n combinations are not enumerated and the predicate is refused as unprovable.
MAX_ENUMERATED_NEEDS = 10


def possible_results(job: dict) -> tuple[str, ...]:
    """The results a job can end with on a scheduled run that is red somewhere. A job with no needs and
    no `if:` always runs, so it ends 'success' or 'failure'; any other job can also end 'skipped' — a
    red ancestor skips a job that keeps the implicit success() (#7458: `a → b → reporter` with `a` red
    leaves `b` SKIPPED, never 'failure'). An `if:` that is exactly `always()` or `!cancelled()` runs on
    every run that is not cancelled, so a red cannot skip it (combo-verify.yml's `verdict`)."""
    cond = re.sub(r"\s+", "", if_text(job))
    if cond.startswith("${{") and cond.endswith("}}"):
        cond = cond[3:-2]
    if cond in ("always()", "!cancelled()") or not (needs_of(job) or cond):
        return ("success", "failure")
    return ("success", "failure", "skipped")


def fire_condition_problem(job: dict, jobs: dict | None = None) -> str | None:
    """A failure reporter must FIRE on a scheduled run whenever ANY of its direct needs is not green
    (Copilot on #7422: `needs: [a, b]` with `if: always() && needs.a.result != 'success'` stays silent on
    b's red). Evaluated JOINTLY over every combination of the direct needs' results — each 'success',
    'failure' or (when it can be) 'skipped' — except the all-green one, on a scheduled run on main, with
    the status functions as they read on a red (always()/failure() true, success()/cancelled() false).
    One need red at a time is not enough (#7458): `always() && needs.b.result == 'failure'` is silent
    when b is SKIPPED behind a red ancestor, and `needs.a.result == 'success' || needs.b.result ==
    'success'` is silent only when BOTH are red. Anything outside the readable subset is a problem,
    never a pass."""
    jobs = jobs or {}
    raw = if_text(job).strip()
    if raw.startswith("${{") and raw.endswith("}}"):
        raw = raw[3:-2]
    if not raw.strip():
        return None  # survives_failure reports the implicit success() on its own line
    text = re.sub(r"\balways\(\)|\bfailure\(\)", "true", raw)
    text = re.sub(r"\bcancelled\(\)|\bsuccess\(\)", "false", text)
    tokens = _tokens(text)
    if tokens is None:
        return f"`if: {raw.strip()}` is not an expression this guard can read"
    direct = needs_of(job)
    if len(direct) > MAX_ENUMERATED_NEEDS:
        return (f"it has {len(direct)} direct needs — more than the {MAX_ENUMERATED_NEEDS} this guard enumerates "
                "jointly, so it cannot prove its `if:` fires on every red; route them through a verdict job")
    for combo in itertools.product(*(possible_results(jobs.get(n) or {}) for n in direct)):
        if all(r == "success" for r in combo):
            continue
        ctx = dict(SCHEDULED)
        ctx.update({f"needs.{n}.result": r for n, r in zip(direct, combo)})
        try:
            fires = _eval_guard(tokens, ctx, _REPORTER_IDENT)
        except ValueError:
            return (f"`if: {raw.strip()}` reads something beyond status functions, event/ref guards and "
                    "needs.<n>.result — this guard cannot prove it fires on every red")
        if not fires:
            state = ", ".join(f"needs.{n}.result == '{r}'" for n, r in zip(direct, combo))
            return f"its `if:` stays false on a scheduled run where {state} — that red is never reported"
    return None


def reporter_problems(jobs: dict) -> tuple[bool, list[str]]:
    """(has a reporter shape at all, the problems with it). Pure."""
    ledger: dict[str, list[tuple[str, tuple[str, str]]]] = {}     # outcome -> [(job id, (label, title))]
    sentinels: list[str] = []
    for jid, job in jobs.items():
        if not isinstance(job, dict):
            continue
        uses = str(job.get("uses") or "")
        if LEDGER_LANE.search(uses):
            w = job.get("with") or {}
            outcome = str(w.get("outcome", "")).strip()
            # The ledger finds its issue by label AND exact title, so the pair must agree on BOTH,
            # with the lane's defaults filled in (Copilot on #7422).
            key = (str(w.get("label") or DEFAULT_LEDGER_LABEL).strip(),
                   str(w.get("issue-title") or DEFAULT_LEDGER_TITLE).strip())
            ledger.setdefault(outcome, []).append((jid, key))
        runs = "\n".join(str(st.get("run") or "") for st in job.get("steps") or [] if isinstance(st, dict))
        if SENTINEL_RUN.search(runs):
            sentinels.append(jid)
    reporters = {j for pairs in ledger.values() for j, _ in pairs} | set(sentinels)
    if not reporters:
        return False, []
    problems: list[str] = []
    failing: list[str] = []
    if ledger:
        fail_keys = {k for _, k in ledger.get("failure", [])}
        ok_keys = {k for _, k in ledger.get("success", [])}
        if not fail_keys:
            problems.append("calls node-repo-ci-failure.yml but never with `outcome: failure` — nothing files the red")
        for label, title in sorted(fail_keys - ok_keys):
            problems.append(f"files reds under label `{label}` / title {title!r} but no `outcome: success` job with that "
                            "SAME label and title closes that ledger — the ledger matches on both, so the alert never clears")
        failing.extend(j for j, _ in ledger.get("failure", []))
        # The success half closes the alert, so a wrong one is a false green (Copilot on #7422): it must
        # see every job too, and its `if:` must run on a scheduled green and never on a red.
        for jid, _ in ledger.get("success", []):
            closing = {j for j in jobs if j not in reporters}
            missing = sorted(closing - closure(jobs, jid))
            if missing:
                problems.append(f"success job '{jid}' does not `needs:` {', '.join(missing)} — it closes the ledger on a "
                                "run where that job is red")
            why = success_condition_problem(jobs[jid])
            if why:
                problems.append(f"success job '{jid}': {why}")
    for jid in sentinels:
        runs = "\n".join(str(st.get("run") or "") for st in jobs[jid].get("steps") or [] if isinstance(st, dict))
        if not SENTINEL_OPENS.search(runs) or not SENTINEL_CLOSES.search(runs):
            problems.append(f"sentinel job '{jid}' looks the issue up (`sentinel-issue.py find`) but does not both "
                            "OPEN one (`gh issue create` / POST …/issues -f title=) and CLOSE it (`state=closed` / "
                            "`gh issue close`) — a lookup reports nothing")
    failing.extend(sentinels)
    others = {j for j in jobs if j not in reporters}
    for jid in failing:
        missing = sorted(others - closure(jobs, jid))
        if missing:
            problems.append(f"reporter job '{jid}' does not `needs:` {', '.join(missing)} — a red there is never reported")
        if not survives_failure(jobs[jid]):
            problems.append(f"reporter job '{jid}' has no `if:` with always()/failure()/!cancelled() — the implicit "
                            "success() SKIPS it on every red it exists to report")
        else:
            why = fire_condition_problem(jobs[jid], jobs)
            if why:
                problems.append(f"reporter job '{jid}': {why}")
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
  report:
    needs: [a]
    if: ${{ !cancelled() }}
    runs-on: x
    timeout-minutes: 5
    steps:
      - run: |
          N=$(python3 .github/scripts/sentinel-issue.py find --repo r --label l)
          gh api -X PATCH "repos/$REPO/issues/$N" -f state=closed --silent
          NEW=$(gh api "repos/$REPO/issues" -f title="$TITLE" -f body="$BODY" --jq .number)
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
        ("sentinel with no failure-surviving if", sentinel.replace("    if: ${{ !cancelled() }}\n", ""), "", True),
        # Copilot on #7422 — four ways the first cut was fooled, each a negative control now:
        ("`always() && success()` re-adds the skip", pair.replace("if: always() && needs.b.result != 'success'", "if: always() && success()"), "", True),
        ("`!failure()` is the negation", pair.replace("if: always() && needs.b.result != 'success'", "if: ${{ !failure() }}"), "", True),
        ("sentinel that only looks up (no open, no close)", sentinel.replace(
            '          gh api -X PATCH "repos/$REPO/issues/$N" -f state=closed --silent\n', "").replace(
            '          NEW=$(gh api "repos/$REPO/issues" -f title="$TITLE" -f body="$BODY" --jq .number)\n', ""), "", True),
        ("pair with matching label but mismatched title", pair.replace(
            "{outcome: failure, label: lane-x}", "{outcome: failure, label: lane-x, issue-title: 'lane-x: red'}"), "", True),
        ("pair with matching label AND title", pair.replace(
            "{outcome: failure, label: lane-x}", "{outcome: failure, label: lane-x, issue-title: 'lane-x: red'}").replace(
            "{outcome: success, label: lane-x}", "{outcome: success, label: lane-x, issue-title: 'lane-x: red'}"), "", False),
        # Copilot on #7422 (second round) — the success half is a false green when wrong:
        ("success job misses a job in needs (closes on b's red)", pair.replace(
            "  green:\n    needs: [b]\n    if: always() && needs.b.result == 'success'",
            "  green:\n    needs: [a]\n    if: always() && needs.a.result == 'success'"), "", True),
        ("success job `if: ${{ false }}` never clears", pair.replace(
            "if: always() && needs.b.result == 'success'", "if: ${{ false }}"), "", True),
        ("success job `always()` with no needs-result check", pair.replace(
            "if: always() && needs.b.result == 'success'", "if: always()"), "", True),
        ("success job on failure()", pair.replace(
            "if: always() && needs.b.result == 'success'", "if: failure()"), "", True),
        ("success job with an `||` over needs", pair.replace(
            "if: always() && needs.b.result == 'success'", "if: always() && (needs.b.result == 'success' || needs.a.result == 'success')"), "", True),
        ("success job guarded to push only (never on schedule)", pair.replace(
            "if: always() && needs.b.result == 'success'", "if: github.event_name == 'push' && success()"), "", True),
        ("success job: YAML boolean `if: false` never clears", pair.replace(
            "if: always() && needs.b.result == 'success'", "if: false"), "", True),
        # Copilot on #7422 (third round) — an intermediate that can SUCCEED after its own need failed
        # does not carry that red to a reporter checking only the intermediate:
        ("intermediate `if: always()` cleanup hides a's red from both halves", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: always(), runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}"), "", True),
        ("intermediate `if: !cancelled()` hides a's red from the success half", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: '${{ !cancelled() }}', runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}").replace(
            "  red:\n    needs: [b]\n    if: always() && needs.b.result != 'success'",
            "  red:\n    needs: [a, b]\n    if: always() && (needs.a.result != 'success' || needs.b.result != 'success')"), "", True),
        ("intermediate `if: always()` with both halves needing a directly", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: always(), runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}").replace(
            "  red:\n    needs: [b]\n    if: always() && needs.b.result != 'success'",
            "  red:\n    needs: [a, b]\n    if: always() && (needs.a.result != 'success' || needs.b.result != 'success')").replace(
            "  green:\n    needs: [b]\n    if: always() && needs.b.result == 'success'",
            "  green:\n    needs: [a, b]\n    if: always() && needs.a.result == 'success' && needs.b.result == 'success'"), "", False),
        ("intermediate with a non-status guard still propagates", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: github.ref == 'refs/heads/main', runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}"), "", False),
        ("success job with implicit success() (no if)", pair.replace(
            "    if: always() && needs.b.result == 'success'\n", ""), "", False),
        ("success job: event guard + !cancelled() + result (the dotnet-test shape)", pair.replace(
            "if: always() && needs.b.result == 'success'",
            "if: github.event_name != 'pull_request' && github.ref == 'refs/heads/main' && !cancelled() && needs.b.result == 'success'"), "", False),
        ("success job: parenthesised `||` event guard (the lock-pinned-digests shape)", pair.replace(
            "if: always() && needs.b.result == 'success'",
            "if: always() && (github.event_name == 'schedule' || (github.event_name == 'workflow_dispatch' && github.event.inputs.mode != 'report-only')) && needs.b.result == 'success'"), "", False),
        # Copilot on #7422 (fourth round) — an explicit success() is a status override too, and a red
        # predicate must cover EVERY direct need:
        ("intermediate `success() || schedule` hides a's red", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: \"success() || github.event_name == 'schedule'\", runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}"), "", True),
        ("intermediate `success() && guard` still propagates", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: \"success() && github.ref == 'refs/heads/main'\", runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}"), "", False),
        ("red predicate ignores a direct need", pair.replace(
            "  red:\n    needs: [b]\n    if: always() && needs.b.result != 'success'",
            "  red:\n    needs: [a, b]\n    if: always() && needs.a.result != 'success'"), "", True),
        ("red predicate covers every direct need", pair.replace(
            "  red:\n    needs: [b]\n    if: always() && needs.b.result != 'success'",
            "  red:\n    needs: [a, b]\n    if: always() && (needs.a.result != 'success' || needs.b.result != 'success')"), "", False),
        # #7458 — three shapes the per-need, `||`-only reading passed while a red went unreported:
        ("#7458.1 intermediate `if: !success()` hides a's red (no `||`)", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: '${{ !success() }}', runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}"), "", True),
        ("#7458.1 fix: both halves need a directly past `!success()`", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: '${{ !success() }}', runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}").replace(
            "  red:\n    needs: [b]\n    if: always() && needs.b.result != 'success'",
            "  red:\n    needs: [a, b]\n    if: always() && (needs.a.result != 'success' || needs.b.result != 'success')").replace(
            "  green:\n    needs: [b]\n    if: always() && needs.b.result == 'success'",
            "  green:\n    needs: [a, b]\n    if: always() && needs.a.result == 'success' && needs.b.result == 'success'"), "", False),
        ("#7458.2 red `== 'failure'` silent when b is SKIPPED behind a", pair.replace(
            "if: always() && needs.b.result != 'success'", "if: always() && needs.b.result == 'failure'"), "", True),
        ("#7458.2 fix: `!= 'success'` also reports the skip", pair, "", False),
        ("#7458.3 red `a ok || b ok` silent when BOTH fail", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}").replace(
            "  red:\n    needs: [b]\n    if: always() && needs.b.result != 'success'",
            "  red:\n    needs: [a, b]\n    if: always() && (needs.a.result == 'success' || needs.b.result == 'success')").replace(
            "  green:\n    needs: [b]\n    if: always() && needs.b.result == 'success'",
            "  green:\n    needs: [a, b]\n    if: always() && needs.a.result == 'success' && needs.b.result == 'success'"), "", True),
        ("#7458.3 fix: `a not ok || b not ok` reports both-red", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}").replace(
            "  red:\n    needs: [b]\n    if: always() && needs.b.result != 'success'",
            "  red:\n    needs: [a, b]\n    if: always() && (needs.a.result != 'success' || needs.b.result != 'success')").replace(
            "  green:\n    needs: [b]\n    if: always() && needs.b.result == 'success'",
            "  green:\n    needs: [a, b]\n    if: always() && needs.a.result == 'success' && needs.b.result == 'success'"), "", False),
        ("#7458.2 control: `== 'failure'` on an `if: always()` verdict that a red cannot skip", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: always(), runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}").replace(
            "  red:\n    needs: [b]\n    if: always() && needs.b.result != 'success'",
            "  red:\n    needs: [a, b]\n    if: always() && (needs.a.result != 'success' || needs.b.result == 'failure')").replace(
            "  green:\n    needs: [b]\n    if: always() && needs.b.result == 'success'",
            "  green:\n    needs: [a, b]\n    if: always() && needs.a.result == 'success' && needs.b.result == 'success'"), "", False),
        # Copilot on #7512 — `&&` binds tighter than `||`: no top-level `&&` conjunct of a disjunction is required.
        ("#7512 intermediate `success() && true || <schedule>` runs b after a's red", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: \"${{ success() && true || github.event_name == 'schedule' }}\", runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}"), "", True),
        ("#7512 control: intermediate `success() && (true || <schedule>)` keeps success() required", pair.replace(
            "  b: {needs: [a], runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}",
            "  b: {needs: [a], if: \"${{ success() && (true || github.event_name == 'schedule') }}\", runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}"), "", False),
        ("#7512 success half `success() && true || <schedule>` closes the ledger on a red", pair.replace(
            "  green:\n    needs: [b]\n    if: always() && needs.b.result == 'success'",
            "  green:\n    needs: [b]\n    if: \"${{ success() && true || github.event_name == 'schedule' }}\""), "", True),
        ("`on:` as a list with schedule, no reporter","on: [schedule, push]\njobs:\n  a: {runs-on: x, timeout-minutes: 5, steps: [{run: echo}]}\n", "", True),
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
