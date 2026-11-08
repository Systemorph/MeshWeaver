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
    r = subprocess.run(["git", "-C", str(repo), "status", "--porcelain", "--untracked-files=all", "--ignored=no", "--", *paths],
                       capture_output=True, text=True)
    if r.returncode != 0:
        return ["<git status failed>"]
    return [l[3:] for l in r.stdout.splitlines() if l.strip()]


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


def restore(entries: list[dict], store_url: str, store_id: str, stage: Path, fetch=None) -> list[dict]:
    """Fetch every keyed entry's archive into `stage/<module>/`; mark entry['hit']."""
    fetch = fetch or _store_fetch
    stage.mkdir(parents=True, exist_ok=True)
    for e in entries:
        e["hit"] = False
        if not e.get("key"):
            say(f"compile cache: {e['module']} — BUILD ({e['reason']})")
            continue
        tmp = stage / f".{e['module']}.tar"
        got = fetch(store_url, store_id, archive_key(e), tmp)
        if not got:
            e["reason"] = "miss: not in the store"
            say(f"compile cache: {e['module']} — BUILD (miss, key {e['key'][:12]})")
            continue
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
        e["reason"] = f"hit: key {e['key'][:12]}"
        say(f"compile cache: {e['module']} — REUSED (key {e['key'][:12]}) — not compiled")
    return entries


def _store_fetch(store_url: str, store_id: str, key: str, out: Path) -> bool:
    sha_file = out.with_suffix(".sha256")
    if store("probe", "--store", store_url, "--locator", f"{store_url}/{key}.sha256").returncode != 0:
        return False
    if store("get", "--store", store_url, "--expect-store-id", store_id, "--locator", f"{store_url}/{key}.sha256",
             "--out", str(sha_file)).returncode != 0:
        return False
    sha = sha_file.read_text().strip()
    sha_file.unlink(missing_ok=True)
    if len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha):
        return False
    # ci-artifact-store verifies the bytes against `#sha256=` itself; checked again here, cheaply.
    if store("get", "--store", store_url, "--expect-store-id", store_id, "--locator", f"{store_url}/{key}#sha256={sha}",
             "--out", str(out)).returncode != 0:
        return False
    return out.is_file() and hashlib.sha256(out.read_bytes()).hexdigest() == sha


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


def save(entries: list[dict], out: Path, store_url: str, store_id: str, put=None) -> int:
    """Keep every entry COMPILED in this run under its key (archive first, then its digest record)."""
    put = put or _store_put
    kept = 0
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
            sha = hashlib.sha256(tar_path.read_bytes()).hexdigest()
            if put(store_url, store_id, archive_key(e), tar_path, sha):
                kept += 1
                say(f"compile cache: {m} — kept under key {e['key'][:12]} ({tar_path.stat().st_size // 1024} KiB)")
            else:
                say(f"::warning title=Compile cache not kept::{m} could not be stored under {archive_key(e)} — this run is unaffected")
    return kept


