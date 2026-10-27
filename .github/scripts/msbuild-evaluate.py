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
# In a preprocessed project, every imported file appears ALONE on a line inside the
# `<!-- ===== <Import Project="…"> … ===== -->` banner MSBuild writes around it.
_PP_FILE = re.compile(r"^(/[^\n<>\"]+\.(?:props|targets|csproj|proj|projitems|xml))$", re.M)
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
    return _in_repo(root, value if value.startswith("/") else str(base / value))


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


# ── items a <Target> creates: invisible to evaluation, so they FAIL CLOSED unless tracked ─────────
# `-getItem` reports EVALUATION-time items only. An ItemGroup inside a <Target> (a BeforeTargets=
# "CoreCompile" adding Compile, say) runs at build time, and its paths would reach the compiler
# while neither the key nor the scope sees them. So every path item a target adds in an in-repo
# MSBuild file must be one of:
#   - outside this repository (the SDK, NuGet, the platform: keyed by the SDK version and the
#     platform ref + digest, as everywhere else in this evaluation);
#   - under one of the project's BUILD OUTPUT directories (_OUTPUT_PROPERTIES): output staging,
#     like the Razor extension's razor-generators.json under $(IntermediateOutputPath), is a
#     product of the build, not an input to it;
#   - inside the project's own directory (tree-hashed by the key, in the scope's reach) or an
#     item MSBuild already evaluated;
#   - a plain `@(X)` whose every in-repo declaration passes these same rules (an X no in-repo file
#     declares is produced by the SDK's own targets, e.g. @(ReferencePath)).
# Anything else (an in-repo path outside them, an item transform `@(X->'…')`, a metadata path,
# a property no evaluation resolves) is REFUSED, naming the target and the file.
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


def target_item_violations(root: Path, files: list[str], project_rel: str, properties: dict[str, str],
                           tracked: set[str]) -> list[str]:
    project_dir = (root / project_rel).parent
    outputs = []
    for name in _OUTPUT_PROPERTIES:
        value = (properties.get(name) or "").strip().replace("\\", "/")
        if value:
            outputs.append(os.path.normpath(value if value.startswith("/") else str(project_dir / value)))
    declarations: dict[str, list[tuple[str, Path]]] = {}
    in_targets: list[tuple[str, str, str, Path, str]] = []   # (target, tag, include, file, rel)
    for rel in files:
        try:
            tree = ET.parse(root / rel)
        except ET.ParseError as exc:
            raise RuntimeError(f"{rel} is not well-formed MSBuild XML ({exc})") from None

        def walk(node, target):
            for el in node:
                tag = _local(el.tag)
                if tag == "Target":
                    walk(el, el.get("Name") or "?")
                elif tag == "ItemGroup":
                    for item in el:
                        name = _local(item.tag)
                        include = item.get("Include")
                        if not name or include is None or name in _NAME_ITEMS:
                            continue
                        declarations.setdefault(name, []).append((include, root / rel))
                        if target:
                            in_targets.append((target, name, include, root / rel, rel))
                else:
                    walk(el, target)
        walk(tree.getroot(), None)

    def reason(include: str, f: Path, seen: frozenset[str]) -> str | None:
        for piece in include.split(";"):
            piece = piece.strip()
            if not piece:
                continue
            if piece.startswith("@("):
                m = re.fullmatch(r"@\(([A-Za-z_][\w.-]*)\)", piece)
                if not m:
                    return f"'{piece}' is an item transform; the paths it produces are not modelled"
                name = m.group(1)
                if name in seen:
                    continue
                for inner, inner_file in declarations.get(name, []):  # none: produced by the SDK's targets
                    why = reason(inner, inner_file, seen | {name})
                    if why:
                        return f"'{piece}' draws on {inner_file.relative_to(root).as_posix()}: {why}"
                continue
            if "%(" in piece:
                return f"'{piece}' is a metadata-based path, not modelled"
            path = _absolute(piece, project_dir, f.parent, project_dir, properties)
            if path is None:
                return f"'{piece}' uses a property this evaluation cannot resolve"
            try:
                rel = Path(path).relative_to(root).as_posix()
            except ValueError:
                continue  # outside the repository: SDK / NuGet / platform bytes, keyed elsewhere
            if any(path == o or path.startswith(o.rstrip("/") + "/") for o in outputs):
                continue  # a build output (staging), not an input
            if rel.startswith(Path(project_rel).parent.as_posix() + "/") or rel in tracked:
                continue
            return f"'{piece}' resolves to {rel}, an in-repo path that is not a tracked input"
        return None

    out = []
    for target, tag, include, f, rel in in_targets:
        why = reason(include, f, frozenset())
        if why:
            out.append(f"{rel}: <Target Name=\"{target}\"> adds <{tag} Include=\"{include}\"> — {why}")
    return out


def evaluate_project(root: Path, rel: str, properties: list[str], dotnet: str) -> dict:
    proj = root / rel
    props = ["-p:Configuration=Release"] + [f"-p:{p}" for p in properties]
    with tempfile.TemporaryDirectory() as tmp:
        pp = Path(tmp) / "pp.xml"
        _run([dotnet, "msbuild", str(proj), "-nologo", *props, f"-pp:{pp}"], root)
        text = pp.read_text(encoding="utf-8", errors="replace")
        if not text.strip():
            raise RuntimeError(f"`dotnet msbuild -pp` wrote nothing for {rel}")
    imports = sorted({r for m in _PP_FILE.findall(text) if (r := _in_repo(root, m)) is not None} | {rel})
    wanted = sorted(set(_referenced_properties(root, imports)) | set(_OUTPUT_PROPERTIES))
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
    tracked = {p for paths in items.values() for p in paths} | set(imports)
    violations = target_item_violations(root, imports, rel, values, tracked)
    if violations:
        raise RuntimeError(
            f"{rel}: a <Target> adds path items that MSBuild's evaluation cannot see, so neither the key "
            "nor the scope would — refused rather than keyed on a guess:\n  " + "\n  ".join(violations)
            + "\nDeclare the item at evaluation time (outside the <Target>), or stage it under a build output.")
    return {"imports": imports, "declaredImports": declared_imports, "declaredPatterns": declared_patterns,
            "unknownPatterns": unknown, "items": items, "references": references}


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
