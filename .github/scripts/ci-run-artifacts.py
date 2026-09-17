#!/usr/bin/env python3
"""Named CI artifacts on an existing shared runner mount, without GitHub storage.

This layer uses ci-artifact-store.py's FileStore for archive transport. It supports only
file: stores: an unsupported backend or an unavailable mount fails, never falls back.
Outer actions choose GitHub storage for callers that have not opted into their own store.

Each repository/run/name owns an independent directory under named/ (separate from the older
runs/ prefix's blanket two-day cleanup). Archives are content-addressed;
the manifest for an attempt is published atomically only after the archive is complete.
Downloads choose the latest published attempt <= the requested attempt, because rerunning
failed jobs does not rerun their successful producers. Names never cross run boundaries.
An explicit overwrite replaces only that attempt's manifest; previous archive bytes remain
immutable. The share's cleanup owns deletion; readers enforce the recorded expiry.

Upload paths are newline-separated literals/globs, with ! exclusions. A directory uploads its
contents, a file uploads its basename, and a glob preserves paths below its first wildcard.
Multiple positive paths use their least common search ancestor; exclusions do not move it.
Only regular files are supported. Symlinks and special files are refused. Downloads verify
the complete archive digest and inventory before writing files and never extract tar links.
No API token, cloud credential, or GitHub API call is used here.
"""
from __future__ import annotations

import argparse
import contextlib
import fcntl
import fnmatch
import glob
import gzip
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import sys
import tarfile
import tempfile
import time


SPEC = importlib.util.spec_from_file_location(
    "ci_artifact_store", Path(__file__).with_name("ci-artifact-store.py"))
STORE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(STORE)
Red = STORE.Red
SCHEMA = "meshweaver.ci-run-artifact/1"
NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._ ()+-]{0,239}$")
REPO_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$")
DIGEST_RE = re.compile(r"^[a-f0-9]{64}$")


def boolean(value: str | bool) -> bool:
    """Accept action inputs' true/false strings as well as bare CLI switches."""
    if isinstance(value, bool):
        return value
    if value.lower() in ("true", "false"):
        return value.lower() == "true"
    raise argparse.ArgumentTypeError("expected true or false")


def safe_name(name: str) -> str:
    if not NAME_RE.fullmatch(name) or name in (".", ".."):
        raise Red("artifact name must be 1-240 plain characters, without separators or wildcards")
    return name


def safe_member(name: str) -> str:
    parts = PurePosixPath(name).parts
    if (not name or name.startswith("/") or "\\" in name or "\x00" in name
            or any(p in (".", "..") for p in name.split("/"))
            or any(":" in p for p in parts)):
        raise Red("archive contains an unsafe relative path")
    return name


def no_links(path: Path, stop: Path) -> None:
    """Reject a link in a path being read or written, including the final component."""
    current = path
    while current != stop:
        if current.is_symlink():
            raise Red("artifact path contains a symbolic link")
        if current == current.parent:
            raise Red("artifact path escaped its root")
        current = current.parent


@contextlib.contextmanager
def attempt_lock(folder: Path, attempt: int, blocking: bool = True):
    """Coordinate publishers and expiry cleanup through a stable, never-pruned lock inode.

    The shared mount must support cross-runner flock; infrastructure proves that before enabling
    its pruner. Cleanup uses the same path with LOCK_NB and rereads expiry while holding the lock.
    No polling, lock-file deletion, or stale-lock heuristic is involved: the OS releases the lock.
    """
    locks = folder / ".locks"
    lock = locks / f"{attempt}.lock"
    no_links(lock, folder)
    locks.mkdir(exist_ok=True)
    with lock.open("a+b") as stream:
        fcntl.flock(stream.fileno(), fcntl.LOCK_EX | (0 if blocking else fcntl.LOCK_NB))
        try:
            yield
        finally:
            fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def lines(values: list[str]) -> list[str]:
    return [line.strip() for value in values for line in value.splitlines() if line.strip()]


def absolute(pattern: str, cwd: Path) -> str:
    return os.path.abspath(os.path.join(cwd, os.path.expanduser(pattern)))


