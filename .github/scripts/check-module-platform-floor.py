#!/usr/bin/env python3
"""Is a module's declared `content.minMeshVersion` FLOOR satisfiable by the platform it is
being built against?

    check-module-platform-floor.py --package AI --floor 3.0.0-rc8 --platform-version 3.0.0-ci.7989
    exit 0 = the floor is satisfied (or none is declared)
    exit 1 = it is not, or the platform version is unknown

🚨 WHY THIS EXISTS (Systemorph/MeshWeaver#3554). A module declares a platform FLOOR, and
`ModulePlatformFloor.DeclineReason` refuses to land the module until the running platform
satisfies it. Nothing checked that the declared floor could be satisfied by a platform build
that ACTUALLY EXISTS, so a floor could be declared that no image can ever meet. The module then
holds for ever, and the hold message names two versions that look like they are in the right
order:

    HOLDING 3.0.0-ci.7989 — AI: the module requires platform 3.0.0-rc8 or newer
                             but this deployment runs 3.0.0-ci.7989

SemVer §11.4 compares pre-release identifiers as TEXT, so `"ci" < "rc"` and `3.0.0-ci.<n>` is
below `3.0.0-rc8` for EVERY n. Measured 2026-09-07: 42 packages declared rc-line or clean-`3.0.0`
floors, and every self-update candidate was held on both AKS portals while the registry held
1268 tags — 48 `3.0.0-ci.*`, ZERO `rc*`, ZERO `3.1.0-*`. The failure is silent at runtime: the
portal logs a hold and carries on.

🚨 WHAT IT COMPARES, AND WHY THAT IS THE RIGHT QUESTION. Not "does some tag somewhere satisfy
this", but "does THE PLATFORM THIS BUNDLE IS BEING BUILT AGAINST satisfy it". That is strictly
stronger and needs no registry listing: a module bundle records the framework identity it was
built against and a consumer only adopts a bundle whose identity matches its own platform, so a
floor ABOVE the build platform is unsatisfiable by construction — there is no deployment that
both runs a satisfying platform and would accept this bundle. It is also the reading the lane
already has in hand, so the check costs nothing.

🚨 IT MUST AGREE WITH THE RUNTIME, NOT MERELY RESEMBLE IT. The ordering below mirrors
`src/MeshWeaver.Plugin.Packaging/NuGetVersionComparer.cs` exactly — numeric core parts compared
as numbers, a release outranking any pre-release, dot-separated pre-release identifiers compared
numerically when both are numeric, numeric ranking below alphanumeric, otherwise ordinal — and
`ModulePlatformFloorScriptParityTest` in `test/Memex.Portal.Shared.Test` pins the two together on
a table of cases. Two call sites computing the same fold differently either never converge or
never fire, and both are silent.

🚨 FAIL CLOSED ON AN UNKNOWN PLATFORM. An empty `--platform-version` is exit 1, not a skip: "the
floor could not be checked" must never be reported as "checked and fine" — that equivalence IS
the defect. It mirrors `ModulePlatformFloor.DeclineReason`, which refuses to land a module on
faith when the running version is unknown.
"""

from __future__ import annotations

import argparse
import sys


#: The largest value C#'s `int.TryParse` accepts. Beyond it TryParse returns FALSE, which the
#: comparer reads as "not a number" — so Python's unbounded `int()` would disagree exactly where a
#: version carries an absurd segment. Small, and the whole point of the parity test.
_INT32_MAX = 2_147_483_647


def as_int32(part: str) -> int | None:
    """`int.TryParse(part, NumberStyles.None, InvariantCulture, out v)` — the value, or None.

    🚨 Three ways to disagree with C#, all closed here (Copilot review on #3554):
    ASCII digits only (`str.isdigit()` alone accepts superscripts and Arabic-Indic digits, which
    `NumberStyles.None` refuses); no sign and no whitespace (`NumberStyles.None` again, and
    `isdigit()` rejects both anyway); and OVERFLOW is a REFUSAL, not a big number — `int.TryParse`
    returns false past Int32.MaxValue, and the two call sites below do different things with that
    refusal, so it has to be reported rather than clamped.
    """
    if not part or not part.isascii() or not part.isdigit():
        return None
    value = int(part)
    return value if value <= _INT32_MAX else None


