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
  * every MSBuild/restore input outside those directories (sorted, `<path> <git blob hash>`): the
    repository-ROOT Directory.*.props|targets, *.props, *.targets, global.json and nuget.config, every
    Directory.Build.*/Directory.Packages.props/nuget.config on the path from the root to each closure
    project, and — RECURSIVELY — every file any hashed MSBuild file `<Import>`s. An ABSENT implicit
    file is not an input.

Files are PARSED as XML (attribute order and quoting do not matter; a Condition is ignored, so a
conditional reference or import is always counted). REFUSED (exit 2, no key) whenever containment
cannot be established: a project or import `git` cannot resolve at HEAD; a file that is not XML; an
`<Import>` whose path is not plain or `$(MSBuildThisFileDirectory)`-relative; a ProjectReference with
any property expression; and any other item path (`Include`/`Update`/`Remove`) that is absolute,
uses a property expression, or normalises to outside its own project directory. A caller treats a
refusal as "no cache" (the commit key), never as a red.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath

ROOT_FILE = re.compile(r'^(?:Directory\.[^/]+\.(?:props|targets)|[^/]+\.(?:props|targets)|global\.json|nuget\.config)$', re.I)
PATH_FILES = ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props")
NUGET_CONFIG = re.compile(r'^nuget\.config$', re.I)
THIS_DIR = "$(MSBuildThisFileDirectory)"
# Properties that name the BUILDING project's own directory or its build OUTPUTS: an item path rooted
# at one of them is the project's own file or an output, never a source input from elsewhere.
PROJECT_ROOTED = ("$(MSBuildProjectDirectory)", "$(ProjectDir)", "$(TargetDir)", "$(OutDir)", "$(OutputPath)",
                  "$(IntermediateOutputPath)", "$(BaseIntermediateOutputPath)", "$(BaseOutputPath)",
                  "$(PublishDir)", "$(ProjectPath)", "$(TargetPath)")


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


def parse(repo: Path, path: str) -> ET.Element:
    try:
        return ET.fromstring(git(repo, "show", f"HEAD:{path}"))
    except ET.ParseError as ex:
        raise Refused(f"{path} is not well-formed XML ({ex}) — its inputs cannot be established")


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def resolve(base_dir: str, value: str) -> str | None:
    """`value` relative to `base_dir`, normalised; None when it climbs above the repository root."""
    v = value.strip().replace("\\", "/")
    joined = os.path.normpath(os.path.join(base_dir, v)).replace(os.sep, "/")
    return None if joined == ".." or joined.startswith("../") or os.path.isabs(v) else joined


def is_path_like(value: str) -> bool:
    v = value.strip()
    return "/" in v or "\\" in v or v.startswith(".") or "$(" in v or os.path.isabs(v)


def scan(repo: Path, path: str, owner_dir: str | None) -> tuple[list[str], list[str]]:
    """(ProjectReferences, Imports) of one MSBuild file, both repo-relative. `owner_dir` is the project
    directory item paths must stay inside (None for a shared props/targets: its items evaluate in the
    importing project, so only their form is checked)."""
    here = str(PurePosixPath(path).parent)
    refs: list[str] = []
    imports: list[str] = []
    for el in parse(repo, path).iter():
        tag = local(el.tag) if isinstance(el.tag, str) else ""
        if tag == "Import":
            proj = (el.get("Project") or "").strip()
            rel = proj[len(THIS_DIR):] if proj.startswith(THIS_DIR) else proj
            if not rel or "$(" in rel or "@(" in rel or "%(" in rel or "*" in rel:
                raise Refused(f"{path}: <Import Project=\"{proj}\"> cannot be resolved to a file in the repository")
            target = resolve(here, rel)
            if not target:
                raise Refused(f"{path}: <Import Project=\"{proj}\"> points outside the repository")
            imports.append(target)
            continue
        for attr in ("Include", "Update", "Remove"):
            raw = el.get(attr)
            if raw is None:
                continue
            for value in (v for v in raw.split(";") if v.strip()):
                if tag == "ProjectReference":
                    if "$(" in value or "@(" in value or "%(" in value:
                        raise Refused(f"{path}: ProjectReference \"{value}\" uses an expression")
                    target = resolve(here, value)
                    if not target:
                        raise Refused(f"{path}: ProjectReference \"{value}\" points outside the repository")
                    refs.append(target)
                    continue
                if not is_path_like(value):
                    continue   # a package id, an attribute type name, a bare file name in the project dir
                head = value.strip()
                for prop in PROJECT_ROOTED:
                    if head.startswith(prop):
                        rest = head[len(prop):]
                        if "$(" not in rest and "@(" not in rest and ".." not in rest.replace("\\", "/").split("/"):
                            head = ""
                        break
                if not head:
                    continue   # the project's own directory or its outputs
                if "$(" in value or "@(" in value or "%(" in value or os.path.isabs(value.strip()):
                    raise Refused(f"{path}: <{tag} {attr}=\"{value}\"> uses an expression or an absolute path")
                target = resolve(here, value)
                if owner_dir is not None and (not target or not (target == owner_dir or target.startswith(owner_dir + "/"))):
                    raise Refused(f"{path}: <{tag} {attr}=\"{value}\"> reaches outside its project directory")
    return refs, imports


def closure(repo: Path, project: str) -> tuple[list[str], list[str]]:
    """(every project in the ProjectReference closure, every file those projects import)."""
    seen: set[str] = set()
    imported: list[str] = []
    stack = [str(PurePosixPath(project))]
    while stack:
        p = stack.pop()
        if p in seen:
            continue
        seen.add(p)
        refs, imports = scan(repo, p, str(PurePosixPath(p).parent))
        stack.extend(refs)
        imported.extend(imports)
    return sorted(seen), imported


