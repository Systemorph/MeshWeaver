#!/usr/bin/env python3
"""seal-packages.py — the publication seals PER PACKAGE (MeshWeaver.Plugins#3507).

    seal-packages.py withhold --bake <dir> --withhold "<pkg> <pkg>…" --state <bake-scope state dir>
                              [--github-output <file>]
    seal-packages.py listing  --bake <dir> --content <repo checkout> --commit <sha>
                              [--withhold "<pkg>…"] [--state <dir>] [--out <file>]
    seal-packages.py --self-test

WHY. A sealed publication used to be ONE listing for ONE commit: `_complete` named every bundle,
`source-commit.txt` named the commit all of them were baked from. A caller whose packages are
published per package (MeshWeaver.Plugins: a package waits while its lock is unsettled or its
platform floor unstamped, and everything depending on it waits with it) could therefore seal only a
commit at which NOTHING waited — and under steady merging that commit almost never exists: the
`plugins` publication stood still from 06:56Z on 2026-10-12 while every merge of the day waited
behind somebody else's pending package.

So each run seals every package that IS publishable at its commit and carries the previously sealed
build forward for every package that waits. The listing this script writes —
`sealed-packages.json`, beside `source-commit.txt`, uploaded before the sentinel — names, PER
PACKAGE, the build that is sealed: its bundle, the commit it was baked from, its version, what it
requires, and whether it was carried.

THE TWO HALVES.

  withhold   runs after the bake and BEFORE the carry-forward. It deletes every withheld package's
             freshly baked `<pkg>.zip` (and its module package `<pkg>.module.nupkg`, lower-cased,
             under `modules/`), so `carry-forward-bundles.sh` — the SAME verified carry the narrowed
             bake uses (digest + publication stamp, refuse-never-shrink) — fetches the previously
             sealed bytes in their place. A withheld package the sealed publication never listed
             stays OUT (it has never been sealed for this identity; there is nothing to carry). A
             publication that could not be READ is a refusal: carrying forward from an unknown
             publication either shrinks it or seals a guess.

             THREE kinds of waiting package, and only one of them is "never sealed":
               carried       its bundle (or module package) is in the sealed publication — fetched.
               never sealed  this run baked bytes for it (a bundle or a module package) that the
                             sealed publication does NOT hold — it stays out, and so does every
                             package that requires it: withhold widens the waiting set over that
                             closure (with --content), so a dependent WAITS, with the reason named,
                             instead of the listing refusing the whole seal.
               bundle-less   it bakes neither a bundle nor a module package (a content-only package),
                             so this publication never seals it either way: a requirement on it is
                             not this listing's to check, exactly like another repository's package.
             A carried module package brings every assembly it carries back at the OLD build — its
             declared module and every module-owned sibling riding in it — so a fresh module package
             carrying any of those names waits too and is carried from the same publication
             (transitively); otherwise the publication seals one name at two builds, which every
             consumer composing both refuses (MeshWeaver.Plugins run 39476483432).
             The effective waiting set and the never-sealed set are written to the state directory,
             and `listing` reads them — the listing and its floor check see what withhold decided.
             (Plugins run 38527548962: 26 waiting content-only packages, AI among them, were read as
             "never sealed", and every carried dependent's `requires AI@…` refused the seal.)

  listing    runs after the carry-forward. It first REFUSES a module set that would carry one
             `MeshWeaver.*` assembly name at two builds — the set it is about to seal, after withhold
             and the carry replaced fresh module packages, which the compose-time verdict never sees.
             It then writes the per-package listing and REFUSES a seal in
             which a package's declared `requires` on another package of the SAME listing is not
             met by the version the listing seals for it — a carried dependency held at 1.21.1
             under a fresh dependent declaring `^1.23.0` is exactly the set Systemorph/MeshWeaver#6192
             saw a portal refuse wholesale after it had landed. Refused HERE, before anything is
             written, the existing publication stays sealed and intact.

The publication commit (`source-commit.txt`) stays THIS run's commit: the never-seal-backwards rule
and every reader that orders publications keep reading it. A reader that wants the commit ONE
package's bytes came from reads the listing, and falls back to `source-commit.txt` for every bundle
of a publication sealed before the listing existed (SealedPublicationIndex.PackagesOf).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

LISTING = "sealed-packages.json"
SCHEMA = "mw-sealed-packages/1"
MODULES_DIR = "modules"

# bake-scope.sh's artefacts (its `read_publication_for_carry`), one directory per run.
STATE_PUBLICATION = "publication-state"          # sealed | absent | unreadable <reason>
STATE_BUNDLES = "published-bundles.txt"          # the sealed `_complete`
STATE_MODULES = "published-modules.txt"          # the sealed `modules/_index` (absent: none sealed)
STATE_COMMIT = "published-commit.txt"            # the sealed `source-commit.txt`
STATE_PACKAGES = "published-packages.json"       # the sealed per-package listing (absent: legacy)
STATE_CARRY_MODULES = "carry-modules.txt"        # what `withhold` asks the module carry to fetch
STATE_WITHHELD = "withheld-effective.txt"        # the waiting set `withhold` settled on (input + closure)
STATE_NEVER_SEALED = "never-sealed.txt"          # waiting packages with fresh bytes and no sealed build


# ─── THE ONE reading of a requirement — MeshWeaver.Plugin.Packaging.PackageRequirement, in Python ───
# Two readings of one range either never converge or never fire, and both are silent (#6067). So
# this is the C# rule case for case: caret/tilde/>=/>/=/bare/empty/*; anything else is None
# (unverifiable), and the caller REFUSES None — a floor nothing can check is not a met floor.
_VERSION = re.compile(r"^\s*v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?\s*$")


def _parse(text: str | None):
    m = _VERSION.match(text or "")
    if not m or (text or "").strip().startswith("v"):
        return None
    pre = m.group(4)
    return (int(m.group(1)), int(m.group(2) or 0), int(m.group(3) or 0)), pre


def _pre_key(pre: str) -> tuple:
    # NuGetVersionComparer.ComparePreRelease, identifier by identifier: both numeric compare as
    # NUMBERS (alpha.10 > alpha.2), a numeric identifier ranks below an alphanumeric one (SemVer
    # §11.4.3), text compares ordinally, and fewer identifiers rank lower when the rest are equal —
    # which is tuple order once each identifier is a (rank, value) pair.
    return tuple((0, int(part), "") if part.isdigit() else (1, 0, part) for part in pre.split("."))


def _key(parsed) -> tuple:
    (major, minor, patch), pre = parsed
    # NuGet order: a release sorts above its own prereleases.
    return (major, minor, patch, 1 if pre is None else 0, _pre_key(pre) if pre else ())


def satisfies(range_text: str | None, version: str) -> bool | None:
    """Whether `version` meets `range_text` — None when either cannot be read."""
    have = _parse(version)
    if have is None:
        return None
    constraint = (range_text or "").strip()
    if constraint in ("", "*"):
        return True
    for op in (">=", ">"):
        if constraint.startswith(op):
            floor = _parse(constraint[len(op):])
            if floor is None:
                return None
            return _key(have) >= _key(floor) if op == ">=" else _key(have) > _key(floor)
    if constraint[0] in "^~":
        floor = _parse(constraint[1:])
        if floor is None:
            return None
        (major, minor, patch), _ = floor
        if constraint[0] == "^":
            ceiling = (major + 1, 0, 0) if major > 0 else (0, minor + 1, 0) if minor > 0 else (0, 0, patch + 1)
        else:
            ceiling = (major, minor + 1, 0)
        return _key(floor) <= _key(have) < (*ceiling, 1, ())
    pinned = _parse(constraint[1:] if constraint.startswith("=") else constraint)
    return None if pinned is None else _key(have) == _key(pinned)


def dependency_id(entry: str) -> str:
    return entry.split("@", 1)[0].strip()


def range_of(entry: str) -> str:
    return entry.split("@", 1)[1].strip() if "@" in entry else ""


# ─── reading the content tree ────────────────────────────────────────────────────────────────────
def _json(text: str | None) -> dict | None:
    if text is None:
        return None
    try:
        value = json.loads(text)
    except json.JSONDecodeError:
        return None
    return value if isinstance(value, dict) else None


def _requires(index: dict | None) -> list[str]:
    content = (index or {}).get("content") or {}
    requires = content.get("requires") if isinstance(content, dict) else None
    return sorted(str(r) for r in requires) if isinstance(requires, list) else []


def _read(path: Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return None


def _git_show(repo: Path, commit: str, rel: str) -> str | None:
    done = subprocess.run(["git", "-C", str(repo), "show", f"{commit}:{rel}"],
                          capture_output=True, text=True)
    return done.stdout if done.returncode == 0 else None


def fresh_entry(repo: Path, package: str, commit: str) -> dict:
    lock = _json(_read(repo / package / "manifest.lock"))
    return {"package": package, "bundle": f"{package}.zip", "sourceCommit": commit,
            "version": (lock or {}).get("version"), "requires": _requires(_json(_read(repo / package / "index.json"))),
            "carried": False}


def legacy_entry(repo: Path, package: str, commit: str | None) -> dict:
    """A carried package of a publication sealed BEFORE the listing existed: every bundle of it was
    baked from `source-commit.txt`, so its version and requires are that commit's committed files."""
    if not commit:
        return {"package": package, "bundle": f"{package}.zip", "sourceCommit": None, "version": None,
                "requires": [], "carried": True}
    lock = _json(_git_show(repo, commit, f"{package}/manifest.lock"))
    index = _json(_git_show(repo, commit, f"{package}/index.json"))
    return {"package": package, "bundle": f"{package}.zip", "sourceCommit": commit,
            "version": (lock or {}).get("version"), "requires": _requires(index), "carried": True}


def _words(text: str | None) -> list[str]:
    return sorted({w for w in (text or "").split() if w})


def _lines(path: Path) -> list[str]:
    text = _read(path)
    return [line.strip() for line in (text or "").splitlines() if line.strip()]


# ─── cross-package SOURCE includes: what a bundle's bytes were compiled from ────────────────────
# 🚨 A `requires` range is not the only thing that ties two packages of one listing together. A
# NodeType's `sources` may read ANOTHER package's source nodes (`shared=@Store/Core/Source`): those
# files are COMPILED INTO the dependent's assembly, and the bundle records a fingerprint over every
# source it read (`sourcePaths` / `sourceIncludes`). A consumer installs the dependent's own nodes
# from its bundle and the included package's nodes from THAT package's bundle — so when the listing
# seals the two from commits whose included files differ, the dependent's fingerprint matches no
# source set any consumer can ever hold, and every adopter DECLINES it (PrebuiltAssemblySeeder,
# `DECLINED before writing (#2813)`): a portal compiles it at boot, and a satellite gate that installs
# the publication is FATAL ("the gate did not judge the bytes the bake shipped").
#
# Measured: the `plugins` publication for c003e001 sealed `Hosting` CARRIED (its lock waited) beside
# `Store` FRESH, while `Hosting/InstanceAction`, `Hosting/ApprovalInbox` and `Hosting/InstanceRequest`
# compile `shared=@Store/Core/Source` & co. Every Reinsurance, Manufacturing and Crm gate that
# installed it from 2026-10-17 ~10:47Z on went red on exactly those three types.
#
# So both halves hold a pair (D compiles sources of P) to ONE rule: the files D read from P are the
# same at the commit the listing seals D from and the commit it seals P from.
#   withhold  restores it where it can: a FRESH P under a CARRIED D waits too (carried at its sealed
#             build) when that build matches what D was compiled against; a FRESH D over a CARRIED P
#             waits too (carried, or — never sealed — out). Neither move ever ships bytes the sealed
#             publication did not already hold together.
#   listing   refuses a seal that would INTRODUCE such a pair (it was consistent in the sealed
#             publication and is not in this listing). A pair already inconsistent in the sealed
#             publication that nothing here can restore is a warning, never a refusal: refusing would
#             keep exactly that publication sealed.
WORKTREE = None  # the content checkout itself: what a package sealed FRESH is baked from


_QUERY_NAME = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_.\-]*$")        # CodeQueryResolver.NamePattern
_CODE_INCLUDE = re.compile(r"@@(\w[\w\-./]*)")                      # NodeCompileShaping.CodeIncludePattern
_COMPILED_EXTENSIONS = (".cs", ".json")                             # where a Code node's text lives


def _is_bare_path(value: str) -> bool:
    """CodeQueryResolver.IsBarePath, conservatively: one unquoted term holding a `/` and no field
    qualifier is a path reference, exactly like its `@` spelling (MeshWeaver#5508)."""
    return ("/" in value and not any(ch.isspace() for ch in value) and ":" not in value
            and '"' not in value and "'" not in value and not value.startswith("-"))


class UnanchoredQuery(ValueError):
    """A `sources` / `tests` query with no `path:` / `namespace:` anchor: NodeSetQuery matches it
    against Code nodes under EVERY path, so which packages' sources it compiles cannot be named."""


def _query_target(entry: str) -> str | None:
    """The node-path prefix a `sources` / `tests` entry reads, None when package-local or
    unreadable. A port of CodeQueryResolver.ParseName + Expand: the `name=` prefix only when the
    name is an identifier (`NamePattern`) and the rest is non-empty, both sides trimmed; `$self`
    is the NodeType's own path (package-local); `@`, `@@` and a BARE PATH are path references; a
    reference that still carries a `:` and every other entry is a query whose `namespace:` /
    `path:` token names the location — a single-segment `namespace:` is rebased under the type.
    Raises UnanchoredQuery for a query with neither anchor (`nodeType:Code`, free text)."""
    value = entry.strip()
    if not value:
        return None
    eq = value.find("=")
    if eq > 0:
        name, rest = value[:eq].rstrip(), value[eq + 1:].strip()
        if rest and _QUERY_NAME.match(name):
            value = rest
    if "$self" in value:
        return None
    if value.startswith("@") or _is_bare_path(value):
        stripped = value.lstrip("@").lstrip()
        if not stripped:
            return None
        if ":" not in stripped:
            return stripped.strip("/") or None
        value = stripped
    for token in value.split():
        for key in ("namespace:", "path:"):
            if token.lower().startswith(key):  # NodeSetQuery reads selectors case-insensitively
                candidate = token[len(key):].strip("/")
                # CodeQueryResolver rebases a single-segment NAMESPACE under the NodeType (package-
                # local); a `path:` is absolute whatever its length (`path:Store` is Store's root).
                if key == "path:":
                    return candidate or None
                return candidate if "/" in candidate else None
    raise UnanchoredQuery(entry)


def _first(path: str) -> str:
    return path.split("/", 1)[0]


