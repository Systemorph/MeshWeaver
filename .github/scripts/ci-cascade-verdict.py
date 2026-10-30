#!/usr/bin/env python3
"""ci-cascade-verdict.py — an aggregator reports an upstream red ONCE, by naming its root.

Policy `report-a-cascade-once` (Doc/Architecture/ReadingCiSignals → "A cascade is reported once").

An AGGREGATOR is a job that judges other jobs' outcomes — `Every gate executed`, the Tests-area
ratchet, a selection-and-receipts check, a lane's `verify`. When an upstream ROOT job failed, the
aggregator cannot pass, and every check it would run next only restates that red in its own words
(`NO receipt`, `gate skipped`, `no gate log`, `scope supplied no valid count`). Measured on
MeshWeaver.Plugins PR runs, 2026-10-30: 9 of 25 runs red, and one root fanned out into 5–10
independent-looking aggregator reds — the one line that mattered was the hardest one to find.

So the aggregator's FIRST step runs this over `toJSON(needs)`:

* a need that ended `failure` or `cancelled` is the cause. Its root is the need's own
  `cascade-root` output when that need is itself an aggregator that cascaded (so the line names the
  job that really failed, two levels up), otherwise the need itself, by its display name.
* a root carrying a `waiting` / `cascade-waiting` output is WAITING FOR PLATFORM, not broken —
  its own waiting sentence is repeated verbatim, so a wait reads the same in every job.
* then exactly ONE `::error` line — "red because <root> failed — see it" — and exit 1. The job is
  RED (never skipped: a skipped required check counts as satisfied; never green), and the steps
  after this one do not run, so the aggregator paints no independent red of its own.
* no need failed → exit 0 and print nothing red: the aggregator's own analysis runs and decides.
  A need that was merely SKIPPED is not a root here — whether a skip is acceptable is precisely
  what the aggregator's own checks judge (`Every gate executed` exists to refuse one).

What it can NOT be is a way to green: it only ever exits 0 when no need failed, and a needs
document it cannot read is RED (exit 2), never "nothing failed".

Outputs (GITHUB_OUTPUT): cascade=true|false, root=<display names, ' + '-joined>, waiting=<text>.
An aggregator re-exports `root`/`waiting` as its job outputs `cascade-root`/`cascade-waiting`,
which is what lets the NEXT aggregator name the true root rather than this job.

Usage:
  ci-cascade-verdict.py --needs '<toJSON(needs)>' [--names 'id=Display name' ...] [--job NAME]
  ci-cascade-verdict.py --self-test
"""
from __future__ import annotations

import argparse
import io
import json
import os
import sys
from contextlib import redirect_stdout

FAILED = ("failure", "cancelled")


def parse_names(raw: list[str]) -> dict[str, str]:
    names: dict[str, str] = {}
    for chunk in raw:
        for line in chunk.splitlines():
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if "=" not in line:
                raise ValueError(f"--names entry {line!r} is not 'id=Display name'")
            key, _, value = line.partition("=")
            names[key.strip()] = value.strip()
    return names


def roots_of(needs: dict, names: dict[str, str]) -> list[tuple[str, str, str]]:
    """(display name, result, waiting text) for every root behind a failed need, in need order."""
    seen: dict[str, tuple[str, str, str]] = {}
    for job_id, need in needs.items():
        if not isinstance(need, dict):
            raise ValueError(f"need {job_id!r} is not an object")
        result = need.get("result")
        if result not in FAILED:
            continue
        outputs = need.get("outputs") or {}
        root = (outputs.get("cascade-root") or "").strip() or names.get(job_id, job_id)
        waiting = (outputs.get("cascade-waiting") or outputs.get("waiting") or "").strip()
        # A cascaded need names an upstream root; the result we report is that root's, i.e. it
        # failed (or waits). Our own need's `cancelled` is only meaningful for a direct root.
        reported = result if not (outputs.get("cascade-root") or "").strip() else "failure"
        if root not in seen:
            seen[root] = (root, reported, waiting)
        elif waiting and not seen[root][2]:
            seen[root] = (root, seen[root][1], waiting)
    return list(seen.values())


