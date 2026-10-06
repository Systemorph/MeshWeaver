#!/usr/bin/env python3
"""assert-bake-floor.py — a publication may never stamp a floor ABOVE the platform it names.

    python3 .github/scripts/assert-bake-floor.py --bake DIR --platform-version 3.0.0-ci.9985
    python3 .github/scripts/assert-bake-floor.py --self-test

Every NodeType bundle the bake writes (``DIR/*.zip``) carries ``meshweaver/manifest.json`` with
``producerPlatformVersion`` — the PLATFORM FLOOR (``BundleReader.Manifest.ProducerPlatformVersion``).
A consumer running an OLDER build than that floor DECLINES the bytes
(``PlatformCompatibility.DeclineReason``: "its platform floor X … is NEWER than the running
platform Y"). The publication record names ONE portal (``PLATFORM_IMAGE``) as the platform the set
was baked for, and its ``MESHWEAVER_PLATFORM_VERSION`` is ``--platform-version``. So a floor above
that version is a set that contradicts itself: every consumer that resolves the set's own portal
declines the bundles, and the satellites' bake-consumption gate goes red on bytes nobody can adopt.

That is exactly what set 3.0.0-ci.9985 shipped (MeshWeaver.Manufacturing#84): floor 9985 on every
Plugins bundle, a portal reporting 9984. This check is the seal-time half of the fix — it runs
before anything is published, so a contradicting set is never sealed. It is a GATE:

  * no bundle at all, an unreadable archive, a manifest without a floor, or a floor that cannot be
    ORDERED against the platform version is RED — "could not check" is never "checked and fine";
  * ordering follows PlatformReleaseOrder: two continuous builds compare by their run ordinal
    (``ci.N`` / ``edge.N``); two clean releases compare by X.Y.Z; a continuous build against a
    clean release cannot be ordered and is RED here (the bake runs IN the named portal, so a
    mixed pair means the lane composed two different builds).

Exit 0 = every bundle's floor <= the platform version; 1 = not; 2 = usage.
"""
from __future__ import annotations

import argparse
import io
import json
import re
import sys
import tempfile
import zipfile
from pathlib import Path

MANIFEST_ENTRY = "meshweaver/manifest.json"  # NuGetPackageWriter.ManifestEntry
VERSION = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$")
CHANNELS = ("ci", "edge")


def parse(version: str) -> tuple[tuple[int, int, int], int | None] | None:
    """(X.Y.Z, run ordinal or None) — None when the text is not a platform version at all."""
    m = VERSION.match((version or "").strip())
    if not m:
        return None
    core = (int(m.group(1)), int(m.group(2)), int(m.group(3)))
    pre = (m.group(4) or "").split(".") if m.group(4) else []
    for i in range(len(pre) - 1):
        if pre[i].lower() in CHANNELS and pre[i + 1].isdigit():
            return core, int(pre[i + 1])
    if pre:
        return None  # a pre-release label no build mints carries no ordinal and no release order
    return core, None


def floor_problem(floor: str | None, platform: str) -> str | None:
    """Why ``floor`` may not be published for ``platform``, or None when it may. Pure."""
    if not floor:
        return "carries no producerPlatformVersion (the floor the bake must stamp)"
    f, p = parse(floor), parse(platform)
    if f is None:
        return f"stamps the floor {floor!r}, which is not a platform version"
    if p is None:
        return f"cannot be judged: the platform version {platform!r} is not a platform version"
    (f_core, f_n), (p_core, p_n) = f, p
    if (f_n is None) != (p_n is None):
        return (f"stamps the floor {floor} against the platform {platform} — a continuous build and "
                "a clean release cannot be ordered, so the floor cannot be shown to be satisfied")
    newer = (f_n > p_n) if f_n is not None else (f_core > p_core)
    if newer:
        return (f"stamps the floor {floor}, NEWER than the platform {platform} the publication names "
                "— every consumer resolving this set's own portal would DECLINE it")
    return None


def floors(bake: Path) -> list[tuple[str, str | None]]:
    """(bundle file name, its floor) for every *.zip in ``bake``. Raises on an unreadable bundle."""
    out = []
    for path in sorted(bake.glob("*.zip")):
        with zipfile.ZipFile(path) as zipped:
            try:
                raw = zipped.read(MANIFEST_ENTRY)
            except KeyError as error:
                raise ValueError(f"{path.name} has no {MANIFEST_ENTRY}") from error
        manifest = json.loads(raw)
        if not isinstance(manifest, dict):
            raise ValueError(f"{path.name}: {MANIFEST_ENTRY} is not a JSON object")
        floor = manifest.get("producerPlatformVersion")
        out.append((path.name, floor if isinstance(floor, str) else None))
    return out


