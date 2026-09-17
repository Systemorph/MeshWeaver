#!/usr/bin/env python3
"""The bulk share I/O behind publish-bake-bundles.sh — ONE process per phase per target.

WHY THIS EXISTS
---------------
publish-bake-bundles.sh used to publish a ~46-file publication with one `az storage file upload`
process per file and then verify it with one `az storage file show` process per file, per target.
Measured 2026-09-08: 184 CLI launches for two targets at 1–3 s each, so "Publish bundles to every
portal target" took 5–10 minutes of a bake whose compile is ~7 minutes. The maintainer's directive
was one bulk operation per target, and this file is that: bash stays the orchestrator and owns
every verdict; this helper owns the share I/O and runs it inside one process with one
authenticated client, one HTTP connection pool and a bounded thread pool.

THE PHASES
----------
`upload`   reads a PLAN (`<path-under-dest>\\t<local-file>\\t<sha256>` per line), ensures every
           parent directory exists, then uploads every file with the two metadata values the
           postcondition reads back — `digest` (the sha256 from the plan) and `publication` (the
           run's token) — through a thread pool of `--workers` threads. Any failure is fatal:
           pending uploads are cancelled, the failure is named on stderr as `::error::`, exit 1.
           The directory stays sentinel-less, which is the state every reader already skips.

`verify`   reads the MANIFEST (`<path-under-dest>\\t<sha256>` per line), LISTS the destination
           directory and each subdirectory the manifest names ONCE, and then reads the properties
           of every listed manifest file — in the same process, over the same connection pool.
           🚨 The listing cannot carry the stamps: Azure Files' `List Directories and Files` returns
           names and sizes only, never metadata, so the per-file `get_file_properties` inside ONE
           process IS the bulk read. What the listing buys is the absent files (no 404 round trip
           per missing file) and the denominator ("N listed, M of them in this publication"). The
           elapsed time is printed so the cost stays measured rather than assumed.

           One TSV row per manifest line, in manifest order, on stdout:

               <path>  <state>  <digest>  <publication>  <length>

           state    ok       the file was read; digest/publication are its metadata, '-' if absent
                    absent   the listing does not contain it (nothing was read)
                    error    reading its properties FAILED — a `::error::` on stderr names why.
                             This is NOT "no digest": the two must never be the same value, or a
                             transient fault would read as a foreign publisher.
           digest / publication are '-' when the metadata key is missing or empty — never the
           empty string, never 'None' — so the bash parse can assert exactly five fields on every
           row and treat '-' as absent, exactly as it did with the CLI's `|| '-'` guards.

`stamp`    the same row for ONE path (`--path`), used by the convergence verdict to read the
           sentinel AFTER the sweep — the ordering is what lets it see a sibling that sealed in the
           meantime.

`dispose`  retires the FLAT COMPATIBILITY COPY at a source prefix (MeshWeaver#3461 phase 5), after the
           caller has moved `_current` to a sealed generation and read it back. Two steps, and the
           order is the whole contract:

             1. the prefix's `_complete` is deleted FIRST, on its own. That single delete is what
                turns the prefix from "a complete, older publication" into "being republished",
                the state every reader already backs off from. If it fails, NOTHING else is
                deleted and the exit code is 1: a sealed flat copy that stays sealed and stops
                being refreshed is a publication frozen on the day phase 5 landed, served whole to
                every torn pointer read — the stale serve this phase exists to remove.
             2. then the files, and only files POSITIVELY identified as the flat publication by the
                caller's `--match` patterns (`*.zip`, `modules/*.module.nupkg`, the markers). A
                file no pattern names is LEFT and named — the worst case of a narrow pattern is
                bytes that stay, never a deletion of something that is not the flat copy. The
                pointer (`--keep`) and every generation directory are never touched: only files
                directly under the prefix and under the pattern directories are listed at all.
                A failure here is reported as `::warning::` and is NOT fatal: once step 1 has run
                the prefix holds no publication, so a left-over file is storage, re-attempted by the
                prefix's next publication — never a reader-visible state.
             3. the seal AGAIN, because a producer still running a pre-phase-5 publisher can seal
                the flat copy INSIDE this disposal (its verify before the deletes, its `_complete`
                after them) and leave a complete-looking publication with files missing. Removing it
                again turns that into "being republished"; a failure to remove it is fatal.

           An already-absent file is the postcondition, not an error: two publications of one
           prefix may dispose of it concurrently. A directory the patterns name (`modules/`) is
           removed once nothing is left in it.

`sdk-check` imports the pinned SDK, constructs the clients this file uses (no network), asserts
           the installed versions are the ones the caller pinned, and prints them. It is the
           positive signal after the install and the one case the overlap harness runs against the
           REAL backend.

AUTH
----
The share is reached with the SAME identity the script's `az login` established: `AzureCliCredential`
asks the CLI for a storage token once per process (the pipeline caches it), and every request
carries `x-ms-file-request-intent: backup` — the SDK's `token_intent="backup"`, which is what the
CLI's `--backup-intent` sets. The identity therefore needs exactly the role it needed before:
Storage File Data Privileged Contributor on the target account.

THE FAKE BACKEND
----------------
`PUBLISH_BAKE_FAKE_SHARE_BACKEND=<path/to/module.py>` replaces the Azure backend with the module's
`make_backend(account, share)` — the overlap harness (test-publish-bake-overlap.py) uses it to run
the REAL script against a filesystem share and to interleave a second publisher mid-upload. It is
announced on stderr whenever it is in use, so a production log could never carry it silently.
"""

