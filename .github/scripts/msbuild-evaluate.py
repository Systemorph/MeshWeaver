#!/usr/bin/env python3
"""msbuild-evaluate.py: ask MSBuild ITSELF what a caller's build extension / shelf evaluates.

The module build key (`module-build-key.py`) and the build scope (`node-repo-scope.py`) must see
every file that reaches the compile of a caller-supplied project, the build extension
(`build-extensions-project`) or the module-libraries shelf (`module-libraries-project`). Reading
the MSBuild by hand never converged: properties, conditions, Choose/Otherwise, item transforms
and conditional imports each became one more review finding. So this script runs the REAL
evaluator, with the lane's publishing inputs, and writes the ONE JSON both of them consume:

  imports     `dotnet msbuild <proj> -pp`: the preprocessed project names every file MSBuild
              actually imported, after conditions, Choose and properties. The exact set.
  items       `dotnet msbuild <proj> -getItem:<type>…`: the evaluated path items, globs and
              transforms already resolved by MSBuild.
  references  the evaluated ProjectReference items; their projects are evaluated the same way.

Only files INSIDE the repository are kept. SDK and NuGet files are keyed by the SDK version, the
platform's by the platform ref and digest, and build outputs are not inputs. One conservative
extra is NOT evaluated: `declaredImports`, every `<Import Project>` written in an evaluated
in-repo file whose path resolves to an in-repo location, present or not. It is kept for hashing
and reach ONLY, so an `Exists()`-guarded import appearing is a change.

Also recorded, for reach only: `declaredPatterns`, every in-repo GLOB written in an `<Import>` or
an item `Include`, so deleting the last match or adding a first one is a change. The scope unions
this evaluation of HEAD with one of its diff BASE (`--skip-missing` there, for a project the base
predates), so a deletion is seen from the tree that still had the file.

A failed evaluation fails the run red: a project MSBuild cannot evaluate is not keyed on a guess.

  msbuild-evaluate.py --root <repo> --project <rel csproj> [--project …] --out <file.json>
                      [--property Name=Value …] [--dotnet dotnet]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

# The path-valued item types read. ProjectReference is followed as an edge, not hashed as a file.
ITEM_TYPES = ("Compile", "EmbeddedResource", "AdditionalFiles", "Content", "None", "Analyzer",
              "ProjectReference")
_IMPORT = re.compile(r"<Import\b[^>]*?\bProject=(?:\"([^\"]*)\"|'([^']*)')")
_ITEM = re.compile(r"<([A-Za-z_][\w.-]*)\s[^>]*?\bInclude=(?:\"([^\"]*)\"|'([^']*)')")
# Item types whose Include is a NAME, not a path.
_NAME_ITEMS = frozenset({"PackageReference", "PackageVersion", "GlobalPackageReference", "PackageDownload",
                         "FrameworkReference", "Using", "InternalsVisibleTo", "ProjectCapability",
                         "SupportedPlatform", "TrimmerRootAssembly", "AssemblyAttribute"})


_PROPERTY = re.compile(r"\$\(([A-Za-z_][\w.-]*)\)")
_DIRECTORY_PROPERTIES = ("MSBuildThisFileDirectory", "MSBuildProjectDirectory")


def normalize_project(project: str) -> str:
    """ONE spelling of a repository-relative project path for every consumer (`./a/../b.csproj` and
    `b.csproj` are the same project). The same function lives in node-repo-scope.py, which the key
    loads: the evaluation's keys, the scope's lookups and the key's lookups all agree."""
    return Path(os.path.normpath(project.strip().replace("\\", "/"))).as_posix()


def _in_repo(root: Path, path: str) -> str | None:
    try:
        return Path(os.path.normpath(path)).resolve().relative_to(root).as_posix()
    except (ValueError, OSError):
        return None


def _run(argv: list[str], cwd: Path) -> str:
    r = subprocess.run(argv, cwd=cwd, capture_output=True, text=True, timeout=600)
    if r.returncode != 0:
        raise RuntimeError(f"`{' '.join(argv)}` failed (exit {r.returncode}):\n{(r.stdout + r.stderr)[-2000:]}")
    return r.stdout


def _declared_path(root: Path, value: str, plain_base: Path, this_dir: Path, project_dir: Path,
                   properties: dict[str, str]):
    """A declared Import/Include value as a repository-relative path or glob.

    Returns None when it is derived from other items (@(…), %(…)) or lies outside the repository,
    and the string "?" when a property in it cannot be resolved. `$(MSBuildThisFileDirectory)` is
    the DEFINING file's directory and `$(MSBuildProjectDirectory)` the project's. Any other
    property is substituted with the value MSBuild ITSELF evaluated for it (`-getProperty`), so
    `$(EmitterRoot)/*.cs` keeps its pattern."""
    value = value.strip().replace("\\", "/")
    base = plain_base
    if value.startswith("$(MSBuildThisFileDirectory)"):
        value, base = value[len("$(MSBuildThisFileDirectory)"):], this_dir
    elif value.startswith("$(MSBuildProjectDirectory)"):
        value, base = value[len("$(MSBuildProjectDirectory)"):].lstrip("/"), project_dir
    if not value or "@(" in value or "%(" in value:
        return None
    value = _PROPERTY.sub(lambda m: properties.get(m.group(1), m.group(0)), value).replace("\\", "/")
    if "$(" in value:
        return "?"  # a property function, or a property only a target sets: not evaluable here
    # 🚨 A path that MSBuild's own property values make EMPTY, or that resolves to a DIRECTORY, names
    # no file MSBuild could import. Kept, it became the importing file's directory, and the scope
    # reads a reach entry as a directory PREFIX: `<Import Project="$(_MeshWeaverCoreRefsPath)">` in
    # a caller's src/Directory.PlatformRefs.targets (the property is empty outside a platform mount)
    # put `src` into the shelf's reach, so EVERY src/ edit (a test-only project the shelf never
    # reaches included) ran the whole module catalog. A directory is never an import target.
    if not value.strip():
        return None
    hit = _in_repo(root, value if value.startswith("/") else str(base / value))
    if hit is not None and not _is_glob(hit) and (root / hit).is_dir():
        return None
    return hit


