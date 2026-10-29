#!/usr/bin/env python3
"""host-gate-log.py — bring a build host's gate log to a node repo's Tests-area ratchet, BY ITS RECEIPT.

H9 of the standing build hosts (Doc/Architecture/BuildHosts §2 step 7). A repository's host `gate`
stage writes the tester's console to `$MW_BUILD_ARTIFACTS/gate-log/gate.log`; after the stage
finishes the host uploads it to the shared CI artifact store, reads it back, and only then reports a
receipt carrying the stage's outcome, `stageState` (MeshWeaver.Deploy#52: a RED stage hands its files
over too, marked `failed`). This fetches the `gate` stage's receipts with core's build-artifacts-fetch.py (the run's
GitHub OIDC token — the same gate as the stage log; each object fetched by its locator and re-hashed
against its receipt), then asserts what a ratchet needs before it may read the file:

  * exactly the one file `gate.log` (a host that handed over something else did not hand back the log);
  * non-empty;
  * carrying the tester's `=== mw-plugin-test summary ===` block (a truncated or foreign log would let
    the ratchet read a partial skipped set).

and copies it to --out. The caller then runs its own `check-test-suites.py --gate-log --out` — the
SAME ratchet it runs over an Actions gate's log — so a host run is ratcheted over the tester's own
console, never over the host's verdict or a declared command's text.

Every node repo calls it through the composite action `.github/actions/host-gate-log` (one copy; the
shape MeshWeaver.Plugins#4482 built for Plugins, generalised here so the satellites need no copy).

    host-gate-log.py --api-url https://control.example.com/api/builds --store file:/ci-artifacts \
        --expect-store-id <id> --sha <head> [--channel latest] --out "$RUNNER_TEMP/gate-log/gate.log"
    host-gate-log.py --self-test

RED, never green, on: anything build-artifacts-fetch.py refuses (no receipt; a gate log handed over
by a FAILED gate stage, which that reader never takes as green; an unreadable endpoint; a hash or size
mismatch), and on each assertion above.
"""
from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
SUMMARY = "=== mw-plugin-test summary ==="
LOG = "gate.log"
UNIT_DIR = "gate-log"
# The tester's terminal verdict line — the same set gate-shard-merge.py's `_summary_blocks` requires.
VERDICT_RE = re.compile(r"^(ALL GREEN\.|GREEN — |GATE FAILED)", re.MULTILINE)


class Red(Exception):
    """A refusal with its reason."""


def _inert(text: str) -> str:
    return (text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            .replace("::", ":​:"))


