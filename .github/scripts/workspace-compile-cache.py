#!/usr/bin/env python3
"""workspace-compile-cache.py — reuse an UNCHANGED container entry's compile across runs.

    workspace-compile-cache.py plan    --repo R --modules @sel.json --salt S --out plan.json
    workspace-compile-cache.py restore --plan plan.json --store S --store-id ID --stage DIR --misses FILE
    workspace-compile-cache.py merge   --plan plan.json --stage DIR --out OUT
    workspace-compile-cache.py save    --plan plan.json --out OUT --store S --store-id ID
    workspace-compile-cache.py dir-hash DIR
    workspace-compile-cache.py --self-test

WHY (MeshWeaver.Plugins Hosting/CiProfile.md → "Compile cache"). `build-workspace` compiles every
selected `build: container` entry in one Roslyn workspace (`mw-plugin-test build-project`). On a pull
request the selection always carries the gates' ALWAYS set, and `MeshWeaver.AI`'s closure alone took
350 s of a 356.5 s build (run 42517167915, a Hosting.Grpc fix that touched nothing AI compiles) —
the same bytes, recompiled on every run. The module ledger and the publication reuse key whole
BUNDLES on the package's `moduleVersion`, which CI-written bookkeeping (floor stamps, locks) and
node edits move although no compiler input changed; this cache keys only what the compiler reads.

THE KEY of one entry = sha256 over:
  * `msbuild-closure-key.py --no-targets` of the entry's project in the checked-out tree: every project
    directory in its ProjectReference closure (git tree hash), every MSBuild file it evaluates, every
    linked item, the repository-root MSBuild inputs — with the platform roots (`$(MeshWeaverRoot)`,
    `$(MeshWeaverRefs)`, …) declared EXTERNAL, because in the container build they resolve to the
    image, which the salt names by digest;
  * the caller's SALT: the platform image digest, the tester digest, the content hash of the
    module-libraries shelf and of the build extension published for this run, the accept and
    superseded flags, the platform ref and the SDK — everything else the compiler reads;
  * the entry's module name and project path, and this script's RECIPE version.
A refusal (an input the closure key cannot establish) or a closure path that differs from HEAD in the
working tree is NO KEY: the entry is built, never served.

WHAT IS CACHED is exactly what the pack legs read of that entry from the workspace output:
`<Module>/`, `<Module>.closure.txt`, every closure sibling's `<Sibling>/` the build emitted, and the
build log of the run that compiled it (the binding-identity line and the uncarried-asset ENFORCED line
come from the same tester digest the key names). The archive is read only through the sha256 recorded
beside it; a missing record, a mismatch, an archive without the entry's assembly or closure manifest,
or any store error is a MISS. Fail-open: a miss builds as before; nothing here can red a run.

WHO WRITES — AND WHY A PULL REQUEST CANNOT POISON IT. The store is a share every job can write, so a
path or a workflow `if:` is no boundary. An entry is served only when a TRUNK signature (Ed25519, key in
a GitHub environment admitting the default branch only — ci_cache_signature.py) vouches for the digest
of its archive and the fetched bytes match that digest. A trusted run `save`s (archive + digest) and
hands the claims to the one job holding the key (`sign`) through its own job outputs; a pull request
neither saves nor signs, and anything it plants — unsigned, signed by its own key, or a replaced
archive or digest — is a miss. With no key pinned, nothing is served.

MERGE never overwrites a file this run's own build emitted (a sibling compiled now from the same
sources wins over the cached copy), and appends each served entry's log under a header naming the key.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path

RECIPE = "workspace-compile-cache/v1"
PREFIX = "cache/workspace-compile/"
CACHED_LOG = ".compiled-by.workspace-build.log"   # the compiling run's log, at the archive root
# The properties that, in the container build, resolve OUTSIDE the mounted checkout (the image's /app,
# a platform checkout that is not mounted). The salt names the image by digest.
EXTERNAL_PROPERTIES = ("MeshWeaverRoot", "MeshWeaverRefs", "_MeshWeaverCoreRefsPath", "MeshWeaverRefsRuntimeOnly")
HERE = Path(__file__).resolve().parent
CLOSURE_KEY = HERE / "msbuild-closure-key.py"
STORE_PY = HERE / "ci-artifact-store.py"
sys.path.insert(0, str(HERE))
import ci_cache_signature as sig  # noqa: E402


def say(msg: str) -> None:
    print(msg, flush=True)


# ─────────────────────────────── plan: the key of every container entry ───────────────────────────────

def closure_key(repo: Path, project: str, salt: str) -> tuple[str | None, list[str], str]:
    """(key, input lines, reason) — key None on a refusal."""
    cmd = [sys.executable, str(CLOSURE_KEY), "--repo", str(repo), "--project", project, "--salt", salt, "--no-targets"]
    for p in EXTERNAL_PROPERTIES:
        cmd += ["--external", p]
    r = subprocess.run(cmd, capture_output=True, text=True, env={k: v for k, v in os.environ.items() if k != "GITHUB_OUTPUT"})
    lines = [l.strip() for l in r.stdout.splitlines() if l.startswith("  ")]
    key = next((l[4:] for l in r.stdout.splitlines() if l.startswith("key=")), None)
    if r.returncode != 0 or not key or len(key) != 64:
        reason = next((l for l in r.stdout.splitlines() if "refused" in l), r.stderr.strip()[-300:] or f"exit {r.returncode}")
        return None, lines, reason
    return key, lines, ""


def dirty_paths(repo: Path, lines: list[str]) -> list[str]:
    """Closure paths whose WORKING TREE differs from HEAD (the closure key reads HEAD's objects)."""
    paths = []
    for l in lines:
        parts = l.split(" ")
        if parts[0] in ("dir", "file", "input") and len(parts) >= 2:
            paths.append(parts[1])
    if not paths:
        return []
    # NUL-delimited (`-z`): a non-ASCII path would otherwise arrive C-quoted and name no file.
    r = subprocess.run(["git", "-C", str(repo), "status", "--porcelain", "-z", "--untracked-files=all", "--ignored=no",
                        "--", *paths], capture_output=True, text=True, errors="surrogateescape")
    if r.returncode != 0:
        return ["<git status failed>"]
    # Each record is `XY <path>`; a rename's source follows as its own NUL-terminated field.
    return [rec[3:] for rec in r.stdout.split("\0") if len(rec) > 3 and rec[2] == " "]


def plan(repo: Path, modules: list[dict], salt: str) -> list[dict]:
    out = []
    for e in modules:
        if e.get("build") != "container":
            continue
        module, project = e["module"], e["project"]
        entry = {"module": module, "project": project, "key": None, "reason": ""}
        ck, lines, why = closure_key(repo, project, salt)
        if ck is None:
            entry["reason"] = f"no key: {why}"
        else:
            dirty = dirty_paths(repo, lines)
            if dirty:
                entry["reason"] = f"no key: the working tree differs from HEAD under the closure ({dirty[0]}{f' +{len(dirty) - 1}' if len(dirty) > 1 else ''})"
            else:
                entry["key"] = hashlib.sha256(f"{RECIPE}\n{module}\n{project}\n{ck}".encode()).hexdigest()
        out.append(entry)
    return out


# ─────────────────────────────── the store ───────────────────────────────
# An entry is three objects under `cache/workspace-compile/<Module>/<key>`: the archive (`.tar`), its
# digest (`.tar.sha256`) and a TRUNK SIGNATURE over the claim "<recipe> <archive key> <digest>"
# (`.tar.sig`, ci_cache_signature.py). The share is writable by every job, pull requests included, so
# only the signature makes an entry trusted: an unsigned entry, one signed by an unpinned key, or one
# whose bytes no longer match the signed digest is a MISS. A pull request never writes and never
# signs; it reads what the trunk signed.

class Store:
    """get(key) → bytes or None; put(key, bytes) → bool. Every error is None / False."""

    def __init__(self, url: str, store_id: str):
        self.url, self.store_id = url, store_id

    def get(self, key: str) -> "bytes | None":
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / "obj"
            if store("probe", "--store", self.url, "--locator", f"{self.url}/{key}").returncode != 0:
                return None
            if store("get", "--store", self.url, "--expect-store-id", self.store_id,
                     "--locator", f"{self.url}/{key}", "--out", str(out)).returncode != 0:
                return None
            return out.read_bytes() if out.is_file() else None

    def put(self, key: str, data: bytes) -> bool:
        with tempfile.TemporaryDirectory() as tmp:
            f = Path(tmp) / "obj"
            f.write_bytes(data)
            return store("put", "--store", self.url, "--expect-store-id", self.store_id,
                         "--key", key, "--file", str(f)).returncode == 0


def store(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(STORE_PY), *args], capture_output=True, text=True)