def _referenced_properties(root: Path, files: list[str]) -> list[str]:
    names: set[str] = set()
    for rel in files:
        text = (root / rel).read_text(encoding="utf-8", errors="replace")
        for regex in (_IMPORT, _ITEM):
            for match in regex.findall(text):
                for value in match if regex is _IMPORT else match[1:]:
                    names.update(n for n in _PROPERTY.findall(value or "") if n not in _DIRECTORY_PROPERTIES)
    return sorted(names)


def declared(root: Path, files: list[str], project_dir: str,
             properties: dict[str, str]) -> tuple[list[str], list[str], list[str]]:
    """What the evaluated in-repo files DECLARE, for hashing and reach only (MSBuild already decided
    what is imported and which items exist today):
      imports   every literal `<Import Project>` path that resolves in-repo, present or not;
      patterns  every in-repo GLOB written in an `<Import Project>` or an item `Include`, its
                properties substituted with MSBuild's values, so a deletion of the last match or a
                first match added is a change;
      unknown   every Import or glob-capable Include whose path a property this cannot resolve
                decides (`file: value`). The scope treats such a project as touched by ANY change.
    A plain Import path is the importing file's; a plain Include is the project's."""
    imports: set[str] = set()
    patterns: set[str] = set()
    unknown: set[str] = set()
    proj_dir = root / project_dir
    for rel in files:
        text = (root / rel).read_text(encoding="utf-8", errors="replace")
        this_dir = (root / rel).parent
        for a, b in _IMPORT.findall(text):
            hit = _declared_path(root, a or b, this_dir, this_dir, proj_dir, properties)
            if hit == "?":
                unknown.add(f"{rel}: <Import Project=\"{a or b}\">")
            elif hit is not None:
                (patterns if _is_glob(hit) else imports).add(hit)
        for tag, a, b in _ITEM.findall(text):
            if tag in _NAME_ITEMS:
                continue
            for include in (a or b).split(";"):
                hit = _declared_path(root, include, proj_dir, this_dir, proj_dir, properties)
                if hit == "?":
                    unknown.add(f"{rel}: <{tag} Include=\"{include.strip()}\">")
                elif hit is not None and _is_glob(hit):
                    patterns.add(hit)
    return sorted(imports), sorted(patterns), sorted(unknown)


def _is_glob(path: str) -> bool:
    return "*" in path or "?" in path


# ── what a <Target> reads: an ALLOW-LIST, everything else FAILS CLOSED ───────────────────────────
# `-getItem` reports EVALUATION-time items only, and a target's task inputs are never items at
# all: an ItemGroup inside a <Target> (a BeforeTargets="CoreCompile" adding Compile) or a task
# attribute (a Copy SourceFiles) reaches the build while neither the key nor the scope sees it.
# Deny-listing shapes did not converge, so target_item_violations() ALLOWS only (a) a literal path
# into a tracked input, a build output or an exempt root, (b) a plain @(X) over allowed items,
# (c) empty — after evaluation-time properties only — and refuses everything else by name.
# Task attributes that only carry TEXT to be printed or written, never a path that is read.
# XmlPeek/XmlPoke's Query, Namespaces and Value are XPath and XML text: `//PlatformVersion/text()` is an
# expression over the document named by XmlInputPath, which stays checked like any task input. Read
# as a path, the leading `//` resolved to the filesystem root and refused the Plugins repository's
# AssemblyVersion check (src/Directory.Build.props, VerifyAssemblyVersionMatchesPlatform).
_TEXT_ONLY_ATTRIBUTES = frozenset({("Message", "Text"), ("Message", "Importance"), ("Warning", "Text"),
                                   ("Warning", "Code"), ("Error", "Text"), ("Error", "Code"),
                                   ("WriteLinesToFile", "Lines"),
                                   ("XmlPeek", "Query"), ("XmlPeek", "Namespaces"),
                                   ("XmlPoke", "Query"), ("XmlPoke", "Namespaces"), ("XmlPoke", "Value")})
# Roots OUTSIDE the repository a target may read from: keyed by the SDK version, the NuGet lock and
# the platform ref + digest, never per file here.
_EXEMPT_ROOT_PROPERTIES = ("NetCoreRoot", "NetCoreTargetingPackRoot", "NuGetPackageRoot", "MeshWeaverRoot")
_OUTPUT_PROPERTIES = ("BaseIntermediateOutputPath", "IntermediateOutputPath", "BaseOutputPath",
                      "OutputPath", "OutDir", "TargetDir", "PublishDir")


