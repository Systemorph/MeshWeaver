#!/usr/bin/env python3
"""Which platform set does the PREVIOUS-epoch bake leg build against? (policy bake-per-live-identity)

    python3 .github/scripts/previous-epoch-ceiling.py --declaration src/MeshWeaver.Compiler/platform-compatibility.json
    python3 .github/scripts/previous-epoch-ceiling.py --self-test

Plugin bundles are baked for every framework identity a live portal runs. Under the compatibility key
(`c<major>e<epoch>`) that is ONE identity per epoch, so in the steady state the publish-bake lane's
normal leg (the newest sealed set) covers every portal. Only across a DECLARED break can two be live:
portals that have rolled run the new key, and portals that have not yet rolled still run the old one.
The previous-epoch leg bakes the same content against the old epoch's LAST platform build — the
break's `previousEpochCeiling`, the build everything of the old epoch "works up to" — so those portals
get a publication under their own key instead of holding their sources at the last one sealed before
the break.

The declaration (`platform-compatibility.json`) is the one source, so this is DECLARED, never derived
from what some replica happens to report:

  * no `breaks` entry                    ⇒ no previous epoch exists; the leg has nothing to bake.
  * the newest break, `previousEpochRetired: true`
                                         ⇒ the maintainer declared no portal runs the old epoch any
                                           more; the leg stops.
  * otherwise                            ⇒ bake against that break's `previousEpochCeiling`.

A break with no `previousEpochCeiling`, or a declaration that cannot be read, is an ERROR (exit 1,
named) — never "nothing to bake", which would silently leave old-epoch portals unserved.

Output (GITHUB_OUTPUT shape, stdout): `run=true|false`, `ceiling=<set>`, `epoch=<n>`, and a
`reason=` line saying why.
"""
from __future__ import annotations

import argparse
import json
import sys


class DeclarationError(Exception):
    pass


def decide(declaration: dict) -> dict:
    if not isinstance(declaration, dict) or "epoch" not in declaration:
        raise DeclarationError("the declaration carries no `epoch` — not a platform-compatibility declaration")
    breaks = declaration.get("breaks")
    if breaks is None:
        breaks = []
    if not isinstance(breaks, list):
        raise DeclarationError("`breaks` is not a list")
    if not breaks:
        return {"run": False, "ceiling": "", "epoch": "",
                "reason": f"no declared break — epoch {declaration['epoch']} is the only one, so every live portal runs the current key and the normal leg covers it"}
    newest = max(breaks, key=lambda b: b.get("epoch", -1) if isinstance(b, dict) else -1)
    if not isinstance(newest, dict) or not isinstance(newest.get("epoch"), int):
        raise DeclarationError("the newest `breaks` entry names no integer `epoch`")
    old_epoch = newest["epoch"] - 1
    if newest.get("previousEpochRetired") is True:
        return {"run": False, "ceiling": "", "epoch": str(old_epoch),
                "reason": f"the break to epoch {newest['epoch']} declares previousEpochRetired: no portal runs epoch {old_epoch} any more"}
    ceiling = newest.get("previousEpochCeiling")
    if not isinstance(ceiling, str) or not ceiling.strip():
        raise DeclarationError(f"the break to epoch {newest['epoch']} names no previousEpochCeiling — the old epoch's last build cannot be resolved")
    return {"run": True, "ceiling": ceiling.strip(), "epoch": str(old_epoch),
            "reason": f"epoch {old_epoch} may still be live (the break to epoch {newest['epoch']} is not declared retired) — baking against its ceiling {ceiling.strip()}"}


def emit(result: dict) -> None:
    print(f"run={'true' if result['run'] else 'false'}")
    print(f"ceiling={result['ceiling']}")
    print(f"epoch={result['epoch']}")
    print(f"reason={result['reason']}")


def self_test() -> int:
    ok_cases = [
        ({"epoch": 1, "breaks": []}, False, "", "no break ⇒ nothing to bake"),
        ({"epoch": 1}, False, "", "absent breaks ⇒ nothing to bake"),
        ({"epoch": 2, "breaks": [{"epoch": 2, "previousEpochCeiling": "3.1.12000", "reason": "x"}]}, True, "3.1.12000", "one break ⇒ bake its ceiling"),
        ({"epoch": 3, "breaks": [{"epoch": 2, "previousEpochCeiling": "3.1.100"},
                                 {"epoch": 3, "previousEpochCeiling": "3.2.900"}]}, True, "3.2.900", "two breaks ⇒ the NEWEST break's ceiling"),
        ({"epoch": 3, "breaks": [{"epoch": 3, "previousEpochCeiling": "3.2.900"},
                                 {"epoch": 2, "previousEpochCeiling": "3.1.100"}]}, True, "3.2.900", "order-independent: newest by epoch, not by position"),
        ({"epoch": 2, "breaks": [{"epoch": 2, "previousEpochCeiling": "3.1.12000", "previousEpochRetired": True}]}, False, "", "retired ⇒ stop"),
        ({"epoch": 2, "breaks": [{"epoch": 2, "previousEpochCeiling": "3.1.12000", "previousEpochRetired": "yes"}]}, True, "3.1.12000", "only a literal true retires (a string does not)"),
    ]
    err_cases = [
        ({"breaks": []}, "no epoch ⇒ error"),
        ({"epoch": 2, "breaks": [{"epoch": 2}]}, "a break without a ceiling ⇒ error, never 'nothing to bake'"),
        ({"epoch": 2, "breaks": [{"epoch": 2, "previousEpochCeiling": "  "}]}, "a blank ceiling ⇒ error"),
        ({"epoch": 2, "breaks": "x"}, "breaks not a list ⇒ error"),
        ({"epoch": 2, "breaks": [{"previousEpochCeiling": "3.1.1"}]}, "a break without an epoch ⇒ error"),
    ]
    failed = 0
    for decl, run, ceiling, label in ok_cases:
        try:
            r = decide(decl)
            good = r["run"] == run and r["ceiling"] == ceiling
        except DeclarationError as e:
            good, r = False, {"reason": f"raised {e}"}
        failed += not good
        print(f"  {'✓' if good else '✗'} {label}  [{r['reason']}]")
    for decl, label in err_cases:
        try:
            r = decide(decl)
            good = False
            why = f"answered run={r['run']}"
        except DeclarationError as e:
            good, why = True, str(e)
        failed += not good
        print(f"  {'✓' if good else '✗'} {label}  [{why}]")
    total = len(ok_cases) + len(err_cases)
    print(f"{total - failed} passed, {failed} failed")
    return 1 if failed else 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--declaration")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.declaration:
        ap.error("--declaration is required")
    try:
        with open(a.declaration, encoding="utf-8") as f:
            declaration = json.load(f)
        emit(decide(declaration))
    except (OSError, ValueError, DeclarationError) as e:
        print(f"::error::cannot decide the previous-epoch bake leg from {a.declaration}: {e}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
