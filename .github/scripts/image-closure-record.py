#!/usr/bin/env python3
"""THE IMAGE'S CLOSURE, AS DATA — the record main-cd writes for every image it publishes (#4066).

    python3 .github/scripts/image-closure-record.py build \
        --repository memex-portal-ai --digest sha256:<64 hex> --tag <staging> [--tag …] \
        --platform-commit <sha> --platform-version <version> --run-url <url> \
        [--image-index index.json] <publish-dir> [<publish-dir> …]   > image-closure.json
    python3 .github/scripts/image-closure-record.py --self-test

WHY (policy `image-closure-as-mesh-data`, Doc/Architecture/ImageClosureAsMeshData)
---------------------------------------------------------------------------------
"What is in /app" was answered by `check-platform-reference-set.sh` and nothing else — a shell
assertion that leaves no record, so every later reader (retention, a satellite's compile, an
operator) re-derives it with `docker run … ls /app`. This script writes down EXACTLY what that
assertion just accepted: it runs over the same publish directories, AFTER the assertion passed,
and emits one `image-closure` record per image, keyed by the image's DIGEST (stable across retag
and promotion — tags are an attribute, never the identity).

The record carries its own denominator: every platform (RID) states `fileCount`, and the C# reader
(`ImageClosureRecord.TryParse`) refuses a record whose `files` list disagrees with it, so a
truncated record can never read as a smaller closure.

FAIL CLOSED, NAMING WHY — never a partial record
------------------------------------------------
  * a digest that is not `sha256:<64 lowercase hex>`        ⇒ refused
  * a publish directory that is missing, holds no files, or carries no
    `meshweaver-surface.manifest` (not the bytes that become /app) ⇒ refused
  * a directory whose RID cannot be read off its path, or two directories with one RID ⇒ refused
  * framework identity files that disagree across platforms ⇒ refused (one image, one identity)
  * with --image-index (the image's own manifest list, `docker buildx imagetools inspect --raw`):
    the directories are SELECTED BY THE IMAGE — exactly one per platform the index names, and a
    platform with no directory (or two) is refused. A directory the image does not carry is not
    recorded: a multi-RID `PublishContainer` also lays down a RID-less outer
    `bin/Release/<tfm>/publish`, which holds the surface manifest but is in no image layer (main-cd
    run 38023872356 refused on exactly that directory, #6402).
  * a body over the inbox cap (1 MiB, WebhookInbox.MaxBodyBytes) ⇒ refused — the inbox would 413
    it, so saying so here names the cause instead of an HTTP status three steps later.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
from pathlib import Path

EVENT = "image-closure"
# WebhookInbox.MaxBodyBytes in src/MeshWeaver.Graph/Configuration/WebhookEventNodeType.cs.
MAX_BODY_BYTES = 1024 * 1024
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")
RID = re.compile(r"(?:^|/)(linux-(?:x64|arm64|musl-x64|musl-arm64)|win-(?:x64|arm64)|osx-(?:x64|arm64))(?:/|$)")
SURFACE_MANIFEST = "meshweaver-surface.manifest"
IDENTITY_FILE = "meshweaver-framework.identity"


class Refused(Exception):
    """A record that must not be written; the message names why."""


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def rid_of(directory: Path) -> str:
    m = RID.search(directory.as_posix())
    if not m:
        raise Refused(
            f"cannot read a runtime identifier off '{directory}' — a closure record is per platform, "
            "and a directory whose platform is unknown cannot be filed under one")
    return m.group(1)


# OCI / Docker platform (os/arch) → .NET runtime identifier. An entry not listed here is refused,
# never guessed: a record filed under the wrong platform is worse than no record.
OCI_TO_RID = {
    ("linux", "amd64"): "linux-x64",
    ("linux", "arm64"): "linux-arm64",
    ("windows", "amd64"): "win-x64",
    ("windows", "arm64"): "win-arm64",
}


def image_rids(index: dict) -> set[str]:
    """The platforms an image index carries, as RIDs. Attestation entries (`unknown/unknown`) are not
    platforms and are skipped; anything else unmappable is refused."""
    manifests = index.get("manifests") if isinstance(index, dict) else None
    if not isinstance(manifests, list) or not manifests:
        raise Refused("--image-index is not a manifest list (no `manifests`) — cannot read the image's platforms")
    rids: set[str] = set()
    for m in manifests:
        plat = (m or {}).get("platform") or {}
        os_, arch = plat.get("os"), plat.get("architecture")
        if os_ == "unknown" or arch == "unknown":
            continue
        rid = OCI_TO_RID.get((os_, arch))
        if rid is None:
            raise Refused(f"the image carries platform {os_}/{arch}, which maps to no known runtime identifier")
        rids.add(rid)
    if not rids:
        raise Refused("--image-index names no platform (only attestations) — nothing to record")
    return rids


def select_for_image(rids: set[str], directories: list[Path]) -> list[Path]:
    """Exactly one publish directory per platform the image carries; directories the image does not
    carry (the RID-less outer publish, a stale RID) are left out."""
    chosen: dict[str, list[Path]] = {r: [] for r in rids}
    for d in directories:
        m = RID.search(d.as_posix())
        if m and m.group(1) in chosen:
            chosen[m.group(1)].append(d)
    for rid in sorted(rids):
        if len(chosen[rid]) != 1:
            raise Refused(
                f"the image carries {rid} but {len(chosen[rid])} publish directories resolve to it "
                f"({', '.join(str(p) for p in chosen[rid]) or 'none'}) — one platform, one closure")
    return [chosen[r][0] for r in sorted(rids)]


def platform_of(directory: Path) -> tuple[dict, str | None]:
    if not directory.is_dir():
        raise Refused(f"publish directory '{directory}' does not exist — nothing to record")
    manifest = directory / SURFACE_MANIFEST
    if not manifest.is_file():
        raise Refused(
            f"'{directory}' carries no {SURFACE_MANIFEST} — it is not the publish output that becomes "
            "/app, so a record of it would describe the wrong bytes")
    files = []
    for root, _dirs, names in os.walk(directory):
        for name in names:
            p = Path(root) / name
            if p.is_symlink() or not p.is_file():
                continue
            files.append({
                "path": p.relative_to(directory).as_posix(),
                "sha256": sha256_file(p),
                "bytes": p.stat().st_size,
            })
    files.sort(key=lambda f: f["path"])
    if not files:
        raise Refused(f"'{directory}' holds no files")
    identity_path = directory / IDENTITY_FILE
    identity = identity_path.read_text(encoding="utf-8").strip() if identity_path.is_file() else None
    return ({
        "rid": rid_of(directory),
        "fileCount": len(files),
        "manifestSha256": sha256_file(manifest),
        "files": files,
    }, identity or None)


def build(repository: str, digest: str, tags: list[str], platform_commit: str,
          platform_version: str, run_url: str, directories: list[Path]) -> str:
    if not repository.strip():
        raise Refused("--repository is empty")
    if not DIGEST.match(digest or ""):
        raise Refused(f"digest '{digest}' is not sha256:<64 lowercase hex> — the record's identity IS the digest")
    if not directories:
        raise Refused("no publish directory given — a closure of nothing is not a closure")
    platforms, identities = [], set()
    for d in directories:
        platform, identity = platform_of(d)
        if any(p["rid"] == platform["rid"] for p in platforms):
            raise Refused(f"two publish directories resolve to platform {platform['rid']} — one image, one closure per platform")
        platforms.append(platform)
        if identity:
            identities.add(identity)
    if len(identities) > 1:
        raise Refused(f"the platforms state different framework identities {sorted(identities)} — one image, one identity")
    platforms.sort(key=lambda p: p["rid"])
    record = {
        "event": EVENT,
        "repository": repository.strip(),
        "digest": digest,
        "tags": sorted({t.strip() for t in tags if t and t.strip()}),
        "platformCommit": platform_commit.strip(),
        "platformVersion": platform_version.strip(),
        "frameworkIdentity": next(iter(identities), None),
        "runUrl": run_url.strip(),
        "platforms": platforms,
    }
    body = json.dumps(record, separators=(",", ":"), sort_keys=False)
    size = len(body.encode("utf-8"))
    if size > MAX_BODY_BYTES:
        raise Refused(
            f"the record is {size} bytes, over the inbox cap of {MAX_BODY_BYTES} (WebhookInbox.MaxBodyBytes) — "
            "the inbox would refuse it with 413; nothing is written partially")
    return body


# ─────────────────────────────────────────── self-test ───────────────────────────────────────────
def _self_test() -> int:
    failures: list[str] = []

    def check(name: str, ok: bool) -> None:
        print(("  ✓ " if ok else "  ✗ ") + name)
        if not ok:
            failures.append(name)

    def refused(fn) -> str | None:
        try:
            fn()
        except Refused as e:
            return str(e)
        return None

    digest = "sha256:" + "ab" * 32
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        x64 = root / "bin/Release/net10.0/linux-x64/publish"
        arm = root / "bin/Release/net10.0/linux-arm64/publish"
        for d, native in ((x64, b"x64"), (arm, b"arm64")):
            (d / "modules/Foo").mkdir(parents=True)
            (d / SURFACE_MANIFEST).write_text("MeshWeaver.Messaging.Hub\n")
            (d / IDENTITY_FILE).write_text("c003e001\n")
            (d / "MeshWeaver.Messaging.Hub.dll").write_bytes(b"hub")
            (d / "libnative.so").write_bytes(native)
            (d / "modules/Foo/Foo.dll").write_bytes(b"foo")

        body = build("memex-portal-ai", digest, ["stg-1", "stg-1", " "], "abc", "3.1.42", "https://run",
                     [x64, arm])
        rec = json.loads(body)
        print("the record, in both directions:")
        check("event is image-closure", rec["event"] == EVENT)
        check("the digest is the identity, verbatim", rec["digest"] == digest)
        check("tags are deduplicated and blanks dropped", rec["tags"] == ["stg-1"])
        check("a version string is carried opaque (no -ci assumed)", rec["platformVersion"] == "3.1.42")
        check("framework identity read from the published file", rec["frameworkIdentity"] == "c003e001")
        check("one entry per platform, ordered", [p["rid"] for p in rec["platforms"]] == ["linux-arm64", "linux-x64"])
        x = next(p for p in rec["platforms"] if p["rid"] == "linux-x64")
        check("fileCount is the denominator and equals the list", x["fileCount"] == len(x["files"]) == 5)
        check("nested module files are in the closure",
              any(f["path"] == "modules/Foo/Foo.dll" for f in x["files"]))
        hub = next(f for f in x["files"] if f["path"] == "MeshWeaver.Messaging.Hub.dll")
        check("per-file sha256 is the content hash", hub["sha256"] == hashlib.sha256(b"hub").hexdigest())
        check("per-file byte count", hub["bytes"] == 3)
        a = next(p for p in rec["platforms"] if p["rid"] == "linux-arm64")
        check("platform-specific bytes differ per platform",
              next(f for f in a["files"] if f["path"] == "libnative.so")["sha256"]
              != next(f for f in x["files"] if f["path"] == "libnative.so")["sha256"])

        print("refusals (each must REFUSE — the negative controls):")
        check("a tag-shaped digest is refused",
              refused(lambda: build("r", "3.1.42", [], "c", "v", "u", [x64])) is not None)
        check("an uppercase digest is refused",
              refused(lambda: build("r", digest.upper().replace("SHA256", "sha256"), [], "c", "v", "u", [x64])) is not None)
        check("no directory is refused",
              refused(lambda: build("r", digest, [], "c", "v", "u", [])) is not None)
        check("a missing directory is refused",
              refused(lambda: build("r", digest, [], "c", "v", "u", [root / "nope/linux-x64/publish"])) is not None)
        bare = root / "bare/linux-x64/publish"
        bare.mkdir(parents=True)
        (bare / "a.dll").write_bytes(b"a")
        check("a directory without the surface manifest is refused",
              refused(lambda: build("r", digest, [], "c", "v", "u", [bare])) is not None)
        norid = root / "norid/publish"
        norid.mkdir(parents=True)
        (norid / SURFACE_MANIFEST).write_text("x")
        check("a directory with no readable platform is refused",
              refused(lambda: build("r", digest, [], "c", "v", "u", [norid])) is not None)
        check("two directories of one platform are refused",
              refused(lambda: build("r", digest, [], "c", "v", "u", [x64, x64])) is not None)
        (arm / IDENTITY_FILE).write_text("c003e002\n")
        check("disagreeing framework identities are refused",
              refused(lambda: build("r", digest, [], "c", "v", "u", [x64, arm])) is not None)
        (arm / IDENTITY_FILE).write_text("c003e001\n")

        print("selection by the image's own index (#6402):")
        index = {"manifests": [
            {"platform": {"os": "linux", "architecture": "amd64"}},
            {"platform": {"os": "linux", "architecture": "arm64"}},
            {"platform": {"os": "unknown", "architecture": "unknown"}},
        ]}
        outer = root / "bin/Release/net10.0/publish"
        outer.mkdir(parents=True)
        (outer / SURFACE_MANIFEST).write_text("x")
        (outer / "a.dll").write_bytes(b"a")
        check("negative control: the RID-less outer publish refuses an unselected build",
              refused(lambda: build("r", digest, [], "c", "v", "u", [arm, x64, outer])) is not None)
        picked = select_for_image(image_rids(index), [arm, x64, outer])
        check("the outer publish is left out, one directory per image platform", picked == [arm, x64])
        check("…and the selected set builds",
              refused(lambda: build("r", digest, [], "c", "v", "u", picked)) is None)
        check("an image platform with no publish directory is refused",
              refused(lambda: select_for_image(image_rids(index), [x64, outer])) is not None)
        check("an unmappable image platform is refused",
              refused(lambda: image_rids({"manifests": [{"platform": {"os": "linux", "architecture": "s390x"}}]})) is not None)
        check("a single manifest (no list) is refused",
              refused(lambda: image_rids({"layers": []})) is not None)
        check("an index of attestations only is refused",
              refused(lambda: image_rids({"manifests": [{"platform": {"os": "unknown", "architecture": "unknown"}}]})) is not None)

        big = root / "big/linux-x64/publish"
        big.mkdir(parents=True)
        (big / SURFACE_MANIFEST).write_text("x")
        for i in range(12000):
            (big / f"f{i:05d}-{'p' * 40}.dll").write_bytes(b"")
        why = refused(lambda: build("r", digest, [], "c", "v", "u", [big]))
        check("a body over the inbox cap is refused, naming the cap", why is not None and str(MAX_BODY_BYTES) in why)
        check("…and the control: the same tree minus the excess is accepted",
              refused(lambda: build("r", digest, [], "c", "v", "u", [x64])) is None)

    print(f"{'FAILED' if failures else 'OK'}: {len(failures)} failure(s)")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if argv[:1] == ["--self-test"]:
        return _self_test()
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    b = sub.add_parser("build")
    b.add_argument("--repository", required=True)
    b.add_argument("--digest", required=True)
    b.add_argument("--tag", action="append", default=[])
    b.add_argument("--platform-commit", required=True)
    b.add_argument("--platform-version", required=True)
    b.add_argument("--run-url", required=True)
    b.add_argument("--image-index", type=Path, default=None,
                   help="the image's manifest list (imagetools inspect --raw); selects one directory per platform")
    b.add_argument("directories", nargs="+", type=Path)
    args = ap.parse_args(argv)
    try:
        directories = args.directories
        if args.image_index is not None:
            try:
                index = json.loads(args.image_index.read_text(encoding="utf-8"))
            except (OSError, ValueError) as e:
                raise Refused(f"cannot read --image-index '{args.image_index}': {e}") from e
            directories = select_for_image(image_rids(index), directories)
        sys.stdout.write(build(args.repository, args.digest, args.tag, args.platform_commit,
                               args.platform_version, args.run_url, directories))
    except Refused as e:
        print(f"::error::image-closure record refused: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
