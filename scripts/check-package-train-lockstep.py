#!/usr/bin/env python3
"""Hold the `Microsoft.Extensions.*` servicing train at ONE version in Directory.Packages.props (#7227).

THE SHAPE (measured on #7219, the 2026-10-26 weekly nuget-minor-patch sweep).
dotnet/runtime ships the `Microsoft.Extensions.*` packages as one servicing train: every package of
patch `<major>.0.<n>` depends on its siblings at `>= <major>.0.<n>`. A central pin that lags the
train is therefore not a stale pin, it is a DOWNGRADE: restore sees a sibling at 10.0.14 asking for
`Microsoft.Extensions.Options >= 10.0.14` while the central pin says 10.0.13, and that is NU1605,
an error under warnings-as-errors.

Dependabot updates only the dependencies it DISCOVERS in a project file. A pin that no project in
this repository references is invisible to it: the satellites import this file and consume such
pins versionless (see check-package-pin-removal.py), so they are live, but the sweep never moves
them. #7219 moved 18 train packages to 10.0.14 and left four behind
(`Options`, `Options.ConfigurationExtensions`, `Configuration.EnvironmentVariables`,
`FileSystemGlobbing`) — none referenced by any core project. Core restored clean, every core gate
was green, and the first build that restored a satellite project against the new list, the
control image in main-cd (run 40300348495), failed with:

    MeshWeaver.AI.OpenAI -> MeshWeaver.AI -> Microsoft.Extensions.Http 10.0.14
                         -> Microsoft.Extensions.Options (>= 10.0.14)
    MeshWeaver.AI.OpenAI -> Microsoft.Extensions.Options (>= 10.0.13)        error NU1605

It failed the same way on every later main run that built that image, so no control image was
promoted until the pins moved.

THE RULE. Every stable `Microsoft.Extensions.*` pin whose version has the servicing-train shape
`<major>.0.<patch>` carries the same version as every other such pin of that major. Packages on a
different release line — `Microsoft.Extensions.AI*`, `Http.Resilience`, `ServiceDiscovery`
(10.11.x, dotnet/extensions), `Microsoft.Extensions.Azure` (1.x) — do not have that shape and are
not part of the train; prereleases are skipped for the same reason.

This needs no checkout of any other repository and no credential, so it runs on every pull request,
fork and Dependabot ones included, before anything is restored.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

PROPS = "Directory.Packages.props"
FAMILY = "Microsoft.Extensions."
_ENTRY = re.compile(r"<PackageVersion\b([^>]*)/?>", re.IGNORECASE)
_ATTR = re.compile(r"""\b(Include|Version)\s*=\s*(["'])(.*?)\2""", re.IGNORECASE)
_TRAIN = re.compile(r"^(\d+)\.0\.(\d+)$")


def pins(text: str) -> list[tuple[str, str]]:
    """Every (id, version) PackageVersion entry, in either attribute order."""
    out: list[tuple[str, str]] = []
    for m in _ENTRY.finditer(text):
        attrs = {k.lower(): v for k, _, v in _ATTR.findall(m.group(1))}
        if "include" in attrs and "version" in attrs:
            out.append((attrs["include"].strip(), attrs["version"].strip()))
    return out


def train_violations(entries: list[tuple[str, str]]) -> tuple[int, list[str]]:
    """Return (train-member count, report lines); the report is empty when the train is in step."""
    by_major: dict[str, dict[str, list[str]]] = {}
    members = 0
    for pkg, version in entries:
        if not pkg.startswith(FAMILY):
            continue
        m = _TRAIN.match(version)
        if not m:
            continue
        members += 1
        by_major.setdefault(m.group(1), {}).setdefault(version, []).append(pkg)

    report: list[str] = []
    for major, versions in sorted(by_major.items()):
        if len(versions) < 2:
            continue
        newest = max(versions, key=lambda v: int(_TRAIN.match(v).group(2)))
        report.append(
            f"The Microsoft.Extensions.* {major}.0.x servicing train is split across "
            f"{len(versions)} versions; every pin must carry {newest}:"
        )
        for version in sorted(versions, key=lambda v: int(_TRAIN.match(v).group(2))):
            if version == newest:
                continue
            for pkg in sorted(versions[version]):
                report.append(f"  • {pkg} {version}  →  {newest}")
    return members, report


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, got, want) -> None:
        if got != want:
            failures.append(f"  {name}: got {got!r}, want {want!r}")

    props = (
        "<Project><ItemGroup>\n"
        '  <PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.14" />\n'
        "  <PackageVersion Version='10.0.13' Include='Microsoft.Extensions.Options' />\n"
        '  <PackageVersion Include="Microsoft.Extensions.AI" Version="10.11.0" />\n'
        '  <PackageVersion Include="Microsoft.Extensions.Azure" Version="1.14.0" />\n'
        '  <PackageVersion Include="Microsoft.Extensions.AI.AzureAIInference" Version="10.0.0-preview.1" />\n'
        '  <PackageVersion Include="System.CommandLine" Version="2.0.13" />\n'
        "</ItemGroup></Project>\n"
    )
    entries = pins(props)
    check("reads both attribute orders", ("Microsoft.Extensions.Options", "10.0.13") in entries, True)

    # Fires on the #7219 shape, and names the laggard alone with the version it must move to.
    members, report = train_violations(entries)
    check("counts only train members", members, 2)
    check("split train fires", bool(report), True)
    check("names the laggard", any("Microsoft.Extensions.Options 10.0.13  →  10.0.14" in l for l in report), True)
    check("does not name the leader as a laggard", any("Http 10.0.14  →" in l for l in report), False)

    # Silent when the train is in step, and other release lines never join it.
    in_step = props.replace("Version='10.0.13'", "Version='10.0.14'")
    check("in-step train is silent", train_violations(pins(in_step))[1], [])

    # Two majors are two trains: an 11.0.x pin beside a 10.0.x pin is not a split.
    two = in_step.replace('"Microsoft.Extensions.Http" Version="10.0.14"', '"Microsoft.Extensions.Http" Version="11.0.1"')
    check("different majors are different trains", train_violations(pins(two))[1], [])

    # Numeric, not lexical: 10.0.9 lags 10.0.10.
    lex = props.replace("10.0.14", "10.0.10").replace("10.0.13", "10.0.9")
    check("numeric order", any("Options 10.0.9  →  10.0.10" in l for l in train_violations(pins(lex))[1]), True)

    # The real tree is read, not starved: a matcher that stopped matching must not read as in step.
    root = Path(__file__).resolve().parent.parent
    real = root / PROPS
    if real.is_file():
        members_here, _ = train_violations(pins(real.read_text(encoding="utf-8")))
        check("the real train is read, not starved", members_here >= 10, True)

    if failures:
        print("self-test FAILED:", file=sys.stderr)
        print("\n".join(failures), file=sys.stderr)
        return 1
    print("✓ check-package-train-lockstep self-test: fires on a split train naming each laggard, "
          "stays silent on an in-step train, keeps other release lines and majors out of it.")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Hold the Microsoft.Extensions.* servicing train in step (#7227).")
    ap.add_argument("--self-test", action="store_true", help="prove the check is not vacuous")
    ap.add_argument("--props", default=None, help=f"path to {PROPS} (default: the repository root's)")
    args = ap.parse_args()
    if args.self_test:
        return self_test()

    path = Path(args.props) if args.props else Path(__file__).resolve().parent.parent / PROPS
    if not path.is_file():
        print(f"::error::{path} does not exist — this check cannot read the train, which is not the same as a clean one.",
              file=sys.stderr)
        return 1
    members, report = train_violations(pins(path.read_text(encoding="utf-8")))
    # 🚨 A STARVED READ IS NOT A CLEAN ONE: the train carried 20+ pins when this was written.
    if members < 10:
        print(f"::error::{path} yielded only {members} Microsoft.Extensions.* servicing-train pins — the "
              "matcher has stopped seeing its subject, so 'in step' would mean nothing. Fix the matcher.",
              file=sys.stderr)
        return 1
    if not report:
        print(f"{path.name}: {members} Microsoft.Extensions.* servicing-train pins, all in step.")
        return 0
    print("::error::" + report[0])
    for line in report[1:]:
        print(line)
    print("A lagging train pin is a downgrade (NU1605) for any project that also reaches a newer "
          "sibling. Dependabot cannot see a pin no core project references, so move it by hand in "
          "the same pull request. Doc/Architecture/CrossRepoPairGate → \"The servicing train\".")
    return 1


if __name__ == "__main__":
    sys.exit(main())
