#!/usr/bin/env python3
"""nuget-shelf — the runner-side twin of the mesh's FileSystemNuGetPackageCache.

    python3 nuget-shelf.py hydrate --shelf /nuget-shelf [--packages ~/.nuget/packages]
    python3 nuget-shelf.py save    --shelf /nuget-shelf [--packages ~/.nuget/packages]
    python3 nuget-shelf.py --self-test

A shared WRITABLE NuGet global-packages folder over a network share does not work: NuGet relies on
local file locks for concurrent extraction, and on 2026-09-13 the fleet's runner set failed twice on
exactly that (Memex#335/#338 — a root-owned parent, then reads denied under concurrent restores).
The mesh solved the same problem for `#r "nuget:"` restores with `MeshWeaver.NuGet`'s
FileSystemNuGetPackageCache (maintainer: "we had a package restore over the mesh. can we use this?
this is thread safe"): the share holds ONE IMMUTABLE ZIP per package version,
`{shelf}/{id-lower}/{version}.zip`, written to a unique temp file and atomically renamed (a loser of
the race deletes its temp; an existing zip is never rewritten), and every consumer EXTRACTS into its
OWN local packages folder. A reader never opens a half-written file; writers never contend.

  hydrate  — before `dotnet restore`: every `{id}/{version}.zip` on the shelf whose
             `{packages}/{id}/{version}/.nupkg.metadata` is absent locally is extracted there
             (parallel; zip-slip guarded). Restore then finds the package complete and downloads
             nothing for it. Missing or unreadable zips are skipped with a count — never fatal.
  save     — after restore/build: every `{packages}/{id}/{version}/` that carries
             `.nupkg.metadata` (NuGet's "extraction complete" marker) and has no zip on the shelf
             is zipped to `{shelf}/{id}/{version}.zip.<uuid>.tmp` and renamed into place.

The shelf's layout and the atomic-rename protocol are the mesh's, byte for byte in intent, so the
build instance and the runners may share one volume. Stdlib only; exit 0 on partial success (counts
printed), exit 2 on a usage error.
"""
from __future__ import annotations

import argparse
import os
import sys
import tempfile
import uuid
import zipfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

MARKER = ".nupkg.metadata"


def default_packages() -> Path:
    env = os.environ.get("NUGET_PACKAGES")
    return Path(env) if env else Path.home() / ".nuget" / "packages"


def shelf_zips(shelf: Path) -> list[tuple[str, str, Path]]:
    out: list[tuple[str, str, Path]] = []
    if not shelf.is_dir():
        return out
    for id_dir in sorted(p for p in shelf.iterdir() if p.is_dir()):
        for z in sorted(id_dir.glob("*.zip")):
            out.append((id_dir.name, z.stem, z))
    return out


def extract(zip_path: Path, target: Path) -> bool:
    """Extract one archive into `target` (created); zip-slip guarded. False on any failure, target removed."""
    tmp = target.parent / f".{target.name}.hydrating-{uuid.uuid4().hex}"
    try:
        tmp.mkdir(parents=True, exist_ok=False)
        root = tmp.resolve()
        with zipfile.ZipFile(zip_path) as archive:
            for entry in archive.infolist():
                if entry.is_dir() or not entry.filename:
                    continue
                dest = (root / entry.filename).resolve()
                if not str(dest).startswith(str(root) + os.sep):
                    continue
                dest.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(entry) as src, open(dest, "wb") as dst:
                    while True:
                        chunk = src.read(1 << 20)
                        if not chunk:
                            break
                        dst.write(chunk)
        if not (tmp / MARKER).exists():
            raise ValueError("archive carries no .nupkg.metadata — not a complete package")
        os.rename(tmp, target)  # atomic on the same filesystem; a racing hydrate of the same package loses and cleans up
        return True
    except (FileExistsError, OSError, zipfile.BadZipFile, ValueError):
        return False
    finally:
        if tmp.exists():
            _rmtree(tmp)
        if not target.exists():
            try:
                target.parent.rmdir()  # a failed hydrate leaves no empty {id}/ behind
            except OSError:
                pass


def _rmtree(p: Path) -> None:
    for child in sorted(p.rglob("*"), reverse=True):
        try:
            child.rmdir() if child.is_dir() else child.unlink()
        except OSError:
            pass
    try:
        p.rmdir()
    except OSError:
        pass


def hydrate(shelf: Path, packages: Path, workers: int = 8) -> tuple[int, int, int]:
    """`(hydrated, already_local, failed)`."""
    todo = [(pid, ver, z) for pid, ver, z in shelf_zips(shelf) if not (packages / pid / ver / MARKER).exists()]
    already = len(shelf_zips(shelf)) - len(todo)
    packages.mkdir(parents=True, exist_ok=True)
    with ThreadPoolExecutor(max_workers=workers) as pool:
        results = list(pool.map(lambda t: extract(t[2], packages / t[0] / t[1]), todo))
    return sum(1 for r in results if r), already, sum(1 for r in results if not r)


