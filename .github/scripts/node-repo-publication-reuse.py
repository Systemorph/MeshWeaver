#!/usr/bin/env python3
"""Reuse unchanged gate dependencies from an attested successful publication.

The source selector proves an entry unaffected (test=false). The baseline resolver proves the
toolchain inputs equal. This helper verifies run identity and artifact availability; absence
keeps the normal build leg. It uses the module lane's existing reuse/receipt path, not a builder.
An explicit --artifact-store reads named artifacts only from that store. GitHub still attests run
identity; an unavailable declared store fails, never falls back to GitHub storage or a rebuild.

ONE BUILD PER ASSEMBLY NAME ACROSS THE REUSED AND THE BUILT (MeshWeaver#3732)
A bundle carries its module-owned in-repo siblings: MeshWeaver.AI's bundle carries its own copy of
MeshWeaver.Markdown.Collaboration. "Unaffected" is therefore not enough to reuse an entry. When the
AI sources change and Collaboration's do not, AI is rebuilt in THIS run's compilation and its bundle
carries THIS run's Collaboration, while a reused Essentials bundle carries the publication run's
Collaboration: two compilations, two builds of one assembly name, and the gate refuses the composed
set ("reaches this mesh as more than one build"). Measured on MeshWeaver.Plugins#4253: 0a54b5391a502a2b
riding AI 1.39.2 against 3884878de5cfae41 as Essentials 1.15.17's declared module, reused from main
run 40272452328. So `cohere` withdraws reuse from every entry whose in-repo ProjectReference closure
shares a project with an entry BUILT here, repeating until nothing changes, because a withdrawn entry
is then built and can pull in the next one. Without `--root` the closures cannot be read, so nothing
is reused.

ON A PULL REQUEST THE ENTRY MUST ALSO BE UNCHANGED SINCE THE BASELINE (Plugins Hosting/CiBuildTimeAnalysis.md #3)
A push narrows by the publication baseline, so `test: false` there means "unchanged since the
baseline". A pull request diffs its MERGE-BASE, so `test: false` only means "this pull request did
not change it": main may have changed the module after the baseline published it. So on a run that
does not narrow by the baseline (`--event` pull_request / merge_group) every annotated entry is also
KEYED at the baseline sha and at HEAD with module-build-key.py (the ledger's content address: project
closure, package versions, requires, every Directory.* import and global build input), plus the tree
of every caller project named with --key-project (the shelf, the build extension), and is reused only
when both sides agree. Anything unkeyable withdraws the reuse; a failure of the comparison itself
keeps every normal build.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


class ArtifactStoreError(RuntimeError):
    """A declared artifact store failed; do not silently change storage or retain builds."""


def stored_artifacts(store, repo, run_id, attempt=None, expected_store_id=None):
    """List the source run's named artifacts, preserving expiry and partial-rerun semantics."""
    command = [sys.executable, str(Path(__file__).with_name("ci-run-artifacts.py")), "list",
               "--store", store, "--repository", repo, "--run-id", str(run_id),
               "--include-expired", "true"]
    if attempt is not None:
        command.extend(["--attempt", str(attempt)])
    if expected_store_id is not None:
        command.extend(["--expect-store-id", expected_store_id])
    try:
        result = subprocess.run(command, capture_output=True, text=True, timeout=180, check=True,
                                env={k: v for k, v in os.environ.items() if k != "GITHUB_OUTPUT"})
        listing = json.loads(result.stdout)
        if not isinstance(listing, list):
            raise ValueError("artifact listing is not an array")
        return listing
    except (OSError, ValueError, subprocess.SubprocessError) as exc:
        detail = getattr(exc, "stderr", "") or str(exc)
        raise ArtifactStoreError(f"declared artifact store failed: {detail.strip()[:600]}") from exc


