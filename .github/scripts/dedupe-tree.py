#!/usr/bin/env python3
"""Ship a network's build output between jobs as a content-addressed store — one copy per unique file.

    python3 scripts/dedupe-tree.py pack   --out DIR  PATH...            # PATHs relative to --root (cwd)
    python3 scripts/dedupe-tree.py unpack --from DIR [--only PREFIX,...] # recreate under --root (cwd)
    python3 scripts/dedupe-tree.py --self-test

WHY THIS EXISTS. The portal-host lane builds a dependency network ONCE and tests it in parallel legs
on separate runners (core #4049 rule 8; the concurrent-lanes-on-one-runner shape was measured and
refused on run 34652827976). The legs need each suite's own `bin/` + `obj/` — and a suite's `bin/`
is ~750 MB of which nearly everything is the same platform and package DLLs every other suite of the
network also copies: 37 suites ≈ 28 GB raw, ~1.5 GB unique. A tar of that does not dedupe (zstd's
long window is 2 GB at most and the duplicates sit gigabytes apart), so the store does it by hand:
`pack` hashes every file, keeps ONE copy per digest under `store/` and writes `manifest.json` (path,
digest, mode — symlinks as their target); `unpack` recreates the paths, hard-linking from the store
where the filesystem allows and copying otherwise. `--only` restores a slice's prefixes alone.

Fail-loud rules: a path in the manifest whose digest is missing from the store is an error, never a
silent skip (a missing DLL is `--no-build`'s "Passed! 0 tests" trap one step later); `pack` refuses a
PATH that does not exist, and `unpack` refuses an `--only` prefix that matches nothing.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path


def _digest(path: Path) -> str:
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def pack(root: Path, out: Path, paths: list[str]) -> dict:
    store = out / "store"
    store.mkdir(parents=True, exist_ok=True)
    entries: list[dict] = []
    seen: set[str] = set()
    raw = 0
    for rel in paths:
        top = root / rel
        if not top.exists():
            sys.exit(f"✗ {rel} does not exist under {root} — nothing to pack, refusing to ship a hole")
        files = [top] if top.is_file() else sorted(p for p in top.rglob("*") if p.is_file() or p.is_symlink())
        for p in files:
            r = p.relative_to(root).as_posix()
            if p.is_symlink():
                entries.append({"path": r, "link": os.readlink(p)})
                continue
            d = _digest(p)
            st = p.stat()
            raw += st.st_size
            if d not in seen:
                seen.add(d)
                shutil.copyfile(p, store / d)
            entries.append({"path": r, "sha256": d, "mode": st.st_mode & 0o777})
    manifest = {"entries": entries, "files": len(entries), "unique": len(seen), "rawBytes": raw,
                "storedBytes": sum((store / d).stat().st_size for d in seen)}
    (out / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    return manifest


def unpack(root: Path, src: Path, only: list[str] | None) -> dict:
    manifest = json.loads((src / "manifest.json").read_text(encoding="utf-8"))
    store = src / "store"
    prefixes = [p.rstrip("/") + "/" for p in only] if only else None
    matched = {p: 0 for p in (prefixes or [])}
    restored = linked = 0
    # A hard link shares its blob's inode, so its mode is the blob's: the first link of a blob sets
    # it, and an entry that wants a DIFFERENT mode gets a copy (else a chmod would strip the
    # executable bit from every earlier link to the same bytes).
    blob_mode: dict[str, int] = {}
    for e in manifest["entries"]:
        if prefixes is not None:
            hit = [p for p in prefixes if e["path"].startswith(p)]
            if not hit:
                continue
            for p in hit:
                matched[p] += 1
        dest = root / e["path"]
        dest.parent.mkdir(parents=True, exist_ok=True)
        if dest.exists() or dest.is_symlink():
            dest.unlink()
        if "link" in e:
            os.symlink(e["link"], dest)
            restored += 1
            continue
        blob = store / e["sha256"]
        if not blob.is_file():
            sys.exit(f"✗ {e['path']} needs blob {e['sha256'][:12]}… which is not in the store — the artifact is incomplete; refusing to restore a tree with holes")
        if blob_mode.setdefault(e["sha256"], e["mode"]) == e["mode"]:
            try:
                os.link(blob, dest)
                linked += 1
            except OSError:
                shutil.copyfile(blob, dest)
        else:
            shutil.copyfile(blob, dest)
        os.chmod(dest, e["mode"])
        restored += 1
    for p, n in matched.items():
        if n == 0:
            sys.exit(f"✗ --only {p} matched nothing in the manifest ({manifest['files']} entries) — the slice would run on no build output")
    return {"restored": restored, "linked": linked, "files": manifest["files"]}


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'✓' if ok else '✗'} {name}" + (f": {detail}" if detail and not ok else ""))
        if not ok:
            failures.append(name)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp) / "ws"
        for suite in ("A.Test", "B.Test"):
            d = root / "src" / suite / "bin" / "Release"
            d.mkdir(parents=True)
            (d / "Shared.dll").write_bytes(b"shared" * 1000)          # identical in both suites
            (d / f"{suite}.dll").write_bytes(suite.encode() * 10)     # unique per suite
            (root / "src" / suite / "obj").mkdir()
            (root / "src" / suite / "obj" / "project.assets.json").write_text("{}")
        exe = root / "src" / "A.Test" / "bin" / "Release" / "A.Test"
        exe.write_bytes(b"apphost")
        exe.chmod(0o755)
        plain = root / "src" / "B.Test" / "bin" / "Release" / "apphost.copy"   # same bytes, NOT executable
        plain.write_bytes(b"apphost")
        plain.chmod(0o644)
        os.symlink("Shared.dll", root / "src" / "B.Test" / "bin" / "Release" / "link.dll")
        out = Path(tmp) / "out"
        m = pack(root, out, ["src/A.Test/bin", "src/A.Test/obj", "src/B.Test/bin", "src/B.Test/obj"])
        check("identical content is stored once", m["unique"] == 5 and m["files"] == 9, repr({k: v for k, v in m.items() if k != "entries"}))
        check("the store is smaller than the tree", m["storedBytes"] < m["rawBytes"])
        fresh = Path(tmp) / "fresh"
        fresh.mkdir()
        r = unpack(fresh, out, None)
        check("every entry is restored", r["restored"] == 9, repr(r))
        check("bytes round-trip", (fresh / "src/B.Test/bin/Release/Shared.dll").read_bytes() == b"shared" * 1000
              and (fresh / "src/A.Test/bin/Release/A.Test.dll").read_bytes() == b"A.Test" * 10)
        check("the executable bit survives", (fresh / "src/A.Test/bin/Release/A.Test").stat().st_mode & 0o111 != 0)
        check("…even when the same bytes are restored elsewhere WITHOUT it (no shared inode, no stripped bit)",
              (fresh / "src/A.Test/bin/Release/A.Test").stat().st_mode & 0o777 == 0o755
              and (fresh / "src/B.Test/bin/Release/apphost.copy").stat().st_mode & 0o777 == 0o644)
        check("a symlink is a symlink", os.path.islink(fresh / "src/B.Test/bin/Release/link.dll")
              and os.readlink(fresh / "src/B.Test/bin/Release/link.dll") == "Shared.dll")
        sliced = Path(tmp) / "sliced"
        sliced.mkdir()
        r = unpack(sliced, out, ["src/B.Test"])
        check("--only restores one slice and nothing else", (sliced / "src/B.Test/bin/Release/B.Test.dll").is_file()
              and not (sliced / "src/A.Test").exists() and r["restored"] == 5, repr(r))
        # Replace (unlink + write), never write THROUGH the restored path: it is a hard link to the
        # store blob, and writing through it would rewrite the store — the reason a restored tree is
        # read-only by contract and every test output lands in NEW files (trx, test-logs).
        (fresh / "src/A.Test/bin/Release/A.Test.dll").unlink()
        (fresh / "src/A.Test/bin/Release/A.Test.dll").write_bytes(b"stale")
        unpack(fresh, out, ["src/A.Test"])
        check("a second unpack overwrites a stale file", (fresh / "src/A.Test/bin/Release/A.Test.dll").read_bytes() == b"A.Test" * 10)
        for name, fn in (("a missing PATH refuses to pack", lambda: pack(root, Path(tmp) / "o2", ["src/Missing"])),
                         ("an --only prefix that matches nothing is an error", lambda: unpack(Path(tmp) / "f2", out, ["src/Nope"]))):
            try:
                fn()
                check(name, False, "did not exit")
            except SystemExit as ex:
                check(name, "refusing" in str(ex) or "matched nothing" in str(ex), str(ex))
        (out / "store" / m["entries"][0]["sha256"]).unlink()
        try:
            unpack(Path(tmp) / "f3", out, None)
            check("a blob missing from the store is an error, never a hole", False, "did not exit")
        except SystemExit as ex:
            check("a blob missing from the store is an error, never a hole", "holes" in str(ex), str(ex))
    # The CLI shape the workflow uses — options BEFORE the positionals — must parse every PATH.
    a = parse(["pack", "--out", "o", "src/A.Test/bin", "src/A.Test/obj", "src/B.Test/bin"])
    check("the workflow's `pack --out DIR PATH...` keeps every PATH", a.command == "pack" and a.out == "o"
          and a.paths == ["src/A.Test/bin", "src/A.Test/obj", "src/B.Test/bin"], repr(vars(a)))
    a = parse(["unpack", "--from", "o", "--only", "src/B.Test"])
    check("the workflow's `unpack --from DIR --only P` parses", a.command == "unpack" and a.src == "o"
          and a.only == "src/B.Test" and a.paths == [], repr(vars(a)))
    if failures:
        print(f"\n::error title=dedupe-tree self-test failed::{len(failures)} case(s): {', '.join(failures)}")
        return 1
    print("\n✓ dedupe-tree self-test: pack stores each content once, unpack restores bytes, modes, links and slices, and every hole is loud")
    return 0


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("command", nargs="?", choices=["pack", "unpack"])
    ap.add_argument("paths", nargs="*")
    ap.add_argument("--out", help="pack: the store directory to write")
    ap.add_argument("--from", dest="src", help="unpack: the store directory to read")
    ap.add_argument("--only", help="unpack: comma-separated path prefixes to restore (default: everything)")
    ap.add_argument("--root", default=".", help="the tree PATHs are relative to (default: cwd)")
    ap.add_argument("--self-test", action="store_true", dest="self_test")
    return ap


def parse(argv: list[str]) -> argparse.Namespace:
    # The workflow calls `pack --out DIR PATH...`: with plain parse_args a `*` positional after an
    # option is consumed EMPTY before `--out` and every PATH after it is "unrecognized arguments"
    # (run 34667013624, job 103481156005 — the build leg packed nothing and three test legs found
    # no artifact). parse_intermixed_args lets options and positionals interleave.
    return build_parser().parse_intermixed_args(argv)


def main() -> int:
    ap = build_parser()
    args = parse(sys.argv[1:])
    if args.self_test:
        return self_test()
    root = Path(args.root).resolve()
    if args.command == "pack":
        if not args.out or not args.paths:
            ap.error("pack needs --out and at least one PATH")
        m = pack(root, Path(args.out), args.paths)
        print(f"packed {m['files']} file(s), {m['unique']} unique: {m['rawBytes'] / 1e9:.2f} GB raw → {m['storedBytes'] / 1e9:.2f} GB stored")
        return 0
    if args.command == "unpack":
        if not args.src:
            ap.error("unpack needs --from")
        only = [p for p in (args.only or "").split(",") if p]
        r = unpack(root, Path(args.src), only or None)
        print(f"restored {r['restored']} of {r['files']} file(s) ({r['linked']} hard-linked)")
        return 0
    ap.error("command required: pack or unpack")
    return 2


if __name__ == "__main__":
    sys.exit(main())
