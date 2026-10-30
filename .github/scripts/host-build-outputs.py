#!/usr/bin/env python3
"""host-build-outputs.py — a host-run `build` hands back what platform-compat and licence-gate read.

The DEPENDENT LEGS of the standing build hosts (Doc/Architecture/BuildHosts → "Core's own route").
On Actions, dotnet-test.yml's `build` job uploads two artifacts that two later jobs read:

  * `build-output-0` — shard 0's tarball, of which `platform-compat` reads exactly one bin: the plugin
    tester (`tools/MeshWeaver.PluginTester/bin`, found as `*/MeshWeaver.PluginTester/bin/*/mw-plugin-test.dll`);
  * `licence-evidence` — check-licenses.py `--stage-evidence` over the build's ONE restore, which
    `licence-gate` judges with `--evidence`;

and both judge the head merged onto the main THE BUILD compiled (`needs.build.outputs.main-sha`).
With the build on a host none of that exists in the run, so this script carries it across in two halves:

    host-build-outputs.py stage --repo . --sha "$MW_BUILD_SHA" --evidence <dir> --artifacts "$MW_BUILD_ARTIFACTS"
        ON THE HOST, at the end of the `build` stage (core-build-stages.py BUILD_COMMAND). Leaves EXACTLY
        three files under `$MW_BUILD_ARTIFACTS/build-outputs/`, which the host hands over as H9 receipts
        once the stage is green (MeshWeaver.Deploy build-run.py):
          build-output-0.tar     the plugin tester's bin, under its repository path
          licence-evidence.tar   the staged evidence (restored-packages.json + packages/**)
          tested.json            {"schema":1, "sha", "tree", "mergeBase"} — the tree the build compiled and the
                                 base the host merged the head onto (null when it merged nothing: a trunk run,
                                 or a head that already contains its base)
        REFUSES: a tester bin without mw-plugin-test.dll, a symlink or special file in either tree, evidence
        without restored-packages.json, and a checkout that is neither the run's commit nor that commit merged
        onto ONE base (the host's `merge --no-ff`: first parent the head).

    host-build-outputs.py fetch --api-url <https> --sha <head> --out <dir> [--repo R] [--channel latest]
        IN A GITHUB-HOSTED JOB (core runs nothing on the self-hosted pool). Reads the run's receipts from the
        build API with the run's own OIDC token, keeps the `build` stage's SUCCEEDED receipts under
        `build-outputs/` (build-artifacts-fetch.py's reader: a failed build's files are never judged),
        fetches each object by its content hash (host-shard-results.py's no-redirect getter, the bearer
        re-minted when stale), verifies sha256 and size against the receipt, and extracts:
          <out>/build-output-0/tools/MeshWeaver.PluginTester/bin/**   (the caller packs it as build-output-0.tar.zst)
          <out>/licence-evidence/**                                   (uploaded as-is as licence-evidence)
        and writes `main-sha` (mergeBase, or the head itself when the host merged nothing — merging a head onto
        itself leaves its own tree) and `tree` to $GITHUB_OUTPUT. The caller then holds the fresh merge onto that
        main-sha to `tree`, so the consumers judge exactly the tree the host built.
        RED, naming it, on: no receipt (a build that did not succeed, or a host whose build stage predates the
        hand-back), a missing or FOREIGN file under build-outputs/, one path with two contents, a refused fetch,
        a truncated / padded / mis-hashed body, a tested.json naming another commit or malformed, and a tar member
        that escapes its tree, is not a regular file or directory, or lies outside the shape above.

    host-build-outputs.py --self-test
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import io
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import urllib.parse
from typing import Callable, Optional

HERE = os.path.dirname(os.path.abspath(__file__))
UNIT = "build-outputs"
STAGE = "build"
TESTER_TAR = "build-output-0.tar"
EVIDENCE_TAR = "licence-evidence.tar"
TESTED = "tested.json"
FILES = (TESTER_TAR, EVIDENCE_TAR, TESTED)
TESTER_BIN = "tools/MeshWeaver.PluginTester/bin"
TESTER_DLL = "mw-plugin-test.dll"
EVIDENCE_MANIFEST = "restored-packages.json"
EVIDENCE_PACKAGES = "packages"
SCHEMA = 1
SHA = re.compile(r"^[0-9a-f]{40}$")
REPO = re.compile(r"^[A-Za-z0-9._-]+$")
CHUNK = 1 << 20


class Red(Exception):
    """A refusal with its reason."""


def _load(name: str, file: str):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def _inert(text: str) -> str:
    return (text.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            .replace("##[", "##​[").replace("::", ":​:"))


# ───────────────────────────────── stage (on the host) ─────────────────────────────────

def _tar_tree(src: str, arc_root: str, dest: str) -> int:
    """Every regular file under src into an uncompressed tar at dest, as arc_root/<rel>, sorted, with
    fixed ownership and mtime (the bytes are a function of the tree). A symlink or special file is RED."""
    entries: list[tuple[str, str]] = []
    for dirpath, dirnames, filenames in os.walk(src):
        dirnames.sort()
        for n in sorted(filenames) + sorted(d for d in dirnames if os.path.islink(os.path.join(dirpath, d))):
            full = os.path.join(dirpath, n)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            st = os.lstat(full)
            if not stat.S_ISREG(st.st_mode):
                raise Red(f"{arc_root}/{rel} is not a regular file — the hand-back carries plain files only")
            entries.append((full, f"{arc_root}/{rel}" if arc_root else rel))
    if not entries:
        raise Red(f"{src} holds no file to hand back")
    with tarfile.open(dest, "w", format=tarfile.PAX_FORMAT) as tf:
        for full, arc in entries:
            info = tf.gettarinfo(full, arcname=arc)
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            info.mtime = 0
            info.mode = stat.S_IMODE(info.mode) & 0o755 | 0o444
            with open(full, "rb") as fh:
                tf.addfile(info, fh)
    return len(entries)


def _git(repo: str, *args: str) -> str:
    r = subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True)
    if r.returncode != 0:
        raise Red(f"git {' '.join(args)} failed ({r.returncode}): {(r.stderr or r.stdout).strip()[:300]}")
    return r.stdout.strip()


def tested_tree(repo: str, sha: str) -> dict:
    """The tree the build compiled and the base the host merged onto. The host (build-run.py
    merge_onto_base) runs `git merge --no-ff <base>` in the run's checkout of <sha>, so HEAD is <sha>
    itself (nothing merged) or a merge commit whose FIRST parent is <sha> and second the base."""
    head = _git(repo, "rev-parse", "HEAD")
    tree = _git(repo, "rev-parse", "HEAD^{tree}")
    if head == sha:
        base = None
    else:
        parents = _git(repo, "rev-list", "--parents", "-n", "1", "HEAD").split()[1:]
        if len(parents) != 2 or parents[0] != sha:
            raise Red(f"the checkout HEAD {head[:12]} is neither the run's commit {sha[:12]} nor that commit merged "
                      f"onto one base (parents {[p[:12] for p in parents]}) — nothing says which tree was built")
        base = parents[1]
    return {"schema": SCHEMA, "sha": sha, "tree": tree, "mergeBase": base}


def stage(repo: str, sha: str, evidence: str, artifacts: str) -> dict:
    if not SHA.match(sha or ""):
        raise Red(f"--sha {sha!r} is not a 40-hex commit (MW_BUILD_SHA)")
    if not artifacts:
        raise Red("--artifacts is empty — the host names no artifacts directory (MW_BUILD_ARTIFACTS)")
    tester = os.path.join(repo, *TESTER_BIN.split("/"))
    if not any(TESTER_DLL in fs for _, _, fs in os.walk(tester)):
        raise Red(f"{TESTER_BIN} carries no {TESTER_DLL} — platform-compat would have no tester to run")
    if not os.path.isfile(os.path.join(evidence, EVIDENCE_MANIFEST)):
        raise Red(f"{evidence} carries no {EVIDENCE_MANIFEST} — check-licenses.py --stage-evidence staged nothing")
    record = tested_tree(repo, sha)
    unit = os.path.join(artifacts, UNIT)
    if os.path.exists(unit):
        shutil.rmtree(unit)
    os.makedirs(unit)
    n_tester = _tar_tree(tester, TESTER_BIN, os.path.join(unit, TESTER_TAR))
    n_evidence = _tar_tree(evidence, "", os.path.join(unit, EVIDENCE_TAR))
    with open(os.path.join(unit, TESTED), "w") as fh:
        json.dump(record, fh, indent=1, sort_keys=True)
    print(f"handed back under $MW_BUILD_ARTIFACTS/{UNIT}/: the plugin tester ({n_tester} files), the licence evidence "
          f"({n_evidence} files), and the tested tree {record['tree'][:12]} "
          f"({'merged onto ' + record['mergeBase'][:12] if record['mergeBase'] else 'nothing merged'})")
    return record


# ───────────────────────────────── fetch (GitHub-hosted) ─────────────────────────────────

def plan(receipts: list[dict]) -> dict[str, dict]:
    """{file name: receipt} for exactly FILES, from receipts already shape-checked and filtered to the build
    stage's build-outputs/ unit by build-artifacts-fetch.receipts_of."""
    got: dict[str, dict] = {}
    for r in receipts:
        rel = r["unit"][len(UNIT) + 1:]
        if rel not in FILES:
            raise Red(f"receipt unit {r['unit']!r} is FOREIGN to the build's hand-back (only "
                      f"{', '.join(UNIT + '/' + f for f in FILES)})")
        if rel in got and (got[rel]["sha256"], got[rel]["bytes"]) != (r["sha256"], r["bytes"]):
            raise Red(f"two receipts name {r['unit']!r} with different content")
        got[rel] = r
    missing = [f for f in FILES if f not in got]
    if missing:
        raise Red(f"the build handed back no {', '.join(UNIT + '/' + f for f in missing)} — a host whose build stage "
                  "predates the hand-back, or a build that did not succeed (its own red is the verdict)")
    return got