def archive_key(e: dict) -> str:
    return f"{PREFIX}{e['module']}/{e['key']}.tar"


def valid_archive(tar_path: Path, module: str) -> bool:
    """The archive holds the entry's assembly and closure manifest, and nothing outside its own tree."""
    try:
        with tarfile.open(tar_path) as t:
            names = t.getnames()
            for m in t.getmembers():
                n = m.name
                if n.startswith("/") or ".." in Path(n).parts or not (m.isfile() or m.isdir()):
                    return False
    except (tarfile.TarError, OSError):
        return False
    names = {n.lstrip("./") for n in names}
    return f"{module}/{module}.dll" in names and f"{module}.closure.txt" in names


def trusted_fetch(st, key: str, trusted: list[str]) -> "tuple[bytes | None, str]":
    """(archive bytes, '') when the trunk signed this archive's digest, else (None, why)."""
    sha_raw, sig_raw = st.get(f"{key}.sha256"), st.get(f"{key}.sig")
    if sha_raw is None:
        return None, "not in the store"
    sha = sha_raw.decode("ascii", errors="replace").strip()
    if len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha):
        return None, "its digest record is malformed"
    if sig_raw is None or not sig.verify(sig.claim(RECIPE, key, sha), sig_raw.decode("ascii", "replace"), trusted):
        return None, "not signed by a trusted trunk key (a pull request's or an unsigned entry is never served)"
    data = st.get(key)
    if data is None:
        return None, "the archive is missing"
    if hashlib.sha256(data).hexdigest() != sha:
        return None, "the archive does not match its signed digest"
    return data, ""