from __future__ import annotations

import argparse
import fnmatch
import importlib.util
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Iterable, Protocol

ABSENT = "-"
DEFAULT_WORKERS = 12
SENTINEL = "_complete"


def log(msg: str) -> None:
    """Progress and errors go to stderr: stdout is the TSV the caller parses."""
    sys.stderr.write(msg + "\n")
    sys.stderr.flush()


# ───────────────────────────── the backend contract ─────────────────────────────
class ShareBackend(Protocol):
    def ensure_directory(self, path: str) -> None: ...
    def upload(self, path: str, local: Path, metadata: dict[str, str]) -> None: ...
    def list_files(self, directory: str) -> dict[str, int | None]:
        """name → size for every FILE directly under `directory`; {} when it does not exist."""
        ...
    def get_properties(self, path: str) -> tuple[dict[str, str], int | None]:
        """(metadata, length) of one file. Raises when it cannot be read."""
        ...
    def delete_file(self, path: str) -> bool:
        """True when the file was deleted, False when it was not there. Raises on any other failure."""
        ...
    def delete_directory(self, path: str) -> bool:
        """True when the (empty) directory was deleted, False when it was not there. Raises otherwise."""
        ...


class AzureFilesBackend:
    """The real share. Imports lazily so the fake backend needs no SDK."""

    def __init__(self, account: str, share: str):
        from azure.core.exceptions import ResourceExistsError, ResourceNotFoundError
        from azure.identity import AzureCliCredential
        from azure.storage.fileshare import ShareClient

        self._exists_error = ResourceExistsError
        self._not_found = ResourceNotFoundError
        # The fleet's accounts are public-cloud accounts; the suffix is the CLI's default too.
        self.share = ShareClient(
            f"https://{account}.file.core.windows.net", share,
            credential=AzureCliCredential(), token_intent="backup",
        )

    def ensure_directory(self, path: str) -> None:
        d = self.share.get_directory_client(path)
        if d.exists():
            return
        try:
            d.create_directory()
        except self._exists_error:
            # Not a swallowed fault: the postcondition of this call is "the directory exists", and a
            # concurrent publisher creating the same level a moment earlier has established it.
            return

    def upload(self, path: str, local: Path, metadata: dict[str, str]) -> None:
        size = local.stat().st_size
        with open(local, "rb") as fh:
            self.share.get_file_client(path).upload_file(fh, length=size, metadata=metadata)

    def list_files(self, directory: str) -> dict[str, int | None]:
        d = self.share.get_directory_client(directory)
        try:
            items = list(d.list_directories_and_files())
        except self._not_found:
            # A directory that is not there holds no files: every manifest entry under it is
            # reported `absent`, which refuses the seal by name. Any OTHER failure propagates.
            return {}
        return {item["name"]: item.get("size") for item in items if not item["is_directory"]}

    def get_properties(self, path: str) -> tuple[dict[str, str], int | None]:
        props = self.share.get_file_client(path).get_file_properties()
        return dict(props.metadata or {}), props.size

    def delete_file(self, path: str) -> bool:
        try:
            self.share.get_file_client(path).delete_file()
        except self._not_found:
            # Not a swallowed fault: the postcondition of this call is "the file is not there", and
            # a concurrent publication disposing of the same flat copy a moment earlier has
            # established it. Every OTHER failure propagates.
            return False
        return True

    def delete_directory(self, path: str) -> bool:
        try:
            self.share.get_directory_client(path).delete_directory()
        except self._not_found:
            return False
        return True


