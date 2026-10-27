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


def run_evaluate(root: Path, project: str, out: Path, *extra: str):
    return subprocess.run([sys.executable, str(SCRIPTS / "msbuild-evaluate.py"), "--root", str(root),
                           "--project", project, "--out", str(out), "--property", f"MeshWeaverRoot={ROOT}/", *extra],
                          capture_output=True, text=True, timeout=900)


def evaluate(root: Path, project: str, out: Path, *extra: str) -> dict:
    r = run_evaluate(root, project, out, *extra)
    if r.returncode != 0:
        raise RuntimeError(f"msbuild-evaluate.py failed (exit {r.returncode}): {(r.stdout + r.stderr)[-1500:]}")
    return json.loads(out.read_text(encoding="utf-8"))


def scratch_repo(root: Path) -> None:
    files = {
        "Ext/Ext.csproj": """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <Import Project="../Lib/Ext.props" />
  <Import Project="../Lib/Optional.props" Condition="Exists('../Lib/Optional.props')" />
  <Import Project="../Imp/*.props" />
  <ItemGroup><Compile Include="../Glob/*.cs" /></ItemGroup>
  <PropertyGroup><EmitterRoot>$(MSBuildThisFileDirectory)../Emit</EmitterRoot></PropertyGroup>
  <ItemGroup><Compile Include="$(EmitterRoot)/*.cs" /></ItemGroup>
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
        "Imp/Last.props": "<Project />\n",
        "Glob/Last.cs": "// the only match of ../Glob/*.cs\n",
        "Glob/notes.txt": "beside the glob, not covered by it\n",
        "Unk/Unk.csproj": """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="$([System.IO.Path]::Combine('$(MSBuildProjectDirectory)', '..', 'Elsewhere'))/*.cs" /></ItemGroup>
</Project>
""",
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
        check("Choose: MSBuild's evaluation does NOT list the Otherwise branch's file as an item",
              "Dbg/Debug.cs" not in str(ev["projects"][proj]["items"]), str(ev["projects"][proj]["items"]))
        check("…but the conservative sweep keeps it, because the project text names it (over-inclusion costs a "
              "rebuild, omission a stale bundle)", "Dbg/Debug.cs" in ev["projects"][proj]["swept"] and reaches(reach, "Dbg/Debug.cs"))

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

        print("== GLOBS: deleting the last match, adding a first one (BASE ∪ HEAD, and the declared patterns)")
        patterns = set(ev["projects"][proj].get("declaredPatterns") or [])
        check("MSBuild imported the import glob's match and listed the Compile glob's",
              "Imp/Last.props" in ev["projects"][proj]["imports"] and "Glob/Last.cs" in linked)
        check("the declared glob patterns are recorded", {"Imp/*.props", "Glob/*.cs"} <= patterns, str(sorted(patterns)))
        (repo / "Imp/Last.props").unlink()
        (repo / "Glob/Last.cs").unlink()
        head = evaluate(repo, proj, tmp / "head.json")
        check("after deleting both last matches, HEAD's evaluation lists neither",
              "Imp/Last.props" not in head["projects"][proj]["imports"]
              and "Glob/Last.cs" not in str(head["projects"][proj]["items"]))
        union = scope.shelf_dirs(repo, proj, [head, ev])
        head_reach = scope.shelf_dirs(repo, proj, head)
        for f in ("Imp/Last.props", "Glob/Last.cs"):
            check(f"deleting the last match {f}: in the BASE ∪ HEAD reach", reaches(union, f))
            check(f"…and HEAD alone still reaches it through the declared pattern", reaches(head_reach, f))
        for f in ("Glob/First.cs", "Imp/First.props"):
            check(f"a FIRST match {f} added to an emptied glob is in reach", reaches(head_reach, f))
        check("negative control — a file in the glob's directory the pattern does not cover is NOT in reach",
              not reaches(union, "Glob/notes.txt"))

        print("== PROPERTY-BASED patterns: substituted by MSBuild, or UNKNOWN and fail safe")
        check("`$(EmitterRoot)/*.cs` (zero matches) is kept as the EVALUATED pattern Emit/*.cs",
              "Emit/*.cs" in patterns, str(sorted(patterns)))
        check("…so a first match Emit/First.cs is in reach", reaches(head_reach, "Emit/First.cs"))
        check("negative control — Emit/notes.txt, not covered by it, is NOT in reach", not reaches(head_reach, "Emit/notes.txt"))
        check("negative control — a project whose patterns all evaluate records no unknown pattern",
              not ev["projects"][proj].get("unknownPatterns"))
        unk = evaluate(repo, "Unk/Unk.csproj", tmp / "unk.json")
        check("a property FUNCTION in an Include is recorded as an unknown pattern",
              bool(unk["projects"]["Unk/Unk.csproj"].get("unknownPatterns")), str(unk["projects"]["Unk/Unk.csproj"]))
        check("…and the scope treats that project as touched by ANY change (fail safe)",
              reaches(scope.shelf_dirs(repo, "Unk/Unk.csproj", unk), "Other/Stray.cs"))
        try:
            key.shelf_inputs(repo, "Unk/Unk.csproj", "build-extensions-project", sdk=sdk, evaluation=unk)
            check("…and the KEY refuses it (scope widening alone does not stop a stale reuse)", False, "no error")
        except ValueError as exc:
            check("…and the KEY refuses it, naming the pattern (scope widening alone does not stop a stale reuse)",
                  "System.IO.Path" in str(exc), str(exc))

        print("== ITEMS A <Target> ADDS: invisible to evaluation, so fail CLOSED unless tracked")
        tgt = repo / "Tgt"
        tgt.mkdir()
        (tgt / "Local.cs").write_text("// in the project's own directory\n")
        (repo / "Gen2").mkdir()
        (repo / "Gen2/Extra.cs").write_text("// outside the project, not tracked\n")

        def tgt_eval(body: str):
            (tgt / "Tgt.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                f'</PropertyGroup>{body}</Project>\n', encoding="utf-8")
            return run_evaluate(repo, "Tgt/Tgt.csproj", tmp / "tgt.json")

        # A statically resolvable in-repo path a target adds is now a SWEPT input (keyed, in reach), so
        # it is accepted; only what cannot be resolved statically is refused.
        for label, body in (
                ("an in-repo Compile item a BeforeTargets=CoreCompile target adds",
                 '<Target Name="AddGen" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="../Gen2/Extra.cs" /></ItemGroup></Target>'),
                ("a plain @(X) whose X names an in-repo file",
                 '<ItemGroup><_Mine Include="../Gen2/Extra.cs" /></ItemGroup><Target Name="Use" BeforeTargets="CoreCompile">'
                 '<ItemGroup><Compile Include="@(_Mine)" /></ItemGroup></Target>')):
            r = tgt_eval(body)
            swept_now = json.loads((tmp / "tgt.json").read_text())["projects"]["Tgt/Tgt.csproj"]["swept"] if r.returncode == 0 else []
            check(f"{label} ⇒ accepted, because the sweep KEYS Gen2/Extra.cs", r.returncode == 0 and "Gen2/Extra.cs" in swept_now,
                  f"exit={r.returncode} swept={swept_now} {r.stderr[-200:]}")
        for label, body, needle in (
                ("an item TRANSFORM inside a target",
                 '<ItemGroup><_Src Include="Local.cs" /></ItemGroup><Target Name="Map" BeforeTargets="CoreCompile">'
                 "<ItemGroup><Compile Include=\"@(_Src->'../Gen2/%(Filename).cs')\" /></ItemGroup></Target>",
                 "item transform"),
                ("a property FUNCTION path inside a target (not statically resolvable)",
                 '<Target Name="Fn" BeforeTargets="CoreCompile"><ItemGroup>'
                 "<Compile Include=\"$([System.IO.Path]::Combine('$(MSBuildProjectDirectory)','..','Gen2','Extra.cs'))\" />"
                 '</ItemGroup></Target>',
                 "property function"),
                # TASK attributes: every dynamic expression shape that is not statically resolved
                ("a TRANSFORM in a task attribute (Copy SourceFiles)",
                 '<ItemGroup><_GeneratorName Include="Extra" /></ItemGroup><Target Name="CopyGen" AfterTargets="Build">'
                 "<Copy SourceFiles=\"@(_GeneratorName->'../Gen2/%(Identity).cs')\" DestinationFolder=\"$(OutDir)\" /></Target>",
                 "item transform"),
                ("METADATA BATCHING in a task attribute",
                 '<ItemGroup><_G Include="Local.cs" /></ItemGroup><Target Name="Batch" AfterTargets="Build">'
                 '<Copy SourceFiles="../Gen2/%(_G.Filename).cs" DestinationFolder="$(OutDir)" /></Target>',
                 "metadata batching"),
                ("a PROPERTY FUNCTION over items in a task attribute",
                 '<ItemGroup><_G Include="Local.cs" /></ItemGroup><Target Name="Fn2" AfterTargets="Build">'
                 "<Copy SourceFiles=\"$([System.IO.Path]::Combine('$(MSBuildProjectDirectory)', '@(_G)'))\" DestinationFolder=\"$(OutDir)\" /></Target>",
                 "not a plain @(X)"),
                ("a transform reaching a task attribute THROUGH a target property",
                 '<ItemGroup><_GeneratorName Include="Extra" /></ItemGroup><Target Name="Via" AfterTargets="Build">'
                 "<PropertyGroup><_Src>@(_GeneratorName->'../Gen2/%(Identity).cs')</_Src></PropertyGroup>"
                 '<Copy SourceFiles="$(_Src)" DestinationFolder="$(OutDir)" /></Target>',
                 "assigned inside a target"),
                ("a target PROPERTY reading a file through a property function over items",
                 '<ItemGroup><_G Include="Local.cs" /></ItemGroup><Target Name="Read" AfterTargets="Build">'
                 "<PropertyGroup><_Text>$([System.IO.File]::ReadAllText('@(_G)'))</_Text></PropertyGroup></Target>",
                 "property <_Text>"),
                # Thread 4384923575, both shapes — refused by the ALLOW-LIST, not by a shape rule:
                ("a TARGET-LOCAL property in a task attribute (GeneratorDir set inside the target)",
                 '<Target Name="Gen" AfterTargets="Build"><PropertyGroup><GeneratorDir>../Gen2</GeneratorDir></PropertyGroup>'
                 '<Copy SourceFiles="$(GeneratorDir)/Extra.cs" DestinationFolder="$(OutDir)" /></Target>',
                 "$(GeneratorDir), a property assigned inside a target"),
                ("a PATH FUNCTION in a task attribute ($([System.IO.Path]::Combine(…)), item-free)",
                 '<Target Name="Path" AfterTargets="Build">'
                 "<Copy SourceFiles=\"$([System.IO.Path]::Combine('$(MSBuildProjectDirectory)','..','Gen2','Extra.cs'))\" "
                 'DestinationFolder="$(OutDir)" /></Target>',
                 "property function"),
                ("an in-repo literal that is NOT a tracked input (a path nothing keys)",
                 '<Target Name="Untracked" AfterTargets="Build">'
                 '<Copy SourceFiles="../Gen2/NotThere.cs" DestinationFolder="$(OutDir)" /></Target>',
                 "not a tracked input"),
                ("a literal OUTSIDE the repository and every exempt root",
                 '<Target Name="Outside" AfterTargets="Build">'
                 '<Copy SourceFiles="/etc/hosts" DestinationFolder="$(OutDir)" /></Target>',
                 "outside every exempt root"),
                ("an XmlPeek whose XmlInputPath (the file it READS) lies outside every exempt root — the XPath exemption covers Query only",
                 '<Target Name="PeekOutside" BeforeTargets="CoreCompile">'
                 '<XmlPeek XmlInputPath="/etc/hosts" Query="//x/text()"><Output TaskParameter="Result" ItemName="_V" /></XmlPeek></Target>',
                 "outside every exempt root")):
            r = tgt_eval(body)
            check(f"{label} ⇒ REFUSED red, naming the target and the file",
                  r.returncode != 0 and "Tgt/Tgt.csproj" in r.stderr and "<Target Name=" in r.stderr and needle in r.stderr,
                  f"exit={r.returncode} {r.stderr[-300:]}")
        for label, body in (
                ("a target staging an OUTPUT under $(IntermediateOutputPath)",
                 '<Target Name="Stage" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="$(IntermediateOutputPath)Gen.cs" /></ItemGroup></Target>'),
                ("a target adding a file inside the project's own (tree-hashed) directory",
                 '<Target Name="Own" BeforeTargets="CoreCompile"><ItemGroup><Compile Include="Local.cs" /></ItemGroup></Target>'),
                ("a target drawing on an SDK-produced item (@(ReferencePath))",
                 '<Target Name="Refs" AfterTargets="ResolveAssemblyReferences"><ItemGroup><_R Include="@(ReferencePath)" /></ItemGroup></Target>'),
                ("a PLAIN @(X) over already-tracked items in a task attribute",
                 '<ItemGroup><_Mine Include="Local.cs" /></ItemGroup><Target Name="CopyMine" AfterTargets="Build">'
                 '<Copy SourceFiles="@(_Mine);@(Compile)" DestinationFolder="$(OutDir)" /></Target>'),
                ("transforms and batching in a Condition, Message Text and WriteLinesToFile Lines (text, not paths)",
                 '<ItemGroup><_G Include="Local.cs" /></ItemGroup><Target Name="Text" AfterTargets="Build">'
                 "<Message Condition=\"'%(_G.Filename)' != ''\" Text=\"@(_G->'%(Filename)')\" />"
                 "<WriteLinesToFile File=\"$(IntermediateOutputPath)names.txt\" Lines=\"@(_G->'%(Filename)')\" /></Target>"),
                ("an XmlPeek/XmlPoke Query is XPath, not a path: `//PlatformVersion/text()` (the Plugins AssemblyVersion check)",
                 '<Target Name="Peek" BeforeTargets="CoreCompile">'
                 '<XmlPeek XmlInputPath="$(MSBuildProjectDirectory)/Tgt.csproj" Query="//TargetFramework/text()">'
                 '<Output TaskParameter="Result" ItemName="_V" /></XmlPeek>'
                 '<XmlPoke XmlInputPath="$(IntermediateOutputPath)x.xml" Query="/a/@b" Value="//c" /></Target>'),
                ("(a) a literal in-repo path the sweep tracks, an evaluation-time property and scalars",
                 '<Target Name="Lit" AfterTargets="Build">'
                 '<Copy SourceFiles="../Gen2/Extra.cs" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true" Retries="2" /></Target>'),
                ("a statically resolved property function reading a file in a target property",
                 '<Target Name="ReadStatic" AfterTargets="Build"><PropertyGroup>'
                 "<_Text>$([System.IO.File]::ReadAllText('$(MSBuildProjectDirectory)/Local.cs'))</_Text></PropertyGroup></Target>")):
            r = tgt_eval(body)
            check(f"negative control — {label} is accepted", r.returncode == 0, r.stderr[-300:])

        print("== THE CONSERVATIVE PATH SWEEP: a HintPath, any import suffix, a task's inputs — not the whole repo")
        sw = repo / "Sw"
        sw.mkdir()
        for rel_, text_ in (("Lib/Extra.msbuild", "<Project />\n"), ("Libs/Foo.dll", "dll v1\n"),
                            ("Assets/logo.png", "png v1\n"), ("Other/Unmentioned.txt", "nobody names me\n")):
            (repo / rel_).parent.mkdir(parents=True, exist_ok=True)
            (repo / rel_).write_text(text_)
        (sw / "Sw.csproj").write_text("""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><RepoTop>$(MSBuildProjectDirectory)/..</RepoTop></PropertyGroup>
  <Import Project="../Lib/Extra.msbuild" />
  <ItemGroup><Reference Include="Foo"><HintPath>../Libs/Foo.dll</HintPath></Reference></ItemGroup>
  <ItemGroup Condition="Exists('$(RepoTop)')"><None Include="Sw.csproj" /></ItemGroup>
  <Target Name="CopyIt" AfterTargets="Build"><Copy SourceFiles="../Assets/logo.png" DestinationFolder="$(OutDir)" /></Target>
</Project>
""")
        swp = "Sw/Sw.csproj"
        sev = evaluate(repo, swp, tmp / "sw.json")
        entry = sev["projects"][swp]
        check("an import with a NON-.props/.targets suffix (Lib/Extra.msbuild) is an import",
              "Lib/Extra.msbuild" in entry["imports"], str(entry["imports"]))
        check("a <Reference>'s HintPath (Libs/Foo.dll) is swept in", "Libs/Foo.dll" in entry["swept"], str(entry["swept"]))
        check("a Copy task's SourceFiles inside a target (Assets/logo.png) is swept in",
              "Assets/logo.png" in entry["swept"], str(entry["swept"]))
        sk0 = key.shelf_inputs(repo, swp, "build-extensions-project", sdk=sdk, evaluation=sev)
        sreach = scope.shelf_dirs(repo, swp, sev)
        for rel_ in ("Lib/Extra.msbuild", "Libs/Foo.dll", "Assets/logo.png"):
            old = (repo / rel_).read_text()
            (repo / rel_).write_text(old + "changed\n")
            changed_key = key.shelf_inputs(repo, swp, "build-extensions-project", sdk=sdk, evaluation=sev) != sk0
            (repo / rel_).write_text(old)
            check(f"{rel_}: editing it re-keys, and the scope reaches it", changed_key and reaches(sreach, rel_))
        check("NEGATIVE: an in-repo file nothing mentions (Other/Unmentioned.txt) is neither keyed nor reached",
              "Other/Unmentioned.txt" not in str(sk0) and not reaches(sreach, "Other/Unmentioned.txt"))
        check("a $(MSBuildProjectDirectory)/.. root pulls in NO directory and never the repository root",
              all((repo / s_).is_file() for s_ in entry["swept"]) and not ({"", "."} & set(sreach))
              and "**" not in sreach, f"swept={entry['swept']}")

        print("== THE HEAD's SDK, pinned for another tree")
        pinned = evaluate(repo, "./Ext/../Ext/Ext.csproj", tmp / "pinned.json", "--pin-sdk", sdk)
        check("--pin-sdk evaluates with exactly the HEAD's SDK, the project spelling normalized",
              pinned["sdk"] == sdk and proj in pinned["projects"], f"{pinned['sdk']} {sorted(pinned['projects'])}")
        check("…and the temporary global.json is removed again", not (repo / "global.json").exists())
        r = run_evaluate(repo, proj, tmp / "bad.json", "--pin-sdk", "9.9.999")
        check("NEGATIVE: a tree that cannot be evaluated under the pinned SDK fails RED, saying so",
              r.returncode != 0 and "HEAD's SDK 9.9.999" in r.stderr, f"exit={r.returncode} {r.stderr[-300:]}")
        check("…and the temporary global.json is removed after the failure too", not (repo / "global.json").exists())

    print()
    if failures:
        print(f"::error::{len(failures)} case(s) FAILED: " + "; ".join(failures))
        return 1
    print("test-build-extension-reading: MSBuild's own evaluation keys and scopes the real extension, "
          "and every negative control behaves")
    return 0


if __name__ == "__main__":
    sys.exit(main())
