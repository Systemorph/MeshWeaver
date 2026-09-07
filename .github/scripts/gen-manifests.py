#!/usr/bin/env python3
"""Generate (or --check) the per-plugin `manifest.lock` files.

Each top-level plugin folder carries a CI-verified `<Plugin>/manifest.lock` — a sidecar,
machine-maintained sync/version index: a sha256 per file (raw bytes, every file in the folder)
plus a deterministic `moduleVersion` derived from the sorted (path, hash) pairs. The mesh node
remains the manifest for the MESH; manifest.lock exists so installers/sync can diff a plugin
cheaply and detect drift. The `.lock` extension keeps it invisible to the node importers and
validators, which only look at `*.json`.

TWO version fields, and the difference matters:

  moduleVersion  a content HASH. Identifies a tree exactly, but is unordered — it cannot express
                 "at least 1.2", so nothing can pin against it.
  version        SemVer `MAJOR.MINOR.PATCH`, the number dependents pin against, and the one
                 `scripts/tag-modules.py` publishes as the git tag `<Module>/vMAJOR.MINOR.PATCH`.

`version` is assembled at build time, never hand-maintained:

  MAJOR.MINOR   AUTHORED in the module's `index.json` (`content.version`, default `1.0`). These
                carry intent — a breaking change, a new feature — so a human owns them.
  PATCH         DERIVED. It counts content changes, not builds: the tree's `moduleVersion` is
                compared against the hash recorded in the highest RELEASE for the series and bumped
                only when they differ. A version that moved on every CI run would leave every
                dependent's pin permanently stale; a version someone had to remember to bump would
                ship two different trees under one number.

🚨 A RELEASE HAS TWO WITNESSES, and reading only one of them is a hole. The `<Module>/vX.Y.Z` tag
is the published label; the trunk's COMMITTED `manifest.lock` is the claim the tag is a label FOR.
They are not simultaneous: a merge lands the lock immediately, and the tag job publishes the tag
minutes later. Derive from tags alone and every branch regenerating inside that window computes the
number main just took — for a different tree — and `--check-versions` passes, because it derives the
same number it is checking (#434, twice in one afternoon). So the baseline is the HIGHEST of the two
witnesses, and the derivation is otherwise unchanged: same patch when this tree's content hash is
the one that release recorded, +1 when it moved.

🚨 BOTH WITNESSES ARE INPUTS, so both are verified, never assumed. A checkout whose tags are a few
hours old derives a number one release too low — and `--check-versions`, reading those same stale
tags, agrees with it. That is a gate going green on evidence it cannot vouch for (AGENTS.md → "A
gate NEVER tests its own inputs"), and it is not hypothetical: it burned three CI cycles on
2026-08-12. Every command that DERIVES a version (the generator and `--check-versions`) therefore
first asks the remote what it has published AND where its default branch points, catches up when it
can, and fails LOUDLY when it cannot. There is no offline/skip branch: an unverifiable baseline is a
red gate, not a green one.

🚨 THIS IS THE CANONICAL COPY, and it is the PLATFORM's (AGENTS.md → "never hand-roll a node
repo's CI"; ModuleBuildArchitecture.md → "scripts are centralized — the lane fetches the platform's
copy at the pin; repos keep only allow-files"). `node-repo-validate.yml` fetches THIS file at the
caller's pinned `platform-ref` and runs it against the caller's tree, exactly as the compile-check
lane runs `.github/scripts/compile-check.py`. It used to be vendored per repo, and the six copies
had drifted to five different vintages by the time this landed (measured 2026-09-07): Plugins 1157
lines, Crm 646, Education 646, Reinsurance 644, SocialMedia 643, Manufacturing 305. Each fix landed
in one of them — #434 (two witnesses), #942/#1023 (--resolve), #1426 (the trunk baseline) — and the
other five kept the bug. Manufacturing's copy never grew the trunk witness at all.

WHAT STAYS IN THE CALLER — `scripts/gen-manifests.config.json`, its allow-file:

    {"skip": ["src", "test", "scripts", "e2e", "WhatsNew", ".git", ".github", ".claude",
              ".worktrees"],
     "hashModuleSources": false}

  skip                REQUIRED. The top-level directories that are NOT node packages. Per-repo by
                      construction — a name that is a scratch directory in one repo is a shipping
                      package in another — and it must equal `validate-repos.py`'s SKIP, which is
                      what each repo's `check-skip-sets.py` asserts.
  hashModuleSources   Optional, default FALSE. When true, a MIXED package's `src/` project (and the
                      in-tree siblings riding its bundle) is hashed into its moduleVersion — the
                      #878 fix — which needs the caller to ship a `scripts/project-closure.py`
                      exposing graph_of / module_owned / riding_siblings. Default false because
                      turning it on MOVES every mixed package's content hash and therefore its
                      published version: measured on MeshWeaver.SocialMedia, whose own
                      project-closure.py answers #878 with a separate `--closure` gate and warns in
                      bold against hashing `src/` here. It is a per-repo DESIGN choice, declared, and
                      never inferred from whether a file happens to exist — a capability that
                      silently degrades on a missing input is the skip-trapdoor shape AGENTS.md
                      forbids. `true` with an unusable project-closure.py is a hard error naming it.

There is deliberately NO default for `skip` and no fallback when the config is missing: the whole
point of enumerating packages is knowing which directories are packages, and a guessed answer either
demands a manifest.lock for a scratch directory or silently stops versioning a real module.

Usage. In CI the lane runs this file directly out of RUNNER_TEMP with `MW_REPO_ROOT` set. Locally,
through the caller's `scripts/platform-script.py`, which resolves THIS file at the repo's pinned
lane sha — the same resolver the compile-check centralization introduced. A caller that has adopted
the lane but not yet grown that resolver still runs its own `scripts/gen-manifests.py` locally; the
two agree, and the resolver is what retires the copy:
    python3 scripts/platform-script.py gen-manifests.py           # (re)write stale/missing manifests
    python3 scripts/platform-script.py gen-manifests.py --check       # exit 1 if any manifest is missing/stale
    python3 scripts/platform-script.py gen-manifests.py --check-versions   # exit 1 if any version is wrong for its tree
    python3 scripts/platform-script.py gen-manifests.py --resolve     # finish a merge whose ONLY conflicts are locks
    …plus --no-fetch on either deriving command: still VERIFY the baseline against the remote, just
    never write to the object/tag database. It can only make the run stricter — a stale checkout
    fails instead of catching itself up.

Deterministic output (sorted keys, LF, trailing newline); an up-to-date manifest is left
byte-untouched so its recorded sourceCommit survives no-op runs. Stdlib only.
"""
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
from pathlib import Path

# 🚨 ONE answer to "what does this module's bundle contain", not two. The CALLER's
# `scripts/project-closure.py` owns the compiled half's graph AND the module-owned/image-shipped
# split that decides which in-tree siblings RIDE a bundle (Plugins #1118); this file hashes exactly
# that set. Imported by path because its file name is not a Python identifier.
#
# Loaded LAZILY and only for a repo whose config says `hashModuleSources: true` — this file now runs
# against every satellite, and four of them ship no project-closure.py at all. The load is still
# MANDATORY once declared: a repo that asks for module-source hashing and cannot get it fails RED
# naming the missing API, never degrades to a different (and silently smaller) content hash.
_CLOSURE_API = ("graph_of", "module_owned", "riding_siblings")
_CLOSURE_CACHE: dict[Path, object] = {}


def _project_closure(root: Path):
    """The caller's project-closure.py, or a hard error saying why its module hashing cannot run."""
    key = root.resolve()
    if key not in _CLOSURE_CACHE:
        path = key / "scripts" / "project-closure.py"
        if not path.is_file():
            raise SystemExit(
                f"✗ gen-manifests: this repo's config sets hashModuleSources=true, which hashes a "
                f"mixed package's src/ project into its moduleVersion, but {path} does not exist. "
                f"Ship it, or set hashModuleSources=false in scripts/gen-manifests.config.json — "
                f"and note that flipping it MOVES every mixed package's version.")
        spec = importlib.util.spec_from_file_location("project_closure", path)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        missing = [a for a in _CLOSURE_API if not hasattr(module, a)]
        if missing:
            raise SystemExit(
                f"✗ gen-manifests: {path} does not expose {', '.join(missing)} — the "
                f"module-owned/riding-siblings split (Plugins #1118) that hashModuleSources=true "
                f"depends on. An older project-closure.py cannot answer it, and hashing without it "
                f"would record a DIFFERENT content hash under the same rules. Update that script, "
                f"or set hashModuleSources=false.")
        _CLOSURE_CACHE[key] = module
    return _CLOSURE_CACHE[key]

VERSION_RE = re.compile(r"\d+\.\d+\.\d+")
# `<Module>/vMAJOR.MINOR.PATCH` — the shape scripts/tag-modules.py publishes.
MODULE_TAG_RE = re.compile(r"^.+/v\d+\.\d+\.\d+$")
# Every flag this script understands. An unrecognised one is an ERROR, never a silent fall-through
# to the writer: `--check-verisons` used to sail past both `if` arms, REWRITE the manifests and
# exit 0 — a "check" that mutated the tree and reported success.
KNOWN_ARGS = {"--check", "--check-versions", "--no-fetch", "--resolve", "--self-test"}