def _local(tag) -> str:
    return tag.split("}", 1)[-1] if isinstance(tag, str) else ""


def _absolute(value: str, plain_base: Path, this_dir: Path, project_dir: Path,
              properties: dict[str, str]) -> str | None:
    """An absolute, normalized path for one Include piece; None when a property stays unresolved."""
    v = value.strip().replace("\\", "/")
    base = plain_base
    if v.startswith("$(MSBuildThisFileDirectory)"):
        v, base = v[len("$(MSBuildThisFileDirectory)"):], this_dir
    elif v.startswith("$(MSBuildProjectDirectory)"):
        v, base = v[len("$(MSBuildProjectDirectory)"):].lstrip("/"), project_dir
    v = _PROPERTY.sub(lambda m: properties.get(m.group(1), m.group(0)), v).replace("\\", "/")
    if "$(" in v:
        return None
    return os.path.normpath(v if v.startswith("/") else str(base / v))


def _output_dirs(project: Path, properties: dict[str, str]) -> list[str]:
    """The project's build OUTPUT directories, absolute: products of the build, never inputs."""
    out = []
    for name in _OUTPUT_PROPERTIES:
        value = (properties.get(name) or "").strip().replace("\\", "/")
        if value:
            out.append(os.path.normpath(value if value.startswith("/") else str(project.parent / value)))
    return out


def target_item_violations(root: Path, files: list[str], project_rel: str, properties: dict[str, str],
                           tracked: set[str], tracked_patterns: frozenset[str] = frozenset()) -> list[str]:
    """An ALLOW-LIST over everything a <Target> in the in-repo graph hands a task or adds as an item.

    Every task attribute (except a Condition and the text-only ones) and every item Include inside a
    target must, after substituting EVALUATION-time properties only, be one of:
      (a) a literal path that resolves into a tracked input (the project's own tree-hashed
          directory, a file the key already hashes, a tracked glob), a build OUTPUT, or an exempt
          root outside the repository: the SDK, the NuGet package root, the platform checkout
          (keyed by the SDK version and the platform ref + digest);
      (b) a plain `@(X)` whose every declaration is itself allowed (an X no in-repo file declares
          is produced by the SDK's own targets);
      (c) empty.
    Anything else is REFUSED, naming the target, the file and the expression: a property assigned
    inside ANY target (its runtime value is not the evaluated one), any `$([…])` function, a
    transform, metadata batching, an unresolved property, an in-repo path that is not tracked, or
    a path outside the repository that is no exempt root. An ItemGroup, item or task whose
    Condition is decidably FALSE over evaluation-time values never runs and is not checked.
    A target PROPERTY may still hold a property function that depends on no item: the file it
    reads is named in the project text, which the sweep keys."""
    root = root.resolve()
    project_dir = (root / project_rel).parent.resolve()
    outputs = _output_dirs(root / project_rel, properties)
    exempt_roots = [os.path.normpath(properties[n]) for n in _EXEMPT_ROOT_PROPERTIES if (properties.get(n) or "").strip()]
    declarations: dict[str, list[tuple[str, Path, tuple]]] = {}
    target_assigned: set[str] = set()
    sites: list[tuple[str, str, str, Path, str, tuple]] = []      # (target, what, value, file, rel, conditions)
    target_prop_sites: list[tuple[str, str, str, str]] = []       # (target, property, raw, rel)
    for rel in files:
        try:
            tree = ET.parse(root / rel)
        except ET.ParseError as exc:
            raise RuntimeError(f"{rel} is not well-formed MSBuild XML ({exc})") from None

        def walk(node, target, conds):
            for el in node:
                tag = _local(el.tag)
                if not tag:
                    continue
                c = conds + ((el.get("Condition"), root / rel),) if el.get("Condition") else conds
                if tag == "Target":
                    walk(el, el.get("Name") or "?", c)
                elif tag == "PropertyGroup":
                    for prop in el:
                        if _local(prop.tag) and target:
                            target_assigned.add(_local(prop.tag))
                            target_prop_sites.append((target, _local(prop.tag), prop.text or "", rel))
                elif tag == "ItemGroup":
                    for item in el:
                        name = _local(item.tag)
                        include = item.get("Include")
                        if not name or include is None or name in _NAME_ITEMS:
                            continue
                        ic = c + ((item.get("Condition"), root / rel),) if item.get("Condition") else c
                        declarations.setdefault(name, []).append((include, root / rel, ic))
                        if target:
                            sites.append((target, f"<{name} Include=\"{include}\">", include, root / rel, rel, ic))
                elif target and tag != "OnError":
                    for attr, value in el.attrib.items():
                        if attr == "Condition" or (tag, attr) in _TEXT_ONLY_ATTRIBUTES:
                            continue
                        sites.append((target, f"task <{tag} {attr}=\"{value}\">", value, root / rel, rel, c))
                else:
                    walk(el, target, c)
        walk(tree.getroot(), None, ())

    def expand(value: str, f: Path):
        """(expanded, None) with evaluation-time properties substituted, or (None, reason)."""
        out, last = [], 0
        for m in re.finditer(r"\$\(([^()]*)\)", value):
            out.append(value[last:m.start()])
            name = m.group(1).strip()
            if name.startswith("[") or not re.fullmatch(r"[A-Za-z_][\w.-]*", name):
                return None, f"'{value}' uses a property function; only a literal, an evaluation-time property or a plain @(X) is allowed"
            if name in target_assigned:
                return None, f"'{value}' uses $({name}), a property assigned inside a target: its runtime value is not the evaluated one"
            if name == "MSBuildThisFileDirectory":
                out.append(str(f.parent) + "/")
            elif name == "MSBuildProjectDirectory":
                out.append(str(project_dir))
            elif name in properties:
                out.append(properties[name])
            else:
                return None, f"'{value}' uses $({name}), which this evaluation cannot resolve"
            last = m.end()
        if "$([" in value:
            return None, f"'{value}' uses a property function"
        out.append(value[last:])
        return "".join(out), None

    def decidably_false(conds: tuple) -> bool:
        for cond, f in conds:
            expanded, why = expand(cond, f)
            if why or expanded is None:
                continue
            m = re.fullmatch(r"\s*'([^']*)'\s*(==|!=)\s*'([^']*)'\s*", expanded)
            if m and ((m.group(1).lower() == m.group(3).lower()) != (m.group(2) == "==")):
                return True
        return False

    def under(path: str, base: str) -> bool:
        return path == base or path.startswith(base.rstrip("/") + "/")

    def allowed(value: str, f: Path, seen: frozenset[str]) -> str | None:
        for piece in value.split(";"):
            piece = piece.strip()
            if not piece:
                continue                                              # (c) empty
            m = re.fullmatch(r"@\(([A-Za-z_][\w.-]*)\)", piece)
            if m:                                                     # (b) a plain @(X)
                name = m.group(1)
                if name in seen:
                    continue
                for inner, inner_file, inner_conds in declarations.get(name, []):
                    if decidably_false(inner_conds):
                        continue
                    why = allowed(inner, inner_file, seen | {name})
                    if why:
                        return f"'{piece}' draws on {inner_file.relative_to(root).as_posix()}: {why}"
                continue
            if "@(" in piece or "->" in piece:
                return f"'{piece}' is an item transform or reference that is not a plain @(X)"
            if "%(" in piece:
                return f"'{piece}' uses metadata batching"
            expanded, why = expand(piece, f)
            if why:
                return why
            expanded = expanded.replace("\\", "/").strip()
            if not expanded:
                continue
            path = os.path.normpath(expanded if expanded.startswith("/") else str(project_dir / expanded))
            check = path
            if _is_glob(path):                                        # a glob: judged by its static root
                cut = min(i for i in (path.find("*"), path.find("?")) if i >= 0)
                check = os.path.dirname(path[:cut]) if not path[:cut].endswith("/") else path[:cut].rstrip("/")
                try:
                    if Path(path).resolve().relative_to(root).as_posix() in tracked_patterns:
                        continue
                except (ValueError, OSError):
                    pass
            if any(under(check, o) for o in outputs) or any(under(check, r) for r in exempt_roots):
                continue                                              # (a) an output, or the SDK / NuGet / platform
            if under(check, str(project_dir)):
                continue                                              # (a) the project's tree-hashed directory
            try:
                rel = Path(check).resolve().relative_to(root).as_posix()
            except (ValueError, OSError):
                return f"'{piece}' resolves to {check}, outside the repository and outside every exempt root (SDK, NuGet, platform)"
            if rel in tracked:
                continue                                              # (a) a file the key already hashes
            return f"'{piece}' resolves to {rel}, an in-repo path that is not a tracked input"
        return None

    out = []
    for target, what, value, f, rel, conds in sites:
        if decidably_false(conds):
            continue
        why = allowed(value, f, frozenset())
        if why:
            out.append(f"{rel}: <Target Name=\"{target}\"> {what} — {why}")
    for target, prop, raw, rel in target_prop_sites:
        for m in re.finditer(r"\$\(\[", raw):
            if "@(" in raw[m.start():] or "%(" in raw[m.start():]:
                out.append(f"{rel}: <Target Name=\"{target}\"> property <{prop}> — a property function over items "
                           f"('{raw.strip()[:120]}'): it may read a file no evaluation can name")
                break
    return out


