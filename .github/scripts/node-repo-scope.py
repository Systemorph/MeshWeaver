#!/usr/bin/env python3
"""node-repo-scope.py — decide what a plugin repo's CI must actually REBUILD.

    "in plugins we only re-run concerned modules ⇒ we can estimate the required count and
     validate it"  ·  "all plugins should only re-build what changed"  ·  "there is always a
     notion of 'full rebuild' when the platform updates"  ·  "unify all MeshWeaver.* repos"

TWO LANES ask this question, with the same rule and the same fallbacks, so they share one script:

  --for modules   `node-repo-module-pack.yml` fans out ONE JOB per mixed package. That fan-out was
                  the caller's literal list, every entry on every event: MeshWeaver.Plugins ran 29
                  module builds on every push of every PR, whether the diff touched one module's
                  C# or a course's markdown.
  --for packages  `plugin-publish.yml` packs EVERY node package in one job — 53 in
                  MeshWeaver.Plugins, `Store` alone being 17 compilation units. It is the COMPILE
                  GATE over ~12k lines of in-mesh NodeType C# against the newest RELEASED
                  framework (a different compiler input from `compile-check.py --image`), and it
                  publishes nothing on a PR because that lane is `dry-run` by design.

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
    beside its bundles; bake-scope.sh reads it). THE MODULE LANE HAS NONE: the registry is keyed
    `{package}@{version}` from `manifest.lock`, and that version identifies only the CONTENT half
    — a `src/`-only change repacks the same version with different bytes, so "the registry already
    serves this version" is NOT evidence the published bundle matches HEAD. (That is the same
    blind spot Plugins #878 records on the delivery side.) Until this lane stamps a source commit
    into what it publishes, a push packs everything.
  * packages — `plugin-publish.yml`'s header spells out why there is no changed-plugin detection
    on the publish path: the derived PATCH is the change detector, and a hand-written diff would
    be a second detector free to disagree, whose failure mode is a plugin that silently never
    ships. That argument is about PUBLISHING. On a `pull_request` nothing is published at all, so
    it does not apply — and the unnarrowed `push` keeps the trunk exhaustive.

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
    node-repo-scope.py --for packages --event schedule
    node-repo-scope.py --self-test

OUTPUT — JSON on stdout, the human report on stderr, plus $GITHUB_OUTPUT / $GITHUB_STEP_SUMMARY.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

# 🚨 EXACTLY scripts/affected-modules.py's NOOP_DIRS, verbatim — the top-level dirs a change in
# which can reach no build at all. It is copied rather than widened on purpose: every entry NOT in
# this set falls through to a FULL run, so ADDING one here silently un-builds something. `clients/`
# is deliberately absent (the established selector treats it as ALL, and a client asset can be
# embedded by a host), as are editor dirs — a repo that wants them exempt changes the selector
# both lanes already share, so the two answers cannot drift.
NOOP_DIRS = {"legacy", "e2e", "docs", "app", ".claude", ".worktrees"}

SELECTOR = "scripts/affected-modules.py"        # the caller's NODE graph
PROJECTS = "scripts/project-closure.py"          # the caller's PROJECT graph

# Events whose diff means anything. Everything else runs the full set, each for its own reason.
NARROWABLE = {"pull_request", "pull_request_target", "merge_group"}

FULL_REASONS = {
    "modules": {
        "push": "a push PUBLISHES, and the only sound baseline is its own publication — which the "
                "module lane does not record (the registry is keyed {package}@{version}, and a "
                "version identifies only the CONTENT half, so a src/-only change repacks the same "
                "version with different bytes). Diffing github.event.before instead would silently "
                "skip every module whose commit belonged to a cancelled, superseded or red run. "
                "Packing every module.",
        "repository_dispatch": "a framework-release dispatch moves the platform pin every module is "
                               "built against — every bundle must be rebuilt and republished or "
                               "every portal reads FrameworkDeclined and adopts nothing "
                               "(MeshWeaver#2088). The git diff is irrelevant.",
        "schedule": "the release poll is how this repo learns the platform released; every module "
                    "must be rebuilt and republished against the new framework identity "
                    "(MeshWeaver#2088), so the git diff is irrelevant.",
        "workflow_dispatch": "a manual dispatch has no diff to narrow by — packing every module.",
    },
    "packages": {
        "push": "the publish path's change detector is the DERIVED VERSION, not a diff "
                "(plugin-publish.yml's header): a second detector free to disagree would let a "
                "plugin silently never ship. Packing every plugin.",
        "repository_dispatch": "the published framework moved — this gate's value is being "
                               "EXHAUSTIVE against that floor, because a package that did not "
                               "change can still stop compiling. Packing every plugin.",
        "schedule": "the released-framework floor moves without this repo changing, and a package "
                    "that did not change can still stop compiling against it. Packing every plugin.",
        "workflow_dispatch": "a manual dispatch has no diff to narrow by — packing every plugin.",
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
    skip = {".git", ".github", ".claude", ".worktrees", "scripts", "src", "test", "docs", "e2e",
            "app", "clients", "legacy"}
    try:
        return {d.name for d in root.iterdir()
                if d.is_dir() and d.name not in skip and (d / "index.json").exists()}
    except OSError:
        return set()


def changed_files(root: Path, diff_range: str, say) -> list[str] | None:
    proc = subprocess.run(["git", "diff", "--name-only", diff_range],
                          cwd=root, capture_output=True, text=True, timeout=120)
    if proc.returncode != 0:
        say("    " + proc.stderr.strip()[-2000:])
        return None
    return [line for line in proc.stdout.splitlines() if line.strip()]


# ── the decision ─────────────────────────────────────────────────────────────────────────────

def decide(root: Path, lane: str, entries: list[dict], event: str, diff_range: str | None,
           override: list[str] | None, say) -> dict:
    all_packages = sorted(node_packages(root))
    universe = ([e["module"] for e in entries] if lane == "modules" else all_packages)

    def full(reason: str) -> dict:
        return {"lane": lane, "scope": "full", "reason": reason,
                "modules": entries if lane == "modules" else [],
                "packages": all_packages if lane == "packages" else [],
                "count": len(universe), "selected": sorted(universe), "skipped": [], "why": {}}

    if event not in NARROWABLE:
        return full(FULL_REASONS[lane].get(
            event, f"event '{event}' has no meaningful content diff — running the full set."))
    if not universe:
        return full("there is nothing to select from — running the full set.")
    if not (root / SELECTOR).is_file():
        return full(f"the caller repo ships no {SELECTOR}, so the affected closure cannot be "
                    "computed — running the full set.")

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

    if lane == "packages":
        chosen = sorted(affected & set(all_packages))
        return {"lane": lane, "scope": "narrowed",
                "reason": f"{len(chosen)} of {len(all_packages)} plugin(s) are reachable from this "
                          f"diff ({len(files)} file(s)).",
                "modules": [], "packages": chosen, "count": len(chosen), "selected": chosen,
                "skipped": sorted(set(all_packages) - set(chosen)),
                "why": {p: "in the affected closure" for p in chosen}}

    # ── the modules lane also needs the COMPILED half ──
    # An entry built from outside this checkout (a platform-hosted transition entry) can never be
    # shown unaffected by this repo's diff.
    external = [e for e in entries if not e.get("project", "").startswith("src/")]
    if external:
        return full(f"matrix entry '{external[0].get('module')}' builds "
                    f"{external[0].get('project')}, which is outside this checkout's src/ — its "
                    "reachability cannot be decided from this diff. Packing every module.")

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
        for f in src_files:
            say(f"  {f}  → src/")

    selected: list[dict] = []
    why: dict[str, str] = {}
    for e in entries:
        reasons = []
        if e["package"] in affected:
            reasons.append(f"node package `{e['package']}` is in the affected closure")
        if hits.get(e["project"]):
            reasons.append("compiled closure reaches " + ", ".join(f"`{p}`"
                                                                   for p in hits[e["project"]]))
        if reasons:
            why[e["module"]] = "; ".join(reasons)
            selected.append(e)

    chosen = {e["module"] for e in selected}
    return {"lane": lane, "scope": "narrowed",
            "reason": f"{len(selected)} of {len(entries)} module bundle(s) are reachable from this "
                      f"diff ({len(files)} file(s)).",
            "modules": selected, "packages": [], "count": len(selected),
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
    "KNOWN={p for c in CLOSURE.values() for p in c}\n"
    "a=sys.argv\n"
    "entries=[a[i+1] for i,v in enumerate(a) if v=='--entry']\n"
    "paths=[l for l in open(a[a.index('--changed')+1]).read().splitlines() if l]\n"
    "own=lambda f: next((k for k in KNOWN if f==k or f.startswith(k+'/')), None)\n"
    "hit={own(f) for f in paths if own(f)}\n"
    "unc=sorted(f for f in paths if f.startswith('src/') and not own(f))\n"
    "json.dump({'projects':sorted(KNOWN),'changedProjects':sorted(hit),'unclassified':unc,"
    "'entries':{e:sorted(CLOSURE.get(e,set())&hit) for e in entries}},sys.stdout)\n")

_STUB_SELECTOR = (
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
            ("Acme.Beta", []), ("Acme.Shared", []), ("Acme.Kit", [])):
        d = root / "src" / name
        d.mkdir(parents=True, exist_ok=True)
        (d / f"{name}.csproj").write_text(_csproj(refs), encoding="utf-8")
    (root / "src" / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
    (root / "docs").mkdir(exist_ok=True)
    (root / "README.md").write_text("x\n", encoding="utf-8")


def _run(root: Path, lane: str, event: str, changed: list[str] | None) -> dict:
    argv = [sys.executable, str(Path(__file__).resolve()), "--for", lane,
            "--root", str(root), "--event", event]
    if lane == "modules":
        argv += ["--modules", json.dumps(_MATRIX)]
    if changed is not None:
        argv += ["--changed-list", ",".join(changed)]
    proc = subprocess.run(argv, capture_output=True, text=True, timeout=600)
    if proc.returncode != 0:
        raise SystemExit(f"self-test harness: exit {proc.returncode}\n{proc.stderr}")
    return json.loads(proc.stdout)


def self_test() -> int:
    import tempfile
    failures: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'✓' if ok else '✗'} {name}{'' if ok else ': ' + detail}")
        if not ok:
            failures.append(name)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        _fixture(root)

        print("FULL-run fallbacks — the bias that makes narrowing safe (both lanes):")
        for lane, n in (("modules", 2), ("packages", 3)):
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
                ("src/<file> outside any project (Directory.Build.props) ⇒ ALL",
                 ["src/Directory.Build.props"]),
                ("src/<dir> holding no csproj ⇒ ALL", ["src/Loose/notes.txt"]),
                ("a project that no longer exists (renamed/deleted) ⇒ ALL",
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
        got = _run(root, "packages", "pull_request", ["Beta/Board.json"])
        check("[packages] the pack lane narrows to the same closure",
              got["packages"] == ["Alpha", "Beta"] and got["skipped"] == ["Gamma"],
              f"{got['packages']} skipped={got['skipped']}")

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
        for lane in ("modules", "packages"):
            got = _run(root, lane, "pull_request", ["docs/guide.md"])
            check(f"[{lane}] a diff reaching nothing selects ZERO, and says so",
                  got["scope"] == "narrowed" and got["count"] == 0 and got["skipped"],
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
        (root / "scripts" / "project-closure.py").unlink()
        got = _run(root, "modules", "pull_request", ["src/Acme.Beta/T.cs"])
        check("[modules] no project-closure.py in the caller repo ⇒ ALL",
              got["scope"] == "full" and got["count"] == 2, f"scope={got['scope']}")

    if failures:
        print(f"\n::error title=node-repo-scope self-test failed::{len(failures)} case(s) — this "
              "script decides what is NOT rebuilt; a fallback that stops falling back is a stale "
              "assembly the registry keeps serving, or an in-mesh compile break that reaches main.")
        return 1
    print("\n✓ node-repo-scope self-test: 23 full-run fallbacks, 8 narrowing assertions, "
          "2 explicit-empty answers, 5 caller-graph refusals — all green.")
    return 0


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n", 1)[0])
    p.add_argument("--for", dest="lane", choices=("modules", "packages"))
    p.add_argument("--modules", help="the module matrix as JSON, or @file (--for modules)")
    p.add_argument("--root", default=".", help="the caller repo checkout")
    p.add_argument("--event", default="", help="the triggering GitHub event name")
    p.add_argument("--base-ref", default="", dest="base_ref",
                   help="the PR base branch — the diff is origin/<base-ref>...HEAD")
    p.add_argument("--changed-list", default=None, dest="changed_list",
                   help="comma-separated changed paths instead of a git diff (tests only)")
    p.add_argument("--self-test", action="store_true", dest="self_test")
    args = p.parse_args()
    if args.self_test:
        return self_test()
    if not args.lane:
        p.error("--for {modules,packages} is required")

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
    answer = decide(root, args.lane, entries, args.event,
                    f"origin/{args.base_ref}...HEAD" if args.base_ref else None, changed, say)

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
            fh.write("packages=" + " ".join(answer["packages"]) + "\n")
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
