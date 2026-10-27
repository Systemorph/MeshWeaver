#!/usr/bin/env python3
"""test-build-extension-reading.py: the extension / shelf inputs, as the REAL MSBuild evaluates them.

`msbuild-evaluate.py` runs `dotnet msbuild` (-pp for the imported files, -getItem for the path
items) and writes the one JSON that both `module-build-key.py` and `node-repo-scope.py` consume.
This control runs the real evaluator; it lives here, in core's dotnet-test lane (which has an SDK),
because the two scripts' own `--self-test`s also run where no SDK or core tree exists.

POSITIVE CONTROL: the real `tools/MeshWeaver.PluginTester.Razor` (the first real extension; a
hand-written reader once refused it) evaluates, keys and scopes cleanly, its ProjectReference
followed.

NEGATIVE CONTROLS, each decided by MSBuild itself on a scratch repository, never argued:
  - an imported file:  its edit re-keys and selects;
  - a Choose branch:   the Release branch's file is an input, the Otherwise branch's is NOT;
  - an item transform: the path a transform produces is an input (and appearing re-keys);
  - an Exists()-guarded import, absent: it is in reach, and appearing re-keys;
  - an UNIMPORTED `Directory.Unused.props`: its own Compile item is NOT an input. The file itself
    stays a conservative hashing extra, because a new ancestor Directory.* file is a change.
"""
from __future__ import annotations

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / ".github" / "scripts"
EXTENSION = "tools/MeshWeaver.PluginTester.Razor/MeshWeaver.PluginTester.Razor.csproj"

failures: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> None:
    print(("  ok   " if ok else "  FAIL ") + label + (f" — {detail}" if detail and not ok else ""))
    if not ok:
        failures.append(label)


def load(name: str, module: str):
    spec = importlib.util.spec_from_file_location(module, SCRIPTS / name)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def evaluate(root: Path, project: str, out: Path) -> dict:
    r = subprocess.run([sys.executable, str(SCRIPTS / "msbuild-evaluate.py"), "--root", str(root),
                        "--project", project, "--out", str(out), "--property", f"MeshWeaverRoot={ROOT}/"],
                       capture_output=True, text=True, timeout=900)
    if r.returncode != 0:
        raise RuntimeError(f"msbuild-evaluate.py failed (exit {r.returncode}): {(r.stdout + r.stderr)[-1500:]}")
    return json.loads(out.read_text(encoding="utf-8"))


def scratch_repo(root: Path) -> None:
    files = {
        "Ext/Ext.csproj": """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <Import Project="../Lib/Ext.props" />
  <Import Project="../Lib/Optional.props" Condition="Exists('../Lib/Optional.props')" />
  <Choose>
    <When Condition="'$(Configuration)' == 'Release'"><ItemGroup><Compile Include="../Rel/Release.cs" /></ItemGroup></When>
    <Otherwise><ItemGroup><Compile Include="../Dbg/Debug.cs" /></ItemGroup></Otherwise>
  </Choose>
  <ItemGroup>
    <_Src Include="../Lib/res/a.txt" />
    <EmbeddedResource Include="@(_Src->'../Gen/%(Filename).res')" />
  </ItemGroup>
</Project>
""",
        "Ext/Code.cs": "// ext\n",
        "Lib/Ext.props": "<Project><PropertyGroup><DefineConstants>A</DefineConstants></PropertyGroup></Project>\n",
        "Lib/res/a.txt": "a\n",
        "Rel/Release.cs": "// release\n",
        "Dbg/Debug.cs": "// debug\n",
        "Directory.Unused.props": '<Project><ItemGroup><Compile Include="Other/Stray.cs" /></ItemGroup></Project>\n',
        "Other/Stray.cs": "// stray\n",
    }
    for rel, text in files.items():
        (root / rel).parent.mkdir(parents=True, exist_ok=True)
        (root / rel).write_text(text, encoding="utf-8")