def restore(entries: list[dict], st, trusted: list[str], stage: Path) -> list[dict]:
    """Fetch every keyed, TRUNK-SIGNED entry's archive into `stage/<module>/`; mark entry['hit']."""
    stage.mkdir(parents=True, exist_ok=True)
    for e in entries:
        e["hit"] = False
        if not e.get("key"):
            say(f"compile cache: {e['module']} — BUILD ({e['reason']})")
            continue
        if not trusted:
            e["reason"] = f"miss: no trusted signing key is pinned ({sig.PUBLIC_KEY_FILE})"
            say(f"compile cache: {e['module']} — BUILD ({e['reason']})")
            continue
        data, why = trusted_fetch(st, archive_key(e), trusted)
        if data is None:
            e["reason"] = f"miss: {why}"
            say(f"compile cache: {e['module']} — BUILD (key {e['key'][:12]}: {why})")
            continue
        tmp = stage / f".{e['module']}.tar"
        tmp.write_bytes(data)
        if not valid_archive(tmp, e["module"]):
            e["reason"] = "miss: the stored archive is not a complete entry"
            say(f"compile cache: {e['module']} — BUILD (unusable archive under key {e['key'][:12]})")
            tmp.unlink(missing_ok=True)
            continue
        dest = stage / e["module"]
        shutil.rmtree(dest, ignore_errors=True)
        dest.mkdir(parents=True)
        with tarfile.open(tmp) as t:
            t.extractall(dest, filter="data")
        tmp.unlink(missing_ok=True)
        e["hit"] = True
        e["reason"] = f"hit: key {e['key'][:12]} (trunk-signed)"
        say(f"compile cache: {e['module']} — REUSED (key {e['key'][:12]}, trunk-signed) — not compiled")
    return entries


