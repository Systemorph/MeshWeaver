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
from pathlib import Path

# The path-valued item types read. ProjectReference is followed as an edge, not hashed as a file.
ITEM_TYPES = ("Compile", "EmbeddedResource", "AdditionalFiles", "Content", "None", "Analyzer",
              "ProjectReference")
# In a preprocessed project, every imported file appears ALONE on a line inside the
# `<!-- ===== <Import Project="…"> … ===== -->` banner MSBuild writes around it.
_PP_FILE = re.compile(r"^(/[^\n<>\"]+\.(?:props|targets|csproj|proj|projitems|xml))$", re.M)
_IMPORT = re.compile(r"<Import\b[^>]*?\bProject=(?:\"([^\"]*)\"|'([^']*)')")


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


def declared_imports(root: Path, files: list[str], project_dir: str) -> list[str]:
    """`<Import Project>` paths WRITTEN in the evaluated in-repo files that resolve in-repo, present
    or not, for hashing and reach only. MSBuild already decided which ones are imported."""
    out: set[str] = set()
    for rel in files:
        text = (root / rel).read_text(encoding="utf-8", errors="replace")
        this_dir = (root / rel).parent
        for a, b in _IMPORT.findall(text):
            value = (a or b).strip().replace("\\", "/")
            base = this_dir
            if value.startswith("$(MSBuildThisFileDirectory)"):
                value = value[len("$(MSBuildThisFileDirectory)"):]
            elif value.startswith("$(MSBuildProjectDirectory)"):
                value, base = value[len("$(MSBuildProjectDirectory)"):].lstrip("/"), root / project_dir
            if not value or "$(" in value or "*" in value or "?" in value:
                continue  # MSBuild resolved what it imports; this extra only adds literal paths
            hit = _in_repo(root, str(base / value))
            if hit is not None:
                out.add(hit)
    return sorted(out)


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
    got = json.loads(_run([dotnet, "msbuild", str(proj), "-nologo", *props,
                           *[f"-getItem:{t}" for t in ITEM_TYPES]], root))
    items: dict[str, list[str]] = {}
    references: list[str] = []
    for kind, entries in (got.get("Items") or {}).items():
        paths = sorted({r for e in entries if (r := _in_repo(root, e.get("FullPath") or "")) is not None})
        if kind == "ProjectReference":
            references = [p for p in paths if (root / p).is_file()]
        elif paths:
            items[kind] = paths
    project_dir = Path(rel).parent.as_posix()
    return {"imports": imports, "declaredImports": declared_imports(root, imports, project_dir),
            "items": items, "references": references}


def evaluate(root: Path, projects: list[str], properties: list[str], dotnet: str) -> dict:
    root = root.resolve()
    sdk = _run([dotnet, "--version"], root).strip()
    out: dict[str, dict] = {}
    queue = [Path(p).as_posix() for p in projects]
    while queue:
        rel = queue.pop(0)
        if rel in out:
            continue
        if not (root / rel).is_file():
            raise RuntimeError(f"{rel} does not exist under {root}")
        out[rel] = evaluate_project(root, rel, properties, dotnet)
        queue += out[rel]["references"]
    return {"sdk": sdk, "properties": sorted(properties), "roots": sorted(Path(p).as_posix() for p in projects),
            "projects": dict(sorted(out.items()))}


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--root", required=True)
    p.add_argument("--project", action="append", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--property", action="append", default=[])
    p.add_argument("--dotnet", default="dotnet")
    a = p.parse_args()
    try:
        result = evaluate(Path(a.root), a.project, a.property, a.dotnet)
    except (RuntimeError, OSError, json.JSONDecodeError, subprocess.TimeoutExpired) as exc:
        print(f"::error title=MSBuild evaluation::{exc}".replace("\n", "%0A"), file=sys.stderr)
        return 1
    Path(a.out).write_text(json.dumps(result, indent=1, sort_keys=True), encoding="utf-8")
    n = len(result["projects"])
    print(f"msbuild-evaluate: {n} project(s) evaluated by MSBuild (SDK {result['sdk']}) — "
          + "; ".join(f"{k}: {len(v['imports'])} import(s), {sum(len(x) for x in v['items'].values())} item(s)"
                      for k, v in result["projects"].items()))
    return 0


if __name__ == "__main__":
    sys.exit(main())