# ── the CONSERVATIVE PATH SWEEP over the preprocessed project ─────────────────────────────────
# MSBuild's evaluation lists imports and evaluation-time items, but a build also reads paths it
# never turns into items: a <Reference>'s <HintPath>, a Copy task's SourceFiles, an Exists() in a
# condition, an import with any suffix. Rather than model each shape, every attribute value and
# every text node of the `-pp` output (and every quoted piece inside them) that, after expansion,
# names an EXISTING in-repo FILE outside the build outputs is an input: hashed by the key and in
# the scope's reach. Over-inclusion costs a rebuild; omission costs a stale bundle, so this errs
# wide. It never takes a DIRECTORY (`$(MSBuildProjectDirectory)` alone must not pull in a tree),
# never the repository root, never `.git`. A glob is kept as a pattern when its static root is an
# in-repo directory other than the root. Paths that cannot be resolved statically are not swept;
# the target-item refusal above still covers those that a target turns into items.
# Each banner opens or closes an imported file; `</Import>` names the file the text returns to.
_PP_BANNER = re.compile(r"^={20,}\n  (?:<Import\b[^\n]*>|</Import>)\n(?:[^\n]*\n)*?\n(/[^\n]+)\n={20,}$", re.M)
_COMMENT = re.compile(r"<!--.*?-->", re.S)
_ATTRIBUTE = re.compile(r"""[\w:.-]+\s*=\s*(?:"([^"]*)"|'([^']*)')""")
_TEXT = re.compile(r">([^<>]+)<")
_QUOTED = re.compile(r"'([^']*)'")


