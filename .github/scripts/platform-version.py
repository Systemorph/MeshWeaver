#!/usr/bin/env python3
"""platform-version.py — the ONE shell-facing reader of a platform version tag, in BOTH notations.

WHY. Core's CD reads the run number back out of a version tag in several bash steps (`never
backwards` before the moving pointers move, the satellite-compat baseline) and spells the line
pointers from a version. Each step used to carry its own `[.-]ci\\.([0-9]+)$` regex and its own
`${v##*ci.}` strip. Policy `platform-semver-versioning` (Doc/Architecture/PlatformVersioning) moves
continuous builds from `3.0.0-ci.<run>` to the plain SemVer `<major>.<minor>.<run>` from line 3.1
on — the PATCH is the same monotonic CD run number — and a strip written for one notation reads the
other as "no run number" (or, worse, as the whole version), which a `-gt` then compares as garbage.
So the rule lives HERE, once, with a self-test, and the steps call it.

THE RULE (the C# twin is `MeshWeaver.Plugin.Packaging.PlatformReleaseOrder`, which must agree):
  * `X.Y.Z[-pre]-ci.<n>` and the retired `X.Y.Z-rcN.ci.<n>` → run number <n>;
  * `<major>.<minor>.<run>` with (major, minor) >= SEMVER_ERA_START → run number <run>;
  * anything else — a clean `3.0.0` promotion, a pointer (`3-latest`), a sha, `main`, a per-RID
    image (`…-linux-x64`), an unverified `-edge` build — is NOT a set and has no run number.

Usage:
  platform-version.py ordinal <version>     # prints the run number; exit 1 when it has none
  platform-version.py max-ordinal            # stdin: one tag per line; prints the highest run number
                                             #        among the SET tags (nothing when there is none)
  platform-version.py line-pointers <version>
                                             # the moving pointers a set moves: `3-latest 3.0-latest
                                             # 3.0.0-latest` for the old notation, `3-latest
                                             # 3.1-latest` for the SemVer one (a per-build
                                             # `3.1.10050-latest` would be a pointer to one build)
  platform-version.py --self-test
"""
from __future__ import annotations

import re
import sys

# 🚨 The first line of the SemVer notation. Must equal PlatformReleaseOrder.SemVerEraStart (C#),
# resolve-platform.py's and arm-promoted-set.py's SEMVER_ERA_START.
SEMVER_ERA_START = (3, 1)
LEGACY_SET = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?[.-]ci\.(\d+)$")
SEMVER_SET = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")


def ordinal(version: str) -> int | None:
    """The CD run number a set tag was published by, or None when the tag is not a set."""
    text = (version or "").strip()
    m = LEGACY_SET.match(text)
    if m:
        return int(m.group(1))
    m = SEMVER_SET.match(text)
    # A zero patch is a floor or a release (`3.1.0`), never a CD run.
    if m and (int(m.group(1)), int(m.group(2))) >= SEMVER_ERA_START and int(m.group(3)) > 0:
        return int(m.group(3))
    return None


def max_ordinal(tags: list[str]) -> int | None:
    numbers = [n for n in (ordinal(t) for t in tags) if n is not None]
    return max(numbers) if numbers else None


def line_pointers(version: str) -> list[str]:
    """The moving pointers a set moves. A non-set is a ValueError — never a guessed pointer."""
    text = (version or "").strip()
    if ordinal(text) is None:
        raise ValueError(f"{version!r} is not a platform set tag — it names no line")
    core = text.split("-", 1)[0]
    major, minor, patch = core.split(".")
    if SEMVER_SET.match(text):
        return [f"{major}-latest", f"{major}.{minor}-latest"]
    return [f"{major}-latest", f"{major}.{minor}-latest", f"{major}.{minor}.{patch}-latest"]


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, got, want) -> None:
        print(f"  {'ok  ' if got == want else 'FAIL'} {name}")
        if got != want:
            failures.append(f"{name}: got {got!r}, want {want!r}")

    check("old notation", ordinal("3.0.0-ci.9999"), 9999)
    check("retired .ci. separator", ordinal("3.0.0-rc9.ci.7824"), 7824)
    check("SemVer notation: the run number is the PATCH", ordinal("3.1.10000"), 10000)
    check("SemVer notation across a minor bump", ordinal("3.2.10400"), 10400)
    check("the withdrawn slip keeps its ci number", ordinal("3.1.0-ci.7841"), 7841)
    for not_a_set in ("3.0.0", "3.1.0", "4.0.0", "3.0.5", "2.9.12345", "3-latest", "3.1-latest", "3.0.0-latest", "main",
                      "abc1234", "3.1.10000-linux-x64", "3.1.10000-edge.10000", "3.1.10000-rc1", ""):
        check(f"{not_a_set!r} is no set", ordinal(not_a_set), None)
    check("max over both notations is the newest PUBLICATION, not the newest notation",
          max_ordinal(["3.0.0-ci.10002", "3.1.10001", "3.1.0-ci.7841", "3-latest", "main"]), 10002)
    check("max: the first SemVer build outranks the last ci set",
          max_ordinal(["3.0.0-ci.9999", "3.1.10000"]), 10000)
    check("max of no sets is None", max_ordinal(["main", "3-latest", "3.0.0"]), None)
    check("pointers, old notation", line_pointers("3.0.0-ci.9999"), ["3-latest", "3.0-latest", "3.0.0-latest"])
    check("pointers, SemVer notation (no per-build pointer)", line_pointers("3.1.10000"), ["3-latest", "3.1-latest"])
    try:
        line_pointers("main")
        check("pointers of a non-set are refused", "accepted", "refused")
    except ValueError:
        check("pointers of a non-set are refused", "refused", "refused")
    print(f"platform-version self-test: {len(failures)} failure(s)")
    for f in failures:
        print(f"  - {f}")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if argv[1:] == ["--self-test"]:
        return self_test()
    if len(argv) == 3 and argv[1] == "ordinal":
        n = ordinal(argv[2])
        if n is None:
            return 1
        print(n)
        return 0
    if len(argv) == 2 and argv[1] == "max-ordinal":
        n = max_ordinal(sys.stdin.read().split())
        if n is not None:
            print(n)
        return 0
    if len(argv) == 3 and argv[1] == "line-pointers":
        try:
            print(" ".join(line_pointers(argv[2])))
        except ValueError as error:
            print(f"::error::{error}", file=sys.stderr)
            return 1
        return 0
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