def make_backend(account: str, share: str) -> ShareBackend:
    fake = os.environ.get("PUBLISH_BAKE_FAKE_SHARE_BACKEND")
    if fake:
        spec = importlib.util.spec_from_file_location("publish_bake_fake_backend", fake)
        if spec is None or spec.loader is None:
            raise SystemExit(f"::error::PUBLISH_BAKE_FAKE_SHARE_BACKEND={fake!r} is not a loadable module")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        log(f"backend: FAKE share ({fake}) — this is a harness run, not Azure Files")
        return module.make_backend(account, share)
    return AzureFilesBackend(account, share)


# ───────────────────────────── inputs ─────────────────────────────
def read_table(path: Path, columns: int, what: str) -> list[list[str]]:
    rows: list[list[str]] = []
    for n, line in enumerate(path.read_text().splitlines(), 1):
        if not line.strip():
            continue
        fields = line.split("\t")
        if len(fields) != columns:
            raise SystemExit(f"::error::{what} line {n} has {len(fields)} tab-separated field(s), "
                             f"{columns} expected: {line!r}")
        rows.append(fields)
    if not rows:
        # A plan or manifest with nothing in it would upload or verify nothing and exit 0 — the
        # vacuity every gate here refuses to render as green.
        raise SystemExit(f"::error::{what} at {path} is EMPTY — nothing to do is a failure here, "
                         f"never a success")
    return rows


def joined(dest: str, rel: str) -> str:
    return f"{dest}/{rel}" if dest else rel


def parent_directories(dest: str, rels: Iterable[str]) -> list[str]:
    """Every directory level any planned file needs, shallowest first, each once."""
    levels: list[str] = []
    for rel in rels:
        full = joined(dest, rel)
        parts = full.split("/")[:-1]
        for i in range(1, len(parts) + 1):
            level = "/".join(parts[:i])
            if level not in levels:
                levels.append(level)
    levels.sort(key=lambda p: p.count("/"))
    return levels


def workers_arg(value: int) -> int:
    if value < 1:
        raise SystemExit(f"::error::--workers must be at least 1, got {value}")
    return value


# ───────────────────────────── upload ─────────────────────────────
def cmd_upload(args: argparse.Namespace) -> int:
    plan = read_table(Path(args.plan), 3, "the upload plan")
    for rel, local, digest in plan:
        if not Path(local).is_file():
            raise SystemExit(f"::error::the upload plan names {local} for {rel}, and it is not a file")
        if not digest:
            raise SystemExit(f"::error::the upload plan carries no digest for {rel} — it would be "
                             f"published and never verifiable")
    backend = make_backend(args.account, args.share)
    for level in parent_directories(args.dest, (rel for rel, _, _ in plan)):
        backend.ensure_directory(level)

    started = time.monotonic()
    total_bytes = 0
    failures: list[str] = []

    def one(rel: str, local: str, digest: str) -> tuple[str, int]:
        backend.upload(joined(args.dest, rel), Path(local),
                       {"digest": digest, "publication": args.publication})
        return rel, Path(local).stat().st_size

    # 🚨 `max_workers=1` makes the plan order the upload order; the overlap harness relies on that
    # to interleave a second publisher at a named file. The reader contract needs no order among
    # these files at all — only that `_complete` (a separate call, after verification) is LAST.
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(one, rel, local, digest): rel for rel, local, digest in plan}
        for future in as_completed(futures):
            rel = futures[future]
            try:
                _, size = future.result()
            except Exception as exc:  # noqa: BLE001 — named on stderr and fatal, never swallowed
                failures.append(rel)
                log(f"::error::upload of {joined(args.dest, rel)} to {args.account}/{args.share} "
                    f"FAILED: {type(exc).__name__}: {exc}")
                pool.shutdown(wait=True, cancel_futures=True)
                break
            total_bytes += size
            log(f"published: {args.account}/{args.share}/{joined(args.dest, rel)}")
    elapsed = time.monotonic() - started
    if failures:
        log(f"::error::{len(failures)} upload(s) failed after {elapsed:.1f}s; the remaining "
            f"{len(plan) - len(failures)} were completed or cancelled. Nothing is sealed.")
        return 1
    log(f"uploaded {len(plan)} file(s), {total_bytes} bytes, to {args.account}/{args.share}/"
        f"{args.dest} in {elapsed:.1f}s ({args.workers} worker(s), one process)")
    return 0