def key(repo: Path, project: str, salt: str = "") -> tuple[str, list[str]]:
    lines = [f"salt {salt}"]
    projects, imported = closure(repo, project)
    dirs = sorted({str(PurePosixPath(p).parent) for p in projects})
    for d in dirs:
        h = object_hash(repo, d)
        if not h:
            raise Refused(f"{d} is not a directory at HEAD")
        lines.append(f"dir {d} {h}")
    root_names = git(repo, "ls-tree", "--name-only", "HEAD").split()
    implicit: set[str] = {f for f in root_names if ROOT_FILE.match(f)}
    for d in dirs:
        parts = PurePosixPath(d).parts
        for i in range(1, len(parts) + 1):
            prefix = PurePosixPath(*parts[:i])
            implicit.update(str(prefix / name) for name in PATH_FILES)
            listing = subprocess.run(["git", "-C", str(repo), "ls-tree", "--name-only", f"HEAD:{prefix}"],
                                     capture_output=True, text=True).stdout.split()
            implicit.update(str(prefix / n) for n in listing if NUGET_CONFIG.match(n))
    # Every hashed MSBuild file's imports, recursively; an explicit import must exist.
    files: dict[str, str] = {}
    queue = [(f, False) for f in sorted(implicit)] + [(f, True) for f in imported]
    while queue:
        f, required = queue.pop()
        if f in files:
            continue
        h = object_hash(repo, f)
        if not h:
            if required:
                raise Refused(f"imported file {f} does not exist at HEAD")
            continue
        files[f] = h
        # Scanned wherever it lives: a .targets inside a project directory is hashed with that directory,
        # but what IT imports from elsewhere is not — follow it.
        if f.lower().endswith((".props", ".targets")):
            _, more = scan(repo, f, None)
            queue.extend((m, True) for m in more)
    for f in sorted(files):
        if not any(f.startswith(d + "/") for d in dirs):
            lines.append(f"file {f} {files[f]}")
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
        write("src/Lib/Lib.csproj", '<Project><ItemGroup><PackageReference Include="Some.Package" /><None Include="README.md" /></ItemGroup></Project>')
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
        write("nuget.config", "<configuration/>"); g("add", "."); g("commit", "-qm", "c5b")
        k5b = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0]
        check("the root nuget.config is a restore input", k5b != k5)
        write("build/extra.props", "<Project/>")
        write("Directory.Build.props", '<Project><Import Project="$(MSBuildThisFileDirectory)build/extra.props" /></Project>')
        g("add", "."); g("commit", "-qm", "c5c")
        k5c = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0]
        write("build/extra.props", "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>"); g("commit", "-qam", "c5d")
        check("a file a root props IMPORTS is an input (recursively)", key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0] != k5c)
        write("src/Tool/Tool.csproj", "<Project><ItemGroup><ProjectReference Condition='true' Include='../Lib/Lib.csproj' /></ItemGroup></Project>")
        g("commit", "-qam", "c5e")
        _, lines = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")
        check("a conditional, single-quoted ProjectReference with the Include LAST is still followed",
              any("dir src/Lib " in l for l in lines))

        def refused(label: str, lib_body: str) -> None:
            write("src/Lib/Lib.csproj", lib_body)
            g("commit", "-qam", label)
            try:
                key(repo, "src/Tool/Tool.csproj")
                check(f"REFUSED: {label}", False)
            except Refused:
                check(f"REFUSED: {label}", True)
        refused("a Compile item outside its directory (single quotes)", "<Project><ItemGroup><Compile Include='../Shared/S.cs' /></ItemGroup></Project>")
        refused("a Compile item with a property path", '<Project><ItemGroup><Compile Include="$(Root)/S.cs" /></ItemGroup></Project>')
        refused("an absolute item path", '<Project><ItemGroup><None Include="/etc/passwd" /></ItemGroup></Project>')
        refused("an Import with a computed path", '<Project><Import Project="$([MSBuild]::GetPathOfFileAbove(x.props))" /></Project>')
        write("build/deep.props", "<Project/>")
        write("src/Lib/Lib.csproj", '<Project><Import Project="local.targets" /></Project>')
        write("src/Lib/local.targets", '<Project><Import Project="../../build/deep.props" /></Project>')
        g("add", "."); g("commit", "-qm", "nested-import")
        kn = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0]
        write("build/deep.props", "<Project><PropertyGroup><Y>2</Y></PropertyGroup></Project>"); g("commit", "-qam", "deep")
        check("an import made by a .targets INSIDE a project directory is followed out of it",
              key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0] != kn)
        refused("a property path climbing out of the project dir", '<Project><ItemGroup><None Include="$(MSBuildProjectDirectory)/../S.cs" /></ItemGroup></Project>')
        refused("a file that is not XML", "<Project>")
        write("src/Lib/Lib.csproj", '<Project><ItemGroup><None Include="$(TargetDir)out.manifest" /></ItemGroup></Project>')
        g("commit", "-qam", "outputs")
        try:
            key(repo, "src/Tool/Tool.csproj")
            check("an item rooted at the project's OUTPUT dir ($(TargetDir)) is accepted", True)
        except Refused as ex:
            check(f"an item rooted at the project's OUTPUT dir ($(TargetDir)) is accepted — {ex}", False)
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