def fetch_files(receipts: dict[str, dict], dest: str, get: Callable[[str, str, int], None]) -> dict[str, str]:
    os.makedirs(dest, exist_ok=True)
    out: dict[str, str] = {}
    for name, r in sorted(receipts.items()):
        path = os.path.join(dest, name)
        get(r["sha256"], path, r["bytes"])
        h = hashlib.sha256()
        with open(path, "rb") as fh:
            for chunk in iter(lambda: fh.read(CHUNK), b""):
                h.update(chunk)
        size = os.path.getsize(path)
        if size != r["bytes"]:
            raise Red(f"{UNIT}/{name} landed as {size} bytes but its receipt attests {r['bytes']} — TRUNCATED or padded")
        if h.hexdigest() != r["sha256"]:
            raise Red(f"{UNIT}/{name} landed as sha256 {h.hexdigest()[:12]}… but its receipt attests {r['sha256'][:12]}…")
        out[name] = path
    return out


def read_tested(path: str, sha: str) -> dict:
    try:
        with open(path) as fh:
            t = json.load(fh)
    except (OSError, ValueError) as e:
        raise Red(f"{UNIT}/{TESTED} is not JSON ({e})")
    if not isinstance(t, dict) or t.get("schema") != SCHEMA:
        raise Red(f"{UNIT}/{TESTED} is not schema {SCHEMA}: {str(t)[:120]}")
    if t.get("sha") != sha:
        raise Red(f"{UNIT}/{TESTED} names commit {str(t.get('sha'))[:12]}, not this run's {sha[:12]} — another run's build")
    if not SHA.match(str(t.get("tree") or "")):
        raise Red(f"{UNIT}/{TESTED} names no tree")
    base = t.get("mergeBase")
    if base is not None and not SHA.match(str(base)):
        raise Red(f"{UNIT}/{TESTED} mergeBase {base!r} is neither null nor a 40-hex commit")
    return t