# ───────────────────────────── verify / stamp ─────────────────────────────
def row(path: str, state: str, metadata: dict[str, str] | None, length: int | None) -> str:
    md = metadata or {}
    digest = md.get("digest") or ABSENT
    publication = md.get("publication") or ABSENT
    size = str(length) if length is not None else ABSENT
    return "\t".join((path, state, digest, publication, size))


def read_one(backend: ShareBackend, path: str) -> str:
    try:
        metadata, length = backend.get_properties(path)
    except Exception as exc:  # noqa: BLE001 — reported as `error`, which refuses the seal by name
        log(f"::error::could not read the properties of {path}: {type(exc).__name__}: {exc}")
        return row(path, "error", None, None)
    return row(path, "ok", metadata, length)


def cmd_verify(args: argparse.Namespace) -> int:
    manifest = read_table(Path(args.manifest), 2, "the publication manifest")
    backend = make_backend(args.account, args.share)
    started = time.monotonic()

    # ONE listing per directory the manifest names (the destination itself and, today, modules/).
    directories: list[str] = []
    for rel, _ in manifest:
        d = "/".join(rel.split("/")[:-1])
        if d not in directories:
            directories.append(d)
    listed: dict[str, dict[str, int | None]] = {}
    for d in directories:
        try:
            listed[d] = backend.list_files(joined(args.dest, d))
        except Exception as exc:  # noqa: BLE001
            log(f"::error::could not list {args.account}/{args.share}/{joined(args.dest, d)}: "
                f"{type(exc).__name__}: {exc} — nothing under it can be verified")
            return 1
    listed_count = sum(len(files) for files in listed.values())

    present: list[str] = []
    results: dict[str, str] = {}
    for rel, _ in manifest:
        d, _, name = rel.rpartition("/")
        if name in listed.get(d, {}):
            present.append(rel)
        else:
            results[rel] = row(joined(args.dest, rel), "absent", None, None)

    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(read_one, backend, joined(args.dest, rel)): rel for rel in present}
        for future in as_completed(futures):
            results[futures[future]] = future.result()

    manifest_names = {rel for rel, _ in manifest}
    extra = sorted(
        f"{d}/{name}" if d else name
        for d, files in listed.items() for name in files
        if (f"{d}/{name}" if d else name) not in manifest_names and name != SENTINEL
    )
    for rel, _ in manifest:
        sys.stdout.write(results[rel] + "\n")
    sys.stdout.flush()
    elapsed = time.monotonic() - started
    states = {"ok": 0, "absent": 0, "error": 0}
    for text in results.values():
        states[text.split("\t")[1]] += 1
    log(f"verified {len(manifest)} file(s) under {args.account}/{args.share}/{args.dest} in "
        f"{elapsed:.1f}s: {states['ok']} read, {states['absent']} absent, {states['error']} "
        f"unreadable ({listed_count} listed in {len(directories)} listing call(s), "
        f"{args.workers} worker(s), one process)")
    if extra:
        log(f"note: {len(extra)} listed file(s) under {args.dest} are not in this publication and "
            f"were not read: {' '.join(extra)}")
    # Rows carry the verdict per file; the exit code says only whether the sweep itself ran.
    return 0