def annotate(entries, run, sha, artifacts):
    if (run.get("status") != "completed" or run.get("conclusion") != "success"
            or run.get("event") != "push" or run.get("head_branch") != "main"
            or run.get("head_sha") != sha or not re.fullmatch(r"[0-9a-f]{40}", sha)):
        return entries
    live = {a["name"] for a in artifacts if not a.get("expired")}
    result = []
    for entry in entries:
        name = f"module-bundle-{entry['module']}"
        if entry.get("test") is False and not entry.get("ledger") and name in live:
            entry = {**entry, "reuse": {"runId": str(run["id"]), "name": name,
                                        "source": run["html_url"]}}
        result.append(entry)
    return result


_INCLUDE = r"<ProjectReference\s[^>]*Include=(?:\"([^\"]+)\"|'([^']+)')"


def project_closure(root, project):
    """Every in-repo project the entry project compiles, itself included, or None if unreadable.

    The edge rule is module-build-key.py's: a `$(…)`-relative reference is the platform's, which
    enters through the image and not through a walk of this repository. None means the closure
    could not be read, and an unreadable closure cannot be shown to share nothing.
    """
    if not project:
        return None
    root = Path(root).resolve()
    start = (root / project).resolve()
    if not start.is_file():
        return None
    seen, queue = set(), [start]
    while queue:
        current = queue.pop()
        if current in seen:
            continue
        seen.add(current)
        text = current.read_text(encoding="utf-8", errors="replace")
        for a, b in re.findall(_INCLUDE, text):
            include = (a or b).strip()
            if "$(" in include:
                continue
            target = (current.parent / include.replace("\\", "/")).resolve()
            try:
                target.relative_to(root)
            except ValueError:
                continue
            if target.is_file():
                queue.append(target)
    return {t.relative_to(root).as_posix() for t in seen}


def platform_shipped(root):
    """The repo's src/MeshWeaver.* project directories the PLATFORM ships, so no bundle carries them.

    Answered by module-owned-platform.sh, the same script the pack's closure and the bundle
    inspection call, so this helper cannot disagree with what a bundle actually carries. This job
    has no platform image, so the script answers from the DECLARED src/platform-shipped.txt and
    says so on stderr. A stale line there can only make a shared project look shipped when it rides;
    the gate's one-build-per-name refusal still catches that case, by name.
    """
    src = Path(root) / "src"
    if not src.is_dir():
        return frozenset()
    script = Path(__file__).with_name("module-owned-platform.sh")
    owned = subprocess.run(["bash", str(script), str(src)], capture_output=True, text=True,
                           timeout=60, check=True)
    sys.stderr.write(owned.stderr)
    owned_names = {n for n in owned.stdout.strip().split(";") if n}
    names = {d.name for d in src.iterdir() if d.is_dir() and (d.name == "MeshWeaver" or d.name.startswith("MeshWeaver."))}
    return frozenset(names - owned_names)


def rides(project, shipped):
    """Whether a bundle carries this in-repo project: everything but a platform-shipped src/<Name>/."""
    parts = project.split("/")
    return not (len(parts) == 3 and parts[0] == "src" and parts[1] in shipped)


def is_reused(entry):
    return bool(entry.get("reuse")) or entry.get("ledger", {}).get("decision") == "reuse"


REUSE_ONLY_EVENTS = frozenset({"pull_request", "merge_group"})