def _shape(rel: str, prefix: str, allowed: Callable[[str], bool], what: str) -> str:
    parts = rel.split("/")
    if (not rel or rel.startswith("/") or "\\" in rel or any(p in ("", ".", "..") for p in parts)
            or any(ord(ch) < 32 or ch == "\x7f" for ch in rel)):
        raise Red(f"{what}: member {rel!r} escapes or malforms the tree")
    if not allowed(rel):
        raise Red(f"{what}: member {rel!r} is FOREIGN to it ({prefix})")
    return rel


def extract(tar_path: str, dest: str, allowed: Callable[[str], bool], what: str, shape: str) -> list[str]:
    """Extract only regular files (and directories) whose names pass `allowed`, never following or creating a
    link; RED on any other member. Returns the regular files written."""
    if os.path.exists(dest) and os.listdir(dest):
        raise Red(f"{dest} already holds files — the extracted tree must be exactly the hand-back's")
    os.makedirs(dest, exist_ok=True)
    root = os.path.realpath(dest)
    written: list[str] = []
    try:
        with tarfile.open(tar_path, "r:") as tf:
            for m in tf:
                rel = _shape(m.name.rstrip("/") if m.isdir() else m.name, shape, (lambda r: True) if m.isdir() else allowed, what)
                target = os.path.join(root, *rel.split("/"))
                if not os.path.realpath(target).startswith(root + os.sep):
                    raise Red(f"{what}: member {rel!r} resolves outside the tree")
                if m.isdir():
                    os.makedirs(target, exist_ok=True)
                    continue
                if not m.isreg():
                    raise Red(f"{what}: member {rel!r} is not a regular file (a link or device is never extracted)")
                os.makedirs(os.path.dirname(target), exist_ok=True)
                src = tf.extractfile(m)
                with open(target, "wb") as fh:
                    shutil.copyfileobj(src, fh, CHUNK)
                os.chmod(target, stat.S_IMODE(m.mode) & 0o755 | 0o644)
                written.append(rel)
    except tarfile.TarError as e:
        raise Red(f"{what}: not a readable tar ({e})")
    if not written:
        raise Red(f"{what}: the tar carries no file")
    return written