def search_root(pattern: str) -> Path:
    parts = Path(pattern).parts
    for i, part in enumerate(parts):
        if glob.has_magic(part):
            return Path(*parts[:i])
    path = Path(pattern)
    return path if path.is_dir() else path.parent


def collect(paths: list[str], excludes: list[str], include_hidden: bool,
            cwd: Path) -> dict[str, Path]:
    """Return archive-relative names with GHA's common search ancestor semantics."""
    positive, negative = [], list(excludes)
    for pattern in lines(paths):
        (negative if pattern.startswith("!") else positive).append(
            pattern[1:] if pattern.startswith("!") else pattern)
    if not positive:
        raise Red("upload needs at least one positive path")
    positive = [absolute(p, cwd) for p in positive]
    negative = [absolute(p, cwd) for p in lines(negative)]
    root = Path(os.path.commonpath([str(search_root(p)) for p in positive]))
    excluded: set[Path] = set()
    for pattern in negative:
        for match in glob.glob(pattern, recursive=True, include_hidden=True):
            path = Path(match)
            excluded.add(path)
            if path.is_dir() and not path.is_symlink():
                excluded.update(path.rglob("*"))
    selected: dict[str, Path] = {}
    for pattern in positive:
        for match in sorted(glob.glob(pattern, recursive=True, include_hidden=include_hidden)):
            path = Path(match)
            candidates = [path]
            if path.is_dir() and not path.is_symlink():
                candidates = sorted(path.rglob("*"))
            for candidate in candidates:
                if candidate in excluded:
                    continue
                relative = candidate.relative_to(root).as_posix()
                if relative == "." and candidate.is_symlink():
                    raise Red("upload root is a symbolic link")
                if not include_hidden and any(p.startswith(".") for p in Path(relative).parts):
                    continue
                no_links(candidate, root.parent)
                if candidate.is_dir():
                    continue
                if not candidate.is_file():
                    raise Red("upload matched a special file; only regular files are supported")
                selected[safe_member(relative)] = candidate
    return dict(sorted(selected.items()))


def archive_files(files: dict[str, Path], archive: Path, compression: int) -> list[dict]:
    inventory = []
    with archive.open("wb") as raw, gzip.GzipFile(
            filename="", mode="wb", fileobj=raw, compresslevel=compression, mtime=0) as gz:
        with tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as tar:
            for name, source in files.items():
                safe_member(name)
                source_stat = source.stat()
                mode = stat.S_IMODE(source_stat.st_mode) & 0o777
                info = tarfile.TarInfo(name)
                info.size, info.mode, info.mtime = source_stat.st_size, mode, 0
                with source.open("rb") as stream:
                    tar.addfile(info, stream)
                inventory.append({"path": name, "size": info.size, "mode": mode})
    return inventory


def patterns(pattern: str) -> list[str]:
    """Brace alternation used by module-bundle-MeshWeaver.{AI,Maps,...} callers."""
    match = re.search(r"\{([^{}]+)\}", pattern)
    if not match:
        return [pattern]
    return [part for option in match.group(1).split(",")
            for part in patterns(pattern[:match.start()] + option + pattern[match.end():])]