def cmd_stamp(args: argparse.Namespace) -> int:
    backend = make_backend(args.account, args.share)
    sys.stdout.write(read_one(backend, args.path) + "\n")
    sys.stdout.flush()
    return 0


# ───────────────────────────── dispose ─────────────────────────────
def match_arg(value: str) -> tuple[str, str]:
    """`<dir>/<basename-glob>` or `<basename-glob>` → (dir, glob), one directory level at most.

    🚨 Split BEFORE matching: `fnmatch` lets `*` cross `/`, so `*.zip` matched against a full
    relative path would also claim `modules/x.zip` — or `<generation>/Store.zip`, a byte of a LIVE
    publication. Matching the basename inside ONE named directory is what keeps a pattern from
    ever reaching into a generation.
    """
    directory, _, glob = value.rpartition("/")
    if not glob or "/" in directory or directory in (".", "..") or glob in (".", ".."):
        raise SystemExit(f"::error::--match {value!r} is not '<glob>' or '<dir>/<glob>' — a disposal "
                         f"pattern names files directly under the prefix or one directory below it")
    return directory, glob


def cmd_dispose(args: argparse.Namespace) -> int:
    matchers = [match_arg(m) for m in args.match or []]
    if not matchers:
        # A disposal that recognises nothing would unseal the prefix and then report "0 deleted" —
        # a clean-looking line over a prefix full of unsealed bytes nobody named.
        raise SystemExit("::error::dispose needs at least one --match — nothing would be recognised "
                         "as the flat publication")
    keep = set(args.keep or [])
    backend = make_backend(args.account, args.share)
    started = time.monotonic()
    where = f"{args.account}/{args.share}/{args.dest}"

    # 1. THE SEAL, FIRST AND ALONE.
    seal = joined(args.dest, SENTINEL)
    try:
        unsealed = backend.delete_file(seal)
    except Exception as exc:  # noqa: BLE001 — fatal and named, never swallowed
        log(f"::error::could not remove {args.account}/{args.share}/{seal}: {type(exc).__name__}: "
            f"{exc} — the flat compatibility copy is STILL SEALED and nothing else under {args.dest} "
            f"was deleted. A sealed flat copy that is no longer refreshed is served whole to every "
            f"reader that cannot follow the pointer (MeshWeaver#3461 phase 5); the prefix's next "
            f"publication retries the disposal.")
        return 1
    log(f"unsealed the flat compatibility copy: {where}/{SENTINEL} "
        f"{'removed' if unsealed else 'was already absent'}")

    # 2. THE FILES — listed only AFTER the seal is gone, so nothing listed here is still served.
    directories: list[str] = []
    for d, _ in matchers:
        if d not in directories:
            directories.append(d)
    listed: dict[str, dict[str, int | None]] = {}
    unlisted: list[str] = []
    for d in directories:
        try:
            listed[d] = backend.list_files(joined(args.dest, d))
        except Exception as exc:  # noqa: BLE001 — reported; the prefix is already unsealed
            unlisted.append(d or ".")
            log(f"::warning::could not list {where}{'/' + d if d else ''}: {type(exc).__name__}: "
                f"{exc} — its files are left in place. The prefix is unsealed, so they are storage, "
                f"not a publication; its next publication retries the disposal.")

    def rel_of(d: str, name: str) -> str:
        return f"{d}/{name}" if d else name

    doomed: list[str] = []
    for d, glob in matchers:
        for name in sorted(listed.get(d, {})):
            rel = rel_of(d, name)
            if name == SENTINEL or rel in keep or rel in doomed:
                continue
            if fnmatch.fnmatchcase(name, glob):
                doomed.append(rel)
    recognised = set(doomed)
    left = sorted(rel_of(d, name) for d, files in listed.items() for name in files
                  if name != SENTINEL and rel_of(d, name) not in keep
                  and rel_of(d, name) not in recognised)

    deleted = 0
    absent = 0
    failed: list[str] = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(backend.delete_file, joined(args.dest, rel)): rel for rel in doomed}
        for future in as_completed(futures):
            rel = futures[future]
            try:
                if future.result():
                    deleted += 1
                    # One line per file DELETED from a production share, the same shape `upload`
                    # prints per file published. It is not recurring noise: a prefix has one flat
                    # copy to dispose of, so after its first phase-5 publication this loop prints
                    # nothing and the summary reads "seal already absent, 0 of 0".
                    log(f"disposed: {where}/{rel}")
                else:
                    absent += 1
            except Exception as exc:  # noqa: BLE001 — reported by name; see the header
                failed.append(rel)
                log(f"::warning::could not delete {where}/{rel}: {type(exc).__name__}: {exc}")

    # 3. 🚨 THE SEAL AGAIN, because a LEGACY WRITER CAN SEAL INSIDE THIS DISPOSAL (Copilot's review
    # of the phase-5 PR, and it is real). A producer still running a pre-phase-5 publisher refreshes
    # the flat copy: it unseals, uploads, verifies, and writes `_complete` LAST. If its verify ran
    # before this sweep's deletes and its seal lands after them, the prefix ends SEALED over a set
    # this sweep has emptied — a complete-looking flat publication with files missing, which is the
    # one outcome ordering alone cannot prevent. There is no lease on this store (measured: no
    # `lease` command under either `az storage` group), so this is a POSTCONDITION, not mutual
    # exclusion — the same shape, and the same honesty, as the publisher's own byte-level check.
    #
    # Re-reading the sentinel and removing it again converts that outcome into "being republished",
    # which every reader already backs off from; a failure to remove it is the one state this phase
    # must never leave behind, so it is fatal and named. (The legacy run's own postcondition sees
    # its files gone and refuses, so its job goes red too — which is correct: it published nothing.)
    try:
        if backend.delete_file(seal):
            log(f"::warning::{where}/{SENTINEL} was written again WHILE this disposal ran — a "
                f"publisher that still refreshes the flat copy sealed it over files this sweep had "
                f"already removed. Removed it again: the prefix reads as 'being republished', which "
                f"every reader backs off from, rather than as a complete publication with files "
                f"missing (MeshWeaver#3461 phase 5).")
    except Exception as exc:  # noqa: BLE001 — fatal: this is the one state phase 5 must not leave
        log(f"::error::{where}/{SENTINEL} reappeared while this disposal ran and could not be "
            f"removed ({type(exc).__name__}: {exc}) — the prefix is SEALED over a publication whose "
            f"files this sweep deleted, and a reader that falls back to it would be served an "
            f"incomplete set. Remove it by hand.")
        return 1

    # 4. A pattern directory the flat copy created (`modules/`), once nothing is left in it.
    for d in sorted((d for d in directories if d), key=lambda d: -d.count("/")):
        if d in unlisted:
            continue
        try:
            if backend.list_files(joined(args.dest, d)):
                continue
            backend.delete_directory(joined(args.dest, d))
        except Exception as exc:  # noqa: BLE001 — reported; an empty directory is not a publication
            log(f"::warning::could not remove the directory {where}/{d}: {type(exc).__name__}: {exc}")

    elapsed = time.monotonic() - started
    log(f"disposed of the flat compatibility copy under {where} in {elapsed:.1f}s: seal "
        f"{'removed' if unsealed else 'already absent'}, {deleted} of {len(doomed)} recognised "
        f"file(s) deleted ({absent} already absent, {len(failed)} failed), {len(left)} unrecognised "
        f"file(s) left in place{(': ' + ' '.join(left)) if left else ''} "
        f"({args.workers} worker(s), one process)")
    if failed or unlisted:
        log(f"::warning::the flat copy under {where} is UNSEALED but not fully removed "
            f"({len(failed)} delete(s) failed, {len(unlisted)} listing(s) failed) — no reader serves "
            f"an unsealed directory, and the prefix's next publication retries the rest.")
    return 0