def sentence(roots: list[tuple[str, str, str]]) -> tuple[str, str]:
    """(title, line) — the ONE error this job prints."""
    waiting = [r for r in roots if r[2]]
    if len(roots) == 1 and waiting:
        name, _, text = roots[0]
        return ("Waiting for platform",
                f"red because {name} is WAITING FOR PLATFORM — see it. {text}")
    parts = []
    for name, result, text in roots:
        verb = "is WAITING FOR PLATFORM" if text else ("was cancelled" if result == "cancelled" else "failed")
        parts.append(f"{name} {verb}")
    who = parts[0] if len(parts) == 1 else ", ".join(parts[:-1]) + " and " + parts[-1]
    see = "see it" if len(parts) == 1 else "see them"
    title = f"Red because {roots[0][0]}" + (" (and more)" if len(roots) > 1 else "")
    line = (f"red because {who} — {see}. This job's own checks were not run: behind that red they "
            "would only restate it.")
    if waiting:
        line += " " + waiting[0][2]
    return title, line


def emit(name: str, value: str) -> None:
    path = os.environ.get("GITHUB_OUTPUT")
    if path:
        with open(path, "a", encoding="utf-8") as stream:
            stream.write(f"{name}={value.replace(chr(10), ' ')}\n")


def run(needs_json: str, names: dict[str, str], job: str = "") -> int:
    try:
        needs = json.loads(needs_json or "")
        if not isinstance(needs, dict):
            raise ValueError("toJSON(needs) is not an object")
        roots = roots_of(needs, names)
    except (ValueError, json.JSONDecodeError) as ex:
        print(f"::error title=Cascade verdict unreadable::could not read this job's needs ({ex}) — "
              "refusing to say whether an upstream root failed; this is never read as 'nothing failed'.")
        emit("cascade", "unknown")
        return 2
    if not roots:
        emit("cascade", "false")
        emit("root", "")
        emit("waiting", "")
        failed_none = ", ".join(f"{k}={v.get('result')}" for k, v in needs.items()) or "no needs"
        print(f"no upstream root failed ({failed_none}) — {job or 'this job'} runs its own checks.")
        return 0
    title, line = sentence(roots)
    print(f"::error title={title}::{line}")
    emit("cascade", "true")
    emit("root", " + ".join(r[0] for r in roots))
    emit("waiting", next((r[2] for r in roots if r[2]), ""))
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as stream:
            stream.write(f"### 🔗 {line}\n")
    return 1


# ─────────────────────────────── self-test ───────────────────────────────

def _capture(fn, *args) -> tuple[int, str]:
    buf = io.StringIO()
    saved = os.environ.pop("GITHUB_OUTPUT", None), os.environ.pop("GITHUB_STEP_SUMMARY", None)
    try:
        with redirect_stdout(buf):
            rc = fn(*args)
    finally:
        if saved[0] is not None:
            os.environ["GITHUB_OUTPUT"] = saved[0]
        if saved[1] is not None:
            os.environ["GITHUB_STEP_SUMMARY"] = saved[1]
    return rc, buf.getvalue()


def aggregator(needs: dict, names: dict[str, str], own_check) -> tuple[int, list[str]]:
    """What a workflow aggregator does: the cascade step first, its own check only if that passed."""
    rc, out = _capture(run, json.dumps(needs), names, "the aggregator")
    errors = [l for l in out.splitlines() if l.startswith("::error")]
    if rc != 0:
        return rc, errors
    own_ok, own_msg = own_check()
    if not own_ok:
        return 1, errors + [f"::error::{own_msg}"]
    return 0, errors