def merge(entries: list[dict], stage: Path, out: Path) -> int:
    """Copy every served entry into the workspace output WITHOUT overwriting this run's own files."""
    out.mkdir(parents=True, exist_ok=True)
    log = out / "workspace-build.log"
    served = 0
    for e in entries:
        if not e.get("hit"):
            continue
        src = stage / e["module"]
        cached_log = src / CACHED_LOG
        for p in sorted(src.rglob("*")):
            rel = p.relative_to(src)
            if p == cached_log:
                continue
            target = out / rel
            if p.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            elif not target.exists():
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(p, target)
        with log.open("a", encoding="utf-8") as fh:
            fh.write(f"\n===== compile cache: {e['module']} REUSED under key {e['key']} — the log of the run that compiled it =====\n")
            if cached_log.is_file():
                fh.write(cached_log.read_text(encoding="utf-8", errors="replace"))
        served += 1
    return served


def entry_paths(out: Path, module: str) -> list[str]:
    """What the pack legs read of one entry: its tree, its closure manifest, its emitted siblings."""
    paths = [module, f"{module}.closure.txt"]
    closure = out / f"{module}.closure.txt"
    for name in closure.read_text(encoding="utf-8").splitlines():
        name = name.strip()
        if name and name != module and "/" not in name and name not in ("..", ".") and (out / name).is_dir():
            paths.append(name)
    return paths


def save(entries: list[dict], out: Path, st) -> list[dict]:
    """Keep every entry COMPILED in this run (archive, then its digest record) and return the CLAIMS to
    sign: [{"entry": <archive key>, "sha256": <digest>}]. Called on a trusted run only; nothing kept is
    trusted until `sign` (a job holding the trunk key) signs the claims, which travel there through the
    run's own job outputs — never read back from the share."""
    claims: list[dict] = []
    log = out / "workspace-build.log"
    for e in entries:
        if e.get("hit") or not e.get("key"):
            continue
        m = e["module"]
        if not (out / m / f"{m}.dll").is_file() or not (out / f"{m}.closure.txt").is_file():
            say(f"compile cache: {m} — not kept (the build emitted no complete entry)")
            continue
        with tempfile.TemporaryDirectory() as tmp:
            tar_path = Path(tmp) / f"{m}.tar"
            with tarfile.open(tar_path, "w") as t:
                for rel in entry_paths(out, m):
                    t.add(out / rel, arcname=rel)
                if log.is_file():
                    t.add(log, arcname=CACHED_LOG)
            data = tar_path.read_bytes()
        sha = hashlib.sha256(data).hexdigest()
        key = archive_key(e)
        if st.put(key, data) and st.put(f"{key}.sha256", (sha + "\n").encode()):
            claims.append({"entry": key, "sha256": sha})
            say(f"compile cache: {m} — kept under key {e['key'][:12]} ({len(data) // 1024} KiB), awaiting the trunk signature")
        else:
            say(f"::warning title=Compile cache not kept::{m} could not be stored under {key} — this run is unaffected")
    return claims


def sign_claims(claims: list[dict], st, private_pem: str) -> int:
    """Sign each claim with the trunk key and store the signature beside its archive."""
    n = 0
    for c in claims:
        entry, sha = str(c.get("entry", "")), str(c.get("sha256", ""))
        if not entry.startswith(PREFIX) or len(sha) != 64:
            say(f"compile cache: refusing to sign a malformed claim {c!r}")
            continue
        signature = sig.sign(sig.claim(RECIPE, entry, sha), private_pem)
        if signature is None:
            say("::warning title=Compile cache not signed::the trunk key could not sign — no entry of this run is trusted")
            return n
        if st.put(f"{entry}.sig", signature.encode("ascii")):
            n += 1
    return n


