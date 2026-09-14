#!/usr/bin/env python3
"""resolve-shard-attempts.py — each shard's results come from the NEWEST ATTEMPT, chosen by attempt number.

WHY THIS EXISTS (MeshWeaver#4303, measured 2026-09-14 on run 34823051999)
-------------------------------------------------------------------------
`Consolidate test results` used to download the shard artefacts by the name pattern
`testResults-shard*`. A workflow run can hold SEVERAL artefacts under one name — a
`rerun-failed-jobs` re-runs the failing shard, and its upload lands beside the failing
attempt's. `actions/download-artifact` de-duplicates such a collision per NAME and keeps the
highest artefact **id**, and artefact ids are NOT monotonic across attempts. Measured:

    id 10338854604  size 297521  created 08:38:28Z   ← attempt 1, one failing test
    id 10338734845  size 298484  created 08:49:55Z   ← attempt 2, 1454/1454 green

10338854604 > 10338734845, so the consolidate downloaded ATTEMPT 1 and re-declared a failure
the re-run had just cleared — on attempt 2 and again on attempt 3, three minutes after both
artefacts were provably listed. The run could not be made green by any re-run; only a new head
could. That makes the lane's own recovery path a no-op for every unattributed or
infrastructure failure, on the ONE required check (`Consolidate test results`).

WHY THE NAME AND NOT THE API
----------------------------
`GET /repos/{o}/{r}/actions/runs/{id}/artifacts` carries `id`, `name`, `size_in_bytes`,
`created_at`, `digest` and a `workflow_run` block of `{id, repository_id, head_repository_id,
head_branch, head_sha}` — and **no attempt**. Measured on the artefacts above: there is no
field by which a consumer can attribute an artefact to the attempt that produced it. So the
producer has to say so, and the only channel it has is the NAME. This is the same remedy the
fleet already applies to the other artefact-name collision (node-repo-module-pack.yml's lane
key, Plugins#1077): when two producers can land one name in one run, the discriminator goes
INTO the name, derived from the producing context rather than trusted from a caller.

`created_at` would order these two correctly, but it is the wrong instrument for the same
reason the id is: it answers "which upload is newer", not "which attempt is THIS run's". A
shard that was not re-run keeps its attempt-1 artefact and is the current evidence for that
shard; a partial re-run therefore legitimately mixes attempts, and only the attempt number
says which mixture is right.

WHAT THIS DOES
--------------
Given the download directory of `pattern: testResults-shard*` — one directory per artefact
NAME, so `testResults-shard1-attempt1` and `testResults-shard1-attempt2` arrive side by side —
it keeps, per shard, the directory with the HIGHEST attempt and renames it to the canonical
`testResults-shard<N>` every downstream step already reads. Superseded directories are removed
from the download tree (the artefacts themselves survive on the run, for forensics).

It is a RESOLVER, not the coverage gate: a shard that reported nothing at all is not its
business — `Every shard must have reported` counts the canonical directories afterwards and
names the missing ones. What this script refuses is evidence it cannot attribute: a directory
whose name is not attempt-scoped, an attempt NEWER than the run's own, or a shard index
outside the matrix. Each of those is a state that should be impossible, and passing one over
would put the consolidate back to reading an artefact it could not attribute.

USAGE
-----
  resolve-shard-attempts.py --dir all-results --shards 6 --attempt 2
  resolve-shard-attempts.py --self-test        prove the resolution can fail

Exit 1 on any unattributable directory; exit 0 having named, per shard, the attempt it kept.
"""
from __future__ import annotations

import argparse
import re
import shutil
import sys
import tempfile
from pathlib import Path

NAME = re.compile(r"testResults-shard(\d+)-attempt(\d+)")