SCHEMA = "mw-manifest/1"
# The caller's allow-file. Named by MW_MANIFEST_CONFIG when the lane places it elsewhere; otherwise
# it sits beside the caller's other policy files.
CONFIG_NAME = "gen-manifests.config.json"
_CONFIG_CACHE: dict[Path, dict] = {}


def config_path(root: Path) -> Path:
    override = os.environ.get("MW_MANIFEST_CONFIG")
    return Path(override).resolve() if override else root / "scripts" / CONFIG_NAME


def config(root: Path) -> dict:
    """This repo's declared package policy, or a hard error. NEVER a guessed default.

    A missing/garbled config is fatal on purpose. Every other answer is worse: an empty skip set
    demands a manifest.lock for `scripts/` and `.github/`, and a guessed one silently stops
    versioning a real module — a module whose version never moves is built, shelved, and fetched by
    nobody (#878), which is the failure this whole file exists to prevent.
    """
    key = root.resolve()
    if key not in _CONFIG_CACHE:
        path = config_path(key)
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            raise SystemExit(
                f"✗ gen-manifests: {path} not found. This is the PLATFORM's canonical script and it "
                f"does not guess which top-level directories are packages — see the header for the "
                f"file's shape. It must list the same directories as validate-repos.py's SKIP.")
        except (OSError, json.JSONDecodeError) as ex:
            raise SystemExit(f"✗ gen-manifests: {path} is unreadable: {ex}")
        if not isinstance(raw, dict):
            raise SystemExit(f"✗ gen-manifests: {path} must hold a JSON object, not {type(raw).__name__}.")
        skip = raw.get("skip")
        if not isinstance(skip, list) or not all(isinstance(x, str) for x in skip):
            raise SystemExit(
                f"✗ gen-manifests: {path} has no usable \"skip\" — it must be a list of the "
                f"top-level directory names that are NOT node packages.")
        unknown = set(raw) - {"skip", "hashModuleSources"}
        if unknown:
            # A typo'd key is a policy that silently does not apply; `hashModulesSources: true`
            # would read as false and quietly stop hashing src/ into a mixed package's version.
            raise SystemExit(
                f"✗ gen-manifests: {path} has unknown key(s): {', '.join(sorted(unknown))}. "
                f"Known: hashModuleSources, skip.")
        if not isinstance(raw.get("hashModuleSources", False), bool):
            raise SystemExit(f"✗ gen-manifests: {path}'s \"hashModuleSources\" must be true or false.")
        _CONFIG_CACHE[key] = {"skip": set(skip),
                              "hashModuleSources": bool(raw.get("hashModuleSources", False))}
    return _CONFIG_CACHE[key]


def skip_set(root: Path) -> set[str]:
    """Directories that are NOT node packages — declared per repo, and kept equal to
    validate-repos.py's SKIP by the caller's check-skip-sets.py. A directory in one and not the
    other either gets validated as nodes it does not contain, or has a manifest.lock demanded for a
    package it is not."""
    return config(root)["skip"]


# Files never part of a plugin's content.
EXCLUDE_FILES = {"manifest.lock", ".DS_Store"}


def plugin_dirs(root: Path) -> list[Path]:
    # A DOT-directory is never a package — MeshWeaver.Education's copy grew this rule and the other
    # five never got it. Tooling writes them (`check-examples.py` builds under `.example-check/`,
    # `platform-script.py` caches under `.platform-scripts/`) and hashing one as an extra "module"
    # is how a scratch build ends up demanding a manifest.lock. Every repo's declared skip list
    # already names the dot-directories it happened to hit; this makes the rule the same everywhere
    # instead of each list re-discovering it.
    skip = skip_set(root)
    return sorted((d for d in root.iterdir()
                   if d.is_dir() and d.name not in skip and not d.name.startswith(".")),
                  key=lambda d: d.name)


def declared_module(plugin: Path) -> str | None:
    """The assembly a MIXED package ships, from its root `content.module`, or None."""
    idx = plugin / "index.json"
    if not idx.is_file():
        return None
    try:
        content = (json.loads(idx.read_text(encoding="utf-8")) or {}).get("content") or {}
    except (json.JSONDecodeError, OSError):
        return None
    m = content.get("module")
    return m if isinstance(m, str) and m else None


# The project graph and the module-owned split are read once per root: `module_source_files` runs
# for every mixed package, and rebuilding the graph each time would walk every csproj under `src/`
# ~30 times for one identical answer.
_CLOSURE_INPUTS: dict[Path, tuple[dict[str, set[str]], set[str]]] = {}


def _closure_inputs(root: Path) -> tuple[dict[str, set[str]], set[str]]:
    key = root.resolve()
    if key not in _CLOSURE_INPUTS:
        closure = _project_closure(key)
        _CLOSURE_INPUTS[key] = (closure.graph_of(key), closure.module_owned(key))
    return _CLOSURE_INPUTS[key]


