#!/usr/bin/env python3
"""Refuse a change to the public-surface DETECTOR that makes it see LESS than it saw before.

WHY THIS EXISTS (#3492)
-----------------------
`check-type-forwards.py` builds an index of every public top-level type and public member under
`src/`, and every cross-repo surface gate is a question asked of that index. A gate can therefore
be green for two reasons that look identical from outside: the change was safe, or **the index
never contained the thing it would have failed on**.

That is not hypothetical. A UTF-8 BOM — three bytes before the first character — defeats the
`^`-anchored matchers:

    ﻿namespace MeshWeaver.Layout;      NAMESPACE_RE is `^namespace`, so this does not match
    ﻿public class ButtonControl        TYPE_RE's indent group is `[ \\t]*`, and U+FEFF is neither

Measured on `origin/main` the night #3487 landed: **310 of 1 271** `.cs` files under `src/` carry
a BOM; **16** public types were in NO index at all (`ButtonControl`, `HtmlControl`,
`SplitterControl`, `INamed`, …) and **124** more were keyed under the wrong name. The blind set was
**identical at the gate's first commit and 754 commits later** — the detector was born blind and
nothing noticed, because a count nobody compares against anything is not a control arm.

WHY THE EXISTING FLOORS DO NOT COVER IT
---------------------------------------
Three static floors already guard the same report, and they are the right instrument for the
question they ask — *"did the scan read anything at all?"*, the green-on-zero-evidence shape:

    MIN_PUBLIC_TYPES_AT_BASE      = 500   check-cross-repo-pair.py     measured 1 971
    MIN_PUBLIC_INTERFACES_AT_BASE =  50   check-interface-addition.py  measured   132
    MIN_OBLIGATIONS_AT_BASE       = 150   check-interface-addition.py  measured   452

Each sits far below its true value ON PURPOSE — a floor must survive a carve-out wave. That slack
is exactly what the BOM slipped through: it cost **17 types of 1 971** (0.9 %) and **1 interface of
132**, against ~1 450 and ~82 of headroom. A floor 3.9× below the value can only catch a total
collapse.

Raising the constants does not fix it either. A floor tracks the CODEBASE, which grows and shrinks
for legitimate reasons, so any floor tight enough to catch a parser bug is loose enough to red on an
ordinary carve-out — and a gate that must be bypassed is worse than no gate. The two instruments are
complementary, not competing: a floor catches "read nothing", this catches "reads less than it did".

WHAT THIS CHECKS INSTEAD — a DIFFERENTIAL, not a floor
-------------------------------------------------------
Run the merge base's OWN copy of the detector and this diff's copy over the **same tree** (the
merge base), both in `--surface-json` report mode, and compare their published denominators.

The corpus is byte-identical for both runs, so **every difference is attributable to the parser
and to nothing else**. The codebase can grow, shrink or be refactored without moving the number:
this gate is invariant under code change by construction, and moves only when the detector's
behaviour moves — which is exactly its subject.

    detector at the merge base   ─┐
                                  ├─→  same tree  ─→  two denominators  ─→  delta
    detector in this diff        ─┘

The verdict is deliberately ASYMMETRIC, because the two directions mean opposite things:

  * a DECREASE is the failure this exists for — the detector now sees less of the same tree, so
    every surface gate downstream is quietly guarding a smaller set. Refused, unless the pull
    request declares it (below).
  * an INCREASE is a fix — the detector sees more of the same tree. Printed loudly, and passed.
    #3487's `lstrip` measures **+16 types / +111 members** here, which is the whole point.
  * ZERO is printed with both numbers, so a pass says something rather than nothing.

DECLARING AN INTENDED DECREASE
------------------------------
A tightening that is genuinely correct — a matcher that used to over-count — says so in the pull
request body, in the same shape as `Pairs-with:`:

    Parser-delta: publicTypesAtBase — nested records were counted as top-level; they are never
    independently bindable by simple name, so the old number was wrong upward.

The declaration names the COUNTER and a REASON, never the number: the corpus is the merge base,
which moves while a pull request is open, so a written figure would go stale and train people to
edit it without reading it. The measured figure is printed by this gate.

🚨 NO SKIP-TRAPDOOR. This gate never asks whether an input is present. If either detector version
cannot be run, or produces no report, that is a FAILURE naming which side and why — never a pass.
The one path that returns 0 without running anything is a PROOF, not an assumption: when every
subject file is byte-identical between the merge base and this diff, the delta is zero by
construction and the two SHA-256 digests are printed to show it.

WHAT IS DELIBERATELY NOT COVERED
--------------------------------
`check-record-signatures.py` shares the family's `^`-anchored parsing and had the same BOM hole
(fixed in #3487, exposure zero at the time). It is NOT gated here, and the reason is structural
rather than an oversight: it scans **the files this diff changed**, not a whole-tree index, so it
publishes no denominator that could shrink. Its blindness is per-file and shows up as a missed
finding on one file, which a differential over a fixed corpus cannot see. Giving it a whole-tree
index would be a real change to that gate, not a wrapper around it.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

# The files whose content decides what the index contains. `check-type-forwards.py` is the
# detector; `transitional_allow.py` is its only local import, and the temp copy of the detector
# resolves imports from its own directory, so it must travel with it.
SUBJECT_FILES = ("scripts/check-type-forwards.py", "scripts/transitional_allow.py")
DETECTOR = "scripts/check-type-forwards.py"

# The denominators the report publishes about the BASE tree. `*AtHead` is the same number here —
# both runs are invoked with `--base X --head X` — so comparing one of each pair is not a gap.
COUNTERS = (
    "publicTypesAtBase",
    "publicMembersAtBase",
    "publicInterfacesAtBase",
    "implementerObligationsAtBase",
)

# `Parser-delta:` — optionally bulleted and/or bolded, as a pull-request body naturally writes it.
# Mirrors DECLARATION_RE in check-cross-repo-pair.py rather than inventing a second spelling.
DECLARATION_RE = re.compile(
    r"^[ \t]*(?:[-*+][ \t]+)?\*{0,2}parser[ \t_-]?delta\*{0,2}[ \t]*:\*{0,2}[ \t]*(?P<value>.*?)[ \t]*$",
    re.IGNORECASE,
)
MIN_REASON_CHARS = 12


class DeltaFailure(Exception):
    """A condition that must end the run RED. Carries the operator-facing lines."""

    def __init__(self, lines: list[str]):
        super().__init__(lines[0] if lines else "parser delta check failed")
        self.lines = lines


def _git(root: Path, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        ["git", "-C", str(root), *args], capture_output=True, text=False
    )


def blob_at(root: Path, ref: str, path: str) -> bytes | None:
    """The file's bytes at `ref`, or None when the path does not exist there."""
    r = _git(root, "show", f"{ref}:{path}")
    return r.stdout if r.returncode == 0 else None