def split(version: str) -> tuple[list[int], list[str]]:
    """Numeric core + pre-release identifiers, build metadata discarded (SemVer ignores it)."""
    plus = version.find("+")
    if plus >= 0:
        version = version[:plus]
    dash = version.find("-")
    core, pre = (version, "") if dash < 0 else (version[:dash], version[dash + 1:])
    # CompareCore: `int.TryParse(...) ? lv : 0` — an unparsable OR OVERFLOWING core part is ZERO.
    parts = [as_int32(p) or 0 for p in core.split(".")]
    return parts, (pre.split(".") if pre else [])


def compare_core(left: list[int], right: list[int]) -> int:
    for i in range(max(len(left), len(right))):
        l = left[i] if i < len(left) else 0
        r = right[i] if i < len(right) else 0
        if l != r:
            return -1 if l < r else 1
    return 0


def compare_pre(left: list[str], right: list[str]) -> int:
    for i in range(max(len(left), len(right))):
        # Fewer identifiers ranks LOWER when all preceding are equal: rc3 < rc3.ci.1.
        if i >= len(left):
            return -1
        if i >= len(right):
            return 1
        # 🚨 `int.TryParse`'s BOOLEAN is the classifier here, not just the value — so an identifier
        # that overflows Int32 is ALPHANUMERIC to the runtime comparer, and must be to this one.
        # Clamping it to a number instead would rank it below every word, silently inverting the
        # comparison in the one case where the two implementations were free to differ.
        lv, rv = as_int32(left[i]), as_int32(right[i])
        lnum, rnum = lv is not None, rv is not None
        if lnum and rnum:
            if lv != rv:
                return -1 if lv < rv else 1
            continue
        # A numeric identifier always ranks below an alphanumeric one (SemVer §11.4.3).
        if lnum:
            return -1
        if rnum:
            return 1
        if left[i] != right[i]:
            return -1 if left[i] < right[i] else 1
    return 0


def compare(x: str, y: str) -> int:
    """NuGetVersionComparer.Compare, in Python."""
    lcore, lpre = split(x)
    rcore, rpre = split(y)
    core = compare_core(lcore, rcore)
    if core != 0:
        return core
    # A version WITHOUT a pre-release outranks one with: 3.0.0 > 3.0.0-rc3.
    if not lpre and not rpre:
        return 0
    if not lpre:
        return 1
    if not rpre:
        return -1
    return compare_pre(lpre, rpre)


def decline_reason(floor: str | None, platform: str | None) -> str | None:
    """None = the floor is satisfied (or none declared); otherwise why it is not.

    Mirrors ModulePlatformFloor.DeclineReason(minMeshVersion, runningVersion)."""
    if not floor or not floor.strip():
        return None
    if not platform or not platform.strip():
        return (f"the module declares minMeshVersion {floor} but the platform's version could not "
                "be determined — not building on faith")
    if compare(platform.strip(), floor.strip()) < 0:
        return (f"the module requires platform {floor} or newer but it is being built against "
                f"{platform} — no deployment can both satisfy that floor and adopt this bundle")
    return None


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--package", required=True, help="the package whose floor is being checked")
    ap.add_argument("--floor", default="", help="content.minMeshVersion, or empty for none")
    ap.add_argument("--platform-version", default="",
                    help="MESHWEAVER_PLATFORM_VERSION of the image this bundle compiles against")
    # Used by the parity test to compare verdicts without a package context.
    ap.add_argument("--quiet", action="store_true", help="verdict via exit code only")
    args = ap.parse_args(argv)

    reason = decline_reason(args.floor, args.platform_version)
    if reason is None:
        if not args.quiet:
            floor = args.floor.strip() or "(none)"
            print(f"{args.package}: platform floor {floor} is satisfied by "
                  f"{args.platform_version.strip() or '(unconstrained)'}")
        return 0

    if not args.quiet:
        # Two different failures, two different next steps — a single blended message would send
        # half the readers to the wrong one.
        guidance = (
            "Pin the caller's `platform-image` + `platform-image-digest` so the floor has something "
            "to be checked against. This is NOT skipped when the pin is absent: a floor that cannot "
            "be checked is exactly the silence #3554 is about."
            if not args.platform_version.strip() else
            "SemVer ranks pre-release identifiers as TEXT, so 'ci' < 'rc' and a 3.0.0-rc* floor is "
            "unreachable from the 3.0.0-ci line for every build number; a clean '3.0.0' floor "
            "outranks every pre-release of 3.0.0 the same way. Declare the OLDEST platform that "
            "actually satisfies the module."
        )
        print(f"::error::{args.package}: {reason}. {guidance} (Systemorph/MeshWeaver#3554)")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
