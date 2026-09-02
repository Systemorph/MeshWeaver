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
    (`module-owned-platform.sh`: in this repo's `src/`, absent from `src/platform-shipped.txt`,
    therefore nowhere in the image's `/app`). Measured on MeshWeaver.Plugins 2026-09-01:
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

# 🚨 scripts/affected-modules.py's NOOP_DIRS, and it must stay EQUAL to it — the top-level dirs a
# change in which can reach no build at all. Every entry NOT in this set falls through to a FULL
# run, so adding one silently un-builds something and dropping one merely costs a full run.
# `clients/` is deliberately absent (the established selector treats it as ALL, and a client asset
# can be embedded by a host).
#
# 🚨 A HAND COPY IS NOT A GUARANTEE, and this one drifted on its first day: `WhatsNew` was missing,
# so a note-only PR ran the entire fleet's module suite — the exact case affected-modules.py added
# it for. `assert_noop_dirs_match()` below now READS the caller's set and refuses to narrow when
# the two disagree, so the next drift is a full run with a named reason instead of a silent one.
NOOP_DIRS = {"legacy", "e2e", "docs", "WhatsNew", "app", ".claude", ".worktrees"}

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


def project_hits(root: Path, entries: list[str], changed: list[str], say) -> dict | None:
    """{entry → the changed project dirs its build+test closure contains} + `unclassified`."""
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as fh:
        fh.write("\n".join(changed) + "\n")
        listing = Path(fh.name)
    argv = ["--hits", "--changed", str(listing), "--json"]
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


def assert_noop_dirs_match(root: Path) -> str | None:
    """None when the caller's NOOP_DIRS equals ours; otherwise why we must not narrow.

    Read out of the caller's source rather than imported: affected-modules.py is a CLI, and
    importing it would run its argparse. A set literal on one line is what it has always been; if
    that ever stops parsing, the honest answer is "cannot verify" — which is also a full run.
    """
    try:
        text = (root / SELECTOR).read_text(encoding="utf-8")
    except OSError as exc:
        return f"{SELECTOR} could not be read ({exc})"
    match = re.search(r"^NOOP_DIRS\s*=\s*\{([^}]*)\}", text, re.M)
    if not match:
        return f"{SELECTOR} has no single-line NOOP_DIRS literal to compare against"
    theirs = set(re.findall(r"[\"']([^\"']+)[\"']", match.group(1)))
    if theirs != NOOP_DIRS:
        only_theirs = ", ".join(sorted(theirs - NOOP_DIRS)) or "(none)"
        only_ours = ", ".join(sorted(NOOP_DIRS - theirs)) or "(none)"
        return (f"this script's NOOP_DIRS has drifted from {SELECTOR}'s — only theirs: "
                f"{only_theirs}; only ours: {only_ours}. The two must classify a top-level dir "
                "identically or the lanes disagree about what a change reaches")
    return None


def changed_files(root: Path, diff_range: str, say) -> list[str] | None:
    """The changed paths, with renames DECOMPOSED into a delete and an add.

    🚨 `--no-renames` is selection logic, not a formatting flag. Git detects renames by default and
    `--name-only` then prints only the DESTINATION — so a file moved from one module (or one
    csproj) to another names only the module it arrived in, and the one it LEFT is never selected,
    even though its build changed. Under-selection is the failure mode this whole script is biased
    against; decomposing the rename makes both ends appear.
    """
    proc = subprocess.run(["git", "diff", "--no-renames", "--name-only", diff_range],
                          cwd=root, capture_output=True, text=True, timeout=120)
    if proc.returncode != 0:
        say("    " + proc.stderr.strip()[-2000:])
        return None
    return [line for line in proc.stdout.splitlines() if line.strip()]


# ── the decision ─────────────────────────────────────────────────────────────────────────────

def decide(root: Path, lane: str, entries: list[dict], event: str, diff_range: str | None,
           override: list[str] | None, say, publishing: bool = False,
           always: frozenset[str] = frozenset()) -> dict:
    all_packages = sorted(node_packages(root))
    universe = [e["module"] for e in entries]

    def full(reason: str) -> dict:
        return {"lane": lane, "scope": "full", "reason": reason,
                "modules": [{**e, "test": True} for e in entries],
                "count": len(universe), "selected": sorted(universe), "skipped": [], "why": {}}

    def refuse(reason: str) -> dict:
        return {"lane": lane, "scope": "refuse", "reason": reason, "modules": [],
                "count": 0, "selected": [], "skipped": [], "why": {}}

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

    if publishing:
        return full("this run PUBLISHES, and a publishing run is never narrowed — the derived "
                    "version is the change detector on that path, and a diff that misses a unit "
                    "means that unit silently never ships.")
    if event not in NARROWABLE:
        return full(FULL_REASONS[lane].get(
            event, f"event '{event}' has no meaningful content diff — running the full set."))
    if not (root / SELECTOR).is_file():
        return full(f"the caller repo ships no {SELECTOR}, so the affected closure cannot be "
                    "computed — running the full set.")
    drift = assert_noop_dirs_match(root)
    if drift is not None:
        return full(f"{drift} — running the full set.")

    if override is not None:
        files = [f for f in override if f.strip()]
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

    packages = node_packages(root)
    node_files: list[str] = []
    src_files: list[str] = []
    global_files: list[str] = []
    report: list[str] = []
    for f in files:
        top = f.split("/", 1)[0]
        if "/" in f and top in packages:
            node_files.append(f)
            report.append(f"  {f}  → node package {top}")
        elif "/" in f and top in NOOP_DIRS:
            report.append(f"  {f}  → (reaches nothing this lane builds — {top}/)")
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
    if node_files:
        answer = affected_packages(root, node_files, say)
        if answer is None:
            return full(f"{SELECTOR} could not narrow the node-package closure (it refused, "
                        "answered ALL, or could not be parsed) — running the full set.")
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
        answer = project_hits(root, sorted(hits), src_files, say)
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

    selected: list[dict] = []
    why: dict[str, str] = {}
    for e in entries:
        reasons = []
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
    return {"lane": lane, "scope": "narrowed",
            "reason": f"{len(selected)} of {len(entries)} module bundle(s) are reachable from this "
                      f"diff ({len(files)} file(s)).",
            "modules": selected, "count": len(selected),
            "selected": sorted(chosen), "skipped": sorted(set(universe) - chosen), "why": why}


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
    'NOOP_DIRS = {"legacy", "e2e", "docs", "WhatsNew", "app", ".claude", ".worktrees"}\n'
    "import json,sys\n"
    "paths=[l for l in open(sys.argv[sys.argv.index('--changed')+1]).read().splitlines() if l]\n"
    "pk=sorted({p.split('/')[0] for p in paths})\n"
    # Alpha is Beta's dependent in the fixture graph, exactly as a `requires` edge would make it.
    "aff=sorted(set(pk)|({'Alpha'} if 'Beta' in pk else set()))\n"
    "json.dump({'runAll':False,'affected':aff,'mount':aff,'skipped':[],'support':[]},sys.stdout)\n")


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
                    ("a repo-ROOT file ⇒ ALL", ["README.md"]),
                    (".github/ (the workflow itself) ⇒ ALL", [".github/workflows/ci.yml"]),
                    ("scripts/ (the gates) ⇒ ALL", ["scripts/gen-manifests.py"]),
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

        print("the NOOP_DIRS copy cannot drift from the caller's unnoticed:")
        selector_src = (root / "scripts" / "affected-modules.py").read_text(encoding="utf-8")
        (root / "scripts" / "affected-modules.py").write_text(
            'NOOP_DIRS = {"legacy", "e2e"}\n' + selector_src, encoding="utf-8")
        got = _run(root, "modules", "pull_request", ["Alpha/index.json"])
        check("a NOOP_DIRS that disagrees with the caller's ⇒ ALL, naming both sides",
              got["scope"] == "full" and "drifted" in got["reason"], f"{got['reason'][:100]}")
        (root / "scripts" / "affected-modules.py").write_text(selector_src, encoding="utf-8")
        got = _run(root, "modules", "pull_request", ["WhatsNew/note.md"])
        check("WhatsNew/ is a NOOP dir — a note-only PR builds NOTHING, not everything",
              got["scope"] == "narrowed" and got["count"] == 0,
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
          "publishing guard, and every caller-graph failure mode.")
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
    p.add_argument("--publishing", action="store_true",
                   help="this run hands its output to a registry or feed — never narrow. The "
                        "publish path's change detector is the derived version, and a diff that "
                        "misses a unit means that unit silently never ships.")
    p.add_argument("--always", default="",
                   help="comma-separated module names the caller's gates compose on EVERY run — "
                        "a floor the narrowing always keeps (--for modules)")
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
    changed = None if args.changed_list is None else [c for c in args.changed_list.split(",") if c]
    always = frozenset(a.strip() for a in args.always.split(",") if a.strip())
    answer = decide(root, args.lane, entries, args.event,
                    f"origin/{args.base_ref}...HEAD" if args.base_ref else None, changed, say,
                    publishing=args.publishing, always=always)

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
    for name, reason in sorted(answer.get("why", {}).items()):
        say(f"  ▸ {name}: {reason}")
    if answer["skipped"]:
        say(f"not built ({len(answer['skipped'])}): {', '.join(answer['skipped'])}")

    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as fh:
            fh.write(f"scope={answer['scope']}\n")
            fh.write(f"count={answer['count']}\n")
            fh.write("modules=" + json.dumps(answer["modules"]) + "\n")
            fh.write("selected=" + " ".join(answer["selected"]) + "\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        total = answer["count"] + len(answer["skipped"])
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as fh:
            fh.write(f"### {'🧱' if answer['scope'] == 'full' else '🎯'} "
                     f"{answer['lane']}: {answer['scope']} — {answer['count']} of {total}\n\n"
                     f"{answer['reason']}\n\n")
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