def check_staged(staged: str) -> str:
    """The staged directory holds exactly one non-empty gate.log carrying the summary block. Returns its path."""
    names = sorted(n for n in os.listdir(staged)) if os.path.isdir(staged) else []
    if names != [LOG]:
        raise Red(f"the gate stage's receipts staged {names or 'nothing'} — expected exactly [{LOG}] "
                  "(a host hands artifacts over only from a SUCCESSFUL stage; a red host gate leaves none, and its own red is the verdict)")
    path = os.path.join(staged, LOG)
    if not os.path.isfile(path) or os.path.getsize(path) == 0:
        raise Red("the handed-back gate.log is empty — the ratchet would read absent evidence")
    with open(path, encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    if SUMMARY not in text:
        raise Red(f"the handed-back gate.log carries no '{SUMMARY}' block — a truncated or foreign log")
    # The LAST summary must end in its terminal verdict: a log cut right after the header, or an
    # earlier complete summary followed by a truncated final one, would hand the ratchet a partial set.
    if not VERDICT_RE.search(text.rsplit(SUMMARY, 1)[1]):
        raise Red("the handed-back gate.log's last summary block ends without its terminal verdict "
                  "(ALL GREEN. / GREEN — … / GATE FAILED …) — a truncated log")
    return path


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--api-url")
    ap.add_argument("--store")
    ap.add_argument("--expect-store-id")
    ap.add_argument("--sha")
    ap.add_argument("--channel", default="latest")
    ap.add_argument("--audience", default="")
    ap.add_argument("--out")
    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    missing = [n for n in ("api_url", "store", "expect_store_id", "sha", "out") if not getattr(a, n)]
    if missing:
        print(f"::error::host-gate-log: missing {', '.join('--' + m.replace('_', '-') for m in missing)}")
        return 2
    staged = tempfile.mkdtemp(prefix="host-gate-log-")
    try:
        cmd = [sys.executable, os.path.join(HERE, "build-artifacts-fetch.py"), "--api-url", a.api_url,
               "--store", a.store, "--expect-store-id", a.expect_store_id, "--sha", a.sha,
               "--channel", a.channel, "--stage", "gate", "--unit-dir", UNIT_DIR, "--out", staged]
        if a.audience:
            cmd += ["--audience", a.audience]
        rc = subprocess.call(cmd)
        if rc != 0:
            print(f"::error title=Build host::fetching the gate stage's receipts failed (build-artifacts-fetch exit {rc}) — "
                  "the Tests-area ratchet has no host gate log to read")
            return 1
        path = check_staged(staged)
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        shutil.copyfile(path, a.out)
        print(f"the host's gate log: {os.path.getsize(a.out)} bytes, verified against its receipt → {a.out}")
        return 0
    except Red as e:
        print(f"::error title=Build host::{_inert(str(e))}")
        return 1
    finally:
        shutil.rmtree(staged, ignore_errors=True)


def self_test() -> int:
    fails: list[str] = []

    def check(name: str, cond: bool) -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}")
        if not cond:
            fails.append(name)

    def staged_with(files: dict) -> str:
        d = tempfile.mkdtemp(prefix="hgl-st-")
        for n, body in files.items():
            with open(os.path.join(d, n), "w", encoding="utf-8") as fh:
                fh.write(body)
        return d

    def red(files: dict) -> bool:
        try:
            check_staged(staged_with(files))
            return False
        except Red:
            return True

    good = f"ok  A/B: compile=Ok render=ok tests=ok\n{SUMMARY}\n[PASS] A (1 node(s), 1 type(s))\nALL GREEN.\n"
    # The positive control first: a check that refuses everything would pass every negative below.
    check("a complete gate.log is accepted", not red({LOG: good}))
    check("nothing staged is RED (a red host gate handed nothing over)", red({}))
    check("an empty gate.log is RED", red({LOG: ""}))
    check("a gate.log with no summary block is RED (truncated or foreign)", red({LOG: "ok  A/B: compile=Ok\n"}))
    check("a log cut right after the summary header is RED (truncated)", red({LOG: f"ok  A/B\n{SUMMARY}\n[PASS] A (1 node(s), 1 type(s))\n"}))
    check("a complete summary followed by a truncated final one is RED", red({LOG: good + f"{SUMMARY}\n[PASS] A (1 node(s), 1 type(s))\n"}))
    check("a GREEN — … verdict is accepted", not red({LOG: f"{SUMMARY}\n[PASS] A (1 node(s), 1 type(s))\nGREEN — 1 debt item(s)\n"}))
    check("another file beside gate.log is RED (not the gate stage's one hand-back)", red({LOG: good, "x.zip": "z"}))
    check("a differently named log is RED", red({"gate.txt": good}))
    check("a missing staged directory is RED", _raises(lambda: check_staged("/nonexistent/hgl")))
    check("missing arguments are a usage error (2), never a pass", main(["--api-url", "x"]) == 2)
    if fails:
        print(f"host-gate-log self-test: {len(fails)} FAILED: {fails}")
        return 1
    print("host-gate-log self-test: every case behaved (accepts a complete log; refuses none, empty, truncated, verdict-less, foreign, extra)")
    return 0


def _raises(fn) -> bool:
    try:
        fn()
        return False
    except Red:
        return True


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