def dir_hash(d: Path) -> str:
    """Content hash of a directory: every file's relative path and bytes (symlinks by their target)."""
    h = hashlib.sha256()
    if not d.is_dir():
        return "absent"
    for p in sorted(d.rglob("*")):
        rel = p.relative_to(d).as_posix()
        if p.is_symlink():
            h.update(f"L {rel} {os.readlink(p)}\n".encode())
        elif p.is_file():
            h.update(f"F {rel} {hashlib.sha256(p.read_bytes()).hexdigest()}\n".encode())
    return h.hexdigest()


# ─────────────────────────────── self-test ───────────────────────────────

def self_test() -> int:
    failures: list[str] = []

    def check(label: str, ok: bool) -> None:
        print(f"  {'✓' if ok else '✗'} {label}")
        if not ok:
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp:
        repo = Path(tmp) / "repo"
        g = lambda *a: subprocess.run(["git", "-C", str(repo), *a], check=True, capture_output=True)  # noqa: E731

        def write(rel: str, text: str) -> None:
            f = repo / rel
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_text(text, encoding="utf-8")

        repo.mkdir()
        g("init", "-q"); g("config", "user.email", "t@example.com"); g("config", "user.name", "t")
        write("src/Directory.Build.props", '<Project><Import Project="$(MeshWeaverRefs)/platform-refs.props" Condition="false" /></Project>')
        write("src/Directory.Build.targets", '<Project><Target Name="T"><ItemGroup><X Include="@(Y->\'$(Z)/%(Filename)\')" /></ItemGroup></Target></Project>')
        write("src/Mod/Mod.csproj", '<Project><ItemGroup><ProjectReference Include="../Lib/Lib.csproj" />'
              '<ProjectReference Include="$(MeshWeaverRoot)/src/Core/Core.csproj" /></ItemGroup></Project>')
        write("src/Mod/M.cs", "class M {}")
        write("src/Lib/Lib.csproj", "<Project/>")
        write("src/Lib/L.cs", "class L {}")
        write("src/Other/Other.csproj", "<Project/>")
        write("src/Other/O.cs", "class O {}")
        write("AI/index.json", '{"minMeshVersion":"3.1.1"}')
        g("add", "."); g("commit", "-qm", "c1")
        sel = [{"module": "Mod", "project": "src/Mod/Mod.csproj", "build": "container"},
               {"module": "Sdk", "project": "src/Other/Other.csproj", "build": "sdk"}]
        p1 = plan(repo, sel, "image=a")
        check("only container entries are planned", [e["module"] for e in p1] == ["Mod"])
        check("a container entry over the platform roots gets a key", bool(p1[0]["key"]))
        k1 = p1[0]["key"]
        write("src/Other/O.cs", "class O { int x; }"); write("AI/index.json", '{"minMeshVersion":"3.1.2"}')
        g("commit", "-qam", "c2")
        check("a change OUTSIDE the closure (incl. a floor stamp in a package's index.json) keeps the key",
              plan(repo, sel, "image=a")[0]["key"] == k1)
        check("the salt (image digest, tester, shelf …) is part of the key", plan(repo, sel, "image=b")[0]["key"] != k1)
        write("src/Lib/L.cs", "class L { int y; }"); g("commit", "-qam", "c3")
        k3 = plan(repo, sel, "image=a")[0]["key"]
        check("a change in a REFERENCED project changes the key", k3 != k1)
        write("src/Mod/M.cs", "class M { int z; }")
        p_dirty = plan(repo, sel, "image=a")[0]
        check("an UNCOMMITTED change under the closure is no key (never a HEAD key for a different tree)",
              p_dirty["key"] is None and "working tree" in p_dirty["reason"])
        g("commit", "-qam", "c4")
        write("src/Mod/New.cs", "class N {}")
        check("an UNTRACKED file under the closure is no key", plan(repo, sel, "image=a")[0]["key"] is None)
        g("add", "."); g("commit", "-qm", "c5")
        write("src/Mod/Mod.csproj", '<Project><ItemGroup><Compile Include="$(Unknown)/S.cs" /></ItemGroup></Project>')
        g("commit", "-qam", "c6")
        pr = plan(repo, sel, "image=a")[0]
        check("a refusal by the closure key is no key, with the reason", pr["key"] is None and "refused" in pr["reason"])
        write("src/Mod/Mod.csproj", '<Project><ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup></Project>')
        g("commit", "-qam", "c7")
        entries = plan(repo, sel, "image=a")

        # ── save → sign → restore → merge, against an in-memory store ──
        class Mem:
            def __init__(self):
                self.objs: dict[str, bytes] = {}

            def get(self, key):
                return self.objs.get(key)

            def put(self, key, data):
                self.objs[key] = bytes(data)
                return True

        keys = sig.test_keypair(Path(tmp))
        check("openssl can make an Ed25519 test key (the signature path is exercised)", keys is not None)
        if keys is None:
            return 1
        trunk_priv, trunk_pub = keys
        other = Path(tmp) / "other"; other.mkdir()
        pr_priv, pr_pub = sig.test_keypair(other) or ("", "")
        trusted = sig.public_keys(trunk_pub)
        st = Mem()

        out1 = Path(tmp) / "out1"
        (out1 / "Mod").mkdir(parents=True); (out1 / "Lib").mkdir(); (out1 / "Unrelated").mkdir()
        (out1 / "Mod" / "Mod.dll").write_bytes(b"MOD")
        (out1 / "Lib" / "Lib.dll").write_bytes(b"LIB")
        (out1 / "Unrelated" / "U.dll").write_bytes(b"U")
        (out1 / "Mod.closure.txt").write_text("Mod\nLib\nImageOnly\n")
        (out1 / "workspace-build.log").write_text("x platform AssemblyVersion 3.1.0.0\nuncarried-asset refusal — ENFORCED for bundle target RID linux-x64\n")
        claims = save(entries, out1, st)
        check("save keeps a compiled entry and returns its claim to sign", len(claims) == 1 and claims[0]["entry"] == archive_key(entries[0]))
        with tarfile.open(fileobj=__import__("io").BytesIO(st.objs[archive_key(entries[0])])) as t:
            names = set(t.getnames())
        check("the archive holds the entry, its closure manifest and its EMITTED siblings only",
              {"Mod/Mod.dll", "Mod.closure.txt", "Lib/Lib.dll", CACHED_LOG} <= names
              and not any(n.startswith("Unrelated") or n.startswith("ImageOnly") for n in names))

        # 🚨 THE POISONING CASES: what a pull request can write must never be served.
        check("an UNSIGNED entry (what a pull request's own step can write) is IGNORED",
              not restore([dict(e) for e in entries], st, trusted, Path(tmp) / "s-unsigned")[0]["hit"])
        k = archive_key(entries[0])
        st.put(f"{k}.sig", (sig.sign(sig.claim(RECIPE, k, claims[0]["sha256"]), pr_priv) or "").encode())
        check("an entry signed by a NON-trunk key (a pull request's own key) is IGNORED",
              not restore([dict(e) for e in entries], st, trusted, Path(tmp) / "s-prkey")[0]["hit"])
        check("with no trusted key pinned nothing is served",
              not restore([dict(e) for e in entries], st, [], Path(tmp) / "s-nokey")[0]["hit"])
        check("sign signs every well-formed claim with the trunk key", sign_claims(claims, st, trunk_priv) == 1)
        stage = Path(tmp) / "stage"
        restored = restore([dict(e) for e in entries], st, trusted, stage)
        check("a TRUNK-SIGNED unchanged key is served from the store", restored[0]["hit"])
        out2 = Path(tmp) / "out2"
        (out2 / "Lib").mkdir(parents=True)
        (out2 / "Lib" / "Lib.dll").write_bytes(b"LIB-BUILT-NOW")
        (out2 / "workspace-build.log").write_text("this run's own build\n")
        check("merge serves the entry", merge(restored, stage, out2) == 1)
        check("merge lays out the entry where the pack legs read it",
              (out2 / "Mod" / "Mod.dll").read_bytes() == b"MOD" and (out2 / "Mod.closure.txt").is_file())
        check("merge never overwrites a file this run's own build emitted", (out2 / "Lib" / "Lib.dll").read_bytes() == b"LIB-BUILT-NOW")
        merged_log = (out2 / "workspace-build.log").read_text()
        check("the served entry's build log is appended under a header naming the key (binding identity + ENFORCED lines)",
              merged_log.startswith("this run's own build") and "REUSED under key" in merged_log
              and "platform AssemblyVersion 3.1.0.0" in merged_log and "ENFORCED" in merged_log)
        check("the cached log is not dropped into the workspace output", not (out2 / CACHED_LOG).exists())

        # ── never a wrong hit ──
        write("src/Lib/L.cs", "class L { int w; }"); g("commit", "-qam", "c8")
        changed = plan(repo, sel, "image=a")
        check("after a change in the closure the stored entry is a MISS",
              not restore([dict(e) for e in changed], st, trusted, Path(tmp) / "stage2")[0]["hit"])
        good = st.objs[k]
        st.put(k, good + b"tampered")
        check("a pull request REPLACING a trunk-signed archive is IGNORED (bytes no longer match the signed digest)",
              not restore([dict(e) for e in entries], st, trusted, Path(tmp) / "stage3")[0]["hit"])
        bad = Path(tmp) / "bad.tar"
        with tarfile.open(bad, "w") as t:
            f = Path(tmp) / "x.txt"; f.write_text("x")
            t.add(f, arcname="Mod.closure.txt")
        bsha = hashlib.sha256(bad.read_bytes()).hexdigest()
        st.put(k, bad.read_bytes()); st.put(f"{k}.sha256", bsha.encode())
        check("a pull request rewriting BOTH the archive and its digest is IGNORED (the signature names the old digest)",
              not restore([dict(e) for e in entries], st, trusted, Path(tmp) / "stage3b")[0]["hit"])
        sign_claims([{"entry": k, "sha256": bsha}], st, trunk_priv)
        check("a trunk-signed archive without the entry's assembly is a MISS",
              not restore([dict(e) for e in entries], st, trusted, Path(tmp) / "stage4")[0]["hit"])
        evil = Path(tmp) / "evil.tar"
        with tarfile.open(evil, "w") as t:
            t.add(f, arcname="Mod/Mod.dll"); t.add(f, arcname="Mod.closure.txt"); t.add(f, arcname="../escape.txt")
        esha = hashlib.sha256(evil.read_bytes()).hexdigest()
        st.put(k, evil.read_bytes()); st.put(f"{k}.sha256", esha.encode())
        sign_claims([{"entry": k, "sha256": esha}], st, trunk_priv)
        check("a trunk-signed archive with a path outside its own tree is a MISS",
              not restore([dict(e) for e in entries], st, trusted, Path(tmp) / "stage5")[0]["hit"])
        check("sign refuses a claim outside the cache's own prefix", sign_claims([{"entry": "named/x.tar", "sha256": "0" * 64}], st, trunk_priv) == 0)
        nokey = [{"module": "Mod", "project": "src/Mod/Mod.csproj", "key": None, "reason": "no key: x"}]
        check("an entry with no key is built and never kept",
              not restore(nokey, st, trusted, Path(tmp) / "stage6")[0]["hit"] and save(nokey, out1, st) == [])
        d = Path(tmp) / "d"; d.mkdir(); (d / "a.dll").write_bytes(b"1")
        h1 = dir_hash(d); (d / "a.dll").write_bytes(b"2")
        check("dir-hash moves with a published file's bytes", dir_hash(d) != h1 and dir_hash(Path(tmp) / "none") == "absent")
    if failures:
        print(f"✗ workspace-compile-cache self-test: {len(failures)} case(s) failed")
        return 1
    print("✓ workspace-compile-cache self-test: every case green")
    return 0