def _lenient_json(text: str):
    """json.loads with the tolerances TreeNodeLoader / JsonFileParser apply to a node file: a leading
    UTF-8 BOM, `//` and `/* */` comments, and trailing commas. Comments go FIRST, so a trailing comma
    followed by a comment (`["@A/B", /* why */]`) is still seen as trailing. Raises ValueError when it
    still does not parse."""
    return json.loads(_strip_json(_strip_json(text.lstrip("﻿"), commas=False), comments=False))


def _ci(mapping, key: str):
    """A case-insensitive property read — the web-defaults behaviour TreeNodeLoader and the content
    deserializer apply (`Content`, `Sources`, `IsExecutable`, …)."""
    if not isinstance(mapping, dict):
        return None
    if key in mapping:
        return mapping[key]
    return next((v for k, v in mapping.items() if isinstance(k, str) and k.lower() == key.lower()), None)


def _strip_json(text: str, comments: bool = True, commas: bool = True) -> str:
    """One string-aware pass removing comments and/or trailing commas."""
    out: list[str] = []
    i, n, in_string = 0, len(text), False
    while i < n:
        ch = text[i]
        if in_string:
            out.append(ch)
            if ch == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if ch == '"':
                in_string = False
            i += 1
            continue
        if ch == '"':
            in_string = True
            out.append(ch)
            i += 1
            continue
        if comments and text.startswith("//", i):
            end = text.find("\n", i)
            i = n if end < 0 else end
            continue
        if comments and text.startswith("/*", i):
            end = text.find("*/", i + 2)
            if end < 0:
                raise ValueError("unterminated /* comment")
            i = end + 2
            continue
        if commas and ch == ",":
            j = i + 1
            while j < n and text[j].isspace():
                j += 1
            if j < n and text[j] in "}]":
                i += 1  # a trailing comma
                continue
        out.append(ch)
        i += 1
    return "".join(out)


def _compiled_code(path: str, text: str, commit: str | None, included: bool = False) -> str:
    """The code this file contributes to an assembly — NodeCompileShaping.CollectCompileSources'
    rule: a Code node (`CodeConfiguration`) with non-empty code that is NOT executable. A `.cs` node
    is one by construction (its header carries no `isExecutable`); a `.json` file is one only when its
    content says so — a Markdown node, an executable cell or a NodeType never enters the assembly, so
    an `@@` in it is no input. A `.json` that does not parse cannot be classified and refuses.

    `included`: the node is an explicit `@@` TARGET. NodeCompileShaping.ResolveCodeIncludes
    substitutes any Code node it names, executable or not — the executable filter governs only the
    roots a query selects."""
    if path.endswith(".cs"):
        return text
    try:
        document = _lenient_json(text)
    except ValueError as error:
        raise SystemExit(f"✗ seal-packages: {path} at {_short(commit) if commit else 'the checkout'} carries `@@` "
                         f"and does not parse ({error}) — whether it is compiled cannot be decided")
    content = _ci(document, "content")
    if not isinstance(content, dict) or content.get("$type") != "CodeConfiguration":
        return ""
    if _ci(content, "isExecutable") and not included:
        return ""
    code = _ci(content, "code")
    return code if isinstance(code, str) else ""


class SourceIncludes:
    """Reads, per (commit, package), which other packages' files the package's compiled bytes were
    built from, and whether those files are identical between two commits. `WORKTREE` stands for
    the checkout.

    What counts mirrors the compiler: every NodeType's `sources` AND `tests` (NodeSet.ResolveSources
    compiles their union), plus every `@@` include (NodeCompileShaping.ResolveCodeIncludes, hashed by
    NodeTypeSourceFingerprint.ComputeWithIncludes) — in the package's own files and, transitively, in
    every file it reads from another package. Paths match CASE-INSENSITIVELY (NodeSetQuery), so each
    one is resolved to its on-disk casing at the commit it is read at, and compared with `:(icase)`."""

    def __init__(self, repo: Path):
        self.repo = repo
        self._edges: dict[tuple[str | None, str], dict[str, tuple[str, ...]] | None] = {}
        self._known: dict[str, bool] = {}
        self._same: dict[tuple, bool] = {}
        self._listing: dict[tuple[str | None, str], list[tuple[str, bool]]] = {}
        self._packages = {d.name.lower(): d.name for d in repo.iterdir()
                          if d.is_dir() and (d / "index.json").is_file()} if repo.is_dir() else {}

    def _git(self, *args: str, stdin: str | None = None) -> subprocess.CompletedProcess:
        return subprocess.run(["git", "-C", str(self.repo), *args], capture_output=True, text=True, input=stdin)

    def resolvable(self, commit: str | None) -> bool:
        if commit is WORKTREE:
            return True
        if commit not in self._known:
            self._known[commit] = self._git("cat-file", "-e", f"{commit}^{{commit}}").returncode == 0
        return self._known[commit]

    def _entries(self, commit: str | None, directory: str) -> list[tuple[str, bool]]:
        """(name, is-directory) for every entry of `directory` ("" = the root) at `commit`."""
        key = (commit, directory)
        if key not in self._listing:
            if commit is WORKTREE:
                base = self.repo / directory if directory else self.repo
                self._listing[key] = ([(e.name, e.is_dir()) for e in base.iterdir()] if base.is_dir() else [])
            else:
                # -z: a non-ASCII name arrives verbatim, never C-quoted ("\303\264…" names no file).
                done = self._git("ls-tree", "-z", commit, "--", f"{directory}/" if directory else ".")
                if done.returncode != 0:
                    raise SystemExit(f"✗ seal-packages: could not list {directory or 'the root'} at "
                                     f"{_short(commit)} ({done.stderr.strip()})")
                rows = [row.split("\t", 1) for row in done.stdout.split("\0") if "\t" in row]
                self._listing[key] = [(path.rsplit("/", 1)[-1], meta.split()[1] == "tree") for meta, path in rows]
        return self._listing[key]

    def resolve(self, commit: str | None, target: str) -> str | None:
        """`target` in its on-disk casing at `commit` — None when its first segment is no package of
        this repository (another repository's path, or text that only looks like one). A path INTO a
        package that does not resolve is a refusal: what the build read cannot be named."""
        segments = [s for s in target.split("/") if s]
        package = self._packages.get(segments[0].lower()) if segments else None
        if package is None:
            return None
        real = [package]
        for index, segment in enumerate(segments[1:], start=1):
            entries = self._entries(commit, "/".join(real))
            directory = next((name for name, is_dir in entries if is_dir and name.lower() == segment.lower()), None)
            if directory is not None:
                real.append(directory)
                continue
            last = index == len(segments) - 1
            node = next((name.rsplit(".", 1)[0] for name, is_dir in entries
                         if last and not is_dir and name.lower() in {f"{segment.lower()}{e}" for e in _COMPILED_EXTENSIONS}),
                        None)
            if node is None:
                raise SystemExit(f"✗ seal-packages: '{target}' names no folder or node in {package} at "
                                 f"{_short(commit) if commit else 'the checkout'} — what that build compiled from it "
                                 "cannot be named, so whether it still matches cannot be decided")
            real.append(node)
        return "/".join(real)

    def _texts(self, commit: str | None, prefix: str, needle: str) -> list[tuple[str, str]]:
        """(path, text) of every compiled-extension file under `prefix` (a folder, or
        `<prefix>.<ext>`; real casing) at `commit` that contains `needle`."""
        if commit is WORKTREE:
            base = self.repo / prefix
            files = list(base.rglob("*")) if base.is_dir() else []
            if base.parent.is_dir():
                files += [base.parent / f"{base.name}{ext}" for ext in _COMPILED_EXTENSIONS]
            texts = ((str(f.relative_to(self.repo)), _read(f)) for f in sorted(files)
                     if f.is_file() and f.suffix in _COMPILED_EXTENSIONS)
            return [(p, t) for p, t in texts if t and needle in t]
        specs = [prefix, *(f"{prefix}{ext}" for ext in _COMPILED_EXTENSIONS)]
        listed = self._git("grep", "-l", "-z", "-F", needle, commit, "--", *specs)
        if listed.returncode not in (0, 1):  # 1 = no match
            raise SystemExit(f"✗ seal-packages: could not read {prefix} at {_short(commit)} "
                             f"({listed.stderr.strip()}) — what a build compiled cannot be decided")
        paths = [row.split(":", 1)[1] for row in listed.stdout.split("\0") if ":" in row]
        texts = ((p, _git_show(self.repo, commit, p)) for p in paths if p.endswith(_COMPILED_EXTENSIONS))
        return [(p, t) for p, t in texts if t]

    def node_files(self, commit: str | None, node: str) -> list[str]:
        """Every file that stores the ONE node at `node` (real casing), as NodeFileMapper maps it:
        `<node>.<ext>` (a leaf) or `<node>/index.<ext>` (a node with children), any extension,
        `index` matched case-insensitively."""
        parent, _, name = node.rpartition("/")
        files = [f"{parent}/{entry}" if parent else entry for entry, is_dir in self._entries(commit, parent)
                 if not is_dir and "." in entry and entry.rsplit(".", 1)[0] == name]
        files += [f"{node}/{entry}" for entry, is_dir in self._entries(commit, node)
                  if not is_dir and "." in entry and entry.rsplit(".", 1)[0].lower() == "index"]
        return sorted(files)

    def _node_texts(self, commit: str | None, node: str) -> list[tuple[str, str]]:
        """(path, text) of the compiled-extension files storing the node one `@@` include names. A
        target stored in none of NodeFileMapper's representations is a refusal: what the include
        substituted cannot be read."""
        files = self.node_files(commit, node)
        if not files:
            raise SystemExit(f"✗ seal-packages: the `@@` target '{node}' is stored as neither `{node}.<ext>` nor "
                             f"`{node}/index.<ext>` at {_short(commit) if commit else 'the checkout'} — what that "
                             "include compiled in cannot be read")
        texts = ((p, _read(self.repo / p) if commit is WORKTREE else _git_show(self.repo, commit, p))
                 for p in files if p.endswith(_COMPILED_EXTENSIONS))
        return [(p, t) for p, t in texts if t and "@@" in t]

    def edges(self, commit: str | None, package: str) -> dict[str, tuple[str, ...]] | None:
        """{other package: (path prefixes read, on-disk casing)}; None when `commit` is not in the
        checkout."""
        key = (commit, package)
        if key in self._edges:
            return self._edges[key]
        if not self.resolvable(commit):
            self._edges[key] = None
            return None
        found: dict[str, set[str]] = {}
        pending: list[tuple[str, bool]] = []
        walked: set[tuple[str, bool]] = set()

        def add(target: str, included: bool) -> None:
            """`included`: an explicit `@@` target (one node, walked whatever IsExecutable says);
            otherwise a query prefix (every compiled node under it, executable ones excluded)."""
            target = target.strip().rstrip("./")
            real = self.resolve(commit, target) if target else None
            if not real:
                return
            other = _first(real)
            if other.lower() != package.lower():
                found.setdefault(other, set()).add(real)
            elif not included:
                return  # an own-package query: the package's own scan below already reads it
            if (real, included) not in walked:
                walked.add((real, included))
                pending.append((real, included))

        for path, text in self._texts(commit, package, "NodeTypeDefinition"):
            if not path.endswith(".json"):
                continue
            try:
                document = _lenient_json(text)
            except ValueError as error:
                raise SystemExit(f"✗ seal-packages: {path} at {_short(commit) if commit else 'the checkout'} names "
                                 f"a NodeTypeDefinition and does not parse ({error}) — what it compiles cannot be "
                                 "read, and skipping it would seal on a guess")
            content = _ci(document, "content")
            if not isinstance(content, dict) or content.get("$type") != "NodeTypeDefinition":
                continue
            for entry in [*(_ci(content, "sources") or []), *(_ci(content, "tests") or [])]:
                try:
                    target = _query_target(entry) if isinstance(entry, str) else None
                except UnanchoredQuery:
                    raise SystemExit(
                        f"✗ seal-packages: {path} at {_short(commit) if commit else 'the checkout'} declares the "
                        f"query '{entry}' with no `path:` or `namespace:` anchor — NodeSetQuery matches it against "
                        "Code nodes under every path, so which packages' sources this NodeType compiles cannot be "
                        "established. Anchor the query to the location it means.")
                if target:
                    add(target, included=False)
        for path, text in self._texts(commit, package, "@@"):
            for match in _CODE_INCLUDE.finditer(_compiled_code(path, text, commit)):
                add(match.group(1), included=True)
        while pending:  # what is compiled in from another package's files, and what an @@ pulls in
            prefix, included = pending.pop()
            for path, text in (self._node_texts(commit, prefix) if included else self._texts(commit, prefix, "@@")):
                for match in _CODE_INCLUDE.finditer(_compiled_code(path, text, commit, included)):
                    add(match.group(1), included=True)
        value = {p: tuple(sorted(v)) for p, v in sorted(found.items()) if v}
        self._edges[key] = value
        return value

    def same(self, a: str | None, b: str | None, prefixes: tuple[str, ...]) -> bool:
        """Whether every file under `prefixes` (a folder, or a node file `<prefix>.<ext>`) is identical
        at `a` and `b`, matched case-insensitively as the compiler matches them. An unreadable answer
        is a refusal, never a guess."""
        if a == b or not prefixes:
            return True
        key = (a, b, prefixes)
        if key in self._same:
            return self._same[key]
        revs = [c for c in (a, b) if c is not WORKTREE]
        specs = [f":(icase){s}" for p in prefixes for s in (p, *(f"{p}{ext}" for ext in _COMPILED_EXTENSIONS))]
        done = self._git("diff", "--quiet", *revs, "--", *specs)
        if done.returncode in (0, 1):
            self._same[key] = done.returncode == 0
            return self._same[key]
        raise SystemExit(f"✗ seal-packages: could not compare {', '.join(prefixes)} between "
                         f"{_short(a) if a else 'the checkout'} and {_short(b) if b else 'the checkout'} "
                         f"({done.stderr.strip()}) — whether a carried build still matches the sources it was "
                         "compiled from cannot be decided, and sealing on a guess is the #2813 class.")


def _sealed_commits(state: Path, listed: set[str]) -> dict[str, str | None]:
    """{package: the commit the SEALED publication's build of it came from} for every sealed bundle —
    the per-package listing where there is one, else `source-commit.txt` (a legacy seal)."""
    prior = _json(_read(state / STATE_PACKAGES)) if (state / STATE_PACKAGES).exists() else None
    by_listing = {str(e["package"]): e.get("sourceCommit")
                  for e in (prior or {}).get("packages") or [] if isinstance(e, dict) and e.get("package")}
    legacy = (_read(state / STATE_COMMIT) or "").strip() or None
    legacy = None if legacy == "unknown" else legacy
    return {b[:-len(".zip")]: by_listing.get(b[:-len(".zip")], legacy) for b in listed if b.endswith(".zip")}


# ─── withhold ────────────────────────────────────────────────────────────────────────────────────
def _requires_in(repo: Path, package: str) -> list[str]:
    return _requires(_json(_read(repo / package / "index.json")))