class Artifacts:
    """One run's named-artifact namespace, backed by an already mounted FileStore."""

    def __init__(self, store: str, repository: str, run_id: str, attempt: int):
        if not REPO_RE.fullmatch(repository or ""):
            raise Red("repository must be owner/name")
        if not re.fullmatch(r"[1-9][0-9]*", str(run_id)) or attempt < 1:
            raise Red("run-id and attempt must be positive integers")
        if not store.startswith("file:"):
            raise Red("named artifacts currently require file:<mounted directory>; no fallback is allowed")
        root = Path(store[5:])
        if not root.is_absolute() or not root.is_dir():
            raise Red("artifact store must name an existing absolute mounted directory")
        self.store = STORE.make_store("file:" + str(root.resolve()))
        unavailable = self.store.reachable()
        if unavailable:
            raise Red(f"artifact store is unavailable: {unavailable}")
        self.repository, self.run_id, self.attempt = repository, str(run_id), attempt
        self.prefix = f"named/{repository}/{run_id}/artifacts"
        self.root = self.store.root
        no_links(self.root / self.prefix, self.root)

    def name_prefix(self, name: str) -> str:
        return self.prefix + "/" + hashlib.sha256(safe_name(name).encode()).hexdigest()

    def upload(self, name: str, files: dict[str, Path], compression: int = 6,
               retention: int = 7, overwrite: bool = False) -> dict:
        if not files:
            raise Red("cannot publish an empty artifact")
        if not 1 <= retention <= 90:
            raise Red("artifact retention must be between 1 and 90 days")
        prefix = self.name_prefix(name)
        folder = self.root / prefix
        no_links(folder, self.root)
        folder.mkdir(parents=True, exist_ok=True)
        # Advertise in-flight work BEFORE producing any bytes. The pruner preserves active
        # stages, and deletes expired attempts individually rather than deleting name families.
        with tempfile.TemporaryDirectory(prefix="ci-artifact-") as temp, \
                tempfile.TemporaryDirectory(prefix=".publish-", dir=folder) as staging:
            stage = Path(staging)
            archive = Path(temp) / "artifact.tar.gz"
            inventory = archive_files(files, archive, compression)
            digest = STORE.sha256_file(archive)
            key = f"{prefix}/objects/{self.attempt}/{digest}.tar.gz"
            no_links(self.root / key, self.root)
            with attempt_lock(folder, self.attempt):
                locator = self.store.put(key, archive)
                now = time.time()
                manifest = {"schema": SCHEMA, "repository": self.repository,
                            "runId": self.run_id, "attempt": self.attempt, "name": name,
                            "archiveSha256": digest, "archiveBytes": archive.stat().st_size,
                            "locator": locator, "files": inventory, "createdAt": now,
                            "expiresAt": now + retention * 86400, "retentionDays": retention}
                destination = folder / str(self.attempt)
                staged_manifest = stage / "manifest.json"
                staged_manifest.write_text(json.dumps(manifest, sort_keys=True) + "\n")
                try:
                    os.rename(stage, destination)
                except OSError:
                    if not destination.is_dir():
                        raise
                    old = self.read_manifest(destination / "manifest.json", name)
                    if old["archiveSha256"] == digest:
                        return old
                    if not overwrite:
                        raise Red("artifact name already published in this attempt; overwrite must be explicit")
                    os.replace(staged_manifest, destination / "manifest.json")
                return manifest

    def read_manifest(self, path: Path, name: str | None = None) -> dict:
        no_links(path, self.root)
        try:
            doc = json.loads(path.read_text())
            if (doc["schema"] != SCHEMA or doc["repository"] != self.repository
                    or doc["runId"] != self.run_id or str(doc["attempt"]) != path.parent.name
                    or not isinstance(doc["attempt"], int) or doc["attempt"] < 1
                    or not DIGEST_RE.fullmatch(doc["archiveSha256"])
                    or (name is not None and doc["name"] != name)):
                raise ValueError("identity mismatch")
            prefix = self.name_prefix(doc["name"])
            if path.parent.parent != self.root / prefix:
                raise ValueError("name does not match its directory")
            expected = (f"{self.store.spec}/{prefix}/objects/{doc['attempt']}/"
                        f"{doc['archiveSha256']}.tar.gz#sha256={doc['archiveSha256']}")
            if doc["locator"] != expected or not isinstance(doc["files"], list) or not doc["files"]:
                raise ValueError("invalid archive declaration")
            names = set()
            for entry in doc["files"]:
                member = safe_member(entry["path"])
                if member in names or type(entry["size"]) is not int or entry["size"] < 0:
                    raise ValueError("invalid inventory")
                if type(entry["mode"]) is not int or not 0 <= entry["mode"] <= 0o777:
                    raise ValueError("invalid mode")
                names.add(member)
            if type(doc["archiveBytes"]) is not int or doc["archiveBytes"] <= 0:
                raise ValueError("invalid archive size")
            if not isinstance(doc["expiresAt"], (int, float)):
                raise ValueError("invalid expiry")
            return doc
        except (KeyError, ValueError, TypeError, OSError) as ex:
            raise Red("artifact manifest is invalid or unreadable") from ex

    def list(self, name: str = "", pattern: str = "", include_expired: bool = False) -> list[dict]:
        if name:
            folders = [self.root / self.name_prefix(name)]
        else:
            base = self.root / self.prefix
            folders = sorted(base.iterdir()) if base.is_dir() else []
        selected = []
        for folder in folders:
            no_links(folder, self.root)
            if not folder.is_dir():
                continue
            attempts = [p for p in folder.iterdir() if p.name.isdigit()
                        and 0 < int(p.name) <= self.attempt]
            if not attempts:
                continue
            latest = max(attempts, key=lambda p: int(p.name))
            doc = self.read_manifest(latest / "manifest.json", name or None)
            expired = doc["expiresAt"] <= time.time()
            if expired and not include_expired:
                continue
            if pattern and not any(fnmatch.fnmatchcase(doc["name"], p) for p in patterns(pattern)):
                continue
            selected.append(dict(doc, expired=expired) if include_expired else doc)
        return sorted(selected, key=lambda doc: doc["name"])

    def verified_archive(self, doc: dict, destination: Path) -> list[tarfile.TarInfo]:
        key, _ = STORE.split_locator(doc["locator"], self.store.spec)
        no_links(self.root / key, self.root)
        self.store.get(doc["locator"], destination)
        if destination.stat().st_size != doc["archiveBytes"]:
            raise Red("artifact archive size differs from its manifest")
        with tarfile.open(destination, "r:gz") as tar:
            members = tar.getmembers()
            actual = []
            for member in members:
                safe_member(member.name)
                if not member.isfile():
                    raise Red("artifact archive contains a link, directory, or special file")
                actual.append({"path": member.name, "size": member.size, "mode": member.mode})
            if actual != doc["files"]:
                raise Red("artifact archive inventory differs from its manifest")
        return members

    def download(self, destination: Path, name: str = "", pattern: str = "",
                 merge_multiple: bool = False) -> int:
        selected = self.list(name, pattern)
        if not selected:
            raise Red("no artifact matches this repository, run, attempt and name/pattern")
        destination = Path(os.path.abspath(destination))
        if destination.is_symlink():
            raise Red("artifact destination is a symbolic link")
        # Existing ancestors are chosen by the caller, and may include macOS /var -> /private/var.
        # Resolve those once; never follow a link at or BELOW the chosen extraction root.
        destination = destination.parent.resolve() / destination.name
        no_links(destination, destination.parent)
        total = 0
        with tempfile.TemporaryDirectory(prefix="ci-artifact-download-") as temp:
            archives = []
            # Verify ALL archives first, so a corrupt later artifact cannot leave a partial merge.
            for index, doc in enumerate(selected):
                archive = Path(temp) / f"{index}.tar.gz"
                self.verified_archive(doc, archive)
                target = destination if name or merge_multiple else destination / doc["name"]
                for entry in doc["files"]:
                    no_links(target / entry["path"], destination.parent)
                archives.append((doc, archive, target))
            for doc, archive, target in archives:
                with tarfile.open(archive, "r:gz") as tar:
                    for member in tar.getmembers():
                        out = target / member.name
                        out.parent.mkdir(parents=True, exist_ok=True)
                        no_links(out, destination.parent)
                        descriptor, temp_name = tempfile.mkstemp(prefix=".artifact-", dir=out.parent)
                        staged = Path(temp_name)
                        try:
                            with os.fdopen(descriptor, "wb") as output, tar.extractfile(member) as source:
                                shutil.copyfileobj(source, output)
                            staged.chmod(member.mode)
                            os.replace(staged, out)
                        finally:
                            staged.unlink(missing_ok=True)
                        total += 1
        return total