def tester_member(rel: str) -> bool:
    return rel.startswith(TESTER_BIN + "/")


def evidence_member(rel: str) -> bool:
    return rel == EVIDENCE_MANIFEST or rel.startswith(EVIDENCE_PACKAGES + "/")


def unpack(files: dict[str, str], out: str, sha: str) -> dict:
    tested = read_tested(files[TESTED], sha)
    tester = extract(files[TESTER_TAR], os.path.join(out, "build-output-0"), tester_member,
                     f"{UNIT}/{TESTER_TAR}", f"only {TESTER_BIN}/**")
    if not any(r.endswith("/" + TESTER_DLL) for r in tester):
        raise Red(f"{UNIT}/{TESTER_TAR} carries no {TESTER_DLL} — platform-compat would have no tester to run")
    evidence = extract(files[EVIDENCE_TAR], os.path.join(out, "licence-evidence"), evidence_member,
                       f"{UNIT}/{EVIDENCE_TAR}", f"only {EVIDENCE_MANIFEST} and {EVIDENCE_PACKAGES}/**")
    if EVIDENCE_MANIFEST not in evidence:
        raise Red(f"{UNIT}/{EVIDENCE_TAR} carries no {EVIDENCE_MANIFEST} — the licence gate would judge no graph")
    # The base the consumers' fresh merge must use to arrive at the host's tree. Nothing merged: the head onto
    # itself, which leaves its own tree (ci-fresh-merge.py `merge-tree <head> <head>`).
    main_sha = tested["mergeBase"] or sha
    return {"main-sha": main_sha, "tree": tested["tree"], "tester-files": len(tester), "evidence-files": len(evidence)}


def write_outputs(path: Optional[str], kv: dict) -> None:
    if not path:
        return
    with open(path, "a") as fh:
        for k in ("main-sha", "tree"):
            fh.write(f"{k}={kv[k]}\n")