def save(shelf: Path, packages: Path, workers: int = 4) -> tuple[int, int, int]:
    """`(saved, already_shelved, skipped_incomplete)`."""
    if not packages.is_dir():
        return 0, 0, 0
    todo: list[tuple[str, str, Path]] = []
    already = incomplete = 0
    for id_dir in sorted(p for p in packages.iterdir() if p.is_dir() and not p.name.startswith(".")):
        for ver_dir in sorted(p for p in id_dir.iterdir() if p.is_dir()):
            if not (ver_dir / MARKER).exists():
                incomplete += 1
                continue
            if (shelf / id_dir.name.lower() / f"{ver_dir.name}.zip").exists():
                already += 1
                continue
            todo.append((id_dir.name.lower(), ver_dir.name, ver_dir))

    def one(t: tuple[str, str, Path]) -> bool:
        pid, ver, src = t
        target = shelf / pid / f"{ver}.zip"
        target.parent.mkdir(parents=True, exist_ok=True)
        tmp = target.parent / f"{ver}.zip.{uuid.uuid4().hex}.tmp"
        try:
            with zipfile.ZipFile(tmp, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
                for file in sorted(p for p in src.rglob("*") if p.is_file()):
                    archive.write(file, file.relative_to(src).as_posix())
            try:
                os.link(tmp, target)  # fails when a racing writer got there first — never overwrite
                return True
            except FileExistsError:
                return False
            except OSError:
                # a share that refuses hard links: rename is still atomic and refuses nothing, so
                # check first — the window is small and the bytes are identical either way
                if target.exists():
                    return False
                os.rename(tmp, target)
                return True
        except OSError:
            return False
        finally:
            if tmp.exists():
                try:
                    tmp.unlink()
                except OSError:
                    pass

    with ThreadPoolExecutor(max_workers=workers) as pool:
        results = list(pool.map(one, todo))
    return sum(1 for r in results if r), already + sum(1 for r in results if not r), incomplete


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'✓' if ok else '✗'} {name}" + ("" if ok else f" — {detail}"))
        if not ok:
            failures.append(name)

    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        pk, shelf, pk2 = root / "pk", root / "shelf", root / "pk2"
        (pk / "newtonsoft.json" / "13.0.3" / "lib").mkdir(parents=True)
        (pk / "newtonsoft.json" / "13.0.3" / "lib" / "n.dll").write_bytes(b"x" * 100)
        (pk / "newtonsoft.json" / "13.0.3" / MARKER).write_text("{}")
        (pk / "half" / "1.0.0").mkdir(parents=True)  # no marker: extraction never completed
        (pk / "half" / "1.0.0" / "a.txt").write_text("partial")
        saved, already, incomplete = save(shelf, pk)
        check("save: one complete package zipped, the incomplete one skipped", (saved, already, incomplete) == (1, 0, 1), repr((saved, already, incomplete)))
        check("the shelf holds {id}/{version}.zip and no temp file", (shelf / "newtonsoft.json" / "13.0.3.zip").exists() and not list(shelf.rglob("*.tmp")))
        saved2, already2, _ = save(shelf, pk)
        check("save again: nothing rewritten, counted as already shelved", (saved2, already2) == (0, 1), repr((saved2, already2)))
        hydrated, local, failed = hydrate(shelf, pk2)
        check("hydrate into an empty folder: one package extracted, marker present", (hydrated, local, failed) == (1, 0, 0) and (pk2 / "newtonsoft.json" / "13.0.3" / MARKER).exists() and (pk2 / "newtonsoft.json" / "13.0.3" / "lib" / "n.dll").read_bytes() == b"x" * 100, repr((hydrated, local, failed)))
        hydrated2, local2, _ = hydrate(shelf, pk2)
        check("hydrate again: already local, nothing extracted", (hydrated2, local2) == (0, 1), repr((hydrated2, local2)))
        bad = shelf / "broken" / "1.0.0.zip"; bad.parent.mkdir(); bad.write_bytes(b"not a zip")
        h3, _, f3 = hydrate(shelf, pk2)
        check("a broken zip is skipped and counted, never fatal, nothing half-extracted", (h3, f3) == (0, 1) and not (pk2 / "broken").exists() and not list(pk2.glob(".*hydrating*")), repr((h3, f3)))
        nomarker = shelf / "nomarker" / "1.0.0.zip"; nomarker.parent.mkdir()
        with zipfile.ZipFile(nomarker, "w") as z:
            z.writestr("lib/x.dll", "y")
        h4, _, f4 = hydrate(shelf, pk2)
        check("a zip without .nupkg.metadata is refused (incomplete packages never reach a restore)", f4 >= 1 and not (pk2 / "nomarker").exists(), repr((h4, f4)))
    if failures:
        print(f"\n✗ nuget-shelf self-test: {len(failures)} failure(s)")
        return 1
    print("✓ nuget-shelf self-test: save (atomic, never rewritten, incomplete skipped), hydrate (idempotent, zip-slip and broken/incomplete archives refused)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("verb", nargs="?", choices=["hydrate", "save"])
    ap.add_argument("--shelf", default=os.environ.get("NUGET_SHELF", "/nuget-shelf"))
    ap.add_argument("--packages", default=None, help="the local global packages folder (default $NUGET_PACKAGES or ~/.nuget/packages)")
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--self-test", action="store_true", dest="self_test")
    args = ap.parse_args()
    if args.self_test:
        return self_test()
    if not args.verb:
        ap.error("verb required: hydrate | save")
    shelf, packages = Path(args.shelf), Path(args.packages) if args.packages else default_packages()
    if not shelf.is_dir():
        print(f"nuget-shelf: no shelf at {shelf} — nothing to {args.verb} (a runner without the volume restores from the feed)")
        return 0
    if args.verb == "hydrate":
        h, l, f = hydrate(shelf, packages, args.workers)
        print(f"nuget-shelf hydrate: {h} package(s) extracted from {shelf} into {packages}, {l} already local, {f} skipped (unreadable or incomplete)")
    else:
        s, a, i = save(shelf, packages, max(1, args.workers // 2))
        print(f"nuget-shelf save: {s} package(s) shelved to {shelf}, {a} already there, {i} skipped (extraction incomplete)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
