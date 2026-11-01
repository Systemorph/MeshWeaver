#!/usr/bin/env python3
"""module-publication.py — the module registry HAND-OVER, downstream of the FULL source verdict.

(The name on this first line is load-bearing: the publication lane fetches this file at the
platform pin and refuses a body whose first 400 bytes do not name it.)

WHY THIS EXISTS (MeshWeaver#3878, requirement 3 of #3842: "a red or cancelled source build must
not become visible to portals")
------------------------------------------------------------------------------------------------
`node-repo-module-pack.yml` used to POST each module bundle to the live registry INSIDE its own
matrix leg, right after that module's own suite. Everything else that validates the source — a
sibling module's suite, the portal-host shards, the NodeType compile-check, the Tests-area gate —
runs BESIDE or AFTER that leg, so a module was already being served to every installation while
the run that produced it was still deciding whether the source was any good. Making the pack job
`needs:` those gates is not available: they consume the `modules-floor` artifacts, so the edge
would be a cycle.

So the hand-over MOVES. `pack` keeps building, uploading and testing — everything a downstream
gate consumes stays exactly where it was, at the same point in the run — and STAGES the bytes it
would have published. This script is what runs afterwards, in a job the caller wires `needs:` on
its whole validation set, and it is deliberately built so that "the validation did not run" and
"the validation passed" can never look the same:

  verdict       Every job in the caller's `needs:` context must be present with result `success`.
                FAILED, SKIPPED, CANCELLED, MISSING and UNKNOWN are all refusals, each naming the
                job and what it actually said. An empty needs context is a refusal too — a
                publication that waited for nothing is the defect, not the base case.
  check-caller  The caller's own workflow file, read at the caller's commit: the publishing job
                must `needs:` (transitively) every job it declares required, must carry no status
                function in its `if:` (`always()`/`!cancelled()` re-open the door the verdict
                closes) but EXACTLY the prefix `!cancelled() && !failure() && ` before the event
                condition (policy publisher-tolerates-upstream-skips — the default rule skips the
                job over ANY skipped ancestor, actions/runner#2205), must hand this lane the WHOLE
                needs context rather than a curated
                literal, and must account for every other job in the workflow — covered, or
                declared unrelated with a reason. This is the half `verdict` cannot see: a
                dependency dropped from `needs:` AND from `required-jobs` is invisible at runtime.
  check-callers The same caller-graph check, run BEFORE MERGE over every workflow in a repository
                (node-repo-validate.yml runs it on every satellite pull request), plus the wiring
                one call cannot see: every `publish-mode: staged` pack call has exactly one
                publisher reading its `lane`/`selected`/`declared` outputs, and that publisher runs
                on exactly the events the pack call stages on (the accepted status prefix removed
                first, on the publisher and its build-host-publication call alike). A staged pack
                call nobody publishes
                would hand the registry NOTHING, silently — the FrameworkDeclined outage of #2088.
                H7: a pack call that may run on a build host (`executor:` anything but `actions`)
                is published THROUGH its one build-host-publication.yml call — the host graph,
                Doc/Architecture/BuildHosts §5 — and every other shape of that graph is refused.
  lane-key      The lane key a pack call stamps on its evidence, for a host's modules stage to stamp
                its publication records with.
  superseded    NEWEST WINS, PER MODULE, under staged publishing (`publish-newest-only` on the
                publication lane). Right before the hand-over, for every staged record that owes one:
                does a NEWER trunk commit reach the module's dependency network? It asks the same
                scope script the pack lane's in-leg check asks (node-repo-scope.py --for modules
                --publication-reach over the trunk tip's diff), and writes the modules to stand down.
                A staged hand-over runs at the END of a run, so two overlapping trunk runs (a main
                push and the release lane) would otherwise publish in finishing order, and the
                registry keeps whatever arrives LAST. "Cannot tell" is RED, never "nothing newer".
  publish       The staged evidence is matched against the selection before ONE byte is POSTed:
                exactly one publication record per selected module, stamped with THIS call's lane,
                naming the bytes by sha256 and the framework identity read back off those bytes.
                A missing, foreign-lane or substituted record refuses the WHOLE publication — the
                registry is left untouched rather than half-served. Only then does the hand-over
                run, and only a 2xx from the registry records `Published` in the build ledger.

USAGE
  module-publication.py verdict --verdicts @needs.json --required "validate compile-check"
  module-publication.py check-caller --workflow .github/workflows/ci.yml \\
        --uses node-repo-module-publish.yml --required "validate compile-check" \\
        --unrelated "auto-arm: arms auto-merge, validates nothing"
  module-publication.py check-callers --root .
  module-publication.py publish --staged DIR --lane L --declared @modules.json \\
        --selected "MeshWeaver.AI" --registry https://memex.meshweaver.cloud --source Plugins
  module-publication.py publish --check-only --staged DIR --lane L --declared @modules.json \\
        --selected "MeshWeaver.AI"        # build-host-publication.yml: the publisher's checks, no POST
  module-publication.py lane-key --lane-id catalog --modules @modules.json
  module-publication.py superseded --staged DIR --lane L --declared @modules.json --root CALLER \\
        --mine SHA --tip SHA --changed-list a,b --scope-script node-repo-scope.py --out superseded.json
  module-publication.py publish … --superseded @superseded.json   # stands those modules down
  module-publication.py --self-test
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

GOOD = "success"
#: The four expression functions that re-open a job GitHub would otherwise have skipped. A
#: publishing job must run under the DEFAULT rule (every `needs:` succeeded) and nothing else —
#: `if: ${{ !cancelled() }}` on a publisher is the same defect as `continue-on-error` on a gate's
#: input step: it makes "a dependency failed" indistinguishable from "everything was fine".
STATUS_FUNCS = ("always", "cancelled", "failure", "success")
#: THE ONE EXCEPTION, and only in exactly this spelling: a publishing job (and its
#: build-host-publication call) may open its `if:` with `!cancelled() && !failure() && ` followed by
#: the event condition. GitHub's DEFAULT rule is transitive — it skips a job when ANY ancestor was
#: skipped, not only a direct need (actions/runner#2205) — so a caller whose graph carries by-design
#: skips upstream (a scoped leg, a wait that had nothing to wait for) would skip its publisher on
#: every normal run and publish NOTHING behind green checks (MeshWeaver.Plugins#4478). This prefix
#: runs the job over upstream SKIPS and nothing else: `!failure()` still holds it back when any
#: ancestor failed, `!cancelled()` when the run was cancelled, and the publish lane's verdict still
#: refuses, BY NAME, every DIRECT need that did not report `success` — a skipped or cancelled direct
#: dependency reds the hand-over exactly as before. `always()`, a bare `!cancelled()`, `success() ||
#: failure()` and every other status-function shape stay refused.
#: The prefix is matched in its CANONICAL lowercase spelling only. GitHub reads `!CANCELLED()` as
#: `!cancelled()`, but a non-canonical spelling is not accepted as the prefix: it falls through to the
#: case-insensitive status-function scan and is REFUSED, so no casing variant can pass as looser.
SKIP_TOLERANT_PREFIX = "!cancelled() && !failure() && "
_SKIP_TOLERANT_RE = re.compile(
    r"!\s*cancelled\s*\(\s*\)\s*&&\s*!\s*failure\s*\(\s*\)\s*&&\s*(?P<rest>.+)", re.S)
#: The caller must hand over its WHOLE needs context, verbatim. A curated object literal would let
#: the caller answer the verdict question with a value it chose rather than one GitHub computed.
VERDICTS_EXPR = "${{ toJSON(needs) }}"
HTTP_TIMEOUT_S = 300
PUBLISH_TOKEN_ENV = "MW_PUBLISH_TOKEN"


def die(msg: str, code: int = 1):
    print(f"::error::{msg}", file=sys.stderr, flush=True)
    sys.exit(code)


def names(raw: str | None) -> list[str]:
    return [n for n in re.split(r"[,\s]+", raw or "") if n]


def load_json_arg(raw: str):
    if raw.startswith("@"):
        return json.loads(Path(raw[1:]).read_text(encoding="utf-8"))
    return json.loads(raw)


def summarise(lines: list[str]) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as fh:
            fh.write("\n".join(lines) + "\n")


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# ══════════════════════════════════ verdict ══════════════════════════════════
def verdict(verdicts_raw: str, required_raw: str) -> int:
    """Every dependency succeeded — asserted, out loud, naming whatever did not."""
    try:
        ctx = load_json_arg(verdicts_raw)
    except Exception as exc:  # noqa: BLE001 — the message has to name the input, not the traceback
        die(f"the verdicts input is not JSON ({exc}). It must be `{VERDICTS_EXPR}` — a publication "
            "that cannot read its dependencies' results must never proceed as if they had passed.")
    if not isinstance(ctx, dict):
        die(f"the verdicts input parsed as {type(ctx).__name__}, not an object. Pass "
            f"`{VERDICTS_EXPR}` unchanged.")
    required = names(required_raw)
    if not required:
        die("required-jobs is EMPTY. A publication lane that names no required validation job "
            "asserts nothing — 'every required job passed' would be vacuously true. Name the jobs "
            "whose verdict entitles this run to publish.")
    if not ctx:
        die("the needs context is EMPTY — this publishing job depends on nothing, so it cannot be "
            "waiting for any validation and would hand bundles to the registry the moment the run "
            "starts. Give the job a `needs:` covering the validation set (MeshWeaver#3878).")

    problems: list[str] = []
    rows: list[tuple[str, str, str]] = []
    for job in sorted(set(list(ctx) + required)):
        entry = ctx.get(job)
        mark = "required" if job in required else "extra"
        if entry is None:
            rows.append((job, "MISSING", mark))
            problems.append(
                f"required validation job `{job}` is NOT among this job's `needs:` — it never "
                "reported to this publication, and an absent verdict is not a positive one. Add it "
                "to `needs:` (or stop declaring it required and say why).")
            continue
        result = entry.get("result") if isinstance(entry, dict) else None
        if not isinstance(result, str) or not result:
            rows.append((job, "UNKNOWN", mark))
            problems.append(
                f"job `{job}` reported no readable result ({json.dumps(entry)[:120]}). A verdict "
                "this lane cannot read must never resolve to 'it passed'.")
            continue
        rows.append((job, result, mark))
        if result != GOOD:
            problems.append(
                f"job `{job}` reported `{result}`, not `success` — the source validation did not "
                "reach a positive verdict, so nothing may be handed to the registry. "
                + ("A SKIPPED dependency is the trap this check exists for: GitHub paints it with "
                   "the same tick as a passed one." if result == "skipped" else
                   "Read that job; this publication is refusing, not failing."))

    width = max((len(j) for j, _, _ in rows), default=8)
    print(f"{len(rows)} dependency verdict(s), {len(required)} of them declared required:")
    for job, result, mark in rows:
        tick = "ok " if result == GOOD else "RED"
        print(f"  {tick} {job.ljust(width)}  {result}  ({mark})")

    lines = ["### Source validation verdict", "",
             "| job | result | |", "|---|---|---|"]
    lines += [f"| `{j}` | {r} | {'required' if m == 'required' else '—'} |" for j, r, m in rows]
    if problems:
        lines += ["", "**REFUSED — nothing was handed to the registry:**", ""]
        lines += [f"- {p}" for p in problems]
    else:
        lines += ["", f"All {len(rows)} dependencies reported `success`. The hand-over may proceed."]
    summarise(lines)

    for p in problems:
        print(f"::error title=Publication refused::{p}", file=sys.stderr)
    return 1 if problems else 0


# ══════════════════════════════════ check-caller ══════════════════════════════════
def _job_graph(jobs: dict) -> dict[str, set[str]]:
    graph: dict[str, set[str]] = {}
    for jid, job in jobs.items():
        need = (job or {}).get("needs") or []
        if isinstance(need, str):
            need = [need]
        graph[jid] = {n for n in need if isinstance(n, str)}
    return graph


def _ancestors(graph: dict[str, set[str]], start: str) -> set[str]:
    seen: set[str] = set()
    stack = list(graph.get(start, ()))
    while stack:
        n = stack.pop()
        if n in seen:
            continue
        seen.add(n)
        stack.extend(graph.get(n, ()))
    return seen


def _descendants(graph: dict[str, set[str]], start: str) -> set[str]:
    seen: set[str] = set()
    changed = True
    while changed:
        changed = False
        for jid, need in graph.items():
            if jid in seen or jid == start:
                continue
            if need & (seen | {start}):
                seen.add(jid)
                changed = True
    return seen


def _status_function_in(expr) -> str | None:
    """The first status function `expr` calls, matched CASE-INSENSITIVELY, else None.

    GitHub's expression function names are case-insensitive (`SUCCESS()` is `success()`), so a
    case-sensitive scan would let an upper- or mixed-case status function through every check that
    reads this answer — at both call sites in `_gate_condition`, an extra `SUCCESS()` after the
    accepted prefix would restore the ancestor-skip that silently suppresses publication.
    """
    if not isinstance(expr, str):
        return None
    for fn in STATUS_FUNCS:
        if re.search(rf"\b{fn}\s*\(", expr, re.IGNORECASE):
            return fn
    return None


def _outer_parens(text: str) -> str | None:
    """The inside of `text` when ONE matching pair of parentheses encloses all of it, else None.

    `(a) || (b)` is not enclosed — its first `(` closes before the end. Single-quoted literals (the
    expression language's only string form, `''` escaping a quote) are skipped, so a parenthesis
    inside a string does not count.
    """
    text = text.strip()
    if not (text.startswith("(") and text.endswith(")")):
        return None
    depth, i, n = 0, 0, len(text)
    while i < n:
        c = text[i]
        if c == "'":
            i += 1
            while i < n:
                if text[i] == "'" and i + 1 < n and text[i + 1] == "'":
                    i += 2
                    continue
                if text[i] == "'":
                    break
                i += 1
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0 and i != n - 1:
                return None
        i += 1
    return text[1:-1].strip() if depth == 0 else None


def _top_level_or(text: str) -> bool:
    """True when `||` occurs outside every parenthesis and string literal in `text`."""
    depth, i, n = 0, 0, len(text)
    while i < n:
        c = text[i]
        if c == "'":
            i += 1
            while i < n:
                if text[i] == "'" and i + 1 < n and text[i + 1] == "'":
                    i += 2
                    continue
                if text[i] == "'":
                    break
                i += 1
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
        elif c == "|" and text[i:i + 2] == "||" and depth == 0:
            return True
        i += 1
    return False


def _comparable(condition: str) -> str:
    """A normalised condition with redundant ENCLOSING parentheses removed — `(a || b)` and
    `a || b` are one condition. Nothing else is rewritten or evaluated."""
    while (inner := _outer_parens(condition)) is not None:
        condition = inner
    return condition


def _gate_condition(value) -> tuple[str, str | None]:
    """A publishing job's `if:` → (its EVENT condition, comparable form; a problem, or None).

    Accepted: no status function at all (GitHub's default rule), or EXACTLY
    `SKIP_TOLERANT_PREFIX` + an event condition that itself carries no status function. A remainder
    with a top-level `||` must be parenthesised: `&&` binds tighter, so
    `!cancelled() && !failure() && a || b` runs on event `b` even after an ancestor FAILED.
    """
    text = _norm_condition(value)
    m = _SKIP_TOLERANT_RE.fullmatch(text)
    if not m:
        fn = _status_function_in(text)
        if fn:
            return _comparable(text), (
                f"uses the status function `{fn}()` outside the one accepted shape. A publishing job "
                "runs under GitHub's default rule, or opens its `if:` with EXACTLY "
                f"`{SKIP_TOLERANT_PREFIX.strip()}` followed by the event condition (which runs it over "
                "upstream SKIPS and nothing else, while the publish lane's verdict still refuses every "
                "direct need that did not succeed). `always()`, a bare `!cancelled()` and any other "
                "status-function shape make the job run after a dependency FAILED or the run was "
                "CANCELLED — exactly the door this lane exists to close")
        return _comparable(text), None
    rest = m.group("rest").strip()
    if _outer_parens(rest) is None and _top_level_or(rest):
        return _comparable(rest), (
            f"opens with `{SKIP_TOLERANT_PREFIX.strip()}` but its event condition `{rest}` has a "
            "top-level `||` outside parentheses. `&&` binds tighter than `||`, so the job would run on "
            "that event even after an ancestor FAILED — wrap the event condition in parentheses")
    cond = _comparable(rest)
    fn = _status_function_in(cond)
    if fn:
        return cond, (
            f"opens with `{SKIP_TOLERANT_PREFIX.strip()}` but its remainder `{rest}` uses the status "
            f"function `{fn}()` again — after the prefix only the EVENT condition may follow")
    return cond, None


def check_caller(workflow: Path, uses_suffix: str, required_raw: str, unrelated_raw: str) -> int:
    try:
        import yaml
    except ImportError:
        die("module-publication.py check-caller needs PyYAML (pip install pyyaml)")
    if not workflow.is_file():
        die(f"the caller's workflow file `{workflow}` does not exist. This lane reads the caller's "
            "own graph to prove the publishing job actually waits for the validation set; a file it "
            "cannot read is a check that did not run, which must never pass.")
    try:
        doc = yaml.safe_load(workflow.read_text(encoding="utf-8")) or {}
    except Exception as exc:  # noqa: BLE001
        die(f"`{workflow}` does not parse as YAML ({exc}) — a caller graph this lane cannot read is "
            "a check that did not run.")
    jobs = doc.get("jobs") or {}
    if not isinstance(jobs, dict) or not jobs:
        die(f"`{workflow}` declares no jobs — nothing here can be the publishing job.")

    publishers = [jid for jid, job in jobs.items()
                  if isinstance((job or {}).get("uses"), str)
                  and uses_suffix in job["uses"].split("@")[0]]
    print(f"{workflow}: {len(jobs)} job(s); {len(publishers)} of them call `{uses_suffix}`")
    if not publishers:
        die(f"no job in `{workflow}` calls `{uses_suffix}` — this lane is running, so one does. "
            "Either `github.workflow_ref` named the wrong file or the caller reaches this lane "
            "through an indirection this check cannot follow; refusing rather than passing on an "
            "empty denominator.")

    required = names(required_raw)
    unrelated: dict[str, str] = {}
    for entry in re.split(r"[\n;]+", unrelated_raw or ""):
        entry = entry.strip()
        if not entry:
            continue
        job, _, reason = entry.partition(":")
        unrelated[job.strip()] = reason.strip()

    graph = _job_graph(jobs)
    problems: list[str] = []
    for name, reason in sorted(unrelated.items()):
        if not reason:
            problems.append(
                f"`unrelated-jobs` exempts `{name}` with NO reason. An exemption is what excuses a job "
                "that could still fail after the hand-over from the verdict, and one that says nothing "
                "is indistinguishable from one nobody meant — say why it validates no source.")
    for pub in publishers:
        job = jobs[pub] or {}
        anc = _ancestors(graph, pub)
        desc = _descendants(graph, pub)
        if not graph.get(pub):
            problems.append(f"`{pub}` has no `needs:` — it would publish the moment the run starts.")
        _, gate_problem = _gate_condition(job.get("if"))
        if gate_problem:
            problems.append(f"`{pub}`'s `if:` {gate_problem} (got: {job.get('if')!r}).")
        if job.get("continue-on-error") in (True, "true"):
            problems.append(f"`{pub}` carries `continue-on-error` — a publication whose failure "
                            "does not red the run is a publication nobody is told about.")
        with_ = job.get("with") or {}
        got = str(with_.get("verdicts", "")).strip()
        if re.sub(r"\s+", "", got) != re.sub(r"\s+", "", VERDICTS_EXPR):
            problems.append(
                f"`{pub}` passes `verdicts: {got or '<nothing>'}` — it must pass `{VERDICTS_EXPR}` "
                "verbatim, so the verdict check reads what GitHub computed rather than a value the "
                "caller curated.")
        for name in required:
            if name not in jobs:
                problems.append(f"`{pub}` declares `{name}` required, but `{workflow}` has no such job.")
            elif name not in anc:
                problems.append(
                    f"`{pub}` declares `{name}` required but does not depend on it (not in `needs:`, "
                    "directly or transitively) — it would publish while that job is still running.")
        for name in sorted(set(jobs) - anc - desc - {pub} - set(unrelated)):
            problems.append(
                f"job `{name}` can still fail AFTER `{pub}` has handed the bundles over: it is "
                "neither upstream of the publication nor declared unrelated. Add it to `needs:`, or "
                "name it in `unrelated-jobs` with the reason it validates no source.")
        for name in sorted(set(unrelated) - set(jobs)):
            problems.append(f"`unrelated-jobs` names `{name}`, which `{workflow}` no longer has — a "
                            "stale exemption hides the job that replaced it.")
        print(f"  `{pub}`: waits for {len(anc)} job(s) — {', '.join(sorted(anc)) or '<none>'}")
        if unrelated:
            print(f"  `{pub}`: declared unrelated — " + "; ".join(
                f"{k} ({v or 'NO REASON GIVEN'})" for k, v in sorted(unrelated.items())))
    for p in problems:
        print(f"::error title=Publication gate::{p}", file=sys.stderr)
    return 1 if problems else 0


# ══════════════════════════════════ check-callers ══════════════════════════════════
PACK_LANE = "node-repo-module-pack.yml"
PUBLISH_LANE = "node-repo-module-publish.yml"
#: H7 (Doc/Architecture/BuildHosts §5): the lane that stages a HOST-executed run's bundles and
#: records for the publisher. When a pack call runs with `executor: host`, its pack legs stage
#: nothing — this call does — so the publisher reads its lane/selection through it.
HOST_PUBLICATION_LANE = "build-host-publication.yml"
#: The only values the pack lane accepts (its `select` job refuses anything else at run time).
PUBLISH_MODES = ("direct", "staged")


def _executor(with_: dict) -> str:
    """A lane call's `executor` in comparable form; absent is the lanes' own default, `actions`."""
    raw = with_.get("executor")
    if raw is None:
        return "actions"
    return _norm_condition(raw) if "${{" in str(raw) else str(raw)


#: Every way an expression names a job of the `needs` context: `needs.x`, `needs['x']`, `needs["x"]`
#: (review of #7271 — bracket access read as "references nothing" and slipped the dependency check).
NEEDS_REF = re.compile(r"""\bneeds\s*(?:\.\s*([A-Za-z_][A-Za-z0-9_-]*)|\[\s*(['"])([A-Za-z_][A-Za-z0-9_-]*)\2\s*\])""")


def _needs_refs(raw: str) -> set[str]:
    """The jobs an expression reads from the `needs` context, in either access syntax."""
    return {m.group(1) or m.group(3) for m in NEEDS_REF.finditer(raw or "")}


def _needs_problems(wf_name: str, jid: str, job: dict, fields: tuple[str, ...]) -> list[str]:
    """Each named expression may read only jobs in the call's OWN `needs:`.

    `needs.<job>` resolves only for a direct need; any other reference evaluates EMPTY. Two calls
    spelling the same expression would then disagree at run time while the static spelling checks
    pass: an `executor` falls back to `actions` (the host-publication call fetches nothing while
    its pack call ran on `host`), and a condition reads false (the host-publication call and its
    publisher are SKIPPED while the pack call staged). `fields` names `executor`/`publish` (in
    `with:`) or `if` (the job's own)."""
    need = job.get("needs") or []
    need = {need} if isinstance(need, str) else set(need)
    problems = []
    for field in fields:
        raw = str(job.get("if") or "") if field == "if" else str((job.get("with") or {}).get(field) or "")
        missing = sorted(_needs_refs(raw) - need)
        if missing:
            problems.append(
                f"{wf_name}: `{jid}`'s `{field}` reads needs.{', needs.'.join(missing)} but its own "
                f"`needs:` does not name {', '.join(f'`{m}`' for m in missing)} — that context is EMPTY "
                "for a job it is not a direct need of, so the expression resolves differently here than "
                "in a sibling call that spells it identically.")
    return problems


def _inert(text) -> str:
    """Record- or host-derived text, printed: one line, workflow-command data escaped.

    The same rule as build-artifacts-fetch.py: a module name or reason carrying `\\n::add-mask::…`
    or a legacy `##[…]` must never become a runner command in this job's log."""
    return (str(text).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            .replace("##[", "##​[").replace("::", ":​:"))


def _may_run_on_host(executor: str) -> bool:
    """Anything but the literal `actions` may resolve to `host` at run time (an expression is read
    as "may"): this static check cannot evaluate it, so it holds the call to the host graph."""
    return executor != "actions"


def _norm_condition(value) -> str:
    """A job `if:` or a pack call's `publish:` in comparable form.

    `${{ }}` is decoration on both (a job `if:` is an expression either way), a folded scalar adds
    line breaks, and an absent `if:` means "always" — so all three are normalised away before the
    two are compared. Nothing is EVALUATED: two different spellings of one condition read as a
    mismatch, which is the safe direction for a check whose failure mode is silence.
    """
    if value is None or value is True:
        return "true"
    if value is False:
        return "false"
    text = str(value).strip()
    m = re.fullmatch(r"\$\{\{(.*)\}\}", text, re.S)
    if m:
        text = m.group(1)
    return re.sub(r"\s+", " ", text).strip()


def _needs_output(value, output: str) -> str | None:
    """The job X in `${{ needs.X.outputs.<output> }}`, or None for any other shape."""
    m = re.fullmatch(r"\$\{\{\s*needs\.([A-Za-z0-9_-]+)\.outputs\.([A-Za-z0-9_-]+)\s*\}\}",
                     str(value or "").strip())
    return m.group(1) if m and m.group(2) == output else None


def _publish_mode(with_: dict) -> str:
    """The pack call's `publish-mode` EXACTLY as the lane will compare it — never stripped.

    The lane tests `inputs.publish-mode == 'staged'` and `case "$MODE" in direct|staged)`, both
    exact. Normalising here (`' staged '` -> `staged`) would let this check pass a value the lane
    reads as neither arm, so the two would disagree about what the call does.
    """
    raw = with_.get("publish-mode", "direct")
    return raw if isinstance(raw, str) else str(raw)


def _calls(jobs: dict, lane: str) -> dict[str, dict]:
    return {jid: job for jid, job in jobs.items()
            if isinstance(job, dict) and isinstance(job.get("uses"), str)
            and job["uses"].split("@")[0].rstrip("/").endswith("/" + lane)}


def check_callers(root: Path) -> int:
    """Every module publication in `root` is wired to wait for its whole validation verdict.

    🚨 WHY THIS RUNS BEFORE MERGE. `node-repo-module-publish.yml` runs `check-caller` itself, but
    only when it is invoked — on trunk. A publisher broken on a pull request is first judged by the
    post-merge run on `main`, which refuses (fail-closed: the registry is untouched) and reds main.
    And one call cannot see the other half at all: a pack call switched to `staged` whose publisher
    was never added, or runs on fewer events, hands the registry NOTHING and reports green.
    """
    try:
        import yaml
    except ImportError:
        die("module-publication.py check-callers needs PyYAML (pip install pyyaml)")
    wf_dir = root / ".github" / "workflows"
    if not wf_dir.is_dir():
        die(f"`{wf_dir}` does not exist — this check read no workflow at all, and a check that read "
            "nothing must never pass.")
    files = sorted(list(wf_dir.glob("*.yml")) + list(wf_dir.glob("*.yaml")))
    problems: list[str] = []
    n_pack = n_pub = n_host = 0
    for wf in files:
        try:
            doc = yaml.safe_load(wf.read_text(encoding="utf-8")) or {}
        except Exception as exc:  # noqa: BLE001
            problems.append(f"`{wf.name}` does not parse as YAML ({exc}) — its publications, if any, "
                            "could not be checked.")
            continue
        jobs = doc.get("jobs") if isinstance(doc, dict) else None
        if not isinstance(jobs, dict):
            continue
        packs, pubs = _calls(jobs, PACK_LANE), _calls(jobs, PUBLISH_LANE)
        hosts = _calls(jobs, HOST_PUBLICATION_LANE)
        n_pack += len(packs)
        n_pub += len(pubs)
        n_host += len(hosts)

        def publisher_pack(pj: dict, hosts=hosts) -> str | None:
            """The pack call a publisher publishes — directly, or through a host-publication call."""
            src = _needs_output((pj.get("with") or {}).get("lane"), "lane")
            if src in hosts:
                return _needs_output((hosts[src].get("with") or {}).get("lane"), "lane")
            return src

        # ── H7: every build-host-publication call belongs to ONE pack call's host graph ──
        # 🚨 Its lane AND its selection come from the pack call's `select`, never from the host's own
        # records: a selection read off what the host staged would accept whatever the host staged.
        for hid, hj in sorted(hosts.items()):
            hw = hj.get("with") or {}
            pack_id = _needs_output(hw.get("lane"), "lane")
            if pack_id is None or pack_id not in packs:
                problems.append(
                    f"{wf.name}: `{hid}` ({HOST_PUBLICATION_LANE}) must pass `lane` as the lane output "
                    f"of a {PACK_LANE} call in this workflow (needs.<call>.outputs.lane) — got "
                    f"{hw.get('lane')!r}. The pack call's `select` is the independent selection a host "
                    "run's records are checked against; without it nothing says what the host owed.")
                continue
            pw = packs[pack_id].get("with") or {}
            for key, output in (("selected", "selected"), ("declared", "declared")):
                if _needs_output(hw.get(key), output) != pack_id:
                    problems.append(f"{wf.name}: `{hid}` must pass `{key}` as needs.{pack_id}.outputs."
                                    f"{output} — the SAME call whose lane it stages — got {hw.get(key)!r}.")
            problems.extend(_needs_problems(wf.name, hid, hj, ("executor", "if")))
            if _executor(hw) != _executor(pw):
                problems.append(
                    f"{wf.name}: `{hid}` passes `executor: {_executor(hw)}` but `{pack_id}` runs with "
                    f"`executor: {_executor(pw)}` — pass the SAME value, so the bytes are fetched from "
                    "the host exactly when the pack legs did not build them.")
            if not _may_run_on_host(_executor(pw)):
                problems.append(
                    f"{wf.name}: `{hid}` stages host-built bytes for `{pack_id}`, which always runs on "
                    "Actions (`executor: actions`) — no host builds for it, so there is nothing to fetch.")
            if _publish_mode(pw) != "staged":
                problems.append(
                    f"{wf.name}: `{hid}` stages host-built bytes for `{pack_id}`, whose publish-mode is "
                    f"`{_publish_mode(pw)}`, not `staged` — no publisher reads a direct call's lane.")
            hneed = hj.get("needs") or []
            hneed = [hneed] if isinstance(hneed, str) else hneed
            if pack_id not in hneed:
                problems.append(f"{wf.name}: `{hid}` reads needs.{pack_id}.outputs but `{pack_id}` is not "
                                "in its own `needs:` — that context resolves only for a job named there.")
            host_on, gate_problem = _gate_condition(hj.get("if"))
            if gate_problem:
                problems.append(f"{wf.name}: `{hid}`'s `if:` {gate_problem} — it would stage bytes after "
                                "the pack call FAILED.")
            stage_on = _comparable(_norm_condition(pw.get("publish", False)))
            if stage_on != host_on:
                problems.append(
                    f"{wf.name}: `{pack_id}` stages on `{stage_on}` but `{hid}` runs on `{host_on}` — give "
                    "the host-publication call exactly the pack call's condition, as its publisher has.")
            # Who READS it is accounted in the pack loop below: a publisher reading through this call
            # counts as that pack call's publisher, so "staged and nobody publishes" and "published
            # twice" are refused there, once, for both graphs.

        for pid, pj in sorted(pubs.items()):
            w = pj.get("with") or {}
            required, unrelated = w.get("required-jobs"), w.get("unrelated-jobs") or ""
            for key, val in (("required-jobs", required), ("unrelated-jobs", unrelated)):
                if "${{" in str(val or ""):
                    problems.append(f"{wf.name}: `{pid}` passes `{key}` as an expression. It must be a "
                                    "literal, so the static half of this gate reads the same list the "
                                    "lane will enforce.")
            if not names(str(required or "")):
                problems.append(f"{wf.name}: `{pid}` declares no `required-jobs` — the lane refuses an "
                                "empty list at run time, so this publisher would never publish.")
            print(f"== {wf.name}#{pid}: the caller-graph check the publication lane runs on trunk")
            if check_caller(wf, PUBLISH_LANE, str(required or ""), str(unrelated)) != 0:
                problems.append(f"{wf.name}: `{pid}` fails the caller-graph check above — on trunk "
                                "the publication lane would refuse the same way, after the merge.")

            src = _needs_output(w.get("lane"), "lane")
            if src is None:
                problems.append(f"{wf.name}: `{pid}` must pass `lane` as the lane output of the pack "
                                f"call it publishes (needs.<call>.outputs.lane) — got {w.get('lane')!r}. "
                                "Artifacts are run-wide; without the lane key a sibling call's bundles "
                                "could answer this publication.")
                continue
            # H7: the publisher may read its lane THROUGH the pack call's build-host-publication call
            # — the host graph. Anything else is refused, exactly as before.
            if src in hosts:
                problems.extend(_needs_problems(wf.name, pid, pj, ("if",)))
                pack_id = _needs_output((hosts[src].get("with") or {}).get("lane"), "lane")
                if pack_id not in packs:
                    # The host call's own wiring is refused (by name) in the host loop below.
                    continue
            elif src in packs:
                pack_id = src
            else:
                problems.append(f"{wf.name}: `{pid}` reads its lane from `{src}`, which is neither a "
                                f"{PACK_LANE} call nor a {HOST_PUBLICATION_LANE} call in this workflow.")
                continue
            for key, output in (("selected", "selected"), ("modules", "declared")):
                if _needs_output(w.get(key), output) != src:
                    problems.append(f"{wf.name}: `{pid}` must pass `{key}` as "
                                    f"needs.{src}.outputs.{output} — the SAME call whose lane it "
                                    f"publishes — got {w.get(key)!r}.")
            need = pj.get("needs") or []
            need = [need] if isinstance(need, str) else need
            if src not in need:
                problems.append(f"{wf.name}: `{pid}` reads needs.{src}.outputs but `{src}` is not in "
                                "its own `needs:` — that context resolves only for a job named there.")
            pw = packs[pack_id].get("with") or {}
            mode = _publish_mode(pw)
            if mode in PUBLISH_MODES and mode != "staged":
                problems.append(f"{wf.name}: `{pid}` publishes `{pack_id}`, whose publish-mode is "
                                f"`{mode}`, not `staged` — that call POSTs in-leg itself and stages "
                                "nothing, so this lane would refuse on an empty staged set.")
            # NEWEST WINS under staged: the pack lane runs its in-leg newer-commit check in `direct`
            # mode ONLY, so a staged call's `publish-newest-only: true` buys nothing unless its
            # PUBLISHER carries it — and asks the scope with the SAME shelf and build extension, or a
            # newer commit that edits only those would not stand an older module down.
            pack_newest, pub_newest = pw.get("publish-newest-only"), w.get("publish-newest-only")
            for who, val in ((pack_id, pack_newest), (pid, pub_newest)):
                if val not in (None, True, False, "true", "false"):
                    problems.append(f"{wf.name}: `{who}` passes `publish-newest-only: {val!r}` — it must be "
                                    "the literal true or false, so this check reads what the lanes will do.")
            if mode == "staged" and pack_newest in (True, "true") and pub_newest not in (True, "true"):
                problems.append(
                    f"{wf.name}: `{pack_id}` asks for `publish-newest-only` but stages (publish-mode: staged), "
                    f"where the pack lane does not run that check — its publisher `{pid}` must pass "
                    "`publish-newest-only: true`, or two overlapping trunk runs publish in FINISHING order "
                    "and the registry keeps whichever arrives last.")
            if pub_newest in (True, "true"):
                # The publisher compares the trunk tip with the CALLER's event commit — the commit the
                # bundles were packed from only when the pack call packs its own checkout. A
                # `content-ref` (an older commit, a tag) or a `content-repository` (another trunk)
                # makes that comparison about the wrong tree: an older commit's bytes would read as
                # "this run IS the tip" and publish. The pack lane's direct-mode check refuses the
                # same foreign repository; here both overrides are refused, in any spelling.
                for key in ("content-repository", "content-ref"):
                    if str(pw.get(key) or "").strip():
                        problems.append(
                            f"{wf.name}: `{pid}` passes `publish-newest-only: true` but `{pack_id}` packs "
                            f"`{key}: {pw.get(key)!r}` — the newer-commit check reads THIS run's commit "
                            "against THIS repository's trunk, so it cannot judge content packed from "
                            "another commit or repository. Turn one of the two off.")
                for key in ("module-libraries-project", "build-extensions-project"):
                    if str(w.get(key) or "") != str(pw.get(key) or ""):
                        problems.append(
                            f"{wf.name}: `{pid}` passes `{key}: {w.get(key) or ''!r}` but `{pack_id}` builds "
                            f"with {pw.get(key) or ''!r} — the newer-commit check must ask the scope about the "
                            "SAME inputs the bundles were built with.")
            # Compared AFTER the one accepted status prefix is taken off (check_caller above refuses
            # every other status-function shape): the remainder must be the pack call's condition.
            stage_on = _comparable(_norm_condition(pw.get("publish", False)))
            publish_on = _gate_condition(pj.get("if"))[0]
            if stage_on != publish_on:
                problems.append(
                    f"{wf.name}: `{pack_id}` stages on `{stage_on}` but `{pid}` runs on `{publish_on}`. A "
                    "run that stages and does not publish hands the registry NOTHING while every tick "
                    "is green (every portal then reads FrameworkDeclined, #2088); a run that publishes "
                    "without staging refuses. Give the publisher exactly the pack call's condition.")
            if src == pack_id and _may_run_on_host(_executor(pw)):
                problems.append(
                    f"{wf.name}: `{pid}` reads its lane straight from `{pack_id}`, whose executor is "
                    f"`{_executor(pw)}` — on a build host that call's pack legs stage NOTHING; the bytes "
                    f"are staged by its {HOST_PUBLICATION_LANE} call. Read `lane`/`selected`/`modules` "
                    "through that call's outputs, so the publisher waits for the bytes it publishes.")

        for kid, kj in sorted(packs.items()):
            kw = kj.get("with") or {}
            mode = _publish_mode(kw)
            if mode not in PUBLISH_MODES:
                problems.append(
                    f"{wf.name}: `{kid}` passes `publish-mode: {mode!r}` — it must be the literal "
                    "`staged` or `direct`. An expression resolves only at run time, so this check "
                    "could not tell whether the call stages (and needs a publisher) or POSTs in-leg; "
                    "a staged call nobody publishes would then pass here and hand the registry nothing.")
                continue
            wired = sorted(pid for pid, pj in pubs.items() if publisher_pack(pj) == kid)
            executor = _executor(kw)
            if _may_run_on_host(executor):
                problems.extend(_needs_problems(wf.name, kid, kj, ("executor", "publish")))
            if mode == "direct" and executor == "host" and _norm_condition(kw.get("publish", False)) != "false":
                problems.append(
                    f"{wf.name}: `{kid}` runs on a build host (`executor: host`) and publishes IN-LEG "
                    "(publish-mode: direct). A host holds no publication credential and its pack legs "
                    "do not run, so a publishing run would hand the registry NOTHING; the lane refuses "
                    f"it at run time. Use `publish-mode: staged` with a {HOST_PUBLICATION_LANE} call "
                    f"and a {PUBLISH_LANE} job reading through it.")
            hosted = sorted(hid for hid, hj in hosts.items()
                            if _needs_output((hj.get("with") or {}).get("lane"), "lane") == kid)
            if mode == "staged" and _may_run_on_host(executor) and len(hosted) != 1:
                problems.append(
                    f"{wf.name}: `{kid}` stages its bundles and may run on a build host (`executor: "
                    f"{executor}`), so exactly ONE {HOST_PUBLICATION_LANE} call must stage a host run's "
                    f"bytes for it — found {len(hosted)} ({', '.join(hosted) or 'none'}). Without it a "
                    "host run stages nothing and its publisher refuses; with two, the same bytes would be "
                    "staged twice under one lane.")
            if mode == "staged" and not wired:
                problems.append(
                    f"{wf.name}: `{kid}` stages its bundles (publish-mode: staged) and NO "
                    f"{PUBLISH_LANE} job reads its lane — the staged bytes reach nobody, and this "
                    "repository silently stops publishing modules.")
            elif mode == "staged" and len(wired) > 1:
                problems.append(f"{wf.name}: `{kid}` is published by {len(wired)} jobs ({', '.join(wired)}) "
                                "— the same bytes would be POSTed twice from one validated call.")
            elif mode != "staged" and _norm_condition(kw.get("publish", False)) != "false":
                # Named, never refused — and since 2026-09-11 this is the design of record, not a
                # repository that has yet to convert: per-module deploy (maintainer directive,
                # MeshWeaver.Plugins#1676 / MeshWeaver#4044, `Doc/Architecture/ModuleBuildArchitecture`
                # → *Per-module deploy*) publishes each module on ITS OWN suite's verdict and never
                # waits for the run's other gates. `staged` + the publication lane stays available
                # as the opt-in for a repository that wants one verdict per run (MeshWeaver#3878's
                # shape); this note must not read as a recommendation to adopt it.
                print(f"  NOTE: {wf.name}#{kid} publishes IN-LEG (publish-mode: {mode}): each module "
                      "reaches the registry on its own suite's verdict, before this run's other gates "
                      "report — the per-module deploy of record since 2026-09-11 (MeshWeaver.Plugins#1676). "
                      f"Not refused; `publish-mode: staged` plus a {PUBLISH_LANE} job is the opt-in for "
                      "one verdict per run (MeshWeaver#3878).")

    print(f"module-publication check-callers: {n_pack} {PACK_LANE} call(s), {n_pub} {PUBLISH_LANE} "
          f"call(s) and {n_host} {HOST_PUBLICATION_LANE} call(s) across {len(files)} workflow file(s) "
          f"under {wf_dir}")
    if not n_pack and not n_pub and not n_host:
        print("  this repository calls neither lane — nothing here hands module bundles to a registry, "
              "so there is no hand-over to order.")
    for p in problems:
        print(f"::error title=Publication wiring::{p}", file=sys.stderr)
    return 1 if problems else 0


# ══════════════════════════════════ publish ══════════════════════════════════
REQUIRED_RECORD_FIELDS = ("lane", "package", "module", "version", "frameworkIdentity")


def _read_records(staged: Path) -> tuple[dict[str, dict], list[str]]:
    records: dict[str, dict] = {}
    problems: list[str] = []
    for path in sorted(staged.rglob("*.publication.json")):
        try:
            rec = json.loads(path.read_text(encoding="utf-8"))
        except Exception as exc:  # noqa: BLE001
            problems.append(f"staged record `{path.name}` is not readable JSON ({exc}) — refusing "
                            "to publish a set whose evidence this lane could not check.")
            continue
        module = rec.get("module")
        if not isinstance(module, str) or not module:
            problems.append(f"staged record `{path.name}` names no module.")
            continue
        if module in records:
            problems.append(f"two staged records claim `{module}` — the staged set is ambiguous, so "
                            "there is no such thing as 'the exact validated bytes' for it.")
            continue
        rec["_dir"] = str(path.parent)
        records[module] = rec
    return records, problems


def publish(a: argparse.Namespace) -> int:
    staged = Path(a.staged)
    lane = (a.lane or "").strip()
    if not lane:
        die("the lane key is empty — every staged record carries the lane of the call that produced "
            "it, and artifacts are RUN-WIDE, so without it a sibling call's bundles could answer "
            "this publication (Plugins#1077). Refusing.")
    declared = {e["module"] for e in load_json_arg(a.declared)} if a.declared else set()
    selected = names(a.selected)
    if not staged.is_dir():
        # 🚨 The ONE shape in which "nothing was staged" is not a finding, and it is positive
        # evidence rather than an absence: the pack call's OWN selection output says it chose zero
        # modules (a narrowed run whose diff reached none of them), so there is nothing this
        # publication could be missing. Every other empty is a refusal below.
        if not selected:
            print(f"the pack call `{lane}` selected ZERO modules — nothing was packed, nothing was "
                  "staged, and nothing is owed to the registry.")
            summarise(["### Module publication", "",
                       f"The pack call `{lane}` selected **zero** modules on this run; nothing was "
                       "handed over."])
            return 0
        die(f"the staged directory `{staged}` does not exist — the pack lane staged nothing for this "
            f"call, yet {len(selected)} module(s) were selected and validated ({', '.join(selected)}). "
            "That is a missing input, never an empty one.")

    records, problems = _read_records(staged)

    foreign = sorted(m for m, r in records.items() if r.get("lane") != lane)
    for m in foreign:
        problems.append(
            f"staged record for `{m}` is stamped lane `{records[m].get('lane')}`, not `{lane}` — it "
            "belongs to another call of the pack lane in this run. A publication may only hand over "
            "bytes its OWN validated call produced.")
    unexpected = sorted(m for m in records if declared and m not in declared)
    for m in unexpected:
        problems.append(f"staged record for `{m}` is not in this call's declared matrix — set aside, "
                        "never published.")
    mine = {m: r for m, r in records.items() if m not in foreign and m not in unexpected}

    for m in selected:
        if m not in mine:
            problems.append(
                f"`{m}` was SELECTED and validated by this run, but no staged publication record "
                "reached this lane. Its pack leg either never ran or never staged — and 'nothing "
                "was staged' must never publish as quietly as 'everything was staged'.")

    plan: list[dict] = []
    for m in sorted(mine):
        rec = mine[m]
        missing = [f for f in REQUIRED_RECORD_FIELDS if not rec.get(f)]
        if "owed" not in rec:
            missing.append("owed")
        if missing:
            problems.append(f"staged record for `{m}` states no {', '.join(missing)}.")
            continue
        if selected and m not in selected:
            problems.append(f"staged record for `{m}` was produced by this lane but the selection "
                            "does not name it — the two disagree about what this run validated.")
            continue
        if not rec.get("owed"):
            print(_inert(f"  -- {m}@{rec['version']}: no hand-over owed — "
                         f"{rec.get('reason') or 'no reason recorded'}"))
            continue
        bundle_meta = rec.get("bundle") or {}
        name, want = bundle_meta.get("name"), bundle_meta.get("sha256")
        if not name or not want:
            problems.append(f"staged record for `{m}` owes a hand-over but names no bundle bytes "
                            "(bundle.name / bundle.sha256).")
            continue
        path = Path(rec["_dir"]) / name
        if not path.is_file():
            problems.append(f"`{m}` owes a hand-over and its record names `{name}`, which is NOT in "
                            "the staged artifact. The bytes the validation covered are missing; "
                            "publishing anything else would publish something nobody validated.")
            continue
        got = sha256_file(path)
        if got != want:
            problems.append(f"`{m}`'s staged bytes are NOT the validated bytes: the record says "
                            f"sha256 {want}, the artifact is {got}. Substituted or truncated — "
                            "refusing the whole publication.")
            continue
        try:
            with zipfile.ZipFile(path) as zf:
                manifest = json.loads(zf.read("meshweaver/manifest.json"))
        except Exception as exc:  # noqa: BLE001
            problems.append(f"cannot read meshweaver/manifest.json out of `{m}`'s staged bundle "
                            f"({exc}) — 'could not check' must never render as 'checked'.")
            continue
        identity = str(manifest.get("frameworkMvid") or "").strip()
        if not identity:
            problems.append(f"`{m}`'s staged bundle states no framework identity — the registry "
                            "would shelve a null and every consumer would answer 'up to date, "
                            "identity could not be checked' for ever (#3154/#3211).")
            continue
        if identity != rec["frameworkIdentity"]:
            problems.append(f"`{m}`'s record was written against framework identity "
                            f"{rec['frameworkIdentity']} and the staged bytes state {identity}.")
            continue
        assembly = (manifest.get("module") or {}).get("assemblyName")
        if assembly != m:
            problems.append(f"`{m}`'s staged bundle declares module `{assembly}`.")
            continue
        plan.append({"module": m, "package": rec["package"], "version": rec["version"],
                     "path": path, "identity": identity, "sha256": got,
                     "ledgerKey": (rec.get("ledger") or {}).get("key") or ""})

    # NEWEST WINS (publish-newest-only): `superseded` decided, over THESE records, which owed modules a
    # newer trunk commit reaches. Each must be one this call was about to hand over — a name the plan
    # does not hold means the two steps read different evidence, and that refuses the whole set.
    stood_down: dict[str, str] = {}
    sup_raw = getattr(a, "superseded", "") or ""
    if sup_raw:
        try:
            doc = load_json_arg(sup_raw)
        except SystemExit:
            raise
        except Exception as exc:  # noqa: BLE001
            die(f"--superseded {sup_raw!r} is not readable JSON ({exc}) — 'cannot tell' never publishes.")
        named = (doc or {}).get("superseded") if isinstance(doc, dict) else None
        if not isinstance(named, dict) or (doc.get("lane") != lane):
            die(f"--superseded is not a `superseded` result for lane `{lane}` (got lane "
                f"{(doc or {}).get('lane') if isinstance(doc, dict) else None!r}) — refusing to guess which "
                "modules a newer trunk commit reaches.")
        planned = {i["module"] for i in plan}
        for m, why in sorted(named.items()):
            if m not in planned:
                problems.append(f"the newer-commit check stood `{m}` down, but this call does not hand "
                                f"`{m}` over — the two steps read different staged evidence.")
            else:
                stood_down[m] = str(why)
        plan = [i for i in plan if i["module"] not in stood_down]

    if problems:
        for p in problems:
            print(f"::error title=Publication refused::{_inert(p)}", file=sys.stderr)
        summarise(["### Module publication REFUSED", "",
                   "The registry was left untouched — not one byte was handed over.", ""]
                  + [f"- {p}" for p in problems])
        return 1

    # H7: build-host-publication.yml runs THIS check — the very one the publisher will run — over a
    # host run's fetched records before it stages them, so a host set the publisher would refuse is
    # red where it was fetched, and the lane's outputs are emitted only over a set that passed.
    if getattr(a, "check_only", False):
        print(f"check-only: {len(mine)} staged record(s) for lane `{lane}` match the selection "
              f"({len(selected)} selected), {len(plan)} owe a hand-over — nothing was POSTed.")
        return 0

    for m, why in sorted(stood_down.items()):
        print(_inert(f"  -- {m}@{mine[m]['version']}: STOOD DOWN for a newer trunk commit — {why}"))
    stood_lines = ([f"- `{m}@{mine[m]['version']}` stood down for a newer trunk commit: {why}"
                    for m, why in sorted(stood_down.items())])

    if not plan:
        print("nothing owes a hand-over in this call: "
              f"{len(mine)} staged record(s), {len(selected)} selected, {len(stood_down)} stood down "
              "for a newer trunk commit, 0 to publish.")
        summarise(["### Module publication", "",
                   f"{len(mine)} staged record(s) accounted for; **nothing handed over**.", ""]
                  + stood_lines
                  + ([f"- `{m}@{rec.get('version', '?')}`: {rec.get('reason') or 'no reason recorded'}"
                      for m, rec in sorted(mine.items()) if m not in stood_down]
                     or ([] if stood_lines else ["The selection was empty."])))
        return 0

    token = os.environ.get(PUBLISH_TOKEN_ENV, "")
    if not token and not a.dry_run:
        die(f"a hand-over is owed for {len(plan)} module(s) but {PUBLISH_TOKEN_ENV} is empty — the "
            "registry would never receive them and this job would have reported success.")

    print(f"handing over {len(plan)} module bundle(s) to {a.registry} as source `{a.source}`:")
    failed: list[str] = []
    published: list[dict] = []
    for item in plan:
        url = (f"{a.registry.rstrip('/')}/api/plugins/bundles/{item['package']}"
               f"?version={item['version']}&packagePath={a.source}/{item['package']}")
        print(_inert(f"  -> {item['module']}@{item['version']} ({item['sha256'][:12]}…, built against "
                     f"{item['identity']}) to {url}"))
        if a.dry_run:
            published.append(item)
            continue
        req = urllib.request.Request(url, data=item["path"].read_bytes(), method="POST", headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/octet-stream",
        })
        try:
            with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT_S) as resp:
                body = resp.read(4096).decode("utf-8", "replace")
                print(f"     registry answered {resp.status}: {body}")
        except urllib.error.HTTPError as exc:
            body = exc.read(4096).decode("utf-8", "replace")
            failed.append(f"registry refused `{item['module']}@{item['version']}` (HTTP "
                          f"{exc.code}): {body}")
            continue
        except Exception as exc:  # noqa: BLE001 — a transport failure is a refusal, never a pass
            failed.append(f"could not reach the registry for `{item['module']}@{item['version']}`: {exc}")
            continue
        published.append(item)
        # 🚨 AFTER the 2xx, never before: a staged artifact is not a Published ledger record, and a
        # failed hand-over must not be recorded as one (#3878, repair contract 4).
        if a.ledger == "required" and item["ledgerKey"] and a.ledger_script:
            rc = subprocess.run([sys.executable, a.ledger_script, "record", "--key", item["ledgerKey"],
                                 "--status", "Published"], check=False).returncode
            if rc != 0:
                print(f"::warning::the build ledger did not record `{item['module']}` as Published "
                      f"(exit {rc}); the bundle IS served — the ledger may cost a duplicate build, "
                      "never a green.")

    lines = ["### Module publication", "",
             f"Source `{a.source}` → {a.registry}", ""]
    lines += [f"- `{i['module']}@{i['version']}` — sha256 `{i['sha256'][:16]}…`, framework "
              f"`{i['identity']}`" for i in published]
    lines += stood_lines
    if failed:
        lines += ["", "**Refused by the registry:**", ""] + [f"- {f}" for f in failed]
    summarise(lines)
    for f in failed:
        print(f"::error title=Publication failed::{_inert(f)}", file=sys.stderr)
    print(f"published {len(published)} of {len(plan)} module bundle(s)")
    return 1 if failed else 0


# ══════════════════════════════════ partition ══════════════════════════════════
def partition(staged: Path, lane: str, aside: Path) -> int:
    """Set aside, BY NAME, every host record stamped with ANOTHER lane, and the bundle it names.

    A host's receipts are keyed by repository, sha and stage — not by lane — so when two pack calls
    of one run share a host stage, each build-host-publication call fetches BOTH calls' records
    (review of #7271). This keeps only what is provably another call's: a record whose `lane` is a
    different non-empty string. A record with no readable lane STAYS, so the check that follows
    refuses it rather than this step hiding it, and the check's completeness rule (one record per
    SELECTED module) still decides whether THIS call's set is whole. A bundle a kept record names is
    never moved, even if a foreign record names it too.
    """
    lane = (lane or "").strip()
    if not lane:
        die("partition: the lane key is empty — nothing could be told apart from a sibling call's records.")
    if not staged.is_dir():
        print(f"partition: `{staged}` does not exist — nothing fetched, nothing to set aside.")
        return 0
    foreign: list[tuple[Path, dict]] = []
    kept_bundles: set[Path] = set()
    for path in sorted(staged.rglob("*.publication.json")):
        try:
            rec = json.loads(path.read_text(encoding="utf-8"))
        except Exception:  # noqa: BLE001 — unreadable stays, and the check refuses it by name
            continue
        other = rec.get("lane") if isinstance(rec, dict) else None
        name = ((rec.get("bundle") or {}).get("name") if isinstance(rec, dict) else None) or ""
        if isinstance(other, str) and other.strip() and other != lane:
            foreign.append((path, rec))
        elif name:
            kept_bundles.add((path.parent / name).resolve())
    for path, rec in foreign:
        rel = path.relative_to(staged)
        dest = aside / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        path.rename(dest)
        name = (rec.get("bundle") or {}).get("name") or ""
        bundle = path.parent / name
        if name and bundle.is_file() and bundle.resolve() not in kept_bundles:
            bundle.rename(dest.parent / name)
        print(_inert(f"  set aside: `{rec.get('module') or rel}` is stamped lane `{rec.get('lane')}` — "
                     f"another pack call's record, never published by lane `{lane}`"))
    print(f"partition: {len(foreign)} record(s) of another lane set aside; the rest is lane `{lane}`'s to check.")
    return 0


# ══════════════════════════════════ superseded ══════════════════════════════════
SHA = re.compile(r"^[0-9a-f]{40}$")


def superseded(a: argparse.Namespace) -> int:
    """Which owed, staged modules does a NEWER trunk commit reach? (`publish-newest-only`, staged arm)

    The in-leg check of node-repo-module-pack.yml (`direct` mode) runs seconds before its own POST.
    Under `staged` the POST moves to the END of the run, so the check moves with it: it runs in the
    publication lane, right before the hand-over, over the records that lane is about to publish.
    The question and its instrument are unchanged — the trunk tip's diff against this run's commit,
    asked of node-repo-scope.py `--for modules --publication-reach` for each module's own matrix
    entry — and so is the safety argument: a module the tip reaches is in that commit's own run's
    selection (the tip's diff is part of every scope it computes), and that run publishes a tree
    containing this one. Every uncertainty is RED: "cannot tell" must never publish as "nothing newer".
    """
    lane = (a.lane or "").strip()
    if not lane:
        die("superseded: the lane key is empty — the records this check reads could not be told apart "
            "from a sibling call's.")
    for flag, value in (("--mine", a.mine), ("--tip", a.tip)):
        if not SHA.match(value or ""):
            die(f"superseded: {flag} is {value!r}, not a full commit sha — the trunk tip cannot be "
                "compared with this run's commit, and 'cannot tell' never publishes.")
    if not a.out:
        die("superseded: --out is required — the hand-over reads the decision from that file.")
    declared = {e["module"]: e for e in load_json_arg(a.declared)} if a.declared else {}
    staged = Path(a.staged)
    records, _ = _read_records(staged) if staged.is_dir() else ({}, [])
    owed = sorted(m for m, r in records.items() if r.get("lane") == lane and r.get("owed") and m in declared)
    changed = [c for c in (a.changed_list or "").split(",") if c]
    decided: dict[str, str] = {}
    if a.tip == a.mine:
        print(f"this run's commit {a.mine} IS the trunk tip — nothing newer; {len(owed)} owed module(s) publish")
    elif not changed:
        print(f"the trunk moved to {a.tip}, but its tree equals this run's — nothing newer reaches any of "
              f"the {len(owed)} owed module(s)")
    else:
        if not a.scope_script or not Path(a.scope_script).is_file():
            die(f"superseded: the scope script {a.scope_script!r} is not a file — the newer commit's reach "
                "cannot be asked, and 'cannot tell' never publishes.")
        import tempfile
        with tempfile.TemporaryDirectory(prefix="mw-newer-") as td:
            for m in owed:
                entry = Path(td) / f"{m}.json"
                entry.write_text(json.dumps([declared[m]]), encoding="utf-8")
                argv = [sys.executable, a.scope_script, "--for", "modules", "--root", a.root or ".",
                        "--modules", f"@{entry}", "--event", "pull_request", "--publication-reach"]
                if a.module_libraries_project:
                    argv += ["--module-libraries-project", a.module_libraries_project]
                if a.build_extensions_project:
                    argv += ["--build-extensions-project", a.build_extensions_project]
                for ev in a.msbuild_evaluation or []:
                    argv += ["--msbuild-evaluation", ev]
                argv += ["--changed-list", ",".join(changed)]
                env = {**os.environ, "GITHUB_OUTPUT": str(Path(td) / f"{m}.out"), "GITHUB_STEP_SUMMARY": os.devnull}
                proc = subprocess.run(argv, capture_output=True, text=True, env=env, check=False)
                if proc.returncode != 0:
                    sys.stderr.write(proc.stderr[-4000:])
                    die(_inert(f"superseded: the scope script failed for `{m}` (exit {proc.returncode}) — "
                               "whether the trunk tip reaches it cannot be told, and 'cannot tell' never publishes."))
                try:
                    answer = json.loads(proc.stdout)
                    count = int(answer["count"])
                except Exception as exc:  # noqa: BLE001 — an unreadable answer is RED, never "zero"
                    die(_inert(f"superseded: the scope script's answer for `{m}` is not readable ({exc}) — "
                               "'cannot tell' never publishes."))
                if count:
                    decided[m] = (f"trunk tip {a.tip} reaches its dependency network "
                                  f"({answer.get('reason') or 'no reason given'}); that commit's own run publishes it")
                    print("::notice title=Stood down for a newer commit::"
                          + _inert(f"{m} is NOT published from {a.mine} — {decided[m]}"))
                else:
                    print(_inert(f"the trunk moved to {a.tip}, but nothing it changed reaches {m} — publishing"))
    Path(a.out).write_text(json.dumps({"lane": lane, "mine": a.mine, "tip": a.tip, "superseded": decided},
                                      indent=2), encoding="utf-8")
    print(f"superseded: {len(decided)} of {len(owed)} owed module(s) stand down for trunk tip {a.tip}")
    return 0


# ══════════════════════════════════ lane-key ══════════════════════════════════
#: node-repo-module-pack.yml's `This call's lane key` step, byte for byte:
#:   slug = printf '%s' "$LANE" | tr -c 'A-Za-z0-9._-' '-' | cut -c1-24
#:   id   = "${slug:-lane}-$(printf '%s' "$DECLARED" | sha256sum | cut -c1-12)"
#: test-module-pack-host.py pins the step's text, so the two cannot drift apart unnoticed.
LANE_SLUG_ALPHABET = frozenset(b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-")


def lane_key(lane_id: str, modules_raw: str) -> str:
    """The lane key a pack call stamps on every marker, receipt and publication record.

    A build host's module stage stamps its `<module>.publication.json` records with it (H9: "in the
    module pack lane's format"), so the publisher's foreign-lane refusal holds for host-built bytes
    exactly as for Actions-built ones. `modules_raw` is the call's `modules` input VERBATIM.
    """
    slug = bytes(b if b in LANE_SLUG_ALPHABET else ord("-") for b in lane_id.encode("utf-8"))[:24]
    fingerprint = hashlib.sha256(modules_raw.encode("utf-8")).hexdigest()[:12]
    return f"{slug.decode('ascii') or 'lane'}-{fingerprint}"


# ══════════════════════════════════ self-test ══════════════════════════════════
def self_test() -> int:  # noqa: C901 — a table of cases reads better than a dozen helpers
    import io
    import contextlib
    import tempfile

    failures: list[str] = []
    # 🚨 The cases below drive `verdict`/`publish` into their REFUSED arms on purpose, and both write
    # a table to $GITHUB_STEP_SUMMARY. Left set, the job summary of every run that proves this gate
    # (the publication lane itself, node-repo-validate) opens with "REFUSED — nothing was handed to
    # the registry" for a publication that never existed.
    os.environ.pop("GITHUB_STEP_SUMMARY", None)

    def check(label: str, ok: bool, detail: str = ""):
        print(("  ok   " if ok else "  FAIL ") + label + (f" — {detail}" if detail and not ok else ""))
        if not ok:
            failures.append(label)

    def run(fn, *args, **kwargs) -> tuple[int, str]:
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf), contextlib.redirect_stderr(buf):
            try:
                rc = fn(*args, **kwargs)
            except SystemExit as exc:
                rc = exc.code if isinstance(exc.code, int) else 1
        return rc, buf.getvalue()

    print("== verdict")
    green = json.dumps({"validate": {"result": "success"}, "gate": {"result": "success"}})
    rc, out = run(verdict, green, "validate gate")
    check("every dependency success publishes", rc == 0, out)
    for bad in ("failure", "cancelled", "skipped"):
        rc, out = run(verdict, json.dumps({"validate": {"result": "success"},
                                           "gate": {"result": bad}}), "validate gate")
        check(f"a `{bad}` dependency refuses", rc == 1 and "gate" in out, out)
    rc, out = run(verdict, json.dumps({"validate": {"result": "success"}}), "validate gate")
    check("a required job MISSING from needs refuses", rc == 1 and "not among" in out.lower() or
          rc == 1 and "NOT among" in out, out)
    rc, out = run(verdict, json.dumps({"validate": {}}), "validate")
    check("a dependency with no readable result refuses", rc == 1, out)
    rc, out = run(verdict, "{}", "validate")
    check("an EMPTY needs context refuses", rc == 1, out)
    rc, out = run(verdict, green, "")
    check("an empty required-jobs list refuses", rc == 1, out)
    rc, out = run(verdict, "not json", "validate")
    check("unreadable verdicts refuse", rc == 1, out)

    print("== check-caller")
    good_caller = """
name: ci
on: { push: { branches: [main] } }
jobs:
  validate:
    runs-on: ubuntu-latest
    steps: [{ run: echo }]
  modules:
    needs: [validate]
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-pack.yml@abc
  gate:
    needs: [modules]
    runs-on: ubuntu-latest
    steps: [{ run: echo }]
  publish:
    needs: [validate, modules, gate]
    if: github.event_name == 'push'
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-publish.yml@abc
    with:
      verdicts: ${{ toJSON(needs) }}
      required-jobs: validate modules gate
  summary:
    needs: [publish]
    if: always()
    runs-on: ubuntu-latest
    steps: [{ run: echo }]
"""
    with tempfile.TemporaryDirectory() as td:
        wf = Path(td) / "ci.yml"

        def caller(text: str, required="validate modules gate", unrelated=""):
            wf.write_text(text, encoding="utf-8")
            return run(check_caller, wf, "node-repo-module-publish.yml", required, unrelated)

        rc, out = caller(good_caller)
        check("a caller that waits for its whole graph passes", rc == 0, out)

        # MUTATION 1 — a required dependency dropped from `needs:` (and from nothing else).
        rc, out = caller(good_caller.replace("needs: [validate, modules, gate]",
                                             "needs: [validate, modules]"))
        check("MUTATION: a dropped `needs:` entry is caught", rc == 1 and "gate" in out, out)

        # MUTATION 1b — dropped from BOTH `needs:` and `required-jobs`: invisible at runtime, which
        # is exactly why this static half exists.
        rc, out = caller(good_caller.replace("needs: [validate, modules, gate]",
                                             "needs: [validate, modules]")
                                    .replace("required-jobs: validate modules gate",
                                             "required-jobs: validate modules"),
                         required="validate modules")
        check("MUTATION: a dependency dropped from needs AND required-jobs is caught",
              rc == 1 and "still fail AFTER" in out, out)

        # MUTATION 2 — a status function re-opens the door the verdict closes.
        for expr in ("${{ always() }}", "${{ !cancelled() }}", "${{ success() || failure() }}"):
            rc, out = caller(good_caller.replace("if: github.event_name == 'push'", f"if: {expr}"))
            check(f"MUTATION: `if: {expr}` on the publisher is caught", rc == 1 and "status function" in out, out)

        # THE ONE ACCEPTED STATUS SHAPE (MeshWeaver.Plugins#4478): the default rule is transitive and
        # skips the publisher over any by-design upstream skip, so exactly `!cancelled() && !failure()
        # && <event>` is accepted — and every looser or reordered spelling stays refused.
        ev = "github.event_name == 'push'"
        rc, out = caller(good_caller.replace(f"if: {ev}", f"if: ${{{{ !cancelled() && !failure() && {ev} }}}}"))
        check("the skip-tolerant prefix `!cancelled() && !failure() && <event>` passes", rc == 0, out)
        rc, out = caller(good_caller.replace(
            f"if: {ev}", f"if: ${{{{ !cancelled() && !failure() && ({ev} || github.event_name == 'schedule') }}}}"))
        check("…and with a parenthesised `||` event condition", rc == 0, out)
        for expr, why in (
                (f"${{{{ !cancelled() && {ev} }}}}", "status function"),
                (f"${{{{ !failure() && {ev} }}}}", "status function"),
                (f"${{{{ !failure() && !cancelled() && {ev} }}}}", "status function"),
                (f"${{{{ always() && {ev} }}}}", "status function"),
                (f"${{{{ !cancelled() || !failure() && {ev} }}}}", "status function"),
                (f"${{{{ success() && !failure() && {ev} }}}}", "status function"),
                ("${{ !cancelled() && !failure() }}", "status function"),
                ("${{ !cancelled() && !failure() && always() }}", "again"),
                (f"${{{{ !cancelled() && !failure() && {ev} && !cancelled() }}}}", "again"),
                (f"${{{{ !cancelled() && !failure() && {ev} || github.event_name == 'schedule' }}}}", "top-level `||`"),
                (f"${{{{ !cancelled() && !failure() && ({ev}) || (github.event_name == 'schedule') }}}}", "top-level `||`"),
                (f"${{{{ !cancelled() && !failure() && {ev} || always() }}}}", "top-level `||`"),
                # Function names are case-insensitive in GitHub expressions: an upper- or mixed-case
                # status function is the same function and must be refused exactly like lowercase.
                ("${{ ALWAYS() }}", "status function"),
                (f"${{{{ Success() && {ev} }}}}", "status function"),
                (f"${{{{ !cancelled() && !failure() && SUCCESS() && {ev} }}}}", "again"),
                (f"${{{{ !cancelled() && !failure() && {ev} && !Cancelled() }}}}", "again"),
                (f"${{{{ !cancelled() && !failure() && ({ev} || aLwAyS()) }}}}", "again"),
                # A non-canonical spelling of the PREFIX is not the prefix: it is refused, never read
                # as the accepted shape (and never as a looser one).
                (f"${{{{ !CANCELLED() && !FAILURE() && {ev} }}}}", "status function"),
                (f"${{{{ !Cancelled() && !failure() && {ev} }}}}", "status function")):
            rc, out = caller(good_caller.replace(f"if: {ev}", f"if: {expr}"))
            check(f"MUTATION: `if: {expr}` on the publisher is caught", rc == 1 and why in out, out)
        # Negative control on the precedence scan: a `||` inside a string literal is not top-level.
        rc, out = caller(good_caller.replace(
            f"if: {ev}", "if: ${{ !cancelled() && !failure() && github.event_name == 'a || b' }}"))
        check("a `||` inside a string literal after the prefix is not read as top-level", rc == 0, out)

        # MUTATION 3 — the caller curates the verdicts instead of handing over the needs context.
        rc, out = caller(good_caller.replace("verdicts: ${{ toJSON(needs) }}",
                                             "verdicts: '{\"validate\":{\"result\":\"success\"}}'"))
        check("MUTATION: a curated `verdicts` literal is caught", rc == 1 and "verbatim" in out, out)

        rc, out = caller(good_caller.replace("    needs: [validate, modules, gate]\n", "", 1)
                         .replace("required-jobs: validate modules gate", "required-jobs: validate"),
                         required="validate")
        check("a publisher with NO needs is caught", rc == 1, out)

        rc, out = caller(good_caller, required="validate modules gate nonesuch")
        check("a required job the workflow does not have is caught", rc == 1 and "nonesuch" in out, out)

        rc, out = caller(good_caller, unrelated="ghost: retired last year")
        check("a STALE unrelated-jobs entry is caught", rc == 1 and "ghost" in out, out)

        rc, out = caller(good_caller.replace(
            "  summary:\n    needs: [publish]\n", "  summary:\n"))
        check("a job that is neither upstream nor declared unrelated is caught",
              rc == 1 and "summary" in out, out)

        rc, out = caller(good_caller.replace(
            "  summary:\n    needs: [publish]\n", "  summary:\n"),
            unrelated="summary: prints a table, validates no source")
        check("…and passes once it is DECLARED unrelated with a reason", rc == 0, out)

        rc, out = caller(good_caller.replace(
            "  summary:\n    needs: [publish]\n", "  summary:\n"), unrelated="summary:")
        check("MUTATION: an unrelated-jobs entry with NO reason is caught", rc == 1 and "NO reason" in out, out)

        rc, out = caller(good_caller.replace(
            "node-repo-module-publish.yml@abc", "node-repo-tag-modules.yml@abc"))
        check("a workflow with NO publishing job refuses (empty denominator)", rc == 1, out)

    print("== check-callers (the pre-merge half, over a whole repository)")
    wired = """
name: ci
on: { push: { branches: [main] }, pull_request: {} }
jobs:
  validate:
    runs-on: ubuntu-latest
    steps: [{ run: echo }]
  modules:
    needs: [validate]
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-pack.yml@main
    with:
      publish: >-
        ${{ github.event_name == 'push'
        || github.event_name == 'schedule' }}
      publish-mode: staged
  gate:
    needs: [modules]
    runs-on: ubuntu-latest
    steps: [{ run: echo }]
  publish-modules:
    if: >
      github.event_name == 'push' ||
      github.event_name == 'schedule'
    needs: [validate, modules, gate]
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-module-publish.yml@main
    with:
      verdicts: ${{ toJSON(needs) }}
      required-jobs: validate modules gate
      lane: ${{ needs.modules.outputs.lane }}
      selected: ${{ needs.modules.outputs.selected }}
      modules: ${{ needs.modules.outputs.declared }}
      platform-ref: abc
"""
    with tempfile.TemporaryDirectory() as td:
        repo = Path(td)
        (repo / ".github" / "workflows").mkdir(parents=True)

        def callers(text: str) -> tuple[int, str]:
            (repo / ".github" / "workflows" / "ci.yml").write_text(text, encoding="utf-8")
            return run(check_callers, repo)

        rc, out = callers(wired)
        check("a staged pack call with one publisher on the same events passes", rc == 0, out)
        rc, out = callers(wired.split("  publish-modules:")[0])
        check("MUTATION: a staged pack call whose publisher was never added is caught",
              rc == 1 and "reach nobody" in out, out)
        rc, out = callers(wired.replace("      github.event_name == 'push' ||\n", ""))
        check("MUTATION: a publisher running on FEWER events than the pack call stages on is caught",
              rc == 1 and "stages on" in out, out)
        rc, out = callers(wired.replace("needs: [validate, modules, gate]", "needs: [validate, modules]"))
        check("MUTATION: a required dependency dropped from the publisher's `needs:` is caught pre-merge",
              rc == 1 and "gate" in out, out)
        rc, out = callers(wired.replace("    if: >\n", "    if: ${{ !cancelled() }} && >\n", 1)
                          .replace("    if: ${{ !cancelled() }} && >\n      github.event_name == 'push' ||\n"
                                   "      github.event_name == 'schedule'\n",
                                   "    if: ${{ !cancelled() }}\n"))
        check("MUTATION: a status function on the publisher's `if:` is caught pre-merge",
              rc == 1 and "status function" in out, out)
        tolerant = wired.replace(
            "    if: >\n      github.event_name == 'push' ||\n      github.event_name == 'schedule'\n    needs: [validate, modules, gate]\n",
            "    if: >\n      !cancelled() && !failure() && (\n      github.event_name == 'push' ||\n"
            "      github.event_name == 'schedule')\n    needs: [validate, modules, gate]\n", 1)
        check("the skip-tolerant fixture really carries the prefix (a control on the fixture itself)",
              tolerant != wired and "!cancelled() && !failure() && (" in tolerant, tolerant)
        rc, out = callers(tolerant)
        check("a publisher `!cancelled() && !failure() && (<the pack call's condition>)` passes pre-merge",
              rc == 0, out)
        rc, out = callers(tolerant.replace("      github.event_name == 'push' ||\n      github.event_name == 'schedule')\n",
                                           "      github.event_name == 'push')\n", 1))
        check("MUTATION: a skip-tolerant publisher whose event remainder is NOT the pack call's condition is caught",
              rc == 1 and "stages on" in out, out)
        rc, out = callers(tolerant.replace("!cancelled() && !failure() && (", "!cancelled() && (", 1))
        check("MUTATION: a bare `!cancelled()` (no `!failure()`) on the publisher is caught pre-merge",
              rc == 1 and "status function" in out, out)
        rc, out = callers(tolerant.replace("!cancelled() && !failure() && (", "always() && (", 1))
        check("MUTATION: `always() && <condition>` on the publisher is caught pre-merge",
              rc == 1 and "status function" in out, out)
        rc, out = callers(tolerant.replace("!cancelled() && !failure() && (\n", "!cancelled() && !failure() &&\n", 1)
                          .replace("      github.event_name == 'schedule')\n", "      github.event_name == 'schedule'\n", 1))
        check("MUTATION: an UNPARENTHESISED `||` remainder after the prefix is caught pre-merge",
              rc == 1 and "top-level `||`" in out, out)
        rc, out = callers(wired.replace("publish-mode: staged", "publish-mode: direct"))
        check("a publisher wired to a pack call that still POSTs in-leg is caught",
              rc == 1 and "not `staged`" in out, out)
        rc, out = callers(wired.replace("${{ needs.modules.outputs.declared }}", "'[]'"))
        check("a publisher whose `modules` is not the pack call's own `declared` output is caught",
              rc == 1 and "declared" in out, out)
        rc, out = callers(wired.replace("${{ needs.modules.outputs.lane }}", "catalog"))
        check("a publisher whose `lane` is a literal rather than the call's lane output is caught",
              rc == 1 and "lane output" in out, out)

        # NEWEST WINS under staged: the pack lane's in-leg check is `direct`-only, so the flag must
        # travel to the publisher, with the same shelf and build extension the bundles were built with.
        newest = (wired.replace("      publish-mode: staged\n",
                                "      publish-mode: staged\n      publish-newest-only: true\n"
                                "      module-libraries-project: src/Shelf/Shelf.csproj\n")
                  .replace("      platform-ref: abc\n",
                           "      platform-ref: abc\n      publish-newest-only: true\n"
                           "      module-libraries-project: src/Shelf/Shelf.csproj\n"))
        rc, out = callers(newest)
        check("newest-only: a staged pack call whose publisher carries the flag and the same shelf passes",
              rc == 0, out)
        rc, out = callers(newest.replace("      platform-ref: abc\n      publish-newest-only: true\n",
                                         "      platform-ref: abc\n"))
        check("MUTATION newest-only: a staged pack call asking for it, its publisher NOT carrying it, is caught",
              rc == 1 and "FINISHING order" in out, out)
        rc, out = callers(newest.replace("      platform-ref: abc\n      publish-newest-only: true\n"
                                         "      module-libraries-project: src/Shelf/Shelf.csproj\n",
                                         "      platform-ref: abc\n      publish-newest-only: true\n"))
        check("MUTATION newest-only: a publisher asking the scope WITHOUT the pack call's shelf is caught",
              rc == 1 and "module-libraries-project" in out and "SAME inputs" in out, out)
        rc, out = callers(newest.replace("      platform-ref: abc\n      publish-newest-only: true\n",
                                         "      platform-ref: abc\n      publish-newest-only: ${{ vars.X }}\n"))
        check("MUTATION newest-only: an EXPRESSION for the flag is caught (this check could not read it)",
              rc == 1 and "literal true or false" in out, out)
        for key, val in (("content-ref", "${{ inputs.ref }}"), ("content-repository", "Systemorph/Other")):
            rc, out = callers(newest.replace("      publish-mode: staged\n",
                                             f"      publish-mode: staged\n      {key}: {val}\n"))
            check(f"MUTATION newest-only: a pack call with `{key}` (content not from this run's commit) is caught",
                  rc == 1 and key in out and "Turn one of the two off" in out, out)
        rc, out = callers(wired.replace("required-jobs: validate modules gate",
                                        "required-jobs: ${{ vars.REQUIRED }}"))
        check("a `required-jobs` passed as an expression is caught (the static half could not read it)",
              rc == 1 and "expression" in out, out)
        rc, out = callers(wired.replace("publish-mode: staged", "publish-mode: ${{ vars.PUBLISH_MODE }}"))
        check("a `publish-mode` passed as an expression is caught (staged or direct cannot be read)",
              rc == 1 and "must be the literal" in out, out)
        rc, out = callers(wired.replace("publish-mode: staged", "publish-mode: ' staged '"))
        check("a padded `publish-mode` is caught — the lane compares it exactly, so it is neither arm",
              rc == 1 and "must be the literal" in out, out)
        rc, out = callers(wired.replace("publish-mode: staged", "publish-mode: stagd"))
        check("an unknown literal `publish-mode` is caught", rc == 1 and "must be the literal" in out, out)
        rc, out = callers(wired.replace("required-jobs: validate modules gate",
                                        "required-jobs: validate modules\n      unrelated-jobs: 'gate:'"))
        check("MUTATION pre-merge: a gate exempted with an EMPTY reason is caught", rc == 1 and "NO reason" in out, out)
        direct = wired.split("  publish-modules:")[0].replace("publish-mode: staged", "publish-mode: direct")
        rc, out = callers(direct)
        check("an in-leg (direct) publisher is NOT refused, but is NAMED", rc == 0 and "IN-LEG" in out, out)
        rc, out = callers("on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps: [{ run: echo }]\n")
        check("a repository calling neither lane passes and SAYS so", rc == 0 and "neither lane" in out, out)
        rc, out = run(check_callers, repo / "missing")
        check("a root with no workflow directory refuses — it read nothing", rc == 1, out)

        # ── H7: the HOST graph — pack (executor host) → build-host-publication → publisher ──
        hosted = wired.replace(
            "      publish-mode: staged\n",
            "      publish-mode: staged\n"
            "      executor: ${{ needs.validate.outputs.executor || 'actions' }}\n"
            "      host-verdict: ${{ needs.validate.result }}\n", 1).replace(
            "  publish-modules:",
            "  host-publication:\n"
            "    if: >\n"
            "      github.event_name == 'push' ||\n"
            "      github.event_name == 'schedule'\n"
            "    needs: [modules, validate]\n"
            "    uses: Systemorph/MeshWeaver/.github/workflows/build-host-publication.yml@main\n"
            "    with:\n"
            "      executor: ${{ needs.validate.outputs.executor || 'actions' }}\n"
            "      api-url: https://ops.example.com/api/builds\n"
            "      lane: ${{ needs.modules.outputs.lane }}\n"
            "      selected: ${{ needs.modules.outputs.selected }}\n"
            "      declared: ${{ needs.modules.outputs.declared }}\n"
            "      artifact-store: file:/ci-artifacts\n"
            "      artifact-store-id: abc\n"
            "  publish-modules:", 1).replace(
            "    needs: [validate, modules, gate]\n", "    needs: [validate, modules, gate, host-publication]\n", 1).replace(
            "required-jobs: validate modules gate", "required-jobs: validate modules gate host-publication", 1).replace(
            "      lane: ${{ needs.modules.outputs.lane }}\n      selected: ${{ needs.modules.outputs.selected }}\n"
            "      modules: ${{ needs.modules.outputs.declared }}\n",
            "      lane: ${{ needs.host-publication.outputs.lane }}\n"
            "      selected: ${{ needs.host-publication.outputs.selected }}\n"
            "      modules: ${{ needs.host-publication.outputs.declared }}\n", 1)
        check("the host fixture really is the host graph (a control on the fixture itself)",
              hosted.count("build-host-publication.yml") == 1
              and "needs.host-publication.outputs.lane" in hosted and "executor:" in hosted, hosted)
        rc, out = callers(hosted)
        check("H7: pack (executor host) → build-host-publication → publisher passes", rc == 0, out)
        literal_host = hosted.replace("${{ needs.validate.outputs.executor || 'actions' }}", "host")
        rc, out = callers(literal_host)
        check("H7: …and with a literal `executor: host` on both calls", rc == 0, out)
        rc, out = callers(hosted.split("  host-publication:", 1)[0] + "  publish-modules:"
                          + hosted.split("  publish-modules:", 1)[1])
        check("MUTATION H7: a host-run pack call with NO build-host-publication call is caught",
              rc == 1 and "exactly ONE" in out, out)
        rc, out = callers(hosted.replace("needs.host-publication.outputs.lane", "needs.modules.outputs.lane")
                          .replace("needs.host-publication.outputs.selected", "needs.modules.outputs.selected")
                          .replace("needs.host-publication.outputs.declared", "needs.modules.outputs.declared"))
        check("MUTATION H7: a publisher reading a host-run pack call's lane DIRECTLY is caught",
              rc == 1 and "stage NOTHING" in out, out)
        rc, out = callers(hosted.replace("      executor: ${{ needs.validate.outputs.executor || 'actions' }}\n      api-url",
                                         "      executor: host\n      api-url", 1))
        check("MUTATION H7: a build-host-publication executor that differs from the pack call's is caught",
              rc == 1 and "SAME value" in out, out)
        rc, out = callers(hosted.replace("      lane: ${{ needs.modules.outputs.lane }}\n      selected: ${{ needs.modules.outputs.selected }}\n      declared",
                                         "      lane: ${{ needs.validate.outputs.lane }}\n      selected: ${{ needs.modules.outputs.selected }}\n      declared", 1))
        check("MUTATION H7: a build-host-publication lane not read from a pack call is caught",
              rc == 1 and "independent selection" in out, out)
        rc, out = callers(hosted.replace("      selected: ${{ needs.modules.outputs.selected }}\n      declared",
                                         "      selected: MeshWeaver.AI\n      declared", 1))
        check("MUTATION H7: a build-host-publication selection that is not the pack call's is caught",
              rc == 1 and "`selected`" in out, out)
        rc, out = callers(hosted.replace("  host-publication:\n    if: >\n      github.event_name == 'push' ||\n",
                                         "  host-publication:\n    if: >\n", 1))
        check("MUTATION H7: a build-host-publication running on fewer events than the pack call stages on is caught",
              rc == 1 and "host-publication call exactly" in out, out)
        rc, out = callers(hosted.replace("  host-publication:\n    if: >\n      github.event_name == 'push' ||\n"
                                         "      github.event_name == 'schedule'\n",
                                         "  host-publication:\n    if: ${{ always() }}\n", 1))
        check("MUTATION H7: a status function on build-host-publication's `if:` is caught",
              rc == 1 and "status function" in out, out)
        host_tol = hosted.replace(
            "  host-publication:\n    if: >\n      github.event_name == 'push' ||\n      github.event_name == 'schedule'\n",
            "  host-publication:\n    if: >\n      !cancelled() && !failure() && (github.event_name == 'push' ||\n"
            "      github.event_name == 'schedule')\n", 1).replace(
            "  publish-modules:\n    if: >\n      github.event_name == 'push' ||\n      github.event_name == 'schedule'\n",
            "  publish-modules:\n    if: >\n      !cancelled() && !failure() && (github.event_name == 'push' ||\n"
            "      github.event_name == 'schedule')\n", 1)
        check("the skip-tolerant host fixture carries the prefix on BOTH calls (a control on the fixture)",
              host_tol.count("!cancelled() && !failure() && (") == 2, host_tol)
        rc, out = callers(host_tol)
        check("H7: build-host-publication and publisher both `!cancelled() && !failure() && (<condition>)` pass",
              rc == 0, out)
        rc, out = callers(host_tol.replace(
            "  host-publication:\n    if: >\n      !cancelled() && !failure() && (github.event_name == 'push' ||\n"
            "      github.event_name == 'schedule')\n",
            "  host-publication:\n    if: >\n      !cancelled() && !failure() && (github.event_name == 'push')\n", 1))
        check("MUTATION H7: a skip-tolerant build-host-publication with a mismatched remainder is caught",
              rc == 1 and "host-publication call exactly" in out, out)
        rc, out = callers(host_tol.replace(
            "  host-publication:\n    if: >\n      !cancelled() && !failure() && (",
            "  host-publication:\n    if: >\n      !cancelled() && (", 1))
        check("MUTATION H7: a bare `!cancelled()` on build-host-publication is caught",
              rc == 1 and "`host-publication`'s `if:`" in out and "status function" in out, out)
        rc, out = callers(hosted.replace("${{ needs.validate.outputs.executor || 'actions' }}", "actions"))
        check("MUTATION H7: a build-host-publication for a pack call that always runs on Actions is caught",
              rc == 1 and "always runs on" in out, out)
        rc, out = callers(hosted.replace("    needs: [modules, validate]\n    uses: Systemorph/MeshWeaver/.github/workflows/build-host-publication.yml",
                                         "    needs: [validate]\n    uses: Systemorph/MeshWeaver/.github/workflows/build-host-publication.yml", 1))
        check("MUTATION H7: a build-host-publication that does not need its pack call is caught",
              rc == 1 and "not in its own `needs:`" in out, out)
        # Review of #7271: an identical executor EXPRESSION resolves differently when a call does not
        # directly need the job it reads (`needs.X` is EMPTY there) — the pack on host, this on actions.
        rc, out = callers(hosted.replace("    needs: [modules, validate]\n    uses: Systemorph/MeshWeaver/.github/workflows/build-host-publication.yml",
                                         "    needs: [modules]\n    uses: Systemorph/MeshWeaver/.github/workflows/build-host-publication.yml", 1))
        check("MUTATION H7: a build-host-publication whose executor reads a job it does not directly need is caught",
              rc == 1 and "`host-publication`'s `executor` reads needs.validate" in out, out)
        rc, out = callers(hosted.replace("needs.validate.outputs.executor", "needs.admission.outputs.executor"))
        check("MUTATION H7: a pack call whose executor reads a job it does not directly need is caught",
              rc == 1 and "`modules`'s `executor` reads needs.admission" in out, out)
        # Review of #7271 (2nd round): the CONDITION too — an identical `publish:`/`if:` spelling reads
        # false in a call that does not directly need the job it reads, skipping host-publication and
        # its publisher while the pack call staged.
        cond = "${{ needs.validate.outputs.publish == 'true' }}"
        on_cond = (literal_host
                   .replace("      publish: >-\n        ${{ github.event_name == 'push'\n"
                            "        || github.event_name == 'schedule' }}\n", f"      publish: {cond}\n", 1)
                   .replace("    if: >\n      github.event_name == 'push' ||\n      github.event_name == 'schedule'\n",
                            f"    if: {cond}\n"))
        check("the condition fixture really carries a needs-reading condition on all three calls",
              on_cond.count(cond) == 3, on_cond)
        rc, out = callers(on_cond)
        check("H7: a needs-reading condition every call directly needs passes", rc == 0, out)
        rc, out = callers(on_cond.replace("    needs: [modules, validate]\n", "    needs: [modules]\n", 1))
        check("MUTATION H7: a build-host-publication whose `if:` reads a job it does not directly need is caught",
              rc == 1 and "`host-publication`'s `if` reads needs.validate" in out, out)
        rc, out = callers(on_cond.replace("    needs: [validate, modules, gate, host-publication]\n",
                                          "    needs: [modules, gate, host-publication]\n", 1))
        check("MUTATION H7: a publisher whose `if:` reads a job it does not directly need is caught",
              rc == 1 and "`publish-modules`'s `if` reads needs.validate" in out, out)
        rc, out = callers(on_cond.replace("  modules:\n    needs: [validate]\n", "  modules:\n    needs: [gate0]\n", 1)
                          .replace("  gate:\n", "  gate0:\n    runs-on: ubuntu-latest\n    steps: [{ run: echo }]\n  gate:\n", 1)
                          .replace("required-jobs: validate modules gate host-publication",
                                   "required-jobs: validate modules gate host-publication gate0", 1)
                          .replace("    needs: [validate, modules, gate, host-publication]\n",
                                   "    needs: [validate, modules, gate, host-publication, gate0]\n", 1))
        check("MUTATION H7: a host-capable pack call whose `publish:` reads a job it does not directly need is caught",
              rc == 1 and "`modules`'s `publish` reads needs.validate" in out, out)

        # Review of #7271 (3rd round): BRACKET access names a job too — `needs['x']` / `needs["x"]`.
        for expr, want in (("needs.a.outputs.x", {"a"}), ("needs['a'].outputs.x", {"a"}),
                           ('needs["a-b"].result', {"a-b"}), ("needs[ 'a' ].outputs.x || needs.b.result", {"a", "b"}),
                           ("toJSON(needs)", set()), ("inputs.needs", set())):
            check(f"needs references of {expr!r} == {sorted(want)}", _needs_refs(expr) == want, str(_needs_refs(expr)))
        for quote in ("'", '"'):
            bracket = f"needs[{quote}validate{quote}].outputs.executor"
            br = hosted.replace("needs.validate.outputs.executor", bracket)
            check(f"the bracket fixture ({quote}) really carries the bracket form on both calls",
                  br.count(bracket) == 2, br)
            rc, out = callers(br)
            check(f"H7: a bracket ({quote}) executor every call directly needs passes", rc == 0, out)
            rc, out = callers(br.replace("    needs: [modules, validate]\n", "    needs: [modules]\n", 1))
            check(f"MUTATION H7: a bracket ({quote}) executor on build-host-publication reading a job it does not "
                  "directly need is caught", rc == 1 and "`host-publication`'s `executor` reads needs.validate" in out, out)
            rc, out = callers(br.replace("  modules:\n    needs: [validate]\n", "  modules:\n    needs: [gate0]\n", 1)
                              .replace("  gate:\n", "  gate0:\n    runs-on: ubuntu-latest\n    steps: [{ run: echo }]\n  gate:\n", 1)
                              .replace("required-jobs: validate modules gate host-publication",
                                       "required-jobs: validate modules gate host-publication gate0", 1)
                              .replace("    needs: [validate, modules, gate, host-publication]\n",
                                       "    needs: [validate, modules, gate, host-publication, gate0]\n", 1))
            check(f"MUTATION H7: a bracket ({quote}) executor on the pack call reading a job it does not directly "
                  "need is caught", rc == 1 and "`modules`'s `executor` reads needs.validate" in out, out)
            bcond = f"${{{{ needs[{quote}validate{quote}].outputs.publish == 'true' }}}}"
            on_bcond = on_cond.replace(cond, bcond)
            check(f"the bracket ({quote}) condition fixture carries it on all three calls", on_bcond.count(bcond) == 3, on_bcond)
            rc, out = callers(on_bcond)
            check(f"H7: a bracket ({quote}) condition every call directly needs passes", rc == 0, out)
            rc, out = callers(on_bcond.replace("    needs: [modules, validate]\n", "    needs: [modules]\n", 1))
            check(f"MUTATION H7: a bracket ({quote}) `if:` on build-host-publication reading a job it does not "
                  "directly need is caught", rc == 1 and "`host-publication`'s `if` reads needs.validate" in out, out)
            rc, out = callers(on_bcond.replace("    needs: [validate, modules, gate, host-publication]\n",
                                               "    needs: [modules, gate, host-publication]\n", 1))
            check(f"MUTATION H7: a bracket ({quote}) `if:` on the publisher reading a job it does not directly need "
                  "is caught", rc == 1 and "`publish-modules`'s `if` reads needs.validate" in out, out)
        rc, out = callers(hosted.replace("      modules: ${{ needs.host-publication.outputs.declared }}",
                                         "      modules: ${{ needs.modules.outputs.declared }}", 1))
        check("MUTATION H7: a publisher mixing the host call's lane with the pack call's catalog is caught",
              rc == 1 and "SAME call" in out, out)
        rc, out = callers(hosted.replace("    needs: [validate, modules, gate, host-publication]\n",
                                         "    needs: [validate, modules, gate]\n", 1)
                          .replace("required-jobs: validate modules gate host-publication",
                                   "required-jobs: validate modules gate", 1))
        check("MUTATION H7: a publisher that does not wait for the build-host-publication call is caught",
              rc == 1 and "host-publication" in out, out)
        host_block = "  host-publication:" + hosted.split("  host-publication:", 1)[1].split("  publish-modules:", 1)[0]
        two = hosted.replace("  publish-modules:",
                             host_block.replace("  host-publication:", "  host-publication-2:", 1) + "  publish-modules:", 1)
        rc, out = callers(two)
        check("MUTATION H7: two build-host-publication calls for one pack call are caught",
              rc == 1 and "found 2" in out, out)
        orphan = wired + ("  stray-host:\n    needs: [validate]\n"
                          "    uses: Systemorph/MeshWeaver/.github/workflows/build-host-publication.yml@main\n"
                          "    with:\n      api-url: https://ops.example.com/api/builds\n"
                          "      lane: ${{ needs.validate.outputs.lane }}\n"
                          "      artifact-store: file:/ci-artifacts\n      artifact-store-id: abc\n")
        rc, out = callers(orphan)
        check("MUTATION H7: a build-host-publication call outside any pack call's host graph is caught",
              rc == 1 and "stray-host" in out, out)
        rc, out = callers(direct.replace("publish-mode: direct", "publish-mode: direct\n      executor: host"))
        check("MUTATION H7: a literal `executor: host` that publishes IN-LEG is caught (a host holds no "
              "publication credential)", rc == 1 and "no publication credential" in out, out)
        rc, out = callers(direct.replace("publish-mode: direct", "publish-mode: direct\n      executor: host")
                          .replace("${{ github.event_name == 'push'\n        || github.event_name == 'schedule' }}", "false"))
        check("…but a NON-publishing `executor: host` call with direct mode passes", rc == 0, out)
        rc, out = callers(direct.replace("publish-mode: direct",
                                         "publish-mode: direct\n      executor: ${{ needs.validate.outputs.executor }}"))
        check("an executor EXPRESSION on an in-leg call passes statically (the lane refuses a publishing "
              "host run at run time)", rc == 0 and "IN-LEG" in out, out)

    print("== publish")
    with tempfile.TemporaryDirectory() as td:
        root = Path(td)

        def bundle(path: Path, module: str, identity: str = "mvid:aaaa"):
            path.parent.mkdir(parents=True, exist_ok=True)
            with zipfile.ZipFile(path, "w") as zf:
                zf.writestr("meshweaver/manifest.json", json.dumps(
                    {"frameworkMvid": identity, "module": {"assemblyName": module}}))

        def stage(dirname: str, module: str, lane: str = "L1", owed: bool = True,
                  identity: str = "mvid:aaaa", sha: str | None = None, with_bytes: bool = True,
                  version: str = "1.2.3"):
            d = root / dirname
            d.mkdir(parents=True, exist_ok=True)
            name = f"{module}.module.nupkg"
            if with_bytes:
                bundle(d / name, module, identity)
            rec = {"lane": lane, "package": module.split(".")[-1], "module": module,
                   "version": version, "frameworkIdentity": identity, "owed": owed,
                   "bundle": {"name": name,
                              "sha256": sha or (sha256_file(d / name) if with_bytes else "0" * 64)},
                   "ledger": {"key": ""}}
            if not owed:
                rec["reason"] = "the build ledger already serves this key"
            (d / f"{module}.publication.json").write_text(json.dumps(rec), encoding="utf-8")
            return d

        def args(**kw):
            base = dict(staged=str(root / "staged"), lane="L1",
                        declared=json.dumps([{"module": "MeshWeaver.AI"}, {"module": "MeshWeaver.Mcp"}]),
                        selected="MeshWeaver.AI", registry="https://registry.invalid",
                        source="Plugins", ledger="off", ledger_script="", dry_run=True, check_only=False)
            base.update(kw)
            return argparse.Namespace(**base)

        stage("staged", "MeshWeaver.AI")
        rc, out = run(publish, args())
        check("a whole, lane-stamped staged set publishes", rc == 0 and "MeshWeaver.AI@1.2.3" in out, out)

        rc, out = run(publish, args(selected="MeshWeaver.AI MeshWeaver.Mcp"))
        check("a SELECTED module with no staged record refuses", rc == 1 and "MeshWeaver.Mcp" in out, out)

        stage("foreign", "MeshWeaver.Mcp", lane="L2")
        rc, out = run(publish, args(staged=str(root / "foreign"), selected="MeshWeaver.Mcp"))
        check("a FOREIGN-lane record refuses", rc == 1 and "another call" in out, out)

        stage("swapped", "MeshWeaver.AI", sha="f" * 64)
        rc, out = run(publish, args(staged=str(root / "swapped")))
        check("SUBSTITUTED bytes refuse", rc == 1 and "not the validated bytes" in out.lower()
              or rc == 1 and "NOT the validated bytes" in out, out)

        stage("gone", "MeshWeaver.AI", with_bytes=False)
        rc, out = run(publish, args(staged=str(root / "gone")))
        check("a record whose bytes are MISSING refuses", rc == 1 and "NOT in" in out, out)

        d = stage("noidentity", "MeshWeaver.AI", identity="")
        bundle(d / "MeshWeaver.AI.module.nupkg", "MeshWeaver.AI", identity="")
        rec = json.loads((d / "MeshWeaver.AI.publication.json").read_text())
        rec["frameworkIdentity"] = "mvid:aaaa"
        rec["bundle"]["sha256"] = sha256_file(d / "MeshWeaver.AI.module.nupkg")
        (d / "MeshWeaver.AI.publication.json").write_text(json.dumps(rec), encoding="utf-8")
        rc, out = run(publish, args(staged=str(d)))
        check("bytes stating NO framework identity refuse", rc == 1 and "framework identity" in out, out)

        stage("notowed", "MeshWeaver.AI", owed=False)
        rc, out = run(publish, args(staged=str(root / "notowed")))
        check("a record that owes no hand-over is accounted for, not published",
              rc == 0 and "no hand-over owed" in out, out)

        rc, out = run(publish, args(lane=""))
        check("an EMPTY lane refuses", rc == 1, out)
        rc, out = run(publish, args(staged=str(root / "never-staged")))
        check("a missing staged directory refuses when modules WERE selected", rc == 1, out)
        rc, out = run(publish, args(staged=str(root / "never-staged"), selected=""))
        check("…but a call whose selection was EMPTY says so and publishes nothing",
              rc == 0 and "ZERO" in out, out)

        stage("undeclared", "MeshWeaver.Other")
        rc, out = run(publish, args(staged=str(root / "undeclared"), selected="MeshWeaver.Other"))
        check("a record outside the declared matrix refuses", rc == 1 and "declared matrix" in out, out)

        # H7: --check-only is the publisher's own check, run by build-host-publication.yml over a
        # host run's fetched records. It must refuse what the publisher refuses and POST nothing.
        os.environ.pop(PUBLISH_TOKEN_ENV, None)
        rc, out = run(publish, args(check_only=True, dry_run=False, registry="check-only", source="check-only"))
        check("H7 check-only: a whole, lane-stamped host set passes with NO token and POSTs nothing",
              rc == 0 and "check-only" in out and "->" not in out, out)
        rc, out = run(publish, args(check_only=True, dry_run=False, selected="MeshWeaver.AI MeshWeaver.Mcp"))
        check("H7 check-only: a SELECTED module the host staged no record for refuses",
              rc == 1 and "MeshWeaver.Mcp" in out, out)
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(root / "foreign"),
                                    selected="MeshWeaver.Mcp"))
        check("H7 check-only: a host record stamped with another lane refuses", rc == 1 and "another call" in out, out)
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(root / "swapped")))
        check("H7 check-only: substituted host bytes refuse", rc == 1 and "NOT the validated bytes" in out, out)

        # Review of #7271: two pack calls sharing one host stage — each host-publication call fetches
        # BOTH calls' records (receipts are keyed by repo/sha/stage, not lane). `partition` sets the
        # sibling's aside by name; the completeness check still runs against THIS call's selection.
        stage("mixed", "MeshWeaver.AI", lane="L1")
        stage("mixed", "MeshWeaver.Mcp", lane="L2")
        mixed = root / "mixed"
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(mixed)))
        check("H7 partition (control): WITHOUT it a sibling lane's record refuses the whole host set",
              rc == 1 and "another call" in out, out)
        rc, out = run(partition, mixed, "L1", root / "mixed-aside")
        check("H7 partition: the sibling lane's record and its bundle are set aside BY NAME",
              rc == 0 and "MeshWeaver.Mcp" in out
              and (root / "mixed-aside" / "MeshWeaver.Mcp.publication.json").is_file()
              and (root / "mixed-aside" / "MeshWeaver.Mcp.module.nupkg").is_file()
              and not (mixed / "MeshWeaver.Mcp.module.nupkg").exists(), out)
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(mixed)))
        check("H7 partition: …and this call's own set then passes the publisher's check", rc == 0, out)
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(mixed),
                                    selected="MeshWeaver.AI MeshWeaver.Mcp"))
        check("H7 partition: completeness still holds — a SELECTED module only the sibling staged refuses",
              rc == 1 and "MeshWeaver.Mcp" in out, out)
        d = stage("nolane", "MeshWeaver.AI", lane="")
        rc, out = run(partition, d, "L1", root / "nolane-aside")
        check("H7 partition: a record with NO lane stays (the check refuses it; partition never hides it)",
              rc == 0 and (d / "MeshWeaver.AI.publication.json").is_file(), out)
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(d)))
        check("H7 partition: …and the check then refuses it", rc == 1, out)
        stage("wronglane", "MeshWeaver.AI", lane="L1")
        rc, out = run(partition, root / "wronglane", "L9", root / "wronglane-aside")
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(root / "wronglane"), lane="L9"))
        check("H7 partition NEGATIVE CONTROL: partitioning under the WRONG lane leaves the selection unanswered — RED",
              rc == 1 and "MeshWeaver.AI" in out, out)
        # Review of #7271 (2nd round): host-produced record text must never print as a runner command.
        evil = "MeshWeaver.Evil\n::add-mask::secret\n  ##[group]x"

        def commands_in(text: str) -> list[str]:
            return [l for l in text.splitlines() if l.lstrip().startswith("::add-mask") or "##[" in l]

        d = stage("evil", "MeshWeaver.AI", lane="L1")
        rec = {"lane": "L2\n::stop-commands::tok", "module": evil, "bundle": {"name": "x.nupkg"}}
        (d / "evil.publication.json").write_text(json.dumps(rec), encoding="utf-8")
        rc, out = run(partition, d, "L1", root / "evil-aside")
        check("H7 partition: a foreign record's module/lane text prints as DATA, never a runner command",
              rc == 0 and "set aside" in out and not commands_in(out)
              and "::stop-commands" not in out, out)
        d = stage("evil2", "MeshWeaver.AI", lane="L1")
        rec = json.loads((d / "MeshWeaver.AI.publication.json").read_text())
        rec["reason"], rec["owed"] = evil, False
        (d / "MeshWeaver.AI.publication.json").write_text(json.dumps(rec), encoding="utf-8")
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(d)))
        check("H7 check-only: a record's reason prints as DATA", rc == 0 and not commands_in(out), out)
        d = stage("evil3", "MeshWeaver.AI", lane="L1")
        (d / "z.publication.json").write_text(json.dumps({"lane": "L1", "module": evil}), encoding="utf-8")
        rc, out = run(publish, args(check_only=True, dry_run=False, staged=str(d)))
        check("H7 check-only: a REFUSAL naming a hostile module prints as one escaped ::error line",
              rc == 1 and not commands_in(out) and "%0A" in out, out)
        check("NEGATIVE CONTROL: the hostile text WOULD be a command if printed raw",
              bool(commands_in(evil)))

        # NEWEST WINS under staged — `superseded` asks the scope per OWED record; publish stands those down.
        stage("newer", "MeshWeaver.AI")
        stage("newer", "MeshWeaver.Mcp")
        stage("newer", "MeshWeaver.Maps", owed=False)
        newer_dir = root / "newer"
        decl3 = json.dumps([{"module": "MeshWeaver.AI"}, {"module": "MeshWeaver.Mcp"}, {"module": "MeshWeaver.Maps"}])
        scope = root / "fake-scope.py"
        asked = root / "scope-asked.txt"
        # A stand-in for node-repo-scope.py that answers "reached" for MeshWeaver.AI only and logs what it
        # was asked, so the cases read the REAL argv this command builds.
        scope.write_text(
            "import json,sys\n"
            f"open({str(asked)!r},'a').write(' '.join(sys.argv[1:])+'\\n')\n"
            "m=json.load(open(sys.argv[sys.argv.index('--modules')+1][1:]))[0]['module']\n"
            "if m=='MeshWeaver.Boom': sys.exit(3)\n"
            "print(json.dumps({'count': 1 if m=='MeshWeaver.AI' else 0, 'reason': 'src/MeshWeaver.AI/X.cs'}))\n",
            encoding="utf-8")
        mine, tip = "a" * 40, "b" * 40

        def sup(**kw):
            base = dict(staged=str(newer_dir), lane="L1", declared=decl3, root=str(root), mine=mine, tip=tip,
                        changed_list="src/MeshWeaver.AI/X.cs", scope_script=str(scope),
                        module_libraries_project="src/Shelf/Shelf.csproj", build_extensions_project="",
                        msbuild_evaluation=["head.json", "tip.json"], out=str(root / "superseded.json"))
            base.update(kw)
            return run(superseded, argparse.Namespace(**base))

        asked.write_text("", encoding="utf-8")
        rc, out = sup()
        doc = json.loads((root / "superseded.json").read_text()) if rc == 0 else {}
        check("superseded: a module the newer tip reaches stands down, a module it does not reach publishes",
              rc == 0 and set(doc.get("superseded", {})) == {"MeshWeaver.AI"}, out)
        lines = asked.read_text().splitlines()
        check("superseded: only OWED records are asked (a not-owed record is never a candidate)",
              len(lines) == 2 and not any("MeshWeaver.Maps" in l for l in lines), "\n".join(lines))
        check("superseded: the scope is asked as a PUBLICATION reach, with the shelf and BOTH evaluations",
              all("--publication-reach" in l and "--module-libraries-project src/Shelf/Shelf.csproj" in l
                  and "--msbuild-evaluation head.json --msbuild-evaluation tip.json" in l for l in lines),
              "\n".join(lines))
        rc, out = run(publish, args(staged=str(newer_dir), declared=decl3,
                                    selected="MeshWeaver.AI MeshWeaver.Mcp MeshWeaver.Maps",
                                    superseded=f"@{root / 'superseded.json'}"))
        check("publish --superseded: the stood-down module is NOT handed over, its sibling IS",
              rc == 0 and "STOOD DOWN" in out and "-> MeshWeaver.Mcp@" in out
              and "-> MeshWeaver.AI@" not in out, out)
        rc, out = run(publish, args(staged=str(newer_dir), declared=decl3,
                                    selected="MeshWeaver.AI MeshWeaver.Mcp MeshWeaver.Maps"))
        check("publish NEGATIVE CONTROL: without --superseded both owed modules are handed over",
              rc == 0 and "-> MeshWeaver.AI@" in out and "-> MeshWeaver.Mcp@" in out, out)
        (root / "foreign-sup.json").write_text(json.dumps({"lane": "L1", "superseded": {"MeshWeaver.Maps": "x"}}))
        rc, out = run(publish, args(staged=str(newer_dir), declared=decl3,
                                    selected="MeshWeaver.AI MeshWeaver.Mcp MeshWeaver.Maps",
                                    superseded=f"@{root / 'foreign-sup.json'}"))
        check("publish --superseded naming a module this call does not hand over REFUSES (different evidence)",
              rc == 1 and "different staged evidence" in out, out)
        (root / "lane-sup.json").write_text(json.dumps({"lane": "L2", "superseded": {}}))
        rc, out = run(publish, args(staged=str(newer_dir), declared=decl3,
                                    selected="MeshWeaver.AI MeshWeaver.Mcp MeshWeaver.Maps",
                                    superseded=f"@{root / 'lane-sup.json'}"))
        check("publish --superseded for ANOTHER lane refuses", rc == 1 and "lane" in out, out)
        asked.write_text("", encoding="utf-8")
        rc, out = sup(tip=mine)
        doc = json.loads((root / "superseded.json").read_text()) if rc == 0 else {}
        check("superseded: this run IS the tip — nothing stands down and the scope is never asked",
              rc == 0 and doc.get("superseded") == {} and asked.read_text() == "", out)
        rc, out = sup(changed_list="")
        check("superseded: the tip's tree equals this run's — nothing stands down",
              rc == 0 and json.loads((root / "superseded.json").read_text()).get("superseded") == {}, out)
        rc, out = sup(tip="main")
        check("superseded: a tip that is not a full sha is RED ('cannot tell' never publishes)", rc == 1, out)
        rc, out = sup(scope_script=str(root / "absent.py"))
        check("superseded: an absent scope script is RED", rc == 1, out)
        stage("boom", "MeshWeaver.Boom")
        rc, out = sup(staged=str(root / "boom"), declared=json.dumps([{"module": "MeshWeaver.Boom"}]))
        check("superseded: a scope script that FAILS is RED, never 'nothing newer'",
              rc == 1 and "MeshWeaver.Boom" in out, out)

        rc, out = run(partition, root / "never-fetched", "L1", root / "x-aside")
        check("H7 partition: a directory that was never fetched is nothing to set aside", rc == 0, out)
        rc, out = run(partition, mixed, "", root / "x-aside")
        check("H7 partition: an EMPTY lane refuses", rc == 1, out)

    print("== lane-key (the pack lane's `This call's lane key` step, in Python)")
    # Vectors computed with the step's own commands (tr -c … | cut -c1-24; sha256sum | cut -c1-12).
    for lane_id, modules, want in (
            ("catalog", '[{"module": "MeshWeaver.AI"}]', "catalog-481e1ec92571"),
            ("", "", "lane-e3b0c44298fc"),
            ("mod ule/über:lane-with-a-very-long-label", "", "mod-ule---ber-lane-with--e3b0c44298fc")):
        got = lane_key(lane_id, modules)
        check(f"lane-key({lane_id!r}) == {want}", got == want, got)
    check("lane-key: a different matrix is a different lane (the fingerprint discriminates)",
          lane_key("catalog", '[{"module": "A"}]') != lane_key("catalog", '[{"module": "B"}]'))

    print()
    if failures:
        print(f"::error::{len(failures)} self-test case(s) FAILED: " + "; ".join(failures))
        return 1
    print("module-publication.py self-test: all cases pass")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("command", nargs="?",
                   choices=("verdict", "check-caller", "check-callers", "publish", "lane-key", "partition",
                            "superseded"))
    p.add_argument("--root", default="")
    p.add_argument("--verdicts", default="")
    p.add_argument("--required", default="")
    p.add_argument("--unrelated", default="")
    p.add_argument("--workflow", default="")
    p.add_argument("--uses", default="node-repo-module-publish.yml")
    p.add_argument("--staged", default="")
    p.add_argument("--lane", default="")
    p.add_argument("--declared", default="")
    p.add_argument("--selected", default="")
    p.add_argument("--registry", default="")
    p.add_argument("--source", default="")
    p.add_argument("--ledger", default="off")
    p.add_argument("--ledger-script", dest="ledger_script", default="")
    p.add_argument("--dry-run", dest="dry_run", action="store_true")
    p.add_argument("--check-only", dest="check_only", action="store_true",
                   help="publish: run every check the hand-over runs, then stop before any POST")
    p.add_argument("--lane-id", dest="lane_id", default="")
    p.add_argument("--aside", default="", help="partition: where another lane's records are set aside")
    p.add_argument("--modules", default="", help="lane-key: the pack call's `modules` input, verbatim (or @file)")
    p.add_argument("--superseded", default="",
                   help="publish: a `superseded` result (@file) — those modules stand down for a newer trunk commit")
    p.add_argument("--mine", default="", help="superseded: this run's commit")
    p.add_argument("--tip", default="", help="superseded: the trunk tip")
    p.add_argument("--changed-list", dest="changed_list", default="",
                   help="superseded: the files the tip changed against --mine, comma-separated")
    p.add_argument("--scope-script", dest="scope_script", default="", help="superseded: node-repo-scope.py")
    p.add_argument("--module-libraries-project", dest="module_libraries_project", default="")
    p.add_argument("--build-extensions-project", dest="build_extensions_project", default="")
    p.add_argument("--msbuild-evaluation", dest="msbuild_evaluation", action="append", default=[])
    p.add_argument("--out", default="", help="superseded: where the decision is written")
    p.add_argument("--self-test", dest="self_test", action="store_true")
    a = p.parse_args()
    if a.self_test:
        return self_test()
    if not a.command:
        p.error("a command or --self-test is required")
    if a.command == "verdict":
        return verdict(a.verdicts, a.required)
    if a.command == "check-caller":
        if not a.workflow:
            p.error("check-caller needs --workflow")
        return check_caller(Path(a.workflow), a.uses, a.required, a.unrelated)
    if a.command == "check-callers":
        if not a.root:
            p.error("check-callers needs --root")
        return check_callers(Path(a.root))
    if a.command == "partition":
        if not a.staged or not a.aside:
            p.error("partition needs --staged and --aside")
        return partition(Path(a.staged), a.lane, Path(a.aside))
    if a.command == "lane-key":
        if not a.modules:
            p.error("lane-key needs --modules (the pack call's `modules` input, verbatim)")
        raw = Path(a.modules[1:]).read_text(encoding="utf-8") if a.modules.startswith("@") else a.modules
        print(lane_key(a.lane_id, raw))
        return 0
    if a.command == "superseded":
        if not a.staged:
            p.error("superseded needs --staged")
        return superseded(a)
    if a.command == "publish":
        if a.check_only and a.superseded:
            p.error("publish --check-only takes no --superseded: it POSTs nothing, so there is nothing to stand down")
        if a.check_only:
            a.registry = a.registry or "check-only"
            a.source = a.source or "check-only"
        for needed in ("staged", "registry", "source"):
            if not getattr(a, needed):
                p.error(f"publish needs --{needed}")
        return publish(a)
    return 2


if __name__ == "__main__":
    sys.exit(main())