def pp_segments(text: str, project: Path) -> list[tuple[Path, str]]:
    """(the file a stretch of preprocessed text was written in, that stretch)."""
    out: list[tuple[Path, str]] = []
    current, last = project, 0
    for m in _PP_BANNER.finditer(text):
        out.append((current, text[last:m.start()]))
        current, last = Path(m.group(1)), m.end()
    out.append((current, text[last:]))
    return out


def pp_imports(root: Path, text: str) -> set[str]:
    """Every in-repo file the preprocessed project imported, whatever its suffix."""
    return {r for m in _PP_BANNER.finditer(text) if (r := _in_repo(root, m.group(1))) is not None}


def _candidates(segment: str):
    for m in _ATTRIBUTE.finditer(segment):
        yield m.group(1) if m.group(1) is not None else m.group(2)
    for m in _TEXT.finditer(segment):
        yield m.group(1)


def pp_properties(root: Path, text: str, project: Path) -> list[str]:
    """Property names the IN-REPO stretches of the preprocessed text reference (resolved by MSBuild)."""
    names: set[str] = set()
    for f, seg in pp_segments(text, project):
        if _in_repo(root, str(f)) is not None:
            names.update(n for n in _PROPERTY.findall(_COMMENT.sub("", seg)) if n not in _DIRECTORY_PROPERTIES)
    return sorted(names)


