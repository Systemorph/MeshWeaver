#!/usr/bin/env python3
"""ci-artifact-store.py — the fleet's CI object store seam: big build outputs live on OUR infra,
GitHub Actions storage keeps only what a human clicks.

(The name on this first line is load-bearing: a lane that fetches this file at the platform pin
refuses a body whose first 400 bytes do not name it — the same rule module-build-ledger.py uses.)

WHY THIS EXISTS (maintainer, 2026-09-17: "we still incur cost for github actions … please see that
it goes to 0" · "disable for any private repo" · "and when free capacity gone => defer to our infra")
---------------------------------------------------------------------------------------------------
Compute already moved: every private-repo job runs on our ARC scale sets and the org Actions budget
is $0 with prevent_further_usage. What is left on the bill is STORAGE, and it is not small —
measured over the live (non-expired) artifact inventory on 2026-09-17, REST only:

    MeshWeaver.Plugins      382 GB    $8.70 on 09-16 alone, rising
    MeshWeaver.SocialMedia   12 GB
    MeshWeaver.Reinsurance   10 GB
    MeshWeaver.Education      8 GB
    MeshWeaver.Manufacturing  6 GB
    MeshWeaver.Crm            6 GB

Of Plugins' 382 GB, 247 GB is `module-bundle-*` at 7-day retention — and most of those bytes are
DUPLICATES: when the build ledger says a module's key is already built, the pack leg downloads that
run's bundle and re-uploads the same bytes under the same name in its own run, because the run's own
consumers (gate, compile-check, publish-bake) read the artifact from THIS run. 754 copies of one
module's bundle were live at once. The Team plan includes ~2 GB.

The public repo is not in this list and must never be pushed into it: Systemorph/MeshWeaver's 4.3 TB
monthly average costs nothing, and a fork PR there holds no secret at all. So this store is
OPT-IN and its absence is not an error — see THE DEGRADE RULE.

WHAT IT IS
----------
One command with a `--store` spec, so a lane says *where* bytes go in one place:

    gha                                     GitHub Actions artifacts — the caller's own
                                            upload-artifact/download-artifact steps. This script
                                            does nothing; it exists so `resolve` can NAME the mode.
    azblob:<account>/<container>[/<prefix>] Azure Blob, through `az storage blob`, authenticated by
                                            the ambient `azure/login` session (--auth-mode login).
                                            NEVER an account key: a key in a repo secret is a
                                            credential whose blast radius is the whole account and
                                            which no federated identity can revoke.
    file:<dir>                              a directory the runner already has — the writable Azure
                                            Files share a runner pod mounts (the shape
                                            /nuget-shelf already has), or a local dir in a test. No
                                            credential at all, which is its advantage and its
                                            limit: it exists only on a self-hosted runner that
                                            mounts it, so a lane must still degrade to `gha`.

    resolve --declared SPEC [--require]   print the effective mode and why; --require makes an
                                          unusable store RED instead of `gha`. Emits `store-id`.
    put     --store SPEC --key K --file F  upload; prints `locator=<spec>#sha256=<hex>`
    get     --store SPEC --locator L --out F   download and VERIFY the sha in the locator
    probe   --store SPEC --locator L       exit 0 iff the object is there (and, with --sha, matches)
    prune   --store SPEC --prefix P --older-than DAYS   the belt to the account's lifecycle braces
    --self-test                            every rule below, against a fake `az` on PATH

    put/get also take --expect-store-id ID, the `store-id` the RUN resolved. See THE PATH IS NOT
    THE IDENTITY below; it is how a cross-pool handoff is refused at the write.

🚨 THE PATH IS NOT THE IDENTITY (#4761)
---------------------------------------
`file:<dir>` names a directory, and two jobs can print a byte-identical `file:/ci-artifacts` while
standing on two DIFFERENT shares. On this fleet they do: each runner namespace declares its own
dynamically-provisioned `ci-artifacts` PVC, so `aks-silos` (`arc-runners`) and `aks-silos-dind`
(`arc-runners-dind`) mount two separate Azure Files shares at that path. A 1.46 GB workspace build
was written by a producer on one pool and asked for by eight consumers on the other 13 seconds
later; it was simply never in their share. Nothing in this file could see the difference, and
`put` — whose byte count and sha256 both came from the SOURCE file, with `dst` never stat-ed —
printed a success that could not be wrong, so the producer was green by construction and the fault
presented as a consumer problem.

Both halves are fixed here and both matter:
  * `put` READS THE DESTINATION BACK (`FileStore._verify_written`): it exists, it is a regular
    file, its size matches, it re-hashes to the digest, it appears in its own directory listing,
    and the staging file is gone. A write that cannot be observed to have failed is not a write.
  * `store_id()` answers WHICH store this is, from `/proc/self/mountinfo` — the mount source, not
    the path — and `--expect-store-id` refuses to move bytes when this runner is not on the store
    the run resolved. The lane threads `resolve`'s `store-id` output through every put and get, so
    a mis-shared mount is RED at the FIRST store operation of the run, naming the mechanism, in
    place of eight confusing absences later.

THE DEGRADE RULE — the one thing to get right
---------------------------------------------
🚨 A caller that declares NO store gets `gha` and behaves exactly as it did before this file existed.
That is not a fallback, it is the default: the public repo, a fork PR, and any runner without the
federated identity have no credential and must keep working. `resolve` says which mode it chose and
why, in the job summary, so "it used GitHub artifacts" is never a silent outcome.

🚨 But a caller that DOES declare a store and cannot use it is RED (`resolve --require`), and every
`put`/`get` failure is RED, naming the phase. There is no path where a store is configured, fails,
and the lane quietly writes somewhere else or rebuilds from source — that is the fault-becomes-fact
defect (#2695) and it is how "unchanged ⇒ no compile" turns into "sometimes compiles, nobody knows".

KEYS, AND WHY THEY DECIDE THE LIFECYCLE
---------------------------------------
Two prefixes, two lifetimes, so the store's growth is bounded by a rule and not by a habit:

    runs/<repo>/<run_id>/<attempt>/<name>   a handoff between jobs of ONE run. Dead when the run is.
    modules/<repo>/<module>/<build-key>/…   the cross-run reuse copy the build ledger points at.
                                            Content-addressed by the ledger's build key, so the same
                                            bytes are written once however many runs want them.

The account's own blob lifecycle-management policy is the primary pruner (server-side, free, and it
cannot be forgotten by a lane that failed early); `prune` is the operational second pair of hands for
a prefix a policy does not cover yet, and it refuses to run without an explicit `--older-than`.

🚨 `put` is IDEMPOTENT BY CONTENT: it probes first and, when the blob is there with the same
sha256, uploads nothing and prints the same locator. That is what makes "unchanged ⇒ no compile"
also mean "unchanged ⇒ no upload" — and it is why a re-run costs nothing.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

# A container/account name grammar strict enough that a malformed --store is refused before the
# first `az` launch, where the error would arrive as an opaque CLI usage message.
ACCOUNT_RE = re.compile(r"^[a-z0-9]{3,24}$")
CONTAINER_RE = re.compile(r"^[a-z0-9]([a-z0-9-]{1,61}[a-z0-9])?$")
# Blob path: no leading slash, no `..`, no backslash — a key is composed from run ids and module
# names, and one of those is attacker-adjacent (a branch name is not, a module name is ours, but
# the check costs nothing and a traversal into another prefix would cross a lifecycle boundary).
KEY_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._\-/]{0,1022}$")


class Red(Exception):
    """A refusal that must reach the log as ::error and exit 1."""


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _unescape_mountinfo(field: str) -> str:
    r"""mountinfo escapes space, tab, newline and backslash as \040 &c. Decode them, so a mount
    point with a space in it is not silently read as a different one."""
    out, i = [], 0
    while i < len(field):
        if field[i] == "\\" and i + 3 < len(field) and field[i + 1:i + 4].isdigit():
            out.append(chr(int(field[i + 1:i + 4], 8)))
            i += 4
        else:
            out.append(field[i])
            i += 1
    return "".join(out)


def mount_identity(path: Path, mountinfo: list[str] | None = None) -> str:
    """WHICH FILESYSTEM this directory lives on — read from the kernel, never minted, never cached.

    🚨 THIS IS THE INSTRUMENT #4761 WAS MISSING. `file:<dir>` names a PATH, and two jobs can print
    a byte-identical path while standing on two different stores. On the fleet's cluster they do:
    each runner namespace declares its OWN dynamically-provisioned `ci-artifacts` PVC (Memex
    `deployments/aks/ci-runners/ci-artifact-store.yaml`, whose header says it in so many words —
    "ONE Azure Files share per runner namespace"), so `aks-silos` (namespace `arc-runners`) and
    `aks-silos-dind` (namespace `arc-runners-dind`) mount two DIFFERENT Azure Files shares at
    `/ci-artifacts`. A producer on one pool then writes bytes the consumers on the other pool can
    never see, and the only symptom is `get` saying the object "is not there".

    (The fleet already owns the cure, one volume over: `ci-platform` reaches both namespaces as two
    static PersistentVolumes carrying ONE `volumeHandle`, i.e. one share. `ci-artifacts` was never
    given that treatment, and nothing measured the difference.)

    `/proc/self/mountinfo` names the mount SOURCE — for cifs, `//<account>.file.core.windows.net/<share>`
    — which is identical for two pods on the same share and different for two shares, so comparing
    it answers "are we looking at the same bytes?" with no credential, no write and no round trip.
    Where there is no /proc (a developer's macOS laptop, a BSD) the device id plus the real path is
    the same question answered locally: two directories are two stores, and one directory reached
    through a symlink is one store.

    `mountinfo` is the kernel's table, injectable ONLY so the self-test can exercise the Linux
    branch on a macOS laptop: this file's own dev machine has no /proc, so without it every local
    green would come from the fallback and the code that actually runs in CI would be untested."""
    real = os.path.realpath(str(path))
    best: tuple[str, str, str, str] | None = None
    if mountinfo is None:
        try:
            with open("/proc/self/mountinfo", encoding="utf-8") as f:
                mountinfo = f.read().splitlines()
        except OSError:
            mountinfo = []
    for line in mountinfo:
        head, sep, tail = line.partition(" - ")
        if not sep:
            continue
        hf, tf = head.split(), tail.split()
        if len(hf) < 5 or len(tf) < 2:
            continue
        bind_root, mount_point = _unescape_mountinfo(hf[3]), _unescape_mountinfo(hf[4])
        if real != mount_point and not real.startswith(mount_point.rstrip("/") + "/"):
            continue
        # The longest mount point wins, and at equal length the LAST line wins: mountinfo is
        # ordered, and a later mount over the same point shadows the earlier one.
        if best is None or len(mount_point) >= len(best[0]):
            best = (mount_point, tf[0], _unescape_mountinfo(tf[1]), bind_root)
    if best:
        mount_point, fstype, source, bind_root = best
        rel = os.path.relpath(real, mount_point)
        rel = "" if rel == "." else rel
        return f"{fstype}:{source}:{bind_root.rstrip('/')}/{rel}".rstrip("/")
    return f"dev:{os.stat(real).st_dev}:{real}"


class Store:
    """Base: `gha` — the mode in which this script moves no bytes at all."""

    kind = "gha"
    spec = "gha"

    def describe(self) -> str:
        return "GitHub Actions artifacts (the caller's own upload/download steps)"

    def store_id(self) -> str:
        """WHICH store this is, as opposed to how it is addressed. For `gha` and `azblob` the spec
        IS the identity — an account and a container name the same bytes from every runner on
        earth, and `split_locator` already refuses a locator naming a different one. Only `file:`
        can wear one name over two different stores, so only `file:` overrides this."""
        return self.spec

    def require_store_id(self, expect: str | None) -> None:
        """🚨 Refuse to move bytes when this runner is not on the store the RUN resolved.

        `expect` is `None` when the caller did not ask (a local invocation, a lane older than
        #4761). An EMPTY string is NOT the same thing and is RED: it means the caller wired the
        check up and handed over nothing to check against, which is a gate passing on no
        evidence."""
        if expect is None:
            return
        if not expect.strip():
            raise Red("--expect-store-id was given an EMPTY value. The caller asked for the "
                      "same-store check and supplied nothing to check against, so the check would "
                      "pass whatever store this runner is standing on. `resolve` emits "
                      "`store-id=<identity>`; wire that output through to this job rather than "
                      "letting the check quietly evaporate.")
        mine = self.store_id()
        if expect.strip() != mine:
            raise Red(
                f"this runner is NOT on the store this run resolved.\n"
                f"    this job stands on : {mine}\n"
                f"    the run resolved   : {expect.strip()}\n"
                f"  Both address it as `{self.spec}` — a `file:` store is a PATH, and the same path "
                f"on two runner pools can be two different shares. On this fleet it IS: each runner "
                f"namespace's `ci-artifacts` PVC provisions its own Azure Files share, so a handoff "
                f"between MW_RUNNER and MW_RUNNER_DOCKER has nowhere to land. Bind both namespaces "
                f"to ONE share (the static-PersistentVolume pattern `ci-platform` already uses: two "
                f"PVs, one volumeHandle) or set MW_ARTIFACT_STORE back to `gha`. Refusing here "
                f"rather than writing bytes the next job cannot read (MeshWeaver #4761).")

    def reachable(self) -> str:
        """'' when this runner can use the store, else why it cannot — one sentence, for the log."""
        return ""

    def put(self, key: str, file: Path, expect_store_id: str | None = None) -> str:
        raise Red("store 'gha' moves no bytes: the caller uploads with actions/upload-artifact. "
                  "`put` was called anyway, which means a lane took the store branch while resolving "
                  "to gha — the two decisions have drifted apart.")

    get = probe = prune = put


class AzBlob(Store):
    kind = "azblob"

    def __init__(self, account: str, container: str, prefix: str = "", az: str = "az"):
        if not ACCOUNT_RE.match(account):
            raise Red(f"'{account}' is not an Azure storage account name (3-24 lowercase alphanumerics)")
        if not CONTAINER_RE.match(container):
            raise Red(f"'{container}' is not a blob container name (3-63 lowercase alphanumerics and dashes)")
        self.account, self.container, self.prefix, self.az = account, container, prefix.strip("/"), az
        self.spec = f"azblob:{account}/{container}" + (f"/{self.prefix}" if self.prefix else "")

    def describe(self) -> str:
        return (f"Azure Blob {self.account}/{self.container}"
                + (f" under {self.prefix}/" if self.prefix else "")
                + " (az storage blob, --auth-mode login — the ambient azure/login session)")

    def reachable(self) -> str:
        r = subprocess.run([self.az, "storage", "container", "show", "--account-name", self.account,
                            "--name", self.container, "--auth-mode", "login", "--only-show-errors"],
                           capture_output=True, text=True)
        return "" if r.returncode == 0 else ((r.stderr or r.stdout).strip()[:400]
                                             or f"az exited {r.returncode} with no message")

    def _blob(self, key: str) -> str:
        if not KEY_RE.match(key) or ".." in key.split("/"):
            raise Red(f"'{key}' is not a usable object key (no leading slash, no '..', no backslash)")
        return f"{self.prefix}/{key}" if self.prefix else key

    def _az(self, *args: str, ok_codes: tuple[int, ...] = (0,)) -> subprocess.CompletedProcess:
        cmd = [self.az, "storage", "blob", *args,
               "--account-name", self.account, "--auth-mode", "login", "--only-show-errors"]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode not in ok_codes:
            raise Red(f"`az storage blob {args[0]}` exited {r.returncode} against "
                      f"{self.account}/{self.container}: {(r.stderr or r.stdout).strip()[:600]}")
        return r

    def _exists_sha(self, blob: str) -> str | None:
        """The blob's recorded sha256 (our own metadata), or None when it is not there.

        🚨 The sha comes from metadata we WROTE, not from Azure's Content-MD5: MD5 is not the
        digest the build ledger records, and a store that answers a different digest than the one
        the caller verifies is a store that cannot be verified at all.

        🚨 ABSENT IS NOT AN ERROR, and which exit code the CLI uses for it has moved (3 today, 1 in
        older builds, and the message is what is stable). So a not-found ANSWER is recognised by its
        text and returns None — while anything else (a 403, a network failure, an unparseable
        answer) still raises, because a store that cannot be read must never look like an empty one:
        `put` would re-upload harmlessly, but `probe` would answer "absent" for bytes that are there
        and the lane would rebuild a module it already had, silently and forever."""
        r = self._az("show", "--container-name", self.container, "--name", blob, ok_codes=(0, 1, 3))
        if r.returncode != 0:
            text = ((r.stderr or "") + (r.stdout or "")).lower()
            if any(m in text for m in ("blobnotfound", "not found", "notfound", "does not exist",
                                       "resourcenotfound", "errorcode:blobnotfound")):
                return None
            raise Red(f"`az storage blob show` exited {r.returncode} for {blob} against "
                      f"{self.account}/{self.container} and did not say the blob is absent: "
                      f"{(r.stderr or r.stdout).strip()[:400]}")
        if not r.stdout.strip():
            return None
        try:
            return (json.loads(r.stdout).get("metadata") or {}).get("sha256")
        except json.JSONDecodeError as e:
            raise Red(f"`az storage blob show` answered something that is not JSON for {blob}: {e}")

    def put(self, key: str, file: Path, expect_store_id: str | None = None) -> str:
        self.require_store_id(expect_store_id)
        if not file.is_file():
            raise Red(f"nothing to upload: {file} is not a file")
        blob = self._blob(key)
        digest = sha256_file(file)
        if self._exists_sha(blob) == digest:
            print(f"store: {blob} is already there with sha256 {digest} — nothing uploaded")
            return f"{self.spec}/{key}#sha256={digest}"
        self._az("upload", "--container-name", self.container, "--name", blob,
                 "--file", str(file), "--overwrite", "true", "--metadata", f"sha256={digest}")
        print(f"store: uploaded {file.name} ({file.stat().st_size} bytes) to {blob}, sha256 {digest}")
        return f"{self.spec}/{key}#sha256={digest}"

    def get(self, locator: str, out: Path, expect_store_id: str | None = None) -> str:
        self.require_store_id(expect_store_id)
        key, want = split_locator(locator, self.spec)
        blob = self._blob(key)
        out.parent.mkdir(parents=True, exist_ok=True)
        self._az("download", "--container-name", self.container, "--name", blob, "--file", str(out))
        got = sha256_file(out)
        if want and got != want:
            out.unlink(missing_ok=True)
            raise Red(f"{blob} downloaded as sha256 {got}, but the locator attests {want} — the bytes "
                      f"are not the ones the record names. Not using them.")
        print(f"store: fetched {blob} -> {out} (sha256 {got})")
        return got

    def probe(self, locator: str) -> bool:
        key, want = split_locator(locator, self.spec)
        have = self._exists_sha(self._blob(key))
        return have is not None and (not want or have == want)

    def prune(self, prefix: str, older_than_days: float) -> tuple[int, int]:
        raise Red("prune is handled by prune_cmd (it needs the CLI's list/delete-batch verbs)")


class FileStore(Store):
    """A directory the runner already has — an Azure Files share mounted into the pod, or a local
    dir under test. Same key grammar, same locator shape, same content-idempotence, so a lane that
    speaks to one backend speaks to both."""

    kind = "file"

    def __init__(self, root: str):
        if not root:
            raise Red("store spec 'file:' names no directory")
        self.root = Path(root)
        self.spec = f"file:{root}"

    def describe(self) -> str:
        return (f"the directory {self.root} (a mounted share; no credential is involved) "
                f"on {self.store_id()}")

    def store_id(self) -> str:
        """🚨 THE PATH IS NOT THE IDENTITY. See mount_identity() — two runner pools mount two
        different Azure Files shares at `/ci-artifacts`, and before #4761 nothing in this file
        could tell them apart."""
        return mount_identity(self.root)

    def reachable(self) -> str:
        if not self.root.is_dir():
            return f"{self.root} is not a directory on this runner (the share is not mounted here)"
        probe = self.root / f".mw-store-write-probe.{os.getpid()}"
        try:
            probe.write_text("x")
            probe.unlink()
        except OSError as e:
            return f"{self.root} is not writable by this job: {e}"
        return ""

    def _path(self, key: str) -> Path:
        if not KEY_RE.match(key) or ".." in key.split("/"):
            raise Red(f"'{key}' is not a usable object key (no leading slash, no '..', no backslash)")
        return self.root / key

    def _publish(self, tmp: Path, dst: Path) -> None:
        """The swap, isolated in one overridable method so the self-test can break it deliberately
        and watch `put` go red — a verification nobody has ever seen fail is not a verification.

        🚨 The bytes are FSYNCED before the rename. `shutil.copyfile` leaves them in the client's
        page cache; on a network filesystem the SERVER — and therefore every other mount of the
        share — holds them only once the client has flushed. Publishing a name whose content has
        not reached the server is exactly the half-written read this fleet already measured on this
        volume class (#2190 → #4547: on Azure Files SMB the share mounts `nobrl`, so a reader on
        another node sees no error at all)."""
        with open(tmp, "rb+") as f:
            os.fsync(f.fileno())
        os.replace(tmp, dst)

    def _verify_written(self, dst: Path, key: str, size: int, digest: str) -> None:
        """🚨 READ THE DESTINATION BACK — the whole of #4761 in one method.

        Before this existed, `put` printed `store: wrote <n> bytes … sha256 <hex>` with BOTH
        numbers taken from the SOURCE file and never stat-ed `dst` at all. The success line could
        not be wrong, so the producer was green BY CONSTRUCTION and every failure of the write
        presented as a consumer problem — in the run that opened #4761, thirteen seconds and eight
        jobs later, with the producer's log carrying nothing but a success. A write that cannot be
        observed to have failed is not a write."""
        where = f"{dst} (key {key}) on {self.store_id()}"
        try:
            st = os.stat(dst)
        except OSError as e:
            raise Red(f"the write did NOT land: {where} cannot be stat-ed after the publish ({e}). "
                      f"The bytes were copied and renamed without an error, and the destination is "
                      f"not there — this store is not holding what it was handed.")
        if not stat.S_ISREG(st.st_mode):
            raise Red(f"the write did NOT land as a file: {where} is not a regular file after the "
                      f"publish (mode {st.st_mode:#o}).")
        if st.st_size != size:
            raise Red(f"the write landed SHORT: {where} is {st.st_size} bytes, and {size} were "
                      f"written. The publish reported no error, so the destination lost bytes "
                      f"after the rename — do not hand this locator to a consumer.")
        got = sha256_file(dst)
        if got != digest:
            raise Red(f"the write landed CORRUPT: {where} reads back as sha256 {got}, and {digest} "
                      f"was written. Same length, different bytes.")
        if dst.name not in os.listdir(dst.parent):
            raise Red(f"the write is INVISIBLE: {where} stats and hashes correctly and does not "
                      f"appear in its own directory listing, so a consumer enumerating the prefix "
                      f"will not find it.")
        leftover = sorted(q.name for q in dst.parent.glob(dst.name + ".tmp.*"))
        if leftover:
            raise Red(f"the publish left its staging file behind next to {where}: {leftover} — the "
                      f"rename did not consume it, which means it was not a rename.")

    def put(self, key: str, file: Path, expect_store_id: str | None = None) -> str:
        self.require_store_id(expect_store_id)
        if not file.is_file():
            raise Red(f"nothing to upload: {file} is not a file")
        dst = self._path(key)
        digest = sha256_file(file)
        size = file.stat().st_size
        if dst.is_file() and sha256_file(dst) == digest:
            print(f"store: {dst} is already there with sha256 {digest} — nothing written "
                  f"({self.store_id()})")
            return f"{self.spec}/{key}#sha256={digest}"
        dst.parent.mkdir(parents=True, exist_ok=True)
        # 🚨 Write-then-rename. Concurrent pack legs of the fleet share this directory, and a reader
        # that opens a half-written bundle gets a sha mismatch at best and a corrupt nupkg at worst.
        # `os.replace` is a real rename(2) — unlike .NET's File.Move it has no link(2)/copy fallback,
        # so it either renames or raises. What it CANNOT tell us is whether the name it published is
        # readable afterwards, which is why _verify_written follows and is not optional.
        tmp = dst.with_name(dst.name + f".tmp.{os.getpid()}")
        shutil.copyfile(file, tmp)
        try:
            self._publish(tmp, dst)
        except OSError as e:
            tmp.unlink(missing_ok=True)
            raise Red(f"could not publish {file.name} as {dst} on {self.store_id()}: {e}")
        self._verify_written(dst, key, size, digest)
        print(f"store: wrote {file.name} ({size} bytes) to {dst}, sha256 {digest} — "
              f"read back from the destination and verified ({self.store_id()})")
        return f"{self.spec}/{key}#sha256={digest}"

    def _absence_diagnosis(self, src: Path) -> str:
        """What this store DOES hold where the record said the object would be. An absence with no
        denominator is the reading that sent #4761 looking at consumers for a day."""
        here = src.parent
        while here != self.root and here != here.parent and not here.is_dir():
            here = here.parent
        try:
            entries = sorted(q.name for q in here.iterdir())
        except OSError as e:
            return f"  (its directory {here} cannot even be listed: {e})"
        shown = ", ".join(entries[:8]) + (f", … (+{len(entries) - 8} more)" if len(entries) > 8 else "")
        return (f"  the deepest directory of that key this store actually has is {here}, "
                f"holding {len(entries)} entr{'y' if len(entries) == 1 else 'ies'}"
                + (f": {shown}" if entries else " (empty)"))

    def get(self, locator: str, out: Path, expect_store_id: str | None = None) -> str:
        self.require_store_id(expect_store_id)
        key, want = split_locator(locator, self.spec)
        src = self._path(key)
        if not src.is_file():
            raise Red(
                f"{src} is not there — the record names an object this store does not hold.\n"
                f"  this runner's `{self.spec}` is {self.store_id()}\n"
                f"{self._absence_diagnosis(src)}\n"
                f"  🚨 A `file:` store is a PATH: if the job that WROTE this object printed a "
                f"different identity above its `store: wrote …` line, the two jobs are on two "
                f"different shares and no amount of waiting will make this object appear. Pass "
                f"--expect-store-id (from `resolve`'s `store-id` output) on both sides to have that "
                f"refused at the write instead of discovered here (MeshWeaver #4761).")
        out.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, out)
        got = sha256_file(out)
        if want and got != want:
            out.unlink(missing_ok=True)
            raise Red(f"{src} read as sha256 {got}, but the locator attests {want} — the bytes are "
                      f"not the ones the record names. Not using them.")
        print(f"store: fetched {src} -> {out} (sha256 {got}) from {self.store_id()}")
        return got

    def probe(self, locator: str) -> bool:
        key, want = split_locator(locator, self.spec)
        src = self._path(key)
        return src.is_file() and (not want or sha256_file(src) == want)


def split_locator(locator: str, spec: str) -> tuple[str, str]:
    """`<spec>/<key>#sha256=<hex>` -> (key, hex). Refuses a locator from a DIFFERENT store: a lane
    that fetched with the wrong account would otherwise fail with a confusing 404."""
    body, _, frag = locator.partition("#")
    want = ""
    if frag:
        if not frag.startswith("sha256="):
            raise Red(f"locator fragment '{frag}' is not a sha256= digest")
        want = frag[len("sha256="):]
        if not re.fullmatch(r"[0-9a-f]{64}", want):
            raise Red(f"locator fragment '{frag}' is not a 64-hex sha256")
    if not body.startswith(spec + "/"):
        raise Red(f"locator '{body}' does not belong to store '{spec}' — the lane resolved one store "
                  f"and the record names another; refusing to guess which is right")
    return body[len(spec) + 1:], want


def make_store(spec: str, az: str = "az") -> Store:
    spec = (spec or "").strip()
    if spec in ("", "gha", "none"):
        return Store()
    if spec.startswith("azblob:"):
        rest = spec[len("azblob:"):]
        parts = rest.split("/", 2)
        if len(parts) < 2 or not parts[0] or not parts[1]:
            raise Red(f"store spec '{spec}' is not azblob:<account>/<container>[/<prefix>]")
        return AzBlob(parts[0], parts[1], parts[2] if len(parts) > 2 else "", az=az)
    if spec.startswith("file:"):
        return FileStore(spec[len("file:"):])
    raise Red(f"unknown store '{spec}'. Known: gha, azblob:<account>/<container>[/<prefix>], file:<dir>")


# ───────────────────────────────── commands ─────────────────────────────────

def emit(name: str, value: str) -> None:
    print(f"{name}={value}")
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as f:
            f.write(f"{name}={value}\n")


def summary(text: str) -> None:
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as f:
            f.write(text + "\n")


def resolve_cmd(a: argparse.Namespace) -> int:
    """Which store this run will use, and WHY — the line that keeps 'it used GitHub artifacts'
    from being a silent outcome."""
    declared = (a.declared or "").strip()
    if not declared or declared in ("gha", "none"):
        if a.require:
            raise Red("--require was given but no store is declared: this caller must name one "
                      "(the credential or the repository variable is missing).")
        emit("store", "gha")
        emit("kind", "gha")
        emit("store-id", Store().store_id())
        print("store: gha — no object store is declared for this run; artifacts stay on GitHub")
        summary(f"**Artifact store:** `gha` — {Store().describe()}")
        return 0
    store = make_store(declared, az=a.az)
    # A store is only usable if this runner can actually reach it — the ambient azure/login for a
    # container, a mount for a directory. Probing here, ONCE, in `resolve`, is what turns "the
    # credential is missing" into one named failure instead of forty confusing upload errors
    # scattered across the matrix.
    why = store.reachable()
    if why:
        msg = f"the declared store {store.spec} is not usable on this runner: {why}"
        if a.require:
            raise Red(msg + "  (--require: a declared store that cannot be used is RED, never a "
                            "quiet return to GitHub artifacts.)")
        print(f"::warning::{msg} — falling back to GitHub artifacts for this run")
        emit("store", "gha")
        emit("kind", "gha")
        emit("store-id", Store().store_id())
        summary(f"**Artifact store:** `gha` (declared `{store.spec}`, unreachable — see the warning)")
        return 0
    emit("store", store.spec)
    emit("kind", store.kind)
    # 🚨 WHICH store, not where it is addressed. Every other job of this run passes this back as
    # --expect-store-id, so a runner standing on a different share is RED at its first store
    # operation instead of writing bytes nobody can read (#4761).
    identity = store.store_id()
    emit("store-id", identity)
    print(f"store: {store.spec} — {store.describe()}")
    print(f"store-id: {identity}")
    summary(f"**Artifact store:** `{store.spec}` — {store.describe()}\n\n"
            f"**Store identity:** `{identity}` — every job of this run must resolve the same one.")
    return 0


def put_cmd(a: argparse.Namespace) -> int:
    store = make_store(a.store, az=a.az)
    locator = store.put(a.key, Path(a.file), expect_store_id=a.expect_store_id)
    emit("locator", locator)
    return 0


def get_cmd(a: argparse.Namespace) -> int:
    store = make_store(a.store, az=a.az)
    emit("sha256", store.get(a.locator, Path(a.out), expect_store_id=a.expect_store_id))
    return 0


def probe_cmd(a: argparse.Namespace) -> int:
    store = make_store(a.store, az=a.az)
    ok = store.probe(a.locator)
    emit("present", "true" if ok else "false")
    print(f"store: {a.locator} {'is present' if ok else 'is NOT present'}")
    return 0 if ok else 1


def prune_cmd(a: argparse.Namespace) -> int:
    """The belt to the account lifecycle policy's braces. Refuses a prefix-less sweep: the one
    mistake that cannot be undone here is deleting a prefix whose lifetime is someone else's."""
    store = make_store(a.store, az=a.az)
    if isinstance(store, Store) and store.kind == "gha":
        raise Red("prune needs an object store; 'gha' artifacts are pruned by their retention")
    if not a.prefix:
        raise Red("prune refuses to run without --prefix: a store-wide sweep is never what a lane means")
    import datetime as dt
    cutoff = dt.datetime.now(dt.timezone.utc) - dt.timedelta(days=a.older_than)
    full = f"{store.prefix}/{a.prefix}" if store.prefix else a.prefix
    r = store._az("list", "--container-name", store.container, "--prefix", full,
                  "--query", "[].{name:name,mod:properties.lastModified}", "-o", "json")
    blobs = json.loads(r.stdout or "[]")
    doomed = []
    for b in blobs:
        mod = b.get("mod")
        if not mod:
            continue
        when = dt.datetime.fromisoformat(str(mod).replace("Z", "+00:00"))
        if when < cutoff:
            doomed.append(b["name"])
    print(f"prune: {len(blobs)} blob(s) under {full}/, {len(doomed)} older than {a.older_than} day(s)")
    if a.dry_run:
        for n in doomed[:50]:
            print(f"  would delete {n}")
        emit("deleted", "0")
        return 0
    for i in range(0, len(doomed), 250):
        batch = doomed[i:i + 250]
        store._az("delete-batch", "--source", store.container, *sum([["--pattern", n] for n in batch], []))
    emit("deleted", str(len(doomed)))
    summary(f"**Store prune:** {len(doomed)} blob(s) under `{full}/` older than {a.older_than} day(s)")
    return 0


# ───────────────────────────────── self-test ─────────────────────────────────

FAKE_AZ = r"""#!/usr/bin/env python3
# A fake `az storage blob|container` over a directory, enough to exercise every rule in
# ci-artifact-store.py without Azure. Its state is $FAKE_AZ_ROOT.
import json, os, shutil, sys, datetime
root = os.environ["FAKE_AZ_ROOT"]
a = sys.argv[1:]
def opt(name, default=None):
    return a[a.index(name) + 1] if name in a else default
if a[:2] == ["storage", "container"] and a[2] == "show":
    sys.exit(0 if os.environ.get("FAKE_AZ_CONTAINER_OK", "1") == "1" else 1)
if a[:2] != ["storage", "blob"]:
    sys.stderr.write("fake az: unsupported command %r\n" % a); sys.exit(2)
verb = a[2]
name, container = opt("--name"), opt("--container-name")
path = os.path.join(root, container or "c", name or "")
meta = path + ".meta.json"
if verb == "show":
    if not os.path.exists(path):
        sys.stderr.write("BlobNotFound\n"); sys.exit(3)
    m = json.load(open(meta)) if os.path.exists(meta) else {}
    print(json.dumps({"name": name, "metadata": m,
                      "properties": {"lastModified": datetime.datetime.now(datetime.timezone.utc).isoformat()}}))
    sys.exit(0)
if verb == "upload":
    os.makedirs(os.path.dirname(path), exist_ok=True)
    shutil.copyfile(opt("--file"), path)
    md = {}
    if "--metadata" in a:
        for kv in a[a.index("--metadata") + 1:]:
            if kv.startswith("--"): break
            k, _, v = kv.partition("="); md[k] = v
    json.dump(md, open(meta, "w"))
    open(os.path.join(root, "uploads.log"), "a").write(name + "\n")
    sys.exit(0)
if verb == "download":
    if not os.path.exists(path):
        sys.stderr.write("BlobNotFound\n"); sys.exit(1)
    dst = opt("--file"); os.makedirs(os.path.dirname(dst) or ".", exist_ok=True)
    shutil.copyfile(path, dst); sys.exit(0)
sys.stderr.write("fake az: unsupported blob verb %r\n" % verb); sys.exit(2)
"""


def self_test() -> int:
    checks: list[tuple[str, bool, str]] = []

    def check(name: str, cond: bool, detail: str = "") -> None:
        checks.append((name, bool(cond), detail))

    tmp = Path(tempfile.mkdtemp(prefix="ci-artifact-store-selftest-"))
    try:
        bindir = tmp / "bin"
        bindir.mkdir()
        fake = bindir / "az"
        fake.write_text(FAKE_AZ)
        fake.chmod(0o755)
        root = tmp / "blobs"
        root.mkdir()
        os.environ["FAKE_AZ_ROOT"] = str(root)
        os.environ["FAKE_AZ_CONTAINER_OK"] = "1"
        az = str(fake)

        payload = tmp / "MeshWeaver.Plugin.AI.1.8.3.module.nupkg"
        payload.write_bytes(b"a module bundle" * 4096)
        digest = sha256_file(payload)

        s = make_store("azblob:meshweaverci/ciartifacts/plugins", az=az)

        # 1. spec parsing and the prefix
        check("azblob spec parses to account/container/prefix",
              (s.account, s.container, s.prefix) == ("meshweaverci", "ciartifacts", "plugins"))

        # 2. put -> locator carries the store spec and the sha256
        loc = s.put("modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg", payload)
        check("put returns a locator naming the store and the digest",
              loc == f"azblob:meshweaverci/ciartifacts/plugins/modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg#sha256={digest}",
              loc)

        # 3. put is idempotent by content — the second put uploads nothing
        uploads = (root / "uploads.log").read_text().count("\n")
        loc2 = s.put("modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg", payload)
        again = (root / "uploads.log").read_text().count("\n")
        check("a second put of identical bytes uploads nothing", uploads == again and loc2 == loc,
              f"{uploads} -> {again}")

        # 4. get round-trips and verifies
        out = tmp / "fetched.nupkg"
        got = s.get(loc, out)
        check("get round-trips the bytes and returns the digest",
              got == digest and out.read_bytes() == payload.read_bytes())

        # 5. 🚨 a locator whose digest does not match the bytes is REFUSED, and the bad file removed
        wrong = loc.split("#")[0] + "#sha256=" + ("0" * 64)
        try:
            s.get(wrong, tmp / "bad.nupkg")
            check("a locator whose sha does not match the bytes is refused", False, "no refusal")
        except Red as e:
            check("a locator whose sha does not match the bytes is refused",
                  "not the ones the record names" in str(e) and not (tmp / "bad.nupkg").exists())

        # 6. 🚨 a locator from ANOTHER store is refused rather than guessed at
        other = "azblob:someoneelse/ciartifacts/x/y.nupkg#sha256=" + digest
        try:
            s.get(other, tmp / "other.nupkg")
            check("a locator from a different store is refused", False, "no refusal")
        except Red as e:
            check("a locator from a different store is refused", "does not belong to store" in str(e))

        # 7. probe: present, absent, and present-but-different
        check("probe finds what put wrote", s.probe(loc))
        check("probe is false for an absent object",
              not s.probe(f"{s.spec}/modules/nope.nupkg#sha256={digest}"))
        check("probe is false when the digest differs", not s.probe(wrong))

        # 8. key grammar: traversal out of a lifecycle prefix is refused
        for bad in ("../escape", "/leading", "a/../../b", "back\\slash"):
            try:
                s.put(bad, payload)
                check(f"key {bad!r} is refused", False, "accepted")
                break
            except Red:
                pass
        else:
            check("a key that escapes its prefix is refused", True)

        # 9. 🚨 THE DEGRADE RULE: no declared store resolves to gha, exit 0
        ns = argparse.Namespace(declared="", require=False, az=az)
        check("no declared store resolves to gha", resolve_cmd(ns) == 0)
        ns = argparse.Namespace(declared="gha", require=False, az=az)
        check("'gha' resolves to gha", resolve_cmd(ns) == 0)

        # 10. 🚨 …but --require makes a missing store RED
        try:
            resolve_cmd(argparse.Namespace(declared="", require=True, az=az))
            check("--require with no store is red", False, "returned 0")
        except Red as e:
            check("--require with no store is red", "must name one" in str(e))

        # 11. 🚨 a DECLARED store that cannot be reached is red under --require, and only warns without
        os.environ["FAKE_AZ_CONTAINER_OK"] = "0"
        try:
            resolve_cmd(argparse.Namespace(declared="azblob:meshweaverci/ciartifacts", require=True, az=az))
            check("an unreachable declared store is red under --require", False, "returned 0")
        except Red as e:
            check("an unreachable declared store is red under --require", "not usable on this runner" in str(e))
        check("an unreachable declared store degrades to gha without --require",
              resolve_cmd(argparse.Namespace(declared="azblob:meshweaverci/ciartifacts",
                                             require=False, az=az)) == 0)
        os.environ["FAKE_AZ_CONTAINER_OK"] = "1"

        # 12. the gha store moves no bytes and says so rather than pretending
        try:
            Store().put("k", payload)
            check("the gha store refuses put", False, "accepted")
        except Red as e:
            check("the gha store refuses put", "moves no bytes" in str(e))

        # 13. an unknown store name is refused, never treated as gha
        try:
            make_store("s3:bucket/x")
            check("an unknown store scheme is refused", False, "accepted")
        except Red as e:
            check("an unknown store scheme is refused", "unknown store" in str(e))

        # 14. a malformed azblob spec is refused before any `az` launch
        for bad in ("azblob:", "azblob:acct", "azblob:ACCT/c", "azblob:acct/UPPER"):
            try:
                make_store(bad)
                check(f"malformed spec {bad!r} is refused", False, "accepted")
                break
            except Red:
                pass
        else:
            check("a malformed azblob spec is refused before any az launch", True)

        # 15. THE `file:` BACKEND — a mounted share, exercised against a real directory: the same
        # locator shape, the same content-idempotence, the same digest refusal, no credential.
        share = tmp / "share"
        share.mkdir()
        fs = make_store(f"file:{share}")
        floc = fs.put("modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg", payload)
        check("file: put returns a locator naming the store and the digest",
              floc == f"file:{share}/modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg#sha256={digest}", floc)
        fout = tmp / "from-share.nupkg"
        check("file: get round-trips and verifies",
              fs.get(floc, fout) == digest and fout.read_bytes() == payload.read_bytes())
        mtime = (share / "modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg").stat().st_mtime_ns
        fs.put("modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg", payload)
        check("file: a second put of identical bytes writes nothing",
              (share / "modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg").stat().st_mtime_ns == mtime)
        check("file: no temp file is left behind",
              not list(share.rglob("*.tmp.*")))
        check("file: put reads the DESTINATION back — the digest it prints is dst's, not the source's",
              sha256_file(share / "modules/Plugins/MeshWeaver.AI/key123/bundle.nupkg") == digest)
        try:
            fs.get(floc.split("#")[0] + "#sha256=" + ("1" * 64), tmp / "bad2.nupkg")
            check("file: a digest mismatch is refused", False, "no refusal")
        except Red:
            check("file: a digest mismatch is refused", not (tmp / "bad2.nupkg").exists())
        check("file: an unmounted directory is reported unreachable, not crashed",
              "not a directory" in make_store("file:" + str(tmp / "no-such-mount")).reachable())
        check("file: a mounted, writable directory is reachable", fs.reachable() == "")
        check("file: resolve --require is green for a usable share",
              resolve_cmd(argparse.Namespace(declared=f"file:{share}", require=True, az=az)) == 0)
        try:
            resolve_cmd(argparse.Namespace(declared=f"file:{tmp / 'no-such-mount'}", require=True, az=az))
            check("file: resolve --require is red for an unmounted share", False, "returned 0")
        except Red:
            check("file: resolve --require is red for an unmounted share", True)

        # 15b. 🚨 THE CROSS-POOL CONTROL — the one #4761 was missing. There was NO test that wrote
        # on one mount and read from another, so "the shared store works" was something the cluster
        # config implied and no run ever proved. Two directories stand in for the two runner pools'
        # `/ci-artifacts`: the fleet's real pair are two dynamically-provisioned Azure Files shares
        # behind one path, and from inside a job they are exactly as indistinguishable as these.
        pool_a = tmp / "pool-a" / "ci-artifacts"
        pool_b = tmp / "pool-b" / "ci-artifacts"
        pool_a.mkdir(parents=True)
        pool_b.mkdir(parents=True)
        sa, sb = make_store(f"file:{pool_a}"), make_store(f"file:{pool_b}")
        id_a, id_b = sa.store_id(), sb.store_id()
        check("two mounts of the same shape have DIFFERENT store identities", id_a != id_b,
              f"{id_a} vs {id_b}")
        # 🚨 …and the negative control, which is what keeps this check from reddening a lane that
        # works: ONE directory reached by two different paths is ONE store. Without this, a symlink
        # or a trailing slash would read as a cross-pool handoff and refuse a correct run.
        alias = tmp / "pool-a-alias"
        os.symlink(str(pool_a), str(alias))
        check("one share reached by two paths has ONE store identity",
              make_store(f"file:{alias}").store_id() == id_a,
              f"{make_store(f'file:{alias}').store_id()} vs {id_a}")
        check("a store identity is stable across resolutions", make_store(f"file:{pool_a}").store_id() == id_a)

        key = "runs/Systemorph/MeshWeaver.Plugins/35401146210/1/workspace-build.tar"
        aloc = sa.put(key, payload, expect_store_id=id_a)
        check("put on the run's own store is allowed", aloc.startswith(f"file:{pool_a}/"))
        # The producer wrote on pool A. A consumer on pool B, addressing the byte-identical path,
        # is REFUSED at the get — naming the mechanism, not the absence.
        try:
            sb.get(f"file:{pool_b}/{key}", tmp / "cross.tar", expect_store_id=id_a)
            check("a get from the OTHER pool is refused, naming the mechanism", False, "no refusal")
        except Red as e:
            check("a get from the OTHER pool is refused, naming the mechanism",
                  "NOT on the store this run resolved" in str(e) and id_a in str(e) and id_b in str(e),
                  str(e)[:200])
        # And the earlier, cheaper failure: a PRODUCER on the wrong pool never writes at all, so the
        # run dies at its first store operation instead of five minutes and eight jobs later.
        try:
            sb.put(key, payload, expect_store_id=id_a)
            check("a put from the OTHER pool is refused BEFORE any bytes move", False, "no refusal")
        except Red as e:
            check("a put from the OTHER pool is refused BEFORE any bytes move",
                  "NOT on the store this run resolved" in str(e) and not (pool_b / key).exists())
        # Without the identity the absence is all a consumer can see — and the message must at
        # least hand the reader the store it IS on and what that store does hold.
        try:
            sb.get(f"file:{pool_b}/{key}", tmp / "cross2.tar")
            check("an absent object names this store's identity and what it holds", False, "no refusal")
        except Red as e:
            check("an absent object names this store's identity and what it holds",
                  "is not there" in str(e) and id_b in str(e) and "#4761" in str(e), str(e)[:200])

        # 15e. 🚨 THE LINUX BRANCH, which is the ONLY one a runner ever takes. These are the real
        # shapes: the two runner namespaces' `ci-artifacts` PVCs are separately provisioned Azure
        # Files shares, and `ci-platform` is the fleet's existing counter-example — two static PVs
        # over ONE volumeHandle, so both namespaces see one share and its identity agrees.
        silos = ["21 20 0:20 / / rw,relatime - overlay overlay rw,lowerdir=/x",
                 "94 21 0:94 / /ci-artifacts rw,relatime - cifs "
                 "//f45d8e671caa74b96a6c37f.file.core.windows.net/pvc-f3e4220c-5f4a-45fd-8c49-04fc3e2e330b rw,vers=3.1.1",
                 "95 21 0:95 / /opt/platform ro,relatime - cifs "
                 "//f45d8e671caa74b96a6c37f.file.core.windows.net/pvc-20a8d165-9942-45aa-ba6b-ff3f1a8eadc6 ro,vers=3.1.1"]
        dind = ["21 20 0:20 / / rw,relatime - overlay overlay rw,lowerdir=/y",
                "94 21 0:94 / /ci-artifacts rw,relatime - cifs "
                "//f45d8e671caa74b96a6c37f.file.core.windows.net/pvc-d9f747ad-2304-44f9-87d2-cc32f0e7318e rw,vers=3.1.1",
                "95 21 0:95 / /opt/platform ro,relatime - cifs "
                "//f45d8e671caa74b96a6c37f.file.core.windows.net/pvc-20a8d165-9942-45aa-ba6b-ff3f1a8eadc6 ro,vers=3.1.1"]
        mi_silos = mount_identity(Path("/ci-artifacts"), silos)
        mi_dind = mount_identity(Path("/ci-artifacts"), dind)
        check("mountinfo: the SAME path on the two runner pools is TWO stores — #4761 in one line",
              mi_silos != mi_dind and "pvc-f3e4220c" in mi_silos and "pvc-d9f747ad" in mi_dind,
              f"{mi_silos} vs {mi_dind}")
        check("mountinfo: ONE share mounted into both namespaces is ONE store (the ci-platform shape)",
              mount_identity(Path("/opt/platform"), silos) == mount_identity(Path("/opt/platform"), dind))
        check("mountinfo: the longest matching mount point wins, never the root overlay",
              "cifs" in mi_silos and "overlay" not in mi_silos, mi_silos)
        check("mountinfo: a directory INSIDE the share belongs to the share, and is distinguished",
              mount_identity(Path("/ci-artifacts/runs"), silos).startswith(mi_silos)
              and mount_identity(Path("/ci-artifacts/runs"), silos) != mi_silos)
        check("mountinfo: a path on no listed mount falls back rather than crashing",
              mount_identity(Path(str(pool_a)), []).startswith("dev:"))
        check("mountinfo: an escaped mount point decodes", _unescape_mountinfo(r"/ci\040artifacts") == "/ci artifacts")

        # 15c. 🚨 AN EMPTY --expect-store-id IS RED, NEVER A SKIP. A lane that wires the check up
        # and passes an unset variable would otherwise get a check that passes on no evidence —
        # the same defect as a gate whose `if:` asks whether its input exists.
        try:
            sa.put(key, payload, expect_store_id="")
            check("an EMPTY --expect-store-id is red, never a silent pass", False, "accepted")
        except Red as e:
            check("an EMPTY --expect-store-id is red, never a silent pass", "EMPTY value" in str(e))
        try:
            sa.get(aloc, tmp / "empty-id.tar", expect_store_id="   ")
            check("an empty --expect-store-id is red on get too", False, "accepted")
        except Red as e:
            check("an empty --expect-store-id is red on get too", "EMPTY value" in str(e))
        check("an ABSENT --expect-store-id still works (a local call, an older lane)",
              sa.get(aloc, tmp / "no-id.tar") == digest)

        # 15d. 🚨 `put` VERIFIES THE DESTINATION — and here is the proof it can FAIL. Before #4761
        # the success line took its byte count and its sha256 from the SOURCE and never looked at
        # `dst`, so it could not be wrong; these three break the publish deliberately and each one
        # must go RED at the producer. A verification nobody has watched fail is not a verification.
        class BreakingStore(FileStore):
            """A FileStore whose swap goes wrong in exactly the ways a network filesystem's can."""

            def __init__(self, root: str, how: str):
                super().__init__(root)
                self.how = how

            def _publish(self, tmp_: Path, dst_: Path) -> None:
                super()._publish(tmp_, dst_)
                if self.how == "vanish":            # the name is published and the bytes are not there
                    os.unlink(dst_)
                elif self.how == "short":           # a truncated write — the shape a torn copy leaves
                    with open(dst_, "r+b") as f:
                        f.truncate(max(0, dst_.stat().st_size - 4096))
                elif self.how == "corrupt":         # same length, different bytes
                    with open(dst_, "r+b") as f:
                        f.seek(17)
                        f.write(b"\x00\x01\x02\x03")
                elif self.how == "copy":            # a "rename" that was really a copy: staging stays
                    shutil.copyfile(dst_, tmp_)

        for how, told, phrase in (("vanish", "the destination vanishes after the rename", "did NOT land"),
                                  ("short", "the destination is truncated", "landed SHORT"),
                                  ("corrupt", "the destination holds different bytes", "landed CORRUPT"),
                                  ("copy", "the 'rename' was really a copy", "left its staging file")):
            broken = tmp / f"broken-{how}"
            broken.mkdir()
            try:
                BreakingStore(str(broken), how).put("runs/x/1/1/bundle.tar", payload)
                check(f"put goes RED when {told}", False, "reported success")
            except Red as e:
                check(f"put goes RED when {told}",
                      phrase in str(e) and "bundle.tar" in str(e), str(e)[:200])
        # …and the SAME store class, unbroken, is green over the same payload — otherwise the four
        # reds above would be consistent with a `put` that simply always fails.
        clean = tmp / "broken-none"
        clean.mkdir()
        check("the same put is GREEN when the destination is intact",
              BreakingStore(str(clean), "none").put("runs/x/1/1/bundle.tar", payload)
              == f"file:{clean}/runs/x/1/1/bundle.tar#sha256={digest}")

        # 16. 🚨 ABSENT vs UNREADABLE. A `show` that says the blob is not there is `None` whatever
        # exit code the CLI chose; a `show` that fails for any OTHER reason RAISES, because a store
        # that cannot be read must never look like an empty one — `probe` would answer "absent" for
        # bytes that are there and the lane would rebuild a module it already had, forever.
        notfound = bindir / "az-notfound"
        notfound.write_text("#!/bin/sh\necho 'ErrorCode:BlobNotFound' >&2\nexit 1\n")
        notfound.chmod(0o755)
        nf = make_store("azblob:meshweaverci/ciartifacts", az=str(notfound))
        check("a not-found `show` is absent, not an error", nf._exists_sha("x") is None)
        denied = bindir / "az-denied"
        denied.write_text("#!/bin/sh\necho 'AuthorizationPermissionMismatch' >&2\nexit 1\n")
        denied.chmod(0o755)
        dn = make_store("azblob:meshweaverci/ciartifacts", az=str(denied))
        try:
            dn._exists_sha("x")
            check("an unreadable store raises rather than reading as empty", False, "returned")
        except Red as e:
            check("an unreadable store raises rather than reading as empty",
                  "did not say the blob is absent" in str(e))

        # 17. an `az` that fails is a RED naming the account and the verb, never a silent skip
        broken = bindir / "az-broken"
        broken.write_text("#!/bin/sh\necho 'AuthorizationPermissionMismatch' >&2\nexit 1\n")
        broken.chmod(0o755)
        try:
            make_store("azblob:meshweaverci/ciartifacts", az=str(broken)).put("k/x.nupkg", payload)
            check("an az failure is red", False, "accepted")
        except Red as e:
            check("an az failure is red and names the account",
                  "meshweaverci/ciartifacts" in str(e) and "AuthorizationPermissionMismatch" in str(e))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
        os.environ.pop("FAKE_AZ_ROOT", None)
        os.environ.pop("FAKE_AZ_CONTAINER_OK", None)

    bad = [c for c in checks if not c[1]]
    for name, ok, detail in checks:
        print(f"{'ok  ' if ok else 'FAIL'} {name}" + (f"   [{detail}]" if detail and not ok else ""))
    if bad:
        print(f"::error::ci-artifact-store self-test: {len(bad)} of {len(checks)} rule(s) failed")
        return 1
    print(f"ci-artifact-store self-test: {len(checks)} rules, all green")
    return 0


EXPECT_HELP = ("the `store-id` this run resolved (from `resolve`). When given, the command refuses "
               "to move bytes unless THIS runner is standing on that same store — see THE PATH IS "
               "NOT THE IDENTITY at the top of this file. An EMPTY value is RED, never a skip.")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--az", default=os.environ.get("MW_AZ_CLI", "az"), help="the az CLI to launch")
    sub = ap.add_subparsers(dest="cmd")

    p = sub.add_parser("resolve"); p.add_argument("--declared", default=""); p.add_argument("--require", action="store_true")
    p = sub.add_parser("put"); p.add_argument("--store", required=True); p.add_argument("--key", required=True); p.add_argument("--file", required=True); p.add_argument("--expect-store-id", default=None, help=EXPECT_HELP)
    p = sub.add_parser("get"); p.add_argument("--store", required=True); p.add_argument("--locator", required=True); p.add_argument("--out", required=True); p.add_argument("--expect-store-id", default=None, help=EXPECT_HELP)
    p = sub.add_parser("probe"); p.add_argument("--store", required=True); p.add_argument("--locator", required=True)
    p = sub.add_parser("prune"); p.add_argument("--store", required=True); p.add_argument("--prefix", default=""); p.add_argument("--older-than", type=float, required=True); p.add_argument("--dry-run", action="store_true")
    ap.add_argument("--self-test", action="store_true")

    a = ap.parse_args(argv)
    if a.self_test:
        return self_test()
    if not a.cmd:
        ap.print_help()
        return 2
    try:
        return {"resolve": resolve_cmd, "put": put_cmd, "get": get_cmd,
                "probe": probe_cmd, "prune": prune_cmd}[a.cmd](a)
    except Red as e:
        print(f"::error::ci-artifact-store {a.cmd}: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