def main() -> int:
    if not (ROOT / EXTENSION).is_file():
        print(f"::error::{EXTENSION} is not in this tree; this control has nothing to check. If the extension "
              "moved, point EXTENSION at its new home rather than deleting the control.")
        return 1
    scope = load("node-repo-scope.py", "node_repo_scope")
    key = load("module-build-key.py", "module_build_key")
    sdk = subprocess.run(["dotnet", "--version"], capture_output=True, text=True, check=True).stdout.strip()

    def reaches(reach, f: str) -> bool:
        return any(f == d or f.startswith(d + "/") or scope.msbuild_glob_matches(f, d) for d in reach)

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        print("== POSITIVE: the real Razor extension, evaluated by MSBuild")
        evaluation = evaluate(ROOT, EXTENSION, tmp / "razor.json")
        check("MSBuild followed its ProjectReference",
              "tools/MeshWeaver.PluginTester.Extensibility/MeshWeaver.PluginTester.Extensibility.csproj"
              in evaluation["projects"], str(sorted(evaluation["projects"])))
        try:
            reach = scope.shelf_dirs(ROOT, EXTENSION, evaluation)
            check("scopes cleanly: reach covers the referenced project and the manifest",
                  reaches(reach, "tools/MeshWeaver.PluginTester.Extensibility/X.cs")
                  and reaches(reach, "tools/MeshWeaver.PluginTester.Razor/build-extension.json"), str(sorted(reach)))
            got = key.shelf_inputs(ROOT, EXTENSION, "build-extensions-project", sdk=sdk, evaluation=evaluation)
            check("keys cleanly, with the publishing SDK recorded", got.get("sdk") == sdk)
            check("the imported MeshWeaverSurfaceManifest.targets is a key input",
                  "MeshWeaverSurfaceManifest.targets" in got["imports"], str(sorted(got["imports"])))
        except ValueError as exc:
            check("scopes and keys cleanly", False, str(exc))

        print("== NEGATIVE CONTROLS on a scratch repository, decided by MSBuild")
        repo = tmp / "repo"
        repo.mkdir()
        scratch_repo(repo)
        proj = "Ext/Ext.csproj"
        ev = evaluate(repo, proj, tmp / "scratch.json")
        k0 = key.shelf_inputs(repo, proj, "build-extensions-project", sdk=sdk, evaluation=ev)
        reach = scope.shelf_dirs(repo, proj, ev)
        linked = set(k0.get("linked", {}))

        check("an imported file is a key input and in reach", "Lib/Ext.props" in k0["imports"] and reaches(reach, "Lib/Ext.props"))
        (repo / "Lib/Ext.props").write_text("<Project><PropertyGroup><DefineConstants>B</DefineConstants></PropertyGroup></Project>\n")
        check("…and its edit re-keys",
              key.shelf_inputs(repo, proj, "build-extensions-project", sdk=sdk, evaluation=ev) != k0)
        (repo / "Lib/Ext.props").write_text("<Project><PropertyGroup><DefineConstants>A</DefineConstants></PropertyGroup></Project>\n")

        check("Choose: the Release branch's file is an input", "Rel/Release.cs" in linked and reaches(reach, "Rel/Release.cs"),
              str(sorted(linked)))
        check("Choose: the Otherwise branch's file is NOT (not keyed, not in reach)",
              "Dbg/Debug.cs" not in linked and not reaches(reach, "Dbg/Debug.cs"), str(sorted(linked)))
        (repo / "Dbg/Debug.cs").write_text("// debug v2\n")
        check("…and editing it leaves the key unchanged",
              key.shelf_inputs(repo, proj, "build-extensions-project", sdk=sdk, evaluation=ev) == k0)

        check("a transform-produced item path is an input and in reach",
              "Gen/a.res" in linked and reaches(reach, "Gen/a.res"), str(sorted(linked)))
        (repo / "Gen").mkdir()
        (repo / "Gen/a.res").write_text("res\n")
        check("…and its appearing re-keys",
              key.shelf_inputs(repo, proj, "build-extensions-project", sdk=sdk, evaluation=ev) != k0)
        shutil.rmtree(repo / "Gen")

        check("an absent Exists()-guarded import is in reach", reaches(reach, "Lib/Optional.props"))
        (repo / "Lib/Optional.props").write_text("<Project />\n")
        ev2 = evaluate(repo, proj, tmp / "scratch2.json")
        check("…MSBuild imports it once it exists", "Lib/Optional.props" in ev2["projects"][proj]["imports"])
        check("…and its appearing re-keys",
              key.shelf_inputs(repo, proj, "build-extensions-project", sdk=sdk, evaluation=ev2)["imports"]
              != k0["imports"])
        (repo / "Lib/Optional.props").unlink()

        check("an UNIMPORTED Directory.Unused.props: MSBuild does not import it",
              "Directory.Unused.props" not in ev["projects"][proj]["imports"])
        check("…so its Compile item is NOT an input (not keyed, not in reach)",
              "Other/Stray.cs" not in linked and not reaches(reach, "Other/Stray.cs"), str(sorted(linked)))
        check("…while the file itself stays a conservative hashing extra (an ancestor Directory.* file)",
              "Directory.Unused.props" in k0["imports"])

    print()
    if failures:
        print(f"::error::{len(failures)} case(s) FAILED: " + "; ".join(failures))
        return 1
    print("test-build-extension-reading: MSBuild's own evaluation keys and scopes the real extension, "
          "and every negative control behaves")
    return 0


if __name__ == "__main__":
    sys.exit(main())