def digest(data: bytes | None) -> str:
    return "absent" if data is None else hashlib.sha256(data).hexdigest()[:16]


def run_report(root: Path, script: Path, base: str, out: Path, label: str) -> dict:
    """Run one detector version over ONE tree and return its report.

    🚨 Every failure path raises. A detector that cannot run is not a detector that found nothing.
    """
    env_path = str(script.parent)
    proc = subprocess.run(
        [sys.executable, str(script), "--base", base, "--head", base, "--surface-json", str(out)],
        cwd=str(root),
        capture_output=True,
        text=True,
        env={**_environ(), "PYTHONPATH": env_path},
    )
    if proc.returncode != 0:
        raise DeltaFailure(
            [
                f"::error::The {label} detector exited {proc.returncode} while reporting on {base}.",
                "  A detector that cannot run is not a detector that found nothing, so this is a",
                "  failure rather than a zero delta.",
                *[f"  {line}" for line in (proc.stderr or proc.stdout or "").splitlines()[-20:]],
            ]
        )
    if not out.exists():
        raise DeltaFailure(
            [
                f"::error::The {label} detector exited 0 but wrote no report to {out}.",
                "  `--surface-json` is the whole interface this gate reads; an absent file means",
                "  the version at that commit does not support it, and the delta is unknown, not zero.",
            ]
        )
    try:
        payload = json.loads(out.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise DeltaFailure([f"::error::The {label} report at {out} is not readable JSON: {exc}"])
    if not isinstance(payload, dict):
        raise DeltaFailure([f"::error::The {label} report is {type(payload).__name__}, not an object."])
    return payload


def _environ() -> dict:
    import os

    return dict(os.environ)


def parse_declarations(body: str) -> set[str]:
    """Counter names the pull-request body declares a decrease for, with a real reason.

    A declaration with no reason, or a reason shorter than MIN_REASON_CHARS, is NOT a declaration —
    the same rule `Pairs-with: none` follows, and for the same reason: a blank permits everything.
    """
    declared: set[str] = set()
    for line in body.splitlines():
        m = DECLARATION_RE.match(line)
        if not m:
            continue
        value = m.group("value")
        # `<counter> — <reason>` / `<counter> - <reason>` / `<counter>: <reason>`
        parts = re.split(r"[ \t]*[—–\-:][ \t]*", value, maxsplit=1)
        if len(parts) != 2:
            continue
        counter, reason = parts[0].strip(), parts[1].strip()
        if counter in COUNTERS and len(reason) >= MIN_REASON_CHARS:
            declared.add(counter)
    return declared


def evaluate(before: dict, after: dict, declared: set[str]) -> tuple[int, list[str]]:
    """Returns (exit code, lines to print). Every failure path returns 1; there is no other."""
    out: list[str] = []
    failures: list[str] = []

    out.append("Detector denominators over ONE tree, under two versions of the parser:")
    out.append(f"  {'counter':<32} {'merge base':>12} {'this diff':>12}   delta")
    for name in COUNTERS:
        b, a = before.get(name), after.get(name)
        if b is None and a is None:
            # Neither version publishes it. Nothing to compare, and nothing to hide behind: the
            # counters this gate knows about are named in COUNTERS, so an absent pair is a
            # counter that does not exist yet in either version.
            out.append(f"  {name:<32} {'—':>12} {'—':>12}   not published by either version")
            continue
        if b is None:
            out.append(f"  {name:<32} {'—':>12} {a:>12}   NEW — the merge base did not publish it")
            continue
        if a is None:
            failures.append(
                f"::error::`{name}` is published by the merge base ({b}) and by this diff NOT AT ALL. "
                "A withdrawn denominator is a control arm that stops being able to fail, which is "
                "the whole failure mode this gate exists for. Keep publishing it, or declare the "
                f"withdrawal with `Parser-delta: {name} — <reason>`."
            )
            out.append(f"  {name:<32} {b:>12} {'withdrawn':>12}   🚨")
            continue
        if not isinstance(b, int) or not isinstance(a, int):
            failures.append(
                f"::error::`{name}` is not an integer in both reports "
                f"({type(b).__name__} vs {type(a).__name__}) — the report shape changed and the "
                "delta cannot be computed. That is unknown, not zero."
            )
            continue
        d = a - b
        if d == 0:
            mark = ""
        elif d > 0:
            mark = "  ↑ the detector sees MORE of the same tree"
        elif name in declared:
            mark = "  ↓ DECLARED in the pull-request body"
        else:
            mark = "  🚨"
        out.append(f"  {name:<32} {b:>12} {a:>12}   {d:+}{mark}")
        if d < 0 and name not in declared:
            failures.append(
                f"::error::`{name}` fell from {b} to {a} ({d}) over an IDENTICAL tree. The corpus "
                "did not change, so this diff makes the public-surface detector see less than it "
                "saw before, and every gate built on it is now guarding a smaller set — silently. "
                f"If the old number was wrong upward, say so in the pull-request body:\n"
                f"  Parser-delta: {name} — <why the old count was wrong, at least "
                f"{MIN_REASON_CHARS} characters>"
            )

    if failures:
        return 1, out + [""] + failures
    return 0, out


# ─────────────────────────────── self-test ───────────────────────────────
#
# 🚨 Hermetic: the fixtures are tiny scripts that emit a report, never the real detector over the
# real tree. A self-test that needed a 1 200-file parse would be too slow to run on every job, and
# a gate whose self-test is skipped for cost is a gate nobody has watched fail.

_FIXTURE = """#!/usr/bin/env python3
import argparse, json
from pathlib import Path
ap = argparse.ArgumentParser()
ap.add_argument("--base"); ap.add_argument("--head"); ap.add_argument("--surface-json")
a = ap.parse_args()
Path(a.surface_json).write_text(json.dumps(%s))
"""

_FIXTURE_DIES = """#!/usr/bin/env python3
import sys
print("the tree could not be read", file=sys.stderr)
sys.exit(3)
"""

_FIXTURE_SILENT = """#!/usr/bin/env python3
import argparse
ap = argparse.ArgumentParser()
ap.add_argument("--base"); ap.add_argument("--head"); ap.add_argument("--surface-json")
ap.parse_args()
"""

_HEALTHY = {
    "publicTypesAtBase": 1964,
    "publicMembersAtBase": 12277,
    "publicInterfacesAtBase": 132,
    "implementerObligationsAtBase": 452,
}


def _with(**over) -> dict:
    d = dict(_HEALTHY)
    d.update(over)
    return d


def self_test() -> int:
    cases: list[tuple[str, dict, dict, str, int]] = [
        # (label, base report, head report, pr body, expected exit)
        (
            "an unchanged parser is a zero delta and passes",
            _HEALTHY, _HEALTHY, "", 0,
        ),
        (
            "🚨 the #3492 shape REVERSED — the detector loses the 16 BOM'd types: RED",
            _HEALTHY, _with(publicTypesAtBase=1948, publicMembersAtBase=12166), "", 1,
        ),
        (
            "#3487's actual fix — +16 types, +111 members over the same tree: PASSES",
            _with(publicTypesAtBase=1948, publicMembersAtBase=12166), _HEALTHY, "", 0,
        ),
        (
            "a declared decrease with a real reason is accepted",
            _HEALTHY, _with(publicTypesAtBase=1900),
            "Parser-delta: publicTypesAtBase — nested records were counted as top-level", 0,
        ),
        (
            "🚨 a declaration with a stub reason permits nothing",
            _HEALTHY, _with(publicTypesAtBase=1900),
            "Parser-delta: publicTypesAtBase — fix", 1,
        ),
        (
            "🚨 a declaration for a DIFFERENT counter does not cover this one",
            _HEALTHY, _with(publicTypesAtBase=1900),
            "Parser-delta: publicMembersAtBase — this counter changed for a good stated reason", 1,
        ),
        (
            "🚨 the tenth shape's own arm: obligations collapse while types are healthy: RED",
            _HEALTHY, _with(implementerObligationsAtBase=0), "", 1,
        ),
        (
            "🚨 a WITHDRAWN counter is a control arm that can no longer fail: RED",
            _HEALTHY, {k: v for k, v in _HEALTHY.items() if k != "publicInterfacesAtBase"}, "", 1,
        ),
        (
            "a counter NEW in this diff is not a decrease",
            {k: v for k, v in _HEALTHY.items() if k != "publicInterfacesAtBase"}, _HEALTHY, "", 0,
        ),
        (
            "🚨 a counter that stops being an integer is UNKNOWN, not zero: RED",
            _HEALTHY, _with(publicTypesAtBase="1964"), "", 1,
        ),
        (
            "a bulleted, bolded declaration is read like the pair gate reads Pairs-with",
            _HEALTHY, _with(publicTypesAtBase=1900),
            "- **Parser-delta:** publicTypesAtBase — the old number double-counted partials", 0,
        ),
    ]

    failed = 0
    for label, before, after, body, expected in cases:
        code, _ = evaluate(before, after, parse_declarations(body))
        ok = code == expected
        failed += 0 if ok else 1
        print(f"  {'ok  ' if ok else 'FAIL'} {label}" + ("" if ok else f"  (exit {code}, expected {expected})"))

    # The two run-level failures cannot be expressed through `evaluate`, because their whole point
    # is that no report exists to evaluate. They are exercised against real subprocesses.
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        for label, source, expect_msg in (
            ("🚨 a detector that EXITS NON-ZERO is a failure, not a zero delta", _FIXTURE_DIES, "exited 3"),
            ("🚨 a detector that exits 0 and writes NOTHING is a failure", _FIXTURE_SILENT, "wrote no report"),
        ):
            script = tmp / "fixture.py"
            script.write_text(source)
            try:
                run_report(Path.cwd(), script, "HEAD", tmp / "out.json", "fixture")
            except DeltaFailure as exc:
                ok = any(expect_msg in line for line in exc.lines)
            else:
                ok = False
            failed += 0 if ok else 1
            print(f"  {'ok  ' if ok else 'FAIL'} {label}")

    print(f"\n{'✓' if not failed else '🚨'} parser-delta self-test: {len(cases) + 2} case(s), {failed} failed.")
    return 1 if failed else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", help="the MERGE BASE to compare against (a commit-ish)")
    ap.add_argument(
        "--pr-body-file",
        help="file holding the pull-request body, which may DECLARE an intended decrease. Absent "
        "means no declaration is in force — a merge_group build has no pull request, and nothing "
        "reaches the queue without the pull_request run being green.",
    )
    ap.add_argument("--self-test", action="store_true", help="prove the gate is not vacuous")
    args = ap.parse_args()

    if args.self_test:
        return self_test()
    if not args.base:
        ap.error("--base is required unless --self-test is given")

    root = Path(
        subprocess.run(
            ["git", "rev-parse", "--show-toplevel"], check=True, capture_output=True, text=True
        ).stdout.strip()
    )

    # ── the one path that returns 0 without running anything, and it is a PROOF ──────────────
    base_blobs = {p: blob_at(root, args.base, p) for p in SUBJECT_FILES}
    head_blobs = {p: (root / p).read_bytes() if (root / p).exists() else None for p in SUBJECT_FILES}
    if base_blobs == head_blobs:
        print("The public-surface detector is byte-identical at the merge base and in this diff:")
        for p in SUBJECT_FILES:
            print(f"  {p}  sha256:{digest(head_blobs[p])}")
        print("So its delta over any tree is zero by construction. Nothing to measure.")
        return 0

    if base_blobs[DETECTOR] is None:
        print(f"{DETECTOR} does not exist at {args.base} — it is NEW in this change, so there is")
        print("no earlier behaviour to have regressed from. Nothing to measure.")
        return 0
    if head_blobs[DETECTOR] is None:
        print(f"::error::{DETECTOR} exists at {args.base} and is GONE in this diff. Every")
        print("::error::cross-repo surface gate reads its report; removing it removes the gates.")
        return 1

    body = ""
    if args.pr_body_file:
        try:
            body = Path(args.pr_body_file).read_text(encoding="utf-8")
        except OSError as exc:
            # 🚨 Not a skip. The body is how an intended decrease is declared; unable to read it
            # means unable to honour a declaration, which must not silently become "undeclared".
            print(f"::error::Cannot read the pull-request body from {args.pr_body_file}: {exc}")
            return 1

    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        for p in SUBJECT_FILES:
            blob = base_blobs[p]
            if blob is not None:
                (tmp / Path(p).name).write_bytes(blob)
        base_script = tmp / Path(DETECTOR).name
        head_script = root / DETECTOR

        print(f"Merge base: {args.base}")
        print("Subject files (what decides the index):")
        for p in SUBJECT_FILES:
            same = "same" if base_blobs[p] == head_blobs[p] else "CHANGED"
            print(f"  {p}  {digest(base_blobs[p])} -> {digest(head_blobs[p])}  [{same}]")
        print()

        try:
            before = run_report(root, base_script, args.base, tmp / "before.json", "merge base")
            after = run_report(root, head_script, args.base, tmp / "after.json", "this diff")
        except DeltaFailure as exc:
            for line in exc.lines:
                print(line)
            return 1

    code, lines = evaluate(before, after, parse_declarations(body))
    for line in lines:
        print(line)
    if code == 0:
        print()
        print("✓ This diff does not shrink what the public-surface detector can see.")
    return code


if __name__ == "__main__":
    # `shutil` is imported for the temp-dir contract this file relies on; keep the reference so a
    # linter cannot quietly drop it and change the failure mode of an unrelated edit.
    assert shutil is not None
    raise SystemExit(main())
