#!/usr/bin/env python3
"""consolidation-body-check.py — `Consolidate test results` is never green over a body the gates did not judge.

    python3 .github/scripts/consolidation-body-check.py --base-ref main      # in collect-results, pull_request
    python3 .github/scripts/consolidation-body-check.py --self-test

THE GAP (MeshWeaver#7659 review). The body gates (`needs: [pr-body]`) judge the body their run's `pr-body`
job read. If the body is EDITED while the run is still building, the body-edit lane (pr-body-edited.yml)
never cancels the build — so without this check the build could finish and publish the required context
green over the OLD body's verdict, and an armed pull request could merge in that window.

So the aggregator re-reads the CURRENT body at consolidation time (scripts/read-current-pr-body.py — never
the reused read) and compares it with the body the gates judged (`needs.pr-body.outputs.body-b64`):
  * identical               → nothing to add: the gates' verdicts are the current body's;
  * different               → the current body is judged with the SAME gate scripts
                              (scripts/preflight-pr.py --only body); anything but GREEN fails this job,
                              naming why. GREEN means every gate passes the current body too, so the
                              required check stays true without a re-run;
  * unreadable / no judged body → fails (no verdict is never a pass).
This is an ADDITIONAL reason to fail the aggregator: it never replaces the job's other failure lines,
and a failed gate still fails the job through its own step.
"""
from __future__ import annotations

import argparse
import base64
import binascii
import os
import subprocess
import sys
import tempfile
from pathlib import Path

NO_VERDICT_EXIT = 3


def judge(judged_b64: str | None, read_now, run_preflight) -> tuple[int, str]:
    """(exit code, sentence). `read_now()` → (rc, body); `run_preflight(body)` → preflight exit code."""
    if not judged_b64:
        return 1, ("the body gates' judged body was not handed over (`pr-body` produced no output) — the "
                   "current body cannot be compared with it; no verdict is never a pass")
    try:
        judged = base64.b64decode(judged_b64.encode("ascii"), validate=True).decode("utf-8")
    except (binascii.Error, UnicodeError, ValueError):
        return 1, "the judged body handed over by `pr-body` is not valid base64 UTF-8"
    rc, now = read_now()
    if rc == NO_VERDICT_EXIT:
        return 1, ("NO VERDICT — the current body could not be read (GitHub API unavailable); re-run this job "
                   "once it answers. This is not a finding about the pull request")
    if rc != 0 or now is None:
        return 1, "the current pull-request body could not be read, so it cannot be shown to be the judged one"
    if now == judged:
        return 0, "the current body is the one the body gates judged — their verdicts stand"
    pf = run_preflight(now)
    if pf == 0:
        return 0, ("the body was EDITED after the body gates judged it; the same gate scripts pass the CURRENT "
                   "body too, so the verdict stands")
    what = {1: "FAILS at least one body gate", 2: "could not be fully judged (INCOMPLETE)"}.get(pf, f"exit {pf}")
    return 1, (f"the body was EDITED after the body gates judged it, and the CURRENT body {what} — see the "
               "preflight report above. The body-edit lane re-runs `Pull-request body (read once)` once this run "
               "finishes; or re-run that job by hand. Doc/Architecture/PrePrPreflight")


def main_live(base_ref: str) -> int:
    with tempfile.TemporaryDirectory() as tmp:
        body = Path(tmp) / "current-pr-body.md"

        def read_now():
            rc = subprocess.run([sys.executable, "scripts/read-current-pr-body.py", "--output", str(body)]).returncode
            return rc, (body.read_text(encoding="utf-8") if rc == 0 and body.is_file() else None)

        def run_preflight(_text: str) -> int:
            return subprocess.run([sys.executable, "scripts/preflight-pr.py", "--only", "body", "--base",
                                   f"origin/{base_ref}", "--body-file", str(body)]).returncode

        rc, sentence = judge(os.environ.get("JUDGED_PR_BODY_B64"), read_now, run_preflight)
    print(("" if rc == 0 else "::error::") + sentence)
    return rc


def self_test() -> int:
    failures: list[str] = []
    b64 = lambda t: base64.b64encode(t.encode()).decode()  # noqa: E731
    calls: list[str] = []

    def pre(code):
        def run(text):
            calls.append(text)
            return code
        return run

    cases = [
        ("unchanged body: pass, preflight NOT run", b64("A"), lambda: (0, "A"), pre(1), 0, 0),
        ("edited body that still passes: pass", b64("A"), lambda: (0, "B"), pre(0), 0, 1),
        ("edited body that FAILS a gate: fail", b64("A"), lambda: (0, "B"), pre(1), 1, 1),
        ("edited body judged INCOMPLETE: fail (never a pass)", b64("A"), lambda: (0, "B"), pre(2), 1, 1),
        ("current body unreadable (no verdict): fail", b64("A"), lambda: (3, None), pre(0), 1, 0),
        ("current body refused: fail", b64("A"), lambda: (1, None), pre(0), 1, 0),
        ("no judged body handed over: fail", "", lambda: (0, "A"), pre(0), 1, 0),
        ("a judged body that is not base64: fail", "%%%", lambda: (0, "A"), pre(0), 1, 0),
    ]
    for label, judged, read_now, run, want_rc, want_calls in cases:
        calls.clear()
        rc, _ = judge(judged, read_now, run)
        if rc != want_rc or len(calls) != want_calls:
            failures.append(f"{label}: rc={rc} preflight calls={len(calls)}, expected rc={want_rc} calls={want_calls}")
    # NEGATIVE CONTROL: a comparison that always says "unchanged" passes the failing edit — so the
    # failing row above is the comparison firing, not an accident of the fixture.
    calls.clear()
    rc, _ = judge(b64("A"), lambda: (0, "A"), pre(1))
    rc_edit, _ = judge(b64("A"), lambda: (0, "B"), pre(1))
    if not (rc == 0 and rc_edit == 1):
        failures.append("negative control: an unchanged body and an edited failing body are not told apart")
    if failures:
        print("self-test FAILED:")
        for f in failures:
            print(f"  ✗ {f}")
        return 1
    print(f"✓ consolidation-body-check self-test: {len(cases)} cases — the aggregator passes only when the current "
          "body IS the judged one or the same gates pass it; an edited failing or incomplete body, an unreadable "
          "body, or no judged body fails it.")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base-ref", default="main")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    return self_test() if args.self_test else main_live(args.base_ref)


if __name__ == "__main__":
    sys.exit(main())