def check(bake: Path, platform: str, log=print) -> int:
    if not bake.is_dir():
        log(f"::error title=Bake floor not checked::{bake} is not a directory — nothing was baked, so the floor cannot be shown to be satisfied")
        return 1
    try:
        found = floors(bake)
    except (OSError, ValueError, zipfile.BadZipFile, json.JSONDecodeError) as error:
        log(f"::error title=Bake floor not checked::a baked bundle could not be read: {error}")
        return 1
    if not found:
        log(f"::error title=Bake floor not checked::{bake} holds no *.zip bundle — refusing to report 'every floor satisfied' over zero bundles")
        return 1
    bad = [(name, problem) for name, floor in found if (problem := floor_problem(floor, platform))]
    stamped = sorted({floor or "(none)" for _, floor in found})
    if bad:
        for name, problem in bad[:20]:
            log(f"::error title=Bake floor above the set's platform::{name} {problem}")
        log(f"::error::{len(bad)} of {len(found)} bundle(s) would publish a set that contradicts itself "
            f"(floors stamped: {', '.join(stamped)}; platform image reports {platform}). Nothing is "
            "published: the bake must run in — and name — ONE build of the platform.")
        return 1
    log(f"bake floor OK: {len(found)} bundle(s), floors {', '.join(stamped)} <= platform {platform}")
    return 0


def self_test() -> int:
    failures = 0

    def expect(name: str, ok: bool, detail: object = "") -> None:
        nonlocal failures
        print(("  ok   " if ok else "  FAIL ") + name + ("" if ok else f" — {detail}"))
        failures += 0 if ok else 1

    # The incident, both directions, and the shapes the pipeline mints.
    expect("9985 floor on a 9984 portal is refused (Manufacturing#84)",
           floor_problem("3.0.0-ci.9985", "3.0.0-ci.9984") is not None)
    expect("equal floor passes", floor_problem("3.0.0-ci.9985", "3.0.0-ci.9985") is None)
    expect("older floor passes", floor_problem("3.0.0-ci.9984", "3.0.0-ci.9985") is None)
    expect("the run ordinal decides, not the line (3.1.0-ci.7841 < 3.0.0-ci.9000)",
           floor_problem("3.1.0-ci.7841", "3.0.0-ci.9000") is None)
    expect("edge ordinal compares with ci", floor_problem("3.0.0-edge.10", "3.0.0-ci.9") is not None)
    expect("retired rc.ci shape reads its ordinal", floor_problem("3.0.0-rc9.ci.7824", "3.0.0-ci.7825") is None)
    expect("build metadata ignored", floor_problem("3.0.0-ci.5+abc", "3.0.0-ci.5") is None)
    expect("clean releases compare by X.Y.Z", floor_problem("3.0.1", "3.0.0") is not None
           and floor_problem("3.0.0", "3.0.1") is None)
    expect("missing floor is RED", floor_problem(None, "3.0.0-ci.1") is not None)
    expect("mixed ci/clean is RED (cannot be ordered)", floor_problem("3.0.0-ci.5", "3.0.0") is not None)
    expect("garbage floor is RED", floor_problem("unknown", "3.0.0-ci.5") is not None)
    expect("garbage platform is RED", floor_problem("3.0.0-ci.5", "main") is not None)

    def bundle(directory: Path, name: str, manifest: object | None) -> None:
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as z:
            if manifest is not None:
                z.writestr(MANIFEST_ENTRY, json.dumps(manifest))
            z.writestr("lib/x.dll", b"\0")
        (directory / name).write_bytes(buf.getvalue())

    quiet = lambda _line: None  # noqa: E731
    with tempfile.TemporaryDirectory() as tmp:
        d = Path(tmp)
        expect("zero bundles is RED, never a vacuous pass", check(d, "3.0.0-ci.9", quiet) == 1)
        bundle(d, "A.zip", {"producerPlatformVersion": "3.0.0-ci.9"})
        expect("one consistent bundle passes", check(d, "3.0.0-ci.9", quiet) == 0)
        bundle(d, "B.zip", {"producerPlatformVersion": "3.0.0-ci.10"})
        expect("one bundle above the platform reds the whole publication", check(d, "3.0.0-ci.9", quiet) == 1)
        (d / "B.zip").unlink()
        bundle(d, "C.zip", {"plugin": "x"})
        expect("a bundle with no floor is RED", check(d, "3.0.0-ci.9", quiet) == 1)
        (d / "C.zip").unlink()
        bundle(d, "D.zip", None)
        expect("a bundle with no manifest is RED", check(d, "3.0.0-ci.9", quiet) == 1)
        (d / "D.zip").unlink()
        (d / "E.zip").write_bytes(b"not a zip")
        expect("an unreadable archive is RED", check(d, "3.0.0-ci.9", quiet) == 1)
    expect("an absent bake directory is RED", check(Path("/nonexistent-bake-dir"), "3.0.0-ci.9", quiet) == 1)

    print(f"{'PASS' if failures == 0 else 'FAIL'}: assert-bake-floor self-test ({failures} failure(s))")
    return 0 if failures == 0 else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--bake", type=Path)
    parser.add_argument("--platform-version")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if not args.bake or not args.platform_version:
        parser.error("--bake and --platform-version are both required")
    return check(args.bake, args.platform_version)


if __name__ == "__main__":
    sys.exit(main())