def emit(name: str, value: str | int) -> None:
    print(f"{name}={value}")
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as stream:
            stream.write(f"{name}={value}\n")


def parser() -> argparse.ArgumentParser:
    cli = argparse.ArgumentParser(description=__doc__)
    commands = cli.add_subparsers(dest="command", required=True)
    for verb in ("upload", "download", "list", "probe"):
        command = commands.add_parser(verb)
        command.add_argument("--store", required=True)
        command.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", ""))
        command.add_argument("--run-id", default=os.environ.get("GITHUB_RUN_ID", ""))
        command.add_argument("--attempt", type=int)
        command.add_argument("--name", required=verb in ("upload", "probe"), default="")
        if verb == "upload":
            command.add_argument("--path", action="append", required=True)
            command.add_argument("--exclude", action="append", default=[])
            command.add_argument("--if-no-files-found", choices=("warn", "error", "ignore"), default="warn")
            command.add_argument("--include-hidden-files", type=boolean, nargs="?", const=True, default=False)
            command.add_argument("--overwrite", type=boolean, nargs="?", const=True, default=False)
            command.add_argument("--compression-level", type=int, choices=range(10), default=6)
            command.add_argument("--retention-days", type=int, default=7)
        elif verb in ("download", "list"):
            command.add_argument("--pattern", default="")
            if verb == "download":
                command.add_argument("--path", default=".")
                command.add_argument("--merge-multiple", type=boolean, nargs="?", const=True, default=False)
            else:
                command.add_argument("--include-expired", type=boolean, nargs="?", const=True, default=False)
    return cli


