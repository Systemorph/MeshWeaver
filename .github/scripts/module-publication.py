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
                closes), must hand this lane the WHOLE needs context rather than a curated
                literal, and must account for every other job in the workflow — covered, or
                declared unrelated with a reason. This is the half `verdict` cannot see: a
                dependency dropped from `needs:` AND from `required-jobs` is invisible at runtime.
  check-callers The same caller-graph check, run BEFORE MERGE over every workflow in a repository
                (node-repo-validate.yml runs it on every satellite pull request), plus the wiring
                one call cannot see: every `publish-mode: staged` pack call has exactly one
                publisher reading its `lane`/`selected`/`declared` outputs, and that publisher runs
                on exactly the events the pack call stages on. A staged pack call nobody publishes
                would hand the registry NOTHING, silently — the FrameworkDeclined outage of #2088.
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
    if not isinstance(expr, str):
        return None
    for fn in STATUS_FUNCS:
        if re.search(rf"\b{fn}\s*\(", expr):
            return fn
    return None


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
        fn = _status_function_in(job.get("if"))
        if fn:
            problems.append(
                f"`{pub}`'s `if:` uses the status function `{fn}()`, which makes the job run even "
                "when a dependency FAILED, was CANCELLED or was SKIPPED — exactly the door this "
                "lane exists to close. A publishing job runs under GitHub's default rule (every "
                "`needs:` succeeded) or not at all; its `if:` may gate the EVENT and nothing else "
                f"(got: {job.get('if')!r}).")
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
#: The only values the pack lane accepts (its `select` job refuses anything else at run time).
PUBLISH_MODES = ("direct", "staged")


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
    n_pack = n_pub = 0
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
        n_pack += len(packs)
        n_pub += len(pubs)

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
            if src not in packs:
                problems.append(f"{wf.name}: `{pid}` reads its lane from `{src}`, which is not a "
                                f"{PACK_LANE} call in this workflow.")
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
            pw = packs[src].get("with") or {}
            mode = _publish_mode(pw)
            if mode in PUBLISH_MODES and mode != "staged":
                problems.append(f"{wf.name}: `{pid}` publishes `{src}`, whose publish-mode is "
                                f"`{mode}`, not `staged` — that call POSTs in-leg itself and stages "
                                "nothing, so this lane would refuse on an empty staged set.")
            stage_on, publish_on = _norm_condition(pw.get("publish", False)), _norm_condition(pj.get("if"))
            if stage_on != publish_on:
                problems.append(
                    f"{wf.name}: `{src}` stages on `{stage_on}` but `{pid}` runs on `{publish_on}`. A "
                    "run that stages and does not publish hands the registry NOTHING while every tick "
                    "is green (every portal then reads FrameworkDeclined, #2088); a run that publishes "
                    "without staging refuses. Give the publisher exactly the pack call's condition.")

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
            wired = sorted(pid for pid, pj in pubs.items()
                           if _needs_output((pj.get("with") or {}).get("lane"), "lane") == kid)
            if mode == "staged" and not wired:
                problems.append(
                    f"{wf.name}: `{kid}` stages its bundles (publish-mode: staged) and NO "
                    f"{PUBLISH_LANE} job reads its lane — the staged bytes reach nobody, and this "
                    "repository silently stops publishing modules.")
            elif mode == "staged" and len(wired) > 1:
                problems.append(f"{wf.name}: `{kid}` is published by {len(wired)} jobs ({', '.join(wired)}) "
                                "— the same bytes would be POSTed twice from one validated call.")
            elif mode != "staged" and _norm_condition(kw.get("publish", False)) != "false":
                print(f"  NOTE: {wf.name}#{kid} publishes IN-LEG (publish-mode: {mode}): each module "
                      "reaches the registry right after its own suite, before this run's other gates "
                      f"report (MeshWeaver#3878). Not refused here; adopt publish-mode: staged and a "
                      f"{PUBLISH_LANE} job.")

    print(f"module-publication check-callers: {n_pack} {PACK_LANE} call(s) and {n_pub} {PUBLISH_LANE} "
          f"call(s) across {len(files)} workflow file(s) under {wf_dir}")
    if not n_pack and not n_pub:
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
            print(f"  -- {m}@{rec['version']}: no hand-over owed — "
                  f"{rec.get('reason') or 'no reason recorded'}")
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

    if problems:
        for p in problems:
            print(f"::error title=Publication refused::{p}", file=sys.stderr)
        summarise(["### Module publication REFUSED", "",
                   "The registry was left untouched — not one byte was handed over.", ""]
                  + [f"- {p}" for p in problems])
        return 1

    if not plan:
        print("nothing owes a hand-over in this call: "
              f"{len(mine)} staged record(s), {len(selected)} selected, 0 to publish.")
        summarise(["### Module publication", "",
                   f"{len(mine)} staged record(s) accounted for; **none owed a hand-over** "
                   "(the build ledger already serves these keys, or the selection was empty)."])
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
        print(f"  -> {item['module']}@{item['version']} ({item['sha256'][:12]}…, built against "
              f"{item['identity']}) to {url}")
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
    if failed:
        lines += ["", "**Refused by the registry:**", ""] + [f"- {f}" for f in failed]
    summarise(lines)
    for f in failed:
        print(f"::error title=Publication failed::{f}", file=sys.stderr)
    print(f"published {len(published)} of {len(plan)} module bundle(s)")
    return 1 if failed else 0


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
        rc, out = callers(wired.replace("publish-mode: staged", "publish-mode: direct"))
        check("a publisher wired to a pack call that still POSTs in-leg is caught",
              rc == 1 and "not `staged`" in out, out)
        rc, out = callers(wired.replace("${{ needs.modules.outputs.declared }}", "'[]'"))
        check("a publisher whose `modules` is not the pack call's own `declared` output is caught",
              rc == 1 and "declared" in out, out)
        rc, out = callers(wired.replace("${{ needs.modules.outputs.lane }}", "catalog"))
        check("a publisher whose `lane` is a literal rather than the call's lane output is caught",
              rc == 1 and "lane output" in out, out)
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
                        source="Plugins", ledger="off", ledger_script="", dry_run=True)
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

    print()
    if failures:
        print(f"::error::{len(failures)} self-test case(s) FAILED: " + "; ".join(failures))
        return 1
    print("module-publication.py self-test: all cases pass")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("command", nargs="?", choices=("verdict", "check-caller", "check-callers", "publish"))
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
    if a.command == "publish":
        for needed in ("staged", "registry", "source"):
            if not getattr(a, needed):
                p.error(f"publish needs --{needed}")
        return publish(a)
    return 2


if __name__ == "__main__":
    sys.exit(main())