# ─── one build per assembly name across the sealed MODULE set (Doc/Architecture/ModuleOwnedSiblingsRide) ───
MODULE_ASSEMBLY_FOLDER = "meshweaver/modules/"


def module_assemblies(path: Path) -> dict[str, str]:
    """{simple name: sha256} of every `MeshWeaver.*` assembly at the top of a module package's
    `meshweaver/modules/` — exactly the files node-repo-gate.yml and node-repo-publish-bake.yml
    compare when they refuse a set that carries one name at two builds (MeshWeaver#3732).

    🚨 A module package that is not a readable archive is REFUSED, never read as "carries
    nothing": an empty reading is the one answer under which no conflict can ever be found."""
    import hashlib
    try:
        with zipfile.ZipFile(path) as archive:
            found: dict[str, str] = {}
            for entry in archive.namelist():
                if not entry.startswith(MODULE_ASSEMBLY_FOLDER):
                    continue
                file = entry[len(MODULE_ASSEMBLY_FOLDER):]
                if "/" in file or not file.endswith(".dll"):
                    continue
                name = file[:-len(".dll")]
                if name == "MeshWeaver" or name.startswith("MeshWeaver."):
                    found[name] = hashlib.sha256(archive.read(entry)).hexdigest()
            return found
    except (zipfile.BadZipFile, OSError) as error:
        raise SystemExit(
            f"✗ seal-packages: the module package {path.name} could not be read as an archive ({error}) — which "
            "assemblies it carries, and so whether this publication seals one build per name, cannot be "
            "established. Nothing is written, and the existing publication stays sealed and intact.")


def module_build_violations(bake: Path) -> list[str]:
    """Every assembly name the module set this run is about to SEAL carries at more than one build.

    This is the producer's assertion over the set it actually publishes — AFTER withhold and the
    carry have replaced fresh module packages by previously sealed ones. The compose step's verdict
    in node-repo-publish-bake.yml runs before both, over the bake's own (fresh) bundles only, so it
    cannot see a carried module package beside a fresh one that rides the same name: that is the
    torn set MeshWeaver.Plugins run 39476483432 sealed (essentials.module.nupkg carried at
    MeshWeaver.Markdown.Collaboration 1d59587d…, ai.module.nupkg fresh riding it at 4959162f…)
    and every satellite gate then refused."""
    directory = bake / MODULES_DIR
    if not directory.is_dir():
        return []
    builds: dict[str, dict[str, list[str]]] = {}
    for module in sorted(directory.glob("*.module.nupkg")):
        for name, digest in module_assemblies(module).items():
            builds.setdefault(name, {}).setdefault(digest, []).append(module.name)
    problems = []
    for name in sorted(builds):
        if len(builds[name]) > 1:
            where = "; ".join(f"{digest[:16]} in {', '.join(packages)}"
                              for digest, packages in sorted(builds[name].items()))
            problems.append(f"{name} is sealed at {len(builds[name])} builds — {where}")
    return problems


def withhold(bake: Path, withheld: list[str], state: Path, repo: Path | None = None) -> dict:
    """Remove every withheld package's fresh bytes and say what the carry must fetch instead.

    With `repo` (the content checkout), a package this run would seal FRESH that requires a waiting
    package with NO sealed build waits too — transitively — so the listing never meets a dependent
    whose dependency it cannot seal. Without it the waiting set is exactly the input."""
    raw = (_read(state / STATE_PUBLICATION) or "").strip()
    status = raw.split(None, 1)[0] if raw else ""
    if status not in ("sealed", "absent"):
        why = raw.split(None, 1)[1] if " " in raw else "bake-scope.sh recorded no reading of the sealed publication"
        raise SystemExit(
            f"✗ seal-packages: {len(withheld)} package(s) wait ({' '.join(withheld)}), and their previously sealed "
            f"builds are carried forward from the sealed publication — which could not be read ({why}). Carrying "
            "from an unknown publication either SHRINKS it (every waiting package drops out of what every portal "
            "adopts) or seals a guess; nothing is written, and the existing publication stays sealed and intact.")
    listed = set(_lines(state / STATE_BUNDLES)) if status == "sealed" else set()
    listed_modules = set(_lines(state / STATE_MODULES)) if status == "sealed" else set()
    carried, absent, bundleless, modules_carried = [], [], [], []
    pulled: dict[str, str] = {}  # package → the never-sealed dependency that made it wait
    riders: dict[str, str] = {}  # package → why a held module package made it wait (one build per name)
    held_names: dict[str, set[str]] = {}  # held package → the MeshWeaver.* assemblies its module package carries

    def hold(package: str) -> None:
        bundle = bake / f"{package}.zip"
        fresh_bundle = bundle.exists()
        if fresh_bundle:
            bundle.unlink()
        module = f"{package.lower()}.module.nupkg"
        fresh = bake / MODULES_DIR / module
        fresh_module = fresh.exists()
        if fresh_module:
            # Read BEFORE the delete: the names this package's module package carries are the names
            # whose sealed build the carry puts back — and every fresh module package riding one of
            # them would then seal that name at a second build (module_step below).
            held_names[package] = set(module_assemblies(fresh))
            fresh.unlink()
        if module in listed_modules:
            modules_carried.append(module)
        sealed_bundle = f"{package}.zip" in listed
        if sealed_bundle:
            carried.append(package)
        # Never sealed = this run baked bytes the sealed publication holds no build of. A package
        # that bakes NEITHER (content-only) is not "never sealed": no publication ever seals it.
        if (fresh_bundle and not sealed_bundle) or (fresh_module and module not in listed_modules):
            absent.append(package)
        elif not sealed_bundle and module not in listed_modules:
            bundleless.append(package)

    # A module package is named lower-case (`shop.module.nupkg`) while its bundle keeps the package's
    # case (`Shop.zip`). The package a module package belongs to is read off every bundle name this
    # run knows — fresh in the bake AND previously sealed — before any hold deletes one, and the
    # content checkout's folders (with --content) take precedence. Without --content the mapping
    # used to be empty, so a mixed-case rider was held as `shop`: its module went, `Shop.zip` stayed
    # fresh and no bundle carry was asked for.
    by_lower = {name[:-len(".zip")].lower(): name[:-len(".zip")] for name in sorted(listed) if name.endswith(".zip")}
    by_lower.update({b.stem.lower(): b.stem for b in sorted(bake.glob("*.zip"))})
    if repo is not None and repo.is_dir():
        by_lower.update({d.name.lower(): d.name for d in repo.iterdir() if d.is_dir()})
    waiting = list(dict.fromkeys(withheld))
    for package in waiting:
        hold(package)
    sourced: dict[str, str] = {}  # package → why a source include made it wait
    source_notes: dict[tuple[str, str], str] = {}

    def module_step() -> bool:
        """ONE BUILD PER NAME across the module set this publication seals.

        A held package's module package is carried at its PREVIOUSLY sealed build, and with it every
        assembly it carries — its declared module and every module-owned sibling riding in it. A
        fresh module package that carries one of those names (riding it, or declaring it) would seal
        that name at THIS commit's build beside the carried one: the loader keeps whichever copy it
        sees first and every satellite gate composing both refuses the set (MeshWeaver#3732). So such
        a package waits too, and is carried from the same publication — the pair stays one build.

        Decided on NAMES, before the carry fetches anything: the sealed copy's bytes are not on hand
        here, so a fresh copy that happens to be byte-identical to the sealed one waits as well. That
        is the conservative direction — a later publication seals it — and `listing` asserts the
        result over the bytes actually sealed (module_build_violations)."""
        carried_names: dict[str, str] = {}
        for package in waiting:
            # Only a module package that is actually CARRIED brings an old build back. A held package
            # whose module was never sealed is deleted without replacement — there is no old build
            # to conflict with, and it reaches its dependents through the requires closure instead.
            if f"{package.lower()}.module.nupkg" not in listed_modules:
                continue
            for name in sorted(held_names.get(package, ())):
                carried_names.setdefault(name, package)
        if not carried_names or not (bake / MODULES_DIR).is_dir():
            return False
        held = set(waiting)
        for module in sorted((bake / MODULES_DIR).glob("*.module.nupkg")):
            stem = module.name[:-len(".module.nupkg")]
            package = by_lower.get(stem, stem)
            if package in held:
                continue
            shared = sorted(set(module_assemblies(module)) & set(carried_names))
            if not shared:
                continue
            owners = sorted({carried_names[n] for n in shared})
            riders[package] = (f"its module package carries {', '.join(shared)}, which "
                               f"{', '.join(owners)} {'carries' if len(owners) == 1 else 'carry'} at the previously "
                               "sealed build — one build per name, so it is carried from the same publication")
            waiting.append(package)
            hold(package)
            return True
        return False

    steps = [module_step]
    if repo is not None:
        includes = SourceIncludes(repo)
        sealed_at = _sealed_commits(state, listed)

        def requires_step() -> bool:
            never, held = set(absent), set(waiting)
            # Every package this run would still seal FRESH: a bundle left in the bake, or a module
            # package (named lower-case — mapped back to its package folder).
            fresh_now = {b.stem for b in bake.glob("*.zip")}
            if (bake / MODULES_DIR).is_dir():
                fresh_now |= {by_lower.get(m.name[:-len(".module.nupkg")], m.name[:-len(".module.nupkg")])
                              for m in (bake / MODULES_DIR).glob("*.module.nupkg")}
            more = {}
            for package in sorted(fresh_now - held):
                blocker = next((d for d in (dependency_id(r) for r in _requires_in(repo, package))
                                if d in never and d != package), None)
                if blocker:
                    more[package] = blocker
            for package, blocker in more.items():
                pulled[package] = blocker
                waiting.append(package)
                hold(package)
            return bool(more)

        def source_step() -> bool:
            """ONE move that restores a source-include pair, or none. One at a time: every hold
            changes which commit the next pair is read at."""
            held = set(waiting)
            shipped = {b.stem for b in bake.glob("*.zip")} | {p for p in held if f"{p}.zip" in listed}

            def at(package: str):
                # False: held, but no commit is recorded for its sealed build (nothing to compare).
                return (sealed_at.get(package) or False) if package in held else WORKTREE

            def carries_cleanly(package: str) -> bool:
                # Held, it is carried — not dropped as never sealed by its module package either.
                module = f"{package.lower()}.module.nupkg"
                return (f"{package}.zip" in listed
                        and (module in listed_modules or not (bake / MODULES_DIR / module).exists()))

            for dependent in sorted(shipped):
                built = at(dependent)
                if built is False:
                    continue
                reads = includes.edges(built, dependent)
                if reads is None:
                    if includes.edges(WORKTREE, dependent):
                        raise SystemExit(
                            f"✗ seal-packages: {dependent} is carried at {_short(built)}, which this checkout "
                            "does not hold, and its NodeTypes compile other packages' sources — whether the "
                            "carried bytes still match them cannot be decided. The publish-bake lane checks the "
                            "content out with full history when it narrows (`narrow-by-affected`); nothing is "
                            "written, and the existing publication stays sealed and intact.")
                    continue
                for dependency, prefixes in reads.items():
                    where = ", ".join(prefixes)
                    if dependency not in shipped:
                        # A provider held OUT (never sealed) is no exemption: a fresh dependent would
                        # be sealed over sources no consumer of this publication receives. A
                        # content-only or another repository's package is not in `absent`.
                        if dependency in absent and dependent not in held:
                            sourced[dependent] = (f"its NodeTypes compile {where}, and {dependency} waits and has "
                                                  "never been sealed — so it waits with it (carried, or out)")
                            waiting.append(dependent)
                            hold(dependent)
                            return True
                        continue
                    against = at(dependency)
                    if against is False or includes.same(built, against, prefixes):
                        continue
                    if (dependency not in held and dependent in held and f"{dependency}.zip" not in listed):
                        # A NEW provider under a carried dependent: the sealed publication never held
                        # it, so holding it out keeps exactly what that publication was.
                        sourced[dependency] = (f"{dependent} waits at {_short(built)} and its NodeTypes compile "
                                               f"{where}; {dependency} has never been sealed, so it stays out "
                                               f"until {dependent} can be sealed with it")
                        waiting.append(dependency)
                        hold(dependency)
                        return True
                    if (dependency not in held and carries_cleanly(dependency)
                            and sealed_at.get(dependency)
                            and includes.same(built, sealed_at[dependency], prefixes)):
                        sourced[dependency] = (f"{dependent} waits at {_short(built)} and its NodeTypes compile "
                                               f"{where} — which moved since; {dependency} is held at "
                                               f"{_short(sealed_at[dependency])}, the build {dependent} was compiled against")
                        waiting.append(dependency)
                        hold(dependency)
                        return True
                    if dependent not in held:
                        own = sealed_at.get(dependent)
                        own_reads = includes.edges(own, dependent) if own else None
                        if f"{dependent}.zip" not in listed:
                            sourced[dependent] = (f"its NodeTypes compile {where}, and {dependency} waits at "
                                                  f"{_short(against)} — it has never been sealed, so it stays out "
                                                  "until both can be sealed together")
                        elif (carries_cleanly(dependent) and own_reads is not None
                              and includes.same(own, against, own_reads.get(dependency, ()))):
                            sourced[dependent] = (f"its NodeTypes compile {where}, and {dependency} waits at "
                                                  f"{_short(against)}; its own sealed build at {_short(own)} "
                                                  "was compiled against that")
                        else:
                            source_notes[(dependent, dependency)] = (
                                f"{dependent} (fresh) compiles {where}, {dependency} waits at {_short(against)}, and "
                                f"{dependent}'s sealed build was not compiled against it either — no build of the "
                                f"pair matches; {dependent} adopts once {dependency} is sealed")
                            continue
                        waiting.append(dependent)
                        hold(dependent)
                        return True
                    source_notes[(dependent, dependency)] = (
                        f"{dependent} (carried at {_short(built)}) compiles {where}, and no build of {dependency} "
                        f"the sealed publication holds matches it (this listing seals {dependency} at "
                        f"{_short(against) if against else 'this commit'}) — already so in the sealed publication; "
                        f"{dependent} adopts once it is sealed again")
            return False

        steps = [requires_step, source_step, module_step]
    # One move at a time, to a fixpoint: every hold changes what the other steps read.
    while any(step() for step in steps):
        pass
    (state / STATE_CARRY_MODULES).write_text("".join(f"{m}\n" for m in modules_carried), encoding="utf-8")
    (state / STATE_WITHHELD).write_text("".join(f"{p}\n" for p in waiting), encoding="utf-8")
    (state / STATE_NEVER_SEALED).write_text("".join(f"{p}\n" for p in absent), encoding="utf-8")
    return {"carried": carried, "absent": absent, "bundleless": bundleless, "modulesCarried": modules_carried,
            "status": status, "waiting": waiting, "pulled": pulled, "sourced": sourced, "riders": riders,
            "sourceNotes": [source_notes[k] for k in sorted(source_notes)]}