def _load_key_module():
    """module-build-key.py beside this file — the SAME content address the ledger keys builds by."""
    import importlib.util
    path = Path(__file__).with_name("module-build-key.py")
    spec = importlib.util.spec_from_file_location("module_build_key", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def build_inputs(root, entry, extra_projects, mbk):
    """What decides this entry's bundle bytes at `root`: its content address (toolchain constants are
    equal on both sides — the baseline resolver already proved the toolchain) and the tree of each
    caller compiler-input project. Raises ValueError when the entry cannot be keyed there."""
    root = Path(root)
    extras = {}
    for project in extra_projects:
        if not (root / project).is_file():
            raise ValueError(f"{project} does not exist at this tree")
        dirs = mbk.closure_dirs(root, project)
        extras[project] = {"closure": {d: mbk.tree_hash(root, d) for d in dirs},
                           "imports": mbk.directory_imports(root, dirs)}
    return {"key": mbk.compute(root, entry, "-", "-", "-")["key"], "extras": extras}


def unchanged_since_baseline(entries, head_inputs, base_inputs):
    """(entries, withdrawn): keep `reuse` only on an entry whose build inputs are identical at the
    baseline and at HEAD. `head_inputs` / `base_inputs` map an entry to its inputs or raise
    ValueError (unkeyable ⇒ withdrawn, named). Pure apart from the two callables."""
    result, withdrawn = [], []
    for entry in entries:
        if entry.get("reuse"):
            try:
                same = head_inputs(entry) == base_inputs(entry)
                why = "its build inputs changed between the baseline and this tree (main moved it after the baseline published)"
            except ValueError as exc:
                same, why = False, f"it cannot be keyed on both trees ({exc})"
            if not same:
                entry = {k: v for k, v in entry.items() if k != "reuse"}
                withdrawn.append((entry["module"], why))
        result.append(entry)
    return result, withdrawn


def baseline_tree_guard(root, sha, extra_projects):
    """The `since` hook for publication_reuse on a pull request: a worktree at the baseline sha, its
    main-owned locks materialized by the generator beside this script (the one the lane's HEAD used),
    and every annotated entry compared on build_inputs."""
    mbk = _load_key_module()

    def since(entries):
        if not any(e.get("reuse") for e in entries):
            return entries
        with tempfile.TemporaryDirectory(prefix="publication-baseline-") as tmp:
            base = Path(tmp) / "tree"
            subprocess.run(["git", "-C", str(root), "worktree", "add", "--detach", str(base), sha],
                           capture_output=True, text=True, timeout=300, check=True)
            try:
                generator = Path(__file__).with_name("gen-manifests.py")
                if (base / "scripts" / "gen-manifests.config.json").is_file() and generator.is_file():
                    subprocess.run([sys.executable, str(generator), "--materialize"], capture_output=True,
                                   text=True, timeout=600, check=True, env={**os.environ, "MW_REPO_ROOT": str(base)})
                kept, withdrawn = unchanged_since_baseline(
                    entries, lambda e: build_inputs(root, e, extra_projects, mbk),
                    lambda e: build_inputs(base, e, extra_projects, mbk))
            finally:
                subprocess.run(["git", "-C", str(root), "worktree", "remove", "--force", str(base)],
                               capture_output=True, text=True, timeout=120)
        for module, why in withdrawn:
            print(f"publication reuse: BUILD {module}, not reused: {why}", file=sys.stderr)
        print(f"publication reuse: {sum(bool(e.get('reuse')) for e in kept)} entr(y/ies) unchanged since "
              f"the baseline {sha[:12]} (keyed at both trees)", file=sys.stderr)
        return kept
    return since


def cohere(entries, root, shipped=None):
    """Withdraw publication reuse from every entry that shares a riding project with a built one.

    Returns (entries, withdrawn); withdrawn lists (module, built module or None, reasons). A reused
    bundle was compiled in another run, and a bundle built here carries its own copy of every
    module-owned sibling it references, so two closures that meet on a project both bundles carry
    put one assembly name into the composed set at two builds. A project the platform ships rides
    neither bundle, so meeting there is harmless. Ledger reuse is left alone: the ledger owns that
    decision, and this helper only adds the publication's.
    """
    entries = [dict(e) for e in entries]
    if not any(e.get("reuse") for e in entries):
        return entries, []
    if root is None:
        withdrawn = [(e["module"], None, ["no --root, so the project closures cannot be read"])
                     for e in entries if e.get("reuse")]
        return [{k: v for k, v in e.items() if k != "reuse"} for e in entries], withdrawn
    if shipped is None:
        shipped = platform_shipped(root)
    closures = {}
    for e in entries:
        closure = project_closure(root, e.get("project"))
        # The entry's own project is its bundle's declared module, so it rides whatever the list says.
        closures[e["module"]] = None if closure is None else \
            {p for p in closure if rides(p, shipped) or p == e.get("project")}
    withdrawn = []
    changed = True
    while changed:
        changed = False
        built = [e for e in entries if not is_reused(e)]
        for e in entries:
            if not e.get("reuse"):
                continue
            mine = closures[e["module"]]
            for b in built:
                theirs = closures[b["module"]]
                if mine is None or theirs is None:
                    unread = e["module"] if mine is None else b["module"]
                    shared = [f"the project closure of {unread} could not be read"]
                else:
                    shared = sorted(mine & theirs)
                if shared:
                    e.pop("reuse")
                    withdrawn.append((e["module"], b["module"], shared))
                    changed = True
                    break
    return entries, withdrawn


def publication_reuse(entries, fetch, sha, root, shipped=None, since=None):
    """The matrix with publication reuse applied, or the matrix UNCHANGED when it cannot be.

    All or nothing: the annotated list is returned only once coherence has ALSO succeeded. A
    failure after annotation (an unreadable project, a platform witness that fails or times out)
    must not leave the annotations behind, because those are exactly the entries `cohere` had not
    yet cleared, so a reused bundle could sit beside a freshly built one carrying a second build of
    the same assembly. An ArtifactStoreError propagates: a declared store never falls back.
    """
    try:
        run, artifacts = fetch()
        annotated = annotate(entries, run, sha, artifacts)
        if since is not None:
            annotated = since(annotated)
        coherent, withdrawn = cohere(annotated, root, shipped)
    except ArtifactStoreError:
        raise
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        print("::warning::publication artifacts unavailable; retaining normal builds", file=sys.stderr)
        return entries
    for module, built, reasons in withdrawn:
        why = (f"it shares {', '.join(reasons)} with {built}, which this run builds, so the reused "
               f"bundle would carry a second build of the same assembly (MeshWeaver#3732)"
               if built else reasons[0])
        print(f"publication reuse: BUILD {module}, not reused: {why}", file=sys.stderr)
    return coherent


def self_test():
    entries = [{"module": "Floor", "test": False}, {"module": "Changed", "test": True}]
    run = dict(status="completed", conclusion="success", event="push", head_branch="main",
               head_sha="a" * 40, id=10, html_url="https://example.test/runs/10")
    artifacts = [{"name": "module-bundle-Floor", "expired": False}]
    got = annotate(entries, run, "a" * 40, artifacts)
    assert got[0]["reuse"]["runId"] == "10" and "reuse" not in got[1]
    for change in [dict(conclusion="failure"), dict(event="pull_request"), dict(status="in_progress"),
                   dict(head_branch="other"), dict(head_sha="b" * 40)]:
        assert annotate(entries, {**run, **change}, "a" * 40, artifacts) == entries
    assert annotate(entries, run, "a" * 40, []) == entries
    assert annotate(entries, run, "a" * 40, [{**artifacts[0], "expired": True}]) == entries
    assert annotate([{**entries[0], "ledger": {"decision": "build"}}], run, "a" * 40, artifacts)[0].get("reuse") is None
    # The wiring, not only the annotation: the lane's "Which selected modules still owe a test
    # run" step writes final.json — the list the pack legs are cut from — and it MUST read this
    # step's annotated output. Cut from the pre-annotation list (Plugins#1853), every reused
    # entry's leg planned BUILD against a global build that had honoured the reuse and emitted
    # nothing; this pins the env line so that regression cannot return in silence.
    lane = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "workflows",
                        "node-repo-module-pack.yml")
    if os.path.exists(lane):
        with open(lane, encoding="utf-8") as fh:
            text = fh.read()
        head = text.split('> "$RUNNER_TEMP/final.json"', 1)[0]
        selection = re.findall(r"SELECTION: \$\{\{ (steps\.[a-z-]+\.outputs\.modules) \}\}", head)
        assert selection and selection[-1] == "steps.reuse.outputs.modules", \
            f"final.json must be cut from the reuse-annotated list, not {selection}"
    # One build per assembly name across the reused and the built (MeshWeaver#3732), in the
    # Plugins#4253 shape: AI changed and references Collab, Collab did not change, Maps shares
    # nothing. Collab must be BUILT with AI, and Maps may still be reused.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)

        def proj(name, *refs):
            (root / "src" / name).mkdir(parents=True)
            items = "".join(f'<ProjectReference Include="../{r}/{r}.csproj" />' for r in refs)
            items += '<ProjectReference Include="$(MeshWeaverRoot)/src/Platform/Platform.csproj" />'
            (root / "src" / name / f"{name}.csproj").write_text(
                f"<Project><ItemGroup>{items}</ItemGroup></Project>")

        def entry(module, test):
            return {"module": module, "project": f"src/{module}/{module}.csproj", "test": test}

        proj("Collab")
        proj("AI", "Collab")
        proj("Maps")
        proj("Doc", "Collab")
        proj("Shared")
        proj("Wide", "Collab", "Shared")
        proj("Narrow", "Shared")
        live = [{"name": f"module-bundle-{m}", "expired": False}
                for m in ("Collab", "AI", "Maps", "Doc", "Wide", "Narrow")]
        got, withdrawn = cohere(annotate([entry("Collab", False), entry("AI", True), entry("Maps", False)],
                                         run, "a" * 40, live), root)
        by = {e["module"]: e for e in got}
        assert "reuse" not in by["Collab"], "a sibling riding a built bundle must be built with it"
        assert "reuse" in by["Maps"], "an entry sharing no project with a built one stays reused"
        assert withdrawn == [("Collab", "AI", ["src/Collab/Collab.csproj"])], withdrawn
        # Transitive: Narrow shares nothing with AI, only Shared with Wide. Wide is withdrawn because it
        # shares Collab with AI, and once Wide is built here Narrow's reused Shared would be a second
        # build, so Narrow goes on the next pass.
        got, withdrawn = cohere(annotate([entry("Narrow", False), entry("AI", True), entry("Wide", False)],
                                         run, "a" * 40, live), root)
        assert not any("reuse" in e for e in got), got
        assert [w[:2] for w in withdrawn] == [("Wide", "AI"), ("Narrow", "Wide")], withdrawn
        # Nothing built: nothing can diverge, so every unaffected entry stays reused.
        got, _ = cohere(annotate([entry("Collab", False), entry("Doc", False)], run, "a" * 40, live), root)
        assert all("reuse" in e for e in got), got
        # An unreadable closure cannot be shown to share nothing, and without a root nothing is reused.
        ghost = {"module": "Ghost", "project": "src/Ghost/Ghost.csproj", "test": False}
        got, _ = cohere(annotate([ghost, entry("AI", True)], run, "a" * 40,
                                 [{"name": "module-bundle-Ghost"}]), root)
        assert "reuse" not in got[0], got
        got, _ = cohere(annotate([entry("Maps", False)], run, "a" * 40, live), None)
        assert "reuse" not in got[0], got
        # A project the platform ships rides no bundle (module-owned-platform.sh), so two closures
        # meeting only there stay apart: Plugins' MeshWeaver.Blazor is referenced by nearly every
        # module and would otherwise withdraw almost every reuse whenever anything is built.
        proj("MeshWeaver.Shell")
        proj("MeshWeaver.Owned")
        proj("MeshWeaver.Front", "MeshWeaver.Shell")
        proj("MeshWeaver.Side", "MeshWeaver.Shell")
        proj("MeshWeaver.Back", "MeshWeaver.Owned")
        (root / "src" / "platform-shipped.txt").write_text("# the image ships these\nMeshWeaver.Shell\n")
        live += [{"name": f"module-bundle-MeshWeaver.{m}"} for m in ("Side", "Back")]
        got, withdrawn = cohere(annotate([entry("MeshWeaver.Front", True), entry("MeshWeaver.Side", False)],
                                         run, "a" * 40, live), root)
        assert "reuse" in got[1] and not withdrawn, (got, withdrawn)
        got, withdrawn = cohere(annotate([entry("MeshWeaver.Owned", True), entry("MeshWeaver.Back", False)],
                                         run, "a" * 40, live), root)
        assert "reuse" not in got[1], "an owned project rides, so sharing it still withdraws"
        # All or nothing: a coherence failure AFTER annotation (an unreadable project, a platform
        # witness that fails or times out) keeps the normal builds, never the annotations it had
        # not yet cleared (Collab would otherwise stay reused beside the AI built here).
        class Unreadable:
            def __contains__(self, name):
                raise OSError("platform witness failed")
        fetch = lambda: (run, live)
        plain = [entry("Collab", False), entry("AI", True)]
        got = publication_reuse(plain, fetch, "a" * 40, root, Unreadable())
        assert not any("reuse" in e for e in got), got
        got = publication_reuse(plain, fetch, "a" * 40, root)
        assert [("reuse" in e) for e in got] == [False, False], got
        got = publication_reuse([entry("Maps", False), entry("AI", True)], fetch, "a" * 40, root)
        assert "reuse" in got[0], "the success path still reuses"

        def store_down():
            raise ArtifactStoreError("declared store unavailable")
        try:
            publication_reuse(plain, store_down, "a" * 40, root)
            raise AssertionError("a declared store failure must not fall back")
        except ArtifactStoreError:
            pass
    # ON A PULL REQUEST: reuse survives only where the build inputs agree at the baseline and HEAD.
    reused = [{"module": "Same", "reuse": {"runId": "10"}}, {"module": "Moved", "reuse": {"runId": "10"}},
              {"module": "Ghost", "reuse": {"runId": "10"}}, {"module": "Built"}]

    def head(e):
        return {"Same": "k1", "Moved": "k2", "Ghost": "k3", "Built": "k4"}[e["module"]]

    def base(e):
        if e["module"] == "Ghost":
            raise ValueError("no such project at the baseline")
        return {"Same": "k1", "Moved": "OLD", "Built": "k4"}[e["module"]]
    got, withdrawn = unchanged_since_baseline(reused, head, base)
    assert [("reuse" in e) for e in got] == [True, False, False, False], got
    assert [w[0] for w in withdrawn] == ["Moved", "Ghost"], withdrawn
    assert "cannot be keyed" in withdrawn[1][1], withdrawn
    # The hook runs BEFORE coherence, inside the all-or-nothing envelope: a hook that fails keeps
    # every normal build, and a withdrawn entry is built (so cohere sees it as built).
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        for name in ("A", "B"):
            (root / "src" / name).mkdir(parents=True)
            (root / "src" / name / f"{name}.csproj").write_text("<Project/>")
        live = [{"name": "module-bundle-A"}, {"name": "module-bundle-B"}]
        mk = lambda m: {"module": m, "project": f"src/{m}/{m}.csproj", "test": False}  # noqa: E731
        drop_b = lambda es: [({k: v for k, v in e.items() if k != "reuse"} if e["module"] == "B" else e) for e in es]  # noqa: E731
        got = publication_reuse([mk("A"), mk("B")], lambda: (run, live), "a" * 40, root, frozenset(), since=drop_b)
        assert [("reuse" in e) for e in got] == [True, False], got

        def broken(es):
            raise subprocess.CalledProcessError(128, ["git", "worktree"])
        got = publication_reuse([mk("A"), mk("B")], lambda: (run, live), "a" * 40, root, frozenset(), since=broken)
        assert not any("reuse" in e for e in got), "a failed baseline comparison keeps every normal build"
        # build_inputs, over the real content address: equal trees agree, a moved closure does not.
        mbk = _load_key_module()
        for t in ("head", "base"):
            (root / t / "src" / "A").mkdir(parents=True)
            (root / t / "src" / "A" / "A.csproj").write_text("<Project/>")
            (root / t / "Pkg").mkdir()
            (root / t / "Pkg" / "manifest.lock").write_text('{"moduleVersion": "m1"}')
        entry = {"package": "Pkg", "module": "A", "project": "src/A/A.csproj"}
        assert build_inputs(root / "head", entry, [], mbk) == build_inputs(root / "base", entry, [], mbk)
        (root / "head" / "src" / "A" / "Code.cs").write_text("class C {}")
        assert build_inputs(root / "head", entry, [], mbk) != build_inputs(root / "base", entry, [], mbk), \
            "NEGATIVE CONTROL: a source file main added after the baseline is a different build"
        (root / "base" / "src" / "A" / "Code.cs").write_text("class C {}")
        (root / "head" / "src" / "Shelf").mkdir()
        (root / "head" / "src" / "Shelf" / "Shelf.csproj").write_text("<Project/>")
        try:
            build_inputs(root / "base", entry, ["src/Shelf/Shelf.csproj"], mbk)
            raise AssertionError("a key project absent at the baseline must not key on nothing")
        except ValueError:
            pass
    print("publication reuse: 33 assertions passed")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--matrix")
    p.add_argument("--run", default="")
    p.add_argument("--sha", default="")
    p.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    p.add_argument("--artifact-store", default="",
                   help="explicit named artifact store; empty/gha keeps GitHub storage, no fallback when declared")
    p.add_argument("--root", default=None,
                   help="the repository checkout the matrix projects resolve against; without it nothing is reused")
    p.add_argument("--event", default=os.environ.get("GITHUB_EVENT_NAME", ""),
                   help="the run's event; pull_request / merge_group also key every entry at the baseline and HEAD")
    p.add_argument("--key-project", action="append", default=[],
                   help="a caller compiler-input project (shelf, build extension) whose tree must also be unchanged since the baseline on a pull request; repeatable")
    p.add_argument("--self-test", action="store_true")
    p.add_argument("--expect-store-id", default=None, help="physical store identity resolved by this run's first producer")
    a = p.parse_args()
    if a.self_test:
        self_test()
        return 0
    entries = json.loads(a.matrix)
    own_store = bool(a.artifact_store and a.artifact_store != "gha")
    if own_store:
        try:
            # Check the declared mount even when no unchanged module needs a historical artifact.
            stored_artifacts(a.artifact_store, a.repo, os.environ.get("GITHUB_RUN_ID", "1"),
                             expected_store_id=a.expect_store_id)
        except ArtifactStoreError as exc:
            print(f"::error::{exc}", file=sys.stderr)
            return 1
    if a.run and a.sha and any(e.get("test") is False for e in entries):
        if not a.run.isdigit() or not re.fullmatch(r"[\w.-]+/[\w.-]+", a.repo):
            print("::warning::publication artifacts unavailable; retaining normal builds", file=sys.stderr)
        else:
            def api(path, *args):
                return json.loads(subprocess.run(["gh", "api", path, *args], capture_output=True,
                                                 text=True, timeout=60, check=True).stdout)

            def fetch():
                run = api(f"repos/{a.repo}/actions/runs/{a.run}")
                if own_store:
                    return run, stored_artifacts(a.artifact_store, a.repo, a.run, run.get("run_attempt"),
                                                 a.expect_store_id)
                pages = api(f"repos/{a.repo}/actions/runs/{a.run}/artifacts?per_page=100", "--paginate", "--slurp")
                return run, [art for page in pages for art in page["artifacts"]]
            try:
                since = None
                if a.event in REUSE_ONLY_EVENTS:
                    since = (baseline_tree_guard(Path(a.root), a.sha, [k for k in a.key_project if k])
                             if a.root else (lambda es: [{k: v for k, v in e.items() if k != "reuse"} for e in es]))
                entries = publication_reuse(entries, fetch, a.sha, a.root, since=since)
            except ArtifactStoreError as exc:
                print(f"::error::{exc}", file=sys.stderr)
                return 1
    build = [e for e in entries if not e.get("reuse") and e.get("ledger", {}).get("decision") != "reuse"]
    print(f"publication reuse: {sum(bool(e.get('reuse')) for e in entries)} unchanged gate dependencies", file=sys.stderr)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"modules={json.dumps(entries)}\nbuild={json.dumps(build)}\n")
    print(json.dumps(entries))
    return 0


if __name__ == "__main__":
    sys.exit(main())