def fetch(a: argparse.Namespace) -> int:
    env = dict(os.environ)
    try:
        repo = (a.repo or env.get("GITHUB_REPOSITORY", "").split("/", 1)[-1]).strip()
        sha = (a.sha or "").strip().lower()
        if not a.api_url.startswith("https://"):
            raise Red(f"--api-url {a.api_url!r} is not https — refusing to send an OIDC token there "
                      "(empty: the admission named no build API)")
        if not REPO.match(repo) or not SHA.match(sha) or not a.out:
            raise Red("--repo (bare), --sha (40 hex) and --out are required")
        if os.path.exists(a.out) and os.listdir(a.out):
            raise Red(f"--out {a.out} already holds files")
        bf = _load("build_follow", "build-follow.py")
        rf = _load("build_artifacts_fetch", "build-artifacts-fetch.py")
        hsr = _load("host_shard_results", "host-shard-results.py")
        p = urllib.parse.urlparse(a.api_url)
        http = bf.Http()
        audience = a.audience or f"{p.scheme}://{p.netloc}"

        def mint() -> str:
            try:
                return bf.oidc_token(http, audience, env)
            except bf.Failure as f:
                raise Red(str(f))
        token = hsr.refreshing_token(mint, bf.TOKEN_REFRESH_SECONDS)
        base = f"{a.api_url.rstrip('/')}/{urllib.parse.quote(repo, safe='')}/{sha}/artifacts"
        status, body = hsr.receipts_get(f"{base}?" + urllib.parse.urlencode({"channel": a.channel}), token())
        if status != 200:
            raise Red(f"the build API answered HTTP {status} for {repo}@{sha[:12]}'s receipts (403: the token's run "
                      f"is not this run's; 404: no host-executed run; 3xx: a redirect is refused)")
        try:
            receipts = rf.receipts_of(body, STAGE, UNIT)
        except rf.Red as e:
            raise Red(str(e))
        getter = hsr.api_getter(base, token, a.channel)

        def get(s: str, d: str, n: int) -> None:
            try:
                getter(s, d, n)
            except hsr.Red as e:
                raise Red(str(e))
        with tempfile.TemporaryDirectory() as tmp:
            result = unpack(fetch_files(plan(receipts), tmp, get), a.out, sha)
        write_outputs(env.get("GITHUB_OUTPUT"), result)
    except Red as r:
        print(f"::error title=Host build outputs::{_inert(str(r))}", flush=True)
        return 1
    print(f"the host build's hand-back, each file verified against its receipt → {a.out}: plugin tester "
          f"({result['tester-files']} files), licence evidence ({result['evidence-files']} files); tested tree "
          f"{result['tree'][:12]}, consumers merge onto {result['main-sha'][:12]}")
    return 0


# ───────────────────────────────── self-test ─────────────────────────────────

