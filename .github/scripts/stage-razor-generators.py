#!/usr/bin/env python3
"""Stage the Razor source generator for linux-x64 AND linux-arm64 — from the SDK FEED, never a registry.

`mw-plugin-test` compiles .razor/.cshtml with the SDK's Razor source generator, and the SDK
ReadyToRun-compiles that generator for its own RID: the same file carries PE machine 0xFD1D on
linux-x64 and 0xD11D on linux-arm64, and the wrong one throws BadImageFormatException at run time
(Doc/Architecture/InMeshBuildAndTest → "The generator is ReadyToRun-compiled for the SDK's own
RID"). main-cd publishes BOTH architectures from ONE x64 host, so it stages one directory per RID.

🚨 WHY THE ARM64 COPY COMES FROM THE FEED. The step this replaces read it out of
`mcr.microsoft.com/dotnet/sdk:<host SDK version>`. The host SDK is `setup-dotnet` with
`dotnet-version: 10.0.x`, which installs the newest SDK the release METADATA names — and MCR
publishes its image for that SDK HOURS later. In between, `docker pull` answered `manifest unknown`
and the tester leg failed, so the whole set was withheld from the seal. Measured on the 10.0.402
release day (2026-10-13): main-cd runs 38469625713, 38476972766 and 38477708481 failed exactly
there ("manifest for mcr.microsoft.com/dotnet/sdk:10.0.402 not found"), and the set for core
2799e2e23 sealed only on the next reconcile, once MCR had caught up. The arm64 tarball is published
with the metadata that made setup-dotnet pick the version, so reading it from the feed cannot lag
the host SDK: one source of truth, no second clock.

The tarball is checked against the SHA-512 the release metadata publishes for it, and only the
generator directory is extracted (no binary is executed — the host is x64).

Fail closed, per RID: the compiler must be present AND carry the PE machine of that RID. The old
step checked presence only, so a copy of the wrong architecture would have shipped.

    stage-razor-generators.py <out-root>   # writes <out-root>/linux-x64 and <out-root>/linux-arm64
    stage-razor-generators.py --self-test  # offline: fires on each defect, silent on the fix
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
from pathlib import Path

COMPILER = "Microsoft.CodeAnalysis.Razor.Compiler.dll"
GENERATORS = "Sdks/Microsoft.NET.Sdk.Razor/source-generators"
# R2R stamps the target machine XOR'd with the OS marker (Linux 0x7B79): AMD64 0x8664 → 0xFD1D,
# ARM64 0xAA64 → 0xD11D.
MACHINE = {"linux-x64": 0xFD1D, "linux-arm64": 0xD11D}
METADATA = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json"


class StageError(Exception):
    pass


def pe_machine(path: Path) -> int | None:
    data = path.read_bytes()
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    offset = struct.unpack_from("<I", data, 0x3C)[0]
    if offset + 6 > len(data) or data[offset:offset + 4] != b"PE\0\0":
        return None
    return struct.unpack_from("<H", data, offset + 4)[0]


def assert_staged(root: Path) -> None:
    """Every RID holds the compiler, built for THAT RID. Raises naming the first defect."""
    for rid, machine in MACHINE.items():
        dll = root / rid / COMPILER
        if not dll.is_file():
            raise StageError(f"no Razor compiler staged for {rid} ({dll})")
        actual = pe_machine(dll)
        if actual != machine:
            shown = "not a PE file" if actual is None else f"PE machine {actual:#06x}"
            raise StageError(f"the {rid} Razor compiler is {shown}, expected {machine:#06x} — the "
                             f"wrong architecture throws BadImageFormatException on {rid} only")


def sdk_file(metadata: dict, version: str, rid: str) -> tuple[str, str]:
    """(url, sha512) of the SDK tarball for `version`/`rid`, from one channel's releases.json."""
    for release in metadata.get("releases", []):
        sdks = list(release.get("sdks") or []) + ([release["sdk"]] if release.get("sdk") else [])
        for sdk in sdks:
            if sdk.get("version") != version:
                continue
            for entry in sdk.get("files", []):
                if entry.get("rid") == rid and entry.get("name", "").endswith(".tar.gz"):
                    url, digest = entry.get("url"), entry.get("hash")
                    if not url or not digest:
                        raise StageError(f"SDK {version} {rid} has no url/hash in the release metadata")
                    return url, digest.lower()
            raise StageError(f"SDK {version} is in the release metadata but carries no {rid} tarball")
    raise StageError(f"SDK {version} is not in the release metadata — the host SDK must be a released "
                     "one (setup-dotnet resolves from the same metadata)")


