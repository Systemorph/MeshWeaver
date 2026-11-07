#!/usr/bin/env python3
"""msbuild-closure-key.py — the CONTENT ADDRESS of one project's build inputs in a git checkout.

    python3 msbuild-closure-key.py --repo <checkout> --project <path/to/X.csproj> [--salt TEXT]
    python3 msbuild-closure-key.py --self-test

Prints `key=<sha256>` (and writes it to $GITHUB_OUTPUT when set) plus one line per input.

WHY (MeshWeaver.Plugins Hosting/CiProfile.md #4). node-repo-module-pack.yml's `prepare` keeps the
module-pack tool in the run's store so the lane stops re-publishing it on every run. Keyed by the
platform COMMIT, the key changed on every core merge — measured 2026-11-07: two runs ten minutes
apart (42420165495, 42420841473) held different platform commits and both missed. The tool is a
function of far less than the whole commit: its project, every project it reaches through
ProjectReference (transitively), and the repository-level MSBuild inputs. This hashes exactly that.

THE KEY = sha256 over, in order:
  * the salt (the caller's toolchain tuple: runner OS/arch + SDK line);
  * for every project directory in the ProjectReference closure (sorted): `<dir> <git tree hash>`;
  * for every repository-ROOT file named Directory.*.props|targets, *.targets, *.props or
    global.json (sorted), and every Directory.Build.*/Directory.Packages.* on the path from the root
    to each closure project: `<path> <git blob hash>`; an ABSENT implicit file is not an input.

REFUSED (exit 2, no key): a closure project that `git` cannot resolve at HEAD, and any project file
that includes or imports a path OUTSIDE its own directory (`Include="..`, `Import Project="..`,
`Link=`) other than a ProjectReference — such a file would be a build input this key does not see.
A caller treats a refusal as "no cache", never as a red.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path, PurePosixPath

PROJECT_REF = re.compile(r'<ProjectReference\s+Include="([^"]+)"', re.I)
OUTSIDE = re.compile(r'(?:<(?!ProjectReference)\w+\s+[^>]*Include="\.\.[\\/])|(?:<Import\s+Project="\.\.[\\/])|(?:\bLink=")', re.I)
ROOT_FILE = re.compile(r'^(?:Directory\.[^/]+\.(?:props|targets)|[^/]+\.(?:props|targets)|global\.json)$', re.I)
PATH_FILES = ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props")


class Refused(Exception):
    pass


def git(repo: Path, *args: str) -> str:
    r = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)
    if r.returncode != 0:
        raise Refused(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout


def object_hash(repo: Path, path: str) -> str | None:
    r = subprocess.run(["git", "-C", str(repo), "rev-parse", "--verify", "-q", f"HEAD:{path}"],
                       capture_output=True, text=True)
    return r.stdout.strip() if r.returncode == 0 else None


def closure(repo: Path, project: str) -> list[str]:
    seen: set[str] = set()
    stack = [str(PurePosixPath(project))]
    while stack:
        p = stack.pop()
        if p in seen:
            continue
        body = git(repo, "show", f"HEAD:{p}")
        if OUTSIDE.search(body):
            raise Refused(f"{p} includes or imports a file outside its own directory — not a closed input set")
        seen.add(p)
        for ref in PROJECT_REF.findall(body):
            stack.append(os.path.normpath(os.path.join(os.path.dirname(p), ref.replace("\\", "/"))).replace(os.sep, "/"))
    return sorted(seen)


def key(repo: Path, project: str, salt: str = "") -> tuple[str, list[str]]:
    lines = [f"salt {salt}"]
    projects = closure(repo, project)
    for d in sorted({str(PurePosixPath(p).parent) for p in projects}):
        h = object_hash(repo, d)
        if not h:
            raise Refused(f"{d} is not a directory at HEAD")
        lines.append(f"dir {d} {h}")
    files: set[str] = {f for f in git(repo, "ls-tree", "--name-only", "HEAD").split() if ROOT_FILE.match(f)}
    for p in projects:
        parts = PurePosixPath(p).parent.parts
        for i in range(1, len(parts) + 1):
            for name in PATH_FILES:
                files.add(str(PurePosixPath(*parts[:i], name)))
    for f in sorted(files):
        h = object_hash(repo, f)
        if h:
            lines.append(f"file {f} {h}")
    return hashlib.sha256("\n".join(lines).encode()).hexdigest(), lines


def self_test() -> int:
    failures: list[str] = []

    def check(label: str, ok: bool) -> None:
        print(f"  {'✓' if ok else '✗'} {label}")
        if not ok:
            failures.append(label)

    with tempfile.TemporaryDirectory() as tmp:
        repo = Path(tmp)
        g = lambda *a: subprocess.run(["git", "-C", str(repo), *a], check=True, capture_output=True)  # noqa: E731

        def write(rel: str, text: str) -> None:
            f = repo / rel
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_text(text, encoding="utf-8")

        g("init", "-q"); g("config", "user.email", "t@example.com"); g("config", "user.name", "t")
        write("global.json", "{}")
        write("Directory.Packages.props", "<Project/>")
        write("src/Tool/Tool.csproj", '<Project><ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup></Project>')
        write("src/Tool/Program.cs", "class P {}")
        write("src/Lib/Lib.csproj", "<Project/>")
        write("src/Lib/A.cs", "class A {}")
        write("src/Other/Other.csproj", "<Project/>")
        write("src/Other/O.cs", "class O {}")
        g("add", "."); g("commit", "-qm", "c1")
        k1, lines = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")
        check("the closure holds the project and what it references, transitively",
              any("dir src/Tool " in l for l in lines) and any("dir src/Lib " in l for l in lines))
        write("src/Other/O.cs", "class O { int x; }"); g("commit", "-qam", "c2")
        check("a change OUTSIDE the closure keeps the key", key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0] == k1)
        write("src/Lib/A.cs", "class A { int y; }"); g("commit", "-qam", "c3")
        k3 = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0]
        check("a change in a REFERENCED project changes the key", k3 != k1)
        write("Directory.Packages.props", "<Project><!-- pin --></Project>"); g("commit", "-qam", "c4")
        k4 = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0]
        check("a change to a root MSBuild input changes the key", k4 != k3)
        write("src/Directory.Build.props", "<Project/>"); g("add", "."); g("commit", "-qm", "c5")
        k5 = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0]
        check("a NEW Directory.Build.props on the project's path changes the key", k5 != k4)
        check("the salt (toolchain) is part of the key", key(repo, "src/Tool/Tool.csproj", "Linux-ARM64-sdk10.0")[0] != k5)
        write("src/Lib/Lib.csproj", '<Project><ItemGroup><Compile Include="../Shared/S.cs" /></ItemGroup></Project>')
        g("commit", "-qam", "c6")
        try:
            key(repo, "src/Tool/Tool.csproj")
            check("a project including a file outside its directory is REFUSED", False)
        except Refused:
            check("a project including a file outside its directory is REFUSED", True)
        try:
            key(repo, "src/Missing/Missing.csproj")
            check("an unresolvable project is REFUSED", False)
        except Refused:
            check("an unresolvable project is REFUSED", True)
    if failures:
        print(f"✗ msbuild-closure-key self-test: {len(failures)} case(s) failed")
        return 1
    print("✓ msbuild-closure-key self-test: every case green")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo", default=".")
    ap.add_argument("--project")
    ap.add_argument("--salt", default="")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.project:
        ap.error("--project is required")
    try:
        k, lines = key(Path(a.repo), a.project, a.salt)
    except Refused as ex:
        print(f"msbuild-closure-key: refused — {ex}")
        return 2
    for line in lines:
        print(f"  {line}")
    print(f"key={k}")
    out = os.environ.get("GITHUB_OUTPUT")
    if out:
        with open(out, "a", encoding="utf-8") as fh:
            fh.write(f"key={k}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
