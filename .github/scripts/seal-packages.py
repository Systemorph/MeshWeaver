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
             The effective waiting set and the never-sealed set are written to the state directory,
             and `listing` reads them — the listing and its floor check see what withhold decided.
             (Plugins run 38527548962: 26 waiting content-only packages, AI among them, were read as
             "never sealed", and every carried dependent's `requires AI@…` refused the seal.)

  listing    runs after the carry-forward. It writes the per-package listing and REFUSES a seal in
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


def _query_target(entry: str) -> str | None:
    """The node-path prefix a `sources` entry reads in ANOTHER package, None when package-local or
    unreadable. The grammar of MeshWeaver.Plugins' scripts/affected-modules.py (`first_segment_of` /
    `query_path`): an optional `name=` prefix, `@`/`@@` shorthand, else the first `namespace:` /
    `path:` token — whose single-segment form is package-local."""
    value = entry
    eq = value.find("=")
    if eq > 0 and " " not in value[:eq] and ":" not in value[:eq]:
        value = value[eq + 1:]
    if value.startswith("@@"):
        target = value[2:]
    elif value.startswith("@"):
        target = value[1:]
    else:
        target = None
        for token in value.split(" "):
            for key in ("namespace:", "path:"):
                if token.startswith(key):
                    candidate = token[len(key):].strip("/")
                    target = candidate if "/" in candidate else None
                    break
            else:
                continue
            break
    if not target:
        return None
    return target.split(" ", 1)[0].strip("/") or None


def _cross_package_sources(documents: list[str], package: str) -> dict[str, tuple[str, ...]]:
    """{other package: (path prefixes it reads)} over the NodeType definitions in `documents`."""
    found: dict[str, set[str]] = {}
    for text in documents:
        if "NodeTypeDefinition" not in text:
            continue  # the cheap filter: most JSON in a package is instances, not NodeTypes
        doc = _json(text)
        content = (doc or {}).get("content")
        if not isinstance(content, dict) or content.get("$type") != "NodeTypeDefinition":
            continue
        for entry in content.get("sources") or []:
            target = _query_target(entry) if isinstance(entry, str) else None
            other = target.split("/", 1)[0] if target else None
            if other and other != package:
                found.setdefault(other, set()).add(target)
    return {p: tuple(sorted(v)) for p, v in sorted(found.items())}


class SourceIncludes:
    """Reads, per (commit, package), which other packages' sources the package's NodeTypes compile,
    and whether those files are identical between two commits. `WORKTREE` stands for the checkout."""

    def __init__(self, repo: Path):
        self.repo = repo
        self._edges: dict[tuple[str | None, str], dict[str, tuple[str, ...]] | None] = {}
        self._known: dict[str, bool] = {}
        self._same: dict[tuple, bool] = {}

    def _git(self, *args: str, stdin: str | None = None) -> subprocess.CompletedProcess:
        return subprocess.run(["git", "-C", str(self.repo), *args], capture_output=True, text=True, input=stdin)

    def resolvable(self, commit: str | None) -> bool:
        if commit is WORKTREE:
            return True
        if commit not in self._known:
            self._known[commit] = self._git("cat-file", "-e", f"{commit}^{{commit}}").returncode == 0
        return self._known[commit]

    def edges(self, commit: str | None, package: str) -> dict[str, tuple[str, ...]] | None:
        """None when `commit` is not in the checkout (shallow, or a fixture commit)."""
        key = (commit, package)
        if key in self._edges:
            return self._edges[key]
        if commit is WORKTREE:
            root = self.repo / package
            docs = [t for t in (_read(p) for p in sorted(root.rglob("*.json"))) if t] if root.is_dir() else []
            value = _cross_package_sources(docs, package)
        elif not self.resolvable(commit):
            value = None
        else:
            listed = self._git("grep", "-l", "-F", "NodeTypeDefinition", commit, "--", f"{package}/")
            if listed.returncode not in (0, 1):  # 1 = no match
                raise SystemExit(f"✗ seal-packages: could not read {package}'s NodeType definitions at "
                                 f"{_short(commit)} ({listed.stderr.strip()})")
            paths = [line.split(":", 1)[1] for line in listed.stdout.splitlines()
                     if ":" in line and line.endswith(".json")]
            docs = [t for t in (_git_show(self.repo, commit, p) for p in paths) if t]
            value = _cross_package_sources(docs, package)
        self._edges[key] = value
        return value

    def same(self, a: str | None, b: str | None, prefixes: tuple[str, ...]) -> bool:
        """Whether every file under `prefixes` (a folder, or a node file `<prefix>.<ext>`) is identical
        at `a` and `b`. An unreadable answer is a refusal, never a guess."""
        if a == b or not prefixes:
            return True
        key = (a, b, prefixes)
        if key in self._same:
            return self._same[key]
        revs = [c for c in (a, b) if c is not WORKTREE]
        specs = [s for p in prefixes for s in (p, f":(glob){p}.*")]
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

    def hold(package: str) -> None:
        bundle = bake / f"{package}.zip"
        fresh_bundle = bundle.exists()
        if fresh_bundle:
            bundle.unlink()
        module = f"{package.lower()}.module.nupkg"
        fresh = bake / MODULES_DIR / module
        fresh_module = fresh.exists()
        if fresh_module:
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

    waiting = list(dict.fromkeys(withheld))
    for package in waiting:
        hold(package)
    sourced: dict[str, str] = {}  # package → why a source include made it wait
    source_notes: dict[tuple[str, str], str] = {}
    if repo is not None:
        by_lower = {d.name.lower(): d.name for d in repo.iterdir() if d.is_dir()} if repo.is_dir() else {}
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
                    if dependency not in shipped:
                        continue
                    against = at(dependency)
                    if against is False or includes.same(built, against, prefixes):
                        continue
                    where = ", ".join(prefixes)
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

        while requires_step() or source_step():
            pass
    (state / STATE_CARRY_MODULES).write_text("".join(f"{m}\n" for m in modules_carried), encoding="utf-8")
    (state / STATE_WITHHELD).write_text("".join(f"{p}\n" for p in waiting), encoding="utf-8")
    (state / STATE_NEVER_SEALED).write_text("".join(f"{p}\n" for p in absent), encoding="utf-8")
    return {"carried": carried, "absent": absent, "bundleless": bundleless, "modulesCarried": modules_carried,
            "status": status, "waiting": waiting, "pulled": pulled, "sourced": sourced,
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
    for m in result["modulesCarried"]:
        print(f"  · module {m}: carried forward from the sealed module set")
    for note in result["sourceNotes"]:
        print(f"::warning title=A carried build that no sealed source set matches::{note}")
    print(f"::notice title=Sealed per package::{len(result['carried'])} waiting package(s) carried forward at their "
          f"previously sealed build, {len(result['absent'])} never sealed and left out, {len(result['bundleless'])} "
          f"bake no bundle, {len(result['pulled'])} wait because a dependency was never sealed, "
          f"{len(result['sourced'])} wait so that the sources a carried build compiled travel with it; every other "
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


def source_include_violations(listing: dict, repo: Path, state: Path | None) -> tuple[list[str], list[str]]:
    """(refusals, notes) for the source-include rule (see SourceIncludes): a pair whose included files
    differ between the commits this listing seals them from is REFUSED when the sealed publication held
    the same two packages consistently — this listing would introduce an unadoptable build — and only
    noted when it was already so there."""
    includes = SourceIncludes(repo)
    listed = set(_lines(state / STATE_BUNDLES)) if state is not None else set()
    before = _sealed_commits(state, listed) if state is not None else {}
    by_name = {e["package"]: e for e in listing["packages"]}

    def sealed_from(entry: dict):
        return entry.get("sourceCommit") if entry.get("carried") else WORKTREE

    problems: list[str] = []
    notes: list[str] = []
    for name, entry in sorted(by_name.items()):
        built = sealed_from(entry)
        if entry.get("carried") and not built:
            continue  # a legacy carried build with no recorded commit: nothing to compare against
        reads = includes.edges(built, name)
        if reads is None:
            if includes.edges(WORKTREE, name):
                problems.append(f"{name} is carried at {_short(built)}, which this checkout does not hold, and its "
                                "NodeTypes compile other packages' sources — whether its bytes match the sources "
                                "this listing seals cannot be decided")
            continue
        for dependency, prefixes in reads.items():
            other = by_name.get(dependency)
            if other is None:
                continue
            against = sealed_from(other)
            if other.get("carried") and not against:
                continue
            if includes.same(built, against, prefixes):
                continue
            text = (f"{name} ({'carried at ' + _short(built) if built else 'fresh'}) compiles "
                    f"{', '.join(prefixes)}, but this listing seals {dependency} "
                    f"{'carried at ' + _short(against) if against else 'fresh'} — those files differ, so every "
                    f"adopter declines {name}'s bytes (#2813)")
            was_d, was_p = before.get(name), before.get(dependency)
            was_reads = includes.edges(was_d, name) if was_d else None
            introduced = (was_d and was_p and was_reads is not None and includes.resolvable(was_p)
                          and includes.same(was_d, was_p, was_reads.get(dependency, ())))
            (problems if introduced else notes).append(text)
    return problems, notes


def _short(sha) -> str:
    return (sha or "an unknown commit")[:10]


def cmd_listing(a: argparse.Namespace) -> int:
    bake, repo = Path(a.bake), Path(a.content)
    state = Path(a.state) if a.state else None
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
    source_problems, source_notes = source_include_violations(listing, repo, state)
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
                    (bake / MODULES_DIR / m).write_text(f"fresh {m}")

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

        def shape(sealed_modules: list[str]) -> None:
            for d in (bake, state):
                shutil.rmtree(d)
                d.mkdir()
            for p in ("Hosting", "Essentials", "Chess"):
                (bake / f"{p}.zip").write_text(f"fresh {p}")
            (bake / MODULES_DIR).mkdir()
            (bake / MODULES_DIR / "ai.module.nupkg").write_text("fresh ai")
            (state / STATE_PUBLICATION).write_text("sealed\n")
            (state / STATE_BUNDLES).write_text("Hosting.zip\nEssentials.zip\nChess.zip\n")
            (state / STATE_MODULES).write_text("".join(f"{m}\n" for m in sealed_modules))
            (state / STATE_COMMIT).write_text("e" * 40 + "\n")
            (state / STATE_PACKAGES).write_text(json.dumps({"schema": SCHEMA, "publicationCommit": "e" * 40, "packages": [
                {"package": "Hosting", "bundle": "Hosting.zip", "sourceCommit": "e" * 40, "version": "1.67.18",
                 "requires": ["AI@^1.24.0"], "carried": False},
                {"package": "Essentials", "bundle": "Essentials.zip", "sourceCommit": "e" * 40, "version": "1.13.8",
                 "requires": ["AI@^1.0.0", "Mail@^1.0.0"], "carried": False},
                {"package": "Chess", "bundle": "Chess.zip", "sourceCommit": "e" * 40, "version": "1.0.0",
                 "requires": [], "carried": False}]}))

        def carry(packages: list[str], modules: list[str] = ()) -> None:
            for p in packages:
                if not (bake / f"{p}.zip").exists():
                    (bake / f"{p}.zip").write_text(f"sealed {p}")
            for m in modules:
                (bake / MODULES_DIR / m).write_text(f"sealed {m}")

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
        (bake / MODULES_DIR / "ai.module.nupkg").write_text("fresh ai")
        (state / STATE_BUNDLES).write_text("".join(f"{b}\n" for b in shape_bundles))
        out = cli("withhold", "--bake", str(bake), "--withhold", "AI", "--state", str(state), "--content", str(repo))
        expect("Chess (never sealed) waits on AI and stays out; Games waits on Chess and stays out",
               sorted(_lines(state / STATE_NEVER_SEALED)) == ["AI", "Chess", "Games"]
               and "Games: waits too — it requires Chess" in out.stdout
               and not (bake / "Chess.zip").exists() and not (bake / "Games.zip").exists(), out.stdout)

    source_include_cases(expect)

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
