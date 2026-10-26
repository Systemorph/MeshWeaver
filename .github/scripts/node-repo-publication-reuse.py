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


def publication_reuse(entries, fetch, sha, root, shipped=None):
    """The matrix with publication reuse applied, or the matrix UNCHANGED when it cannot be.

    All or nothing: the annotated list is returned only once coherence has ALSO succeeded. A
    failure after annotation (an unreadable project, a platform witness that fails or times out)
    must not leave the annotations behind, because those are exactly the entries `cohere` had not
    yet cleared, so a reused bundle could sit beside a freshly built one carrying a second build of
    the same assembly. An ArtifactStoreError propagates: a declared store never falls back.
    """
    try:
        run, artifacts = fetch()
        coherent, withdrawn = cohere(annotate(entries, run, sha, artifacts), root, shipped)
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
    print("publication reuse: 24 assertions passed")


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
                entries = publication_reuse(entries, fetch, a.sha, a.root)
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