def cmd_withhold(a: argparse.Namespace) -> int:
    withheld = _words(a.withhold)
    if not withheld:
        print("seal-packages: nothing is withheld — every package this run baked is sealed at this commit")
        state = Path(a.state)
        state.mkdir(parents=True, exist_ok=True)
        for name in (STATE_WITHHELD, STATE_NEVER_SEALED):
            (state / name).write_text("", encoding="utf-8")
        if a.github_output:
            with open(a.github_output, "a", encoding="utf-8") as out:
                out.write("carry=false\ncarry-modules=false\n")
        return 0
    result = withhold(Path(a.bake), withheld, Path(a.state), Path(a.content) if a.content else None)
    print(f"seal-packages: {len(result['waiting'])} package(s) wait — their fresh bytes are NOT sealed:")
    for p in result["carried"]:
        print(f"  · {p}: carried forward — the previously sealed {p}.zip is fetched and verified in its place")
    for p in result["absent"]:
        print(f"  · {p}: never sealed for this identity — it stays out of the listing until it is publishable")
    for p in result["bundleless"]:
        print(f"  · {p}: bakes no bundle and no module package — nothing of it is sealed in any publication, "
              "so it neither travels nor blocks a dependent here")
    for p, dep in result["pulled"].items():
        print(f"  · {p}: waits too — it requires {dep}, which waits and has never been sealed for this identity")
    for p, why in result["sourced"].items():
        print(f"  · {p}: waits too — {why}")
    for p, why in result["riders"].items():
        print(f"  · {p}: waits too — {why}")
    for m in result["modulesCarried"]:
        print(f"  · module {m}: carried forward from the sealed module set")
    for note in result["sourceNotes"]:
        print(f"::warning title=A carried build that no sealed source set matches::{note}")
    print(f"::notice title=Sealed per package::{len(result['carried'])} waiting package(s) carried forward at their "
          f"previously sealed build, {len(result['absent'])} never sealed and left out, {len(result['bundleless'])} "
          f"bake no bundle, {len(result['pulled'])} wait because a dependency was never sealed, "
          f"{len(result['sourced'])} wait so that the sources a carried build compiled travel with it, "
          f"{len(result['riders'])} wait so that a carried module package's assemblies stay one build; every other "
          "package this run baked is sealed at this commit")
    if a.github_output:
        with open(a.github_output, "a", encoding="utf-8") as out:
            out.write(f"carry={'true' if result['carried'] else 'false'}\n")
            out.write(f"carry-modules={'true' if result['modulesCarried'] else 'false'}\n")
    return 0


# ─── listing ─────────────────────────────────────────────────────────────────────────────────────
def _prior_listing(path: Path, withheld: list[str]) -> dict | None:
    """The sealed publication's per-package listing — None ONLY when it is absent (a legacy seal).

    🚨 A listing that EXISTS and cannot be read, parsed, or does not have the listing's shape is not
    a legacy one: folded to None, every carried package would be described from the publication
    commit — the wrong version and requires for the floor verdict. Refused when a package waits
    (the only case that reads it); nothing is written."""
    if not path.exists():
        return None
    fault = None
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as ex:
        value, fault = None, f"{type(ex).__name__}: {ex}"
    if fault is None:
        packages = value.get("packages") if isinstance(value, dict) else None
        if not isinstance(packages, list):
            fault = "it carries no 'packages' list"
        elif any(not isinstance(e, dict) or not e.get("package") for e in packages):
            fault = "an entry has no 'package'"
    if fault is None:
        return value
    if withheld:
        raise SystemExit(
            f"✗ seal-packages: the sealed per-package listing ({LISTING}) exists but could not be read ({fault}). "
            f"{len(withheld)} package(s) wait ({' '.join(withheld)}) and are carried at the build that listing names; "
            "reading it as a legacy publication would describe them from the publication commit instead. "
            "Nothing is written, and the existing publication stays sealed and intact.")
    return None


def build_listing(bake: Path, repo: Path, commit: str, withheld: list[str], state: Path | None) -> dict:
    previous: dict[str, dict] = {}
    previous_commit = None
    if state is not None:
        prior = _prior_listing(state / STATE_PACKAGES, withheld)
        for entry in (prior or {}).get("packages") or []:
            if isinstance(entry, dict) and entry.get("package"):
                previous[str(entry["package"])] = entry
        previous_commit = (_read(state / STATE_COMMIT) or "").strip() or None
        if previous_commit == "unknown":
            previous_commit = None
    waiting = set(withheld)
    entries: list[dict] = []
    for bundle in sorted(bake.glob("*.zip")):
        package = bundle.stem
        if package in waiting:
            if package in previous:
                entry = dict(previous[package])
                entry["carried"] = True
                entry["bundle"] = f"{package}.zip"
                entry["requires"] = sorted(str(r) for r in entry.get("requires") or [])
            else:
                entry = legacy_entry(repo, package, previous_commit)
        else:
            entry = fresh_entry(repo, package, commit)
        module = f"{package.lower()}.module.nupkg"
        if (bake / MODULES_DIR / module).is_file():
            entry["module"] = f"{MODULES_DIR}/{module}"
        else:
            entry.pop("module", None)
        entries.append(entry)
    if not entries:
        raise SystemExit("✗ seal-packages: the bake directory holds no bundle — a listing of nothing is not a seal")
    return {"schema": SCHEMA, "publicationCommit": commit, "packages": entries}


def settled_waiting(withheld: list[str], state: Path | None) -> tuple[list[str], set[str] | None]:
    """The waiting set `withhold` settled on (the input plus its closure) and the never-sealed set.

    None for the never-sealed set when `withhold` recorded none (it did not run, or predates the
    record): the floor check then reads every waiting package missing from the listing as unmet."""
    if state is None or not (state / STATE_WITHHELD).exists():
        return withheld, None
    effective = list(dict.fromkeys([*withheld, *_lines(state / STATE_WITHHELD)]))
    never = set(_lines(state / STATE_NEVER_SEALED)) if (state / STATE_NEVER_SEALED).exists() else None
    return effective, never


def floor_violations(listing: dict, repo: Path, withheld: list[str],
                     never_sealed: set[str] | None = None) -> tuple[list[str], list[str]]:
    """(refusals, notes). A requirement on a package this listing does not hold is this listing's to
    refuse only when that package HAS bytes no publication sealed (`never_sealed`): one that bakes no
    bundle (content-only, or another repository's) is sealed by no publication either way."""
    by_name = {e["package"]: e for e in listing["packages"]}
    waiting = set(withheld)
    problems: list[str] = []
    notes: list[str] = []
    for entry in listing["packages"]:
        for requirement in entry.get("requires") or []:
            dep = dependency_id(requirement)
            if not dep or dep == entry["package"]:
                continue
            sealed = by_name.get(dep)
            if sealed is None:
                unsealable = dep in waiting if never_sealed is None else dep in never_sealed
                if not unsealable:
                    continue  # another repository's package, or one with no NodeType bundle at all
                if entry.get("carried") and never_sealed is not None:
                    # The carried bytes are the ones the sealed publication already holds beside the
                    # same absence: re-sealing them changes nothing, refusing them seals nothing better.
                    notes.append(f"{entry['package']} (carried at {_short(entry.get('sourceCommit'))}) requires "
                                 f"{requirement}, and {dep} has never been sealed for this identity — unchanged "
                                 f"from the sealed publication; it adopts once {dep} is sealed")
                    continue
                problems.append(f"{entry['package']} (sealed at {_short(entry.get('sourceCommit'))}) requires "
                                f"{requirement}, but {dep} waits and this listing seals NO build of it")
                continue
            have = sealed.get("version")
            verdict = satisfies(range_of(requirement), have) if have else None
            if verdict is True:
                continue
            if verdict is False:
                problems.append(f"{entry['package']} (sealed at {_short(entry.get('sourceCommit'))}) requires "
                                f"{requirement}, but this listing seals {dep} {have} "
                                f"({'carried at ' + _short(sealed.get('sourceCommit')) if sealed.get('carried') else 'fresh'})")
            else:
                problems.append(f"{entry['package']} requires {requirement}, and {dep}'s sealed version "
                                f"'{have}' cannot be checked against it — an unverifiable floor is not a met one")
    return problems, notes


def source_include_violations(listing: dict, repo: Path, state: Path | None,
                              never_sealed: set[str] | None = None) -> tuple[list[str], list[str]]:
    """(refusals, notes) for the source-include rule (see SourceIncludes). A pair whose included files
    differ between the commits this listing seals them from is REFUSED unless the sealed publication is
    POSITIVELY shown to have held the same two packages split already (then it is noted: refusing would
    keep exactly that publication). Anything that cannot be checked refuses — a carried build with no
    readable commit, a provider whose commit is unknown — and so does a fresh dependent of a provider
    that waits and was never sealed (its sources reach no consumer)."""
    includes = SourceIncludes(repo)
    listed = set(_lines(state / STATE_BUNDLES)) if state is not None else set()
    before = _sealed_commits(state, listed) if state is not None else {}
    by_name = {e["package"]: e for e in listing["packages"]}
    absent = never_sealed or set()

    def sealed_from(entry: dict):
        return entry.get("sourceCommit") if entry.get("carried") else WORKTREE

    problems: list[str] = []
    notes: list[str] = []
    for name, entry in sorted(by_name.items()):
        built = sealed_from(entry)
        reads = includes.edges(built, name) if not (entry.get("carried") and not built) else None
        if reads is None:
            # Whatever the checkout shows now: HEAD may have dropped the includes, or the package,
            # that the carried build was compiled from — only the build's own commit can say.
            problems.append(f"{name} is carried at {_short(built)}, which this checkout cannot read — what its "
                            "bytes were compiled from, and so whether they match the sources this listing seals, "
                            "cannot be decided")
            continue
        for dependency, prefixes in reads.items():
            where = ", ".join(prefixes)
            other = by_name.get(dependency)
            if other is None:
                if dependency in absent and not entry.get("carried"):
                    problems.append(f"{name} (fresh) compiles {where}, but {dependency} waits and has never been "
                                    "sealed — no consumer of this publication receives those sources")
                continue
            against = sealed_from(other)
            if other.get("carried") and not against:
                problems.append(f"{name} compiles {where}, and {dependency} is carried with no recorded commit — "
                                "whether the two match cannot be decided")
                continue
            if includes.same(built, against, prefixes):
                continue
            text = (f"{name} ({'carried at ' + _short(built) if built else 'fresh'}) compiles "
                    f"{where}, but this listing seals {dependency} "
                    f"{'carried at ' + _short(against) if against else 'fresh'} — those files differ, so every "
                    f"adopter declines {name}'s bytes (#2813)")
            was_d, was_p = before.get(name), before.get(dependency)
            was_reads = includes.edges(was_d, name) if was_d else None
            already_split = bool(was_d and was_p and was_reads is not None and includes.resolvable(was_p)
                                 and not includes.same(was_d, was_p, was_reads.get(dependency, ())))
            (notes if already_split else problems).append(text)
    return problems, notes


def _short(sha) -> str:
    return (sha or "an unknown commit")[:10]