def resolve(root: Path, shards: int, attempt: int) -> tuple[list[str], list[str]]:
    """Return (notes, errors). Renames each shard's newest attempt to `testResults-shard<N>`."""
    notes: list[str] = []
    errors: list[str] = []
    if not root.is_dir():
        # Nothing was downloaded. That is not this script's verdict to give: the evidence gate
        # ("No test results found in any shard") and the coverage gate ("Only N of M shards
        # reported") both run after this one, in the same job, and both say it better. Saying
        # so here and exiting 0 keeps ONE red per cause; it skips no assertion, because every
        # assertion about coverage lives downstream.
        notes.append(f"{root} does not exist — no shard artefact was downloaded; the evidence and coverage gates report on that.")
        return notes, errors

    found: dict[int, list[tuple[int, Path]]] = {}
    for child in sorted(p for p in root.iterdir() if p.is_dir()):
        m = NAME.fullmatch(child.name)
        if not m:
            errors.append(
                f"::error::{child.name} is not an attempt-scoped shard artefact. Every shard uploads as "
                f"`testResults-shard<N>-attempt<run_attempt>` (MeshWeaver#4303) — a directory without the "
                f"attempt cannot be attributed to an attempt, and consolidating it is how attempt 1's "
                f"failure re-declared itself over attempt 2's green."
            )
            continue
        shard, att = int(m.group(1)), int(m.group(2))
        if att > attempt:
            errors.append(
                f"::error::{child.name} claims attempt {att} but this run is on attempt {attempt}. An artefact "
                f"from a LATER attempt cannot exist in this run — refusing to consolidate evidence this run did not produce."
            )
            continue
        if shard >= shards:
            errors.append(
                f"::error::{child.name} is shard {shard}, outside this run's matrix of {shards} "
                f"(0..{shards - 1}). The upload and the matrix have diverged; the coverage gate counts "
                f"against the matrix, so an out-of-range shard would be evidence nothing is checking."
            )
            continue
        found.setdefault(shard, []).append((att, child))

    superseded = 0
    for shard in sorted(found):
        attempts = sorted(found[shard])
        winner_attempt, winner = attempts[-1]
        for older_attempt, older in attempts[:-1]:
            shutil.rmtree(older)
            superseded += 1
        canonical = root / f"testResults-shard{shard}"
        if canonical.exists():  # pragma: no cover - the regex rejects the canonical name above
            errors.append(f"::error::{canonical.name} already exists — refusing to overwrite it.")
            continue
        winner.rename(canonical)
        if len(attempts) == 1:
            notes.append(f"shard {shard}: attempt {winner_attempt} (its only upload)")
        else:
            older_list = ", ".join(str(a) for a, _ in attempts[:-1])
            notes.append(f"shard {shard}: attempt {winner_attempt} — superseding attempt(s) {older_list}")

    notes.append(
        f"resolved {len(found)} shard director{'y' if len(found) == 1 else 'ies'} to the newest attempt "
        f"(run attempt {attempt}); {superseded} superseded upload(s) dropped from the download tree."
    )
    return notes, errors


# ── self-test ───────────────────────────────────────────────────────────────────────────────
def _plant(root: Path, shard: int, attempt: int, verdict: str) -> None:
    d = root / f"testResults-shard{shard}-attempt{attempt}"
    d.mkdir(parents=True)
    (d / "results.trx").write_text(verdict, encoding="utf-8")


def _verdict(root: Path, shard: int) -> str:
    return (root / f"testResults-shard{shard}" / "results.trx").read_text(encoding="utf-8")