def _scope_matcher():
    """node-repo-scope.py's msbuild_glob_matches: the ONE MSBuild glob matcher (`**/` = zero or
    more directories), loaded from the sibling so evaluation and scope cannot disagree."""
    import importlib.util
    path = Path(__file__).resolve().with_name("node-repo-scope.py")
    spec = importlib.util.spec_from_file_location("node_repo_scope_for_evaluate", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod.msbuild_glob_matches


_GLOB_MATCHES = _scope_matcher()
_DIRECTORY_PREFIXES = {"MSBuildThisFileDirectory": "this", "MSBuildProjectDirectory": "project"}


def paths_in(root: Path, value: str, project_dir: Path, this_dir: Path, properties: dict[str, str],
             outputs: list[str]) -> tuple[set[str], set[str]]:
    """THE one parser of a path-ish value for the conservative sweep: (existing in-repo FILES it
    names, in-repo glob PATTERNS it names). Every rule lives here, and the table in --self-test
    holds them:
      - a `;`-list is split; each piece is stripped of whitespace and of surrounding quotes;
      - `\\` separators become `/`; spaces are kept (a file may have one);
      - `$(MSBuildThisFileDirectory)` is the DEFINING file's directory and
        `$(MSBuildProjectDirectory)` the project's, with any separator after them dropped, so the
        join never becomes root-absolute; other properties take MSBuild's evaluated value, and a
        piece with one still unresolved names nothing;
      - a plain relative piece is the project's; `..` segments and doubled or trailing separators
        are normalized;
      - a GLOB (`*`, `**`, `?`) contributes its pattern AND every in-repo file it matches today,
        when its static root is an existing in-repo directory other than the root;
      - only EXISTING FILES count: never a directory, never the repository root, never `.git`,
        never a build output, never anything outside the repository;
      - item references (`@(…)`, `%(…)`) name nothing here: the items they draw on are read
        where they are declared."""
    root = root.resolve()
    files: set[str] = set()
    patterns: set[str] = set()

    def admit(path: str) -> str | None:
        try:
            rel = Path(path).resolve().relative_to(root).as_posix()
        except (ValueError, OSError):
            return None
        if rel in ("", ".") or rel == ".git" or rel.startswith(".git/"):
            return None
        if any(path == o or path.startswith(o.rstrip("/") + "/") for o in outputs):
            return None
        return rel

    for piece in value.split(";"):
        piece = piece.strip()
        if len(piece) >= 2 and piece[0] == piece[-1] and piece[0] in "'\"":
            piece = piece[1:-1].strip()
        piece = piece.replace("\\", "/")
        if not piece or "@(" in piece or "%(" in piece:
            continue
        base = project_dir
        m = re.match(r"\$\((MSBuildThisFileDirectory|MSBuildProjectDirectory)\)", piece)
        if m:
            base = this_dir if _DIRECTORY_PREFIXES[m.group(1)] == "this" else project_dir
            piece = piece[m.end():].lstrip("/")
        piece = _PROPERTY.sub(lambda p: properties.get(p.group(1), p.group(0)), piece).replace("\\", "/")
        if "$(" in piece:
            continue
        path = os.path.normpath(piece if piece.startswith("/") else os.path.join(str(base), piece)) if piece \
            else os.path.normpath(str(base))
        if not _is_glob(path):
            if os.path.isfile(path):
                rel = admit(path)
                if rel is not None:
                    files.add(rel)
            continue
        cut = min(i for i in (path.find("*"), path.find("?")) if i >= 0)
        static_dir = path[:cut] if path[:cut].endswith("/") else os.path.dirname(path[:cut])
        static_dir = static_dir.rstrip("/") or "/"
        static_rel = admit(static_dir)
        if static_rel is None or not os.path.isdir(static_dir):
            continue  # rooted at the root, outside, an output, or nowhere: never swept
        try:
            pattern = Path(static_dir).resolve().relative_to(root).as_posix() + path[len(static_dir):]
        except (ValueError, OSError):
            continue
        patterns.add(pattern)
        for dirpath, dirnames, filenames in os.walk(static_dir):
            dirnames[:] = [d for d in dirnames if d != ".git"]
            for name in filenames:
                full = os.path.join(dirpath, name)
                rel = admit(full)
                if rel is not None and _GLOB_MATCHES(rel, pattern):
                    files.add(rel)
    return files, patterns


def sweep(root: Path, text: str, project: Path, properties: dict[str, str], outputs: list[str]) -> tuple[list[str], list[str]]:
    """(existing in-repo files, in-repo glob patterns) the preprocessed project names anywhere:
    every attribute value, text node and quoted piece, through paths_in()."""
    project_dir = project.parent
    own = None
    try:
        own = project_dir.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        pass
    files: set[str] = set()
    patterns: set[str] = set()
    for f, seg in pp_segments(text, project):
        seg = _COMMENT.sub("", seg)
        for value in _candidates(seg):
            for chunk in [value] + _QUOTED.findall(value):
                got_files, got_patterns = paths_in(root, chunk, project_dir, f.parent, properties, outputs)
                files |= got_files
                # the project's own directory is tree-hashed already; a pattern there adds nothing
                patterns |= {p for p in got_patterns if not (own and (p == own or p.startswith(own + "/")))}
    return sorted(files), sorted(patterns)


def self_test() -> int:
    """The TABLE behind paths_in(): every spelling class, each with its expected files/patterns."""
    failures: list[str] = []
    with tempfile.TemporaryDirectory() as tmp:
        top = Path(tmp).resolve()
        root = top / "repo"
        for rel in ("Proj/Proj.csproj", "Lib/Ext.props", "Lib/Beside.props", "Libs/Helper File.dll", "Libs/a.dll",
                    "Shared/x.dll", "Shared/y.dll", "Shared/deep/z.dll", "Shared/notes.txt", "Shared/ab.dll",
                    "root.dll", "Proj/bin/out.dll", ".git/config"):
            (root / rel).parent.mkdir(parents=True, exist_ok=True)
            (root / rel).write_text("x\n", encoding="utf-8")
        (top / "outside.dll").write_text("x\n", encoding="utf-8")
        project_dir, this_dir = root / "Proj", root / "Lib"
        props = {"Libs": "../Libs", "LibsAbs": str(root / "Libs"), "OutDir": str(root / "Proj/bin") + "/"}
        outputs = [str(root / "Proj/bin")]
        table = (
            # (label, value, expected files, expected patterns)
            ("a space in the name", "../Libs/Helper File.dll", {"Libs/Helper File.dll"}, set()),
            ("a leading / after $(MSBuildThisFileDirectory)", "$(MSBuildThisFileDirectory)/Beside.props", {"Lib/Beside.props"}, set()),
            ("a leading \\ after $(MSBuildThisFileDirectory)", "$(MSBuildThisFileDirectory)\\Beside.props", {"Lib/Beside.props"}, set()),
            ("a leading / after $(MSBuildProjectDirectory)", "$(MSBuildProjectDirectory)/../Libs/a.dll", {"Libs/a.dll"}, set()),
            ("\\ separators", "..\\Libs\\a.dll", {"Libs/a.dll"}, set()),
            (".. segments", "../Shared/../Libs/a.dll", {"Libs/a.dll"}, set()),
            ("a single-quoted value", "'../Libs/a.dll'", {"Libs/a.dll"}, set()),
            ("a double-quoted value", '"../Libs/a.dll"', {"Libs/a.dll"}, set()),
            ("a ;-list, spaced", " ../Libs/a.dll ; ../Shared/x.dll ;", {"Libs/a.dll", "Shared/x.dll"}, set()),
            ("a property", "$(Libs)/a.dll", {"Libs/a.dll"}, set()),
            ("an absolute property", "$(LibsAbs)/a.dll", {"Libs/a.dll"}, set()),
            ("a * glob: pattern AND matches", "../Shared/*.dll",
             {"Shared/x.dll", "Shared/y.dll", "Shared/ab.dll"}, {"Shared/*.dll"}),
            ("a ** glob: zero or more directories", "../Shared/**/*.dll",
             {"Shared/x.dll", "Shared/y.dll", "Shared/ab.dll", "Shared/deep/z.dll"}, {"Shared/**/*.dll"}),
            ("a ? glob", "../Shared/?.dll", {"Shared/x.dll", "Shared/y.dll"}, {"Shared/?.dll"}),
            ("a glob in a custom item, \\-separated", "..\\Shared\\*.dll",
             {"Shared/x.dll", "Shared/y.dll", "Shared/ab.dll"}, {"Shared/*.dll"}),
            ("a doubled and a trailing separator on a file", "..//Libs//a.dll/", {"Libs/a.dll"}, set()),
            ("NEGATIVE: a trailing separator names a DIRECTORY", "../Libs/", set(), set()),
            ("NEGATIVE: a nonexistent path", "../Libs/missing.dll", set(), set()),
            ("NEGATIVE: a path outside the repository", "../../outside.dll", set(), set()),
            ("NEGATIVE: the repository root", "$(MSBuildProjectDirectory)/..", set(), set()),
            ("NEGATIVE: a glob rooted at the repository root", "../*.dll", set(), set()),
            ("NEGATIVE: a build output", "$(OutDir)out.dll", set(), set()),
            ("NEGATIVE: .git", "../.git/config", set(), set()),
            ("NEGATIVE: an unresolved property", "$(Nope)/a.dll", set(), set()),
            ("NEGATIVE: an item reference", "@(Compile)", set(), set()),
        )
        for label, value, want_files, want_patterns in table:
            got_files, got_patterns = paths_in(root, value, project_dir, this_dir, props, outputs)
            ok = got_files == want_files and got_patterns == want_patterns
            print(f"  {'✓' if ok else '✗'} {label}: {value!r}"
                  + ("" if ok else f" → files {sorted(got_files)} patterns {sorted(got_patterns)}, "
                                   f"want {sorted(want_files)} {sorted(want_patterns)}"))
            if not ok:
                failures.append(label)
        # declared(): the `<Import Project>` paths kept for reach. A reach entry is read as a
        # directory PREFIX by node-repo-scope.py, so an import that resolves to a directory would
        # make every file under it "reach" the project (the empty-property shape put `src` into the
        # Plugins shelf's reach and widened every src/ edit to the full module catalog).
        (root / "Lib" / "Imports.targets").write_text(
            '<Project>\n'
            '  <Import Project="$(EmptyPath)" Condition="Exists(\'$(EmptyPath)\')" />\n'
            '  <Import Project="$(MSBuildThisFileDirectory)" Condition="false" />\n'
            '  <Import Project="$(LibDir)" Condition="false" />\n'
            '  <Import Project="$(MSBuildThisFileDirectory)Optional.props" Condition="Exists(\'x\')" />\n'
            '  <Import Project="$(MSBuildThisFileDirectory)Beside.props" />\n'
            '  <Import Project="$(MSBuildThisFileDirectory)../Shared/*.props" />\n'
            '</Project>\n', encoding="utf-8")
        got_imports, got_patterns, got_unknown = declared(
            root, ["Lib/Imports.targets"], "Proj", {"EmptyPath": "", "LibDir": str(root / "Libs")})
        cases = (
            ("NEGATIVE: an import whose property is EMPTY names no file (not the importing directory)",
             "Lib" not in got_imports),
            ("NEGATIVE: an import that resolves to a DIRECTORY names no file", "Libs" not in got_imports),
            ("NEGATIVE: $(MSBuildThisFileDirectory) alone names no file", "Lib" not in got_imports),
            ("CONTROL: an absent Exists()-guarded import is still declared (its appearance is a change)",
             "Lib/Optional.props" in got_imports),
            ("CONTROL: a present import is still declared", "Lib/Beside.props" in got_imports),
            ("CONTROL: a glob import is still a pattern", got_patterns == ["Shared/*.props"]),
            ("CONTROL: nothing above is unknown", got_unknown == []),
        )
        for label, ok in cases:
            print(f"  {'✓' if ok else '✗'} {label}"
                  + ("" if ok else f" → imports {got_imports} patterns {got_patterns} unknown {got_unknown}"))
            if not ok:
                failures.append(label)
    if failures:
        print(f"\n::error title=msbuild-evaluate self-test failed::{len(failures)} case(s): " + "; ".join(failures))
        return 1
    print(f"\n✓ msbuild-evaluate self-test: {len(table)} path spellings resolve as the table says, "
          f"{len(cases)} declared-import cases hold")
    return 0


def evaluate_project(root: Path, rel: str, properties: list[str], dotnet: str) -> dict:
    proj = root / rel
    props = ["-p:Configuration=Release"] + [f"-p:{p}" for p in properties]
    with tempfile.TemporaryDirectory() as tmp:
        pp = Path(tmp) / "pp.xml"
        _run([dotnet, "msbuild", str(proj), "-nologo", *props, f"-pp:{pp}"], root)
        text = pp.read_text(encoding="utf-8", errors="replace")
        if not text.strip():
            raise RuntimeError(f"`dotnet msbuild -pp` wrote nothing for {rel}")
    # Every in-repo file the banners name, WHATEVER its suffix (a .msbuild, a .proj, a .xml import).
    imports = sorted(pp_imports(root, text) | {rel})
    wanted = sorted(set(_referenced_properties(root, imports)) | set(_OUTPUT_PROPERTIES) | set(_EXEMPT_ROOT_PROPERTIES)
                    | set(pp_properties(root, text, root / rel)))
    got = json.loads(_run([dotnet, "msbuild", str(proj), "-nologo", *props,
                           *[f"-getItem:{t}" for t in ITEM_TYPES],
                           *[f"-getProperty:{n}" for n in wanted]], root))
    items: dict[str, list[str]] = {}
    references: list[str] = []
    for kind, entries in (got.get("Items") or {}).items():
        paths = sorted({r for e in entries if (r := _in_repo(root, e.get("FullPath") or "")) is not None})
        if kind == "ProjectReference":
            references = [p for p in paths if (root / p).is_file()]
        elif paths:
            items[kind] = paths
    values = {k: v for k, v in (got.get("Properties") or {}).items() if isinstance(v, str)}
    project_dir = Path(rel).parent.as_posix()
    declared_imports, declared_patterns, unknown = declared(root, imports, project_dir, values)
    outputs = _output_dirs(root / rel, values)
    swept, swept_patterns = sweep(root, text, (root / rel).resolve(), values, outputs)
    declared_patterns = sorted(set(declared_patterns) | set(swept_patterns))
    tracked = {p for paths in items.values() for p in paths} | set(imports) | set(swept)
    violations = target_item_violations(root, imports, rel, values, tracked, frozenset(declared_patterns))
    if violations:
        raise RuntimeError(
            f"{rel}: a <Target> adds path items that MSBuild's evaluation cannot see, so neither the key "
            "nor the scope would — refused rather than keyed on a guess:\n  " + "\n  ".join(violations)
            + "\nDeclare the item at evaluation time (outside the <Target>), or stage it under a build output.")
    return {"imports": imports, "declaredImports": declared_imports, "declaredPatterns": declared_patterns,
            "unknownPatterns": unknown, "items": items, "references": references, "swept": swept}


def _pinned(root: Path, sdk: str):
    """Write a global.json pinning `sdk` (rollForward: disable) at the tree's root, merged into any
    existing one, and return a restore callback. The BASE and trunk-tip trees are evaluated by the
    SDK the HEAD resolved, so an SDK bump between them needs no old SDK and changes nothing else."""
    gj = root / "global.json"
    original = gj.read_text(encoding="utf-8") if gj.is_file() else None
    try:
        data = json.loads(original) if original else {}
    except json.JSONDecodeError:
        data = {}
    data = data if isinstance(data, dict) else {}
    data["sdk"] = {**(data.get("sdk") if isinstance(data.get("sdk"), dict) else {}),
                   "version": sdk, "rollForward": "disable", "allowPrerelease": False}
    gj.write_text(json.dumps(data, indent=2), encoding="utf-8")

    def restore() -> None:
        if original is None:
            gj.unlink(missing_ok=True)
        else:
            gj.write_text(original, encoding="utf-8")
    return restore


def evaluate(root: Path, projects: list[str], properties: list[str], dotnet: str,
             skip_missing: bool = False, pin_sdk: str = "") -> dict:
    root = root.resolve()
    projects = [normalize_project(p) for p in projects]
    restore = _pinned(root, pin_sdk) if pin_sdk else None
    try:
        try:
            sdk = _run([dotnet, "--version"], root).strip()
        except RuntimeError as exc:
            if pin_sdk:
                raise RuntimeError(f"this tree cannot be evaluated under the HEAD's SDK {pin_sdk}: dotnet does "
                                   f"not resolve it here — {exc}") from None
            raise
        if pin_sdk and sdk != pin_sdk:
            raise RuntimeError(f"this tree resolved SDK {sdk}, not the HEAD's {pin_sdk}, despite the pin — "
                               "refusing to evaluate it with a different compiler")
        out: dict[str, dict] = {}
        missing = [p for p in projects if skip_missing and not (root / p).is_file()]
        queue = [p for p in projects if p not in missing]
        while queue:
            rel = queue.pop(0)
            if rel in out:
                continue
            if not (root / rel).is_file():
                raise RuntimeError(f"{rel} does not exist under {root}")
            try:
                out[rel] = evaluate_project(root, rel, properties, dotnet)
            except RuntimeError as exc:
                if pin_sdk:
                    raise RuntimeError(f"{rel} cannot be evaluated under the HEAD's SDK {pin_sdk} — {exc}") from None
                raise
            queue += out[rel]["references"]
    finally:
        if restore:
            restore()
    return {"sdk": sdk, "properties": sorted(properties), "roots": sorted(projects),
            "missing": missing, "projects": dict(sorted(out.items()))}


def main() -> int:
    if "--self-test" in sys.argv[1:]:
        return self_test()
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--root", required=True)
    p.add_argument("--project", action="append", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--property", action="append", default=[])
    p.add_argument("--dotnet", default="dotnet")
    p.add_argument("--skip-missing", action="store_true",
                   help="a named project absent from this tree (a BASE tree that predates it) is recorded "
                        "under `missing` instead of failing")
    p.add_argument("--pin-sdk", default="",
                   help="evaluate with exactly this SDK (a temporary global.json, rollForward disable): the "
                        "HEAD's resolved SDK, for a BASE or trunk-tip tree. Red when it cannot")
    a = p.parse_args()
    try:
        result = evaluate(Path(a.root), a.project, a.property, a.dotnet, a.skip_missing, a.pin_sdk)
    except (RuntimeError, OSError, json.JSONDecodeError, subprocess.TimeoutExpired) as exc:
        print(f"::error title=MSBuild evaluation::{exc}".replace("\n", "%0A"), file=sys.stderr)
        return 1
    Path(a.out).write_text(json.dumps(result, indent=1, sort_keys=True), encoding="utf-8")
    n = len(result["projects"])
    print(f"msbuild-evaluate: {n} project(s) evaluated by MSBuild (SDK {result['sdk']}) — "
          + "; ".join(f"{k}: {len(v['imports'])} import(s), {sum(len(x) for x in v['items'].values())} item(s)"
                      + (f", {len(v['unknownPatterns'])} UNKNOWN pattern(s)" if v["unknownPatterns"] else "")
                      for k, v in result["projects"].items()))
    for k, v in result["projects"].items():
        for u in v["unknownPatterns"]:
            print(f"::warning title=MSBuild evaluation::{k}: {u} — a property here cannot be evaluated, so "
                  "the scope treats this project as touched by any change (fail safe)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