def extract_generators(tarball: bytes, expected_sha512: str, version: str, dest: Path) -> None:
    actual = hashlib.sha512(tarball).hexdigest()
    if actual != expected_sha512:
        raise StageError(f"SDK {version} tarball SHA-512 {actual[:16]}… does not match the release "
                         f"metadata's {expected_sha512[:16]}… — refusing to stage it")
    prefix = f"sdk/{version}/{GENERATORS}/"
    dest.mkdir(parents=True, exist_ok=True)
    taken = 0
    with tarfile.open(fileobj=io.BytesIO(tarball), mode="r:gz") as archive:
        for member in archive.getmembers():
            name = member.name[2:] if member.name.startswith("./") else member.name
            if not name.startswith(prefix) or not member.isfile():
                continue
            leaf = name[len(prefix):]
            if not leaf or "/" in leaf:
                continue
            source = archive.extractfile(member)
            assert source is not None
            (dest / leaf).write_bytes(source.read())
            taken += 1
    if taken == 0:
        raise StageError(f"the SDK {version} tarball holds nothing under {prefix}")


def fetch(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=300) as response:  # noqa: S310 — fixed https host
        return response.read()


def host_sdk() -> tuple[str, Path]:
    version = subprocess.run(["dotnet", "--version"], check=True, capture_output=True,
                             text=True).stdout.strip()
    listing = subprocess.run(["dotnet", "--list-sdks"], check=True, capture_output=True,
                             text=True).stdout
    for line in listing.splitlines():
        if line.startswith(f"{version} [") and line.endswith("]"):
            return version, Path(line[len(version) + 2:-1]) / version
    raise StageError(f"`dotnet --list-sdks` does not list the active SDK {version}")


def stage(root: Path) -> None:
    machine = os.uname().machine
    if machine != "x86_64":
        raise StageError(f"this step assumes an x64 host (it takes the host SDK's copy as the "
                         f"linux-x64 one); the host is {machine}")
    version, sdk_dir = host_sdk()
    print(f"SDK {version}; host source-generators: {sdk_dir / GENERATORS}")
    x64 = root / "linux-x64"
    x64.mkdir(parents=True, exist_ok=True)
    for dll in (sdk_dir / GENERATORS).glob("*.dll"):
        shutil.copy2(dll, x64 / dll.name)

    channel = ".".join(version.split(".")[:2])
    metadata = json.loads(fetch(METADATA.format(channel=channel)))
    url, digest = sdk_file(metadata, version, "linux-arm64")
    print(f"linux-arm64: {url}")
    extract_generators(fetch(url), digest, version, root / "linux-arm64")

    assert_staged(root)
    for rid in MACHINE:
        print(f"{rid}: {' '.join(sorted(p.name for p in (root / rid).iterdir()))}")


# ---------------------------------------------------------------------------------------------
# Self-test: offline, synthetic PE files and tarballs. Each case FIRES on its defect; the fix
# case stays silent.

def _pe(machine: int) -> bytes:
    data = bytearray(0x100)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", data, 0x84, machine)
    return bytes(data)


def _tarball(version: str, files: dict[str, bytes]) -> bytes:
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as archive:
        for name, payload in files.items():
            info = tarfile.TarInfo(f"./sdk/{version}/{GENERATORS}/{name}")
            info.size = len(payload)
            archive.addfile(info, io.BytesIO(payload))
        other = tarfile.TarInfo("./dotnet")
        other.size = 4
        archive.addfile(other, io.BytesIO(b"\x7fELF"))
    return buffer.getvalue()


