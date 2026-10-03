#!/usr/bin/env python3
"""Select compiled host builds and suites using the caller's project graph and input policy.

Policy is a nonempty list of {project, kind: build|test, inputs: [repo-relative prefixes or globs]}.
Inputs name runtime source scans that ProjectReference cannot express. Declared linked content
is read from every project in the closure. Unknown paths and unresolved scope run everything.
This selects validation only; it never decides what gets published.

Two precisions (2026-10-03, MeshWeaver.Plugins — "should only run test of delta plugin not all";
measured on Plugins#2734, which changed only a vendored Monaco bundle under
src/MeshWeaver.Blazor/wwwroot/ and owed 14 portal-host suites, four 22-minute Blazor legs among them):

  * An input or a linked include holding `*` or `?` is a GLOB, matched whole (`input_covers`):
    `**/` spans zero or more directories, `**` anything, `*` and `?` stay inside one segment. A
    linked `../X/**/*.razor` reads Razor markup, not X's `wwwroot/`; an input `src/**/*.cs` is a
    census of C# files, not of every vendored asset. A prefix stays a prefix. The caller's
    scripts/ci-tests.py reads both with the same grammar, so the reconcile agrees by construction.
  * A STATIC WEB ASSET (`<project>/wwwroot/...`) of a project the caller declares IMAGE-SHIPPED
    (its project-closure.py `platform_shipped`, i.e. src/platform-shipped.txt) is read by no
    compiler and carried by no module bundle, so it does not reach a project merely by being in
    that project's compiled closure: only the owner itself, its sibling `<owner>.Test`, and an
    entry that LINKS it or DECLARES it as an input. A caller without that helper keeps the plain
    closure rule (the loud direction).
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def load(path, name):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def under(path, prefix):
    return prefix == "." or path == prefix.rstrip("/") or path.startswith(prefix.rstrip("/") + "/")


def _glob_re(pattern):
    out, i = [], 0
    while i < len(pattern):
        if pattern.startswith("**/", i):
            out.append("(?:.*/)?"); i += 3
        elif pattern.startswith("**", i):
            out.append(".*"); i += 2
        elif pattern[i] == "*":
            out.append("[^/]*"); i += 1
        elif pattern[i] == "?":
            out.append("[^/]"); i += 1
        else:
            out.append(re.escape(pattern[i])); i += 1
    return re.compile("".join(out))


def input_covers(path, pattern):
    """A declared input or linked include — a prefix, `.`, or a glob — covers this path."""
    if "*" in pattern or "?" in pattern:
        return _glob_re(pattern).fullmatch(path) is not None
    return under(path, pattern)


def linked_pattern(root, project, raw):
    """One `../` include as a repo-relative path or glob, its wildcard part kept; None if not a link."""
    raw = raw.strip().replace("\\", "/")
    if not raw or "$(" in raw or "%(" in raw or not raw.startswith("../"):
        return None
    wild = re.search(r"[*?]", raw)
    cut = len(raw) if wild is None else raw.rfind("/", 0, wild.start()) + 1
    try:
        base = (project.parent / raw[:cut]).resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return None  # outside this checkout: the pinned platform is its input
    rest = raw[cut:]
    if not rest:
        return base
    return (base.rstrip("/") + "/" if base not in ("", ".") else "") + rest


def select(root, policy, files):
    if not isinstance(policy, list) or not policy:
        raise ValueError("project policy must be a nonempty list")
    seen = set()
    for entry in policy:
        if (entry.get("kind") not in ("build", "test") or not entry.get("project")
                or entry["project"] in seen or not (root / entry["project"]).is_file()
                or not isinstance(entry.get("inputs", []), list)):
            raise ValueError(f"invalid, duplicate or absent project: {entry}")
        seen.add(entry["project"])
        for prefix in entry.get("inputs", []):
            if not isinstance(prefix, str) or not prefix or prefix.startswith("/") or ".." in prefix.split("/"):
                raise ValueError(f"invalid input prefix: {prefix}")

    def answer(entries, reason):
        return {"reason": reason, "build": [e["project"] for e in entries if e["kind"] == "build"],
                "test": [e["project"] for e in entries if e["kind"] == "test"],
                "count": len(entries), "total": len(policy)}

    if not files:
        return answer(policy, "no reliable diff — full validation")
    try:
        scope = load(Path(__file__).with_name("node-repo-scope.py"), "node_scope")
        projects = load(root / scope.PROJECTS, "caller_projects")
        graph = projects.graph_of(root)
    except (OSError, AttributeError, ValueError, ImportError, SyntaxError):
        return answer(policy, "project graph unavailable — full validation")
    shipped_of = getattr(projects, "platform_shipped", None)
    try:
        shipped = set(shipped_of(root)) if callable(shipped_of) else set()
    except (OSError, ValueError):
        shipped = set()
    packages = scope.node_packages(root)
    noop_dirs, _ = scope.resolve_noop_dirs(root)
    if noop_dirs is None:
        return answer(policy, "content classifier unavailable — full validation")
    relevant = []
    for path in files:
        # 🚨 `<Package>/manifest.lock` is GENERATED — a function of the package's other files, so
        # whatever really changed is in this same diff under its own path, and the lock alone
        # selects nothing. Every trunk merge regenerates the locks of every module main touched
        # (the caller's post-merge hook), so without this a PR that never touched Store/ handed
        # `Store/` — the declared input of MeshWeaver.PluginCatalog.Test — a changed path, the
        # caller's classifier (which already treats locks as no-op) expected no such suite, and
        # the reconcile went red on every trunk-merged pull request (Plugins#1857, #1859).
        if path.rsplit("/", 1)[-1] == "manifest.lock":
            continue
        if path in scope.NOOP_FILES or path.split("/", 1)[0] in noop_dirs:
            relevant.append(path)  # explicit runtime inputs can still consume documentation
            continue
        if path.startswith("src/"):
            if not any(under(path, project) for project in graph):
                return answer(policy, f"unclassified compiled input {path} — full validation")
        elif path.split("/", 1)[0] not in packages:
            return answer(policy, f"shared or unknown input {path} — full validation")
        relevant.append(path)
    # A static web asset of an image-shipped project reaches no referencing project's closure.
    asset_owner = {}
    for path in relevant:
        if path.startswith("src/"):
            owner = max((p for p in graph if under(path, p)), key=len, default=None)
            if owner and owner.rsplit("/", 1)[-1] in shipped and path.startswith(owner + "/wwwroot/"):
                asset_owner[path] = owner
    chosen = []
    for entry in policy:
        entry_dir = projects.entry_dir(entry["project"])
        closure = projects.forward(entry_dir, graph)
        reads = list(entry.get("inputs", []))
        # Linked files in node packages are dependencies too (e.g. Cornerstone's test data).
        for directory in closure:
            for project in (root / directory).glob("*.csproj"):
                source = project.read_text()
                for attr in re.findall(r'<(?:Compile|Content|EmbeddedResource|None)\s[^>]*Include=["\']([^"\']+)', source):
                    for raw in attr.split(";"):
                        pattern = linked_pattern(root, project, raw)
                        if pattern is not None:
                            reads.append(pattern)

        def reaches(path):
            owner = asset_owner.get(path)
            if owner is not None:
                if entry_dir in (owner, owner + ".Test"):
                    return True
            elif any(under(path, d) for d in closure):
                return True
            return any(input_covers(path, r) for r in reads)

        if any(reaches(path) for path in relevant):
            chosen.append(entry)
    return answer(chosen, "compiled project closure plus declared runtime/linked inputs")


def self_test():
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        scope = load(Path(__file__).with_name("node-repo-scope.py"), "scope_fixture")
        scope._fixture(root)
        _ENTRY_SOURCE = (root / scope._ENTRY_A["project"]).read_text()
        # A tiny real project graph; the production graph is owned by the caller.
        (root / scope.PROJECTS).write_text('''from pathlib import Path
def graph_of(root):
    return {"src/Acme.Alpha": {"src/Acme.Shared"}, "src/Acme.Shared": set(), "src/Acme.Beta": set()}
def entry_dir(path): return str(Path(path).parent)
def forward(seed, graph): return {seed} | graph.get(seed, set())
''')
        a = {"project": scope._ENTRY_A["project"], "kind": "build"}
        b = {"project": scope._ENTRY_B["project"], "kind": "test", "inputs": ["Beta/"]}
        policy = [a, b]
        assert select(root, policy, ["Alpha/Lesson.md"])["count"] == 0
        assert select(root, policy, ["Beta/Lesson.md"])["test"] == [b["project"]]
        assert select(root, policy, ["src/Acme.Shared/Thing.cs"])["build"] == [a["project"]]
        assert select(root, policy, ["src/Acme.Beta/Test.cs"])["test"] == [b["project"]]
        for path in ["src/Directory.Build.props", "scripts/gate.py", "Gone/index.json", ".github/workflows/ci.yml"]:
            assert select(root, policy, [path])["count"] == 2
        assert select(root, policy, ["README.md"])["count"] == 0
        # A generated lock under a declared input selects nothing on its own; beside a real
        # change it neither adds nor hides anything.
        assert select(root, policy, ["Beta/manifest.lock"])["count"] == 0
        assert select(root, policy, ["Beta/manifest.lock", "Beta/Lesson.md"])["test"] == [b["project"]]
        assert select(root, policy, None)["count"] == 2
        # GLOB inputs: a census of one file kind is not a read of every file under its root.
        g = {**b, "inputs": ["src/**/*.cs"]}
        assert select(root, [a, g], ["src/Acme.Shared/Thing.cs"])["test"] == [b["project"]]
        assert select(root, [a, g], ["src/Acme.Shared/wwwroot/x.js"])["test"] == []
        assert input_covers("src/C.cs", "src/**/*.cs") and not input_covers("src/A/b.js", "src/**/*.cs")
        assert input_covers("Store/Core/Source/X.cs", "Store/*/Source/*.cs") and not input_covers("Store/Core/Source/D/X.cs", "Store/*/Source/*.cs")
        # A STATIC ASSET of an IMAGE-SHIPPED project (Plugins#2734): Alpha compiles Acme.Shared, but
        # no compiler reads Shared's wwwroot/ and no bundle carries it — unless the caller does not
        # declare Shared image-shipped, in which case the closure rule stands.
        assert select(root, policy, ["src/Acme.Shared/wwwroot/lib/monaco.js"])["build"] == [a["project"]]
        (root / scope.PROJECTS).write_text((root / scope.PROJECTS).read_text()
                                           + "def platform_shipped(root): return {'Acme.Shared'}\n")
        assert select(root, policy, ["src/Acme.Shared/wwwroot/lib/monaco.js"])["count"] == 0
        assert select(root, policy, ["src/Acme.Shared/wwwroot/lib/monaco.js", "src/Acme.Shared/Thing.cs"])["build"] == [a["project"]]
        assert select(root, policy, ["src/Acme.Shared/wwwrootish/x.js"])["build"] == [a["project"]]
        linker = {**a, "inputs": []}
        (root / a["project"]).write_text('<Project><Content Include="../Acme.Shared/wwwroot/lib/**" /></Project>')
        assert select(root, [linker, b], ["src/Acme.Shared/wwwroot/lib/monaco.js"])["build"] == [a["project"]]
        # A GLOB link reads what it names — `**/*.razor` is not the asset folder; `;` separates links.
        (root / a["project"]).write_text('<Project><Content Include="../Acme.Shared/**/*.razor;../Acme.Shared/**/*.razor.cs" /></Project>')
        assert select(root, [linker, b], ["src/Acme.Shared/wwwroot/lib/monaco.js"])["count"] == 0
        assert select(root, [linker, b], ["src/Acme.Shared/View.razor.cs"])["build"] == [a["project"]]
        (root / a["project"]).write_text(_ENTRY_SOURCE)
        project = root / a["project"]
        project.write_text('<Project><Content Include="../../Alpha/**" /></Project>')
        assert select(root, policy, ["Alpha/Lesson.md"])["build"] == [a["project"]]
        for bad in [[], [a, a], [{**a, "project": "src/Missing/Missing.csproj"}]]:
            try:
                select(root, bad, ["Alpha/Lesson.md"])
            except ValueError:
                pass
            else:
                raise AssertionError("invalid policy accepted")
        # Load the actual selector from a separate directory: missing or broken helper files
        # must broaden validation, never turn an unresolved graph into an empty selection.
        isolated = root / "isolated"
        isolated.mkdir()
        selector = isolated / Path(__file__).name
        selector.write_text(Path(__file__).read_text())
        fallback = load(selector, "isolated_selector")
        assert fallback.select(root, policy, ["Alpha/Lesson.md"])["count"] == 2
        (isolated / "node-repo-scope.py").write_text("this is invalid Python !")
        assert fallback.select(root, policy, ["Alpha/Lesson.md"])["count"] == 2
    print("compiled project scope: 29 assertions passed")


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--root", default=".")
    p.add_argument("--policy")
    p.add_argument("--range")
    p.add_argument("--self-test", action="store_true")
    a = p.parse_args()
    if a.self_test:
        self_test()
        return 0
    if not a.policy:
        p.error("--policy is required")
    root = Path(a.root).resolve()
    files = None
    if a.range:
        diff = subprocess.run(["git", "diff", "--no-renames", "--name-only", a.range], cwd=root,
                              capture_output=True, text=True, timeout=120)
        if diff.returncode == 0:
            files = diff.stdout.splitlines()
    result = select(root, json.loads(Path(a.policy).read_text()), files)
    print(json.dumps(result))
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            for key in ("build", "test", "count"):
                out.write(f"{key}={json.dumps(result[key])}\n")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as out:
            out.write(f"### Compiled validation\n\n{result['count']} of {result['total']} projects: "
                      f"{result['reason']}\n\nBuild: {result['build']}\n\nTest: {result['test']}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