def self_test() -> int:
    fails: list[str] = []

    def check(name: str, cond: bool, detail: object = "") -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}")
        if not cond:
            fails.append(f"{name}: {detail}")

    def red(fn, *args) -> bool:
        try:
            fn(*args)
            return False
        except Red:
            return True

    rf = _load("build_artifacts_fetch", "build-artifacts-fetch.py")
    tmp = tempfile.mkdtemp(prefix="host-build-outputs-")
    genv = {**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@invalid", "GIT_COMMITTER_NAME": "t",
            "GIT_COMMITTER_EMAIL": "t@invalid"}

    def g(repo: str, *args: str) -> str:
        return subprocess.run(["git", "-C", repo, *args], check=True, capture_output=True, text=True, env=genv).stdout.strip()

    # A fake checkout: a built tester bin, a staged evidence dir, a commit, and a base to merge it onto.
    repo = os.path.join(tmp, "repo")
    bin_dir = os.path.join(repo, *TESTER_BIN.split("/"), "Release", "net10.0")
    os.makedirs(bin_dir)
    open(os.path.join(bin_dir, TESTER_DLL), "wb").write(b"MZ tester")
    open(os.path.join(bin_dir, "MeshWeaver.Messaging.dll"), "wb").write(b"MZ core")
    os.makedirs(os.path.join(repo, "src"))
    open(os.path.join(repo, "src", "a.txt"), "w").write("a\n")
    g(tmp, "init", "-q", "-b", "main", repo)
    open(os.path.join(repo, ".gitignore"), "w").write("tools/\nevidence/\n")
    g(repo, "add", "-A"); g(repo, "commit", "-q", "-m", "base")
    root = g(repo, "rev-parse", "HEAD")
    g(repo, "checkout", "-q", "-b", "head")
    open(os.path.join(repo, "src", "b.txt"), "w").write("b\n")
    g(repo, "add", "-A"); g(repo, "commit", "-q", "-m", "head")
    head = g(repo, "rev-parse", "HEAD")
    g(repo, "checkout", "-q", "main")
    open(os.path.join(repo, "src", "c.txt"), "w").write("c\n")
    g(repo, "add", "-A"); g(repo, "commit", "-q", "-m", "main moved")
    base = g(repo, "rev-parse", "HEAD")
    g(repo, "checkout", "-q", "head")
    evidence = os.path.join(repo, "evidence")
    os.makedirs(os.path.join(evidence, "packages", "a", "1.0.0"))
    open(os.path.join(evidence, EVIDENCE_MANIFEST), "w").write('{"a": ["A", "1.0.0"]}')
    open(os.path.join(evidence, "packages", "a", "1.0.0", "a.nuspec"), "w").write("<package/>")

    print("stage — on the host, at the end of the build:")
    art = os.path.join(tmp, "art-trunk")
    rec = stage(repo, head, evidence, art)
    check("nothing merged (a trunk run): mergeBase null, the head's own tree",
          rec["mergeBase"] is None and rec["tree"] == g(repo, "rev-parse", "HEAD^{tree}"), rec)
    check("exactly the three files under build-outputs/", sorted(os.listdir(os.path.join(art, UNIT))) == sorted(FILES))
    with tarfile.open(os.path.join(art, UNIT, TESTER_TAR)) as tf:
        names = tf.getnames()
    check("the tester tar holds the bin under its repository path (platform-compat's find pattern)",
          f"{TESTER_BIN}/Release/net10.0/{TESTER_DLL}" in names and all(n.startswith(TESTER_BIN + "/") for n in names), names)
    first = open(os.path.join(art, UNIT, TESTER_TAR), "rb").read()
    stage(repo, head, evidence, art)
    check("the tar bytes are a function of the tree (re-staged: identical)", open(os.path.join(art, UNIT, TESTER_TAR), "rb").read() == first)
    g(repo, "merge", "-q", "--no-ff", "--no-edit", "-m", "build host: merge onto base", base)
    merged_tree = g(repo, "rev-parse", "HEAD^{tree}")
    art = os.path.join(tmp, "art-pr")
    rec = stage(repo, head, evidence, art)
    check("merged onto a base (a PR run, build-run.py merge --no-ff): mergeBase is the second parent",
          rec["mergeBase"] == base and rec["tree"] == merged_tree, rec)
    check("RED: a checkout that is neither the head nor the head merged onto one base", red(stage, repo, root, evidence, os.path.join(tmp, "x1")))
    check("RED: a malformed --sha", red(stage, repo, "abc", evidence, os.path.join(tmp, "x2")))
    check("RED: no artifacts directory named", red(stage, repo, head, evidence, ""))
    os.remove(os.path.join(evidence, EVIDENCE_MANIFEST))
    check("RED: evidence without restored-packages.json (nothing staged)", red(stage, repo, head, evidence, os.path.join(tmp, "x3")))
    open(os.path.join(evidence, EVIDENCE_MANIFEST), "w").write('{"a": ["A", "1.0.0"]}')
    os.rename(os.path.join(bin_dir, TESTER_DLL), os.path.join(bin_dir, "other.dll"))
    check("RED: a tester bin without mw-plugin-test.dll", red(stage, repo, head, evidence, os.path.join(tmp, "x4")))
    os.rename(os.path.join(bin_dir, "other.dll"), os.path.join(bin_dir, TESTER_DLL))
    os.symlink("/etc/passwd", os.path.join(bin_dir, "link.dll"))
    check("RED: a symlink in the tester bin is never handed back", red(stage, repo, head, evidence, os.path.join(tmp, "x5")))
    os.remove(os.path.join(bin_dir, "link.dll"))

    print("fetch — in the GitHub-hosted job, by receipt:")
    blobs: dict[str, bytes] = {}

    def rc(name: str, data: bytes, stage_name: str = STAGE, unit: str = UNIT) -> dict:
        h = hashlib.sha256(data).hexdigest()
        blobs[h] = data
        return {"stage": stage_name, "unit": f"{unit}/{name}", "key": "api:#sha256=" + h, "sha256": h,
                "bytes": len(data), "stageState": "succeeded"}
    good_files = {f: open(os.path.join(art, UNIT, f), "rb").read() for f in FILES}
    good = [rc(f, b) for f, b in good_files.items()] + [rc("testResults-shard0/test/test-results.log", b"[CI] x exit=0\n",
                                                          "test-shard-0", "testResults-shard0")]

    def serve(s: str, d: str, n: int) -> None:
        open(d, "wb").write(blobs[s])

    counter = [0]

    def run(rows: list[dict], sha: str = head, getter=serve) -> dict:
        counter[0] += 1
        out = os.path.join(tmp, f"out{counter[0]}")
        with tempfile.TemporaryDirectory() as t:
            r = unpack(fetch_files(plan(rf.receipts_of(rows, STAGE, UNIT)), t, getter), out, sha)
        r["out"] = out
        return r

    def red_run(rows: list[dict], sha: str = head, getter=serve) -> bool:
        try:
            run(rows, sha, getter)
            return False
        except (Red, rf.Red):
            return True
    try:
        r = run(good)
        check("a complete hand-back: main-sha = the host's merge base, tree = the built tree",
              r["main-sha"] == base and r["tree"] == merged_tree, r)
        check("the tester lands where platform-compat's find looks for it",
              os.path.isfile(os.path.join(r["out"], "build-output-0", *TESTER_BIN.split("/"), "Release", "net10.0", TESTER_DLL)))
        check("the evidence lands as check-licenses.py --evidence reads it",
              os.path.isfile(os.path.join(r["out"], "licence-evidence", EVIDENCE_MANIFEST))
              and os.path.isfile(os.path.join(r["out"], "licence-evidence", "packages", "a", "1.0.0", "a.nuspec")))
    except (Red, rf.Red) as e:
        check("a complete hand-back is accepted", False, e)
    trunk_files = {f: open(os.path.join(tmp, "art-trunk", UNIT, f), "rb").read() for f in FILES}
    try:
        r = run([rc(f, b) for f, b in trunk_files.items()])
        check("nothing merged: main-sha is the head itself (merging a head onto itself leaves its tree)",
              r["main-sha"] == head, r)
    except (Red, rf.Red) as e:
        check("nothing merged is accepted", False, e)
    check("a receipt repeated by a later stage with the same content is one file", not red_run(good + [dict(good[0])]))
    check("RED: no receipt at all", red_run([]))
    check("RED: only another stage's receipts", red_run([dict(x, stage="test-shard-0") for x in good[:3]]))
    check("RED: the build stage did NOT succeed (its files are never judged)",
          red_run([dict(x, stageState="failed") for x in good[:3]]))
    for f in FILES:
        check(f"RED: {UNIT}/{f} missing", red_run([x for x in good[:3] if not x["unit"].endswith("/" + f)]))
    check("RED: a FOREIGN file under build-outputs/", red_run(good + [rc("notes.txt", b"x")]))
    check("RED: one path, two contents", red_run(good + [rc(TESTED, b"{}")]))
    check("RED: TRUNCATED body", red_run(good, getter=lambda s, d, n: open(d, "wb").write(blobs[s][:-1]) and None))
    check("RED: padded body", red_run(good, getter=lambda s, d, n: open(d, "wb").write(blobs[s] + b"x") and None))
    check("RED: tested.json names another commit", red_run(good, sha=root))

    def with_tested(t: object) -> list[dict]:
        return [x for x in good[:3] if not x["unit"].endswith(TESTED)] + [rc(TESTED, json.dumps(t).encode())]
    check("RED: tested.json with a malformed mergeBase", red_run(with_tested({"schema": 1, "sha": head, "tree": merged_tree, "mergeBase": "main"})))
    check("RED: tested.json of another schema", red_run(with_tested({"schema": 2, "sha": head, "tree": merged_tree, "mergeBase": None})))
    check("RED: tested.json naming no tree", red_run(with_tested({"schema": 1, "sha": head, "mergeBase": None})))

    def tar_of(members: list[tuple[str, Optional[bytes], str]]) -> bytes:
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w") as tf:
            for name, data, kind in members:
                info = tarfile.TarInfo(name)
                if kind == "sym":
                    info.type, info.linkname = tarfile.SYMTYPE, "/etc/passwd"
                    tf.addfile(info)
                else:
                    info.size = len(data or b"")
                    tf.addfile(info, io.BytesIO(data or b""))
        return buf.getvalue()

    def with_tester(members) -> list[dict]:
        return [x for x in good[:3] if not x["unit"].endswith(TESTER_TAR)] + [rc(TESTER_TAR, tar_of(members))]
    dll = f"{TESTER_BIN}/Release/net10.0/{TESTER_DLL}"
    check("negative control: a hand-made tester tar of the right shape is accepted", not red_run(with_tester([(dll, b"MZ", "f")])))
    check("RED: a tester tar member escaping the tree", red_run(with_tester([(dll, b"MZ", "f"), (f"{TESTER_BIN}/../../x", b"x", "f")])))
    check("RED: an absolute tester tar member", red_run(with_tester([(dll, b"MZ", "f"), ("/etc/x", b"x", "f")])))
    check("RED: a FOREIGN tester tar member (outside the tester bin)", red_run(with_tester([(dll, b"MZ", "f"), ("test/A/bin/A.dll", b"x", "f")])))
    check("RED: a symlink in the tester tar", red_run(with_tester([(dll, b"MZ", "f"), (f"{TESTER_BIN}/l.dll", None, "sym")])))
    check("RED: a tester tar without mw-plugin-test.dll", red_run(with_tester([(f"{TESTER_BIN}/Release/x.dll", b"MZ", "f")])))
    check("RED: a tester 'tar' that is not one", red_run([x for x in good[:3] if not x["unit"].endswith(TESTER_TAR)] + [rc(TESTER_TAR, b"not a tar")]))

    def with_evidence(members) -> list[dict]:
        return [x for x in good[:3] if not x["unit"].endswith(EVIDENCE_TAR)] + [rc(EVIDENCE_TAR, tar_of(members))]
    check("RED: evidence without restored-packages.json", red_run(with_evidence([("packages/a/1.0.0/a.nuspec", b"<p/>", "f")])))
    check("RED: a FOREIGN evidence member", red_run(with_evidence([(EVIDENCE_MANIFEST, b"{}", "f"), ("bin/evil.sh", b"x", "f")])))

    print("check-licenses.py stages evidence from the host's FALLBACK folder too:")
    cl = _load("check_licenses", "check-licenses.py")
    writable, fallback = os.path.join(tmp, "nuget-w"), os.path.join(tmp, "nuget-fb")
    os.makedirs(os.path.join(fallback, "pkg", "2.0.0"))
    open(os.path.join(fallback, "pkg", "2.0.0", "pkg.nuspec"), "w").write(
        '<package><metadata><license type="expression">MIT</license></metadata></package>')
    saved = {k: os.environ.get(k) for k in ("NUGET_PACKAGES", "NUGET_FALLBACK_PACKAGES")}
    try:
        os.environ["NUGET_PACKAGES"] = writable
        os.environ["NUGET_FALLBACK_PACKAGES"] = fallback
        check("a package held only by the fallback folder is found there",
              cl.package_dir("Pkg", "2.0.0") == os.path.join(fallback, "pkg", "2.0.0"))
        os.environ["NUGET_FALLBACK_PACKAGES"] = ""
        check("negative control: without the fallback folder it is not found (the defect this pays)",
              cl.package_dir("Pkg", "2.0.0", [writable]) is None)
    finally:
        for k, v in saved.items():
            if v is None:
                os.environ.pop(k, None)
            else:
                os.environ[k] = v

    def quiet(args: list[str]) -> int:
        import contextlib
        with contextlib.redirect_stdout(io.StringIO()):
            return main(args)
    check("usage: a non-https api-url is RED (no OIDC token sent in clear)",
          quiet(["fetch", "--api-url", "http://x", "--sha", "a" * 40, "--repo", "R", "--out", os.path.join(tmp, "u1")]) == 1)
    check("usage: an empty api-url is RED", quiet(["fetch", "--api-url", "", "--sha", "a" * 40, "--repo", "R", "--out", os.path.join(tmp, "u2")]) == 1)
    shutil.rmtree(tmp, ignore_errors=True)
    if fails:
        for f in fails:
            print(f"SELF-TEST FAILED: {f}")
        return 1
    print("host-build-outputs self-test: every case behaved (stages exactly the three files, records the merge base or "
          "none, fetches by receipt and extracts only the declared shapes; refuses every malformed, foreign, failed, "
          "truncated, padded, escaping or linked case)")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("stage")
    s.add_argument("--repo", default=".")
    s.add_argument("--sha", required=True)
    s.add_argument("--evidence", required=True)
    s.add_argument("--artifacts", required=True)
    f = sub.add_parser("fetch")
    f.add_argument("--api-url", default="")
    f.add_argument("--repo", default="")
    f.add_argument("--sha", default="")
    f.add_argument("--channel", default="latest")
    f.add_argument("--audience", default="")
    f.add_argument("--out", default="")
    a = ap.parse_args(argv)
    if a.cmd == "stage":
        try:
            stage(os.path.abspath(a.repo), a.sha.strip().lower(), a.evidence, a.artifacts)
        except Red as r:
            print(f"::error title=Host build outputs::{_inert(str(r))}", flush=True)
            return 1
        return 0
    return fetch(a)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