def self_test() -> int:
    failures: list[str] = []

    def expect(label: str, action, fires: str | None) -> None:
        try:
            action()
        except StageError as error:
            if fires is None:
                failures.append(f"{label}: refused the fix — {error}")
            elif fires not in str(error):
                failures.append(f"{label}: fired, but without naming '{fires}' — {error}")
            return
        if fires is not None:
            failures.append(f"{label}: did NOT fire")

    version = "10.0.999"
    good_arm = _tarball(version, {COMPILER: _pe(0xD11D), "Other.dll": _pe(0xD11D)})
    meta = {"releases": [{"sdks": [{"version": version, "files": [
        {"name": "dotnet-sdk-linux-arm64.zip", "rid": "linux-arm64", "url": "u.zip", "hash": "00"},
        {"name": "dotnet-sdk-linux-arm64.tar.gz", "rid": "linux-arm64", "url": "u.tgz",
         "hash": hashlib.sha512(good_arm).hexdigest()}]}]}]}

    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)

        def staged(name: str, x64: bytes | None, arm: bytes | None) -> Path:
            root = base / name
            for rid, payload in (("linux-x64", x64), ("linux-arm64", arm)):
                (root / rid).mkdir(parents=True)
                if payload is not None:
                    (root / rid / COMPILER).write_bytes(payload)
            return root

        # The fix: both present, each for its own RID.
        expect("both RIDs correct", lambda: assert_staged(staged("ok", _pe(0xFD1D), _pe(0xD11D))), None)
        # Defects the old presence-only check let through or caught.
        expect("arm64 missing", lambda: assert_staged(staged("miss", _pe(0xFD1D), None)),
               "no Razor compiler staged for linux-arm64")
        expect("x64 copy staged as arm64", lambda: assert_staged(staged("swap", _pe(0xFD1D), _pe(0xFD1D))),
               "linux-arm64 Razor compiler is PE machine 0xfd1d")
        expect("not a PE file", lambda: assert_staged(staged("junk", b"not a dll" * 20, _pe(0xD11D))),
               "not a PE file")

        # The metadata lookup: the .tar.gz entry (never the .zip), an unreleased version named.
        def lookup_ok() -> None:
            url, digest = sdk_file(meta, version, "linux-arm64")
            if url != "u.tgz" or digest != hashlib.sha512(good_arm).hexdigest():
                raise StageError(f"picked {url}")
        expect("metadata names the tarball", lookup_ok, None)
        expect("version not released", lambda: sdk_file(meta, "10.0.998", "linux-arm64"),
               "SDK 10.0.998 is not in the release metadata")
        expect("rid absent", lambda: sdk_file(meta, version, "linux-riscv64"), "carries no linux-riscv64")

        # Extraction: only the generator directory, and only bytes whose digest matches.
        def extract_ok() -> None:
            dest = base / "extract"
            extract_generators(good_arm, sdk_file(meta, version, "linux-arm64")[1], version, dest)
            names = sorted(p.name for p in dest.iterdir())
            if names != sorted([COMPILER, "Other.dll"]):
                raise StageError(f"extracted {names}")
            if pe_machine(dest / COMPILER) != 0xD11D:
                raise StageError("extracted the wrong bytes")
        expect("extract the generators", extract_ok, None)
        expect("tampered tarball", lambda: extract_generators(good_arm + b"x", hashlib.sha512(good_arm).hexdigest(),
                                                              version, base / "tampered"),
               "does not match the release metadata")
        empty = _tarball("10.0.1", {COMPILER: _pe(0xD11D)})
        expect("tarball of another version", lambda: extract_generators(
            empty, hashlib.sha512(empty).hexdigest(), version, base / "other"), "holds nothing under")

    for failure in failures:
        print(f"::error::stage-razor-generators self-test: {failure}")
    if failures:
        return 1
    print("✓ stage-razor-generators self-test: 10 cases — each RID must hold the compiler built FOR "
          "it (missing, swapped and non-PE refused), the arm64 tarball is the metadata's .tar.gz for "
          "the host SDK version (an unreleased version and an absent RID named), and only the "
          "generator directory of a digest-matching tarball is extracted")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        stage(Path(argv[0]))
    except StageError as error:
        print(f"::error::{error}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