# ───────────────────────────── sdk-check ─────────────────────────────
def cmd_sdk_check(args: argparse.Namespace) -> int:
    import importlib.metadata as md

    pins = [p for p in (args.pins or "").split() if p]
    if not pins:
        raise SystemExit("::error::sdk-check needs --pins 'name==version …' — an unpinned check "
                         "proves nothing about what production runs")
    for pin in pins:
        name, sep, version = pin.partition("==")
        if not sep or not version:
            raise SystemExit(f"::error::pin {pin!r} is not of the form name==version")
        installed = md.version(name)
        if installed != version:
            raise SystemExit(f"::error::{name} is {installed}, the pin says {version} — this helper "
                             f"must run against the versions it was verified with")
    from azure.core.credentials import AccessToken
    from azure.storage.fileshare import ShareClient, ShareDirectoryClient, ShareFileClient

    class _NoNetworkCredential:
        def get_token(self, *scopes, **kwargs):  # pragma: no cover — never called
            return AccessToken("unused", 0)

    share = ShareClient("https://sdkcheck.file.core.windows.net", "sdkcheck",
                        credential=_NoNetworkCredential(), token_intent="backup")
    file = share.get_file_client("dir/file.zip")
    directory = share.get_directory_client("dir")
    for obj, attrs in ((file, ("upload_file", "get_file_properties", "delete_file")),
                       (directory, ("exists", "create_directory", "list_directories_and_files",
                                    "delete_directory"))):
        for a in attrs:
            if not callable(getattr(obj, a, None)):
                raise SystemExit(f"::error::{type(obj).__name__}.{a} is missing — the SDK surface "
                                 f"this helper uses has moved")
    assert isinstance(file, ShareFileClient) and isinstance(directory, ShareDirectoryClient)
    log("sdk-check: " + " ".join(f"{p.split('==')[0]}=={md.version(p.split('==')[0])}" for p in pins)
        + " — clients construct with token_intent=backup; no network was touched")
    return 0


