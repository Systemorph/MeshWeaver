#!/usr/bin/env python3
"""THE IMAGE'S CLOSURE, AS DATA — the record main-cd writes for every image it publishes (#4066).

    python3 .github/scripts/image-closure-record.py build \
        --repository memex-portal-ai --digest sha256:<64 hex> --tag <staging> [--tag …] \
        --platform-commit <sha> --platform-version <version> --run-url <url> \
        <publish-dir> [<publish-dir> …]                 > image-closure.json
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
    b.add_argument("directories", nargs="+", type=Path)
    args = ap.parse_args(argv)
    try:
        sys.stdout.write(build(args.repository, args.digest, args.tag, args.platform_commit,
                               args.platform_version, args.run_url, args.directories))
    except Refused as e:
        print(f"::error::image-closure record refused: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