def self_test() -> None:
    checks = 0
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)

        # 1 — one attempt per shard: every directory takes the canonical name, nothing is dropped.
        one = base / "one"
        one.mkdir()
        for shard in range(3):
            _plant(one, shard, 1, f"shard{shard} attempt1")
        notes, errors = resolve(one, 3, 1)
        assert errors == [], errors
        assert sorted(p.name for p in one.iterdir()) == [f"testResults-shard{i}" for i in range(3)]
        assert _verdict(one, 1) == "shard1 attempt1"
        assert "0 superseded" in notes[-1], notes
        checks += 4

        # 2 — 🚨 THE DEFECT (#4303). Shard 1 re-ran; attempt 2 is the evidence, attempt 1 is gone.
        #     This is the case an id-ordered or creation-order resolution got WRONG in production:
        #     attempt 2's artefact id was LOWER than attempt 1's.
        rerun = base / "rerun"
        rerun.mkdir()
        _plant(rerun, 0, 1, "shard0 attempt1 green")
        _plant(rerun, 1, 1, "shard1 attempt1 FAILED")
        _plant(rerun, 1, 2, "shard1 attempt2 green")
        notes, errors = resolve(rerun, 2, 2)
        assert errors == [], errors
        assert _verdict(rerun, 1) == "shard1 attempt2 green", _verdict(rerun, 1)
        # A shard that was NOT re-run keeps attempt 1 — a partial re-run legitimately mixes attempts,
        # so "only the current attempt" would throw away five sixths of the suite.
        assert _verdict(rerun, 0) == "shard0 attempt1 green", _verdict(rerun, 0)
        assert sorted(p.name for p in rerun.iterdir()) == ["testResults-shard0", "testResults-shard1"]
        assert "superseding attempt(s) 1" in " ".join(notes), notes
        checks += 5

        # 3 — the attempt is a NUMBER, not a string: attempt 10 supersedes attempt 9. A lexical
        #     max — the plausible wrong implementation, and what `sort` would give — keeps 9.
        two_digit = base / "two_digit"
        two_digit.mkdir()
        _plant(two_digit, 0, 9, "attempt9")
        _plant(two_digit, 0, 10, "attempt10")
        notes, errors = resolve(two_digit, 1, 10)
        assert errors == [], errors
        assert _verdict(two_digit, 0) == "attempt10", _verdict(two_digit, 0)
        checks += 2

        # 4 — a directory that is not attempt-scoped is REFUSED, by name. This is what the pre-#4303
        #     upload produced, and consolidating it is exactly the unattributable read.
        legacy = base / "legacy"
        legacy.mkdir()
        (legacy / "testResults-shard0").mkdir()
        notes, errors = resolve(legacy, 1, 1)
        assert len(errors) == 1 and "not an attempt-scoped shard artefact" in errors[0], errors
        checks += 2

        # 5 — an attempt NEWER than this run's is impossible; it must be loud, not quietly preferred.
        future = base / "future"
        future.mkdir()
        _plant(future, 0, 1, "attempt1")
        _plant(future, 0, 4, "attempt4")
        notes, errors = resolve(future, 1, 2)
        assert len(errors) == 1 and "this run is on attempt 2" in errors[0], errors
        assert _verdict(future, 0) == "attempt1", _verdict(future, 0)
        checks += 3

        # 6 — a shard outside the matrix is refused: the coverage gate counts against the matrix,
        #     so an out-of-range directory would be evidence nothing downstream is checking.
        stray = base / "stray"
        stray.mkdir()
        _plant(stray, 0, 1, "attempt1")
        _plant(stray, 7, 1, "attempt1")
        notes, errors = resolve(stray, 2, 1)
        assert len(errors) == 1 and "outside this run's matrix" in errors[0], errors
        checks += 2

        # 7 — a shard that reported NOTHING is not this script's verdict: it resolves the rest and
        #     exits 0, leaving `Every shard must have reported` to name the gap (one red per cause).
        missing = base / "missing"
        missing.mkdir()
        _plant(missing, 0, 1, "attempt1")
        _plant(missing, 2, 1, "attempt1")
        notes, errors = resolve(missing, 3, 1)
        assert errors == [], errors
        assert sorted(p.name for p in missing.iterdir()) == ["testResults-shard0", "testResults-shard2"]
        checks += 2

        # 8 — no download directory at all: a note, not a verdict (the gates downstream own that).
        notes, errors = resolve(base / "absent", 6, 1)
        assert errors == [] and "does not exist" in notes[0], (notes, errors)
        checks += 1

    print(f"resolve-shard-attempts: {checks} assertions passed")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dir", default="all-results", help="the download directory of pattern: testResults-shard*")
    ap.add_argument("--shards", type=int, help="CI_SHARD_TOTAL — the matrix size this run declared")
    ap.add_argument("--attempt", type=int, help="github.run_attempt")
    ap.add_argument("--self-test", action="store_true", help="prove the resolution can fail")
    args = ap.parse_args()
    if args.self_test:
        self_test()
        return 0
    if args.shards is None or args.attempt is None:
        print("::error::--shards and --attempt are required (CI_SHARD_TOTAL and github.run_attempt)")
        return 2
    notes, errors = resolve(Path(args.dir), args.shards, args.attempt)
    for note in notes:
        print(note)
    for error in errors:
        print(error)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