def _hash_project(proj: Path, root: Path, files: dict[str, str]) -> None:
    """Hash one `src/` project directory into `files`, keyed by its repo-relative POSIX path."""
    for path in sorted(proj.rglob("*")):
        if not path.is_file() or path.name in EXCLUDE_FILES:
            continue
        rel = path.relative_to(proj)
        if rel.parts and rel.parts[0] in {"bin", "obj"}:
            continue
        files[path.relative_to(root).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()


def module_source_files(plugin: Path, root: Path) -> dict[str, str]:
    """The `src/` files a MIXED package's assembly is built from, hashed like its node files.

    🚨 WITHOUT THIS A `src/`-ONLY CHANGE MOVES NO VERSION (#878), so the rebuilt assembly is
    shelved and fetched by nobody. Delivery is keyed on SemVer alone and two gates close on an
    unchanged version BEFORE any bytes are read: `ModuleUpdateDecision` returns `SkipUpToDate`
    with no `If-None-Match`, and the manual Update button returns `InstallResult(0,0)` before the
    module half is composed. Fresh installs get the new assembly; existing portals never do — and
    repo, CI and registry all look green.

    MEASURED 2026-08-29: 12 of 29 mixed packages were in that state. Worst was the engine itself:
    `AI/manifest.lock` byte-identical to tag `AI/v1.0.0` across 205 changed `src/` files, while
    `MeshWeaver.AI` carries `Agent/` and `Skill/` content as embedded resources — so edits to
    built-in agents reached nobody.

    SCOPE — the module's own project PLUS the in-tree siblings that RIDE its bundle, and nothing
    else. That set is the artifact being versioned, and it is neither of the two answers this
    docstring used to weigh (Plugins #1118):

      * not "the own project only" — it UNDER-covers. A `MeshWeaver.*` project whose source lives
        in this repo's `src/` is by construction nowhere in the portal image's `/app`, so when a
        module's closure reaches one the pack lane copies that assembly INTO the bundle (the
        container path's `--with`, from the compiler's own closure manifest; the SDK path's
        `DepsClosure.Derive(..., ownedPlatformNames)`). It MUST ride — nothing else supplies it at
        run time. Measured on `main` @ 22d3c1d8: `MeshWeaver.Blazor` rode 11 bundles and
        `MeshWeaver.Markdown.Collaboration` rode 13, and not one of those 23 packages' locks
        hashed a byte of the assembly it ships. So an edit under `src/MeshWeaver.Blazor/` changed
        what all 11 delivered and moved none of their versions — the #878 defect exactly, one hop
        out, with the same silence: `ModuleUpdateDecision` returns `SkipUpToDate` before any
        download and the manual Update button returns `InstallResult(0,0)` before the module half
        is composed, so fresh installs get the new bytes and existing portals never do.
      * not "the whole reference closure" — it OVER-covers, and that objection stands. An
        IMAGE-SHIPPED sibling (`src/platform-shipped.txt`) is excluded from the bundle precisely
        so it cannot land a same-identity duplicate beside `/app`'s copy, so its bytes are not in
        the artifact and must not move its version. A change to `MeshWeaver.AI` therefore still
        does not alter `MeshWeaver.AI.OpenAI`'s version today — and starts to on the day
        MeshWeaver#2599 takes that line out of `platform-shipped.txt` and the assembly begins to
        ride, which is the correct answer at both moments and needs no edit here.

    The `.csproj` IS included, for the entry and every riding sibling: it pins the package
    versions that ride with them.
    """
    # DECLARED, never inferred. A repo that has not opted in keeps hashing exactly the files it
    # hashed before — flipping this moves every mixed package's content hash and therefore the
    # version its dependents pin, which is a release event, not a script upgrade.
    if not config(root)["hashModuleSources"]:
        return {}
    module = declared_module(plugin)
    if not module:
        return {}
    proj = root / "src" / module
    if not proj.is_dir():
        return {}
    files: dict[str, str] = {}
    _hash_project(proj, root, files)
    graph, owned = _closure_inputs(root)
    for sibling in _project_closure(root).riding_siblings(root, f"src/{module}", graph, owned):
        directory = root / sibling
        if directory.is_dir():
            _hash_project(directory, root, files)
    return files


def hash_files(plugin: Path, root: Path) -> dict[str, str]:
    """POSIX path (prefixed with the plugin dir name) -> sha256 hex of the raw file bytes.

    For a MIXED package this also covers the `src/` project its assembly is built from AND the
    in-tree siblings that ride its bundle — see `module_source_files` for why, and for why the set
    stops exactly there.
    """
    files: dict[str, str] = {}
    for path in sorted(plugin.rglob("*")):
        if not path.is_file() or path.name in EXCLUDE_FILES:
            continue
        files[path.relative_to(root).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    files.update(module_source_files(plugin, root))
    return files


def module_version(files: dict[str, str]) -> str:
    """sha256 over the sorted (path, hash) pairs with unambiguous framing, truncated to 16 hex.
    sourceCommit is deliberately NOT part of the version — only content counts."""
    h = hashlib.sha256()
    for path in sorted(files):
        h.update(f"{path}\n{files[path]}\n".encode("utf-8"))
    return h.hexdigest()[:16]


def git(root: Path, args: list[str], timeout: int = 30) -> str | None:
    """Run a git command, returning stdout or None when it fails (never raises).

    `subprocess.SubprocessError` is caught alongside `OSError` because the network commands below
    can time out, and an uncaught TimeoutExpired would crash the run rather than being reported as
    "could not reach the remote" — which is the answer the freshness check needs to fail loudly on.
    """
    try:
        out = subprocess.run(["git", *args], cwd=root, capture_output=True, text=True,
                             timeout=timeout)
        return out.stdout.strip() if out.returncode == 0 else None
    except (OSError, subprocess.SubprocessError):
        return None


def _remote_name(root: Path) -> str | None:
    """The remote that publishes this repo's tags — `origin` when present, else the only one."""
    remotes = [r.strip() for r in (git(root, ["remote"]) or "").splitlines() if r.strip()]
    if not remotes:
        return None
    return "origin" if "origin" in remotes else remotes[0]


def _local_module_tags(root: Path) -> dict[str, str]:
    """`<Module>/vMAJOR.MINOR.PATCH` -> object id, from THIS checkout's tag database."""
    listed = git(root, ["for-each-ref", "--format=%(refname:strip=2)\t%(objectname)", "refs/tags"])
    found: dict[str, str] = {}
    for line in (listed or "").splitlines():
        name, _, oid = line.partition("\t")
        if MODULE_TAG_RE.match(name.strip()):
            found[name.strip()] = oid.strip()
    return found


def _published_module_tags(root: Path, remote: str) -> dict[str, str] | None:
    """The same map as PUBLISHED on `remote`, or None when the remote could not be reached.

    `--refs` drops the peeled `refs/tags/X^{}` lines, so an annotated tag compares as its tag
    object — the same id `for-each-ref` reports locally.
    """
    listed = git(root, ["ls-remote", "--tags", "--refs", remote], timeout=60)
    if listed is None:
        return None
    found: dict[str, str] = {}
    for line in listed.splitlines():
        oid, _, ref = line.partition("\t")
        name = ref.strip().removeprefix("refs/tags/")
        if name and MODULE_TAG_RE.match(name):
            found[name] = oid.strip()
    return found


def _tag_drift(local: dict[str, str], published: dict[str, str]) -> list[str]:
    """Published module tags this checkout is missing, or holds pointing somewhere else."""
    return sorted(f"{name} (published {oid[:8]}, local {local.get(name, 'ABSENT')[:8]})"
                  for name, oid in published.items() if local.get(name) != oid)


def _sample(drift: list[str], limit: int = 5) -> str:
    shown = ", ".join(drift[:limit])
    return shown if len(drift) <= limit else f"{shown}, … (+{len(drift) - limit} more)"


def _published_trunk(root: Path, remote: str) -> tuple[str, str | None] | None:
    """`(ref, oid)` of the branch `remote`'s HEAD points at — the TRUNK, whose committed
    `manifest.lock` files are the second witness every version is derived against.

    `--symref` answers both halves in one round-trip: the symbolic line names the default branch
    (never hard-code `main` — a fork, or a sibling node repo, may not use it) and the ordinary line
    carries the tip it currently points at. `oid` is None for a remote that has the symref but no
    commit behind it (a repo nothing has been pushed to): nothing is published there, so there is no
    baseline to derive against — which is a different answer from "could not ask".
    """
    listed = git(root, ["ls-remote", "--symref", remote, "HEAD"], timeout=60)
    if listed is None:
        return None
    ref: str | None = None
    oid: str | None = None
    for line in listed.splitlines():
        value, _, name = line.partition("\t")
        if name.strip() != "HEAD":
            continue
        if value.startswith("ref: "):
            ref = value.removeprefix("ref: ").strip()
        elif value.strip():
            oid = value.strip()
    return (ref, oid) if ref else None


def _have_commit(root: Path, oid: str) -> bool:
    """True when this checkout already holds `oid` (and its tree — `git show` needs both)."""
    return git(root, ["cat-file", "-e", f"{oid}^{{commit}}"]) is not None


def _trunk_commit(root: Path, remote: str, fetch: bool) -> tuple[str | None, list[str]]:
    """The trunk commit to derive against, or the reason this run cannot vouch for one.

    🚨 This is the witness the tag set structurally cannot be. A merge writes the new version into
    main's `manifest.lock` at once; the tag job publishes `<Module>/vX.Y.Z` minutes later. Inside
    that window the tag set is genuinely CURRENT with origin — #423's check is right to pass it —
    and yet the highest tag is one release behind what main has already taken. Reading main's
    committed lock is what closes it, because the lock is the fact and the tag is its lagging index.

    Same doctrine as the tag set: fetch the commit when we may, fail LOUDLY when we cannot get it.
    """
    trunk = _published_trunk(root, remote)
    if trunk is None:
        return None, [f"could not resolve which branch '{remote}' publishes "
                      f"(`git ls-remote --symref {remote} HEAD` failed or answered nothing). Its "
                      f"committed manifest.lock files are half the version baseline, so this "
                      f"cannot pass on a baseline it is unable to read. "
                      f"Fix: restore access to '{remote}' and re-run."]
    ref, oid = trunk
    branch = ref.removeprefix("refs/heads/")
    if oid is None:
        print(f"  trunk: nothing published on {remote} yet — no committed baseline to derive from")
        return None, []
    if not _have_commit(root, oid) and fetch:
        print(f"  trunk: {remote}/{branch} is at {oid[:8]}, missing from this checkout — fetching…")
        git(root, ["fetch", remote, ref], timeout=180)
    if not _have_commit(root, oid):
        return None, [f"'{remote}/{branch}' is at {oid[:8]}, which this checkout does not have"
                      f"{'' if fetch else ' (--no-fetch, so it was not fetched)'}. Its committed "
                      f"manifest.lock files are the half of the version baseline that exists BEFORE "
                      f"the tag job runs, so a number derived without them can silently repeat the "
                      f"one {branch} just took. Fix: git fetch {remote} {branch}"]
    head = git(root, ["rev-parse", "HEAD"])
    # A tree with uncommitted or untracked changes is NOT a committed trunk state, whatever
    # commit it sits on — see below. `git()` answers None when the command itself failed, and
    # an unreadable status must read as "not clean", never as clean.
    clean = git(root, ["status", "--porcelain", "--untracked-files=all"]) == ""
    if head and head != oid and clean \
            and git(root, ["merge-base", "--is-ancestor", head, oid]) is not None:
        # 🚨 #1426: this checkout IS a committed state of the trunk, older than the tip. That is
        # every intermediate commit of a merge-queue group — four PRs land as four pushes inside
        # one minute, and by the time the run for the first of them asks the remote, `HEAD` there
        # already names the fourth. Deriving against the tip then compares this tree with locks
        # that describe trees it never had: Essentials read "lock 1.1.1, content requires 1.1.3
        # (content moved since v1.1.2 committed on the trunk)" on two PRs that never touched
        # Essentials, and every gate downstream of Validate went red with them. A tree that is
        # itself a trunk commit has its baseline in its own committed locks — the fact the tip's
        # lock is a LATER edition of.
        #
        # Both halves of the condition are load-bearing. A PR merge ref or a branch with commits
        # of its own is never an ancestor of the tip. But a branch cut from an OLDER trunk commit
        # whose first edits are still uncommitted sits on an ancestor too — and deriving THAT
        # against itself would let the writer hand an already-published patch number to new
        # content (review of #1427). Such a tree is not a committed trunk state, so the fallback
        # requires a clean tree: an edit in progress keeps deriving against the tip, exactly as
        # before. The self-test holds both negative controls.
        print(f"  trunk: this checkout is a clean {remote}/{branch} @ {head[:8]}, behind the tip "
              f"{oid[:8]} — deriving against itself, not against locks committed after it")
        return head, []
    print(f"  trunk: verified baseline {remote}/{branch} @ {oid[:8]}")
    return oid, []


def derivation_inputs(root: Path, fetch: bool = True) -> tuple[str | None, list[str]]:
    """Verify BOTH witnesses a derived version rests on, and hand back the trunk to derive against.

    Returns `(trunk_commit_or_None, errors)`. A non-empty `errors` means NOTHING may be derived —
    neither written nor checked — because the evidence could not be vouched for.

    The one case that legitimately needs no remote is a repo that HAS no remote: nothing can have
    been published elsewhere, so the local tags are the complete set by construction and there is no
    trunk to compare against. A directory that is not a git checkout at all is an error, not that
    case — there is no history to derive anything from.
    """
    if git(root, ["rev-parse", "--git-dir"]) is None:
        return None, [f"{root} is not a git checkout — module versions are derived from git tags "
                      f"and the trunk's committed manifests, so there is no evidence here to "
                      f"derive them from."]

    remote = _remote_name(root)
    if remote is None:
        print("  tag set: no git remote configured — the local tags are the whole published set")
        return None, []

    errors = _tag_freshness_errors(root, remote, fetch)
    trunk, trunk_errors = _trunk_commit(root, remote, fetch)
    return trunk, errors + trunk_errors


def _tag_freshness_errors(root: Path, remote: str, fetch: bool) -> list[str]:
    """Prove the local tag set IS the published one, or refuse to derive a version from it.

    A derived version is read off the highest release in the series, so the tag database is this
    script's INPUT — not context, not a nicety. A checkout a few hours behind derives one release
    too low, writes that into manifest.lock, and `--check-versions` (reading the same stale tags)
    confirms it. Local green, CI red, and the local green was on absent evidence.

    So: ask the remote what it published, catch up when we can, fail LOUDLY when we cannot. There
    is deliberately NO "skip when offline" branch — that would be the skip-trapdoor AGENTS.md
    forbids, and an unverifiable tag set has to be a red gate.
    """
    published = _published_module_tags(root, remote)
    if published is None:
        return [f"could not reach '{remote}' to verify the tag set is current "
                f"(`git ls-remote --tags {remote}` failed). Versions are derived from PUBLISHED "
                f"tags, so this cannot pass on a tag set it is unable to vouch for. "
                f"Fix: restore access to '{remote}' and re-run."]

    drift = _tag_drift(_local_module_tags(root), published)
    if drift and fetch:
        print(f"  tag set: {len(drift)} published tag(s) missing from this checkout — "
              f"fetching from {remote}…")
        if git(root, ["fetch", "--tags", "--force", remote], timeout=180) is None:
            return [f"`git fetch --tags --force {remote}` failed while catching up on "
                    f"{len(drift)} published tag(s): {_sample(drift)}. Fix: fetch by hand."]
        drift = _tag_drift(_local_module_tags(root), published)

    if drift:
        return [f"this checkout's tag set is STALE — {len(drift)} tag(s) published on '{remote}' "
                f"are missing or differ locally: {_sample(drift)}. Module versions are derived "
                f"from the highest published tag, so every number computed here would be wrong "
                f"(this is exactly the passes-locally / fails-in-CI case). "
                f"Fix: git fetch --tags --force {remote}"]

    print(f"  tag set: verified current with {remote} "
          f"({len(published)} published module tag(s))")
    return []


def declared_series(plugin: Path) -> tuple[int, int]:
    """The module's AUTHORED major.minor from its index.json (`content.version`), default 1.0.

    Major/minor carry INTENT — a breaking change, a feature — so a human owns them. The patch is
    machine-owned (see release_version): nobody should have to remember to bump a number because a
    file changed. Tolerates a missing/short/garbage value rather than failing the whole run: a
    module that never declared a series is simply on 1.0.
    """
    try:
        content = json.loads((plugin / "index.json").read_text(encoding="utf-8")).get("content", {})
        parts = str(content.get("version", "1.0")).split(".")
        return int(parts[0]), int(parts[1]) if len(parts) > 1 else 0
    except (OSError, json.JSONDecodeError, ValueError, IndexError, AttributeError):
        return 1, 0


def released_versions(root: Path, module: str, major: int, minor: int,
                      as_of: str | None = None) -> list[tuple[int, str]]:
    """Every `<module>/vMAJOR.MINOR.PATCH` tag in this series, as (patch, tag), highest first.

    `as_of` limits the set to tags REACHABLE from that commit. The tag job publishes a tag for
    every trunk commit it releases, so when the trunk commit derived against is older than the
    tip (#1426, see _trunk_commit) the tags published for the commits after it are not this
    tree's history and must not be its witness. For the tip itself every published tag is
    reachable, so the normal case reads exactly what it read before.
    """
    listed = git(root, ["tag", "--list", f"{module}/v{major}.{minor}.*"]
                 + (["--merged", as_of] if as_of else []))
    found: list[tuple[int, str]] = []
    for tag in (listed or "").splitlines():
        tag = tag.strip()
        try:
            found.append((int(tag.rsplit(".", 1)[1]), tag))
        except (ValueError, IndexError):
            continue                      # a hand-made tag that isn't ours — ignore, never crash
    return sorted(found, reverse=True)


def _lock_at(root: Path, rev: str, module: str) -> dict | None:
    """The `manifest.lock` `module` had at `rev`, or None when there is no readable one there."""
    recorded = git(root, ["show", f"{rev}:{module}/manifest.lock"])
    if recorded is None:
        return None                        # rev predates the manifest (or the module was renamed)
    try:
        lock = json.loads(recorded)
    except json.JSONDecodeError:
        return None
    return lock if isinstance(lock, dict) else None


def tag_baseline(root: Path, module: str, major: int, minor: int,
                 as_of: str | None = None) -> tuple[int, str | None, str] | None:
    """`(patch, recorded moduleVersion, label)` of the highest PUBLISHED release in the series
    (reachable from `as_of` when given — see released_versions)."""
    for patch, tag in released_versions(root, module, major, minor, as_of):
        lock = _lock_at(root, tag, module)
        if lock is not None:
            return patch, lock.get("moduleVersion"), f"tag {tag}"
    return None


def trunk_baseline(root: Path, trunk: str, module: str, major: int, minor: int) \
        -> tuple[int, str | None, str] | None:
    """`(patch, recorded moduleVersion, label)` of the release the TRUNK's committed lock claims.

    🚨 This is the witness that exists at MERGE time. Between a merge and its tag job finishing,
    main's lock already says 1.0.30 while the highest published tag is still v1.0.29 — so a branch
    deriving from tags alone computes 1.0.30 a second time, for a different tree, and
    `--check-versions` agrees because it derives the same number it is checking (#434).

    A record from a DIFFERENT major.minor is discarded rather than compared: patch numbers only
    order within a series, so 1.0.30 says nothing about where 1.1 starts.
    """
    lock = _lock_at(root, trunk, module)
    if lock is None:
        return None                        # the module does not exist on the trunk yet
    version = str(lock.get("version", ""))
    if not VERSION_RE.fullmatch(version):
        return None
    trunk_major, trunk_minor, patch = (int(p) for p in version.split("."))
    if (trunk_major, trunk_minor) != (major, minor):
        return None
    return patch, lock.get("moduleVersion"), f"v{version} committed on the trunk"


def derive_release(root: Path, plugin: Path, content_hash: str,
                   trunk: str | None = None) -> tuple[str, str]:
    """`(version, the evidence it rests on)` for THIS tree: `major.minor.patch` + a one-line basis.

    Major/minor are authored (declared_series). The PATCH is derived — it counts content changes,
    not builds: compare this tree's content hash against the one recorded in the highest RELEASE
    for the series, and bump only when they differ. That is what makes a pin stable. A version that
    moved on every CI run would leave every dependent's caret pin permanently stale, and a version
    a human had to remember to bump would silently ship two different trees under one number.

    "The highest release" is the highest of the two witnesses — the published tag and the trunk's
    committed lock (see trunk_baseline for why one of them alone is a hole). When both sit on the
    same patch, this tree counts as already released if EITHER recorded its hash; a disagreement
    between them can only ever bump, never re-issue a number under new content.

    No release in the series from either witness ⇒ `.0`, so a newly declared series starts clean.
    `trunk=None` (a repo with no remote) falls back to tags alone — the pre-#434 behaviour, which is
    correct there because nothing can have been merged elsewhere.
    """
    major, minor = declared_series(plugin)
    witnesses = [w for w in (
        tag_baseline(root, plugin.name, major, minor, as_of=trunk),
        trunk_baseline(root, trunk, plugin.name, major, minor) if trunk else None,
    ) if w is not None]
    if not witnesses:
        return f"{major}.{minor}.0", f"nothing released yet in the {major}.{minor} series"

    patch = max(p for p, _, _ in witnesses)
    highest = [w for w in witnesses if w[0] == patch]
    basis = " and ".join(label for _, _, label in highest)
    if any(recorded == content_hash for _, recorded, _ in highest):
        return f"{major}.{minor}.{patch}", f"unchanged since {basis}"
    return f"{major}.{minor}.{patch + 1}", f"content moved since {basis}"


def release_version(root: Path, plugin: Path, content_hash: str, trunk: str | None = None) -> str:
    """The module's SemVer for THIS tree — see derive_release for how it is arrived at."""
    return derive_release(root, plugin, content_hash, trunk)[0]


def git_head(root: Path) -> str | None:
    try:
        out = subprocess.run(["git", "rev-parse", "HEAD"], cwd=root, capture_output=True,
                             text=True, timeout=30)
        return out.stdout.strip() if out.returncode == 0 and out.stdout.strip() else None
    except OSError:
        return None


def serialize(manifest: dict) -> str:
    return json.dumps(manifest, indent=2, sort_keys=True) + "\n"


def read_existing(lock: Path) -> dict | None:
    try:
        existing = json.loads(lock.read_text(encoding="utf-8"))
        return existing if isinstance(existing, dict) else None
    except (OSError, json.JSONDecodeError):
        return None


def check_manifests(root: Path) -> list[str]:
    """Recompute every plugin's manifest and report missing/stale ones. Never writes."""
    errors: list[str] = []
    for plugin in plugin_dirs(root):
        lock = plugin / "manifest.lock"
        files = hash_files(plugin, root)
        version = module_version(files)
        existing = read_existing(lock)
        if existing is None:
            errors.append(f"{plugin.name}: manifest.lock missing or unreadable")
        elif existing.get("files") != files or existing.get("moduleVersion") != version \
                or existing.get("module") != plugin.name or existing.get("schema") != SCHEMA:
            errors.append(f"{plugin.name}: manifest.lock is stale "
                          f"(expected moduleVersion {version})")
        # `version` is checked for PRESENCE and SHAPE only, never recomputed here. Its value depends
        # on which tags the checkout has, and a shallow/tagless clone would otherwise report every
        # manifest stale. The tagging job on main is the authority on the number itself.
        elif not VERSION_RE.fullmatch(str(existing.get("version", ""))):
            errors.append(f"{plugin.name}: manifest.lock has no valid `version` "
                          f"(got {existing.get('version')!r}, expected MAJOR.MINOR.PATCH)")
    if errors:
        errors.append("fix: python3 scripts/gen-manifests.py")
    return errors


def check_versions(root: Path, trunk: str | None = None) -> list[str]:
    """RECOMPUTE every module's version and compare it to what is committed.

    `--check` deliberately validates `version` for shape only, because its correct VALUE depends on
    which tags the checkout has and a tagless clone would report every manifest stale. That leaves a
    hole: a content change committed with a hand-edited or simply un-regenerated version passes the
    gate, and the tag job then publishes a number that does not describe the tree. This closes it —
    the review gate proves the bump was actually done, rather than trusting that someone ran the
    generator.

    🚨 The evidence this reads is verified BEFORE this runs (derivation_inputs), and that is not
    belt-and-braces. The comparison below was once assumed to be self-guarding: with NO tags every
    module computes `major.minor.0`, so a released repo mismatches and fails. That reasoning only
    covers the all-or-nothing case. PARTIAL staleness — the ordinary one, a tag published while you
    were working — is invisible to it: the local set still has a highest tag, the derived number is
    simply one release behind the truth, and a manifest generated moments earlier against those same
    stale tags matches it exactly. Both sides wrong, agreeing, green. CI (fetch-depth: 0) then
    computes the real number and fails. Verifying the input is the only thing that closes it.

    🚨 …and the TRUNK is the second input, for the same reason (#434). A tag set can be perfectly
    current with origin while a merge that has already claimed the next number sits untagged, its
    tag job still running. Deriving against the trunk's committed lock as well is what makes this
    comparison see that number; without it the check derives the very number it is checking.
    """
    errors: list[str] = []
    for plugin in plugin_dirs(root):
        files = hash_files(plugin, root)
        expected, basis = derive_release(root, plugin, module_version(files), trunk)
        committed = (read_existing(plugin / "manifest.lock") or {}).get("version")
        if committed != expected:
            # The direction says WHICH mistake this is, and they have opposite fixes. Committed
            # BELOW expected means the module changed, or the trunk took this number while you were
            # working, and nobody regenerated. Committed ABOVE it means the releases this comparison
            # needs are missing — running the generator then would not fix anything, it would
            # rewrite a correct version down to a wrong one.
            behind = _is_behind(committed, expected)
            errors.append(
                f"{plugin.name}: manifest.lock records version {committed!r}, but this tree's "
                f"content requires {expected!r} ({basis}) — "
                + ("the module changed, or the trunk released this number while you were working, "
                   "and the version was not regenerated against it"
                   if behind else
                   "the manifest claims a number neither the published tags nor the trunk's "
                   "committed manifest justify. Both were verified against the remote before this "
                   "comparison, so this is a hand-edited or orphaned version, NOT missing evidence"))
    if errors:
        errors.append("fix: merge the trunk if it has moved, then python3 scripts/gen-manifests.py "
                      "— the tag set AND the trunk baseline were verified against the remote first, "
                      "so the expected numbers above are the ones CI computes")
    return errors


def _is_behind(committed: str | None, expected: str) -> bool:
    """True when the committed version is OLDER than expected (a forgotten regeneration)."""
    def parts(text: str | None) -> tuple[int, ...]:
        try:
            return tuple(int(p) for p in str(text).split("."))
        except ValueError:
            return (-1,)
    return parts(committed) < parts(expected)


def generate(root: Path, fetch: bool = True) -> int:
    # Deriving a version from a stale tag database is the ORIGINAL sin: the wrong number is written
    # into manifest.lock, and every later check — reading the same stale tags — agrees with it. So
    # both witnesses (the published tags AND the trunk's committed locks) are proven current BEFORE
    # anything is written, not after.
    trunk, unvouchable = derivation_inputs(root, fetch)
    if unvouchable:
        print("✗ refusing to derive versions from a baseline that cannot be vouched for:")
        for e in unvouchable:
            print(f"  - {e}")
        return 1

    written = 0
    commit = git_head(root)
    modules = plugin_dirs(root)
    for plugin in modules:
        lock = plugin / "manifest.lock"
        files = hash_files(plugin, root)
        version = module_version(files)
        existing = read_existing(lock)
        # The SemVer is recomputed here, not just shape-checked, because a REVERT needs it: the
        # restored tree hashes to an already-published one, so `files`/`moduleVersion` match and the
        # old skip left `version` pointing at a number that describes a DIFFERENT (later) tree. A
        # published version describes exactly one tree forever, so a revert must move FORWARD.
        #
        # 🚨 Only ever rewrite the version FORWARD. On a tagless/shallow checkout release_version
        # computes `major.minor.0` for everything, and blindly writing that would rewrite correct
        # versions DOWN — the very thing `--check-versions`' hint warns about. Comparing direction
        # (rather than equality) makes the tagless case a no-op instead of a corruption.
        expected_semver = release_version(root, plugin, version, trunk)
        semver_stale = VERSION_RE.fullmatch(str(existing.get("version", ""))) is not None \
            and _is_behind(existing.get("version"), expected_semver) if existing else False
        if existing is not None and existing.get("files") == files \
                and existing.get("moduleVersion") == version \
                and existing.get("module") == plugin.name and existing.get("schema") == SCHEMA \
                and VERSION_RE.fullmatch(str(existing.get("version", ""))) \
                and not semver_stale:
            # Reported, not silent. A run that rewrites nothing used to print one summary line
            # indistinguishable from a run that did the work, which is why agents kept having to
            # prove by hand that moduleVersion had actually moved.
            print(f"  = {plugin.name}: unchanged "
                  f"(v{existing.get('version')}, moduleVersion {version})")
            continue  # up to date — leave byte-untouched (preserves its recorded sourceCommit)
        manifest = {
            "schema": SCHEMA,
            "module": plugin.name,
            "moduleVersion": version,
            "version": expected_semver,
            "sourceCommit": commit,
            "files": files,
        }
        lock.write_text(serialize(manifest), encoding="utf-8", newline="\n")
        was = (f"was v{existing.get('version')} / {existing.get('moduleVersion')}"
               if existing else "new manifest")
        print(f"  ✎ {plugin.name}: v{manifest['version']}, moduleVersion {version}  ({was})")
        written += 1

    if written:
        print(f"✓ {len(modules)} module(s): {written} rewritten, "
              f"{len(modules) - written} already current")
        return 0
    # 🚨 A no-op is a real outcome, and it must not read like a job well done. "0 (re)written" next
    # to a ✓ is how a run that hashed nothing you edited passes for a run that regenerated it.
    print(f"⚠ {len(modules)} module(s): NOTHING was rewritten — every manifest.lock already "
          f"matches this working tree.")
    print(f"  If you just edited a module, that edit changed no file this script hashes: wrong "
          f"folder, a name in EXCLUDE_FILES ({', '.join(sorted(EXCLUDE_FILES))}), or unsaved. "
          f"Check `git status` before you push — nothing here was regenerated.")
    return 0


def resolve_conflicts(root: Path, fetch: bool = True, regenerate=None) -> int:
    """Finish a merge whose only unmerged paths are `manifest.lock` files.

    🚨 A lock has no meaningful three-way merge: both sides are DERIVED from their own tree, so a
    textual merge of two hash sets describes neither. The only correct resolution is to regenerate
    from the merged tree — measured seven times out of seven in one session (#942). This does that
    and stages the result, so a merge that conflicts ONLY on generated files stops being manual.

    The refusal below is the whole safety property. Regenerating while a SOURCE file is still
    unmerged would hash a half-merged tree and stage a lock that describes nothing — a green
    `--check` over content nobody has ever built. So this exits non-zero and names those paths
    instead: a real conflict is still yours to resolve, and only after that does the lock follow.

    `regenerate` is injectable so the self-test can exercise this function itself rather than a
    re-implementation of its decisions.
    """
    regenerate = regenerate or (lambda: generate(root, fetch))
    unmerged = git(root, ["diff", "--name-only", "--diff-filter=U"])
    if unmerged is None:
        print("✗ --resolve: cannot list unmerged paths (is this a git checkout?)")
        return 2
    paths = sorted({p.strip() for p in unmerged.splitlines() if p.strip()})
    locks = [p for p in paths if p.endswith("/manifest.lock")]
    others = [p for p in paths if not p.endswith("/manifest.lock")]

    if others:
        print(f"✗ --resolve refuses: {len(others)} non-generated path(s) are still unmerged, and a "
              f"lock regenerated over a half-merged tree would describe a tree nobody has:")
        for o in others[:20]:
            print(f"  - {o}")
        if len(others) > 20:
            print(f"  … and {len(others) - 20} more")
        print("  Resolve those first, then re-run --resolve.")
        return 1

    # 🚨 The unmerged list is not the whole question. `generate()` hashes the WORKING TREE, so an
    # unstaged or untracked edit sitting under a module gets baked into the lock this stages — a
    # hash describing a tree that was never committed, arriving inside a merge commit where nobody
    # is looking for it. During a merge every merged file is staged, so an unstaged change here is
    # not part of the merge and this refuses rather than silently absorbing it.
    if (loose := _loose_worktree_paths(root, locks)):
        print(f"✗ --resolve refuses: {len(loose)} unstaged/untracked path(s) would be hashed into "
              f"the regenerated lock, and they are not part of this merge:")
        for path in loose[:20]:
            print(f"  - {path}")
        if len(loose) > 20:
            print(f"  … and {len(loose) - 20} more")
        print("  Stage or stash them, then re-run --resolve.")
        return 1
    if not locks:
        # Not an error, and deliberately not a ✓: "nothing to resolve" must not read like
        # "conflicts resolved".
        print("⚠ --resolve: no conflicted manifest.lock files — nothing was resolved.")
        return 0

    print(f"  {len(locks)} conflicted manifest.lock file(s); regenerating from the merged tree:")
    for lock in locks:
        print(f"  - {lock}")
    rc = regenerate()
    if rc != 0:
        return rc
    # 🚨 STAGE EVERY LOCK THE REGENERATION MOVED, not just the conflicted ones.
    #
    # `generate()` rewrites the WHOLE tree, and a merge can change a module whose lock did not
    # conflict — a module both sides touched compatibly, or one whose closure picked up a file
    # through `project-closure.py`. Staging only `locks` left that module's regenerated lock
    # UNSTAGED, the merge commit went out without it, and the failure surfaced two steps later as
    # a CI-only red: `--check` reads the WORKING TREE, where the lock is correct, while CI checks
    # the COMMITTED tree, where it is stale. Measured on #1023 (2026-09-03) — `Chat: manifest.lock
    # is stale (expected moduleVersion ef2aac72d13f798d)` on a branch whose local `--check` was
    # green.
    #
    # Safe because of the refusal above: `_loose_worktree_paths` has already established that the
    # tree carried no unstaged or untracked edits apart from the conflicted locks, so anything
    # `generate()` has just modified is a product of THIS merge and belongs in it.
    changed = sorted(set(locks) | set(_dirty_locks(root)))
    if git(root, ["add", "--"] + changed) is None:
        print("✗ --resolve: regeneration succeeded but `git add` failed — stage the locks yourself")
        return 1
    if (extra := [c for c in changed if c not in locks]):
        print(f"  …and {len(extra)} lock(s) the regeneration also moved, which did NOT conflict "
              f"and would otherwise have been left out of the merge commit:")
        for path in extra:
            print(f"  - {path}")
    still = (git(root, ["diff", "--name-only", "--diff-filter=U"]) or "").strip()
    if still:
        print("✗ --resolve: paths are still unmerged after staging:")
        for path in still.splitlines():
            print(f"  - {path}")
        return 1
    print(f"✓ resolved {len(locks)} generated lock conflict(s) — the merge can be committed")
    return 0


def _dirty_locks(root: Path) -> list[str]:
    """Every `*/manifest.lock` git currently reports as changed — staged or not.

    `git status --porcelain` rather than `diff --name-only`, because after `generate()` a lock may
    be modified in the index, in the working tree, or both, and all three must be staged.
    """
    out = git(root, ["status", "--porcelain", "--", "*/manifest.lock"]) or ""
    found: list[str] = []
    for line in out.splitlines():
        # "XY <path>" — the status codes are the first two columns; a rename would carry " -> ",
        # which a generated lock never does (the generator only rewrites in place).
        path = line[3:].strip().strip('"')
        if path.endswith("/manifest.lock"):
            found.append(path)
    return found


def _loose_worktree_paths(root: Path, locks: list[str]) -> list[str]:
    """Unstaged modifications and untracked files that `generate()` would hash — excluding the
    conflicted locks themselves, which are exactly what this run is about to rewrite. Pure-ish:
    two git reads, no writes."""
    conflicted = set(locks)
    unstaged = (git(root, ["diff", "--name-only"]) or "").splitlines()
    untracked = (git(root, ["ls-files", "--others", "--exclude-standard"]) or "").splitlines()
    return sorted({p.strip() for p in unstaged + untracked
                   if p.strip() and p.strip() not in conflicted})


def self_test() -> int:
    """Runs --resolve's real function in throwaway repos, including the case it must REFUSE."""
    import tempfile
    failures: list[str] = []

    def g(cwd: Path, *args: str) -> str:
        return subprocess.run(["git", "-C", str(cwd), *args], capture_output=True,
                              text=True, check=False).stdout

    def declare(repo: Path, skip=("scripts", ".git"), hash_module_sources=False) -> Path:
        """Write the caller's allow-file. Every fixture is a repo, so every fixture declares one —
        there is no default, which is the point of case 5."""
        (repo / "scripts").mkdir(parents=True, exist_ok=True)
        cfg = repo / "scripts" / CONFIG_NAME
        cfg.write_text(json.dumps({"skip": list(skip),
                                   "hashModuleSources": hash_module_sources}) + "\n")
        _CONFIG_CACHE.pop(repo.resolve(), None)
        return cfg

    def fixture(tmp: Path, also_conflict_source: bool, name: str | None = None) -> Path:
        """Two branches that both rewrote the same generated lock — the shape #942 measured."""
        repo = tmp / (name or ("both" if also_conflict_source else "lock-only"))
        (repo / "Mod").mkdir(parents=True)
        g(repo.parent, "init", "-q", "-b", "main", str(repo))
        g(repo, "config", "user.email", "t@example.com")
        g(repo, "config", "user.name", "t")
        declare(repo)
        (repo / "Mod" / "manifest.lock").write_text('{"moduleVersion": "base"}\n')
        (repo / "Mod" / "src.cs").write_text("base\n")
        g(repo, "add", "-A")
        g(repo, "commit", "-qm", "base")
        g(repo, "checkout", "-qb", "feature")
        (repo / "Mod" / "manifest.lock").write_text('{"moduleVersion": "feature"}\n')
        (repo / "Mod" / "src.cs").write_text("feature\n")
        g(repo, "commit", "-qam", "feature")
        g(repo, "checkout", "-q", "main")
        (repo / "Mod" / "manifest.lock").write_text('{"moduleVersion": "trunk"}\n')
        if also_conflict_source:
            (repo / "Mod" / "src.cs").write_text("trunk\n")
        g(repo, "commit", "-qam", "trunk")
        g(repo, "checkout", "-q", "feature")
        g(repo, "merge", "main")
        return repo

    with tempfile.TemporaryDirectory() as tmpdir:
        tmp = Path(tmpdir)

        # 1. lock-only conflict → resolved and staged, merge committable
        repo = fixture(tmp, also_conflict_source=False)
        unmerged = g(repo, "diff", "--name-only", "--diff-filter=U").split()
        if unmerged != ["Mod/manifest.lock"]:
            failures.append(f"fixture(lock-only): expected only the lock unmerged, got {unmerged}")

        def regen() -> int:
            (repo / "Mod" / "manifest.lock").write_text('{"moduleVersion": "regenerated"}\n')
            return 0

        rc = resolve_conflicts(repo, fetch=False, regenerate=regen)
        if rc != 0:
            failures.append(f"lock-only conflict should resolve, got exit {rc}")
        if g(repo, "diff", "--name-only", "--diff-filter=U").split():
            failures.append("lock-only conflict left unmerged paths behind")
        if (repo / "Mod" / "manifest.lock").read_text() != '{"moduleVersion": "regenerated"}\n':
            failures.append("the staged lock is not the regenerated one")

        # 1b. 🚨 A NON-CONFLICTED lock the regeneration ALSO moved must be staged too.
        #
        # This is the #1023 trap (2026-09-03). `generate()` rewrites the whole tree, so a merge can
        # move a lock that never conflicted; staging only the conflicted set left that one unstaged,
        # the merge commit went out without it, and it surfaced as a CI-ONLY red — `--check` reads
        # the working tree, where the lock is correct, while CI checks the COMMITTED tree, where it
        # is stale.
        #
        # 🚨 `Other/manifest.lock` is COMMITTED in the base, not `git add`ed into the merge. A newly
        # added file shows up in `diff --cached` whatever --resolve does, which made the first
        # version of this case pass against the reverted fix — vacuous, and exactly the shape this
        # file warns about elsewhere. Committed-and-then-moved is the only shape that can fail.
        repo1b = tmp / "extra-lock"
        (repo1b / "Mod").mkdir(parents=True)
        (repo1b / "Other").mkdir(parents=True)
        g(repo1b.parent, "init", "-q", "-b", "main", str(repo1b))
        g(repo1b, "config", "user.email", "t@example.com")
        g(repo1b, "config", "user.name", "t")
        declare(repo1b)
        (repo1b / "Mod" / "manifest.lock").write_text('{"moduleVersion": "base"}\n')
        (repo1b / "Mod" / "src.cs").write_text("base\n")
        (repo1b / "Other" / "manifest.lock").write_text('{"moduleVersion": "base"}\n')
        (repo1b / "Other" / "src.cs").write_text("base\n")
        g(repo1b, "add", "-A")
        g(repo1b, "commit", "-qm", "base")
        g(repo1b, "checkout", "-qb", "feature")
        (repo1b / "Mod" / "manifest.lock").write_text('{"moduleVersion": "feature"}\n')
        (repo1b / "Mod" / "src.cs").write_text("feature\n")
        g(repo1b, "commit", "-qam", "feature")
        g(repo1b, "checkout", "-q", "main")
        (repo1b / "Mod" / "manifest.lock").write_text('{"moduleVersion": "trunk"}\n')
        # Other/ moves on the trunk only, so it merges CLEANLY — no conflict, but the merged tree
        # is not what either side's lock described, which is why regeneration moves it.
        (repo1b / "Other" / "src.cs").write_text("trunk\n")
        g(repo1b, "commit", "-qam", "trunk")
        g(repo1b, "checkout", "-q", "feature")
        g(repo1b, "merge", "main")

        def regen1b() -> int:
            (repo1b / "Mod" / "manifest.lock").write_text('{"moduleVersion": "regenerated"}\n')
            (repo1b / "Other" / "manifest.lock").write_text('{"moduleVersion": "also-moved"}\n')
            return 0

        rc = resolve_conflicts(repo1b, fetch=False, regenerate=regen1b)
        if rc != 0:
            failures.append(f"a regeneration that moves a non-conflicted lock should resolve, "
                            f"got exit {rc}")
        # UNSTAGED only: `git diff --name-only` excludes the index, so a lock --resolve staged is
        # correctly silent here while one it left in the working tree is not.
        elif (leftover := g(repo1b, "diff", "--name-only", "--", "*/manifest.lock").strip()):
            failures.append("a NON-conflicted lock the regeneration moved was left UNSTAGED "
                            f"({leftover!r}) — the merge commit would go out without it and CI "
                            "would red on a stale lock while a local --check passed (#1023)")

        # 2. a SOURCE file conflicts too → must refuse, and must not touch the lock
        repo2 = fixture(tmp, also_conflict_source=True)
        before = (repo2 / "Mod" / "manifest.lock").read_text()
        called = []

        def regen2() -> int:
            called.append(True)
            return 0

        rc = resolve_conflicts(repo2, fetch=False, regenerate=regen2)
        if rc != 1:
            failures.append(f"a half-merged tree must be refused with exit 1, got {rc}")
        if called:
            failures.append("refused case still ran the regenerator")
        if (repo2 / "Mod" / "manifest.lock").read_text() != before:
            failures.append("refused case modified the lock anyway")

        # 2b. an UNSTAGED edit under a module would be hashed in — refuse (Copilot's review of
        #     this change: `generate()` reads the working tree, not the index).
        repo2b = fixture(tmp, also_conflict_source=False, name="unstaged")
        (repo2b / "Mod" / "stray.cs").write_text("not part of this merge\n")
        called2b = []
        rc = resolve_conflicts(repo2b, fetch=False,
                               regenerate=lambda: (called2b.append(True), 0)[1])
        if rc != 1:
            failures.append(f"an untracked file under a module must be refused, got {rc}")
        if called2b:
            failures.append("the untracked-file case still ran the regenerator")
        # …and staging it makes it part of the merge, so the resolve proceeds.
        g(repo2b, "add", "Mod/stray.cs")
        rc = resolve_conflicts(repo2b, fetch=False, regenerate=lambda: 0)
        if rc != 0:
            failures.append(f"a STAGED file is part of the merge and must not block, got {rc}")

        # 4. 🚨 #1426: a checkout that IS an older committed state of the trunk — every intermediate
        #    commit of a merge-queue group's main push — derives against ITSELF. Two trunk commits,
        #    each with a lock that honestly describes its own tree and a tag published for it; the
        #    older one checked out. The tip's lock (1.0.1, another tree's hash) and the tip's tag
        #    are both LATER than this tree, and both used to be its witness.
        origin = tmp / "origin.git"
        g(tmp, "init", "-q", "--bare", "-b", "main", str(origin))
        repo4 = tmp / "queue-intermediate"
        (repo4 / "Mod").mkdir(parents=True)
        g(tmp, "init", "-q", "-b", "main", str(repo4))
        g(repo4, "config", "user.email", "t@example.com")
        g(repo4, "config", "user.name", "t")
        g(repo4, "remote", "add", "origin", str(origin))
        declare(repo4)
        (repo4 / "Mod" / "index.json").write_text('{"content": {"version": "1.0"}}\n')

        def commit_release(src: str, version: str, tag: str) -> str:
            (repo4 / "Mod" / "src.cs").write_text(src)
            content = module_version(hash_files(repo4 / "Mod", repo4))
            (repo4 / "Mod" / "manifest.lock").write_text(
                json.dumps({"version": version, "moduleVersion": content}) + "\n")
            g(repo4, "add", "-A")
            g(repo4, "commit", "-qm", version)
            g(repo4, "tag", tag)
            return g(repo4, "rev-parse", "HEAD").strip()

        older = commit_release("one\n", "1.0.0", "Mod/v1.0.0")
        tip = commit_release("two\n", "1.0.1", "Mod/v1.0.1")
        g(repo4, "push", "-q", "origin", "main", "--tags")
        g(repo4, "checkout", "-q", older)
        trunk4, unvouchable4 = derivation_inputs(repo4, fetch=True)
        if unvouchable4:
            failures.append(f"the queue-intermediate fixture could not vouch for its baseline: "
                            f"{unvouchable4}")
        elif trunk4 != older:
            failures.append(f"a checkout that is an older trunk commit must derive against ITSELF "
                            f"({older[:8]}), not the tip ({tip[:8]}); got {trunk4!r} (#1426)")
        elif (problems := check_versions(repo4, trunk4)):
            failures.append("an older trunk commit whose lock describes its own tree must pass "
                            f"--check-versions, got: {problems[0]} (#1426)")
        # The control: derived against the TIP — what every run did before — the same tree reads
        # as stale. Without this line a fixture that never reproduced the red would pass vacuously.
        if not check_versions(repo4, tip):
            failures.append("the queue-intermediate fixture does not reproduce #1426 — deriving "
                            "the older commit against the tip should have reported a version problem")
        # Negative control A (review of #1427): the same older commit with an UNCOMMITTED edit
        # is a branch being authored, not a trunk state — it must derive against the tip, or the
        # writer could hand v1.0.1's number to a third tree.
        (repo4 / "Mod" / "src.cs").write_text("three, uncommitted\n")
        trunk4a, _ = derivation_inputs(repo4, fetch=True)
        if trunk4a != tip:
            failures.append(f"an older trunk commit with UNCOMMITTED edits must derive against "
                            f"the tip ({tip[:8]}), got {trunk4a!r} — the writer would reuse a "
                            f"published patch number for new content (#1427 review)")
        g(repo4, "checkout", "-q", "--", "Mod/src.cs")
        # Negative control B: a branch with a commit of its own off the older trunk commit is not
        # an ancestor of the tip and derives against the tip, as every branch always did.
        g(repo4, "checkout", "-qb", "feature", older)
        (repo4 / "Mod" / "src.cs").write_text("three, committed\n")
        g(repo4, "commit", "-qam", "feature")
        trunk4c, _ = derivation_inputs(repo4, fetch=True)
        if trunk4c != tip:
            failures.append(f"a feature branch off an older trunk commit must derive against the "
                            f"tip ({tip[:8]}), got {trunk4c!r}")
        # …and the tip itself still derives against the tip, reading every published tag.
        g(repo4, "checkout", "-q", tip)
        trunk4b, _ = derivation_inputs(repo4, fetch=True)
        if trunk4b != tip:
            failures.append(f"the trunk tip must derive against itself, got {trunk4b!r}")
        elif (problems := check_versions(repo4, trunk4b)):
            failures.append(f"the trunk tip must pass --check-versions, got: {problems[0]}")

        # 5. 🚨 THE CONFIG IS AN INPUT, so it is verified like one. This file is the platform's and
        #    runs against six repos whose package sets differ; the skip list is the ONLY thing that
        #    tells it which top-level directories are packages. Every branch below has to be an
        #    ERROR rather than a default, because each silent alternative is a shipped defect:
        #    guessing an empty skip demands a manifest.lock for `scripts/`, and guessing another
        #    repo's list stops versioning a real module (a module whose version never moves is
        #    built, shelved and fetched by nobody — #878).
        repo5 = tmp / "config"
        (repo5 / "Pkg").mkdir(parents=True)
        (repo5 / "notapackage").mkdir(parents=True)
        g(tmp, "init", "-q", "-b", "main", str(repo5))
        (repo5 / "Pkg" / "index.json").write_text('{"content": {"version": "1.0"}}\n')

        def enumerated(repo: Path) -> list[str]:
            _CONFIG_CACHE.pop(repo.resolve(), None)
            return [d.name for d in plugin_dirs(repo)]

        try:
            enumerated(repo5)
            failures.append("a repo with no gen-manifests.config.json must FAIL, not guess which "
                            "directories are packages")
        except SystemExit as ex:
            if CONFIG_NAME not in str(ex):
                failures.append(f"the missing-config error must name {CONFIG_NAME}, said: {ex}")

        declare(repo5, skip=("scripts", ".git", "notapackage"))
        if (got := enumerated(repo5)) != ["Pkg"]:
            failures.append(f"the declared skip list must drive enumeration; expected ['Pkg'], got {got}")

        # A typo'd key is a policy that silently does not apply — `hashModulesSources: true` would
        # read as false and quietly stop hashing src/ into a mixed package's version.
        (repo5 / "scripts" / CONFIG_NAME).write_text(
            '{"skip": ["scripts", ".git"], "hashModulesSources": true}\n')
        try:
            enumerated(repo5)
            failures.append("an unknown config key must FAIL — a typo'd flag silently does nothing")
        except SystemExit as ex:
            if "hashModulesSources" not in str(ex):
                failures.append(f"the unknown-key error must name the key, said: {ex}")

        # 5b. hashModuleSources is DECLARED, and declaring it without a usable project-closure.py
        #     is an error rather than a quiet fall-back to the smaller hash. A capability that
        #     degrades on a missing input is the skip-trapdoor shape AGENTS.md forbids, and here it
        #     would silently change what a published version MEANS.
        (repo5 / "src" / "Mod.Asm").mkdir(parents=True)
        (repo5 / "src" / "Mod.Asm" / "a.cs").write_text("code\n")
        (repo5 / "Pkg" / "index.json").write_text(
            '{"content": {"version": "1.0", "module": "Mod.Asm"}}\n')
        declare(repo5, skip=("scripts", ".git", "src", "notapackage"))
        off = module_version(hash_files(repo5 / "Pkg", repo5))

        declare(repo5, skip=("scripts", ".git", "src", "notapackage"), hash_module_sources=True)
        try:
            hash_files(repo5 / "Pkg", repo5)
            failures.append("hashModuleSources=true without scripts/project-closure.py must FAIL, "
                            "not fall back to the src-less hash")
        except SystemExit as ex:
            if "project-closure.py" not in str(ex):
                failures.append(f"the missing-closure error must name the script, said: {ex}")

        # An OLD project-closure.py — the shape MeshWeaver.SocialMedia ships, which predates the
        # module-owned/riding-siblings split (#1118) — must be refused for the same reason.
        (repo5 / "scripts" / "project-closure.py").write_text("def graph_of(root):\n    return {}\n")
        _CLOSURE_CACHE.pop(repo5.resolve(), None)
        try:
            hash_files(repo5 / "Pkg", repo5)
            failures.append("a project-closure.py missing module_owned/riding_siblings must FAIL")
        except SystemExit as ex:
            if "module_owned" not in str(ex):
                failures.append(f"the incompatible-closure error must name the API, said: {ex}")

        (repo5 / "scripts" / "project-closure.py").write_text(
            "def graph_of(root):\n    return {}\n"
            "def module_owned(root):\n    return set()\n"
            "def riding_siblings(root, proj, graph, owned):\n    return []\n")
        _CLOSURE_CACHE.pop(repo5.resolve(), None)
        on = module_version(hash_files(repo5 / "Pkg", repo5))
        if on == off:
            failures.append("hashModuleSources must CHANGE a mixed package's content hash — the "
                            "fixture does not reproduce the flip, so the default proves nothing")
        # …and the default really is the pre-centralization behaviour: with the flag off, the
        # src/ tree is not in the hash even though project-closure.py is now right there.
        declare(repo5, skip=("scripts", ".git", "src", "notapackage"))
        if module_version(hash_files(repo5 / "Pkg", repo5)) != off:
            failures.append("hashModuleSources=false must hash exactly what a vendored copy hashed "
                            "— a satellite adopting this script must not have its versions move")

        # 3. nothing unmerged → a no-op that does not claim to have resolved anything
        repo3 = tmp / "clean"
        repo3.mkdir()
        g(repo3.parent, "init", "-q", "-b", "main", str(repo3))
        g(repo3, "config", "user.email", "t@example.com")
        g(repo3, "config", "user.name", "t")
        declare(repo3)
        # COMMITTED, not just written: --resolve refuses a tree with loose paths, and an untracked
        # config would make this case pass for the wrong reason.
        g(repo3, "add", "-A")
        g(repo3, "commit", "-qm", "declare")
        rc = resolve_conflicts(repo3, fetch=False, regenerate=lambda: 0)
        if rc != 0:
            failures.append(f"a clean tree should be a no-op, got exit {rc}")

    if failures:
        print("✗ gen-manifests self-test:")
        for f in failures:
            print(f"  - {f}")
        return 1
    print("✓ gen-manifests self-test: --resolve regenerates a lock-only conflict, ALSO stages a "
          "non-conflicted lock the regeneration moved, REFUSES a half-merged tree and an unstaged "
          "edit, no-ops on a clean one, an older trunk commit derives against ITSELF (#1426), and "
          "the per-repo config is REQUIRED — a missing one, a typo'd key and an unusable "
          "project-closure.py each fail rather than defaulting")
    return 0


def repo_root() -> Path:
    """The tree under test. The lane runs this file out of RUNNER_TEMP, so the caller's checkout
    arrives in the environment — the same `MW_REPO_ROOT` contract compile-check.py takes. The
    fallback covers a copy sitting in a repo's own `scripts/`, which is how the self-test and a
    local `platform-script.py` run resolve it."""
    env = os.environ.get("MW_REPO_ROOT")
    return Path(env).resolve() if env else Path(__file__).resolve().parent.parent


def main() -> int:
    root = repo_root()
    args = sys.argv[1:]
    unknown = [a for a in args if a not in KNOWN_ARGS]
    if unknown:
        # Never fall through to the writer. A mistyped flag reaching `generate()` is a "check"
        # that rewrites the tree and exits 0 — the most convincing possible false green.
        print(f"✗ unknown argument(s): {' '.join(unknown)}")
        print(f"  known: {', '.join(sorted(KNOWN_ARGS))}")
        return 2
    fetch = "--no-fetch" not in args

    if "--check-versions" in args:
        # The published tags AND the trunk's committed locks are this check's evidence. Prove both
        # first: evidence that is stale (or absent, as the untagged-merge window makes the trunk's)
        # makes every number below wrong in the same direction as the manifest it is comparing to,
        # so the comparison would agree with itself and report green.
        trunk, unvouchable = derivation_inputs(root, fetch)
        if unvouchable:
            print("✗ cannot verify module versions — the baseline is not trustworthy:")
            for e in unvouchable:
                print(f"  - {e}")
            return 1
        errors = check_versions(root, trunk)
        if errors:
            print(f"✗ {len(errors) - 1 if len(errors) > 1 else len(errors)} version problem(s):")
            for e in errors:
                print(f"  - {e}")
            return 1
        print(f"✓ every module's version matches its content ({len(plugin_dirs(root))} module(s) "
              f"verified against {'the published tags and the trunk' if trunk else 'the local tags'})")
        return 0
    if "--self-test" in args:
        return self_test()
    if "--resolve" in args:
        return resolve_conflicts(root, fetch)
    if "--check" in args:
        errors = check_manifests(root)
        if errors:
            print(f"✗ {len(errors) - 1} stale/missing manifest(s):")
            for e in errors:
                print(f"  - {e}")
            return 1
        print("✓ all manifest.lock files up to date")
        return 0
    return generate(root, fetch)


if __name__ == "__main__":
    sys.exit(main())