def main(argv: list[str] | None = None) -> int:
    args = parser().parse_args(argv)
    try:
        attempt = args.attempt
        if attempt is None:
            # A cross-run consumer asks for the source run's latest publication, not this run's
            # attempt number. Same-run composite actions always pass the attempt explicitly.
            same_run = (args.run_id == os.environ.get("GITHUB_RUN_ID")
                        and args.repository == os.environ.get("GITHUB_REPOSITORY"))
            attempt = (int(os.environ.get("GITHUB_RUN_ATTEMPT", "1"))
                       if same_run or args.command == "upload" else sys.maxsize)
        artifacts = Artifacts(args.store, args.repository, args.run_id, attempt)
        if args.command == "upload":
            safe_name(args.name)
            if not 0 <= args.retention_days <= 90:
                raise Red("retention-days must be 0-90 (0 uses the 7-day default)")
            retention = args.retention_days or 7
            files = collect(args.path, args.exclude, args.include_hidden_files, Path.cwd())
            if not files:
                if args.if_no_files_found == "error":
                    raise Red("no regular files matched the upload paths")
                if args.if_no_files_found == "warn":
                    print("::warning::no regular files matched; no artifact was published")
                emit("file-count", 0)
                return 0
            doc = artifacts.upload(args.name, files, args.compression_level,
                                   retention, args.overwrite)
            emit("artifact-id", "")  # It is not a GitHub artifact and has no GitHub id.
            emit("artifact-digest", doc["archiveSha256"])
            emit("artifact-url", "")  # A file locator is not a browser URL.
            emit("artifact-locator", doc["locator"])
            emit("file-count", len(doc["files"]))
        elif args.command == "download":
            if args.name and args.pattern:
                raise Red("choose name or pattern, not both")
            count = artifacts.download(Path(args.path), args.name, args.pattern, args.merge_multiple)
            emit("download-path", str(Path(args.path).resolve()))
            emit("file-count", count)
        elif args.command == "list":
            print(json.dumps(artifacts.list(args.name, args.pattern, args.include_expired), sort_keys=True))
        else:
            docs = artifacts.list(args.name)
            if not docs:
                return 1
            with tempfile.TemporaryDirectory(prefix="ci-artifact-probe-") as temp:
                artifacts.verified_archive(docs[0], Path(temp) / "artifact.tar.gz")
            emit("present", "true")
        return 0
    except (Red, OSError, tarfile.TarError, EOFError) as ex:
        print(f"::error::ci-run-artifacts: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