def cmd_listing(a: argparse.Namespace) -> int:
    bake, repo = Path(a.bake), Path(a.content)
    state = Path(a.state) if a.state else None
    # 🚨 ONE BUILD PER NAME over the module set this run SEALS — read after withhold and the carry
    # have replaced fresh module packages by sealed ones, which the compose-time verdict cannot see.
    torn = module_build_violations(bake)
    if torn:
        print(f"::error title=Seal refused — the module set would carry one assembly at two builds::{len(torn)} "
              "assembly name(s) reach this module set from module packages sealed at different builds — typically a "
              "carried module package beside a fresh one riding the same module-owned sibling. Every consumer composing "
              "both refuses the set, and a portal loading both runs whichever copy it saw first (MeshWeaver#3732, "
              "Doc/Architecture/ModuleOwnedSiblingsRide). `withhold` carries every rider of a carried name; nothing is "
              "written, and the existing publication stays sealed and intact.")
        for p in torn:
            print(f"::error::{p}")
        return 1
    module_count = len(list((bake / MODULES_DIR).glob("*.module.nupkg"))) if (bake / MODULES_DIR).is_dir() else 0
    # The denominator, on every run: a check over an empty directory must not read as a clean one.
    print(f"module set to seal: {module_count} module package(s), one build per MeshWeaver.* assembly name")
    withheld, never_sealed = settled_waiting(_words(a.withhold), state)
    listing = build_listing(bake, repo, a.commit, withheld, state)
    problems, notes = floor_violations(listing, repo, withheld, never_sealed)
    fresh = [e for e in listing["packages"] if not e.get("carried")]
    carried = [e for e in listing["packages"] if e.get("carried")]
    print(f"sealed per package: {len(listing['packages'])} package(s) — {len(fresh)} at this commit "
          f"{_short(a.commit)}, {len(carried)} carried at their previously sealed build")
    for e in carried:
        print(f"  · {e['package']} {e.get('version') or '?'} carried at {_short(e.get('sourceCommit'))}")
    for n in notes:
        print(f"::warning title=Carried dependent of a never-sealed package::{n}")
    source_problems, source_notes = source_include_violations(listing, repo, state, never_sealed)
    for n in source_notes:
        print(f"::warning title=A carried build that no sealed source set matches::{n} — already so in the sealed "
              "publication; this listing does not make it worse")
    if source_problems:
        print(f"::error title=Seal refused — a carried build would not match the sources sealed beside it::"
              f"{len(source_problems)} package(s) compile sources this listing seals from a different commit. "
              "Every adopter would decline their bytes (a portal compiles them at boot; a dependent's gate that "
              "installs this publication is fatal). `withhold --content` holds the moving side back; nothing is "
              "written, and the existing publication stays sealed and intact.")
        for p in source_problems:
            print(f"::error::{p}")
        return 1
    if problems:
        print(f"::error title=Seal refused — a dependency floor would be unmet::{len(problems)} requirement(s) of a "
              "sealed package are not met by what the SAME listing seals. Sealing them would hand every portal a "
              "module set it must refuse wholesale (Systemorph/MeshWeaver#6192); nothing is written, and the existing "
              "publication stays sealed and intact.")
        for p in problems:
            print(f"::error::{p}")
        return 1
    out = Path(a.out) if a.out else bake / LISTING
    out.write_text(json.dumps(listing, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"listing written: {out} — every in-listing dependency floor is met")
    return 0


# ─── self-test ───────────────────────────────────────────────────────────────────────────────────
def write_module_package(path: Path, assemblies: dict[str, str]) -> None:
    """A module package as the lanes write it: each assembly at the top of meshweaver/modules/."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w") as archive:
        archive.writestr("meshweaver/manifest.json", "{}")
        for name, content in assemblies.items():
            archive.writestr(f"{MODULE_ASSEMBLY_FOLDER}{name}.dll", content)


def own_assembly(module_file: str) -> str:
    """A fixture module package's own entry assembly — one distinct name per package."""
    return "MeshWeaver." + module_file[:-len(".module.nupkg")]


def module_rider_cases(expect) -> None:
    """MeshWeaver.Plugins run 39476483432, reduced: Essentials waits (its NodeTypes compile Store's
    moved sources), so its module package — declaring MeshWeaver.Markdown.Collaboration — is carried
    at the PREVIOUS build, while AI's module package, built fresh, rides the same assembly at THIS
    commit's build. Sealed as it was, every satellite gate composing AI + Essentials refused the set
    (MeshWeaver.SocialMedia#246, MeshWeaver.Manufacturing#84, MeshWeaver.Crm#172)."""
    collab, ai, stripe = "MeshWeaver.Markdown.Collaboration", "MeshWeaver.AI", "MeshWeaver.Payments.Stripe"
    print("a carried module package and a fresh one riding the same assembly (one build per name):")
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        bake, state = root / "bake", root / "state"
        me = str(Path(__file__).resolve())

        def cli(*args: str) -> subprocess.CompletedProcess:
            return subprocess.run([sys.executable, me, *args], capture_output=True, text=True)

        def shape(sealed_modules: list[str]) -> None:
            for d in (bake, state):
                shutil.rmtree(d, ignore_errors=True)
                d.mkdir()
            (bake / "Essentials.zip").write_text("fresh Essentials")
            (bake / "Shop.zip").write_text("fresh Shop")
            write_module_package(bake / MODULES_DIR / "essentials.module.nupkg", {collab: "collab@new"})
            write_module_package(bake / MODULES_DIR / "ai.module.nupkg", {ai: "ai@new", collab: "collab@new"})
            write_module_package(bake / MODULES_DIR / "acp.module.nupkg", {"MeshWeaver.AI.Acp": "acp@new", ai: "ai@new"})
            write_module_package(bake / MODULES_DIR / "stripe.module.nupkg", {stripe: "stripe@new"})
            (state / STATE_PUBLICATION).write_text("sealed\n")
            (state / STATE_BUNDLES).write_text("Essentials.zip\nShop.zip\n")
            (state / STATE_MODULES).write_text("".join(f"{m}\n" for m in sealed_modules))
            (state / STATE_COMMIT).write_text("e" * 40 + "\n")

        def carry_sealed(modules: list[str]) -> None:
            # What carry-forward-bundles.sh puts back: the PREVIOUS publication's bytes, which were one
            # build with each other (collab@old in both Essentials and AI).
            sealed = {"essentials.module.nupkg": {collab: "collab@old"},
                      "ai.module.nupkg": {ai: "ai@old", collab: "collab@old"},
                      "acp.module.nupkg": {"MeshWeaver.AI.Acp": "acp@old", ai: "ai@old"}}
            for m in modules:
                write_module_package(bake / MODULES_DIR / m, sealed[m])
            if not (bake / "Essentials.zip").exists():
                (bake / "Essentials.zip").write_text("sealed Essentials")

        everything = ["acp.module.nupkg", "ai.module.nupkg", "essentials.module.nupkg", "stripe.module.nupkg"]

        # The incident, as the pre-fix withhold left it: Essentials carried, AI fresh.
        shape(everything)
        (bake / MODULES_DIR / "essentials.module.nupkg").unlink()
        carry_sealed(["essentials.module.nupkg"])
        torn = module_build_violations(bake)
        expect("NEGATIVE CONTROL: the set run 39476483432 sealed is named torn — Collaboration at two builds, "
               "the carried Essentials and the fresh AI",
               len(torn) == 1 and torn[0].startswith(collab) and "essentials.module.nupkg" in torn[0]
               and "ai.module.nupkg" in torn[0], str(torn))
        out = cli("listing", "--bake", str(bake), "--content", str(root), "--commit", "f" * 40,
                  "--withhold", "Essentials", "--state", str(state))
        expect("…and `listing` REFUSES to seal it, writing nothing",
               out.returncode == 1 and "one assembly at two builds" in out.stdout and collab in out.stdout
               and not (bake / LISTING).exists(), out.stdout + out.stderr)

        # The fix: withhold holds every rider of a carried name, transitively, and carries it too.
        shape(everything)
        result = withhold(bake, ["Essentials"], state)
        expect("withhold holds AI (it rides Collaboration, which Essentials' carried package seals at the old build)",
               "ai" in result["riders"] and collab in result["riders"]["ai"], str(result["riders"]))
        expect("…and Acp, which rides MeshWeaver.AI — the closure is transitive",
               "acp" in result["riders"] and ai in result["riders"]["acp"], str(result["riders"]))
        expect("…carrying all three module packages from the sealed set",
               sorted(_lines(state / STATE_CARRY_MODULES))
               == ["acp.module.nupkg", "ai.module.nupkg", "essentials.module.nupkg"],
               _read(state / STATE_CARRY_MODULES) or "")
        expect("NEGATIVE CONTROL: Stripe shares no assembly with anything carried and stays fresh",
               (bake / MODULES_DIR / "stripe.module.nupkg").exists() and "stripe" not in result["riders"])
        carry_sealed(["acp.module.nupkg", "ai.module.nupkg", "essentials.module.nupkg"])
        expect("the sealed set is ONE build per name", module_build_violations(bake) == [],
               str(module_build_violations(bake)))

        # A rider the sealed publication never held cannot be carried: it stays out (never sealed),
        # rather than being sealed fresh beside the carried name.
        shape(["essentials.module.nupkg", "stripe.module.nupkg"])
        result = withhold(bake, ["Essentials"], state)
        expect("a rider with no sealed build waits and stays OUT — never sealed fresh beside the carried name",
               "ai" in result["absent"] and not (bake / MODULES_DIR / "ai.module.nupkg").exists()
               and "ai.module.nupkg" not in _lines(state / STATE_CARRY_MODULES), str(result))

        # A mixed-case rider WITHOUT --content: `Shop.zip` beside `shop.module.nupkg`. The package is
        # read off the bundle names (fresh and sealed), so the hold carries its bundle too.
        shape(everything + ["shop.module.nupkg"])
        write_module_package(bake / MODULES_DIR / "shop.module.nupkg", {"MeshWeaver.Shop": "shop@new", collab: "collab@new"})
        (state / STATE_BUNDLES).write_text("Essentials.zip\nShop.zip\n")
        result = withhold(bake, ["Essentials"], state)  # no repo: no content checkout to name packages
        expect("a mixed-case rider (Shop.zip / shop.module.nupkg) without --content is held as `Shop`, "
               "its fresh bundle removed and named for the bundle carry",
               "Shop" in result["riders"] and "shop" not in result["riders"] and not (bake / "Shop.zip").exists()
               and "Shop" in result["carried"]
               and "shop.module.nupkg" in _lines(state / STATE_CARRY_MODULES), str(result))

        # A FIRST publication: nothing is sealed, so nothing is carried and no old build can conflict.
        # Withholding alpha must not take beta — independently ready — with it for sharing an assembly.
        for d in (bake, state):
            shutil.rmtree(d, ignore_errors=True)
            d.mkdir()
        for p in ("Alpha", "Beta"):
            (bake / f"{p}.zip").write_text(f"fresh {p}")
        write_module_package(bake / MODULES_DIR / "alpha.module.nupkg",
                             {"MeshWeaver.Alpha": "alpha@new", "MeshWeaver.Shared": "shared@new"})
        write_module_package(bake / MODULES_DIR / "beta.module.nupkg",
                             {"MeshWeaver.Beta": "beta@new", "MeshWeaver.Shared": "shared@new"})
        (state / STATE_PUBLICATION).write_text("absent\n")
        result = withhold(bake, ["Alpha"], state)
        expect("first publication: withholding alpha leaves beta, which shares MeshWeaver.Shared, fresh — a "
               "module never sealed brings no old build back",
               not result["riders"] and (bake / "Beta.zip").exists()
               and (bake / MODULES_DIR / "beta.module.nupkg").exists() and result["modulesCarried"] == [],
               str(result))

        # Fail closed: a module package that is not an archive cannot be read as "carries nothing".
        shape(everything)
        (bake / MODULES_DIR / "stripe.module.nupkg").write_text("not an archive")
        try:
            module_build_violations(bake)
            refused = None
        except SystemExit as stop:
            refused = str(stop)
        expect("an unreadable module package is REFUSED, never read as carrying nothing",
               refused is not None and "stripe.module.nupkg" in refused, str(refused))


def self_test() -> int:
    failures: list[str] = []

    def expect(label: str, cond: bool, detail: str = "") -> None:
        print(f"  {'✓' if cond else '✗'} {label}" + (f" — {detail}" if detail and not cond else ""))
        if not cond:
            failures.append(label)

    print("the requirement reading is core's (PackageRequirement.Satisfies):")
    expect("a caret is met by a newer minor", satisfies("^1.21.0", "1.23.4") is True)
    expect("…and missed by the next major", satisfies("^1.21.0", "2.0.0") is False)
    expect("a caret on 0.x keeps the minor", satisfies("^0.2.1", "0.3.0") is False)
    expect("a bare version is an EXACT pin", satisfies("1.4.0", "1.9.0") is False)
    expect("a floor below is missed", satisfies("^1.23.0", "1.21.1") is False)
    expect("a prerelease of the ceiling stays under a caret (NuGet order)", satisfies("^1.21.0", "2.0.0-rc1") is True)
    expect("NEGATIVE CONTROL: an unreadable range is None, never met", satisfies("1.x", "1.2.0") is None)
    # Prerelease identifiers compare as NuGetVersionComparer does — the parity cases.
    expect("numeric prerelease identifiers compare as numbers (alpha.10 meets >=alpha.2)",
           satisfies(">=1.0.0-alpha.2", "1.0.0-alpha.10") is True)
    expect("…and the reverse is missed (alpha.2 does not meet >=alpha.10)",
           satisfies(">=1.0.0-alpha.10", "1.0.0-alpha.2") is False)
    expect("ci.3758 is above ci.900 (the comparer's own case)", satisfies(">3.0.0-ci.900", "3.0.0-ci.3758") is True)
    expect("a numeric identifier ranks below an alphanumeric one", satisfies(">=1.0.0-alpha.beta", "1.0.0-alpha.1") is False)
    expect("fewer identifiers rank lower (rc3 < rc3.ci.1)", satisfies(">1.0.0-rc3", "1.0.0-rc3.ci.1") is True)
    expect("a release outranks its prerelease", satisfies(">1.0.0-rc.1", "1.0.0") is True)
    expect("text identifiers compare ordinally", satisfies(">=1.0.0-beta", "1.0.0-alpha") is False)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        repo, bake, state = root / "repo", root / "bake", root / "state"
        for d in (repo, bake, state):
            d.mkdir()

        def git(*args: str) -> str:
            return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True,
                                  text=True).stdout.strip()

        git("init", "-q", "-b", "main")
        git("config", "user.email", "t@t")
        git("config", "user.name", "t")
        names = [f"P{i:02d}" for i in range(78)]

        def write(package: str, version: str, requires: list[str] | None = None) -> None:
            (repo / package).mkdir(exist_ok=True)
            content = {"version": version}
            if requires:
                content["requires"] = requires
            (repo / package / "index.json").write_text(json.dumps({"id": package, "content": content}))
            (repo / package / "manifest.lock").write_text(json.dumps({"version": version}))

        for n in names:
            write(n, "1.0.0")
        git("add", "-A")
        git("commit", "-qm", "the commit the previous seal covered")
        old = git("rev-parse", "HEAD")
        for n in names:
            write(n, "1.1.0")
        git("add", "-A")
        git("commit", "-qm", "the commit this run bakes")
        new = git("rev-parse", "HEAD")

        def bake_all(packages: list[str], modules: list[str] = ()) -> None:
            shutil.rmtree(bake)
            bake.mkdir()
            for p in packages:
                (bake / f"{p}.zip").write_text(f"fresh {p}")
            if modules:
                (bake / MODULES_DIR).mkdir()
                for m in modules:
                    write_module_package(bake / MODULES_DIR / m, {own_assembly(m): f"fresh {m}"})

        def seal_previous(listing: list[dict] | None, bundles: list[str], modules: list[str] = ()) -> None:
            shutil.rmtree(state)
            state.mkdir()
            (state / STATE_PUBLICATION).write_text("sealed\n")
            (state / STATE_BUNDLES).write_text("".join(f"{b}\n" for b in bundles))
            (state / STATE_MODULES).write_text("".join(f"{m}\n" for m in modules))
            (state / STATE_COMMIT).write_text(old + "\n")
            if listing is not None:
                (state / STATE_PACKAGES).write_text(json.dumps({"schema": SCHEMA, "publicationCommit": old,
                                                                 "packages": listing}))

        def carry_forward(packages: list[str]) -> None:
            # What carry-forward-bundles.sh does for the names withhold deleted: the sealed bytes.
            for p in packages:
                if not (bake / f"{p}.zip").exists():
                    (bake / f"{p}.zip").write_text(f"sealed {p}")

        publishable, waiting = names[:41], names[41:]
        print("a run where 41 of 78 packages are publishable:")
        bake_all(names)
        seal_previous([{"package": n, "bundle": f"{n}.zip", "sourceCommit": old, "version": "1.0.0",
                        "requires": [], "carried": False} for n in names], [f"{n}.zip" for n in names])
        result = withhold(bake, waiting, state)
        expect("withhold deletes exactly the 37 waiting packages' fresh bundles",
               sorted(p.stem for p in bake.glob("*.zip")) == publishable)
        expect("…and names all 37 to be carried forward (all were sealed before)",
               result["carried"] == waiting and not result["absent"])
        carry_forward(waiting)
        listing = build_listing(bake, repo, new, waiting, state)
        fresh = [e for e in listing["packages"] if not e["carried"]]
        carried = [e for e in listing["packages"] if e["carried"]]
        expect("the listing names all 78", len(listing["packages"]) == 78)
        expect("41 are sealed fresh, at THIS commit, at this tree's version",
               len(fresh) == 41 and all(e["sourceCommit"] == new and e["version"] == "1.1.0" for e in fresh))
        expect("37 are carried, each at its PREVIOUSLY sealed commit and version",
               len(carried) == 37 and all(e["sourceCommit"] == old and e["version"] == "1.0.0" for e in carried))
        expect("NEGATIVE CONTROL: no waiting package is sealed at this commit",
               not any(e["package"] in waiting and e["sourceCommit"] == new for e in listing["packages"]))
        expect("the publication commit stays this run's (never-seal-backwards reads it)",
               listing["publicationCommit"] == new)
        expect("no floor is violated", floor_violations(listing, repo, waiting)[0] == [])

        print("a carried build keeps its OWN commit across seals (carried twice):")
        seal_previous(listing["packages"], [f"{n}.zip" for n in names])
        bake_all(names)
        withhold(bake, waiting[:5], state)
        carry_forward(waiting[:5])
        again = build_listing(bake, repo, new, waiting[:5], state)
        by = {e["package"]: e for e in again["packages"]}
        expect("a package carried again still names the commit its bytes came from, not the publication before",
               all(by[p]["sourceCommit"] == old and by[p]["carried"] for p in waiting[:5]))
        expect("…and one that became publishable is sealed fresh", by[waiting[5]]["sourceCommit"] == new)

        print("a dependency floor the listing would leave unmet is REFUSED (the #6192 class):")
        write("P00", "1.1.0", ["P50@^1.1.0"])  # fresh P00 needs the NEW P50; P50 waits at 1.0.0
        bake_all(names)
        seal_previous(None, [f"{n}.zip" for n in names])
        withhold(bake, ["P50"], state)
        carry_forward(["P50"])
        bad = build_listing(bake, repo, new, ["P50"], state)
        problems = floor_violations(bad, repo, ["P50"])[0]
        expect("refused, naming the dependent, the requirement and the carried version",
               len(problems) == 1 and "P00" in problems[0] and "P50@^1.1.0" in problems[0] and "1.0.0" in problems[0],
               str(problems))
        write("P00", "1.1.0", ["P50@^1.0.0"])  # the same shape, with a floor the carried build meets
        good = build_listing(bake, repo, new, ["P50"], state)
        expect("NEGATIVE CONTROL: a floor the carried build meets is sealed", floor_violations(good, repo, ["P50"])[0] == [])
        write("P00", "1.1.0", ["P77@^1.0.0"])
        bake_all(names)  # P77 bakes a bundle this time, and the publication never sealed it
        seal_previous(None, [f"{n}.zip" for n in names[:77]])
        withhold(bake, ["P77"], state)  # no --content: no closure, so P00 stays fresh
        never = build_listing(bake, repo, new, ["P77"], state)
        problems = floor_violations(never, repo, *settled_waiting(["P77"], state))[0]
        expect("without the closure, a fresh dependent of a NEVER-sealed waiting package is still refused",
               len(problems) == 1 and "seals NO build" in problems[0], str(problems))
        write("P00", "1.1.0", ["External@^9.0.0"])
        expect("NEGATIVE CONTROL: another repository's package is not this listing's to check",
               floor_violations(build_listing(bake, repo, new, [], None), repo, [])[0] == [])
        write("P00", "1.1.0")

        print("a publication sealed before the listing existed (legacy):")
        bake_all(names)
        seal_previous(None, [f"{n}.zip" for n in names])
        withhold(bake, ["P60"], state)
        carry_forward(["P60"])
        legacy = {e["package"]: e for e in build_listing(bake, repo, new, ["P60"], state)["packages"]}
        expect("a carried package takes source-commit.txt and that commit's committed version",
               legacy["P60"]["sourceCommit"] == old and legacy["P60"]["version"] == "1.0.0" and legacy["P60"]["carried"])

        print("a per-package listing that EXISTS and cannot be read is refused, never read as legacy:")
        for label, text in (("malformed JSON", "{not json"), ("no 'packages' list", '{"schema": "x"}'),
                            ("an entry with no 'package'", '{"packages": [{"bundle": "P60.zip"}]}')):
            (state / STATE_PACKAGES).write_text(text)
            try:
                build_listing(bake, repo, new, ["P60"], state)
                refused = False
            except SystemExit as e:
                refused = "could not be read" in str(e)
            expect(f"{label}: refused while a package waits", refused)
        (state / STATE_PACKAGES).write_text("{not json")
        expect("NEGATIVE CONTROL: with nothing waiting the unreadable prior listing is not read, nothing refused",
               len(build_listing(bake, repo, new, [], state)["packages"]) == len(names))
        (state / STATE_PACKAGES).unlink()

        print("a waiting package that was never sealed stays OUT:")
        bake_all(names)
        seal_previous(None, [f"{n}.zip" for n in names[:70]])
        result = withhold(bake, ["P75"], state)
        expect("it is named absent, not carried", result["absent"] == ["P75"] and not result["carried"])
        expect("…and its fresh bytes are gone, so no listing can seal them", not (bake / "P75.zip").exists())

        print("module packages are carried with their package:")
        bake_all(names[:3], ["p01.module.nupkg", "p02.module.nupkg"])
        seal_previous(None, [f"{n}.zip" for n in names[:3]], ["p01.module.nupkg"])
        result = withhold(bake, ["P01", "P02"], state)
        expect("both fresh module packages are removed",
               not (bake / MODULES_DIR / "p01.module.nupkg").exists() and not (bake / MODULES_DIR / "p02.module.nupkg").exists())
        expect("the one the sealed module set lists is handed to the carry; the never-sealed one is not",
               _lines(state / STATE_CARRY_MODULES) == ["p01.module.nupkg"])

        print("an UNREADABLE publication refuses to carry:")
        (state / STATE_PUBLICATION).write_text("unreadable the pointer could not be followed\n")
        try:
            withhold(bake, ["P00"], state)
            refused = False
        except SystemExit as e:
            refused = "could not be read" in str(e)
        expect("refused, naming why — never a shrink and never a guess", refused)
        (state / STATE_PUBLICATION).unlink()
        try:
            withhold(bake, ["P00"], state)
            refused = False
        except SystemExit:
            refused = True
        expect("NEGATIVE CONTROL: no recorded reading at all refuses too", refused)

        print("the CLI writes the listing and exits RED on a violation:")
        write("P00", "1.1.0", ["P50@^1.1.0"])
        bake_all(names)
        seal_previous(None, [f"{n}.zip" for n in names])
        me = str(Path(__file__).resolve())
        subprocess.run([sys.executable, me, "withhold", "--bake", str(bake), "--withhold", "P50", "--state", str(state)],
                       check=True, capture_output=True)
        carry_forward(["P50"])
        red = subprocess.run([sys.executable, me, "listing", "--bake", str(bake), "--content", str(repo),
                              "--commit", new, "--withhold", "P50", "--state", str(state)], capture_output=True, text=True)
        expect("exit 1 and no listing written", red.returncode == 1 and not (bake / LISTING).exists(), red.stdout)
        write("P00", "1.1.0")
        green = subprocess.run([sys.executable, me, "listing", "--bake", str(bake), "--content", str(repo),
                                "--commit", new, "--withhold", "P50", "--state", str(state)], capture_output=True, text=True)
        written = _json(_read(bake / LISTING))
        expect("exit 0 and the listing names P50 carried",
               green.returncode == 0 and written is not None
               and any(e["package"] == "P50" and e["carried"] for e in written["packages"]), green.stdout + green.stderr)

    print("Plugins run 38527548962: waiting content-only packages (AI, Mail) under carried dependents:")
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        repo, bake, state = root / "repo", root / "bake", root / "state"
        for d in (repo, bake, state):
            d.mkdir()

        def put(package: str, version: str, requires: list[str] | None = None) -> None:
            (repo / package).mkdir(exist_ok=True)
            (repo / package / "index.json").write_text(json.dumps(
                {"id": package, "content": {"version": version, **({"requires": requires} if requires else {})}}))
            (repo / package / "manifest.lock").write_text(json.dumps({"version": version}))

        # AI ships a module package and NO NodeType bundle; Mail ships neither (content only).
        put("AI", "1.27.0")
        put("Mail", "1.0.0")
        put("Hosting", "1.68.0", ["AI@^1.24.0"])
        put("Essentials", "1.14.0", ["AI@^1.0.0", "Mail@^1.0.0"])
        put("Chess", "1.1.0")
        # A carried build is read at the commit it came from, so that commit must exist (an
        # unreadable one is refused — see source_include_violations).
        for args in (("init", "-q", "-b", "main"), ("config", "user.email", "t@t"), ("config", "user.name", "t"),
                     ("add", "-A"), ("commit", "-qm", "the commit the sealed publication came from")):
            subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True)
        sealed_commit = subprocess.run(["git", "-C", str(repo), "rev-parse", "HEAD"], check=True,
                                       capture_output=True, text=True).stdout.strip()

        def shape(sealed_modules: list[str]) -> None:
            for d in (bake, state):
                shutil.rmtree(d)
                d.mkdir()
            for p in ("Hosting", "Essentials", "Chess"):
                (bake / f"{p}.zip").write_text(f"fresh {p}")
            (bake / MODULES_DIR).mkdir()
            write_module_package(bake / MODULES_DIR / "ai.module.nupkg", {"MeshWeaver.AI": "fresh ai"})
            (state / STATE_PUBLICATION).write_text("sealed\n")
            (state / STATE_BUNDLES).write_text("Hosting.zip\nEssentials.zip\nChess.zip\n")
            (state / STATE_MODULES).write_text("".join(f"{m}\n" for m in sealed_modules))
            (state / STATE_COMMIT).write_text(sealed_commit + "\n")
            (state / STATE_PACKAGES).write_text(json.dumps({"schema": SCHEMA, "publicationCommit": sealed_commit, "packages": [
                {"package": "Hosting", "bundle": "Hosting.zip", "sourceCommit": sealed_commit, "version": "1.67.18",
                 "requires": ["AI@^1.24.0"], "carried": False},
                {"package": "Essentials", "bundle": "Essentials.zip", "sourceCommit": sealed_commit, "version": "1.13.8",
                 "requires": ["AI@^1.0.0", "Mail@^1.0.0"], "carried": False},
                {"package": "Chess", "bundle": "Chess.zip", "sourceCommit": sealed_commit, "version": "1.0.0",
                 "requires": [], "carried": False}]}))

        def carry(packages: list[str], modules: list[str] = ()) -> None:
            for p in packages:
                if not (bake / f"{p}.zip").exists():
                    (bake / f"{p}.zip").write_text(f"sealed {p}")
            for m in modules:
                write_module_package(bake / MODULES_DIR / m, {own_assembly(m): f"sealed {m}"})

        me = str(Path(__file__).resolve())

        def cli(*args: str) -> subprocess.CompletedProcess:
            return subprocess.run([sys.executable, me, *args], capture_output=True, text=True)

        waiting = ["AI", "Essentials", "Hosting", "Mail"]
        shape(["ai.module.nupkg"])
        out = cli("withhold", "--bake", str(bake), "--withhold", " ".join(waiting), "--state", str(state),
                  "--content", str(repo))
        expect("withhold succeeds", out.returncode == 0, out.stdout + out.stderr)
        expect("AI and Mail are NOT read as never sealed (they bake no bundle)",
               _lines(state / STATE_NEVER_SEALED) == [], _read(state / STATE_NEVER_SEALED) or "")
        expect("AI's module package is carried; Hosting and Essentials carried at their sealed bundles",
               _lines(state / STATE_CARRY_MODULES) == ["ai.module.nupkg"]
               and not (bake / "Hosting.zip").exists() and not (bake / "Essentials.zip").exists())
        carry(["Hosting", "Essentials"], ["ai.module.nupkg"])
        out = cli("listing", "--bake", str(bake), "--content", str(repo), "--commit", "f" * 40,
                  "--withhold", " ".join(waiting), "--state", str(state))
        written = _json(_read(bake / LISTING))
        by = {e["package"]: e for e in (written or {}).get("packages", [])}
        expect("the seal is GREEN: Hosting and Essentials carried at their sealed build, Chess fresh",
               out.returncode == 0 and by.get("Hosting", {}).get("carried") is True
               and by.get("Hosting", {}).get("version") == "1.67.18" and by.get("Chess", {}).get("carried") is False,
               out.stdout + out.stderr)
        expect("…and the carried build is what the listing SEES (the run's 18 refusals are gone)",
               "requirement(s)" not in out.stdout and "seals NO build" not in out.stdout, out.stdout)
        legacy = floor_violations(build_listing(bake, repo, "f" * 40, waiting, state), repo, waiting, None)[0]
        expect("NEGATIVE CONTROL: the pre-fix reading (no never-sealed record) refuses this very listing",
               any("Hosting" in p and "AI@^1.24.0" in p and "seals NO build" in p for p in legacy)
               and any("Mail@^1.0.0" in p for p in legacy), str(legacy))

        print("a waiting package with bytes but NO sealed build: its dependents wait too, the seal stays green:")
        put("Chess", "1.1.0", ["AI@^1.27.0"])  # a fresh dependent of AI
        shape([])  # AI's module was never sealed
        out = cli("withhold", "--bake", str(bake), "--withhold", "AI", "--state", str(state), "--content", str(repo))
        expect("AI is never sealed (fresh module, none sealed) and named so",
               _lines(state / STATE_NEVER_SEALED) == ["AI"] and "AI: never sealed" in out.stdout, out.stdout)
        expect("Hosting, Essentials and Chess are pulled into waiting, each naming AI as the reason",
               sorted(_lines(state / STATE_WITHHELD)) == ["AI", "Chess", "Essentials", "Hosting"]
               and all(f"{p}: waits too — it requires AI" in out.stdout for p in ("Chess", "Essentials", "Hosting")),
               out.stdout)
        carry(["Hosting", "Essentials", "Chess"])
        out = cli("listing", "--bake", str(bake), "--content", str(repo), "--commit", "f" * 40,
                  "--withhold", "AI", "--state", str(state))
        by = {e["package"]: e for e in (_json(_read(bake / LISTING)) or {}).get("packages", [])}
        expect("GREEN, every dependent at its previously sealed build, a warning names the absent AI",
               out.returncode == 0 and all(by.get(p, {}).get("carried") for p in ("Hosting", "Essentials", "Chess"))
               and "has never been sealed" in out.stdout, out.stdout + out.stderr)
        (bake / LISTING).unlink()
        shape([])
        cli("withhold", "--bake", str(bake), "--withhold", "AI", "--state", str(state))  # no --content: no closure
        carry([])
        out = cli("listing", "--bake", str(bake), "--content", str(repo), "--commit", "f" * 40,
                  "--withhold", "AI", "--state", str(state))
        expect("NEGATIVE CONTROL: without the closure the fresh Chess requiring a never-sealed AI is REFUSED",
               out.returncode == 1 and "Chess (sealed at ffffffffff) requires AI@^1.27.0" in out.stdout
               and not (bake / LISTING).exists(), out.stdout)

        print("the closure is transitive, and a dependent never sealed itself stays out:")
        put("Chess", "1.1.0", ["AI@^1.27.0"])
        put("Games", "1.0.0", ["Chess@^1.0.0"])
        shape([])
        (bake / "Games.zip").write_text("fresh Games")  # Games was never sealed either
        out = cli("withhold", "--bake", str(bake), "--withhold", "AI", "--state", str(state), "--content", str(repo))
        expect("NEGATIVE CONTROL: a dependent of a CARRIED dependent (Games on Chess, sealed before) stays fresh",
               "Games" not in _lines(state / STATE_WITHHELD), out.stdout)
        (state / STATE_BUNDLES).write_text("Hosting.zip\nEssentials.zip\n")  # now Chess was never sealed
        shape_bundles = ["Hosting.zip", "Essentials.zip"]
        for d in (bake,):
            shutil.rmtree(d)
            d.mkdir()
        for p in ("Hosting", "Essentials", "Chess", "Games"):
            (bake / f"{p}.zip").write_text(f"fresh {p}")
        (bake / MODULES_DIR).mkdir()
        write_module_package(bake / MODULES_DIR / "ai.module.nupkg", {"MeshWeaver.AI": "fresh ai"})
        (state / STATE_BUNDLES).write_text("".join(f"{b}\n" for b in shape_bundles))
        out = cli("withhold", "--bake", str(bake), "--withhold", "AI", "--state", str(state), "--content", str(repo))
        expect("Chess (never sealed) waits on AI and stays out; Games waits on Chess and stays out",
               sorted(_lines(state / STATE_NEVER_SEALED)) == ["AI", "Chess", "Games"]
               and "Games: waits too — it requires Chess" in out.stdout
               and not (bake / "Chess.zip").exists() and not (bake / "Games.zip").exists(), out.stdout)

    module_rider_cases(expect)
    source_include_cases(expect)
    source_include_reading_cases(expect)

    if failures:
        print(f"\nseal-packages self-test: {len(failures)} FAILED")
        return 1
    print("\nseal-packages self-test: all green")
    return 0


