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

EXTERNAL ROOTS (`--external NAME`, repeatable). A caller that builds against something OUTSIDE the
checkout — the platform image's `/app` in the module lane's container build, reached through
`$(MeshWeaverRoot)` / `$(MeshWeaverRefs)` — may declare those property names external: an
`<Import>`, ProjectReference or item path that STARTS with `$(NAME)` (and carries no further
expression) is then not followed but recorded as an `external` line, and the caller asserts that its
SALT identifies everything such a path can reach (an image digest does; a branch name does not). A
property not declared stays a refusal, and nothing in a `$(NAME)` path is ever read from the checkout.

NO TARGETS (`--no-targets`). A caller whose builder EVALUATES project files but runs no MSBuild target
(the module lane's `mw-plugin-test build-project`, a Roslyn workspace with no MSBuild) may say so: the
contents of every `<Target>` are then not scanned, because nothing inside a target is ever executed by
that builder. A caller that runs `dotnet build`/`publish` must never pass it.
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


def names(repo: Path, treeish: str) -> list[str]:
    """The entry names of one tree, NUL-delimited (a non-ASCII name is never C-quoted)."""
    r = subprocess.run(["git", "-C", str(repo), "ls-tree", "-z", "--name-only", treeish],
                       capture_output=True, text=True, errors="surrogateescape")
    return [n for n in r.stdout.split("\0") if n] if r.returncode == 0 else []


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


def external_root(value: str, externals: frozenset[str]) -> bool:
    """True when `value` starts with `$(NAME)` for a declared external NAME and has no other expression."""
    v = value.strip()
    for name in externals:
        prefix = f"$({name})"
        if v.startswith(prefix):
            rest = v[len(prefix):]
            return "$(" not in rest and "@(" not in rest and "%(" not in rest
    return False


def _elements(root: ET.Element, no_targets: bool):
    """Every element of one MSBuild file — without the insides of a <Target> when `no_targets`."""
    stack = [root]
    while stack:
        el = stack.pop()
        yield el
        if no_targets and isinstance(el.tag, str) and local(el.tag) == "Target":
            continue
        stack.extend(reversed(list(el)))


def scan(repo: Path, path: str, project_dir: str,
         externals: frozenset[str] = frozenset(),
         no_targets: bool = False) -> tuple[list[str], list[str], list[str], list[str]]:
    """(ProjectReferences, Imports) of one MSBuild file as evaluated by the project in `project_dir`.
    MSBuild resolves an <Import Project> against the IMPORTING FILE's directory, but every item path
    (Include/Update/Remove — ProjectReference included) against the PROJECT's directory, wherever the
    item is declared; an item path must stay inside that project directory. The third list is every path
    rooted at a declared external property (see EXTERNAL ROOTS)."""
    here = str(PurePosixPath(path).parent)
    refs: list[str] = []
    imports: list[str] = []
    external: list[str] = []
    inputs: list[str] = []
    for el in _elements(parse(repo, path), no_targets):
        tag = local(el.tag) if isinstance(el.tag, str) else ""
        if tag == "Import":
            proj = (el.get("Project") or "").strip()
            if external_root(proj, externals):
                external.append(proj)
                continue
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
                if external_root(value, externals):
                    external.append(value.strip())
                    continue
                if tag == "ProjectReference":
                    if "$(" in value or "@(" in value or "%(" in value:
                        raise Refused(f"{path}: ProjectReference \"{value}\" uses an expression")
                    target = resolve(project_dir, value)
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
                segs = head.replace("\\", "/").split("/")
                if segs[0].lower() in ("bin", "obj") and ".." not in segs:
                    # The project's OWN build output (`bin\$(Configuration)\…\X.xml`): produced by the
                    # build, never a source input (gitignored, so nothing a commit can carry). Whatever
                    # expressions follow the first segment only pick WHICH output.
                    continue
                if head.startswith(THIS_DIR):
                    # Rooted at the DECLARING file's directory (a shared Directory.Build.targets
                    # compiling `$(MSBuildThisFileDirectory)Shared/X.cs` into every project): a repository
                    # path like any other, hashed as an input (a glob by its fixed directory's tree).
                    rest = head[len(THIS_DIR):]
                    if "$(" in rest or "@(" in rest or "%(" in rest:
                        raise Refused(f"{path}: <{tag} {attr}=\"{value}\"> uses an expression after $(MSBuildThisFileDirectory)")
                    fixed = rest.replace("\\", "/").split("*", 1)[0]
                    fixed = fixed if "*" not in rest else fixed.rsplit("/", 1)[0] if "/" in fixed else ""
                    target = resolve(here, fixed) if fixed else here
                    if not target:
                        raise Refused(f"{path}: <{tag} {attr}=\"{value}\"> points outside the repository")
                    inputs.append(target)
                    continue
                if "$(" in value or "@(" in value or "%(" in value or os.path.isabs(value.strip()):
                    raise Refused(f"{path}: <{tag} {attr}=\"{value}\"> uses an expression or an absolute path")
                rel = value.strip().replace("\\", "/")
                fixed = rel.split("*", 1)[0].rsplit("/", 1)[0] if "*" in rel else rel
                target = resolve(project_dir, fixed) if fixed else project_dir
                if not target:
                    raise Refused(f"{path}: <{tag} {attr}=\"{value}\"> points outside the repository")
                if not (target == project_dir or target.startswith(project_dir + "/")):
                    # A linked item from elsewhere in the repository (`<Compile Include="../Other/X.cs"/>`):
                    # hashed as an input of its own — a glob by its fixed directory's tree.
                    inputs.append(target)
    return refs, imports, external, inputs


def implicit_files(repo: Path, project_dir: str) -> list[str]:
    """The files MSBuild may pick up for a project without an <Import>: Directory.Build.*,
    Directory.Packages.props and nuget.config from the root down to the project directory (all of
    them, conservatively — MSBuild takes the nearest), present at HEAD."""
    found: list[str] = []
    parts = PurePosixPath(project_dir).parts
    for i in range(0, len(parts) + 1):
        prefix = PurePosixPath(*parts[:i]) if i else PurePosixPath("")
        listing = names(repo, f"HEAD:{prefix}" if i else "HEAD")
        for n in listing:
            if n in PATH_FILES or NUGET_CONFIG.match(n):
                found.append(str(prefix / n) if i else n)
    return found


def evaluate(repo: Path, project: str,
             externals: frozenset[str] = frozenset(), no_targets: bool = False) -> tuple[list[str], set[str], set[str], set[str]]:
    """(every project in the ProjectReference closure, every file any of them EVALUATES).

    A project evaluates its own file, the implicit files on its path, and — recursively, whatever their
    extension — everything those import. Each evaluated file is scanned AS that project (item paths
    against the project directory), and a ProjectReference found in ANY of them joins the closure."""
    projects: set[str] = set()
    evaluated: set[str] = set()
    external: set[str] = set()
    inputs: set[str] = set()
    stack = [str(PurePosixPath(project))]
    while stack:
        p = stack.pop()
        if p in projects:
            continue
        projects.add(p)
        pdir = str(PurePosixPath(p).parent)
        files = [p] + [f for f in implicit_files(repo, pdir) if not NUGET_CONFIG.match(PurePosixPath(f).name)]
        seen_here: set[str] = set()
        while files:
            f = files.pop()
            if f in seen_here:
                continue
            seen_here.add(f)
            if not object_hash(repo, f):
                raise Refused(f"{f} (evaluated by {p}) does not exist at HEAD")
            evaluated.add(f)
            refs, imports, ext, extra = scan(repo, f, pdir, externals, no_targets)
            stack.extend(refs)
            files.extend(imports)
            external.update(ext)
            inputs.update(extra)
    return sorted(projects), evaluated, external, inputs


def key(repo: Path, project: str, salt: str = "",
        externals: frozenset[str] | set[str] = frozenset(), no_targets: bool = False) -> tuple[str, list[str]]:
    externals = frozenset(externals)
    lines = [f"salt {salt}"]
    if no_targets:
        lines.append("builder runs no targets")
    if externals:
        # Declared, so the same tree keyed with and without a declaration never shares a key.
        lines.append("externals " + ",".join(sorted(externals)))
    projects, evaluated, external, inputs = evaluate(repo, project, externals, no_targets)
    dirs = sorted({str(PurePosixPath(p).parent) for p in projects})
    for d in dirs:
        h = object_hash(repo, d)
        if not h:
            raise Refused(f"{d} is not a directory at HEAD")
        lines.append(f"dir {d} {h}")
    # Hashed besides the project trees: every evaluated file, every implicit file (nuget.config included),
    # and — conservatively — every root-level *.props/*.targets/global.json/nuget.config.
    files: set[str] = set(evaluated)
    for d in dirs:
        files.update(implicit_files(repo, d))
    files.update(f for f in names(repo, "HEAD") if ROOT_FILE.match(f))
    for f in sorted(files):
        if any(f.startswith(d + "/") for d in dirs):
            continue   # already in that directory's tree hash
        h = object_hash(repo, f)
        if h:
            lines.append(f"file {f} {h}")
    for f in sorted(inputs):
        if any(f == d or f.startswith(d + "/") for d in dirs):
            continue   # already in that directory's tree hash
        # A path that does not exist at HEAD is recorded as absent: it is an input the moment it appears.
        lines.append(f"input {f} {object_hash(repo, f) or 'absent'}")
    lines.extend(f"external {e}" for e in sorted(external))
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
        write("src/Shared/S.cs", "class S {}")
        write("src/Lib/Lib.csproj", "<Project><ItemGroup><Compile Include='../Shared/S.cs' /></ItemGroup></Project>")
        g("add", "."); g("commit", "-qm", "linked")
        kl, lines = key(repo, "src/Tool/Tool.csproj", "s")
        check("a linked item from elsewhere in the repository is hashed as an input (single quotes)",
              any(l.startswith("input src/Shared/S.cs ") for l in lines))
        write("src/Shared/S.cs", "class S { int k; }"); g("commit", "-qam", "linked-edit")
        check("editing a linked item changes the key", key(repo, "src/Tool/Tool.csproj", "s")[0] != kl)
        refused("a linked item climbing out of the repository", "<Project><ItemGroup><Compile Include='../../../S.cs' /></ItemGroup></Project>")
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
        write("src/Lib/Lib.csproj", '<Project><Import Project="local.targets" /></Project>')
        write("src/Lib/local.targets", '<Project><ItemGroup><Compile Include="../Shared/S.cs" /></ItemGroup></Project>')
        g("add", "."); g("commit", "-qm", "imported-item")
        _, lines = key(repo, "src/Tool/Tool.csproj")
        check("an IMPORTED file's item resolves against the PROJECT's directory and is hashed",
              any(l.startswith("input src/Shared/S.cs ") for l in lines))
        write("build/inner.props", "<Project/>")
        write("src/Lib/local.targets", '<Project><Import Project="local.xml" /></Project>')
        write("src/Lib/local.xml", '<Project><Import Project="../../build/inner.props" /></Project>')
        g("add", "."); g("commit", "-qm", "xml-import")
        kx = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0]
        write("build/inner.props", "<Project><PropertyGroup><Z>3</Z></PropertyGroup></Project>"); g("commit", "-qam", "inner")
        check("an import is followed whatever its extension (local.xml → build/inner.props)",
              key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")[0] != kx)
        write("src/Extra/Extra.csproj", "<Project/>")
        write("src/Extra/E.cs", "class E {}")
        write("src/Lib/local.targets", '<Project><ItemGroup><ProjectReference Include="../Extra/Extra.csproj" /></ItemGroup></Project>')
        g("add", "."); g("commit", "-qm", "imported-ref")
        _, lines = key(repo, "src/Tool/Tool.csproj", "Linux-X64-sdk10.0")
        check("a ProjectReference declared in an IMPORTED file joins the closure", any("dir src/Extra " in l for l in lines))
        write("src/Lib/local.targets", "<Project/>"); g("commit", "-qam", "reset")
        refused("a property path climbing out of the project dir", '<Project><ItemGroup><None Include="$(MSBuildProjectDirectory)/../S.cs" /></ItemGroup></Project>')
        refused("a file that is not XML", "<Project>")
        write("src/Lib/Lib.csproj", '<Project><ItemGroup><None Include="$(TargetDir)out.manifest" /></ItemGroup></Project>')
        g("commit", "-qam", "outputs")
        try:
            key(repo, "src/Tool/Tool.csproj")
            check("an item rooted at the project's OUTPUT dir ($(TargetDir)) is accepted", True)
        except Refused as ex:
            check(f"an item rooted at the project's OUTPUT dir ($(TargetDir)) is accepted — {ex}", False)
        # ── EXTERNAL ROOTS ──
        write("src/Lib/Lib.csproj", '<Project><Import Project="$(PlatRefs)/refs.props" Condition="Exists(\'x\')" />'
              '<ItemGroup><ProjectReference Include="$(PlatRoot)/src/Core/Core.csproj" />'
              '<Compile Include="$(PlatRoot)/samples/S.cs" /></ItemGroup></Project>')
        g("commit", "-qam", "external")
        try:
            key(repo, "src/Tool/Tool.csproj", "s")
            check("REFUSED: an UNDECLARED property root stays a refusal", False)
        except Refused:
            check("REFUSED: an UNDECLARED property root stays a refusal", True)
        try:
            key(repo, "src/Tool/Tool.csproj", "s", {"PlatRoot"})
            check("REFUSED: declaring ONE property does not cover another", False)
        except Refused:
            check("REFUSED: declaring ONE property does not cover another", True)
        ke, lines = key(repo, "src/Tool/Tool.csproj", "s", {"PlatRoot", "PlatRefs"})
        check("declared external roots are recorded, not followed",
              "external $(PlatRoot)/src/Core/Core.csproj" in lines and "external $(PlatRefs)/refs.props" in lines
              and not any(l.startswith("dir src/Core") for l in lines))
        check("the external declaration is part of the key", key(repo, "src/Tool/Tool.csproj", "s", {"PlatRoot", "PlatRefs", "X"})[0] != ke)
        check("the salt still keys an external build (it is what identifies the outside)",
              key(repo, "src/Tool/Tool.csproj", "t", {"PlatRoot", "PlatRefs"})[0] != ke)
        write("src/Lib/A.cs", "class A { int z; }"); g("commit", "-qam", "ext-src")
        check("an in-repo change still changes an externally-rooted key",
              key(repo, "src/Tool/Tool.csproj", "s", {"PlatRoot", "PlatRefs"})[0] != ke)
        write("src/Lib/Lib.csproj", '<Project><ItemGroup><Compile Include="$(PlatRoot)/$(Other)/S.cs" /></ItemGroup></Project>')
        g("commit", "-qam", "ext-nested")
        try:
            key(repo, "src/Tool/Tool.csproj", "s", {"PlatRoot"})
            check("REFUSED: a declared root followed by ANOTHER expression", False)
        except Refused:
            check("REFUSED: a declared root followed by ANOTHER expression", True)
        write("src/Lib/Lib.csproj", "<Project/>"); g("commit", "-qam", "ext-reset")
        # ── items rooted at the DECLARING file's directory ──
        write("shared/X.cs", "class X {}")
        write("Directory.Build.targets", '<Project><ItemGroup><Compile Include="$(MSBuildThisFileDirectory)shared/X.cs" /></ItemGroup></Project>')
        g("add", "."); g("commit", "-qm", "this-dir-item")
        kt, lines = key(repo, "src/Tool/Tool.csproj", "s")
        check("an item rooted at $(MSBuildThisFileDirectory) is hashed as an input", any(l.startswith("input shared/X.cs ") for l in lines))
        write("shared/X.cs", "class X { int q; }"); g("commit", "-qam", "this-dir-edit")
        check("editing a $(MSBuildThisFileDirectory) item changes the key", key(repo, "src/Tool/Tool.csproj", "s")[0] != kt)
        write("Directory.Build.targets", '<Project><ItemGroup><Compile Include="$(MSBuildThisFileDirectory)shared/**/*.cs" /></ItemGroup></Project>')
        g("commit", "-qam", "this-dir-glob")
        kg, lines = key(repo, "src/Tool/Tool.csproj", "s")
        check("a $(MSBuildThisFileDirectory) glob is hashed by its fixed directory's tree", any(l.startswith("input shared ") for l in lines))
        write("shared/deep/Y.cs", "class Y {}"); g("add", "."); g("commit", "-qm", "this-dir-glob-add")
        check("a file ADDED under a $(MSBuildThisFileDirectory) glob changes the key", key(repo, "src/Tool/Tool.csproj", "s")[0] != kg)
        write("Directory.Build.targets", '<Project><ItemGroup><Compile Include="$(MSBuildThisFileDirectory)../../outside.cs" /></ItemGroup></Project>')
        g("commit", "-qam", "this-dir-out")
        try:
            key(repo, "src/Tool/Tool.csproj", "s")
            check("REFUSED: a $(MSBuildThisFileDirectory) item climbing out of the repository", False)
        except Refused:
            check("REFUSED: a $(MSBuildThisFileDirectory) item climbing out of the repository", True)
        write("Directory.Build.targets", "<Project/>"); g("commit", "-qam", "this-dir-reset")
        # ── --no-targets ──
        write("src/Lib/Lib.csproj", '<Project><Target Name="T"><ItemGroup><X Include="@(Y->\'$(Q)/%(Filename)\')" /></ItemGroup></Target></Project>')
        g("commit", "-qam", "target-items")
        try:
            key(repo, "src/Tool/Tool.csproj", "s")
            check("REFUSED: an expression inside a <Target> for a builder that RUNS targets", False)
        except Refused:
            check("REFUSED: an expression inside a <Target> for a builder that RUNS targets", True)
        try:
            kn1 = key(repo, "src/Tool/Tool.csproj", "s", no_targets=True)[0]
            check("--no-targets: a <Target>'s contents are not scanned", True)
            check("--no-targets yields a key", len(kn1) == 64)
        except Refused as ex:
            check(f"--no-targets: a <Target>'s contents are not scanned — {ex}", False)
        write("src/Lib/Lib.csproj", '<Project><Target Name="T"/><ItemGroup><Compile Include="$(Q)/S.cs" /></ItemGroup></Project>')
        g("commit", "-qam", "outside-target")
        try:
            key(repo, "src/Tool/Tool.csproj", "s", no_targets=True)
            check("REFUSED: --no-targets still refuses an evaluated item OUTSIDE a target", False)
        except Refused:
            check("REFUSED: --no-targets still refuses an evaluated item OUTSIDE a target", True)
        write("src/Lib/Lib.csproj", "<Project/>"); g("commit", "-qam", "target-reset")
        # ── the project's OWN build output is not an input ──
        write("src/Lib/Lib.csproj", '<Project><ItemGroup><EmbeddedResource Include="bin\\$(Configuration)\\$(TargetFramework)\\$(AssemblyName).xml" /></ItemGroup></Project>')
        g("commit", "-qam", "own-output")
        try:
            ko = key(repo, "src/Tool/Tool.csproj", "s", no_targets=True)[0]
            check("an item under the project's own bin/ or obj/ (expressions after it) is an output, not an input", len(ko) == 64)
        except Refused as ex:
            check(f"an item under the project's own bin/ or obj/ is an output, not an input — {ex}", False)
        write("src/Lib/Lib.csproj", '<Project><ItemGroup><EmbeddedResource Include="bin/../../Shared/$(X).cs" /></ItemGroup></Project>')
        g("commit", "-qam", "own-output-climb")
        try:
            key(repo, "src/Tool/Tool.csproj", "s")
            check("REFUSED: a bin/ path that climbs out with `..`", False)
        except Refused:
            check("REFUSED: a bin/ path that climbs out with `..`", True)
        write("src/Lib/Lib.csproj", "<Project/>"); g("commit", "-qam", "own-output-reset")
        kp = key(repo, "src/Tool/Tool.csproj", "s")[0]
        check("--no-targets changes the key (a key for one builder never serves the other)",
              kp != key(repo, "src/Tool/Tool.csproj", "s", no_targets=True)[0])
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
    ap.add_argument("--external", action="append", default=[], metavar="NAME",
                    help="a property naming a root OUTSIDE the checkout that the salt identifies (repeatable)")
    ap.add_argument("--no-targets", action="store_true",
                    help="the caller's builder evaluates project files but runs no MSBuild target")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.project:
        ap.error("--project is required")
    try:
        k, lines = key(Path(a.repo), a.project, a.salt, set(a.external), a.no_targets)
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
