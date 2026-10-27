#!/usr/bin/env python3
"""gate-bake-handoff.py — a sharded node-repo gate compiles its bake ONCE; every shard adopts its
own verified copy, or refreshes it loudly, and never silently uses a bake that is not its own.

(The name on this first line is load-bearing: node-repo-gate.yml fetches this file at the lane's
scripts ref and refuses a body whose first 400 bytes do not name it.)

    gate-bake-handoff.py key   --out KEY.json [--value NAME=STR]... [--tree NAME=DIR]... [--listing NAME=DIR]...
    gate-bake-handoff.py seal  --bake DIR --key-doc KEY.json --out HANDOFF_DIR
    gate-bake-handoff.py adopt --handoff HANDOFF_DIR --key-doc KEY.json --identity ID --into DIR
    gate-bake-handoff.py --self-test

WHY (measured on MeshWeaver.Plugins main, REST job logs, 2026-10-25..27)
-------------------------------------------------------------------------
Every gate shard ran the SAME `mw-plugin-test compile` over the SAME mount, the same source sha and
the same framework identity, and then gated a quarter of it. The compile was 75-93 % of every
shard's job:

    run 40422667038 (10-26)  4 shards × 538-548 s compile, 38-184 s gating after it
    run 40527810899 (10-27)  4 shards × 1647-1694 s compile, 17-323 s gating after it
    run 40531711572 (10-27)  4 shards × 1694-1954 s compile (25 packages, 138 assemblies each)

The mesh each shard then stands up is installed from that bake (13-50 s of package installs), so
the bake IS the prepared state a gate needs, and it was rebuilt from nothing four times per run.
With `bake` in the lane it is compiled once and handed to every shard
(Doc/Architecture/NodeRepoGateSharding → "The prepared bake").

THE RULES THIS FILE ENFORCES
----------------------------
* A KEY OVER EVERYTHING THE COMPILE READ. `key` hashes every file of every named tree (the composed
  gate host — the portal's /app and the tester —, the mounted repository tree, the external module
  directory), the names of every listed directory (the shared frameworks), and every named value
  (the framework identity, the platform reference, the source sha, the compile arguments). Each
  tree's per-file digests stay in the key document so a mismatch can NAME the paths that differ.
  `.git` and `__pycache__` are not inputs of a compile and differ between two checkouts of one
  commit, so they are skipped by name and nothing else is — except that a `.git` BELOW a tree's
  root is keyed by its presence, because the sweep skips the directory that holds one.
* A STALE BAKE IS REFRESHED, LOUDLY. `adopt` exit 3 means "this bake was compiled from other inputs
  than this shard's" and prints which ones; the lane then compiles the shard's own bake with a
  ::warning::. A stale bake is never used.
* A DAMAGED BAKE IS REFUSED. A hand-off whose files do not match its own manifest (a missing file,
  an extra file, a byte changed, a link), whose identity file disagrees with the key it claims, or
  that has no manifest at all is exit 1 — RED, never "compile instead", because a hand-off that
  changed in transit is a fault to surface, not an absence to paper over.
* EVERY SHARD GETS ITS OWN COPY. `adopt` copies into an EMPTY directory of the shard's own and
  re-verifies the copy, so nothing a shard does to its bake can reach the hand-off or a sibling.

Exit codes: 0 done · 3 stale (refresh) · 1 refused (red) · 2 usage.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

RECIPE = "gate-bake-handoff/1"
MANIFEST = "prepared.json"
BAKE_DIR = "bake"
IDENTITY_FILE = "framework-mvid.txt"
SKIPPED_NAMES = frozenset({".git", "__pycache__"})
EXIT_STALE = 3
EXIT_REFUSED = 1


class Refused(Exception):
    """The hand-off cannot be trusted: RED."""


class Stale(Exception):
    """The hand-off was compiled from other inputs: refresh, loudly."""


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def tree_digests(root: Path) -> dict[str, str]:
    """Every file under `root`, by relative path: its content digest. A symbolic link is FOLLOWED,
    because the compile follows it (LocalNodeRepo.Sweep reads a file link's target and traverses a
    directory link): a file link records `link:<target>:<digest of the target's bytes>`, a directory
    link records `link:<target>/` and its resolved contents under the link's own path, and a link
    that leads back into a directory already on the current path records `link:<target> (cycle)`
    instead of being walked again. A link whose target is outside every hashed tree is therefore
    keyed by what it RESOLVES to on this runner — the name alone would let a shard adopt a bake
    compiled from other bytes. A dangling link records `link:<target> (missing)`. A `.git` entry
    BELOW the root records only that it is there (`nested-git-marker`) — the sweep skips the
    directory that holds one. Skips SKIPPED_NAMES only."""
    if not root.is_dir():
        raise Refused(f"'{root}' is not a directory — a key over a missing tree would key less than was compiled")
    out: dict[str, str] = {}

    def walk(directory: Path, prefix: str, ancestors: frozenset[str]) -> None:
        ancestors = ancestors | {os.path.realpath(directory)}
        for name in sorted(os.listdir(directory)):
            if name == ".git" and prefix:
                # Below the root a `.git` marker (file or directory) makes LocalNodeRepo.Sweep
                # skip the WHOLE directory (IsNestedGitTree), so its PRESENCE changes what is
                # compiled. Key the presence, never the volatile Git metadata behind it.
                out[prefix + ".git"] = "nested-git-marker"
                continue
            if name in SKIPPED_NAMES:
                continue
            p = directory / name
            rel = prefix + name
            if p.is_symlink():
                target = os.readlink(p)
                if not p.exists():
                    out[rel] = f"link:{target} (missing)"
                elif p.is_dir():
                    if os.path.realpath(p) in ancestors:
                        out[rel] = f"link:{target} (cycle)"
                    else:
                        out[rel] = f"link:{target}/"
                        walk(p, rel + "/", ancestors)
                else:
                    out[rel] = f"link:{target}:" + sha256_file(p)
            elif p.is_dir():
                walk(p, rel + "/", ancestors)
            else:
                out[rel] = sha256_file(p)

    walk(root, "", frozenset())
    return out


def digest_of(obj) -> str:
    return hashlib.sha256(json.dumps(obj, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def pairs(items: list[str], what: str) -> dict[str, str]:
    out: dict[str, str] = {}
    for item in items or []:
        name, sep, value = item.partition("=")
        if not sep or not name:
            raise SystemExit(f"--{what} wants NAME=VALUE, got {item!r}")
        if name in out:
            raise SystemExit(f"--{what} {name} given twice")
        out[name] = value
    return out


def make_key(values: dict[str, str], trees: dict[str, Path], listings: dict[str, Path]) -> dict:
    doc = {"recipe": RECIPE, "values": dict(sorted(values.items())), "trees": {}, "listings": {}}
    for name, root in sorted(trees.items()):
        files = tree_digests(root)
        doc["trees"][name] = {"digest": digest_of(files), "count": len(files), "files": files}
    for name, root in sorted(listings.items()):
        if not root.is_dir():
            raise Refused(f"listing '{name}': '{root}' is not a directory")
        names = sorted(p.relative_to(root).as_posix() for p in root.glob("*/*"))
        doc["listings"][name] = names
    doc["key"] = digest_of({"recipe": RECIPE, "values": doc["values"],
                            "trees": {k: v["digest"] for k, v in doc["trees"].items()},
                            "listings": doc["listings"]})
    return doc


def read_json(path: Path, what: str) -> dict:
    try:
        doc = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise Refused(f"{what} '{path}' does not exist")
    except (OSError, ValueError) as exc:
        raise Refused(f"{what} '{path}' is not readable JSON ({exc})")
    if not isinstance(doc, dict):
        raise Refused(f"{what} '{path}' is not a JSON object")
    return doc


def check_key_doc(doc: dict, what: str) -> None:
    if doc.get("recipe") != RECIPE or not isinstance(doc.get("key"), str) or len(doc["key"]) != 64:
        raise Refused(f"{what} is not a {RECIPE} key document")
    recomputed = digest_of({"recipe": RECIPE, "values": doc.get("values", {}),
                            "trees": {k: v.get("digest") for k, v in (doc.get("trees") or {}).items()},
                            "listings": doc.get("listings", {})})
    if recomputed != doc["key"]:
        raise Refused(f"{what} names key {doc['key'][:16]}… but its own inputs hash to {recomputed[:16]}… — edited after it was written")
    for name, tree in (doc.get("trees") or {}).items():
        if digest_of(tree.get("files", {})) != tree.get("digest"):
            raise Refused(f"{what}: tree '{name}' lists files that do not hash to its digest")


def explain(mine: dict, theirs: dict) -> list[str]:
    """What differs between two key documents, named — the content of a STALE verdict."""
    lines: list[str] = []
    mv, tv = mine.get("values", {}), theirs.get("values", {})
    for name in sorted(set(mv) | set(tv)):
        if mv.get(name) != tv.get(name):
            lines.append(f"value '{name}': this shard {mv.get(name)!r}, the bake {tv.get(name)!r}")
    mt, tt = mine.get("trees", {}), theirs.get("trees", {})
    for name in sorted(set(mt) | set(tt)):
        a, b = (mt.get(name) or {}).get("files", {}), (tt.get(name) or {}).get("files", {})
        if (mt.get(name) or {}).get("digest") == (tt.get(name) or {}).get("digest"):
            continue
        diff = sorted(p for p in set(a) | set(b) if a.get(p) != b.get(p))
        shown = ", ".join(diff[:5]) + (f" (+{len(diff) - 5} more)" if len(diff) > 5 else "")
        lines.append(f"tree '{name}': {len(diff)} file(s) differ — {shown or 'tree absent on one side'}")
    ml, tl = mine.get("listings", {}), theirs.get("listings", {})
    for name in sorted(set(ml) | set(tl)):
        if ml.get(name) != tl.get(name):
            only_mine = sorted(set(ml.get(name) or []) - set(tl.get(name) or []))
            only_bake = sorted(set(tl.get(name) or []) - set(ml.get(name) or []))
            lines.append(f"listing '{name}': only here {only_mine[:5]}, only in the bake {only_bake[:5]}")
    return lines or ["the keys differ but no named input does — the recipes disagree"]


def bake_files(bake: Path) -> dict[str, dict]:
    files: dict[str, dict] = {}
    for dirpath, dirnames, filenames in os.walk(bake, followlinks=False):
        base = Path(dirpath)
        for name in dirnames + filenames:
            p = base / name
            if p.is_symlink():
                raise Refused(f"the bake carries a symbolic link '{p.relative_to(bake).as_posix()}' — a bake is regular files only")
        for name in filenames:
            p = base / name
            if not stat.S_ISREG(p.lstat().st_mode):
                raise Refused(f"'{p.relative_to(bake).as_posix()}' in the bake is not a regular file")
            files[p.relative_to(bake).as_posix()] = {"sha256": sha256_file(p), "size": p.stat().st_size}
    return files


def seal(bake: Path, key_doc_path: Path, out: Path) -> dict:
    key_doc = read_json(key_doc_path, "key document")
    check_key_doc(key_doc, "the key document")
    identity = key_doc.get("values", {}).get("identity", "")
    if not identity:
        raise Refused("the key document carries no 'identity' value — a bake must be keyed to its framework identity")
    ident_file = bake / IDENTITY_FILE
    if not ident_file.is_file() or ident_file.read_text(encoding="utf-8").strip() != identity:
        raise Refused(f"the bake's {IDENTITY_FILE} does not name identity '{identity}' — refusing to hand over a bake keyed to another platform")
    if out.exists() and any(out.iterdir()):
        raise Refused(f"'{out}' is not empty — a hand-off is written once, into a directory of its own")
    files = bake_files(bake)
    out.mkdir(parents=True, exist_ok=True)
    shutil.copytree(bake, out / BAKE_DIR, symlinks=True)
    copied = bake_files(out / BAKE_DIR)
    if copied != files:
        raise Refused("the hand-off copy does not match the bake it was copied from")
    manifest = {"recipe": RECIPE, "key": key_doc["key"], "identity": identity,
                "keyDocument": key_doc, "files": files}
    (out / MANIFEST).write_text(json.dumps(manifest, sort_keys=True, indent=1), encoding="utf-8")
    return manifest


def adopt(handoff: Path, key_doc_path: Path, identity: str, into: Path) -> dict:
    manifest = read_json(handoff / MANIFEST, "the hand-off manifest")
    if manifest.get("recipe") != RECIPE:
        raise Refused(f"the hand-off manifest is not a {RECIPE} manifest (recipe {manifest.get('recipe')!r})")
    theirs = manifest.get("keyDocument")
    if not isinstance(theirs, dict):
        raise Refused("the hand-off manifest carries no key document")
    check_key_doc(theirs, "the hand-off's key document")
    if theirs["key"] != manifest.get("key"):
        raise Refused("the hand-off manifest's key and its own key document disagree")
    files = manifest.get("files")
    if not isinstance(files, dict) or not files:
        raise Refused("the hand-off manifest lists no files")
    present = bake_files(handoff / BAKE_DIR) if (handoff / BAKE_DIR).is_dir() else None
    if present is None:
        raise Refused(f"the hand-off has no '{BAKE_DIR}/' directory")
    if present != files:
        missing = sorted(set(files) - set(present))
        extra = sorted(set(present) - set(files))
        changed = sorted(p for p in set(files) & set(present) if files[p] != present[p])
        raise Refused(f"the hand-off's files do not match its manifest — missing {missing[:5]}, extra {extra[:5]}, changed {changed[:5]}")
    if manifest.get("identity") != theirs.get("values", {}).get("identity") \
            or (handoff / BAKE_DIR / IDENTITY_FILE).read_text(encoding="utf-8").strip() != manifest.get("identity"):
        raise Refused("the hand-off's identity file, manifest and key document do not name one identity")
    mine = read_json(key_doc_path, "this shard's key document")
    check_key_doc(mine, "this shard's key document")
    if mine.get("values", {}).get("identity") != identity:
        raise Refused(f"this shard's key document names identity {mine.get('values', {}).get('identity')!r}, but the shard resolved {identity!r}")
    if mine["key"] != theirs["key"]:
        raise Stale("; ".join(explain(mine, theirs)))
    if into.exists() and any(into.iterdir()):
        raise Refused(f"'{into}' is not empty — a shard adopts into a directory of its own")
    into.mkdir(parents=True, exist_ok=True)
    for rel in sorted(files):
        dst = into / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(handoff / BAKE_DIR / rel, dst)
    if bake_files(into) != files:
        raise Refused("the shard's own copy does not match the hand-off it was copied from")
    return manifest


# ───────────────────────────────────────── self-test ─────────────────────────────────────────

def self_test() -> int:
    failures: list[str] = []

    def check(name: str, cond: bool, detail: str = "") -> None:
        print(("ok   " if cond else "FAIL ") + name + ("" if cond else f" — {detail}"))
        if not cond:
            failures.append(name)

    def run(*args: str) -> tuple[int, str]:
        r = subprocess.run([sys.executable, str(Path(__file__).resolve()), *args], capture_output=True, text=True)
        return r.returncode, (r.stdout or "") + (r.stderr or "")

    with tempfile.TemporaryDirectory() as t:
        t = Path(t)
        host, repo, mods, shared = t / "host", t / "repo", t / "mods", t / "shared"
        for d in (host, repo / "Store", repo / ".git", repo / "Store" / "__pycache__", mods, shared / "Microsoft.NETCore.App" / "10.0.1"):
            d.mkdir(parents=True)
        (host / "mw-plugin-test.dll").write_bytes(b"tester")
        (repo / "Store" / "index.json").write_text("{}")
        (repo / ".git" / "HEAD").write_text("ref: one")
        (repo / "Store" / "__pycache__" / "x.pyc").write_bytes(b"a")
        (mods / "MeshWeaver.AI.dll").write_bytes(b"ai")

        def key(out: Path, identity: str = "c003e001") -> list[str]:
            return ["key", "--out", str(out), "--value", f"identity={identity}", "--value", "source-sha=abc",
                    "--tree", f"host={host}", "--tree", f"repo={repo}", "--tree", f"modules={mods}",
                    "--listing", f"shared={shared}"]

        # LINKS are keyed by what they RESOLVE to: a link out of every hashed tree whose target
        # bytes differ between two runners must change the key, file link and directory link alike.
        outside = t / "outside"
        (outside / "dir").mkdir(parents=True)
        (outside / "file.cs").write_text("class A {}")
        (outside / "dir" / "inner.cs").write_text("class B {}")
        linked = t / "linked"
        linked.mkdir()
        os.symlink(outside / "file.cs", linked / "file.cs")
        os.symlink(outside / "dir", linked / "dir")
        os.symlink(".", linked / "dir-up")  # a cycle: back to a directory already on the walk
        before = tree_digests(linked)
        check("links: a directory link's contents are keyed under the link's path",
              "dir/inner.cs" in before, json.dumps(before))
        check("links: a link back up the path is recorded as a cycle, not walked",
              before.get("dir-up", "").endswith("(cycle)"), json.dumps(before))
        (outside / "file.cs").write_text("class A { int x; }")
        after_file = tree_digests(linked)
        check("links: another FILE-link target's bytes change the key",
              digest_of(after_file) != digest_of(before) and after_file["file.cs"] != before["file.cs"], json.dumps(after_file))
        (outside / "dir" / "inner.cs").write_text("class B { int y; }")
        after_dir = tree_digests(linked)
        check("links: another DIRECTORY-link target's bytes change the key",
              after_dir["dir/inner.cs"] != after_file["dir/inner.cs"], json.dumps(after_dir))
        check("links: an unchanged tree keys the same twice", tree_digests(linked) == after_dir)
        shutil.rmtree(linked)

        # A NESTED `.git` marker makes the sweep skip its whole directory, so its presence is a
        # compile input; the metadata behind it is not. Negative control: the root's own `.git`.
        nested = t / "nested"
        (nested / "Pkg").mkdir(parents=True)
        (nested / "Pkg" / "a.cs").write_text("class A {}")
        (nested / ".git").write_text("gitdir: one")
        no_marker = tree_digests(nested)
        (nested / "Pkg" / ".git").mkdir()
        (nested / "Pkg" / ".git" / "HEAD").write_text("ref: one")
        with_marker = tree_digests(nested)
        check("nested .git: gaining a marker below the root changes the key",
              digest_of(with_marker) != digest_of(no_marker) and with_marker.get("Pkg/.git") == "nested-git-marker",
              json.dumps(with_marker))
        (nested / "Pkg" / ".git" / "HEAD").write_text("ref: two")
        (nested / ".git").write_text("gitdir: two")
        check("nested .git: its metadata, and the root's own .git, do not change the key",
              tree_digests(nested) == with_marker, json.dumps(tree_digests(nested)))
        shutil.rmtree(nested)

        rc, out = run(*key(t / "bake.key.json"))
        check("key: writes a key document", rc == 0 and (t / "bake.key.json").is_file(), out)
        bake = t / "own-bake"
        bake.mkdir()
        (bake / IDENTITY_FILE).write_text("c003e001\n")
        (bake / "Store.zip").write_bytes(b"store-bundle")
        (bake / "sub").mkdir()
        (bake / "sub" / "edges.json").write_text("[]")
        rc, out = run("seal", "--bake", str(bake), "--key-doc", str(t / "bake.key.json"), "--out", str(t / "handoff"))
        check("seal: writes the hand-off", rc == 0 and (t / "handoff" / MANIFEST).is_file(), out)

        # A second checkout of the same commit: .git and __pycache__ differ, nothing else does.
        (repo / ".git" / "HEAD").write_text("ref: two")
        (repo / "Store" / "__pycache__" / "x.pyc").write_bytes(b"b")
        rc, out = run(*key(t / "shard.key.json"))
        rc, out = run("adopt", "--handoff", str(t / "handoff"), "--key-doc", str(t / "shard.key.json"),
                      "--identity", "c003e001", "--into", str(t / "shard1"))
        check("adopt: equal inputs adopt (.git and __pycache__ are not inputs)", rc == 0, out)
        check("adopt: the shard's copy is byte-equal",
              (t / "shard1" / "Store.zip").read_bytes() == b"store-bundle" and (t / "shard1" / "sub" / "edges.json").is_file(), out)

        # ISOLATION: what a shard does to its copy reaches neither the hand-off nor a sibling.
        (t / "shard1" / "Store.zip").write_bytes(b"mutated by shard 1")
        rc, out = run("adopt", "--handoff", str(t / "handoff"), "--key-doc", str(t / "shard.key.json"),
                      "--identity", "c003e001", "--into", str(t / "shard2"))
        check("isolation: a sibling adopts the original bytes after shard 1 changed its copy",
              rc == 0 and (t / "shard2" / "Store.zip").read_bytes() == b"store-bundle", out)
        check("isolation: the hand-off itself is unchanged",
              (t / "handoff" / BAKE_DIR / "Store.zip").read_bytes() == b"store-bundle")
        rc, out = run("adopt", "--handoff", str(t / "handoff"), "--key-doc", str(t / "shard.key.json"),
                      "--identity", "c003e001", "--into", str(t / "shard2"))
        check("isolation: adopting into a non-empty directory is refused", rc == EXIT_REFUSED, out)

        # NEGATIVE CONTROLS — STALE: each named input class, changed alone, is a refresh, named.
        rc, out = run(*key(t / "other-identity.key.json", identity="beef0001"))
        rc, out = run("adopt", "--handoff", str(t / "handoff"), "--key-doc", str(t / "other-identity.key.json"),
                      "--identity", "beef0001", "--into", str(t / "s-ident"))
        check("stale: another framework identity is a refresh, not an adoption",
              rc == EXIT_STALE and "value 'identity'" in out and not (t / "s-ident").exists(), out)
        (repo / "Store" / "Type.cs").write_text("class T {}")
        rc, out = run(*key(t / "edited.key.json"))
        rc, out = run("adopt", "--handoff", str(t / "handoff"), "--key-doc", str(t / "edited.key.json"),
                      "--identity", "c003e001", "--into", str(t / "s-tree"))
        check("stale: a changed source file is a refresh naming the file",
              rc == EXIT_STALE and "Store/Type.cs" in out, out)
        (repo / "Store" / "Type.cs").unlink()
        (mods / "MeshWeaver.AI.dll").write_bytes(b"ai-v2")
        rc, out = run(*key(t / "module.key.json"))
        rc, out = run("adopt", "--handoff", str(t / "handoff"), "--key-doc", str(t / "module.key.json"),
                      "--identity", "c003e001", "--into", str(t / "s-mod"))
        check("stale: a changed module assembly is a refresh", rc == EXIT_STALE and "tree 'modules'" in out, out)
        (mods / "MeshWeaver.AI.dll").write_bytes(b"ai")
        (shared / "Microsoft.AspNetCore.App" / "10.0.1").mkdir(parents=True)
        rc, out = run(*key(t / "shared.key.json"))
        rc, out = run("adopt", "--handoff", str(t / "handoff"), "--key-doc", str(t / "shared.key.json"),
                      "--identity", "c003e001", "--into", str(t / "s-shared"))
        check("stale: another shared-framework set is a refresh", rc == EXIT_STALE and "listing 'shared'" in out, out)
        shutil.rmtree(shared / "Microsoft.AspNetCore.App")

        # NEGATIVE CONTROLS — REFUSED: a hand-off that changed is RED, never "compile instead".
        def damaged(name: str, mutate) -> None:
            copy = t / f"damaged-{name}"
            shutil.copytree(t / "handoff", copy)
            mutate(copy)
            rc_, out_ = run("adopt", "--handoff", str(copy), "--key-doc", str(t / "shard.key.json"),
                            "--identity", "c003e001", "--into", str(t / f"r-{name}"))
            check(f"refused: {name}", rc_ == EXIT_REFUSED and not (t / f"r-{name}").exists(), out_)

        damaged("a byte changed in a bundle", lambda c: (c / BAKE_DIR / "Store.zip").write_bytes(b"store-bundlX"))
        damaged("a bundle missing", lambda c: (c / BAKE_DIR / "Store.zip").unlink())
        damaged("an extra bundle", lambda c: (c / BAKE_DIR / "Evil.zip").write_bytes(b"x"))
        damaged("no manifest", lambda c: (c / MANIFEST).unlink())

        def edit_manifest(c: Path, f) -> None:
            m = json.loads((c / MANIFEST).read_text())
            f(m)
            (c / MANIFEST).write_text(json.dumps(m))

        # The forgery that matters: a hand-off re-labelled with THIS shard's key, manifest and key
        # document alike, over inputs that do not hash to it. Only the recomputation catches it.
        shard_key = json.loads((t / "edited.key.json").read_text())["key"]
        damaged("a hand-off re-labelled with another shard's key",
                lambda c: edit_manifest(c, lambda m: (m.__setitem__("key", shard_key),
                                                      m["keyDocument"].__setitem__("key", shard_key))))
        damaged("the identity file rewritten with the manifest re-hashed", lambda c: (
            (c / BAKE_DIR / IDENTITY_FILE).write_text("beef0001\n"),
            edit_manifest(c, lambda m: m["files"].__setitem__(IDENTITY_FILE, {
                "sha256": hashlib.sha256(b"beef0001\n").hexdigest(), "size": 9}))))

        def symlink(c: Path) -> None:
            os.symlink("/etc/passwd", c / BAKE_DIR / "link.zip")
        damaged("a symbolic link in the bake", symlink)

        rc, out = run("adopt", "--handoff", str(t / "handoff"), "--key-doc", str(t / "shard.key.json"),
                      "--identity", "beef0001", "--into", str(t / "r-resolved"))
        check("refused: the shard resolved another identity than its own key document names", rc == EXIT_REFUSED, out)
        rc, out = run("seal", "--bake", str(bake), "--key-doc", str(t / "other-identity.key.json"), "--out", str(t / "h2"))
        check("seal refuses a bake whose identity file is not the key's identity", rc == EXIT_REFUSED, out)
        rc, out = run("key", "--out", str(t / "x.json"), "--tree", f"repo={t / 'absent'}")
        check("key refuses a tree that is not there (a key over less than was compiled)", rc == EXIT_REFUSED, out)

    if failures:
        print(f"gate-bake-handoff self-test: {len(failures)} FAILED: {', '.join(failures)}")
        return 1
    print("gate-bake-handoff self-test: passed (equal inputs adopt an isolated copy; identity, source, module and "
          "framework changes each refresh by name; a changed, missing, extra, linked or re-keyed hand-off is refused)")
    return 0


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    ap.add_argument("--self-test", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    k = sub.add_parser("key")
    k.add_argument("--out", required=True)
    k.add_argument("--value", action="append", default=[])
    k.add_argument("--tree", action="append", default=[])
    k.add_argument("--listing", action="append", default=[])
    s = sub.add_parser("seal")
    s.add_argument("--bake", required=True)
    s.add_argument("--key-doc", required=True)
    s.add_argument("--out", required=True)
    a = sub.add_parser("adopt")
    a.add_argument("--handoff", required=True)
    a.add_argument("--key-doc", required=True)
    a.add_argument("--identity", required=True)
    a.add_argument("--into", required=True)
    args = ap.parse_args(argv)
    if args.self_test:
        return self_test()
    try:
        if args.cmd == "key":
            doc = make_key(pairs(args.value, "value"),
                           {n: Path(p) for n, p in pairs(args.tree, "tree").items()},
                           {n: Path(p) for n, p in pairs(args.listing, "listing").items()})
            Path(args.out).write_text(json.dumps(doc, sort_keys=True), encoding="utf-8")
            trees = ", ".join(f"{n} {v['count']} file(s)" for n, v in doc["trees"].items())
            print(f"gate bake key {doc['key'][:16]}… over {len(doc['values'])} value(s), {trees or 'no trees'}")
        elif args.cmd == "seal":
            m = seal(Path(args.bake), Path(args.key_doc), Path(args.out))
            print(f"prepared bake sealed: key {m['key'][:16]}…, identity {m['identity']}, {len(m['files'])} file(s) → {args.out}")
        elif args.cmd == "adopt":
            m = adopt(Path(args.handoff), Path(args.key_doc), args.identity, Path(args.into))
            print(f"adopted the run's prepared bake: key {m['key'][:16]}…, identity {m['identity']}, {len(m['files'])} file(s) "
                  f"verified and copied into this shard's own '{args.into}' — no compile")
        else:
            ap.print_usage()
            return 2
    except Stale as exc:
        print(f"STALE: the prepared bake was compiled from other inputs than this shard's — {exc}")
        return EXIT_STALE
    except Refused as exc:
        print(f"REFUSED: {exc}", file=sys.stderr)
        return EXIT_REFUSED
    return 0


if __name__ == "__main__":
    sys.exit(main())