def source_include_cases(expect) -> None:
    """The `plugins` publication of 2026-10-17: Hosting carried, Store fresh, Hosting's NodeTypes
    compiling Store's sources — and its mirror image and its near misses."""
    print("the query grammar of a cross-package `sources` entry:")
    expect("shared=@P/A/Source reads P/A/Source", _query_target("shared=@Store/Core/Source") == "Store/Core/Source")
    expect("@@P/X reads P/X", _query_target("@@Store/Tools/Source/Clock") == "Store/Tools/Source/Clock")
    expect("namespace:P/A scope:subtree reads P/A", _query_target("namespace:Store/Core scope:subtree") == "Store/Core")
    expect("NEGATIVE CONTROL: a single-segment namespace is package-local",
           _query_target("namespace:Source scope:subtree") is None)

    me = str(Path(__file__).resolve())

    def cli(*args: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, me, *args], capture_output=True, text=True)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        repo, bake, state = root / "repo", root / "bake", root / "state"
        for d in (repo, bake, state):
            d.mkdir()

        def git(*args: str) -> str:
            return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True,
                                  text=True).stdout.strip()

        def put(rel: str, text: str) -> None:
            (repo / rel).parent.mkdir(parents=True, exist_ok=True)
            (repo / rel).write_text(text)

        def commit(message: str) -> str:
            git("add", "-A")
            git("commit", "-qm", message)
            return git("rev-parse", "HEAD")

        git("init", "-q", "-b", "main")
        git("config", "user.email", "t@t")
        git("config", "user.name", "t")
        for package in ("Store", "Hosting", "Chess"):
            put(f"{package}/index.json", json.dumps({"id": package, "content": {"version": "1.0.0"}}))
            put(f"{package}/manifest.lock", json.dumps({"version": "1.0.0"}))
        put("Store/Core/Source/Licensing.cs", "class Licensing {}\n")
        put("Store/Tools/Source/Clock.cs", "class Clock {}\n")
        put("Store/Guide/Source/Notes.cs", "class Notes {}\n")
        put("Hosting/InstanceAction.json", json.dumps({"id": "InstanceAction", "content": {
            "$type": "NodeTypeDefinition",
            "sources": ["namespace:Source scope:subtree", "shared=@Store/Core/Source", "@@Store/Tools/Source/Clock"]}}))
        put("Hosting/InstanceAction/Source/Action.cs", "class Action {}\n")
        put("Chess/Board/Source/Board.cs", "class Board {}\n")
        c1 = commit("the commit both builds in the sealed publication came from")

        includes = SourceIncludes(repo)
        expect("Hosting's NodeTypes read Store/Core/Source and the single node Store/Tools/Source/Clock",
               includes.edges(c1, "Hosting") == {"Store": ("Store/Core/Source", "Store/Tools/Source/Clock")},
               str(includes.edges(c1, "Hosting")))
        expect("…the same read from the checkout itself", includes.edges(WORKTREE, "Hosting") == includes.edges(c1, "Hosting"))
        expect("NEGATIVE CONTROL: Chess compiles nobody's sources", includes.edges(c1, "Chess") == {})

        def shape(previous: dict[str, str], fresh: list[str]) -> None:
            for d in (bake, state):
                shutil.rmtree(d)
                d.mkdir()
            for p in fresh:
                (bake / f"{p}.zip").write_text(f"fresh {p}")
            (state / STATE_PUBLICATION).write_text("sealed\n")
            (state / STATE_BUNDLES).write_text("".join(f"{p}.zip\n" for p in sorted(previous)))
            (state / STATE_MODULES).write_text("")
            (state / STATE_COMMIT).write_text(max(previous.values()) + "\n")
            (state / STATE_PACKAGES).write_text(json.dumps({"schema": SCHEMA, "publicationCommit": c1, "packages": [
                {"package": p, "bundle": f"{p}.zip", "sourceCommit": c, "version": "1.0.0", "requires": [],
                 "carried": False} for p, c in sorted(previous.items())]}))

        def carry(packages: list[str]) -> None:
            for p in packages:
                if not (bake / f"{p}.zip").exists():
                    (bake / f"{p}.zip").write_text(f"sealed {p}")

        def withhold_cli(waiting: str, content: bool = True) -> subprocess.CompletedProcess:
            args = ["withhold", "--bake", str(bake), "--withhold", waiting, "--state", str(state)]
            return cli(*args, *(["--content", str(repo)] if content else []))

        def listing_cli(waiting: str) -> subprocess.CompletedProcess:
            return cli("listing", "--bake", str(bake), "--content", str(repo), "--commit", git("rev-parse", "HEAD"),
                       "--withhold", waiting, "--state", str(state))

        def sealed() -> dict[str, dict]:
            return {e["package"]: e for e in (_json(_read(bake / LISTING)) or {}).get("packages", [])}

        put("Store/Core/Source/Licensing.cs", "class Licensing { int Seats; }\n")
        c2 = commit("Store's included source moves while Hosting's lock waits")

        print("THE INCIDENT — Hosting waits, Store's included source moved, both sealed at one commit before:")
        everyone = {"Store": c1, "Hosting": c1, "Chess": c1}
        shape(everyone, ["Store", "Hosting", "Chess"])
        out = withhold_cli("Hosting")
        expect("withhold holds Store back too, naming Hosting and the moved sources",
               out.returncode == 0 and "Store" in _lines(state / STATE_WITHHELD)
               and "Store: waits too — Hosting waits at" in out.stdout and "Store/Core/Source" in out.stdout
               and not (bake / "Store.zip").exists(), out.stdout + out.stderr)
        expect("…and NEGATIVE CONTROL: the unrelated Chess is still sealed fresh",
               (bake / "Chess.zip").exists() and "Chess" not in _lines(state / STATE_WITHHELD))
        carry(["Hosting", "Store"])
        out = listing_cli("Hosting")
        by = sealed()
        expect("the seal is GREEN with Hosting AND Store carried at the commit Hosting was compiled against",
               out.returncode == 0 and by.get("Hosting", {}).get("sourceCommit") == c1
               and by.get("Store", {}).get("sourceCommit") == c1 and by.get("Store", {}).get("carried") is True
               and by.get("Chess", {}).get("carried") is False, out.stdout + out.stderr)

        print("NEGATIVE CONTROL — the same run without the source rule in withhold is REFUSED by the listing:")
        shape(everyone, ["Store", "Hosting", "Chess"])
        withhold_cli("Hosting", content=False)
        carry(["Hosting"])
        out = listing_cli("Hosting")
        expect("refused, naming Hosting, the included path and Store sealed fresh — nothing written",
               out.returncode == 1 and "Hosting (carried at" in out.stdout and "Store/Core/Source" in out.stdout
               and "this listing seals Store fresh" in out.stdout and not (bake / LISTING).exists(),
               out.stdout + out.stderr)

        print("a Store change OUTSIDE what Hosting compiles holds nothing back:")
        put("Store/Guide/Source/Notes.cs", "class Notes { string Text; }\n")
        c3 = commit("Store moves a source nobody else compiles")
        shape({"Store": c2, "Hosting": c2, "Chess": c2}, ["Store", "Hosting", "Chess"])
        out = withhold_cli("Hosting")
        expect("Hosting waits at c2, Store moved only Store/Guide/Source since: Store is sealed fresh",
               out.returncode == 0 and _lines(state / STATE_WITHHELD) == ["Hosting"] and (bake / "Store.zip").exists()
               and "A carried build" not in out.stdout, out.stdout + out.stderr)
        carry(["Hosting"])
        out = listing_cli("Hosting")
        expect("…and the listing is GREEN with no source warning",
               out.returncode == 0 and "source set matches" not in out.stdout, out.stdout + out.stderr)
        expect("Store/Guide/Source moved and Hosting's inputs did not, between c2 and c3",
               includes.same(c2, c3, ("Store/Core/Source", "Store/Tools/Source/Clock"))
               and not includes.same(c2, c3, ("Store/Guide/Source",)))
        expect("a single included NODE (Store/Tools/Source/Clock → Clock.cs) is compared, not only folders",
               not includes.same(c1, WORKTREE, ("Store/Core/Source",))
               and includes.same(c1, WORKTREE, ("Store/Tools/Source/Clock",)))
        put("Store/Tools/Source/Clock.cs", "class Clock { long Ticks; }\n")
        expect("…and an edit to that one node file IS a difference",
               not includes.same(c3, WORKTREE, ("Store/Tools/Source/Clock",)))
        git("checkout", "-q", "--", "Store/Tools/Source/Clock.cs")

        print("ALREADY BROKEN in the sealed publication (Hosting at c1, Store at c2) — warned, never refused:")
        shape({"Store": c2, "Hosting": c1, "Chess": c1}, ["Store", "Hosting", "Chess"])
        out = withhold_cli("Hosting")
        expect("Store is NOT held (its sealed build does not match Hosting either) and the pair is named",
               out.returncode == 0 and "Store" not in _lines(state / STATE_WITHHELD)
               and "A carried build that no sealed source set matches" in out.stdout, out.stdout + out.stderr)
        carry(["Hosting"])
        out = listing_cli("Hosting")
        expect("the listing is GREEN with a warning — refusing would only keep that same publication sealed",
               out.returncode == 0 and "already so in the sealed publication" in out.stdout, out.stdout + out.stderr)

        print("the MIRROR — Store waits, Hosting fresh compiles Store's moved sources:")
        shape(everyone, ["Store", "Hosting", "Chess"])
        out = withhold_cli("Store")
        expect("Hosting waits too, carried at the build compiled against the Store that is carried",
               out.returncode == 0 and "Hosting" in _lines(state / STATE_WITHHELD)
               and "Hosting: waits too — its NodeTypes compile Store/Core/Source" in out.stdout,
               out.stdout + out.stderr)
        carry(["Store", "Hosting"])
        out = listing_cli("Store")
        expect("GREEN, both carried at c1",
               out.returncode == 0 and sealed().get("Hosting", {}).get("sourceCommit") == c1, out.stdout + out.stderr)

        print("a fresh Hosting that was NEVER sealed, over a carried Store it no longer matches, stays out:")
        shape({"Store": c1, "Chess": c1}, ["Store", "Hosting", "Chess"])
        out = withhold_cli("Store")
        expect("Hosting is held and, never sealed, left out of the listing",
               out.returncode == 0 and "Hosting" in _lines(state / STATE_NEVER_SEALED)
               and not (bake / "Hosting.zip").exists(), out.stdout + out.stderr)

        print("a provider held OUT (never sealed) holds a fresh dependent of its sources too:")
        shape({"Hosting": c3, "Chess": c3}, ["Store", "Hosting", "Chess"])
        out = withhold_cli("Store")
        expect("Store waits and was never sealed, so the fresh Hosting compiling it waits too (carried)",
               out.returncode == 0 and "Store" in _lines(state / STATE_NEVER_SEALED)
               and "Hosting" in _lines(state / STATE_WITHHELD) and not (bake / "Hosting.zip").exists()
               and "Hosting: waits too — its NodeTypes compile" in out.stdout, out.stdout + out.stderr)
        shape({"Hosting": c3, "Chess": c3}, ["Store", "Hosting", "Chess"])
        withhold_cli("Store", content=False)
        out = listing_cli("Store")
        expect("…and the listing REFUSES a fresh Hosting over a never-sealed Store (no withhold rule)",
               out.returncode == 1 and "waits and has never been sealed" in out.stdout
               and not (bake / LISTING).exists(), out.stdout + out.stderr)

        print("a NEW provider under a carried dependent stays out with it:")
        shape({"Hosting": c1, "Chess": c1}, ["Store", "Hosting", "Chess"])
        out = withhold_cli("Hosting")
        expect("Store (never sealed, its sources moved since Hosting's build) is held out",
               out.returncode == 0 and "Store" in _lines(state / STATE_NEVER_SEALED)
               and "Store: waits too — Hosting waits at" in out.stdout, out.stdout + out.stderr)
        carry(["Hosting"])
        out = listing_cli("Hosting")
        expect("…and the listing is GREEN", out.returncode == 0, out.stdout + out.stderr)

        print("a mismatch whose PREVIOUS state cannot be read is refused, never waved through:")
        shape({"Store": c1, "Hosting": "0" * 40, "Chess": c1}, ["Store", "Hosting", "Chess"])
        withhold_cli("Store", content=False)
        carry(["Store"])
        out = listing_cli("Store")
        expect("fresh Hosting vs Store carried at c1, Hosting's previous commit unreadable: REFUSED",
               out.returncode == 1 and "Hosting (fresh) compiles" in out.stdout and not (bake / LISTING).exists(),
               out.stdout + out.stderr)

        print("what a build compiles, read as the compiler reads it:")
        expect("a BARE path behind a name is a path reference (shared=Store/Core/Source)",
               _query_target("shared=Store/Core/Source") == "Store/Core/Source")
        expect("whitespace around `=` and after `@` is trimmed (shared= @Store/Core/Source)",
               _query_target("shared= @Store/Core/Source") == "Store/Core/Source"
               and _query_target("shared = @ Store/Core/Source") == "Store/Core/Source")
        def unanchored(entry: str) -> bool:
            try:
                _query_target(entry)
            except UnanchoredQuery:
                return True
            return False

        expect("NEGATIVE CONTROL: a quoted term and a multi-term entry are queries, not paths — and, "
               "anchored nowhere, they are refused rather than read as package-local",
               unanchored('"Store/Core/Source"') and unanchored("Store/Core Source"))
        expect("NEGATIVE CONTROL: $self is the NodeType's own path", _query_target("$self/Source") is None)
        put("Hosting/Probe.json", json.dumps({"id": "Probe", "content": {
            "$type": "NodeTypeDefinition",
            "sources": ["shared=Store/Bare/Source", "shared= @Store/Spaced/Source"],
            "tests": ["@Store/Core/Test"]}}))
        put("Hosting/Probe/Source/Uses.cs", "// @@Store/Shared/Source/Util\nclass Uses {}\n")
        put("Store/Shared/Source/Util.cs", "// @@Chess/Board/Source/Board\nclass Util {}\n")
        for folder in ("Bare/Source", "Spaced/Source", "Core/Test"):
            put(f"Store/{folder}/File.cs", "class File {}\n")
        c4 = commit("Hosting reads Store through tests, bare paths and @@ includes")
        reads = SourceIncludes(repo).edges(c4, "Hosting") or {}
        store = set(reads.get("Store", ()))
        expect("bare and spaced `sources` entries are read",
               {"Store/Bare/Source", "Store/Spaced/Source"} <= store, str(reads))
        expect("`tests` entries are read (NodeSet.ResolveSources compiles the union)",
               "Store/Core/Test" in store, str(reads))
        expect("an @@ include in the package's own Code node is read", "Store/Shared/Source/Util" in store, str(reads))
        expect("…and TRANSITIVELY: what that included file @@-includes from Chess",
               reads.get("Chess") == ("Chess/Board/Source/Board",), str(reads))
        expect("the checkout reads the same", SourceIncludes(repo).edges(WORKTREE, "Hosting") == reads)
        put("Store/Shared/Source/Util.cs", "// @@Chess/Board/Source/Board\nclass Util { int Changed; }\n")
        expect("…and a change to the @@-included file IS a difference",
               not SourceIncludes(repo).same(c4, WORKTREE, reads["Store"]))


