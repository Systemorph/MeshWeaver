#!/usr/bin/env python3
"""Is the commit main-cd is about to publish OLDER than a set that is already published?

    python3 .github/scripts/cd-superseded.py --target SHA --newest SHA --compare compare.json
    python3 .github/scripts/cd-superseded.py --self-test

WHY
---
A platform set's version is `<major>.<minor>.<run#>` — the number of the run that BUILT it — and
every consumer takes "the newest set" by that number: each install's self-updater, and every
satellite lane that resolves "the newest sealed platform". The invariant that makes that sound is
that a higher number never carries OLDER code. The reconcile path has kept it from the start ("it
heals to HEAD, never to the failed commit"); the push path did not.

`workflow_run` fires once per completed "MeshWeaver Build and Test" run, in COMPLETION order, and
two merges a minute apart finish their builds in either order. The delivery runs then queue behind
the one concurrency slot and each publishes the commit it was handed. Measured on 2026-10-14:
CD #10795 published core 04d56c7 (which carried the xunit.v3.assert 4.0.2 fix, #6738) as 3.1.10795;
CD #10796, triggered 21 s later by the build of its PARENT 6b9944c, ran next — its gate logged
`Newest published set: 04d56c7` and still published 6b9944c as 3.1.10796. Every MeshWeaver.Plugins
main run then resolved 3.1.10796, lost the fix, and failed restore with NU1109 on every portal-hosts
unit until a later set (3.1.10799) superseded it again.

INPUT
-----
`--target` is the commit this run would publish, `--newest` the commit of the newest published set
(empty when there is none), `--compare` the GitHub compare JSON for `compare/<newest>...<target>`
(a missing or unreadable file is allowed).

VERDICT (stdout, last line): `superseded=true` or `superseded=false`, preceded by one reason line.

`superseded=true` needs a POSITIVE finding: the compare says the target is strictly BEHIND the
newest published set (`status: behind`, `ahead_by: 0`, `behind_by > 0`) — every change in the
target is already in a published set, so publishing it can only mint a higher number for older
code. Anything else — no published set, the same commit (main-cd's one-commit-one-set rule owns
that), a target ahead of or diverged from the set, an unreadable compare — is `superseded=false`
and says which, so the decision falls back to exactly what it was before this check existed.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile


def classify(target: str, newest: str, compare: dict | None) -> tuple[bool, str]:
    target = (target or "").strip()
    newest = (newest or "").strip()
    if not newest:
        return False, "no published set to compare against"
    if not target:
        return False, "no target commit was named — cannot judge"
    if target.startswith(newest) or newest.startswith(target):
        return False, f"{target[:7]} IS the newest published set — the one-commit-one-set rule decides"
    if not isinstance(compare, dict):
        return False, f"the compare {newest[:7]}...{target[:7]} could not be read — not judged superseded"
    status = compare.get("status")
    ahead = compare.get("ahead_by")
    behind = compare.get("behind_by")
    if status == "behind" and ahead == 0 and isinstance(behind, int) and behind > 0:
        return True, (f"{target[:7]} is {behind} commit(s) BEHIND the newest published set {newest[:7]} — "
                      f"every change in it already shipped, and publishing it would give older code a higher version")
    return False, f"{target[:7]} is '{status}' relative to {newest[:7]} (ahead {ahead}, behind {behind}) — not superseded"


def _load(path: str | None) -> dict | None:
    if not path:
        return None
    try:
        with open(path, encoding="utf-8") as fh:
            value = json.load(fh)
    except (OSError, ValueError):
        return None
    return value if isinstance(value, dict) else None


def self_test() -> int:
    t = "6b9944cbde4e294acaa7905fbb8cc1c958628fc0"
    n = "04d56c739b2f0c1e9a7d3b6c5e4f8a1d2c3b4a59"
    cases = [
        # (name, target, newest, compare, expected)
        ("the 2026-10-14 shape: parent published after its child", t, n,
         {"status": "behind", "ahead_by": 0, "behind_by": 2}, True),
        ("a newer commit is never superseded", t, n,
         {"status": "ahead", "ahead_by": 2, "behind_by": 0}, False),
        ("the same commit is left to the one-commit-one-set rule", t, t[:7], None, False),
        ("no published set at all", t, "", None, False),
        ("an unreadable compare falls back to publishing", t, n, None, False),
        ("diverged history is not claimed as superseded", t, n,
         {"status": "diverged", "ahead_by": 1, "behind_by": 1}, False),
        ("identical trees with different shas are not superseded", t, n,
         {"status": "identical", "ahead_by": 0, "behind_by": 0}, False),
        ("behind_by 0 is not a positive finding", t, n,
         {"status": "behind", "ahead_by": 0, "behind_by": 0}, False),
        ("a missing target is never judged", "", n,
         {"status": "behind", "ahead_by": 0, "behind_by": 2}, False),
    ]
    failed = 0
    for name, target, newest, compare, expected in cases:
        got, reason = classify(target, newest, compare)
        mark = "✓" if got == expected else "✗"
        if got != expected:
            failed += 1
        print(f"{mark} {name}: superseded={str(got).lower()} — {reason}")
    # The file reader: an unreadable or non-object file is "could not be read", never a crash.
    with tempfile.TemporaryDirectory() as tmp:
        bad = os.path.join(tmp, "bad.json")
        with open(bad, "w", encoding="utf-8") as fh:
            fh.write("[not json")
        arr = os.path.join(tmp, "arr.json")
        with open(arr, "w", encoding="utf-8") as fh:
            fh.write("[]")
        for label, path in (("malformed", bad), ("non-object", arr), ("absent", os.path.join(tmp, "nope.json"))):
            got, _ = classify(t, n, _load(path))
            ok = got is False
            failed += 0 if ok else 1
            print(f"{'✓' if ok else '✗'} a {label} compare file is read as unreadable, not superseded")
    if failed:
        print(f"\n✗ {failed} self-test case(s) failed", file=sys.stderr)
        return 1
    print("\n✓ all self-test cases passed")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--target")
    ap.add_argument("--newest", default="")
    ap.add_argument("--compare")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    superseded, reason = classify(args.target or "", args.newest, _load(args.compare))
    print(reason)
    print(f"superseded={'true' if superseded else 'false'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