def _store_put(store_url: str, store_id: str, key: str, tar_path: Path, sha: str) -> bool:
    sha_file = tar_path.with_suffix(".sha256")
    sha_file.write_text(sha + "\n")
    ok = store("put", "--store", store_url, "--expect-store-id", store_id, "--key", key, "--file", str(tar_path)).returncode == 0
    return ok and store("put", "--store", store_url, "--expect-store-id", store_id, "--key", f"{key}.sha256",
                        "--file", str(sha_file)).returncode == 0


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

        # ── save → restore → merge, against an in-memory store ──
        mem: dict[str, tuple[bytes, str]] = {}

        def put(_s, _i, key, tar_path, sha):
            mem[key] = (tar_path.read_bytes(), sha)
            return True

        def fetch(_s, _i, key, out):
            if key not in mem:
                return False
            data, sha = mem[key]
            if hashlib.sha256(data).hexdigest() != sha:
                return False
            out.write_bytes(data)
            return True

        out1 = Path(tmp) / "out1"
        (out1 / "Mod").mkdir(parents=True); (out1 / "Lib").mkdir(); (out1 / "Unrelated").mkdir()
        (out1 / "Mod" / "Mod.dll").write_bytes(b"MOD")
        (out1 / "Lib" / "Lib.dll").write_bytes(b"LIB")
        (out1 / "Unrelated" / "U.dll").write_bytes(b"U")
        (out1 / "Mod.closure.txt").write_text("Mod\nLib\nImageOnly\n")
        (out1 / "workspace-build.log").write_text("x platform AssemblyVersion 3.1.0.0\nuncarried-asset refusal — ENFORCED for bundle target RID linux-x64\n")
        check("save keeps a compiled entry", save(entries, out1, "s", "i", put=put) == 1)
        with tarfile.open(fileobj=__import__("io").BytesIO(mem[archive_key(entries[0])][0])) as t:
            names = set(t.getnames())
        check("the archive holds the entry, its closure manifest and its EMITTED siblings only",
              {"Mod/Mod.dll", "Mod.closure.txt", "Lib/Lib.dll", CACHED_LOG} <= names
              and not any(n.startswith("Unrelated") or n.startswith("ImageOnly") for n in names))
        stage = Path(tmp) / "stage"
        restored = restore([dict(e) for e in entries], "s", "i", stage, fetch=fetch)
        check("an unchanged key is served from the store", restored[0]["hit"])
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
              not restore([dict(e) for e in changed], "s", "i", Path(tmp) / "stage2", fetch=fetch)[0]["hit"])
        k = archive_key(entries[0])
        data, sha = mem[k]
        mem[k] = (data + b"tampered", sha)
        check("an archive that does not match its recorded sha256 is a MISS",
              not restore([dict(e) for e in entries], "s", "i", Path(tmp) / "stage3", fetch=fetch)[0]["hit"])
        bad = Path(tmp) / "bad.tar"
        with tarfile.open(bad, "w") as t:
            f = Path(tmp) / "x.txt"; f.write_text("x")
            t.add(f, arcname="Mod.closure.txt")
        mem[k] = (bad.read_bytes(), hashlib.sha256(bad.read_bytes()).hexdigest())
        check("an archive without the entry's assembly is a MISS",
              not restore([dict(e) for e in entries], "s", "i", Path(tmp) / "stage4", fetch=fetch)[0]["hit"])
        evil = Path(tmp) / "evil.tar"
        with tarfile.open(evil, "w") as t:
            t.add(f, arcname="Mod/Mod.dll"); t.add(f, arcname="Mod.closure.txt"); t.add(f, arcname="../escape.txt")
        mem[k] = (evil.read_bytes(), hashlib.sha256(evil.read_bytes()).hexdigest())
        check("an archive with a path outside its own tree is a MISS",
              not restore([dict(e) for e in entries], "s", "i", Path(tmp) / "stage5", fetch=fetch)[0]["hit"])
        nokey = [{"module": "Mod", "project": "src/Mod/Mod.csproj", "key": None, "reason": "no key: x"}]
        check("an entry with no key is built and never kept",
              not restore(nokey, "s", "i", Path(tmp) / "stage6", fetch=fetch)[0]["hit"]
              and save(nokey, out1, "s", "i", put=put) == 0)
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
    p = sub.add_parser("merge"); p.add_argument("--plan", required=True); p.add_argument("--stage", required=True); p.add_argument("--out", required=True)
    p = sub.add_parser("save"); p.add_argument("--plan", required=True); p.add_argument("--out", required=True)
    p.add_argument("--store", required=True); p.add_argument("--store-id", default="")
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
        entries = restore(json.loads(Path(a.plan).read_text()), a.store, a.store_id, Path(a.stage))
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
        n = save(json.loads(Path(a.plan).read_text()), Path(a.out), a.store, a.store_id)
        say(f"compile cache: {n} compiled entr(y|ies) kept")
        return 0
    if a.cmd == "dir-hash":
        print(dir_hash(Path(a.dir)))
        return 0
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