def source_include_reading_cases(expect) -> None:
    """Reading a build's inputs as leniently as the compiler and the node loader do — and refusing,
    never skipping, what still cannot be read."""
    print("selectors and paths match case-insensitively, as NodeSetQuery matches them:")
    expect("`Namespace:` is a namespace selector", _query_target("Namespace:Store/Core scope:subtree") == "Store/Core")
    expect("`PATH:` is a path selector", _query_target("PATH:Store/Core/Source") == "Store/Core/Source")

    def refused(action) -> str | None:
        try:
            action()
        except SystemExit as stop:
            return str(stop)
        return None

    with tempfile.TemporaryDirectory() as tmp:
        repo = Path(tmp) / "repo"
        repo.mkdir()

        def git(*args: str) -> str:
            return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True,
                                  text=True).stdout.strip()

        def put(rel: str, text: str) -> None:
            (repo / rel).parent.mkdir(parents=True, exist_ok=True)
            (repo / rel).write_text(text, encoding="utf-8")

        def commit(message: str) -> str:
            git("add", "-A")
            git("commit", "-qm", message)
            return git("rev-parse", "HEAD")

        git("init", "-q", "-b", "main")
        git("config", "user.email", "t@t")
        git("config", "user.name", "t")
        for package in ("Store", "Hosting", "Chess"):
            put(f"{package}/index.json", json.dumps({"id": package, "content": {"version": "1.0.0"}}))
        put("Store/Core/Source/Licensing.cs", "class Licensing {}\n")
        put("Store/Tools/Source/Clock.cs", "class Clock {}\n")
        put("Hosting/Lower.json", json.dumps({"id": "Lower", "content": {
            "$type": "NodeTypeDefinition", "sources": ["@store/core/source", "@@store/tools/source/clock"]}}))
        put("Chess/Lenient.json", "﻿{\n  // a node file the loader accepts\n  \"id\": \"Lenient\",\n"
            "  \"content\": { \"$type\": \"NodeTypeDefinition\", /* both */\n"
            "    \"sources\": [\"@Store/Core/Source\",], },\n}\n")
        c1 = commit("lower-cased and lenient NodeType definitions")
        includes = SourceIncludes(repo)

        reads = includes.edges(c1, "Hosting") or {}
        expect("`@store/core/source` is Store's Store/Core/Source, recorded in its ON-DISK casing",
               reads == {"Store": ("Store/Core/Source", "Store/Tools/Source/Clock")}, str(reads))
        put("Store/Core/Source/Licensing.cs", "class Licensing { int Seats; }\n")
        expect("…and a change under it IS a difference",
               not includes.same(c1, WORKTREE, reads.get("Store", ())))
        expect("…also when the authored casing is what is compared",
               not includes.same(c1, WORKTREE, ("store/core/source",)))
        git("checkout", "-q", "--", ".")
        expect("NEGATIVE CONTROL: unchanged, it is the same",  # a fresh reader: answers are cached per run
               SourceIncludes(repo).same(c1, WORKTREE, reads.get("Store", ())))

        lenient = includes.edges(c1, "Chess") or {}
        expect("a NodeType file with a BOM, comments and trailing commas is read, not skipped",
               lenient == {"Store": ("Store/Core/Source",)}, str(lenient))

        put("Chess/Broken.json", '{"id": "Broken", "content": {"$type": "NodeTypeDefinition", "sources": [')
        c2 = commit("a NodeType file nothing can parse")
        stop = refused(lambda: SourceIncludes(repo).edges(c2, "Chess"))
        expect("a NodeType file that still does not parse REFUSES", stop is not None and "Broken.json" in stop, str(stop))
        git("rm", "-q", "Chess/Broken.json")
        put("Hosting/Nowhere.json", json.dumps({"id": "Nowhere", "content": {
            "$type": "NodeTypeDefinition", "sources": ["@Store/Nowhere/Source"]}}))
        c3 = commit("a query into Store that names nothing there")
        stop = refused(lambda: SourceIncludes(repo).edges(c3, "Hosting"))
        expect("a path INTO a package that does not resolve REFUSES", stop is not None and "Store/Nowhere" in stop,
               str(stop))
        expect("NEGATIVE CONTROL: a path whose first segment is no package here is not an edge",
               SourceIncludes(repo).resolve(c1, "CodeNodePath/Whatever") is None)

        print("a carried build whose commit cannot be read is refused, whatever the checkout shows:")
        listing = {"schema": SCHEMA, "publicationCommit": c1, "packages": [
            {"package": "Chess", "bundle": "Chess.zip", "sourceCommit": "0" * 40, "version": "1.0.0",
             "requires": [], "carried": True},
            {"package": "Store", "bundle": "Store.zip", "sourceCommit": c1, "version": "1.0.0",
             "requires": [], "carried": False}]}
        git("rm", "-q", "Chess/Lenient.json")  # HEAD no longer shows Chess compiling anything of Store
        problems, _ = source_include_violations(listing, repo, None)
        expect("REFUSED although the checkout shows no include of Chess's any more",
               any("Chess is carried at 0000000000" in p for p in problems), str(problems))

        print("only what an assembly compiles counts, and node keys match as the loader matches them:")
        expect("a single-segment `path:` is ABSOLUTE (`path:Store scope:descendants` reads Store)",
               _query_target("path:Store scope:descendants") == "Store")
        expect("…while a single-segment `namespace:` stays package-local",
               _query_target("namespace:Store scope:descendants") is None)
        expect("a trailing comma followed by a comment parses",
               _lenient_json('{"sources": ["@Store/Core/Source", /* inputs */], // done\n}')
               == {"sources": ["@Store/Core/Source"]})
        put("Chess/Caps.json", json.dumps({"Id": "Caps", "Content": {
            "$type": "NodeTypeDefinition", "Sources": ["@Store/Core/Source"]}}))
        put("Chess/Doc.json", json.dumps({"id": "Doc", "content": {
            "$type": "MarkdownContent", "content": "See @@Store/Nowhere/Page for the pattern."}}))
        put("Chess/Cell.json", json.dumps({"id": "Cell", "content": {
            "$type": "CodeConfiguration", "isExecutable": True, "code": "// @@Store/Nowhere/Cell"}}))
        put("Chess/Shared.json", json.dumps({"id": "Shared", "content": {
            "$type": "CodeConfiguration", "Code": "// @@Store/Tools/Source/Clock\nclass Shared {}"}}))
        git("rm", "-q", "--cached", "--ignore-unmatch", "Hosting/Nowhere.json")
        (repo / "Hosting/Nowhere.json").unlink()
        c5 = commit("PascalCase keys, a Markdown node and an executable cell naming @@ paths")
        stop = refused(lambda: SourceIncludes(repo).edges(c5, "Chess"))
        expect("an @@ in a Markdown node or an executable cell does not abort", stop is None, str(stop))
        chess = SourceIncludes(repo).edges(c5, "Chess") if stop is None else {}
        expect("`Content` / `Sources` read case-insensitively, and the compiled Code node's @@ counts — "
               "the Markdown and executable ones do not",
               chess == {"Store": ("Store/Core/Source", "Store/Tools/Source/Clock")}, str(chess))

        print("a NON-ASCII path segment is listed with -z, resolved and compared — never C-quoted away:")
        put("Store/Côté/Source/Grüße.cs", "class Gruesse {}\n")
        put("Chess/Accents.json", json.dumps({"id": "Accents", "content": {
            "$type": "NodeTypeDefinition", "sources": ["@store/côté/source", "@@Store/Côté/Source/grüße"]}}))
        c6 = commit("a source folder and a node with non-ASCII names")
        accents = (SourceIncludes(repo).edges(c6, "Chess") or {}).get("Store", ())
        expect("`@store/côté/source` resolves to Store/Côté/Source and the node to Store/Côté/Source/Grüße",
               "Store/Côté/Source" in accents and "Store/Côté/Source/Grüße" in accents, str(accents))
        expect("NEGATIVE CONTROL: unchanged, it is the same", SourceIncludes(repo).same(c6, WORKTREE, accents))
        put("Store/Côté/Source/Grüße.cs", "class Gruesse { int Changed; }\n")
        expect("…and a change to the non-ASCII file IS a difference",
               not SourceIncludes(repo).same(c6, WORKTREE, accents))

        print("a query anchored nowhere names no packages — so it refuses:")
        put("Chess/Unanchored.json", json.dumps({"id": "Unanchored", "content": {
            "$type": "NodeTypeDefinition", "sources": ["nodeType:Code"]}}))
        c7 = commit("a NodeType compiling every Code node of the mesh")
        stop = refused(lambda: SourceIncludes(repo).edges(c7, "Chess"))
        expect("`nodeType:Code` REFUSES, naming the NodeType file and the query",
               stop is not None and "Chess/Unanchored.json" in stop and "'nodeType:Code'" in stop, str(stop))
        git("rm", "-q", "Chess/Unanchored.json")

        print("an explicit @@ target is walked even when it is an EXECUTABLE cell:")
        put("Hosting/Chain/Source/Root.cs", "// @@Store/Shared/Outer\nclass Root {}\n")
        put("Store/Shared/Outer.json", json.dumps({"id": "Outer", "content": {
            "$type": "CodeConfiguration", "isExecutable": True, "code": "// @@Chess/Shared/Inner"}}))
        put("Chess/Shared/Inner.cs", "class Inner {}\n")
        c8 = commit("Hosting → @@Store/Shared/Outer (executable) → @@Chess/Shared/Inner")
        chain = SourceIncludes(repo).edges(c8, "Hosting") or {}
        expect("Store/Shared/Outer AND, through it, Chess/Shared/Inner are recorded",
               "Store/Shared/Outer" in chain.get("Store", ()) and chain.get("Chess") == ("Chess/Shared/Inner",),
               str(chain))

        print("an @@ target stored as `<path>/index.<ext>` (a node with children) is read too:")
        for ext, body in ((".cs", "// @@Chess/Shared/ViaCs\nclass Outer2 {}\n"),
                          (".json", json.dumps({"id": "Outer3", "content": {
                              "$type": "CodeConfiguration", "code": "// @@Chess/Shared/ViaJson"}}))):
            name = "Outer2" if ext == ".cs" else "Outer3"
            put(f"Store/Shared/{name}/index{ext}", body)
            put(f"Store/Shared/{name}/Child.cs", "class Child {}\n")
        put("Chess/Shared/ViaCs.cs", "class ViaCs {}\n")
        put("Chess/Shared/ViaJson.cs", "class ViaJson {}\n")
        put("Hosting/Chain/Source/Index.cs", "// @@Store/Shared/Outer2\n// @@Store/Shared/Outer3\nclass Index {}\n")
        c9 = commit("Hosting → @@ two index-backed Store nodes → @@ Chess")
        for where, at in (("at a commit", c9), ("in the checkout", WORKTREE)):
            via = set((SourceIncludes(repo).edges(at, "Hosting") or {}).get("Chess", ()))
            expect(f"`Outer2/index.cs` is followed into Chess ({where})", "Chess/Shared/ViaCs" in via, str(via))
            expect(f"`Outer3/index.json` is followed into Chess ({where})", "Chess/Shared/ViaJson" in via, str(via))
        put("Hosting/Chain/Source/Missing.cs", "// @@Store/Shared/NoSuchNode\nclass Missing {}\n")
        (repo / "Store/Shared/NoSuchNode").mkdir(parents=True)
        (repo / "Store/Shared/NoSuchNode/Notes.txt").write_text("a folder, but no node file\n")
        c10 = commit("an @@ target that is a folder with no node file")
        stop = refused(lambda: SourceIncludes(repo).edges(c10, "Hosting"))
        expect("an @@ target stored in none of NodeFileMapper's representations REFUSES",
               stop is not None and "Store/Shared/NoSuchNode" in stop, str(stop))


def main(argv: list[str]) -> int:
    if argv[:1] == ["--self-test"]:
        return self_test()
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="cmd", required=True)
    w = sub.add_parser("withhold")
    w.add_argument("--bake", required=True)
    w.add_argument("--withhold", default="")
    w.add_argument("--state", required=True)
    w.add_argument("--content", help="the content checkout: a fresh package requiring a never-sealed one waits too")
    w.add_argument("--github-output")
    l = sub.add_parser("listing")
    l.add_argument("--bake", required=True)
    l.add_argument("--content", required=True)
    l.add_argument("--commit", required=True)
    l.add_argument("--withhold", default="")
    l.add_argument("--state")
    l.add_argument("--out")
    a = parser.parse_args(argv)
    return cmd_withhold(a) if a.cmd == "withhold" else cmd_listing(a)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