# ─────────────────────────────── CLI ───────────────────────────────

def load_json(arg: str):
    return json.loads(Path(arg[1:]).read_text() if arg.startswith("@") else arg)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--self-test", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    p = sub.add_parser("plan"); p.add_argument("--repo", default="."); p.add_argument("--modules", required=True)
    p.add_argument("--salt", required=True); p.add_argument("--out", required=True)
    p = sub.add_parser("restore"); p.add_argument("--plan", required=True); p.add_argument("--store", required=True)
    p.add_argument("--store-id", default=""); p.add_argument("--stage", required=True); p.add_argument("--misses", required=True)
    p.add_argument("--trust", default="", help=f"file of pinned trunk public keys (the content repo's {sig.PUBLIC_KEY_FILE})")
    p = sub.add_parser("merge"); p.add_argument("--plan", required=True); p.add_argument("--stage", required=True); p.add_argument("--out", required=True)
    p = sub.add_parser("save"); p.add_argument("--plan", required=True); p.add_argument("--out", required=True)
    p.add_argument("--store", required=True); p.add_argument("--store-id", default=""); p.add_argument("--claims-out", required=True)
    p = sub.add_parser("sign"); p.add_argument("--claims", required=True); p.add_argument("--store", required=True)
    p.add_argument("--store-id", default="")
    p = sub.add_parser("dir-hash"); p.add_argument("dir")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if a.cmd == "plan":
        entries = plan(Path(a.repo), load_json(a.modules), a.salt)
        Path(a.out).write_text(json.dumps(entries, indent=1))
        keyed = sum(1 for e in entries if e["key"])
        say(f"compile cache: {keyed} of {len(entries)} container entr(y|ies) keyed")
        for e in entries:
            if not e["key"]:
                say(f"compile cache: {e['module']} — {e['reason']}")
        return 0
    if a.cmd == "restore":
        trusted = sig.public_keys(Path(a.trust).read_text()) if a.trust and Path(a.trust).is_file() else []
        say(f"compile cache: {len(trusted)} trusted trunk key(s) pinned" + ("" if trusted else f" — nothing can be served (no {sig.PUBLIC_KEY_FILE})"))
        entries = restore(json.loads(Path(a.plan).read_text()), Store(a.store, a.store_id), trusted, Path(a.stage))
        Path(a.plan).write_text(json.dumps(entries, indent=1))
        misses = [e["project"] for e in entries if not e.get("hit")]
        Path(a.misses).write_text("".join(f"{m}\n" for m in misses))
        hits = len(entries) - len(misses)
        say(f"compile cache: {hits} of {len(entries)} container entr(y|ies) REUSED, {len(misses)} to compile")
        return 0
    if a.cmd == "merge":
        n = merge(json.loads(Path(a.plan).read_text()), Path(a.stage), Path(a.out))
        say(f"compile cache: {n} reused entr(y|ies) laid into {a.out}")
        return 0
    if a.cmd == "save":
        claims = save(json.loads(Path(a.plan).read_text()), Path(a.out), Store(a.store, a.store_id))
        Path(a.claims_out).write_text(json.dumps(claims))
        say(f"compile cache: {len(claims)} compiled entr(y|ies) kept, awaiting the trunk signature")
        return 0
    if a.cmd == "sign":
        key = os.environ.get(sig.SIGNING_KEY_ENV, "")
        if not key.strip():
            say(f"compile cache: no {sig.SIGNING_KEY_ENV} in this job — nothing signed, so nothing this run kept is trusted")
            return 0
        n = sign_claims(load_json(a.claims), Store(a.store, a.store_id), key)
        say(f"compile cache: {n} entr(y|ies) signed with the trunk key")
        return 0
    if a.cmd == "dir-hash":
        print(dir_hash(Path(a.dir)))
        return 0
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
