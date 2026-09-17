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
                                          unusable store RED instead of `gha`
    put     --store SPEC --key K --file F  upload; prints `locator=<spec>#sha256=<hex>`
    get     --store SPEC --locator L --out F   download and VERIFY the sha in the locator
    probe   --store SPEC --locator L       exit 0 iff the object is there (and, with --sha, matches)
    prune   --store SPEC --prefix P --older-than DAYS   the belt to the account's lifecycle braces
    --self-test                            every rule below, against a fake `az` on PATH

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


class Store:
    """Base: `gha` — the mode in which this script moves no bytes at all."""

    kind = "gha"
    spec = "gha"

    def describe(self) -> str:
        return "GitHub Actions artifacts (the caller's own upload/download steps)"

    def reachable(self) -> str:
        """'' when this runner can use the store, else why it cannot — one sentence, for the log."""
        return ""

    def put(self, key: str, file: Path) -> str:
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

    def put(self, key: str, file: Path) -> str:
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

    def get(self, locator: str, out: Path) -> str:
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
        return f"the directory {self.root} (a mounted share; no credential is involved)"

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

    def put(self, key: str, file: Path) -> str:
        if not file.is_file():
            raise Red(f"nothing to upload: {file} is not a file")
        dst = self._path(key)
        digest = sha256_file(file)
        if dst.is_file() and sha256_file(dst) == digest:
            print(f"store: {dst} is already there with sha256 {digest} — nothing written")
            return f"{self.spec}/{key}#sha256={digest}"
        dst.parent.mkdir(parents=True, exist_ok=True)
        # 🚨 Write-then-rename. Concurrent pack legs of the fleet share this directory, and a reader
        # that opens a half-written bundle gets a sha mismatch at best and a corrupt nupkg at worst.
        # `os.replace` within one filesystem is atomic; SMB honours it for a same-directory rename.
        tmp = dst.with_name(dst.name + f".tmp.{os.getpid()}")
        shutil.copyfile(file, tmp)
        os.replace(tmp, dst)
        print(f"store: wrote {file.name} ({file.stat().st_size} bytes) to {dst}, sha256 {digest}")
        return f"{self.spec}/{key}#sha256={digest}"

    def get(self, locator: str, out: Path) -> str:
        key, want = split_locator(locator, self.spec)
        src = self._path(key)
        if not src.is_file():
            raise Red(f"{src} is not there — the record names an object this store does not hold")
        out.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(src, out)
        got = sha256_file(out)
        if want and got != want:
            out.unlink(missing_ok=True)
            raise Red(f"{src} read as sha256 {got}, but the locator attests {want} — the bytes are "
                      f"not the ones the record names. Not using them.")
        print(f"store: fetched {src} -> {out} (sha256 {got})")
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
        summary(f"**Artifact store:** `gha` (declared `{store.spec}`, unreachable — see the warning)")
        return 0
    emit("store", store.spec)
    emit("kind", store.kind)
    print(f"store: {store.spec} — {store.describe()}")
    summary(f"**Artifact store:** `{store.spec}` — {store.describe()}")
    return 0


def put_cmd(a: argparse.Namespace) -> int:
    store = make_store(a.store, az=a.az)
    locator = store.put(a.key, Path(a.file))
    emit("locator", locator)
    return 0


def get_cmd(a: argparse.Namespace) -> int:
    store = make_store(a.store, az=a.az)
    emit("sha256", store.get(a.locator, Path(a.out)))
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


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--az", default=os.environ.get("MW_AZ_CLI", "az"), help="the az CLI to launch")
    sub = ap.add_subparsers(dest="cmd")

    p = sub.add_parser("resolve"); p.add_argument("--declared", default=""); p.add_argument("--require", action="store_true")
    p = sub.add_parser("put"); p.add_argument("--store", required=True); p.add_argument("--key", required=True); p.add_argument("--file", required=True)
    p = sub.add_parser("get"); p.add_argument("--store", required=True); p.add_argument("--locator", required=True); p.add_argument("--out", required=True)
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