# ───────────────────────────── main ─────────────────────────────
def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="bulk share I/O for publish-bake-bundles.sh")
    sub = ap.add_subparsers(dest="command", required=True)

    def target(p: argparse.ArgumentParser) -> None:
        p.add_argument("--account", required=True)
        p.add_argument("--share", required=True)

    up = sub.add_parser("upload", help="upload every file of a plan, stamped, in one process")
    target(up)
    up.add_argument("--dest", required=True, help="directory under the share the plan is relative to")
    up.add_argument("--plan", required=True, help="<path-under-dest>\\t<local-file>\\t<sha256> per line")
    up.add_argument("--publication", required=True, help="this run's publication token")
    up.add_argument("--workers", type=int, default=DEFAULT_WORKERS)
    up.set_defaults(fn=cmd_upload)

    ve = sub.add_parser("verify", help="list once, read every manifest file's stamps, print TSV rows")
    target(ve)
    ve.add_argument("--dest", required=True)
    ve.add_argument("--manifest", required=True, help="<path-under-dest>\\t<sha256> per line")
    ve.add_argument("--workers", type=int, default=DEFAULT_WORKERS)
    ve.set_defaults(fn=cmd_verify)

    st = sub.add_parser("stamp", help="one TSV row for one file")
    target(st)
    st.add_argument("--path", required=True, help="full path under the share")
    st.set_defaults(fn=cmd_stamp)

    di = sub.add_parser("dispose", help="unseal, then delete, the flat compatibility copy at a prefix")
    target(di)
    di.add_argument("--dest", required=True, help="the source prefix whose flat copy is disposed of")
    di.add_argument("--match", action="append",
                    help="'<glob>' or '<dir>/<glob>' naming a flat-copy file; repeatable")
    di.add_argument("--keep", action="append",
                    help="a path relative to --dest that is never deleted (the pointer); repeatable")
    di.add_argument("--workers", type=int, default=DEFAULT_WORKERS)
    di.set_defaults(fn=cmd_dispose)

    sc = sub.add_parser("sdk-check", help="the pinned SDK imports and its clients construct")
    sc.add_argument("--pins", required=True, help="'name==version …' as installed")
    sc.set_defaults(fn=cmd_sdk_check)

    args = ap.parse_args(argv)
    if hasattr(args, "workers"):
        args.workers = workers_arg(args.workers)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
