#!/usr/bin/env python3
"""node-repo-scope.py — decide what a plugin repo's CI must actually REBUILD.

    "in plugins we only re-run concerned modules ⇒ we can estimate the required count and
     validate it"  ·  "all plugins should only re-build what changed"  ·  "there is always a
     notion of 'full rebuild' when the platform updates"  ·  "unify all MeshWeaver.* repos"

ONE LANE asks this question (a second, `--for packages` for the retired NuGet-floor pack lane
`plugin-publish.yml`, was removed on 2026-08-30 — in-mesh source is type-checked against the
platform IMAGE by node-repo-compile-check.yml, and nothing a node repo builds goes to a feed):

  --for modules   `node-repo-module-pack.yml` fans out ONE JOB per mixed package. That fan-out was
                  the caller's literal list, every entry on every event: MeshWeaver.Plugins ran 29
                  module builds on every push of every PR, whether the diff touched one module's
                  C# or a course's markdown.

🚨 THE BIAS IS THE DESIGN, and it is copied from bake-scope.sh deliberately. Narrowing a build is
the shape that produces a SILENT under-build, and the evidence of the miss is the absence of
evidence. So every uncertainty below — an event without a meaningful diff, a missing selector, a
git failure, a file that reaches the tooling, a matrix entry built from outside this checkout —
resolves to a FULL run, out loud, NAMING which one.

═══ WHY ONLY `pull_request` / `merge_group` NARROW ═══

**A platform update is always FULL.** A module is built against a platform PIN; when the platform
releases, the pin moves and every module must be rebuilt AND republished or every portal reads
`FrameworkDeclined (built against <old>, live <new>)` and adopts nothing (MeshWeaver#2088). The
release-follow triggers — `repository_dispatch` and the `schedule` poll — therefore never narrow.
The same argument makes the pack lane exhaustive against a MOVING FLOOR: a package that did not
change can stop compiling when the published framework advances, which is the whole reason that
gate exists.

**A `push` is always FULL, and that is deliberate rather than unfinished.**
  * modules — the baseline a publishing run must diff against is its own PUBLICATION, never
    `github.event.before`, which silently under-builds after ANY run that did not publish
    (cancelled, superseded, red, re-run). The bake HAS such a marker (`source-commit.txt` sealed
    beside its bundles; bake-scope.sh reads it). THE MODULE LANE HAS NONE, and the registry's
    `{package}@{version}` key is NOT a substitute for one.

    🚨 The reason that key fails has MOVED, and reading the old one wastes a day. It used to be
    "the version identifies only the CONTENT half" — true until Plugins#878 landed, after which
    `gen-manifests.py` hashes a mixed package's own `src/` project into its `moduleVersion` too.
    What remains is narrower and still fatal: the version covers the module's OWN project, while
    the CONTAINER pack path copies every MODULE-OWNED `MeshWeaver.*` sibling INTO the bundle
    (`module-owned-platform.sh`: in this repo's `src/`, and MEASURED as absent from the pinned
    platform host — its app closure, its surface manifest and its seeded `modules/<Name>/` lane;
    MeshWeaver#3732 replaced the declared `src/platform-shipped.txt` reading with that measurement).
    Measured on MeshWeaver.Plugins 2026-09-01:
    `MeshWeaver.Blazor` rides in SEVEN published bundles and not one of their `manifest.lock`s
    hashes a byte of it. So version equality still does not imply byte equality, and "the registry
    already serves this version" is still NOT evidence the published bundle matches HEAD.

    Until this lane stamps a SOURCE COMMIT into what it publishes — the analogue of the bake's
    marker, diffed with `project-closure.py`, which walks transitive in-repo ProjectReferences and
    therefore sees riders for free — a push packs everything. Narrowing on the version instead
    would under-publish exactly those seven. See Doc/Architecture/ModuleVersioning.

So the whole win sits on `pull_request` / `merge_group`, which is exactly where the cost is: a PR
pushes many times, a trunk commit lands once.

═══ WHAT MAKES A MODULE ENTRY REACHABLE — two halves, because a bundle has two ═══

  * the CONTENT half — `{package}/manifest.lock`'s version and `{package}/index.json`'s
    `content.module` + `content.minMeshVersion`, which the lane asserts. The reachable set is the
    caller's own AFFECTED closure: `scripts/affected-modules.py`, the ONE answer this repo family
    already uses for the mesh gate and the bake, INVOKED here rather than reimplemented.
  * the COMPILED half — the module's project, its transitive in-repo `ProjectReference`s, and the
    sibling `.Test` project the lane runs, with the same closure. `affected-modules.py` cannot see
    this: it answers ALL modules for any `src/` path. `scripts/project-closure.py` in the caller
    owns it, and is likewise INVOKED, because at least three consumers must agree on that graph
    (this narrowing, the Plugins #878 version gate, and anything else asking "what does this
    csproj depend on here").

USAGE
    node-repo-scope.py --for modules  --modules @matrix.json --event pull_request --base-ref main
    node-repo-scope.py --self-test

OUTPUT — JSON on stdout, the human report on stderr, plus $GITHUB_OUTPUT / $GITHUB_STEP_SUMMARY.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path

# 🚨 scripts/affected-modules.py's NOOP_DIRS — the top-level dirs a change in which can reach no
# build at all. Every entry NOT in this set falls through to a FULL run, so adding one silently
# un-builds something and dropping one merely costs a full run.
# `clients/` is deliberately absent (the established selector treats it as ALL, and a client asset
# can be embedded by a host). Measured over 120 first-parent merges to MeshWeaver.Plugins' main,
# `clients/` is the largest remaining full-set trigger after `.github/` (11 of 45); the entry stays
# out because this set also feeds the mesh gate and the bake, so moving it is a wider decision than
# the module matrix — see Doc/Architecture/BuildScopeNarrowing.
#
# 🚨 A HAND COPY IS NOT A GUARANTEE, and this one drifted on its first day: `WhatsNew` was missing,
# so a note-only PR ran the entire fleet's module suite — the exact case affected-modules.py added
# it for. `resolve_noop_dirs()` below READS the caller's set and narrows on the INTERSECTION, so a
# drift over-builds the divergent dir out loud instead of silently, and — since 2026-09-08 —
# instead of switching the whole narrowing off (which is what equality did, on every pull request
# three satellites ever opened). A literal it cannot READ is still a refusal.
#
# `devtools/` is the DECLARED home for a script no lane runs — a local dev loop, a triage helper.
# Before it existed such a script could only live in `scripts/`, which is (correctly) the gates'
# directory and therefore EVERYTHING: measured on MeshWeaver.Plugins run 34618468550 (#1668), a
# one-line edit to `scripts/run-node-tests.py` — referenced by no workflow — selected 52 of 52
# compiled projects and every module suite (~89 runner-minutes of portal-host shards + ~200 of
# module tests); the same diff without it selected 1 of 52. The name is NEW on purpose: `tools/`
# exists in MeshWeaver.Plugins and is NOT inert there (`src/Directory.Build.targets` and two test
# projects read it), and a name no repo has yet lands DORMANT in `check-noop-scope-parity.py`
# everywhere, so the platform can declare it first without reddening any caller. What a file
# placed there claims: no compiled project, no module content and no gate reads it.
NOOP_DIRS = {"legacy", "e2e", "docs", "WhatsNew", "app", ".claude", ".worktrees", "devtools"}

# 🚨 A repo-ROOT path HAS NO TOP-LEVEL DIRECTORY, so NOOP_DIRS above can never reach it: both
# no-op branches of the classifier are guarded by `"/" in f`, and a single-segment path takes
# neither. Every root file therefore fell through to the global catch-all and ran the FULL set.
#
# Measured on MeshWeaver.Plugins run 34158493654 (2026-09-07), a pull request whose entire diff
# was one file:
#
#     AGENTS.md  → EVERYTHING (tooling / repo-root / unknown scope)
#     scope: full — 1 changed file(s) reach the tooling/global scope — first: AGENTS.md.
#     34 of 34 selected module(s) still owe a test run: …
#
# 100 jobs, 38 bundles, 34 module suites, ~350 billable job-minutes and ~54 minutes of wall clock
# for a documentation edit — and `AGENTS.md` is the file every agent session in this fleet edits.
# The `docs/` entry above was added for exactly this shape and could not cover it, because the
# fleet keeps its agent guidance at the ROOT rather than under `docs/`.
#
# 🚨 THIS IS A CLOSED LIST OF NAMES, NEVER A PATTERN, and that is the whole safety argument. A
# root file this set does not name is UNKNOWN and still runs the full set — which is what keeps
# `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `nuget.config`, a `*.slnx`
# and (measured across the fleet: `plugin-gate.allow`, `plugin-tests.allow`, `cover-prose.allow`,
# `impersonation.allow`) every gate ALLOW FILE on the loud path where they belong. Adding a name
# here is a deliberate claim that the file cannot change what any lane builds; a `*.md` glob, or
# "any dotfile", would make that claim for files nobody has read yet.
NOOP_FILES = {
    "AGENTS.md", "CLAUDE.md", "README.md",
    "LICENSE", "LICENSE-APACHE", "LICENSE-MIT",
    ".gitignore", ".gitattributes", ".editorconfig",
}

# 🎯 CI PATHS OWE NO MODULE LEG — policy `ci-paths-owe-no-module-leg` (Doc/Architecture/
# BuildScopeNarrowing → "CI paths owe no module leg"). A pull request whose diff is limited to
# `.github/` and `scripts/` runs the CI checks and their self-tests; it owes no module compile-check,
# no bundle and no realm-gate leg, because main's push run (full, or narrowed only by its own
# publication — where a CI path stays EVERYTHING) and the daily full run cover those. Until this rule these paths took the global branch below BEFORE the caller's selector
# was asked, so no caller could narrow them: MeshWeaver.Plugins#4187 taught its selector the policy
# and this lane still packed every module (review thread 4370061382).
#
# 🚨 THE SHAPE IS "RESPECT THE CALLER'S SELECTOR", NEVER "THESE PATHS ARE INERT". A CI path is
# handed to the caller's `affected-modules.py` beside the node-package paths, and the CALLER answers
# whether it reaches a module. That keeps every satellite safe without a co-ordinated change:
#   * a caller with no selector already runs the full set (checked before classification);
#   * a selector that predates the policy answers `runAll` for a CI path, which is a FULL run here;
#   * a selector that knows the policy answers `affected: []` for it — the narrowing.
# A caller that keeps `.github/workflows/ci.yml` loud (it carries the matrix and the pin) says so in
# its own selector. `tools/` is deliberately NOT a prefix: MeshWeaver.Plugins' build reads it
# (`src/Directory.Build.targets`, two test projects), so it stays on the global branch — EVERYTHING —
# whatever any selector would answer.
CI_PATH_PREFIXES = (".github/", "scripts/")

SELECTOR = "scripts/affected-modules.py"        # the caller's NODE graph
PROJECTS = "scripts/project-closure.py"          # the caller's PROJECT graph

# Events whose diff means anything. Everything else runs the full set, each for its own reason.
# 🚨 `pull_request_target` is deliberately ABSENT. With the default checkout it runs at the BASE
# tip, so `origin/<base>...HEAD` is empty and it could only ever take the empty-diff fallback — and
# the day someone adds `ref: github.event.pull_request.head.sha` it would start narrowing on
# fork-controlled content. A lane that cannot narrow should not claim it can.
NARROWABLE = {"pull_request", "merge_group"}

FULL_REASONS = {
    "modules": {
        "push": "a push PUBLISHES, and the only sound baseline is its own publication — which the "
                "module lane does not record. The registry's {package}@{version} key is not a "
                "substitute: the version covers the module's OWN project (Plugins#878), while the "
                "container pack path copies every module-owned MeshWeaver.* sibling INTO the "
                "bundle, so a sibling's change moves the bundle's bytes and not its version. "
                "Diffing github.event.before instead would silently skip every module whose commit "
                "belonged to a cancelled, superseded or red run. Packing every module — the module "
                "build ledger (module-build-ledger.py, when the caller sets ledger: required) then "
                "reuses every entry whose content key is already Published.",
        "repository_dispatch": "a framework-release dispatch moves the platform pin every module is "
                               "built against — every bundle must be rebuilt and republished or "
                               "every portal reads FrameworkDeclined and adopts nothing "
                               "(MeshWeaver#2088). The git diff is irrelevant.",
        "schedule": "the release poll is how this repo learns the platform released; every module "
                    "must be rebuilt and republished against the new framework identity "
                    "(MeshWeaver#2088), so the git diff is irrelevant.",
        "workflow_dispatch": "a manual dispatch has no diff to narrow by — packing every module.",
    },
}


# ── the caller's two graphs, invoked (never reimplemented) ───────────────────────────────────

def _invoke(root: Path, script: str, argv: list[str], say) -> dict | None:
    path = root / script
    if not path.is_file():
        return None
    try:
        proc = subprocess.run([sys.executable, str(path), *argv],
                              cwd=root, capture_output=True, text=True, timeout=600)
    except (OSError, subprocess.SubprocessError) as exc:
        say(f"    {script}: {exc}")
        return None
    if proc.returncode != 0:
        say("    " + (proc.stderr.strip()[-2000:] or f"{script} exited {proc.returncode}"))
        return None
    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError:
        say(f"    {script}: answer is not JSON")
        return None


def affected_packages(root: Path, changed: list[str], say) -> set[str] | None:
    """The caller's affected closure over the given paths. None ⇒ cannot narrow.

    🚨 Its refusals stay load-bearing: a non-zero exit, an unparseable answer, or `runAll` is taken
    as "cannot narrow", never as "nothing affected". It refuses an empty diff for the same reason.
    """
    # A temp file, never a path inside the checkout: this script runs against a tree CI is about
    # to diff, and a stray file in it is a changed file.
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as fh:
        fh.write("\n".join(changed) + "\n")
        listing = Path(fh.name)
    try:
        answer = _invoke(root, SELECTOR, ["--changed", str(listing), "--json"], say)
    finally:
        listing.unlink(missing_ok=True)
    if answer is None or answer.get("runAll") is not False:
        return None
    affected = answer.get("affected")
    return set(affected) if isinstance(affected, list) else None


def _accepts_base(root: Path) -> bool:
    """Whether the caller's project-closure.py declares `--hits --base` (MeshWeaver.Plugins#4314:
    a comment/whitespace-only MSBuild edit owes nothing against that ref). An older script would
    REFUSE the flag (argparse exit 2 ⇒ "cannot narrow" ⇒ full), so it is passed only when declared."""
    try:
        return '"--base"' in (root / PROJECTS).read_text(encoding="utf-8")
    except OSError:
        return False


def project_hits(root: Path, entries: list[str], changed: list[str], say,
                 base: str | None = None) -> dict | None:
    """{entry → the changed project dirs its build+test closure contains} + `unclassified`.
    `base` is the ref the changed paths were diffed from (the PR base, or a push's publication
    baseline); handed on as `--base` only to a project-closure.py that declares it."""
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as fh:
        fh.write("\n".join(changed) + "\n")
        listing = Path(fh.name)
    argv = ["--hits", "--changed", str(listing), "--json"]
    if base and _accepts_base(root):
        argv += ["--base", base]
    for e in entries:
        argv += ["--entry", e]
    try:
        return _invoke(root, PROJECTS, argv, say)
    finally:
        listing.unlink(missing_ok=True)


# ── classification ───────────────────────────────────────────────────────────────────────────

def node_packages(root: Path) -> set[str]:
    """Top-level dirs carrying an index.json — the caller's node packages.

    🚨 An OSError here is NOT caught. "I could not read the tree" must never become "the tree is
    empty": a swallowed error made this function return an empty set, `full()` then answered
    scope=full with count=0, and both lanes reported SUCCESS having built nothing — a green tick
    over a compile gate that compiled zero packages. Letting it raise exits non-zero and fails the
    job, which is the loud version of the same fact.
    """
    # "meshweaver" is CI's checkout of Systemorph/MeshWeaver — gen-manifests.py skips it for the
    # same reason. The select job keeps the two trees apart anyway; this is the belt to that brace,
    # because the day core grows a root index.json is the day the framework becomes a plugin.
    skip = {".git", ".github", ".claude", ".worktrees", "scripts", "src", "test", "docs", "e2e",
            "app", "clients", "legacy", "meshweaver"}
    return {d.name for d in root.iterdir()
            if d.is_dir() and d.name not in skip and (d / "index.json").exists()}


def caller_noop_dirs(root: Path) -> tuple[set[str] | None, str | None]:
    """(the caller's NOOP_DIRS, or None with the reason it could not be READ AT ALL).

    Read out of the caller's source rather than imported: affected-modules.py is a CLI, and
    importing it would run its argparse. A set literal on one line is what it has always been; if
    that ever stops parsing, the honest answer is "cannot verify" — which is also a full run.
    Shared with `check-noop-scope-parity.py`, the fleet guard that reds when this stops parsing.
    """
    try:
        text = (root / SELECTOR).read_text(encoding="utf-8")
    except OSError as exc:
        return None, f"{SELECTOR} could not be read ({exc})"
    match = re.search(r"^NOOP_DIRS\s*=\s*\{([^}]*)\}", text, re.M)
    if not match:
        return None, f"{SELECTOR} has no single-line NOOP_DIRS literal to compare against"
    return set(re.findall(r"[\"']([^\"']+)[\"']", match.group(1))), None


def resolve_noop_dirs(root: Path) -> tuple[frozenset[str] | None, str | None]:
    """(the no-op dirs BOTH sides agree on, a note about any divergence).

    A `None` set means the caller's copy could not be READ AT ALL — the subject moved — and the
    only honest answer to that is a full run. A note beside a set is a cost report, not a refusal.

    🚨 DIVERGENCE USED TO MEAN "NARROW NOTHING", AND THAT SWITCHED NARROWING OFF FLEET-WIDE.
    NOOP_DIRS is HAND-COPIED into every caller repo, so it drifts the moment one side gains an
    entry — and the old equality check answered a drift with a blanket FULL run, forever, visible
    only in a log line inside the very job it disabled. Measured 2026-09-07, MeshWeaver.SocialMedia
    run 34122662676:

        scope: full — this script's NOOP_DIRS has drifted from scripts/affected-modules.py's —
        only theirs: (none); only ours: app.

    …on EVERY pull request since `app` was added here — for a repo that has no `app/` directory at
    all, so not one changed file could ever have been classified by the entry they disagree about.
    MeshWeaver.Manufacturing and MeshWeaver.Reinsurance carry the same divergence today.

    The INTERSECTION is what the safety argument actually needs, and unlike equality it is safe in
    BOTH directions:
      * a dir only WE call inert — the caller believes it reaches its gate, so we must not skip
        it: the intersection drops it and it goes to EVERYTHING;
      * a dir only THEY call inert — we believe it reaches a module, so we must not skip it: the
        intersection drops it and it goes to EVERYTHING.
    Either way the divergent dir is over-built, out loud, and every OTHER dir keeps narrowing.
    "The two must classify a top-level dir identically" was the right requirement expressed as the
    wrong remedy: they need only agree WHERE IT MATTERS, and the intersection makes them agree by
    construction. The residual cost of a real drift is NAMED in the report so it can be closed,
    and `check-noop-scope-parity.py` reds a caller whose divergence actually costs it something.
    """
    theirs, unreadable = caller_noop_dirs(root)
    if theirs is None:
        return None, unreadable
    if theirs == NOOP_DIRS:
        return frozenset(NOOP_DIRS), None
    only_theirs = ", ".join(sorted(theirs - NOOP_DIRS)) or "(none)"
    only_ours = ", ".join(sorted(NOOP_DIRS - theirs)) or "(none)"
    return frozenset(NOOP_DIRS & theirs), (
        f"NOOP_DIRS diverges from {SELECTOR}'s — only theirs: {only_theirs}; only ours: "
        f"{only_ours}. Narrowing on the INTERSECTION, so every divergent dir above is treated as "
        f"EVERYTHING (over-built, never skipped). Align {SELECTOR}'s literal with this script's "
        "to stop paying for it")


def changed_files(root: Path, diff_range: str, say) -> list[str] | None:
    """The changed paths, with renames DECOMPOSED into a delete and an add.

    🚨 `--no-renames` is selection logic, not a formatting flag. Git detects renames by default and
    `--name-only` then prints only the DESTINATION — so a file moved from one module (or one
    csproj) to another names only the module it arrived in, and the one it LEFT is never selected,
    even though its build changed. Under-selection is the failure mode this whole script is biased
    against; decomposing the rename makes both ends appear.
    """
    proc = subprocess.run(["git", "diff", "-z", "--no-renames", "--name-only", diff_range],
                          cwd=root, capture_output=True, text=True, timeout=120)
    if proc.returncode != 0:
        say("    " + proc.stderr.strip()[-2000:])
        return None
    return [p for p in proc.stdout.split("\0") if p]


# ── the caller's module-libraries shelf ──────────────────────────────────────────────────────

_SHELF_INCLUDE = r"<ProjectReference\s[^>]*Include=(?:\"([^\"]+)\"|'([^']+)')"
_SHELF_COMPILE = r"<Compile\s[^>]*Include=(?:\"([^\"]+)\"|'([^']+)')"
# The project-relative MSBuild properties a linked item may start with; any other $(…) names a
# location outside this repository (the platform's), whose bytes the platform digest already keys.


# ── what a caller's build extension / shelf evaluates: MSBuild's OWN answer ──────────────────
# The key (module-build-key.py) and this scope both consume ONE evaluation, written by
# msbuild-evaluate.py, which runs the real `dotnet msbuild` (-pp for the imported files,
# -getItem for the path items, ProjectReference followed) with the lane's publishing inputs. A
# hand-written MSBuild reader here never converged: conditions, Choose, transforms and
# conditional imports were each one more finding. MSBuild answers all of them exactly.
#
# The ONLY additions to MSBuild's answer are conservative extras for HASHING and REACH, never
# evaluation: the implicit `Directory.*` candidates in every ancestor (a NEW one is a change), the
# declared-but-absent import paths and the declared in-repo glob patterns msbuild-evaluate.py
# records. The scope unions HEAD's evaluation with its diff BASE's (a deletion is seen from the
# tree that still had the file).
_IMPLICIT_IMPORTS = ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props")
_IMPLICIT_GLOBS = ("Directory.*.props", "Directory.*.targets")


def implicit_import_candidates(root: Path, csproj: Path) -> list[str]:
    """Repository-relative implicit-import paths (literal, present or not) and per-directory globs."""
    root = root.resolve()
    out: list[str] = []
    d = csproj.resolve().parent
    while True:
        rel = d.relative_to(root).as_posix()
        prefix = "" if rel == "." else rel + "/"
        out += [prefix + n for n in _IMPLICIT_IMPORTS] + [prefix + g for g in _IMPLICIT_GLOBS]
        if d == root or root not in d.parents:
            break
        d = d.parent
    return out


def load_msbuild_evaluation(path: str | Path) -> dict:
    """The JSON msbuild-evaluate.py wrote; ValueError when it is not one."""
    try:
        data = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"--msbuild-evaluation {path} could not be read ({exc})") from None
    if not isinstance(data, dict) or not isinstance(data.get("projects"), dict):
        raise ValueError(f"--msbuild-evaluation {path} is not an msbuild-evaluate.py result (no `projects`)")
    return data


def normalize_project(project: str) -> str:
    """ONE spelling of a repository-relative project path for every consumer (`./a/../b.csproj` and
    `b.csproj` are the same project). msbuild-evaluate.py normalizes the projects it writes with the
    same rule, and module-build-key.py loads this one, so a lookup can never miss on spelling."""
    return Path(os.path.normpath(project.strip().replace("\\", "/"))).as_posix()


def evaluated_closure(evaluation: dict | None, project: str) -> list[str]:
    """The project and every in-repo project it references, transitively, as MSBuild evaluated them.

    ValueError when the project was not evaluated: a named input keyed or scoped without MSBuild's
    answer would be keyed on a guess, and an EMPTY reach for a configured project is always a bug."""
    if evaluation is None:
        raise ValueError(f"{project} is named, but no --msbuild-evaluation was given — its imports and "
                         "items come from MSBuild itself (msbuild-evaluate.py), never from a guess")
    projects = evaluation.get("projects") or {}
    out: list[str] = []
    queue = [normalize_project(project)]
    while queue:
        rel = normalize_project(queue.pop(0))
        if rel in out:
            continue
        if rel not in projects:
            raise ValueError(f"{rel} is not in the MSBuild evaluation (evaluated: {sorted(projects)}) — "
                             "evaluate the project the lane names, with msbuild-evaluate.py")
        out.append(rel)
        queue += list(projects[rel].get("references") or [])
    return out


def evaluated_files(evaluation: dict, rel: str) -> tuple[list[str], list[str], list[str]]:
    """(imports, declaredImports, every path item) of one evaluated project. "Items" includes the
    CONSERVATIVE SWEEP (`swept`): every existing in-repo file the preprocessed project names
    anywhere (a HintPath, a Copy task's SourceFiles, an Exists()), so the key hashes it and the
    scope reaches it exactly like an evaluated item."""
    entry = (evaluation.get("projects") or {})[normalize_project(rel)]
    items = sorted({p for paths in (entry.get("items") or {}).values() for p in paths}
                   | set(entry.get("swept") or []))
    return list(entry.get("imports") or []), list(entry.get("declaredImports") or []), items


# A pattern MSBuild's evaluation could not resolve (a property function, a property only a target
# sets) is recorded as UNKNOWN, and a project carrying one is reached by ANY change: "**" matches
# every path. Fail safe — an unknown input selects too much, never too little.
_EVERYTHING = "**"


def evaluated_reach(root: Path, evaluation: dict | None, project: str) -> set[str]:
    """Everything a diff can touch to change what this project and its references evaluate in ONE
    evaluation: their directories, MSBuild's imports and items, and the conservative extras."""
    root = root.resolve()
    reach: set[str] = set()
    projects = (evaluation or {}).get("projects") or {}
    for rel in evaluated_closure(evaluation, project):
        reach.add(Path(rel).parent.as_posix())
        imports, declared, items = evaluated_files(evaluation, rel)
        reach |= set(imports) | set(declared) | set(items)
        # Declared in-repo GLOBS, properties substituted by MSBuild: a deletion of the last match, or
        # a first match added to a glob that matches nothing yet, is a change no list of today's
        # files can show.
        reach |= set(projects[rel].get("declaredPatterns") or [])
        if projects[rel].get("unknownPatterns"):
            reach.add(_EVERYTHING)
        reach |= set(implicit_import_candidates(root, root / rel))
    return reach


def union_reach(root: Path, evaluations, project: str) -> set[str]:
    """The reach over EVERY evaluation given: the scope passes HEAD's and its diff BASE's (and the
    supersession check this run's and the newer trunk tip's), so a file one tree no longer has is
    seen from the tree that still had it.

    An evaluation that lacks the project RAISES, unless it lists it under `missing` (a tree that
    predates the project, evaluated with --skip-missing). At least one evaluation must hold it."""
    project = normalize_project(project)
    if evaluations is None or isinstance(evaluations, dict):
        evaluations = [evaluations]
    holding = []
    for e in evaluations:
        if e is not None and project in [normalize_project(m) for m in (e.get("missing") or [])]:
            continue  # explicitly absent from that tree
        evaluated_closure(e, project)  # raises on a miss, saying why
        holding.append(e)
    if not holding:
        raise ValueError(f"{project} is absent from every MSBuild evaluation given — a configured project "
                         "with an empty reach would select nothing on a guess")
    reach: set[str] = set()
    for e in holding:
        reach |= evaluated_reach(root, e, project)
    return reach


def is_msbuild_glob(pattern: str) -> bool:
    return "*" in pattern or "?" in pattern


def msbuild_glob_matches(path: str, pattern: str) -> bool:
    """MSBuild item-glob semantics over '/'-separated relative paths.

    `**` as a whole segment matches ZERO or more directories (`a/**/*.cs` matches `a/x.cs`);
    `*` and `?` never cross a '/'. A pattern without wildcards matches only itself."""
    if not is_msbuild_glob(pattern):
        return path == pattern
    segs = pattern.split("/")
    rx = ""
    for i, seg in enumerate(segs):
        last = i == len(segs) - 1
        if seg == "**":
            rx += ".*" if last else "(?:[^/]+/)*"
            continue
        rx += "".join("[^/]*" if c == "*" else "[^/]" if c == "?" else re.escape(c) for c in seg)
        rx += "" if last else "/"
    return re.fullmatch(rx, path) is not None


def shelf_dirs(root: Path, project: str, evaluation=None) -> frozenset[str] | None:
    """Everything a diff can touch to change the caller's shelf / build extension: the project, every
    in-repo project it references, MSBuild's imports and items for each, and the conservative extras
    (`evaluated_reach`). The SAME evaluation feeds module-build-key.py, so key and scope cannot drift.

    None when the project is not in this checkout — the caller of this function reads that as
    "cannot decide", which is a full run. ValueError when it was not evaluated by MSBuild. The shelf
    and the extension are compiler inputs of EVERY module, yet in no module's ProjectReference
    closure, so without this an edit to them reaches no entry and narrows to nothing.
    """
    root = root.resolve()
    project = normalize_project(project)
    if not (root / project).is_file():
        return None
    return frozenset(union_reach(root, evaluation, project))


# ── the decision ─────────────────────────────────────────────────────────────────────────────

def decide(root: Path, lane: str, entries: list[dict], event: str, diff_range: str | None,
           override: list[str] | None, say, publishing: bool = False,
           always: frozenset[str] = frozenset(), publication_base: str = "",
           publication_reach: bool = False, shelf_project: str = "",
           extensions_project: str = "", msbuild_evaluation=None,
           carry: frozenset[str] = frozenset()) -> dict:
    all_packages = sorted(node_packages(root))
    universe = [e["module"] for e in entries]

    divergence: str | None = None

    def full(reason: str) -> dict:
        return {"lane": lane, "scope": "full", "reason": reason,
                "modules": [{**e, "test": True} for e in entries],
                "count": len(universe), "selected": sorted(universe), "skipped": [], "why": {},
                "tested": sorted(universe), "divergence": divergence}

    def refuse(reason: str) -> dict:
        return {"lane": lane, "scope": "refuse", "reason": reason, "modules": [],
                "count": 0, "selected": [], "skipped": [], "why": {},
                "tested": [], "divergence": divergence}

    # 🚨 THE ANSWER "BUILD EVERYTHING, AND EVERYTHING IS NOTHING" IS NOT AN ANSWER. `full` with a
    # count of 0 is an internal contradiction, and it is REACHABLE: a --root that does not match
    # the checkout path, a matrix input of `[]`, a tree that could not be listed. Every consumer
    # keys on the count alone, so it sailed through as a green tick over a lane that built nothing
    # — the precise failure this script exists to prevent. It is a refusal, not a scope.
    if not universe:
        return refuse(
            f"there is nothing to select from: {'the modules input is empty' if lane == 'modules' else f'no node package was found under {root}'}. "
            "'Build everything' and 'everything is nothing' cannot both be true, so this is a "
            "broken input (a --root that does not match the checkout?), not a scope.")

    # Only an ancestor from a successful publishing run can narrow a main push. Never use
    # event.before: it loses changes from failed/cancelled runs. Other events retain full scope.
    publication_diff = False
    if event == "push" and publication_base:
        if not re.fullmatch(r"[0-9a-f]{40}", publication_base):
            return full("publication baseline is not a full commit SHA")
        ancestor = subprocess.run(["git", "merge-base", "--is-ancestor", publication_base, "HEAD"],
                                  cwd=root, capture_output=True, timeout=30)
        if ancestor.returncode != 0:
            return full("publication baseline is unavailable or not an ancestor of HEAD")
        publication_diff = True
        diff_range = f"{publication_base}..HEAD"
        override = None  # a publishing decision must read the actual tree, never a supplied list
    if publishing and not publication_diff:
        return full("this run PUBLISHES, and a publishing run is never narrowed — the derived "
                    "version is the change detector on that path, and a diff that misses a unit "
                    "means that unit silently never ships.")
    if event not in NARROWABLE and not publication_diff:
        return full(FULL_REASONS[lane].get(
            event, f"event '{event}' has no meaningful content diff — running the full set."))
    if not (root / SELECTOR).is_file():
        return full(f"the caller repo ships no {SELECTOR}, so the affected closure cannot be "
                    "computed — running the full set.")
    noop_dirs, divergence = resolve_noop_dirs(root)
    if noop_dirs is None:
        # The caller's copy could not be READ — its subject moved. "Cannot verify" is a full run.
        return full(f"{divergence} — running the full set.")

    if override is not None:
        files = [f for f in override if f.strip()]
    else:
        if publication_diff:
            # A failed run may have published SOME bundles. Include every intervening change,
            # even one later reverted, so HEAD repairs those partial publications too.
            history = subprocess.run(["git", "log", "-z", "--format=", "--name-only", "--no-renames",
                                      "-m", diff_range], cwd=root, capture_output=True,
                                     text=True, timeout=120)
            got = sorted(set(history.stdout.split("\0")) - {""}) if history.returncode == 0 else None
        else:
            got = changed_files(root, diff_range or "", say)
        if got is None:
            return full(f"git diff --name-only {diff_range} failed (see above) — the changed set "
                        "is unknown, so the full set runs.")
        files = got
    # 🚨 A CI diff is never empty; an empty one is a broken range (an unfetched base). Answering
    # "nothing to do" to it would skip the whole lane under a green tick.
    if not files:
        return full(f"the diff for '{diff_range or '--changed'}' came back EMPTY — a CI diff is "
                    "never empty, so this is a broken range (is the base ref fetched?), not "
                    "'nothing to do'. Running the full set.")

    # 🚨 The caller's own module-libraries shelf decides what EVERY container-built module binds,
    # but no module references it, so the project closure below would rule a shelf edit inert.
    # A build extension (`build-extensions-project`, e.g. the Razor one) is the same shape: it decides
    # what the compiler GENERATES for every module, and no module references it.
    for input_name, label, project, consequence in (
            ("module-libraries-project", "module-libraries shelf", shelf_project,
             "the shelf is what every module binds"),
            ("build-extensions-project", "build extension", extensions_project,
             "the extension decides what every module's compile generates")):
        if not project:
            continue
        try:
            dirs_ = shelf_dirs(root, project, msbuild_evaluation)
        except ValueError as exc:
            return refuse(f"{input_name} {project}: {exc}")
        if dirs_ is None:
            return full(f"{input_name} {project} is not in this checkout, so whether this diff "
                        f"changes the {label} cannot be decided — running the full set.")
        touched = [f for f in files
                   if any(f.startswith(d + "/") or msbuild_glob_matches(f, d) for d in dirs_)]
        if touched:
            # Name the reach entry that matched: "first: <a test file>" alone could not say WHY a file
            # the shelf never compiles counted (an import that resolved to the `src` directory did).
            via = sorted(d for d in dirs_ if touched[0].startswith(d + "/") or msbuild_glob_matches(touched[0], d))
            return full(f"{len(touched)} changed file(s) are in the caller's {label} "
                        f"({project} and the projects it reaches) — first: {touched[0]} "
                        f"(reached through {', '.join(via[:3])}). "
                        f"{consequence[0].upper() + consequence[1:]}, so every module is rebuilt and tested.")

    packages = node_packages(root)
    node_files: list[str] = []
    ci_files: list[str] = []
    src_files: list[str] = []
    global_files: list[str] = []
    report: list[str] = []
    for f in files:
        top = f.split("/", 1)[0]
        if "/" in f and top in packages:
            node_files.append(f)
            report.append(f"  {f}  → node package {top}")
        elif "/" in f and top in noop_dirs:
            report.append(f"  {f}  → (reaches nothing this lane builds — {top}/)")
        # 🚨 A repo-ROOT path reaches neither branch above — both require a "/" — so before this
        # entry every one of them fell through to EVERYTHING (see NOOP_FILES). The list is closed:
        # an unnamed root file is still UNKNOWN and still runs the full set, which is what keeps
        # every gate `*.allow`, Directory.Build.props and global.json on the loud path.
        elif "/" not in f and f in NOOP_FILES:
            report.append(f"  {f}  → (reaches nothing any lane builds — a repo-root no-op file)")
        # 🎯 policy `ci-paths-owe-no-module-leg`: a CI path is the CALLER's call (see
        # CI_PATH_PREFIXES) — deferred to its selector, never ruled inert here.
        # 🚨 ONLY on a pull request / merge group. A main push narrowed by `--publication-base`
        # IS "main's push run" the policy relies on to cover the modules: the caller's workflow
        # carries the platform pin and the image digests, so there a CI path stays EVERYTHING.
        # 🚨 And NEVER for a PUBLICATION-REACH question (`--publication-reach`): the
        # newest-wins supersession check asks whether a newer trunk commit reaches a module, and
        # a newer CI-only commit changes the workflow and build inputs that commit's own run packs
        # with. Answering "nothing" there lets an older, slower run publish over the newer one.
        elif f.startswith(CI_PATH_PREFIXES) and not publication_diff and not publication_reach:
            ci_files.append(f)
            report.append(f"  {f}  → a CI path — the caller's selector decides "
                          "(policy ci-paths-owe-no-module-leg)")
        elif f.startswith("src/") and f.count("/") >= 2 and lane == "modules":
            src_files.append(f)
            report.append(f"  {f}  → src/ (which project decides, below)")
        else:
            global_files.append(f)
            report.append(f"  {f}  → EVERYTHING (tooling / repo-root / unknown scope)")
    for line in report:
        say(line)

    if global_files:
        return full(f"{len(global_files)} changed file(s) reach the tooling/global scope — first: "
                    f"{global_files[0]}. Running the full set.")

    affected: set[str] = set()
    if node_files or ci_files:
        answer = affected_packages(root, node_files + ci_files, say)
        if answer is None:
            # 🚨 For a CI path this is the EXPECTED answer from a selector that predates policy
            # ci-paths-owe-no-module-leg — it answers ALL — and the full set is the safe reading.
            ci_note = (f" A selector that predates policy ci-paths-owe-no-module-leg answers ALL "
                       f"for a CI path ({len(ci_files)} here, first: {ci_files[0]}); teach it the "
                       "policy to narrow them.") if ci_files else ""
            return full(f"{SELECTOR} could not narrow the node-package closure (it refused, "
                        f"answered ALL, or could not be parsed) — running the full set.{ci_note}")
        affected = answer

    # ── the modules lane also needs the COMPILED half ──
    # An entry built from outside this checkout (a platform-hosted transition entry) can never be
    # shown unaffected by this repo's diff.
    # 🚨 EVERY ENTRY MUST BE REACHABLE IN PRINCIPLE BEFORE ANY OF THEM IS RULED OUT, and this
    # check is UNCONDITIONAL — not behind `if src_files`, or a diff that touches no src/ path
    # would never look at the entry list at all.
    #
    # A stale entry (a renamed csproj, a package that no longer exists) used to be caught on the
    # first PR, because every PR built every entry and went red immediately. Narrowed, it is
    # simply never selected: the closure cannot reach a project that is not there, so the entry
    # goes quiet and the failure defers to the next `push` on main — which is exactly where this
    # repo's policy says the cost must not land. Two direct filesystem facts, no graph needed.
    for e in entries:
        project = e.get("project", "")
        if not project.startswith("src/"):
            return full(f"matrix entry '{e.get('module')}' builds {project}, which is outside "
                        "this checkout's src/ — its reachability cannot be decided from this "
                        "diff. Packing every module.")
        if not (root / project).is_file():
            return full(f"matrix entry '{e.get('module')}' names {project}, which does not exist "
                        "in this checkout — the matrix and the tree have drifted, and a narrowed "
                        "run would simply never select it. Packing every module, so the entry "
                        "fails on THIS pull request rather than on the next push to main.")
        if e.get("package") not in all_packages:
            return full(f"matrix entry '{e.get('module')}' names package '{e.get('package')}', "
                        "which is not a node package in this checkout (no index.json) — the "
                        "matrix and the tree have drifted. Packing every module.")

    hits: dict[str, list[str]] = {e["project"]: [] for e in entries}
    if src_files:
        # The ref the diff is FROM (`origin/<base>` of `origin/<base>...HEAD`) — so an inert MSBuild
        # edit selects nothing here either. 🚨 NEVER on a publication diff: that list is the HISTORY
        # union (it keeps a file a later commit reverted, so HEAD repairs a partial publication),
        # and judging it against the baseline would rule the reverted file inert because HEAD
        # matches the baseline — dropping exactly the module the repair owes.
        diff_from = (diff_range.split("..", 1)[0]
                     if diff_range and not publication_diff else None)
        answer = project_hits(root, sorted(hits), src_files, say, diff_from)
        if answer is None:
            return full(f"the caller repo's {PROJECTS} could not answer the compiled-half closure "
                        f"for {len(src_files)} src/ file(s) — packing every module.")
        unclassified = answer.get("unclassified") or []
        if unclassified:
            return full(f"{len(unclassified)} changed path(s) under src/ belong to no project — "
                        f"first: {unclassified[0]}. Such a file (Directory.Build.props, "
                        "platform-shipped.txt, a renamed project) can change what EVERY project "
                        "builds. Packing every module.")
        got = answer.get("entries")
        if not isinstance(got, dict) or set(got) != set(hits):
            return full(f"{PROJECTS} answered for {len(got or {})} of {len(hits)} entries — "
                        "packing every module.")
        hits = {k: list(v) for k, v in got.items()}

    # 🚨 THE FLOOR IS NOT A SELECTION. The caller's gates COMPOSE a fixed set of module bundles
    # (the AI engine that registers Skill/Agent; the carved-out collaboration module) from THIS
    # run's artifacts, so those bundles must exist on every run regardless of the diff. A
    # narrowing that dropped them made a Hosting-only PR red on both required gates with "the
    # modules job's artifact matched nothing" — an input the workflow itself failed to produce
    # (Plugins #915, 2026-08-29). A floor name the matrix does not carry is a misconfiguration
    # that must be loud, and the safe bias is the full set.
    known = {e["module"] for e in entries}
    unknown_floor = sorted(always - known)
    if unknown_floor:
        return full(f"the caller's always-modules floor names {', '.join(unknown_floor)}, which "
                    "the module matrix does not carry — the floor and the matrix have drifted. "
                    "Packing every module.")

    # 🚨 THE CARRY IS NOT OPTIONAL ONCE GIVEN. A push narrowed by a PARTIAL publication
    # (node-repo-publication-base.py --carry-withheld) names the packages that baseline run
    # WITHHELD: built, tested, never handed over. Their sources may not change again for days, so
    # the diff alone would never select them and they would never ship. They are selected here on
    # top of the diff until a run publishes them (that run's marker no longer names them).
    carried = carry if publication_diff else frozenset()
    selected: list[dict] = []
    why: dict[str, str] = {}
    for e in entries:
        reasons = []
        if e["package"] in carried:
            reasons.append(f"package `{e['package']}` was WITHHELD by the publication baseline run — "
                           "carried until a run publishes it")
        if e["package"] in affected:
            reasons.append(f"node package `{e['package']}` is in the affected closure")
        if hits.get(e["project"]):
            reasons.append("compiled closure reaches " + ", ".join(f"`{p}`"
                                                                   for p in hits[e["project"]]))
        floor_only = e["module"] in always and not reasons
        if e["module"] in always:
            reasons.append("always built: the caller's gates compose this bundle on every run")
        if reasons:
            why[e["module"]] = "; ".join(reasons)
            # 🚨 A floor-only entry is BUILT (the gates compose it) but NOT TESTED: nothing in this
            # diff can reach it, so its suite would answer a question nobody asked — the AI engine's
            # 1,600 tests cost ~13 min per PR that never touched AI (maintainer, 2026-08-29: "the ai
            # test suite should be executed once. obviously. And only when we touch AI"). A diff
            # that reaches the entry keeps test=true, and a full run tests everything.
            selected.append({**e, "test": not floor_only})

    chosen = {e["module"] for e in selected}
    # 🚨 THE SECOND DENOMINATOR. `selected` answers what is BUILT; a floor-only entry is built and
    # deliberately not tested, so "how many suites will run" is a different number and a reader
    # who only sees the first cannot tell "nothing needed testing" from "the filter is broken and
    # matched nothing". Both counts are stated, with names, at the point the decision is made.
    tested = sorted(e["module"] for e in selected if e.get("test") is not False)
    return {"lane": lane, "scope": "narrowed",
            "reason": f"{len(selected)} of {len(entries)} module bundle(s) are reachable from this "
                      f"diff ({len(files)} file(s)); {len(tested)} of those owe a test run.",
            "modules": selected, "count": len(selected),
            "selected": sorted(chosen), "skipped": sorted(set(universe) - chosen), "why": why,
            "tested": tested, "divergence": divergence}


# ── self-test ────────────────────────────────────────────────────────────────────────────────
# 🚨 THIS SCRIPT DECIDES WHAT DOES NOT GET BUILT, so it owes its own proof that it can say no AND
# that every fallback still falls back. The fixture is a miniature plugin repo: two node packages
# (Beta ← Alpha, a dependent edge), a five-project src/ graph whose entry reaches a shared library
# and whose sibling .Test reaches a project the entry does not, and a two-entry matrix.

_ENTRY_A = {"package": "Alpha", "module": "Acme.Alpha",
            "project": "src/Acme.Alpha/Acme.Alpha.csproj"}
_ENTRY_B = {"package": "Beta", "module": "Acme.Beta",
            "project": "src/Acme.Beta/Acme.Beta.csproj"}
_MATRIX = [_ENTRY_A, _ENTRY_B]

# The fixture project graph the stub answers from — entry closures already resolved, so the stub
# is obviously a fixture rather than a second implementation of the real graph.
_STUB_PROJECTS = (
    "import json,sys\n"
    "CLOSURE={'src/Acme.Alpha/Acme.Alpha.csproj':"
    "{'src/Acme.Alpha','src/Acme.Shared','src/Acme.Alpha.Test','src/Acme.Kit'},"
    "'src/Acme.Beta/Acme.Beta.csproj':{'src/Acme.Beta'}}\n"
    # Acme.Other is a KNOWN project in NO entry's closure — so a path in it is classified (not
    # `unclassified`, which would force a full run) and yet selects nothing. That is what makes
    # the rename case able to tell "both ends" from "destination only".
    "KNOWN={p for c in CLOSURE.values() for p in c}|{'src/Acme.Other'}\n"
    "a=sys.argv\n"
    "entries=[a[i+1] for i,v in enumerate(a) if v=='--entry']\n"
    "paths=[l for l in open(a[a.index('--changed')+1]).read().splitlines() if l]\n"
    "own=lambda f: next((k for k in KNOWN if f==k or f.startswith(k+'/')), None)\n"
    "hit={own(f) for f in paths if own(f)}\n"
    "unc=sorted(f for f in paths if f.startswith('src/') and not own(f))\n"
    "json.dump({'projects':sorted(KNOWN),'changedProjects':sorted(hit),'unclassified':unc,"
    "'entries':{e:sorted(CLOSURE.get(e,set())&hit) for e in entries}},sys.stdout)\n")

# 🚨 The stub carries a NOOP_DIRS literal because the real selector does and this script now
# READS it — a fixture without one would make every case take the drift fallback.
_STUB_SELECTOR = (
    'NOOP_DIRS = {"legacy", "e2e", "docs", "WhatsNew", "app", ".claude", ".worktrees", "devtools"}\n'
    "import json,sys\n"
    "paths=[l for l in open(sys.argv[sys.argv.index('--changed')+1]).read().splitlines() if l]\n"
    # policy ci-paths-owe-no-module-leg, as MeshWeaver.Plugins#4187's selector answers it: a CI
    # path reaches no package. Any OTHER non-package path (e.g. `tools/`) would also reach none in
    # this stub — which is what makes "tools/ ⇒ ALL" prove the CORE classifier keeps it loud.
    "paths=[p for p in paths if not p.startswith(('.github/','scripts/'))]\n"
    "pk=sorted({p.split('/')[0] for p in paths})\n"
    # Alpha is Beta's dependent in the fixture graph, exactly as a `requires` edge would make it.
    "aff=sorted(set(pk)|({'Alpha'} if 'Beta' in pk else set()))\n"
    "json.dump({'runAll':False,'affected':aff,'mount':aff,'skipped':[],'support':[]},sys.stdout)\n")


# A selector that PREDATES policy ci-paths-owe-no-module-leg — every satellite's until it adopts
# the policy: a CI path is ALL to it, exactly as MeshWeaver.Plugins' main answered before #4187.
_STUB_SELECTOR_PRE_POLICY = _STUB_SELECTOR.replace(
    "paths=[p for p in paths if not p.startswith(('.github/','scripts/'))]\n",
    "_ci=[p for p in paths if p.startswith(('.github/','scripts/'))]\n"
    "_ci and (json.dump({'runAll':True,'affected':[]},sys.stdout) or sys.exit(0))\n")
assert _STUB_SELECTOR_PRE_POLICY != _STUB_SELECTOR, "the pre-policy stub must differ from the stub"


def _csproj(refs: list[str]) -> str:
    return "<Project><ItemGroup>%s</ItemGroup></Project>\n" % "".join(
        f'<ProjectReference Include="{r}" />' for r in refs)


def _fixture(root: Path) -> None:
    (root / "scripts").mkdir(parents=True, exist_ok=True)
    (root / "scripts" / "affected-modules.py").write_text(_STUB_SELECTOR, encoding="utf-8")
    # 🚨 A CONTRACT STUB, deliberately — not a copy of the caller's project-closure.py. What this
    # script depends on is that file's CONTRACT (exit code + `entries`/`unclassified` JSON), and
    # that is what the fixture varies; the graph's own semantics (transitive edges, the .Test hop,
    # $(MeshWeaverRoot) not being an edge) are pinned by its own --self-test, in the repo that owns
    # it. Copying it here would be the second implementation this whole design exists to avoid.
    (root / "scripts" / "project-closure.py").write_text(_STUB_PROJECTS, encoding="utf-8")
    for pkg in ("Alpha", "Beta", "Gamma"):
        (root / pkg).mkdir(exist_ok=True)
        (root / pkg / "index.json").write_text('{"id":"%s"}\n' % pkg, encoding="utf-8")
    for name, refs in (
            ("Acme.Alpha", ["../Acme.Shared/Acme.Shared.csproj"]),
            ("Acme.Alpha.Test", ["../Acme.Alpha/Acme.Alpha.csproj",
                                 "../Acme.Kit/Acme.Kit.csproj"]),
            ("Acme.Beta", []), ("Acme.Shared", []), ("Acme.Kit", []),
            # referenced by NO matrix entry — the destination of the rename case below, chosen so
            # that "both ends" and "destination only" give DIFFERENT answers.
            ("Acme.Other", [])):
        d = root / "src" / name
        d.mkdir(parents=True, exist_ok=True)
        (d / f"{name}.csproj").write_text(_csproj(refs), encoding="utf-8")
    (root / "src" / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
    (root / "docs").mkdir(exist_ok=True)
    (root / "README.md").write_text("x\n", encoding="utf-8")


def _raw(root: Path, lane: str, event: str, changed: list[str] | None,
         matrix: list[dict] | None = None, extra: list[str] | None = None):
    argv = [sys.executable, str(Path(__file__).resolve()), "--for", lane,
            "--root", str(root), "--event", event]
    if lane == "modules":
        argv += ["--modules", json.dumps(_MATRIX if matrix is None else matrix)]
    if changed is not None:
        argv += ["--changed-list", ",".join(changed)]
    argv += extra or []
    return subprocess.run(argv, capture_output=True, text=True, timeout=600)


def _run(root: Path, lane: str, event: str, changed: list[str] | None,
         matrix: list[dict] | None = None, extra: list[str] | None = None) -> dict:
    proc = _raw(root, lane, event, changed, matrix, extra)
    if proc.returncode != 0:
        raise SystemExit(f"self-test harness: exit {proc.returncode}\n{proc.stderr}")
    return json.loads(proc.stdout)


def self_test() -> int:
    import tempfile
    failures: list[str] = []
    ran = 0

    def check(name: str, ok: bool, detail: str = "") -> None:
        nonlocal ran
        ran += 1
        print(f"  {'✓' if ok else '✗'} {name}{'' if ok else ': ' + detail}")
        if not ok:
            failures.append(name)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        _fixture(root)

        # Real history: a failed intermediate publication followed by a revert still owes
        # a publish. A net diff would incorrectly select nothing here.
        def git(*args):
            return subprocess.run(["git", *args], cwd=root, capture_output=True, text=True,
                                  check=True).stdout.strip()
        git("init", "-q")
        git("config", "user.name", "Scope Test")
        git("config", "user.email", "scope@example.test")
        git("add", ".")
        git("commit", "-qm", "baseline")
        base = git("rev-parse", "HEAD")
        node = root / "Alpha" / "index.json"
        before = node.read_text()
        node.write_text(before + "\n")
        git("commit", "-qam", "intermediate partial publication")
        node.write_text(before)
        git("commit", "-qam", "restore original content")
        result = _run(root, "modules", "push", None,
                      extra=["--publication-base", base, "--publishing"])
        check("a reverted partial publication is repaired", result["selected"] == ["Acme.Alpha"])
        # THE CARRY (node-repo-publication-base.py --carry-withheld): the packages a PARTIAL
        # baseline withheld are selected on top of the diff — Beta did not change since `base`.
        result = _run(root, "modules", "push", None,
                      extra=["--publication-base", base, "--publishing", "--carry", "Beta Gamma"])
        beta = next((m for m in result["modules"] if m["module"] == "Acme.Beta"), {})
        check("a package the baseline WITHHELD is carried into a push narrowed by it, and tested",
              result["selected"] == ["Acme.Alpha", "Acme.Beta"] and beta.get("test") is True
              and "WITHHELD" in result["why"].get("Acme.Beta", ""),
              f"selected={result['selected']} why={result['why']}")
        check("NEGATIVE CONTROL: the same push without a carry selects only the diff",
              _run(root, "modules", "push", None,
                   extra=["--publication-base", base, "--publishing"])["selected"] == ["Acme.Alpha"])
        got = _run(root, "modules", "pull_request", ["Alpha/index.json"], extra=["--carry", "Beta"])
        check("a carry on a pull request (no publication baseline) selects nothing extra",
              got["selected"] == ["Acme.Alpha"], f"selected={got['selected']}")
        bad = _raw(root, "modules", "push", None,
                   extra=["--publication-base", base, "--publishing", "--carry", "Be;ta"])
        check("a carry that is not a package id is refused (non-zero), never ignored",
              bad.returncode != 0, f"exit {bad.returncode}")
        # 🚨 policy ci-paths-owe-no-module-leg lets a PULL REQUEST skip module legs for a CI path
        # because main's push run covers them — so that push run must never take the same shortcut.
        # The history also carries the Alpha change above, so deferring the CI path to the selector
        # would answer `narrowed [Acme.Alpha]` here, not `full`.
        wf = root / ".github" / "workflows" / "ci.yml"
        wf.parent.mkdir(parents=True, exist_ok=True)
        wf.write_text("on: push\n", encoding="utf-8")
        git("add", ".")
        git("commit", "-qm", "move the platform pin")
        result = _run(root, "modules", "push", None,
                      extra=["--publication-base", base, "--publishing"])
        check("a CI path on a main push narrowed by its publication is still FULL",
              result["scope"] == "full" and result["count"] == 2,
              f"scope={result['scope']} selected={result['selected']}")
        for event in ("schedule", "repository_dispatch", "workflow_dispatch"):
            result = _run(root, "modules", event, None, extra=["--publication-base", base])
            check(f"{event} ignores publication baseline", result["scope"] == "full")
        for invalid in ("short", "f" * 40):
            result = _run(root, "modules", "push", None, extra=["--publication-base", invalid])
            check("invalid or unavailable baseline builds full", result["scope"] == "full")
        result = _run(root, "modules", "push", None,
                      extra=["--publication-base", git("rev-parse", "HEAD")])
        check("empty publication range builds full", result["scope"] == "full")

        print("FULL-run fallbacks — the bias that makes narrowing safe (both lanes):")
        for lane, n in (("modules", 2),):
            for event, label in (
                    ("push", "a push has no sound publication baseline / is the publish path"),
                    ("repository_dispatch", "a framework release rebuilds everything"),
                    ("schedule", "the release poll rebuilds everything"),
                    ("workflow_dispatch", "a manual dispatch has no diff"),
                    ("issue_comment", "an unrecognised event")):
                got = _run(root, lane, event, ["Alpha/index.json"])
                check(f"[{lane}] {label} ({event})",
                      got["scope"] == "full" and got["count"] == n,
                      f"scope={got['scope']} count={got['count']}")
            for label, changed in (
                    # 🚨 Each of these is a repo-ROOT path NOOP_FILES deliberately does NOT name,
                    # and each is a real file in this fleet: two build-input files that change
                    # what every project compiles, and the gate ALLOW files every satellite keeps
                    # at its root. The narrowing must never reach them.
                    ("an UNNAMED repo-ROOT file (a build input) ⇒ ALL",
                     ["Directory.Build.props"]),
                    ("a repo-ROOT global.json ⇒ ALL", ["global.json"]),
                    ("a repo-ROOT gate ALLOW file ⇒ ALL", ["plugin-gate.allow"]),
                    ("a repo-ROOT file nobody has classified yet ⇒ ALL", ["brand-new-thing.toml"]),
                    # 🚨 `tools/` is build input in MeshWeaver.Plugins — never a CI path, and the
                    # fixture's selector would call it inert, so only core's classifier keeps it here.
                    ("tools/ (read by the build) ⇒ ALL", ["tools/monaco/build.js"]),
                    ("an UNKNOWN top-level dir (a deleted package) ⇒ ALL", ["Gone/index.json"]),
                    ("an EMPTY diff is a broken range, never 'nothing to do'", [])):
                got = _run(root, lane, "pull_request", changed)
                check(f"[{lane}] {label}", got["scope"] == "full" and got["count"] == n,
                      f"scope={got['scope']} count={got['count']} — {got['reason']}")
        for label, changed in (
                # 🚨 This one does NOT go through `unclassified`: `src/Directory.Build.props` has a
                # single slash, so it never reaches the project graph at all — it is the
                # TWO-SLASH GUARD that sends it to the global scope. Labelled for what it pins,
                # because it was labelled for what the case below pins and neither was tested.
                ("a file directly under src/ (one slash: Directory.Build.props) ⇒ ALL, without "
                 "ever consulting the project graph", ["src/Directory.Build.props"]),
                ("src/<dir> holding no csproj ⇒ ALL (via `unclassified`)", ["src/Loose/notes.txt"]),
                ("a project that no longer exists (renamed/deleted) ⇒ ALL (via `unclassified`)",
                 ["src/Acme.Gone/Gone.cs"])):
            got = _run(root, "modules", "pull_request", changed)
            check(f"[modules] {label}", got["scope"] == "full" and got["count"] == 2,
                  f"scope={got['scope']} — {got['reason']}")

        print("narrowed — the CONTENT half (the caller's affected closure, invoked not copied):")
        got = _run(root, "modules", "pull_request", ["Alpha/index.json"])
        check("[modules] a node package selects its own bundle and no other",
              got["selected"] == ["Acme.Alpha"], f"{got['selected']}")
        got = _run(root, "modules", "pull_request", ["Beta/Board.json"])
        check("[modules] a node package's DEPENDENT is selected too",
              got["selected"] == ["Acme.Alpha", "Acme.Beta"], f"{got['selected']}")

        print("the floor — bundles the caller's gates compose on EVERY run:")
        got = _run(root, "modules", "pull_request", ["docs/guide.md"], extra=["--always", "Acme.Alpha"])
        check("[modules] a diff reaching nothing still builds the floor, and only the floor",
              got["scope"] == "narrowed" and got["selected"] == ["Acme.Alpha"] and got["count"] == 1
              and "always built" in got["why"].get("Acme.Alpha", ""),
              f"{got['scope']} {got['selected']} why={got['why']}")
        got = _run(root, "modules", "pull_request", ["Beta/Board.json"], extra=["--always", "Acme.Alpha"])
        check("[modules] the floor never shrinks a selection that already reaches it",
              got["selected"] == ["Acme.Alpha", "Acme.Beta"], f"{got['selected']}")
        got = _run(root, "modules", "pull_request", ["docs/guide.md"], extra=["--always", "Acme.Alpha"])
        check("[modules] a floor-only entry is built but NOT tested (test=false)",
              got["modules"][0].get("test") is False, f"{got['modules']}")
        got = _run(root, "modules", "pull_request", ["Alpha/index.json"], extra=["--always", "Acme.Alpha"])
        check("[modules] an entry the diff reaches is tested even when it is also the floor",
              got["modules"][0].get("test") is True, f"{got['modules']}")
        got = _run(root, "modules", "push", None, extra=["--always", "Acme.Alpha"])
        check("[modules] a full run tests every entry",
              all(m.get("test") is True for m in got["modules"]), f"{got['modules']}")
        got = _run(root, "modules", "pull_request", ["docs/guide.md"], extra=["--always", "Acme.Nope"])
        check("[modules] a floor naming a module the matrix lacks is LOUD: full set, reason names it",
              got["scope"] == "full" and "Acme.Nope" in got["reason"], f"{got['scope']} {got['reason'][:80]}")

        print("narrowed — the COMPILED half (the graph affected-modules.py answers ALL for):")
        for label, changed, expect in (
                ("a module's own project selects only that module", ["src/Acme.Beta/T.cs"],
                 ["Acme.Beta"]),
                ("a shared library selects every module whose closure references it",
                 ["src/Acme.Shared/S.cs"], ["Acme.Alpha"]),
                ("the sibling .Test project the lane RUNS is in the closure",
                 ["src/Acme.Alpha.Test/T.cs"], ["Acme.Alpha"]),
                ("a project reached only THROUGH the .Test project still selects",
                 ["src/Acme.Kit/K.cs"], ["Acme.Alpha"]),
                ("both halves union", ["src/Acme.Beta/T.cs", "Alpha/index.json"],
                 ["Acme.Alpha", "Acme.Beta"])):
            got = _run(root, "modules", "pull_request", changed)
            check(f"[modules] {label}",
                  got["scope"] == "narrowed" and got["selected"] == expect, f"{got['selected']}")

        print("the legitimate EMPTY answer — explicit, never a missing input:")
        for lane in ("modules",):
            got = _run(root, lane, "pull_request", ["docs/guide.md"])
            check(f"[{lane}] a diff reaching nothing selects ZERO, and says so",
                  got["scope"] == "narrowed" and got["count"] == 0 and got["skipped"],
                  f"scope={got['scope']} count={got['count']}")

        print("CI paths owe no module leg (policy ci-paths-owe-no-module-leg) — the caller's call:")
        ci_only = [".github/workflows/ci.yml", ".github/scripts/helper.py",
                   "scripts/gen-manifests.py"]
        got = _run(root, "modules", "pull_request", ci_only)
        check("[modules] a CI-only diff packs ZERO bundles and tests nothing",
              got["scope"] == "narrowed" and got["count"] == 0 and got["tested"] == []
              and got["skipped"] == ["Acme.Alpha", "Acme.Beta"],
              f"scope={got['scope']} count={got['count']} — {got['reason'][:90]}")
        got = _run(root, "modules", "pull_request", [".github/workflows/ci.yml", "Alpha/index.json"])
        check("[modules] a CI path beside a node package selects exactly that module",
              got["scope"] == "narrowed" and got["selected"] == ["Acme.Alpha"]
              and got["tested"] == ["Acme.Alpha"], f"{got['scope']} {got['selected']}")
        got = _run(root, "modules", "pull_request", ["scripts/gen-manifests.py", "src/Acme.Beta/T.cs"])
        check("[modules] a CI path beside a module's project selects exactly that module",
              got["scope"] == "narrowed" and got["selected"] == ["Acme.Beta"], f"{got['selected']}")
        got = _run(root, "modules", "pull_request", ci_only, extra=["--always", "Acme.Alpha"])
        check("[modules] a CI-only diff with a floor builds only the floor and TESTS NOTHING",
              got["scope"] == "narrowed" and got["selected"] == ["Acme.Alpha"]
              and got["tested"] == [], f"{got['selected']} tested={got.get('tested')}")
        got = _run(root, "modules", "pull_request", [".github/workflows/ci.yml", "tools/x/y.json"])
        check("[modules] tools/ beside a CI path still runs the full set, naming tools/",
              got["scope"] == "full" and got["count"] == 2 and "tools/x/y.json" in got["reason"],
              f"scope={got['scope']} — {got['reason'][:90]}")
        got = _run(root, "modules", "push", ci_only)
        check("[modules] a CI-only PUSH is still FULL (main's push run covers the modules)",
              got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']}")
        # 🚨 The newest-wins supersession check (node-repo-module-pack.yml, `publish-newest-only`)
        # calls this script with `--event pull_request` over the trunk tip's diff. A newer CI-only
        # commit must REACH every module there, or an older run publishes over the newer one.
        got = _run(root, "modules", "pull_request", ci_only, extra=["--publication-reach"])
        check("[modules] PUBLICATION REACH: a newer CI-only commit reaches every module",
              got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']} count={got['count']}")
        got = _run(root, "modules", "pull_request", ["Alpha/index.json"], extra=["--publication-reach"])
        check("[modules] …while publication reach still narrows a diff with no CI path",
              got["scope"] == "narrowed" and got["selected"] == ["Acme.Alpha"],
              f"{got['scope']} {got['selected']}")
        selector = root / "scripts" / "affected-modules.py"
        selector.write_text(_STUB_SELECTOR_PRE_POLICY, encoding="utf-8")
        try:
            got = _run(root, "modules", "pull_request", ci_only)
            check("[modules] a selector that PREDATES the policy keeps a CI-only diff FULL",
                  got["scope"] == "full" and got["count"] == 2
                  and "ci-paths-owe-no-module-leg" in got["reason"],
                  f"scope={got['scope']} — {got['reason'][:90]}")
            got = _run(root, "modules", "pull_request", ["Alpha/index.json"])
            check("[modules] …while that selector still narrows a diff with no CI path",
                  got["scope"] == "narrowed" and got["selected"] == ["Acme.Alpha"],
                  f"{got['scope']} {got['selected']}")
        finally:
            selector.write_text(_STUB_SELECTOR, encoding="utf-8")
        selector.unlink()
        try:
            got = _run(root, "modules", "pull_request", ci_only)
            check("[modules] a caller with NO selector keeps a CI-only diff FULL",
                  got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']}")
        finally:
            selector.write_text(_STUB_SELECTOR, encoding="utf-8")

        print("renames are DECOMPOSED — the module a file LEFT is selected too:")
        # A real git fixture, because this case is about what `git diff` prints: with git's
        # default rename detection `--name-only` names only the destination, and the source
        # project silently drops out of the selection.
        import subprocess as sp
        git = lambda *a: sp.run(["git", *a], cwd=root, capture_output=True, text=True)
        # 🚨 Three things make this assertion non-vacuous, and each was needed:
        #   * the moved file exists ON THE BASE — otherwise the diff is a plain add and rename
        #     detection has nothing to collapse;
        #   * it moves OUT of a project a matrix entry builds INTO one no entry references, so
        #     "both ends" (['Acme.Beta']) and "destination only" ([]) differ;
        #   * a real `origin` remote, because the range this script takes is origin/<base>...HEAD.
        # Verified by reverting --no-renames: this case then reports [] and goes red.
        (root / "src" / "Acme.Beta" / "Moved.cs").write_text(
            "// long enough that git is certain it is the same file\n" * 8, encoding="utf-8")
        git("init", "-q", ".")
        git("config", "user.email", "t@t")
        git("config", "user.name", "t")
        git("add", "-A")
        git("commit", "-qm", "base")
        git("branch", "-M", "main")
        git("remote", "add", "origin", str(root))
        git("fetch", "-q", "origin")
        git("checkout", "-q", "-b", "move")
        (root / "src" / "Acme.Beta" / "Moved.cs").rename(
            root / "src" / "Acme.Other" / "Moved.cs")
        git("add", "-A")
        git("commit", "-qm", "move it")
        proc = sp.run([sys.executable, str(Path(__file__).resolve()), "--for", "modules",
                       "--root", str(root), "--event", "pull_request", "--base-ref", "main",
                       "--modules", json.dumps(_MATRIX)], capture_output=True, text=True)
        got = (json.loads(proc.stdout) if proc.returncode == 0
               else {"scope": f"<exit {proc.returncode}>", "selected": []})
        check("a file moved between projects selects the module it LEFT, not only the destination",
              got["scope"] == "narrowed" and got["selected"] == ["Acme.Beta"],
              f"scope={got['scope']} selected={got['selected']}")

        print("REFUSALS — 'build everything' and 'everything is nothing' cannot both be true:")
        # 🚨 The bug this pins shipped as a GREEN TICK: an empty universe took the `full` branch,
        # which set count = len(universe) = 0, and every consumer keys on the count alone — so
        # both lanes reported success having built nothing. It is a broken input, not a scope.
        proc = _raw(root, "modules", "pull_request", ["Alpha/index.json"], matrix=[])
        check("an EMPTY matrix input is refused (exit 2), never answered as full/0",
              proc.returncode == 2 and "nothing to select from" in proc.stderr,
              f"exit={proc.returncode}")
        proc = _raw(root, "modules", "pull_request", ["Alpha/index.json"],
                    extra=["--root", str(root / "no-such-dir")])
        check("a --root that is not the checkout is refused, never answered as full/0",
              proc.returncode != 0 and '"count": 0' not in proc.stdout,
              f"exit={proc.returncode} stdout={proc.stdout[:120]}")

        print("a PUBLISHING run never narrows, whatever the event:")
        got = _run(root, "modules", "pull_request", ["Alpha/index.json"], extra=["--publishing"])
        check("--publishing forces FULL even on a pull_request",
              got["scope"] == "full" and got["count"] == 2 and "PUBLISHES" in got["reason"],
              f"scope={got['scope']} count={got['count']}")

        print("a stale matrix entry must fail on THIS PR, not on the next push to main:")
        ghost = _MATRIX + [{"package": "Alpha", "module": "Acme.Ghost",
                            "project": "src/Acme.Ghost/Acme.Ghost.csproj"}]
        got = _run(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"], matrix=ghost)
        check("an entry whose csproj does not exist ⇒ ALL (it could never be selected)",
              got["scope"] == "full" and "does not exist" in got["reason"], f"{got['reason'][:90]}")
        nopkg = _MATRIX + [{"package": "NoSuchPkg", "module": "Acme.Nope",
                            "project": "src/Acme.Beta/Acme.Beta.csproj"}]
        got = _run(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"], matrix=nopkg)
        check("an entry naming a package with no index.json ⇒ ALL",
              got["scope"] == "full" and "not a node package" in got["reason"],
              f"{got['reason'][:90]}")
        # 🚨 UNCONDITIONAL: a diff touching no src/ path must still look at the entry list.
        got = _run(root, "modules", "pull_request", ["Alpha/index.json"], matrix=ghost)
        check("…and it is checked even when the diff touches NO src/ path",
              got["scope"] == "full" and "does not exist" in got["reason"], f"{got['reason'][:90]}")

        # ── the repo-ROOT no-op files (the AGENTS.md blind spot, Plugins run 34158493654) ──
        # 🚨 THE SUBJECT OF THIS FILTER IS A LIST OF NAMES, and a name that no longer exists in the
        # fleet would make its case pass having filtered nothing. So every name is asserted
        # INDIVIDUALLY, off NOOP_FILES itself rather than off a hand-written copy: adding a name
        # without an assertion is impossible, and REMOVING one turns its assertion red.
        print(f"repo-ROOT no-op files ({len(NOOP_FILES)} named) — the blind spot NOOP_DIRS "
              "structurally cannot cover:")
        # 🚨 THE DENOMINATOR, ASSERTED. The loop below iterates NOOP_FILES, so DELETING a name
        # would delete its case and the sweep would stay green having stopped covering it — the
        # exact "a guard whose subject moved" shape. These three names are the measured ones
        # (`AGENTS.md` is Plugins run 34158493654; every repo in the fleet carries all three), so
        # removing any of them turns THIS case red instead of quietly shrinking the population.
        check("NOOP_FILES still names the root files this fleet actually has",
              {"AGENTS.md", "CLAUDE.md", "README.md"} <= NOOP_FILES and len(NOOP_FILES) >= 9,
              f"NOOP_FILES={sorted(NOOP_FILES)}")
        for name in sorted(NOOP_FILES):
            got = _run(root, "modules", "pull_request", [name])
            check(f"[modules] a {name}-only diff builds NOTHING, not everything",
                  got["scope"] == "narrowed" and got["count"] == 0,
                  f"scope={got['scope']} count={got['count']} — {got['reason'][:80]}")
        got = _run(root, "modules", "pull_request", ["AGENTS.md"], extra=["--always", "Acme.Alpha"])
        check("[modules] …and with a floor it builds the floor and TESTS NOTHING",
              got["scope"] == "narrowed" and got["selected"] == ["Acme.Alpha"]
              and got["tested"] == [], f"{got['selected']} tested={got.get('tested')}")
        got = _run(root, "modules", "pull_request", ["AGENTS.md", "src/Acme.Beta/T.cs"])
        check("[modules] a root no-op file riding along never widens a real selection",
              got["scope"] == "narrowed" and got["selected"] == ["Acme.Beta"], f"{got['selected']}")

        print("the caller's shelf / build extension, as MSBuild evaluated it (--msbuild-evaluation):")
        # Acme.Other is a project the graph KNOWS and no entry reaches, exactly what a shelf is to the
        # real project-closure.py — so an edit there narrows to nothing unless the evaluation reaches it.
        shelf_rel = "src/Acme.ModuleLibraries/Acme.ModuleLibraries.csproj"
        shelf_dir = root / "src" / "Acme.ModuleLibraries"
        shelf_dir.mkdir(parents=True, exist_ok=True)
        (shelf_dir / "Acme.ModuleLibraries.csproj").write_text(_csproj([]), encoding="utf-8")
        other_rel = "src/Acme.Other/Acme.Other.csproj"
        ev_file = root.parent / f"{root.name}-msbuild-evaluation.json"

        def evaluation(shelf: dict, other: dict | None = None) -> list[str]:
            projects = {shelf_rel: {"imports": [shelf_rel], "declaredImports": [], "items": {}, "references": [],
                                    **shelf}}
            if other is not None:
                projects[other_rel] = {"imports": [other_rel], "declaredImports": [], "items": {}, "references": [],
                                       **other}
            ev_file.write_text(json.dumps({"sdk": "10.0.200", "projects": projects}), encoding="utf-8")
            return ["--msbuild-evaluation", str(ev_file)]

        try:
            got = _run(root, "modules", "pull_request", ["src/Acme.Other/K.cs"])
            check("[modules] negative control — WITHOUT a shelf named, an edit to Acme.Other selects nothing",
                  got["scope"] == "narrowed" and got["count"] == 0, f"scope={got['scope']} count={got['count']}")
            refs = evaluation({"references": [other_rel]}, {})
            for flag, label in (("--module-libraries-project", "module-libraries shelf"),
                                ("--build-extensions-project", "build extension")):
                arg = [flag, shelf_rel] + refs
                for what, path in (("the project itself", shelf_rel),
                                   ("a project MSBuild says it references", "src/Acme.Other/K.cs")):
                    got = _run(root, "modules", "pull_request", [path], extra=arg)
                    check(f"[modules] {label}: {what} changed ⇒ ALL, and SAYS it is the {label}",
                          got["scope"] == "full" and got["count"] == 2 and label in got["reason"],
                          f"scope={got['scope']} count={got['count']} — {got['reason'][:80]}")
                got = _run(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"], extra=arg)
                check(f"[modules] {label} named but untouched still narrows",
                      got["scope"] == "narrowed" and got["selected"] == ["Acme.Beta"], f"{got['selected']}")
            # MSBuild's imports and items, plus the conservative extras, for an extension that does
            # NOT reference Acme.Other but imports a file there, links a source there, and declares an
            # optional import that does not exist yet.
            arg = ["--build-extensions-project", shelf_rel] + evaluation({
                "imports": [shelf_rel, "src/Acme.Other/Ext.props"],
                "declaredImports": ["src/Acme.Other/Ext.props", "src/Acme.Other/Optional.props"],
                "declaredPatterns": ["src/Acme.Other/Gen/*.cs", "src/Acme.Other/Imp/*.props"],
                "swept": ["src/Acme.Other/Libs/Foo.dll"],
                "items": {"Compile": ["src/Acme.Other/Gen/Emitter.cs"],
                          "EmbeddedResource": ["src/Acme.Other/Res/a.res"]}})
            for what, path in (("an imported file (Ext.props)", "src/Acme.Other/Ext.props"),
                               ("an absent DECLARED import appearing (Optional.props)", "src/Acme.Other/Optional.props"),
                               ("a linked Compile item", "src/Acme.Other/Gen/Emitter.cs"),
                               ("a transform-produced item path", "src/Acme.Other/Res/a.res"),
                               ("a NEW file the declared Compile glob covers", "src/Acme.Other/Gen/New.cs"),
                               ("a file the conservative sweep found (a HintPath)", "src/Acme.Other/Libs/Foo.dll"),
                               ("a first match of a declared IMPORT glob that matches nothing yet",
                                "src/Acme.Other/Imp/First.props"),
                               ("a new ancestor implicit import", "src/Directory.Build.targets")):
                got = _run(root, "modules", "pull_request", [path], extra=arg)
                check(f"[modules] {what} changed ⇒ ALL", got["scope"] == "full" and got["count"] == 2,
                      f"scope={got['scope']} count={got['count']} — {got['reason'][:80]}")
            for what, path in (("a file in the same project MSBuild did not evaluate", "src/Acme.Other/K.cs"),
                               ("a file in the glob's directory the pattern does NOT cover", "src/Acme.Other/Gen/notes.txt"),
                               ("an UNIMPORTED Directory.Unused.props outside the ancestry",
                                "src/Acme.Other/Directory.Unused.props")):
                got = _run(root, "modules", "pull_request", [path], extra=arg)
                check(f"[modules] negative control — {what} narrows",
                      got["scope"] == "narrowed" and got["count"] == 0,
                      f"scope={got['scope']} count={got['count']} — {got['reason'][:80]}")
            # DELETION: the head no longer has the file, so only the BASE evaluation lists it. The scope
            # unions both (--msbuild-evaluation given twice).
            base_file = root.parent / f"{root.name}-msbuild-evaluation-base.json"
            head_only = ["--build-extensions-project", shelf_rel] + evaluation({"imports": [shelf_rel]})
            head_file = root.parent / f"{root.name}-msbuild-evaluation-head.json"
            head_file.write_text(ev_file.read_text(encoding="utf-8"), encoding="utf-8")
            evaluation({"imports": [shelf_rel, "src/Acme.Other/Imp/Last.props"],
                        "items": {"Compile": ["src/Acme.Other/Lib/Last.cs"]}})
            base_file.write_text(ev_file.read_text(encoding="utf-8"), encoding="utf-8")
            union = ["--build-extensions-project", shelf_rel, "--msbuild-evaluation", str(head_file),
                     "--msbuild-evaluation", str(base_file)]
            try:
                for what, path in (("the LAST match of an import glob deleted", "src/Acme.Other/Imp/Last.props"),
                                   ("the LAST match of a Compile glob deleted", "src/Acme.Other/Lib/Last.cs")):
                    got = _run(root, "modules", "pull_request", [path],
                               extra=["--build-extensions-project", shelf_rel, "--msbuild-evaluation", str(head_file)])
                    check(f"[modules] negative control — {what}: HEAD's evaluation alone no longer sees it",
                          got["scope"] == "narrowed" and got["count"] == 0, f"scope={got['scope']} count={got['count']}")
                    got = _run(root, "modules", "pull_request", [path], extra=union)
                    check(f"[modules] {what}: the BASE ∪ HEAD union selects ALL",
                          got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']} count={got['count']}")
                got = _run(root, "modules", "pull_request", ["src/Acme.Other/Lib/notes.txt"], extra=union)
                check("[modules] negative control — an unrelated file beside the deleted one narrows under the union",
                      got["scope"] == "narrowed" and got["count"] == 0, f"scope={got['scope']} count={got['count']}")
                # A tree that predates the project (a new extension) contributes nothing; head still decides.
                evaluation({}, None)
                ev_file.write_text(json.dumps({"sdk": "10.0.200", "missing": [shelf_rel], "projects": {}}), encoding="utf-8")
                got = _run(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"],
                           extra=["--build-extensions-project", shelf_rel, "--msbuild-evaluation", str(head_file),
                                  "--msbuild-evaluation", str(ev_file)])
                check("[modules] a BASE tree that predates the project is skipped, not refused",
                      got["scope"] == "narrowed" and got["selected"] == ["Acme.Beta"], f"{got['selected']}")
                # A second evaluation that LACKS the project without listing it as `missing` is a miss,
                # never an empty contribution.
                ev_file.write_text(json.dumps({"sdk": "10.0.200", "projects": {}}), encoding="utf-8")
                proc = _raw(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"], None,
                            ["--build-extensions-project", shelf_rel, "--msbuild-evaluation", str(head_file),
                             "--msbuild-evaluation", str(ev_file)])
                check("[modules] an evaluation that silently lacks the project ⇒ REFUSED (exit 2), not an empty reach",
                      proc.returncode == 2 and "not in the MSBuild evaluation" in proc.stderr,
                      f"exit={proc.returncode} {proc.stderr[-200:]}")
            finally:
                base_file.unlink(missing_ok=True)
                head_file.unlink(missing_ok=True)
            # The project as the lane may SPELL it: `./a/../a/x.csproj` is the same project.
            spelled = ["--build-extensions-project", "./src/Acme.ModuleLibraries/../Acme.ModuleLibraries/Acme.ModuleLibraries.csproj"] \
                + evaluation({"imports": [shelf_rel, "src/Acme.Other/Ext.props"]})
            got = _run(root, "modules", "pull_request", ["src/Acme.Other/Ext.props"], extra=spelled)
            check("[modules] a `./…/../…`-spelled project is normalized before the lookup: its import selects ALL",
                  got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']} count={got['count']} {got['reason'][:80]}")
            got = _run(root, "modules", "pull_request", ["src/Acme.Other/K.cs"], extra=spelled)
            check("[modules] negative control — the same spelling, an unrelated file, narrows",
                  got["scope"] == "narrowed" and got["count"] == 0, f"scope={got['scope']} count={got['count']}")
            # A pattern MSBuild could not resolve: fail SAFE, any change reaches the project.
            unknown = ["--build-extensions-project", shelf_rel] + evaluation(
                {"unknownPatterns": ["src/Acme.ModuleLibraries/Acme.ModuleLibraries.csproj: <Compile Include=\"$([System.IO.Path]::Combine('a','b'))/*.cs\">"]})
            got = _run(root, "modules", "pull_request", ["src/Acme.Other/K.cs"], extra=unknown)
            check("[modules] an UNKNOWN (unevaluable) pattern ⇒ an unrelated change still selects ALL (fail safe)",
                  got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']} count={got['count']}")
            # Refusals: a named input is never scoped without MSBuild's evaluation of it.
            for what, extra, needle in (
                    ("named with NO --msbuild-evaluation", ["--build-extensions-project", shelf_rel], "--msbuild-evaluation"),
                    ("named but absent from the evaluation", ["--module-libraries-project", other_rel] + evaluation({}),
                     "not in the MSBuild evaluation"),
                    ("with an unreadable evaluation", ["--build-extensions-project", shelf_rel,
                                                       "--msbuild-evaluation", str(root / "nope.json")], "could not be read")):
                proc = _raw(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"], None, extra)
                check(f"[modules] {what} ⇒ REFUSED (exit 2), saying why",
                      proc.returncode == 2 and needle in proc.stderr, f"exit={proc.returncode} {proc.stderr[-200:]}")
            got = _run(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"],
                       extra=["--build-extensions-project", "src/Nope/Nope.csproj"])
            check("[modules] a named extension missing from the checkout ⇒ ALL (cannot decide), named by its input",
                  got["scope"] == "full" and got["count"] == 2 and "build-extensions-project" in got["reason"],
                  f"scope={got['scope']} — {got['reason'][:80]}")
        finally:
            ev_file.unlink(missing_ok=True)
            (shelf_dir / "Acme.ModuleLibraries.csproj").unlink()
            shelf_dir.rmdir()

        print("the MSBuild glob matcher (the implicit Directory.* candidates):")
        for path, pattern, want in (
                ("src/Directory.Packages.Plugins.props", "src/Directory.*.props", True),
                ("src/x/Directory.Build.props", "src/Directory.*.props", False),
                ("src/Shared/Emitter.cs", "src/Shared/**/*.cs", True),
                ("src/Shared/a/b/Emitter.cs", "src/Shared/**/*.cs", True),
                ("src/SharedX/Emitter.cs", "src/Shared/**/*.cs", False),
                ("src/Shared/Emitter.cs", "src/Shared/Emitter.cs", True)):
            check(f"msbuild_glob_matches({path!r}, {pattern!r}) is {want}",
                  msbuild_glob_matches(path, pattern) is want)
        got = _run(root, "modules", "pull_request", ["AGENTS.md", "plugin-gate.allow"])
        check("[modules] one UNNAMED root file among no-op ones still runs the full set",
              got["scope"] == "full" and "plugin-gate.allow" in got["reason"],
              f"scope={got['scope']} — {got['reason'][:90]}")

        print("the NOOP_DIRS copy drifting from the caller's costs the DIVERGENT DIR, not the run:")
        selector_src = (root / "scripts" / "affected-modules.py").read_text(encoding="utf-8")
        selector_path = root / "scripts" / "affected-modules.py"
        # THEIRS is missing `app` — MeshWeaver.SocialMedia's live divergence, measured on run
        # 34122662676, which used to answer `full` on every pull request that repo ever opened.
        theirs_short = ('NOOP_DIRS = {"legacy", "e2e", "docs", "WhatsNew", ".claude", ".worktrees", '
                        '"devtools"}\n')
        selector_path.write_text(theirs_short + selector_src, encoding="utf-8")
        got = _run(root, "modules", "pull_request", ["docs/guide.md"])
        check("a caller missing one of our NOOP dirs still NARROWS on the ones both agree about",
              got["scope"] == "narrowed" and got["count"] == 0 and got.get("divergence")
              and "only ours: app" in got["divergence"],
              f"scope={got['scope']} divergence={got.get('divergence')}")
        got = _run(root, "modules", "pull_request", ["app/rn/App.tsx"])
        check("…while the DIVERGENT dir itself is treated as EVERYTHING (over-built, never skipped)",
              got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']}")
        # THEIRS carries a dir we do not — the other direction, and it must be just as safe.
        selector_path.write_text(
            'NOOP_DIRS = {"legacy", "e2e", "docs", "WhatsNew", "app", ".claude", ".worktrees", '
            '"devtools", "vendor"}\n' + selector_src, encoding="utf-8")
        got = _run(root, "modules", "pull_request", ["vendor/lib.js"])
        check("a dir only THEY call inert is still EVERYTHING here (the other direction)",
              got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']}")
        got = _run(root, "modules", "pull_request", ["docs/guide.md"])
        check("…and their extra entry does not stop US narrowing on the agreed ones",
              got["scope"] == "narrowed" and got["count"] == 0, f"scope={got['scope']}")
        # 🚨 THE SUBJECT MOVING is the one thing that must still refuse to narrow: a literal that
        # no longer parses means this script cannot know what the caller believes, and "cannot
        # verify" has exactly one honest answer.
        selector_path.write_text(
            selector_src.replace("NOOP_DIRS", "NOOP_TOPS"), encoding="utf-8")
        got = _run(root, "modules", "pull_request", ["docs/guide.md"])
        check("a RENAMED NOOP_DIRS literal ⇒ ALL (the subject moved, so we cannot know)",
              got["scope"] == "full" and "no single-line NOOP_DIRS literal" in got["reason"],
              f"scope={got['scope']} — {got['reason'][:100]}")
        selector_path.write_text(selector_src, encoding="utf-8")
        got = _run(root, "modules", "pull_request", ["WhatsNew/note.md"])
        check("WhatsNew/ is a NOOP dir — a note-only PR builds NOTHING, not everything",
              got["scope"] == "narrowed" and got["count"] == 0,
              f"scope={got['scope']} count={got['count']}")
        got = _run(root, "modules", "pull_request", ["devtools/run-node-tests.py"])
        check("devtools/ is a NOOP dir — a local dev tool builds NOTHING, not everything",
              got["scope"] == "narrowed" and got["count"] == 0,
              f"scope={got['scope']} count={got['count']}")
        got = _run(root, "modules", "pull_request", ["tools/run-node-tests.py"])
        check("…while the same script under tools/ (build input) is still EVERYTHING",
              got["scope"] == "full" and got["count"] == 2,
              f"scope={got['scope']} count={got['count']}")

        print("the caller's graphs are load-bearing — every failure of theirs is a FULL run:")
        selector = root / "scripts" / "affected-modules.py"
        for label, body in (
                ("a selector that REFUSES", "import sys; sys.exit(1)\n"),
                ("a selector answering runAll", "import json,sys; json.dump({'runAll':True},sys.stdout)\n"),
                ("an unparseable selector answer", "not json\n")):
            selector.write_text(body, encoding="utf-8")
            got = _run(root, "modules", "pull_request", ["Alpha/index.json"])
            check(f"[modules] {label} ⇒ ALL", got["scope"] == "full" and got["count"] == 2,
                  f"scope={got['scope']}")
        selector.unlink()
        got = _run(root, "modules", "pull_request", ["Alpha/index.json"])
        check("[modules] no selector in the caller repo ⇒ ALL",
              got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']}")
        selector.write_text(_STUB_SELECTOR, encoding="utf-8")
        projects = root / "scripts" / "project-closure.py"
        graph_src = projects.read_text(encoding="utf-8")
        for label, body in (
                ("a project graph that REFUSES", "import sys; sys.exit(1)\n"),
                ("an unparseable project-graph answer", "not json\n"),
                ("a project graph that answers for FEWER entries than were asked about",
                 "import json,sys; json.dump({'entries':{},'unclassified':[],"
                 "'projects':[],'changedProjects':[]},sys.stdout)\n")):
            projects.write_text(body, encoding="utf-8")
            got = _run(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"])
            check(f"[modules] {label} ⇒ ALL", got["scope"] == "full" and got["count"] == 2,
                  f"scope={got['scope']} — {got['reason'][:80]}")
        projects.write_text(graph_src, encoding="utf-8")

        # `--base` reaches a project graph that DECLARES it (MeshWeaver.Plugins#4314: an inert,
        # comment-only MSBuild edit owes nothing) — and never one that does not, which would
        # refuse the unknown flag and turn every src/ diff into ALL.
        inert = ["src/Acme.Shared/Directory.Build.props"]
        projects.write_text(graph_src.replace(
            "a=sys.argv\n",
            'a=sys.argv\nB="--base"\n'
            "assert B not in a or a[a.index(B)+1]=='origin/main', a\n"
            "_inert=B in a\n").replace(
            "hit={own(f) for f in paths if own(f)}\n",
            "hit={own(f) for f in paths if own(f) and not (_inert and f.endswith('.props'))}\n"),
            encoding="utf-8")
        got = _run(root, "modules", "pull_request", inert, extra=["--base-ref", "main"])
        check("[modules] a base-aware project graph gets `--base origin/main` and its inert MSBuild "
              "edit selects nothing", got["count"] == 0 and got["scope"] != "full",
              f"scope={got['scope']} count={got['count']} — {got['reason'][:80]}")
        got = _run(root, "modules", "pull_request", inert)
        check("[modules] …with no base ref there is no `--base`: the edit still counts (Alpha)",
              got["selected"] == ["Acme.Alpha"], f"selected={got['selected']}")
        projects.write_text(graph_src.replace(
            "a=sys.argv\n", "a=sys.argv\nimport sys as _s\n'--base' in a and _s.exit(2)\n"),
            encoding="utf-8")
        got = _run(root, "modules", "pull_request", inert, extra=["--base-ref", "main"])
        check("[modules] NEGATIVE CONTROL: a project graph that does NOT declare `--base` is never "
              "handed it — the edit narrows to Alpha, not ALL",
              got["selected"] == ["Acme.Alpha"] and got["scope"] != "full",
              f"scope={got['scope']} selected={got['selected']} — {got['reason'][:80]}")
        # A PUBLICATION push is a history union: a props edit a later commit reverted is still owed
        # (a failed run may have published a partial bundle). The base-aware graph would rule it
        # inert against WHATEVER base it is handed (here: any `--base` at all), so the module is
        # selected ONLY if no `--base` reaches the graph on this path; handing it one reproduces
        # the defect — the repair silently selects nothing.
        projects.write_text(graph_src.replace(
            "a=sys.argv\n",
            'a=sys.argv\nB="--base"\n'
            "_inert=B in a\n").replace(
            "hit={own(f) for f in paths if own(f)}\n",
            "hit={own(f) for f in paths if own(f) and not (_inert and f.endswith('.props'))}\n"),
            encoding="utf-8")
        def _g(*a):
            return subprocess.run(["git", *a], cwd=root, capture_output=True, text=True,
                                  check=True).stdout.strip()
        pub_base = _g("rev-parse", "HEAD")
        props = root / "src" / "Acme.Shared" / "Directory.Build.props"
        props.parent.mkdir(parents=True, exist_ok=True)
        props.write_text("<Project><!-- edit --></Project>\n", encoding="utf-8")
        _g("add", "--", "src/Acme.Shared/Directory.Build.props")
        _g("commit", "-qm", "an MSBuild edit a failed run partially published")
        _g("rm", "-q", "--", "src/Acme.Shared/Directory.Build.props")
        _g("commit", "-qm", "revert it")
        got = _run(root, "modules", "push", None,
                   extra=["--publication-base", pub_base, "--publishing"])
        check("[modules] a publication push hands NO `--base` to the project graph — a reverted "
              "MSBuild edit still selects its module (Alpha) for the repair",
              got["selected"] == ["Acme.Alpha"] and got["scope"] != "full",
              f"scope={got['scope']} selected={got['selected']} — {got['reason'][:80]}")
        projects.write_text(graph_src, encoding="utf-8")
        projects.unlink()
        got = _run(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"])
        check("[modules] no project-closure.py in the caller repo ⇒ ALL",
              got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']}")

    if failures:
        print(f"\n::error title=node-repo-scope self-test failed::{len(failures)} case(s) — this "
              "script decides what is NOT rebuilt; a fallback that stops falling back is a stale "
              "assembly the registry keeps serving, or an in-mesh compile break that reaches main.")
        return 1
    print(f"\n✓ node-repo-scope self-test: {ran} cases green — full-run fallbacks, "
          "narrowing assertions (one over a REAL git range: a rename must select the end it "
          "LEFT), two explicit-empty answers, the two REFUSALS that must never render as "
          "'full, count 0', the stale-matrix-entry guard, the NOOP_DIRS drift guard, the "
          "publishing guard, CI paths deferred to the caller's selector (policy "
          "ci-paths-owe-no-module-leg), and every caller-graph failure mode.")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    p.add_argument("--for", dest="lane", choices=("modules",))
    p.add_argument("--modules", help="the module matrix as JSON, or @file (--for modules)")
    p.add_argument("--root", default=".", help="the caller repo checkout")
    p.add_argument("--event", default="", help="the triggering GitHub event name")
    p.add_argument("--base-ref", default="", dest="base_ref",
                   help="the PR base branch — the diff is origin/<base-ref>...HEAD")
    p.add_argument("--changed-list", default=None, dest="changed_list",
                   help="comma-separated changed paths instead of a git diff (tests only)")
    p.add_argument("--publication-base", default="",
                   help="successful trunk publication SHA; push only, ancestor-checked")
    p.add_argument("--publishing", action="store_true",
                   help="this run hands its output to a registry or feed — never narrow. The "
                        "publish path's change detector is the derived version, and a diff that "
                        "misses a unit means that unit silently never ships.")
    p.add_argument("--publication-reach", action="store_true", dest="publication_reach",
                   help="the question is PUBLICATION reachability (does a newer trunk commit reach "
                        "this module?), not a pull request's build selection: a CI path stays "
                        "EVERYTHING (policy ci-paths-owe-no-module-leg covers PR selection only)")
    p.add_argument("--always", default="",
                   help="comma-separated module names the caller's gates compose on EVERY run — "
                        "a floor the narrowing always keeps (--for modules)")
    p.add_argument("--carry", default="",
                   help="packages (comma/space separated) the --publication-base run WITHHELD; on a push "
                        "narrowed by that baseline every matrix entry of these packages is selected "
                        "(--for modules; node-repo-publication-base.py's publication-carry)")
    p.add_argument("--module-libraries-project", default="", dest="shelf_project",
                   help="the caller's module-libraries shelf csproj (relative to --root): a diff "
                        "that touches it or a project it reaches runs the full set")
    p.add_argument("--build-extensions-project", default="", dest="extensions_project",
                   help="the caller's build-extension csproj (relative to --root): a diff that "
                        "touches it or a project it reaches runs the full set")
    p.add_argument("--msbuild-evaluation", action="append", default=[], dest="msbuild_evaluation",
                   help="the JSON msbuild-evaluate.py writes for the shelf / extension: MSBuild's own imports and "
                        "items, required when either is named. Repeatable: HEAD's and the diff BASE's are unioned")
    p.add_argument("--self-test", action="store_true", dest="self_test")
    args = p.parse_args()
    if args.self_test:
        return self_test()
    if args.lane is None:
        p.error("--for modules is required (only --self-test runs without a lane)")

    entries: list[dict] = []
    if args.lane == "modules":
        if not args.modules:
            p.error("--for modules needs --modules")
        raw = (Path(args.modules[1:]).read_text(encoding="utf-8")
               if args.modules.startswith("@") else args.modules)
        entries = json.loads(raw)
        if not isinstance(entries, list):
            sys.exit("✗ --modules must be a JSON array of {package, module, project}")
        for e in entries:
            if not isinstance(e, dict) or not {"package", "module", "project"} <= set(e):
                sys.exit(f"✗ matrix entry missing package/module/project: {e!r}")

    root = Path(args.root).resolve()
    say = lambda *a: print(*a, file=sys.stderr)  # noqa: E731
    try:
        evaluation = [load_msbuild_evaluation(f) for f in args.msbuild_evaluation] or None
    except ValueError as exc:
        print(f"::error title=Cannot decide the build scope::{exc}", file=sys.stderr)
        return 2
    changed = None if args.changed_list is None else [c for c in args.changed_list.split(",") if c]
    always = frozenset(a.strip() for a in args.always.split(",") if a.strip())
    carry = frozenset(re.split(r"[\s,]+", args.carry.strip())) - {""}
    bad_carry = sorted(c for c in carry if not re.fullmatch(r"[\w.-]+", c))
    if bad_carry:
        sys.exit(f"✗ --carry names something that is not a package id: {bad_carry[0]!r}")
    answer = decide(root, args.lane, entries, args.event,
                    f"origin/{args.base_ref}...HEAD" if args.base_ref else None, changed, say,
                    publishing=args.publishing, always=always, publication_base=args.publication_base,
                    publication_reach=args.publication_reach, shelf_project=args.shelf_project,
                    extensions_project=args.extensions_project, msbuild_evaluation=evaluation,
                    carry=carry)

    # 🚨 A refusal is a BROKEN INPUT, not a scope: exit non-zero so the step fails and the job
    # goes red. Emitting it as an answer is how "build everything, and everything is nothing"
    # became a green tick in the first place.
    if answer["scope"] == "refuse":
        print(f"::error title=Cannot decide the build scope::{answer['reason']}", file=sys.stderr)
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as fh:
                fh.write(f"### ⛔ Cannot decide the build scope\n\n{answer['reason']}\n\n")
        return 2

    say("")
    say(f"scope: {answer['scope']} — {answer['reason']}")
    if answer.get("divergence"):
        say(f"⚠ {answer['divergence']}")
    for name, reason in sorted(answer.get("why", {}).items()):
        say(f"  ▸ {name}: {reason}")
    if answer["skipped"]:
        say(f"not built ({len(answer['skipped'])}): {', '.join(answer['skipped'])}")
    # 🚨 STATE BOTH DENOMINATORS, ALWAYS, AND NEVER LEAVE A ZERO BARE. "0 selected" and "0 tested"
    # are the two answers a reader must be able to tell apart from "the filter matched nothing
    # because it is broken", and the only thing that distinguishes them is the count they are
    # measured against being printed beside them.
    total = answer["count"] + len(answer["skipped"])
    tested = answer.get("tested", [])
    say(f"built {answer['count']} of {total} bundle(s): "
        f"{', '.join(answer['selected']) if answer['selected'] else '<none>'}")
    say(f"tested {len(tested)} of {answer['count']} selected bundle(s): "
        f"{', '.join(tested) if tested else '<none> — no selected bundle owes a test run'}")

    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as fh:
            fh.write(f"scope={answer['scope']}\n")
            fh.write(f"count={answer['count']}\n")
            fh.write("modules=" + json.dumps(answer["modules"]) + "\n")
            fh.write("selected=" + " ".join(answer["selected"]) + "\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as fh:
            fh.write(f"### {'🧱' if answer['scope'] == 'full' else '🎯'} "
                     f"{answer['lane']}: {answer['scope']} — {answer['count']} of {total}\n\n"
                     f"{answer['reason']}\n\n"
                     f"**Built** {answer['count']} of {total} · **tested** "
                     f"{len(answer.get('tested', []))} of {answer['count']}"
                     f"{' — no selected bundle owes a test run' if not answer.get('tested') else ''}"
                     "\n\n")
            if answer.get("divergence"):
                fh.write(f"> ⚠ {answer['divergence']}\n\n")
            if answer["scope"] == "narrowed":
                if answer["why"]:
                    fh.write("| built | why |\n|---|---|\n")
                    for name, reason in sorted(answer["why"].items()):
                        fh.write(f"| `{name}` | {reason} |\n")
                if answer["skipped"]:
                    fh.write(f"\n**Not built:** `{'`, `'.join(answer['skipped'])}`\n")

    print(json.dumps(answer, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