def self_test() -> int:
    failures: list[str] = []

    def check(label: str, ok: bool) -> None:
        print(("ok   " if ok else "FAIL ") + label)
        if not ok:
            failures.append(label)

    names = {"platform-ref": "Resolve the released platform", "validate": "Validate node repos",
             "admission": "Admitted by the build queue", "test-repos": "test-repos"}
    green = lambda: (True, "")
    own_red = lambda: (False, "the selection and the receipts disagree: 1 receipt missing")

    # 1. root failed → ONE red line naming the root; the aggregator's own check never runs.
    called = []
    def tracking():
        called.append(True)
        return own_red()
    rc, errs = aggregator({"platform-ref": {"result": "failure", "outputs": {}},
                           "admission": {"result": "skipped", "outputs": {}}}, names, tracking)
    check("a failed root is RED (exit 1)", rc == 1)
    check("...with exactly ONE error line", len(errs) == 1)
    check("...naming the root by its display name", "red because Resolve the released platform failed — see it" in errs[0])
    check("...and the aggregator's own analysis did not run", not called)

    # 2. all roots ok, own check fails → its OWN red, and only that.
    rc, errs = aggregator({"platform-ref": {"result": "success", "outputs": {}},
                           "validate": {"result": "success", "outputs": {}}}, names, own_red)
    check("roots green + own check red → RED", rc == 1)
    check("...with the aggregator's own line, not a cascade line",
          len(errs) == 1 and "receipts disagree" in errs[0] and "red because" not in errs[0])

    # 3. all ok → green, nothing red printed.
    rc, errs = aggregator({"platform-ref": {"result": "success", "outputs": {}}}, names, green)
    check("everything green → GREEN (exit 0), no error line", rc == 0 and not errs)

    # 4. a SKIPPED need is not a root — the aggregator judges skips itself (no green by skip).
    rc, errs = aggregator({"platform-ref": {"result": "success", "outputs": {}},
                           "test-repos": {"result": "skipped", "outputs": {}}},
                          names, lambda: (False, "these gates SKIPPED rather than running: test-repos"))
    check("a skipped need reaches the aggregator's own skip check, which stays RED",
          rc == 1 and len(errs) == 1 and "SKIPPED" in errs[0])

    # 5. transitive: the failed need is itself a cascade → the line names the TRUE root.
    rc, errs = aggregator({"admission": {"result": "failure",
                                         "outputs": {"cascade-root": "Validate node repos"}},
                           "test-repos": {"result": "skipped", "outputs": {}}}, names, own_red)
    check("a cascaded need names its upstream root, not itself",
          rc == 1 and len(errs) == 1 and "red because Validate node repos failed" in errs[0]
          and "Admitted" not in errs[0])

    # 6. waiting for platform → the same waiting wording, still RED.
    wait = "WAITING FOR PLATFORM: this change requires ≥ core CD #6100 (declared floor)"
    rc, errs = aggregator({"platform-ref": {"result": "failure", "outputs": {"waiting": wait}}}, names, green)
    check("a waiting root is RED", rc == 1 and len(errs) == 1)
    check("...titled and worded as a wait, carrying the root's own sentence",
          "title=Waiting for platform" in errs[0] and "is WAITING FOR PLATFORM" in errs[0] and wait in errs[0])
    rc, errs = aggregator({"admission": {"result": "failure", "outputs": {
        "cascade-root": "Resolve the released platform", "cascade-waiting": wait}}}, names, green)
    check("...and a wait two levels up reads the same", rc == 1 and wait in errs[0]
          and "Resolve the released platform is WAITING FOR PLATFORM" in errs[0])

    # 7. two distinct roots → still ONE line, naming both; duplicates collapse.
    rc, errs = aggregator({"platform-ref": {"result": "failure", "outputs": {}},
                           "validate": {"result": "failure", "outputs": {}},
                           "admission": {"result": "failure", "outputs": {"cascade-root": "Validate node repos"}}},
                          names, green)
    check("two roots → ONE line naming both, each once",
          rc == 1 and len(errs) == 1 and errs[0].count("Validate node repos") == 1
          and "Resolve the released platform failed" in errs[0] and "see them" in errs[0])

    # 8. a cancelled root says so.
    rc, errs = aggregator({"validate": {"result": "cancelled", "outputs": {}}}, names, green)
    check("a cancelled root is RED and says 'was cancelled'", rc == 1 and "was cancelled" in errs[0])

    # 9. an unknown job id falls back to its id (never an empty name).
    rc, errs = aggregator({"mystery": {"result": "failure"}}, names, green)
    check("a root without a display name is named by its id", rc == 1 and "red because mystery failed" in errs[0])

    # 10. unreadable needs → RED, never 'nothing failed'.
    for bad in ("", "not json", "[]", '{"x": 3}'):
        rc, out = _capture(run, bad, names, "")
        check(f"unreadable needs {bad!r} is RED (exit 2), never green", rc == 2 and "::error" in out)

    # 11. names parsing refuses a malformed line.
    try:
        parse_names(["platform-ref Resolve"])
        check("a malformed --names line is refused", False)
    except ValueError:
        check("a malformed --names line is refused", True)

    print(f"\n{'FAILED' if failures else 'passed'}: {len(failures)} failure(s)")
    return 1 if failures else 0


def main(argv: list[str] | None = None) -> int:
    cli = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    cli.add_argument("--self-test", action="store_true")
    cli.add_argument("--needs", default=None, help="toJSON(needs); default: $CASCADE_NEEDS")
    cli.add_argument("--names", action="append", default=[], help="'id=Display name' lines")
    cli.add_argument("--job", default="")
    args = cli.parse_args(argv)
    if args.self_test:
        return self_test()
    needs = args.needs if args.needs is not None else os.environ.get("CASCADE_NEEDS", "")
    try:
        names = parse_names(args.names or [os.environ.get("CASCADE_NAMES", "")])
    except ValueError as ex:
        print(f"::error title=Cascade verdict misconfigured::{ex}")
        return 2
    return run(needs, names, args.job)


if __name__ == "__main__":
    sys.exit(main())
