#!/usr/bin/env python3
"""Select compiled host builds and suites using the caller's project graph and input policy.

Policy is a nonempty list of {project, kind: build|test, inputs: [repo-relative prefixes]}.
Inputs name runtime source scans that ProjectReference cannot express. Declared linked content
is read from every project in the closure. Unknown paths and unresolved scope run everything.
This selects validation only; it never decides what gets published.
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
    packages = scope.node_packages(root)
    noop_dirs, _ = scope.resolve_noop_dirs(root)
    if noop_dirs is None:
        return answer(policy, "content classifier unavailable — full validation")
    relevant = []
    for path in files:
        if path in scope.NOOP_FILES or path.split("/", 1)[0] in noop_dirs:
            relevant.append(path)  # explicit runtime inputs can still consume documentation
            continue
        if path.startswith("src/"):
            if not any(under(path, project) for project in graph):
                return answer(policy, f"unclassified compiled input {path} — full validation")
        elif path.split("/", 1)[0] not in packages:
            return answer(policy, f"shared or unknown input {path} — full validation")
        relevant.append(path)
    chosen = []
    for entry in policy:
        closure = projects.forward(projects.entry_dir(entry["project"]), graph)
        prefixes = list(closure) + entry.get("inputs", [])
        # Linked files in node packages are dependencies too (e.g. Cornerstone's test data).
        for directory in closure:
            for project in (root / directory).glob("*.csproj"):
                source = project.read_text()
                for raw in re.findall(r'<(?:Compile|Content|EmbeddedResource|None)\s[^>]*Include=["\']([^"\']+)', source):
                    raw = raw.replace("\\", "/")
                    if "$(" in raw or not raw.startswith("../"):
                        continue
                    literal = re.split(r"[*?]", raw, maxsplit=1)[0]
                    try:
                        prefix = (project.parent / literal).resolve().relative_to(root.resolve()).as_posix()
                    except ValueError:
                        continue  # outside this checkout: the pinned platform is its input
                    prefixes.append(prefix)
        if any(under(path, prefix) for path in relevant for prefix in prefixes):
            chosen.append(entry)
    return answer(chosen, "compiled project closure plus declared runtime/linked inputs")


def self_test():
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        scope = load(Path(__file__).with_name("node-repo-scope.py"), "scope_fixture")
        scope._fixture(root)
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
        assert select(root, policy, None)["count"] == 2
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
    print("compiled project scope: 16 assertions passed")


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
