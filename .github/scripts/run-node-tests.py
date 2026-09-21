#!/usr/bin/env python3
"""run-node-tests.py — EXECUTE a module's in-node tests locally, without booting a mesh.

`compile-check.py` proves a NodeType's C# compiles. It cannot prove the C# is CORRECT, and the gap
between those two is where the expensive bugs live: during the Ifrs17 port every genuine defect —
a cash-flow array supplied yearly onto a monthly grid, a group-currency constant that never compared
equal to the node paths in the data — compiled perfectly green and returned silently wrong numbers.

This script closes that gap. For EVERY NodeType in the module it resolves that type's own declared
`sources` (+ its `Test` subtree) exactly as the mesh does, assembles THAT set — and nothing else —
into a throwaway console app, references the same framework assemblies the mesh compiles against,
reflects over every `*Tests` class and runs each public static parameterless method.

    python3 scripts/run-node-tests.py Ifrs17                    # run one module's tests
    python3 scripts/run-node-tests.py Ifrs17 --list             # just list what would run
    python3 scripts/run-node-tests.py Underwriting --type Workbench     # one NodeType

In a satellite `scripts/run-node-tests.py` is a launcher that fetches THIS file and runs it; in core
it is run directly (`python3 .github/scripts/run-node-tests.py --self-test`).

🚨 ONE COMPILATION PER NODETYPE — never a merged module-wide compile. The mesh compiles each
NodeType on its own, so a package may DELIBERATELY duplicate a shared file across several types'
`Source/` folders; each type then sees exactly one definition. A harness that concatenates every
`Source/` folder in the package is compiling a program that does not exist anywhere, and it dies on
the duplicates: `run-node-tests.py UWDeepfield` used to fail with ~150 CS0101/CS0111/CS0102/CS8863
errors before a single test ran, so **46 NodeTypes' suites had never executed once** — invisibly,
because `compile-check.py` compiles per type (like the mesh) and stayed green throughout
(MeshWeaver.Reinsurance#113). Source resolution is therefore delegated to `compile-check.py`'s
`resolve_sources`, so the two gates can never drift apart on what a NodeType actually contains.

Exit code is non-zero if any test fails or any NodeType's set fails to build, so it can gate a PR
alongside the compile check. The mesh CI gate still EXECUTES the `Tests` layout areas against a real
instance — this is the fast local loop, not a replacement for it.

🚨 THIS IS THE CANONICAL, AND IT IS THE ONLY COPY (MeshWeaver#4785). It lived as three vendored
copies — MeshWeaver.Crm, MeshWeaver.Reinsurance, MeshWeaver.Plugins — with nothing in core to
compare them against, and `compile-check.py`, which they all import, is fetched at a moving ref. So
when core #4711 replaced the compile model and removed `usings_union`, all three broke at once and
differently, and no CI lane anywhere was red: Reinsurance's died with `AttributeError` before a
single test ran, Crm's had been patched with a local re-derivation of the union (so it kept running,
against a model the gate no longer uses), and Plugins' kept working only because it loads a VENDORED
`compile-check.py` fork that still carried the removed helper. Three vintages of one script, three
different silent failures. Do not re-vendor it: a satellite fetches it through
`scripts/platform-script.py` at the SAME ref that lane fetches `compile-check.py` at, so the harness
and the gate can never again be two vintages of the same model.

🚨 AND IT DOES NOT SHAPE ITS OWN COMPILATION UNIT — `compile-check.py` does, via `build_unit`.
That is the invariant the vendored copies stated and then lost. The mesh concatenates a NodeType's
sources into ONE unit and MOVES every `using` to the top
(`DynamicMeshNodeAttributeGenerator.ShapeAuthoredSource`), so a file relying on a sibling's `using`
compiles there; a per-file compile does not, and a per-file compile plus a COPIED global-usings
prelude — what this script used to do — cannot carry an alias or a `using static` across files at
all (a copied `using X = Y;` is CS1537, so the old union dropped aliases and the sibling then failed
CS0246). Calling the gate's own `build_unit` is what makes "compiles under the gate" and "compiles
under the test harness" the same sentence rather than two implementations that agree today.
"""
from __future__ import annotations

import argparse
import ast
import glob
import hashlib
import json
import platform
import re
import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
# 🚨 The repo under test arrives in the ENVIRONMENT, never from this file's location — because this
# file's location is a temp cache. `scripts/platform-script.py` fetches the canonical into
# `<repo>/.platform-scripts/<ref>/`, and the CI lanes into `$RUNNER_TEMP`; `SCRIPTS.parent` there is
# a cache directory with no node content in it, so a copy that derived its root that way would
# report "no such module" for every module in the repository that invoked it. `MW_REPO_ROOT` is the
# same variable `compile-check.py` reads and `platform-script.lane_env()` already sets, so a
# satellite needs nothing new; run from a checkout with neither set, the CWD is the repo.
ROOT = Path(os.environ["MW_REPO_ROOT"]).resolve() if os.environ.get("MW_REPO_ROOT") else Path.cwd().resolve()

# The runner brackets its own output with these so the harness never has to guess where MSBuild
# stops and the tests begin. A missing BEGIN line means the set did not BUILD — reported as such,
# with the compiler diagnostics, instead of being read as "no tests here".
BEGIN = "##NODETESTS-BEGIN##"
SUMMARY = "##NODETESTS-SUMMARY"

# A class the runner could pick up. Used only to decide which sets are worth compiling: a NodeType
# with no test suite is not compiled here (compile-check.py already compiles every type) — and the
# count of those is REPORTED, never silently dropped.
SUITE_DECL = re.compile(r"\bclass\s+\w*Tests\b")

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>NodeTests</AssemblyName>
    <NoWarn>CS1591;CS0618;CS8618;CS8603;CS8604</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <!-- 🚨 The shared framework, so the Microsoft.Extensions.* assemblies resolve at the version
         the MeshWeaver assemblies were BUILT against (10.0.0). Without it the only copies in the
         ref set are whatever happens to sit in some project's bin — MeshWeaver.Cli ships 9.0.0 —
         and the load fails at RUN time with "Could not load file or assembly
         'Microsoft.Extensions.DependencyInjection.Abstractions, Version=10.0.0.0'".
         That failure looks exactly like a broken test: it is reported against whichever case first
         touches a MeshWeaver type with a DI dependency, e.g. any test that constructs a MeshNode.
         Until this was added no test in this repo could construct one. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
{compiles}
  </ItemGroup>
  <ItemGroup>
{references}
  </ItemGroup>
  <ItemGroup>
    <!-- 🚨 Rx IS NOT RESTORED FROM NuGet HERE — it comes from the reference set, the same rule
         compile-check.py states at length (core #4404, after MeshWeaver.Plugins#1911). A version
         LITERAL is a guess about what the platform carries, and this one was measurably wrong:
         `<PackageReference Include="System.Reactive" Version="6.1.0" />` sat here until 2026-09-19
         and MSBuild resolved it as PRIMARY over the reference set's own 7.0.0.0 —

           MSB3277: There was a conflict between "System.Reactive, Version=6.1.0.0" and
                    "System.Reactive, Version=7.0.0.0" …
                    "6.1.0.0" was chosen because it was primary

         — so every suite ran against an Rx the platform does not ship, and the warning was
         invisible because this harness prints only what the runner emits after its BEGIN marker.

         🚨 Removing it RELOCATES that decision to the reference set; it does not silence MSB3277 and
         must not be read as doing so. Measured 2026-09-19 against a sibling SOURCE build: the set's
         own System.Reactive.dll came from a BYSTANDER copy in `MeshWeaver.Plugin.Build/bin` at
         6.1.0.0 while other core assemblies reference 7.0.0.0, so the same conflict is reported with
         the refs dir named instead of NuGet. That is the right place for it — `compile-check.py`
         resolves Rx out of the same set by the same rule, so the harness and the gate now disagree
         about nothing, and on CI's image-extracted set both get the platform's actual Rx.

         Nothing fills the hole if the set is short: `short_reference_set_refusal`, which this script
         already calls before compiling anything, REFUSES a reference set with no System.Reactive.dll
         rather than guessing. Do not reintroduce a literal "for the case the refs dir has none" —
         that case exits first, and a pin is a second place to forget the platform moved. -->
    <!-- 10.0.0, matching the net10.0 framework assemblies. Pinned at 9.0.0 these win as "primary"
         and every test that touches MeshWeaver.Data/Graph/Mesh dies at RUN time with
         "Could not load file or assembly ... Abstractions, Version=10.0.0.0" — after compiling
         perfectly, which makes it read like a broken test rather than a broken harness. -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
  </ItemGroup>
{analyzers}
</Project>
"""

RUNNER = r"""
using System;
using System.Linq;
using System.Reflection;

internal static class NodeTestRunner
{
    private static int Main(string[] args)
    {
        var listOnly = args.Contains("--list");
        Console.WriteLine("##NODETESTS-BEGIN##");
        var suites = typeof(NodeTestRunner).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsAbstract && t.IsSealed && t.Name.EndsWith("Tests", StringComparison.Ordinal))
            .OrderBy(t => t.Name)
            .ToArray();

        if (suites.Length == 0)
        {
            Console.WriteLine("No *Tests classes found.");
            Console.WriteLine("##NODETESTS-SUMMARY suites=0 cases=0 passed=0 failed=0 skipped=0");
            return 0;
        }

        int passed = 0, failed = 0, skippedTotal = 0, ran = 0, listed = 0;
        foreach (var suite in suites)
        {
            var voids = suite.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.ReturnType == typeof(void))
                .ToArray();
            var cases = voids.Where(m => m.GetParameters().Length == 0)
                .OrderBy(m => m.Name)
                .ToArray();
            // 🚨 Say what was NOT run. A test taking arguments (the ones needing the hub's
            // JsonSerializerOptions, which only the Tests layout area can supply) used to be
            // dropped in silence — so the suite reported all-green while a case that had caught a
            // real cross-hub read bug never executed at all. Under-reported coverage that looks
            // like full coverage is the same class of lie as a read that reports "not found".
            var skipped = voids.Where(m => m.GetParameters().Length > 0)
                .OrderBy(m => m.Name)
                .ToArray();
            if (cases.Length == 0 && skipped.Length == 0) continue;

            ran++;
            skippedTotal += skipped.Length;
            listed += cases.Length;
            Console.WriteLine($"== {suite.Name} ==");
            foreach (var s in skipped)
                Console.WriteLine($"  SKIP  {s.Name} (takes {s.GetParameters().Length} argument(s) — "
                    + "runs only in the Tests layout area, which supplies them)");
            foreach (var test in cases)
            {
                if (listOnly) { Console.WriteLine($"  {test.Name}"); continue; }
                try
                {
                    test.Invoke(null, null);
                    Console.WriteLine($"  PASS  {test.Name}");
                    passed++;
                }
                catch (TargetInvocationException ex)
                {
                    Console.WriteLine($"  FAIL  {test.Name}");
                    Console.WriteLine($"        {ex.InnerException?.Message}");
                    failed++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR {test.Name}: {ex.Message}");
                    failed++;
                }
            }
        }

        // Always emit the machine-readable tally, list mode included: the caller aggregates across
        // NodeTypes from it, and a harness that cannot state HOW MANY suites ran is exactly the
        // failure this script exists to make impossible.
        Console.WriteLine($"##NODETESTS-SUMMARY suites={ran} cases={listed} passed={passed} "
            + $"failed={failed} skipped={skippedTotal}");
        if (listOnly) return 0;
        return failed == 0 ? 0 : 1;
    }
}
"""


def _load_module(name: str, file: Path):
    spec = importlib.util.spec_from_file_location(name, file)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def load_compile_check():
    """`compile-check.py`, as a module — its assembly discovery, its per-type source resolution AND
    its compilation-unit shaping, rather than three forks of each.

    One place to fix when the core's output layout moves; one definition of "what a NodeType
    contains" (the two gates disagreeing on that is how this script came to compile a program no
    NodeType actually is, Reinsurance#113); and one definition of "what the mesh compiles" (the two
    disagreeing on THAT is MeshWeaver#4785 — see this file's header).

    🚨 THE COPY MUST BE THE SAME VINTAGE AS THE GATE'S, which is what the resolution order below is
    for, in this priority:

      1. `MW_PLATFORM_SCRIPTS` — the documented explicit override. A CI lane sets it to the single
         directory it fetched the canonical set into, so every consumer in that job reads the same
         bytes; a developer sets it to a core checkout's `.github/scripts` to work offline.
      2. the caller repo's `scripts/platform-script.py` — the satellites' loader, which fetches
         `compile-check.py` at the ref its own `node-repo-compile-check.yml` call declares. This is
         the route that makes the harness and the gate ONE vintage by construction, and it also
         hands `compile-check.py` this repo's root and allow-file the way the lane does.
      3. a sibling `compile-check.py` next to this file — core's own tree, where the canonical pair
         lives side by side, and a satellite's script cache once the loader has populated it.

    Nothing FALLS BACK to a guess: with none of the three present the run is refused, naming all
    three, because a harness that cannot reach the gate's shaping cannot claim to reproduce it."""
    override = os.environ.get("MW_PLATFORM_SCRIPTS")
    if override:
        candidate = Path(override).expanduser() / "compile-check.py"
        if not candidate.is_file():
            raise SystemExit(f"✗ MW_PLATFORM_SCRIPTS={override} has no compile-check.py")
        # The lane's own env, so the module's ROOT / ALLOW_FILE resolve to the repo under test —
        # `setdefault`, so an explicit value from the caller always wins.
        os.environ.setdefault("MW_REPO_ROOT", str(ROOT))
        os.environ.setdefault("MW_ALLOW_FILE", str(ROOT / "scripts" / "compile-check.allow"))
        return _load_module("compile_check", candidate)
    loader_path = ROOT / "scripts" / "platform-script.py"
    if loader_path.is_file():
        return _load_module("platform_script", loader_path).load("compile-check.py")
    sibling = SCRIPTS / "compile-check.py"
    if sibling.is_file():
        os.environ.setdefault("MW_REPO_ROOT", str(ROOT))
        return _load_module("compile_check", sibling)
    raise SystemExit(
        "✗ cannot reach compile-check.py, so this harness cannot reproduce what the gate compiles.\n"
        f"    tried  $MW_PLATFORM_SCRIPTS/compile-check.py  (unset)\n"
        f"    tried  {loader_path}\n"
        f"    tried  {sibling}\n\n"
        "  Point MW_PLATFORM_SCRIPTS at a core checkout's .github/scripts, or run this from a repo\n"
        "  that carries scripts/platform-script.py. A local re-derivation of the gate's shaping is\n"
        "  exactly what MeshWeaver#4785 is about — there is deliberately no fallback to one.")


def sibling_plugin_refs(refs: dict) -> tuple[dict, int]:
    """Overlay the sibling MeshWeaver.Plugins build onto the core ref set.

    🚨 Not every assembly a NodeType compiles against is built in the core checkout any more.
    `CommentsExtensions` / `CommentLayoutAreas` (namespace `MeshWeaver.Graph`) now ship from
    `MeshWeaver.Markdown.Collaboration`, which is built in MeshWeaver.Plugins — core's `src/*/bin`
    carries only a PRE-MOVE copy of that dll, so the two UWDeepfield NodeTypes that use them failed
    CS0103 locally while compiling perfectly in CI (whose `--refs` is the platform IMAGE, where the
    assembly is present). A stale local checkout reading as broken CONTENT is the exact confusion
    #113 is about, so close it here rather than leave it for the next reader.

    Applies compile-check's own preference rule — an assembly's OWN project output beats a copy in
    someone else's bin, newest mtime breaks a tie — so a plugins copy can only displace a core entry
    when it is the assembly's own build and core's is a stale bystander copy."""
    candidates = [
        ROOT.parent / "MeshWeaver.Plugins",
        ROOT.parent.parent / "MeshWeaver.Plugins",
        Path(os.path.expanduser("~/code/MeshWeaver.Plugins")),
    ]
    for base in candidates:
        dlls = []
        for cfg in ("Debug", "Release"):
            dlls += glob.glob(str(base / "src" / "*" / "bin" / cfg / "net10.0" / "*.dll"))
        if not dlls:
            continue
        def is_own(path) -> bool:
            return os.path.basename(Path(path).parents[3]) + ".dll" == os.path.basename(path)

        taken = 0
        for dll in dlls:
            name = os.path.basename(dll)
            if not is_own(dll):
                continue                       # only an assembly's OWN output may displace core's
            cur = refs.get(name)
            # own-ness first, mtime only as the tie-break — the same order discover_refs uses. A
            # bystander copy in someone's bin can be NEWER than the real build and must still lose,
            # which is precisely how the pre-move Markdown.Collaboration copy won locally.
            if cur is None or not is_own(cur) or os.path.getmtime(dll) > os.path.getmtime(cur):
                refs[name] = os.path.abspath(dll)
                taken += 1
        return refs, taken
    return refs, 0


def find_generator(search_roots) -> str | None:
    """The BusinessRules scope generator ships with the platform; a module whose scopes are IScope<,>
       interfaces needs it or nothing implements them. Loaded by its real file name — Roslyn resolves
       analyzers by assembly identity, and a renamed copy silently produces no output."""
    name = "MeshWeaver.BusinessRules.Generator.dll"
    for root in filter(None, search_roots):
        hits = [p for p in glob.glob(str(Path(root) / "**" / name), recursive=True)
                if "/obj/" not in p]
        if hits:
            return sorted(hits, key=os.path.getmtime, reverse=True)[0]
    return None


def discover_module_nodetypes(cc, module_dir: Path):
    """Every NodeType declared anywhere under the module, with its resolved source set.
       Returns [(node_path_str, frozenset_of_cs)] — the same resolution compile-check.py gates on."""
    out = []
    for f in sorted(module_dir.rglob("*.json")):
        try:
            node = json.loads(f.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError, UnicodeDecodeError):
            continue
        if not cc.is_nodetype(node):
            continue
        name = cc.node_dir(f).relative_to(ROOT).as_posix()
        out.append((name, cc.resolve_sources(f, node, ROOT)))
    return out


# 🚨 EVERY top-level declaration form C# has, because this regex is the duplicate-type detector's
# only eyes: a form it cannot parse is a collision it cannot SEE, and the harness then hands the
# reader the ~150 CS0101/CS0111 wall that `collisions_within` exists to replace — with nothing
# anywhere saying the detector missed it. Two bugs it carried until 2026-09-19 (PR #119 review):
#
#   * `readonly` was not an accepted modifier, so `public readonly record struct X(...)` matched
#     NOTHING. That is the form LossModelling/FrequencySeverityLossModel uses for `DiscretePoint`
#     and `DistributionStatistics` — two live types the detector was blind to.
#   * `record struct` / `record class` captured the SECOND keyword as the type name, so two
#     unrelated `record struct`s read as one duplicate type called `struct` — a FALSE refusal that
#     names a keyword, and the `partial` detector had the same bug, which then made a legitimately
#     split `partial record struct` look like a duplicate.
#
# The modifier list is EXACTLY the set that may precede a TOP-LEVEL type, and deliberately stops
# there: `private`/`protected` occur only on a NESTED type, where two same-named types under two
# different outer types are perfectly legal C# and refusing them would be a false refusal (measured
# 2026-09-19: adding them alone made `ApplicabilityRules`, a `private sealed class` nested in both
# Planning's and Ifrs17's ScopeApplicabilityResolver, look like a duplicate). `virtual`/`override`/
# `extern` cannot precede a type at all. The `record` alternatives are ordered longest-first so
# `record struct X` captures `X`, never `struct`.
DECLARATION = re.compile(
    r"^[ \t]*(?P<mods>(?:(?:public|internal|file|sealed|abstract|partial|static"
    r"|readonly|ref|unsafe)[ \t]+)*)"
    r"(?:record[ \t]+class|record[ \t]+struct|record|class|interface|struct|enum)[ \t]+"
    r"(?P<name>\w+)", re.M)

# A namespace declaration, either style. Its brace (this line's or the next line's) is not a type
# scope, so `declarations` discounts that depth; a file-scoped `namespace X;` opens no brace and the
# `;` clears the pending flag.
_NAMESPACE_DECL = re.compile(r"^[ \t]*namespace[ \t]+[\w.]+[ \t]*(\{|;)?[ \t]*$")


_LINE_COMMENT = re.compile(r"//[^\n]*")
_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.S)
_STRING = re.compile(r'@?"(?:""|\\.|[^"\\\n])*"|\'(?:\\.|[^\'\\\n])*\'')


def _scannable(text: str) -> str:
    """`text` with string literals and comments blanked, keeping every NEWLINE and every brace that
    is really code.

    Strings go FIRST: a `"http://x"` would otherwise lose its tail to the line-comment pattern, and
    a `"{"` in a text table would then be counted as a brace and shift the depth of everything after
    it. Comments go second, because a `//` comment may contain an unbalanced quote. Neither pattern
    has to be a C# lexer — it has to keep the BRACE COUNT right, which is all `declarations` reads."""
    text = _STRING.sub(lambda m: '"' + " " * max(0, len(m.group(0)) - 2) + '"', text)
    text = _BLOCK_COMMENT.sub(lambda m: re.sub(r"[^\n]", " ", m.group(0)), text)
    return _LINE_COMMENT.sub("", text)


# 🚨 WHICH WAY THIS FAILS, on purpose. `_STRING` does not understand a C# 11 RAW string (`"""…"""`),
# so braces inside one can still shift the depth — and then declarations AFTER it sit at a non-zero
# depth and are NOT recorded. That direction is a MISSED duplicate (Roslyn then emits the CS0101 the
# detector meant to pre-empt, which is noisy but true), never a FALSE REFUSAL of a set the gate
# compiles, which is the failure that blocks work. Verified as a subset over every NodeType with
# resolved sources in the three repos: the new detector refuses 0 where the old refused 10, and
# refuses nothing the old one did not.


def declarations(text: str) -> set[tuple[frozenset[str], str]]:
    """Every TOP-LEVEL type declared in one file, as (its modifiers, its name). Deduped per file.

    🚨 TOP-LEVEL, BY BRACE DEPTH — indentation is not a scope (MeshWeaver#4785, Copilot review of
    #4916). The regex allows arbitrary leading whitespace, so a NESTED `public sealed class FieldRow`
    used by two different outer types in two files read as ONE top-level type declared twice, and the
    harness then REFUSED a set the gate compiles: measured over every NodeType with resolved sources
    in MeshWeaver.Reinsurance, MeshWeaver.Crm and MeshWeaver.Plugins, **10 of 243** were refused for
    exactly that, all in Plugins — `Edu/CourseCatalog`, `Edu/CourseInvite`, `Governance/Activity`,
    `Hosting/{Backup,Deployment,FleetConsole,InstanceAction,InstanceRequest,PlatformBuildInbox}`,
    `Store/Maintenance` — so ten NodeTypes' suites could not be run locally at all. That is the
    mirror image of the CS0101 wall this detector exists to replace, and it is worse, because a
    refusal looks deliberate. One of the names was `with`, a keyword the regex captured off an
    expression.

    A block `namespace X { }` opens a brace that is not a type scope, so its depth is discounted; a
    file-scoped `namespace X;` opens nothing and needs no special case. Modifiers stay as they were —
    `private`/`protected` are still absent from the pattern, which is now belt AND braces rather than
    the only guard."""
    out: set[tuple[frozenset[str], str]] = set()
    depth = 0                       # every open brace
    namespace_braces: list[int] = []  # the depths at which a NAMESPACE brace was opened
    pending_namespace = False       # a `namespace X` whose `{` has not been seen yet
    for line in _scannable(text).split("\n"):
        match = DECLARATION.match(line)
        if match and depth - len(namespace_braces) == 0:
            out.add((frozenset(match.group("mods").split()), match.group("name")))
        # 🚨 BOTH brace styles. `namespace X {` and `namespace X` + `{` on the NEXT line are both
        # common, and keying on the same-line brace alone is not a smaller guard, it is a BLIND SPOT:
        # the namespace's brace would count as a type scope, every declaration in the file would sit
        # at depth 1, and the detector would record NOTHING — refusing nothing, ever, in that file.
        if _NAMESPACE_DECL.match(line):
            pending_namespace = True
        for char in line:
            if char == "{":
                if pending_namespace:
                    namespace_braces.append(depth)
                    pending_namespace = False
                depth += 1
            elif char == "}":
                depth -= 1
                while namespace_braces and depth <= namespace_braces[-1]:
                    namespace_braces.pop()
            elif char == ";" and pending_namespace:
                pending_namespace = False    # file-scoped `namespace X;` — opens no brace at all
    return out


def collisions_within(cs_files, module_dir: Path):
    """Duplicate detection scoped to ONE NodeType's resolved sources.

    Across NodeTypes duplication is LEGITIMATE and expected — the mesh compiles each type on its
    own, so each sees exactly one definition. Inside a SINGLE type's declared sources it is not:
    the mesh concatenates those, so a doubly-declared symbol breaks the type on the mesh too. This
    keeps the loud naming introduced for #113 (option 3) — name the symbols, never let Roslyn emit
    ~150 CS0101/CS0111 lines that read like broken content — and points it at the only scope where
    a collision is now a real defect.

    Returns (sources, problems): `sources` is EVERY path the set declares, and `problems` a list of
    human-readable refusals.

    🚨 `sources` USED TO COLLAPSE byte-identical copies, and that was a false-pass divergence
    (MeshWeaver#4785, Copilot review of #4916). The gate hands `build_unit` the resolved frozenset of
    PATHS; two DISTINCT paths with identical bytes are therefore a duplicate declaration in the unit
    the gate and the mesh compile — CS0101 — and collapsing them here compiled once and ran green.
    Measured over every NodeType with resolved sources in the three repos, 0 of 243 exercise it, so
    nothing in the fleet changes; it is removed anyway, because "this harness compiles what the gate
    compiles" cannot hold with an exception in it. A byte-identical copy is now refused and NAMED
    like every other collision, which is the same verdict the gate reaches and a legible one.

    🚨 AND AN UNREADABLE SOURCE FAILS CLOSED. An `OSError` on a declared source used to be swallowed
    and the file dropped from the set, so if another file supplied the tests the run went green over
    source that was never compiled — while the gate's `read_source` raises on the same path. It is a
    refusal naming the path."""
    paths = sorted((Path(p) for p in cs_files), key=str)
    problems: list[str] = []
    sources = sorted(str(p) for p in paths)

    def where(p: Path) -> str:
        try:
            return str(p.relative_to(ROOT))
        except ValueError:
            return str(p)

    by_content: dict[str, list[Path]] = {}
    unreadable: list[Path] = []
    for path in paths:
        try:
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
        except OSError:
            unreadable.append(path)
            continue
        by_content.setdefault(digest, []).append(path)
    if unreadable:
        lines = [f"{len(unreadable)} declared source(s) could not be READ, so this NodeType's unit "
                 f"cannot be assembled:"]
        lines += [f"    {where(p)}" for p in unreadable[:10]]
        lines.append("  The gate reads the same paths and raises on this one. Running the set without")
        lines.append("  it would compile a program the mesh never builds, and pass.")
        problems.append("\n".join(lines))

    # Two DISTINCT paths, identical bytes, both declared by this one type: the unit carries every
    # declaration twice, which is CS0101 on the gate and on the mesh.
    duplicated = sorted((ps for ps in by_content.values() if len(ps) > 1), key=lambda ps: str(ps[0]))
    if duplicated:
        lines = [f"{len(duplicated)} file(s) are declared TWICE, byte for byte, inside this ONE "
                 f"NodeType's declared sources:"]
        for group in duplicated[:10]:
            for p in group:
                lines.append(f"      {where(p)}")
            lines.append("      ↑ identical contents")
        lines.append("  The mesh concatenates a type's declared sources by PATH, so every type in")
        lines.append("  them is declared twice. Narrow the `sources` entry that pulls the copy in.")
        problems.append("\n".join(lines))

    # Same file NAME, different bytes, both declared by this one type: the compilation would carry
    # two different files claiming the same role and this harness cannot know which was meant.
    by_name: dict[str, set[str]] = {}
    for digest, ps in by_content.items():
        for p in ps:
            by_name.setdefault(p.name, set()).add(digest)
    ambiguous = sorted(n for n, hs in by_name.items() if len(hs) > 1)
    if ambiguous:
        lines = [f"{len(ambiguous)} file name(s) resolve twice with DIFFERENT contents "
                 f"inside this ONE NodeType's declared sources:"]
        for name in ambiguous[:10]:
            lines.append(f"    {name}")
            for p in sorted((q for q in paths if q.name == name), key=str)[:6]:
                lines.append(f"      {where(p)}")
        lines.append("  The mesh concatenates a type's declared sources too, so this breaks on the")
        lines.append("  mesh as well. Drop one copy, or narrow the `sources` entry that pulls it in.")
        problems.append("\n".join(lines))

    # The harder half: the SAME TYPE declared in two DIFFERENTLY-NAMED files that this ONE NodeType
    # declares. A byte-identical pair is already refused above; this is the differing-files case.
    declared_in: dict[str, set[str]] = {}
    partials: set[str] = set()
    for path in (Path(x) for x in sources):
        try:
            text = path.read_text(encoding="utf-8", errors="ignore")
        except OSError:
            continue           # already refused above, by path, with its own message
        for mods, name in declarations(text):
            declared_in.setdefault(name, set()).add(where(path))
            if "partial" in mods:
                partials.add(name)
    doubled = sorted(n for n, fs in declared_in.items() if len(fs) > 1 and n not in partials)
    if doubled:
        lines = [f"{len(doubled)} type(s) are declared in TWO files this ONE NodeType pulls in:"]
        for name in doubled[:12]:
            lines.append(f"    {name}  <- {', '.join(sorted(declared_in[name]))}")
        if len(doubled) > 12:
            lines.append(f"    … and {len(doubled) - 12} more")
        lines.append("  The mesh compiles this type's declared sources as ONE unit, so it hits the")
        lines.append("  same CS0101. Hoist the shared type to a partition-level Source/ that only")
        lines.append("  ONE of the declared `sources` entries covers.")
        problems.append("\n".join(lines))

    return sources, problems


def unit_for(cc, sources):
    """The ONE compilation unit for a NodeType's source set, and its line -> authored-origin map.

    🚨 THE GATE'S OWN FUNCTION, called — never reproduced. The mesh concatenates a type's sources in
    node-path order and MOVES every `using` to the top of the single unit it compiles; `build_unit`
    IS that shaping, down to the `utf-8-sig` read, the node-path ordering, the culture-sensitive
    prefix test and the dedup against the implicit prelude. This harness therefore compiles the
    identical text the gate compiles for the same set, for as long as it goes on calling this
    function — which is a stronger statement than "the two implementations agree", and it is the
    statement the three vendored copies made in a comment and then lost (MeshWeaver#4785).

    The configuration LAMBDA is deliberately not appended (`lambda_src=None`): a source SET can be
    shared by several NodeTypes with different lambdas — the harness compiles the set once and
    attributes the result to all of them — and the gate already compiles every type's lambda with
    its own sources. Nothing here runs a lambda, so its absence cannot hide a test failure.

    🚨 "IDENTICAL TEXT" IS A CLAIM ABOUT THIS FUNCTION, NOT ABOUT THE SET IT IS GIVEN. `run_set`
    hands it `collisions_within`'s output, which collapses byte-identical copies, while the gate
    hands `build_unit` the resolved frozenset of PATHS. Two DISTINCT paths with identical content
    therefore compile once here and twice there — CS0101 on the gate and on the mesh, silent here.
    Measured 2026-09-19 over every NodeType with resolved sources in MeshWeaver.Reinsurance,
    MeshWeaver.Crm and MeshWeaver.Plugins: **0 of 243** exercise it, so it is latent, and left as it
    is rather than changed with no subject to measure the change against. If you ever see the gate
    report a CS0101 this harness does not, that is where to look first."""
    text, origins, _imports = cc.build_unit(sources, None)
    return text, origins


def compile_items(work: Path, cc) -> str:
    """The csproj's `<Compile>` items: the gate's ONE unit, plus this harness's entry point.

    🚨 EXACTLY ONE authored item, and that is the shape change #4785 carries. A csproj with one item
    per source file is a DIFFERENT COMPILATION from the one the mesh runs — `#nullable`,
    `#pragma warning`, `#define` and the `using` scope are all per-FILE in C#, and the mesh has
    exactly one file (core #4711). `NodeTestRunner.cs` stays its own file because it is not authored
    node content: it carries its own `using` lines and the mesh compiles nothing like it.

    Factored out so `--self-test` can assert the shape. Left inline it was a string built where
    nothing could read it, which is how a per-file list survived a model change in three repos."""
    return (f'    <Compile Include="{work / cc.UNIT_FILE}" />\n'
            f'    <Compile Include="{work / "NodeTestRunner.cs"}" />')


def _attribute(origins, line_no: int) -> str:
    """`  [<authored file>:<line>]`, or "" when the unit line is one this file generated."""
    index = line_no - 1
    if not (0 <= index < len(origins)):
        return ""
    origin = origins[index]
    if origin is None:
        return ""
    where = origin[0] if origin[0].startswith("<") else f"{origin[0]}.cs"
    return f"  [{where}:{origin[1]}]"


# 🚨 THE PREFIX IS THE CLASSIFICATION (MeshWeaver#5080). A set whose ONLY "diagnostic" is a
# non-zero exit code did not fail to build — Roslyn names every refusal it makes, so nothing can
# fail to compile silently. It BUILT and the produced process could not start. Reporting that as
# `BUILD FAILED` accuses content that `compile-check.py` reads as clean, which is the failure mode
# `short_reference_set_refusal` was written to prevent one layer up.
EXEC_FAILED = "the sources COMPILED and the produced process could not run — "

# 128 + N, as a shell reports a signalled child. Named, because `exit 134` is not a number anybody
# should have to look up while reading a gate log.
_SIGNALS = {132: "SIGILL", 133: "SIGTRAP", 134: "SIGABRT", 135: "SIGBUS", 136: "SIGFPE",
            137: "SIGKILL", 139: "SIGSEGV", 141: "SIGPIPE", 143: "SIGTERM"}


def host_rid() -> str:
    """This host's RID, in the spelling .NET uses for it."""
    machine = platform.machine().lower()
    arch = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(machine, machine)
    os_part = ("linux" if sys.platform.startswith("linux")
               else "osx" if sys.platform == "darwin"
               else "win" if sys.platform.startswith("win")
               else sys.platform)
    return f"{os_part}-{arch}"


def execution_host_refusal(layout: str, host: str) -> str | None:
    """Why the suites cannot be EXECUTED on this host against this reference set, or None.

    🚨 THIS HARNESS BOTH COMPILES AND RUNS, AND ONLY THE FIRST HALF IS HOST-AGNOSTIC
    (MeshWeaver#5080). An `image`-shaped set is extracted from the platform CONTAINER, so its
    assemblies are `linux-*`. Compiling against them is fine — `compile-check.py` does exactly that
    and reads the same tree green. LOADING them into a `dotnet run` process on a host that is not
    Linux is not: every set aborts on SIGABRT before the runner prints a line, and because no
    compiler diagnostic exists to quote, each one was reported as `BUILD FAILED`. Measured
    2026-09-19 on darwin-arm64: 8 of 8 NodeTypes "failed to BUILD" while `compile-check.py` read
    102 of 102 clean.

    So the answer is known before the first compile, and it is said once instead of being
    mis-attributed 102 times. What is NOT claimed here: that a `linux-arm64` host can load a
    `linux-x64` extraction. That combination is unmeasured, so it is not refused up front — it
    falls to the per-set classification, which needs no assumption about RIDs at all.
    """
    if layout != "image" or host.startswith("linux"):
        return None
    return (f"✗ this reference set is an IMAGE extraction — its assemblies are container-built for "
            f"linux, and this host is {host}. The suites cannot be EXECUTED here: the compile "
            f"succeeds and the produced process aborts on load, which this harness used to report "
            f"as BUILD FAILED against content that is clean (MeshWeaver#5080).\n"
            f"  What DOES answer here:\n"
            f"    • compilation — `compile-check.py` over the same tree and the same set; it only "
            f"compiles, so the host never matters\n"
            f"    • execution — run this harness inside the tester image, the way CI does, or point "
            f"`--refs` at a sibling MeshWeaver checkout BUILT ON THIS HOST\n"
            f"  Refusing rather than reporting {host} as a content failure.")


def exit_code_reading(code: int) -> str:
    """`dotnet run`'s exit status, said in words."""
    if code in _SIGNALS:
        return (f"exit {code} = 128 + {_SIGNALS[code]}, so the runtime took the process down before "
                f"the runner reached its first line")
    if code < 0:
        return f"killed by signal {-code} before the runner reached its first line"
    return f"exit {code}, with no compiler diagnostic to attribute it to"


def run_set(work: Path, sources, refs_xml, analyzers, cc, ai_available, list_only, restored):
    """Compile and run ONE NodeType's source set.

    Returns (tally|None, build_errors, raw_tail, unverifiable). `unverifiable` is True when the set
    failed and EVERY diagnostic is attributable to the ABSENT Microsoft.Extensions.AI assemblies the
    mesh supplies at runtime — the same classification `compile-check.py` makes, because reporting a
    missing mesh-supplied assembly as broken content is a confident wrong answer that sends the
    reader off to fix source that was never broken. It is REPORTED in the summary, never dropped: a
    set nobody could run is not a set that passed."""
    # The mesh HOISTS every `using` across a type's sources into one compilation unit; a per-file
    # compile does not, so a file relying on a sibling's `using` would fail here and pass on the
    # mesh — and a hoist reproduced as a COPIED prelude cannot carry an alias or a `using static`
    # across files at all. compile-check.py builds the unit the mesh builds; call it, so "compiles
    # under the gate" and "compiles under the test harness" cannot diverge.
    unit, origins = unit_for(cc, sources)
    (work / cc.UNIT_FILE).write_text(unit, encoding="utf-8")

    compiles = compile_items(work, cc)
    (work / "NodeTests.csproj").write_text(
        CSPROJ.format(compiles=compiles, references=refs_xml, analyzers=analyzers), encoding="utf-8")

    cmd = ["dotnet", "run", "-c", "Debug", "--project", str(work / "NodeTests.csproj")]
    if restored:
        cmd.append("--no-restore")
    cmd += ["--"] + (["--list"] if list_only else [])
    proc = subprocess.run(cmd, cwd=work, capture_output=True, text=True)
    out = proc.stdout

    if BEGIN not in out:
        # It never got as far as running: a build failure (or a load failure with no output).
        # Surface the compiler diagnostics — deduped — rather than the whole MSBuild transcript.
        #
        # 🚨 Each one carries the AUTHORED file and line it came from. MSBuild reports against
        # Combined.cs, whose line numbers belong to the generated unit; a diagnostic printed with
        # those is a defect report nobody can act on, which is the other half of #4711. `origins`
        # maps them back. The dedup key stays the code + message, so a diagnostic is still reported
        # once however many sites it has, with the FIRST site's attribution.
        errors, seen = [], set()
        for line in (proc.stdout + proc.stderr).splitlines():
            m = re.search(r"error (CS\d+|NU\w*\d*|NETSDK\w*\d*|MSB\w*\d*): (.*?)(?: \[|$)", line)
            if m:
                e = f"{m.group(1)}: {m.group(2).strip()}"
                if e in seen:
                    continue
                seen.add(e)
                loc = re.search(r"\((\d+),\d+\): error (?:CS|NU|NETSDK|MSB)", line)
                errors.append(e + (_attribute(origins, int(loc.group(1))) if loc else ""))
        if not errors:
            errors = [EXEC_FAILED + exit_code_reading(proc.returncode)]
        unverifiable = (not ai_available) and bool(errors) and all(cc._is_ai_error(e) for e in errors)
        return None, errors, proc.stdout[-2000:], unverifiable

    body = out.split(BEGIN, 1)[1]
    # 🚨 THE SUMMARY MARKER IS REQUIRED, NOT OPTIONAL (MeshWeaver#4785, Copilot review of #4916).
    # `BEGIN` is printed BEFORE reflection starts, so a `ReflectionTypeLoadException` out of
    # `Assembly.GetTypes()` — or anything else thrown between the two markers — leaves `BEGIN`
    # present and `##NODETESTS-SUMMARY` absent. Defaulting the tally to zeros then returned a
    # non-None tally, the set counted as EXECUTED, and the command exited 0 having run no test at
    # all: a runtime failure wearing full coverage's clothes. The runner emits the summary on every
    # path it can reach, list mode included, so its absence means the process died mid-flight.
    if SUMMARY not in body:
        tail = "\n".join(l for l in body.splitlines() if l.strip())
        return None, ["the runner printed its BEGIN marker and then died before its summary — it "
                      "BUILT and failed at run time (a type load or a static initializer), so no "
                      "test in this set executed"], tail[-2000:], False
    tally = {"suites": 0, "cases": 0, "passed": 0, "failed": 0, "skipped": 0}
    lines = []
    for line in body.splitlines():
        if line.startswith(SUMMARY):
            for kv in line[len(SUMMARY):].split():
                k, _, v = kv.partition("=")
                if k in tally and v.isdigit():
                    tally[k] = int(v)
            continue
        if line.strip():
            lines.append(line)
    return tally, [], "\n".join(lines), False


def hoist_self_test(cc, case) -> None:
    """The invariant this harness exists to hold: it compiles what the GATE compiles.

    🚨 THIS IS THE CONTROL THAT WAS MISSING (MeshWeaver#4785). The old `--self-test` exercised only
    the collision detector, so it stayed GREEN while the script's compile path was dead — measured
    2026-09-19 on MeshWeaver.Reinsurance's copy: `--self-test` printed `✓ self-test green` in the
    same checkout where a real run died with `AttributeError: module 'compile_check' has no attribute
    'usings_union'` before one test executed. A verification step that cannot fail is not a
    verification step; these cases fail if the harness stops calling `compile-check.py`'s shaping,
    if that shaping stops hoisting, or if the canonical renames the function under it.

    Every case is TEXTUAL and needs no reference set, so it runs anywhere in seconds. The compiled
    proof is a real run against a real NodeType (see the module docstring's invocations) — this pins
    the property that made the compiled proof come out right, so a regression is caught by the
    self-test rather than by whoever next reads a phantom CS0246.

    Each pair is a POSITIVE fact plus the NEGATIVE control that makes it sensitive to its input:
    a directive is in the import block *because the sibling wrote one*, and absent when nobody did.
    """
    with tempfile.TemporaryDirectory(prefix="node-tests-hoist-") as tmp:
        d = Path(tmp)

        def unit(declarer: str, user: str) -> tuple[str, str, str]:
            """(whole unit, import block, code body) for a two-file set — `A.cs` carries the
               directive and declares nothing interesting, `B.cs` uses it and imports nothing."""
            (d / "A.cs").write_text(declarer, encoding="utf-8")
            (d / "B.cs").write_text(user, encoding="utf-8")
            text, _origins = unit_for(cc, [str(d / "A.cs"), str(d / "B.cs")])
            marker = "// User-defined types"
            # 🚨 The split has to HAPPEN, or every "…is not in the body" case below passes against an
            # empty body and every "…is in the head" case against the whole file — the controls would
            # go vacuous without going red, which is the exact defect this whole file is about.
            # `build_unit` omits the marker when the code half is blank, so assert it is there.
            case("the unit separates its import block from the code (the split below is real)",
                 marker in text, text[:200])
            head, _, body = text.partition(marker)
            return text, head, body

        # 1. The provenance of the text itself. A future fork of the shaping would still produce
        #    something that hoists; it would not produce the GATE's own generated header. This is
        #    the assertion that "the harness and the gate cannot diverge" rests on.
        text, head, body = unit(
            "using System.Text.Json;\npublic sealed class Holder { }\n",
            "public static class Probe { public static string Read(JsonElement e) => e.ToString(); }\n")
        case("the unit is built by compile-check.py, not by a local re-derivation",
             "Generated by compile-check.py" in text and "ONE compilation unit" in text,
             f"header: {text.splitlines()[0][:70] if text else '(empty)'}")
        case("exactly ONE authored <Compile> item, and it is the gate's unit file",
             compile_items(d, cc).count("<Compile ") == 2
             and compile_items(d, cc).count(cc.UNIT_FILE) == 1
             and "NodeTestRunner.cs" in compile_items(d, cc),
             compile_items(d, cc).replace("\n", " | "))

        # 2. The caller's comment's own case: a file relying on a SIBLING's plain `using`.
        case("a sibling's plain `using` is hoisted into the import block",
             "using System.Text.Json;" in head, head[-200:])
        case("…and MOVED, not copied — it is gone from the code body",
             "using System.Text.Json;" not in body, body[:200])
        case("…and the file that never imported it is in the same unit",
             "JsonElement" in body, body[:200])

        # 3. The negative control for 2: with no sibling directive, nothing is hoisted. Without this
        #    the case above would also pass on a prelude that imports System.Text.Json
        #    unconditionally, which is not a hoist at all.
        _, head_none, _ = unit(
            "public sealed class Holder { }\n",
            "public static class Probe { public static int N => 1; }\n")
        case("CONTROL: with no sibling directive, nothing is hoisted",
             "using System.Text.Json;" not in head_none, head_none[-200:])

        # 4. The ALIAS — the divergence the one-unit model CLOSES and the old global-usings union
        #    could not. A copied prelude leaves the alias in its own file too, and a duplicate
        #    `using X = Y;` is CS1537, so `usings_union`'s `_directive_parts` dropped every alias:
        #    the sibling then failed CS0246 on a name the mesh resolves. Restoring that helper to
        #    make this script start would have restored this hole, which is why it was not restored.
        _, head_alias, body_alias = unit(
            "using J = System.Text.Json.JsonElement;\npublic sealed class Holder { }\n",
            "public static class Probe { public static string Read(J e) => e.ToString(); }\n")
        case("an ALIAS `using` is hoisted (the old union dropped these)",
             "using J = System.Text.Json.JsonElement;" in head_alias, head_alias[-200:])
        case("…and MOVED, so it cannot be the CS1537 duplicate the old copy was",
             "using J = System.Text.Json.JsonElement;" not in body_alias, body_alias[:200])
        _, head_no_alias, _ = unit(
            "public sealed class Holder { }\n",
            "public static class Probe { public static int N => 1; }\n")
        case("CONTROL: with no alias written, none is hoisted",
             "using J =" not in head_no_alias, head_no_alias[-200:])

        # 5. `using static`, the third form — hoisted by the mesh, and the form whose namespace the
        #    pre-#1510 parser truncated. Same two halves.
        _, head_static, body_static = unit(
            "using static System.Math;\npublic sealed class Holder { }\n",
            "public static class Probe { public static double N => Sqrt(4.0); }\n")
        case("a `using static` is hoisted",
             "using static System.Math;" in head_static, head_static[-200:])
        case("…and MOVED out of the file that wrote it",
             "using static System.Math;" not in body_static, body_static[:200])

        # 6. `using var` is a STATEMENT, never a directive. Hoisting it out of a method body would
        #    silently delete a line of the program — the reason the gate's extractor tests for it,
        #    and a property this harness inherits only by calling that extractor.
        _, head_var, body_var = unit(
            "public sealed class Holder { }\n",
            "public static class Probe { public static void Go() { using var d = "
            "System.IO.File.OpenRead(\"x\"); } }\n")
        case("`using var` is a statement and stays in the body",
             "using var d" in body_var and "using var d" not in head_var, body_var[:200])


def self_test() -> int:
    """Pin the collision diagnostics (#113 option 3, PR #119) AND the hoist fidelity (#4785).

       The collision diagnostics fire on a shape the tree does not currently contain, so nothing
       else would notice if they stopped naming the symbol and went back to letting Roslyn emit 150
       CS0101 lines. Each case asserts a POSITIVE fact — the refusal happened AND the offending name
       appears in the text.

       `hoist_self_test` is the other half, and the half whose absence let this script die unnoticed
       in three repositories: it asserts that the unit this harness compiles is the unit the GATE
       compiles, which is the whole claim the script makes about itself."""
    failures = []

    def case(name, ok, detail=""):
        print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  — {detail}" if not ok and detail else ""))
        if not ok:
            failures.append(name)

    # 🚨 FIRST, because it is the case that was not covered. Loading compile-check.py is itself part
    # of the assertion: the removal that broke this script would be caught here by the load or by the
    # missing `build_unit`, either way RED and named, instead of by a developer's first real run.
    #
    # 🚨 THE LIST IS DERIVED, NOT HAND-KEPT (Copilot review of #4916). A hand-written four-name list
    # asserted `build_unit`, `resolve_sources`, `discover_refs` and `UNIT_FILE` while the file also
    # called `is_nodetype`, `node_dir`, `short_reference_set_refusal`, `discover_ai_refs`,
    # `discover_module_refs`, `_is_ai_error`, `MODULE_REFS_NOT_IN_IMAGE` and `IMPLICIT_USINGS` — any
    # of which could be renamed with `--self-test` still green, deferring the break to whoever next
    # ran a real module. Which is `usings_union` again, in the very check meant to prevent it. So the
    # names come from THIS FILE'S OWN SOURCE: every `cc.<name>` it references. A newly-used symbol is
    # asserted the moment it is used, and the list cannot go stale.
    print("── the harness compiles what the gate compiles (#4785) ──")
    cc = load_compile_check()
    used = sorted({node.attr for node in ast.walk(ast.parse(Path(__file__).read_text(encoding="utf-8")))
                   if isinstance(node, ast.Attribute)
                   and isinstance(node.value, ast.Name) and node.value.id == "cc"})
    case("this file's own `cc.<name>` references could be collected (the list is derived, not kept)",
         len(used) >= 8, f"{len(used)} found: {used}")
    for attr in used:
        case(f"compile-check.py still offers `{attr}`", hasattr(cc, attr),
             "the canonical moved — follow it, do not re-derive it locally")
    if not failures:
        hoist_self_test(cc, case)
    print("\n── colliding sources inside ONE NodeType (Reinsurance#113) ──")

    with tempfile.TemporaryDirectory(prefix="node-tests-selftest-") as tmp:
        d = Path(tmp)
        (d / "a").mkdir()
        (d / "b").mkdir()
        same = "public record Shared(string X);\n"
        (d / "a" / "Shared.cs").write_text(same, encoding="utf-8")
        (d / "b" / "Shared.cs").write_text(same, encoding="utf-8")
        srcs, problems = collisions_within(
            [str(d / "a" / "Shared.cs"), str(d / "b" / "Shared.cs")], d)
        # 🚨 THIS CASE IS INVERTED FROM WHAT IT ASSERTED BEFORE #4785. It used to demand that
        # byte-identical copies COLLAPSE to one and pass — which was the divergence: the gate compiles
        # both paths and hits CS0101. The set the harness compiles is now the set the gate compiles,
        # and the duplicate is refused and NAMED.
        case("byte-identical copies are KEPT (the gate compiles both) and REFUSED, naming both paths",
             len(srcs) == 2 and len(problems) >= 1
             and any("byte for byte" in p and "a/Shared.cs" in p.replace(os.sep, "/")
                     and "b/Shared.cs" in p.replace(os.sep, "/") for p in problems),
             f"{len(srcs)} source(s), {len(problems)} problem(s): {problems[0][:120] if problems else ''}")

        (d / "b" / "Shared.cs").write_text("public record Shared(int Y);\n", encoding="utf-8")
        _, problems = collisions_within(
            [str(d / "a" / "Shared.cs"), str(d / "b" / "Shared.cs")], d)
        case("same NAME, different bytes is refused AND names the file",
             len(problems) >= 1 and any("Shared.cs" in p for p in problems),
             f"{len(problems)} problem(s)")

        (d / "b" / "Other.cs").write_text("public record Shared(int Y);\n", encoding="utf-8")
        _, problems = collisions_within(
            [str(d / "a" / "Shared.cs"), str(d / "b" / "Other.cs")], d)
        case("same TYPE in two differently-named files is refused AND names the type",
             len(problems) == 1 and "Shared" in problems[0] and "Other.cs" in problems[0],
             f"{len(problems)} problem(s)")

        (d / "a" / "P1.cs").write_text("public partial record Split { public int A; }\n", encoding="utf-8")
        (d / "b" / "P2.cs").write_text("public partial record Split { public int B; }\n", encoding="utf-8")
        _, problems = collisions_within([str(d / "a" / "P1.cs"), str(d / "b" / "P2.cs")], d)
        case("a PARTIAL type split across two files is NOT refused",
             not problems, f"{len(problems)} problem(s)")

        # 🚨 EVERY top-level declaration form, because the detector reads the SOURCE TEXT and a form
        # it cannot parse is a collision it cannot see — the harness then hands the reader the ~150
        # CS0101 wall this check exists to replace, and nothing anywhere says the detector missed it.
        # `readonly` was not an accepted modifier (so `public readonly record struct X` matched
        # NOTHING — the form LossModelling/FrequencySeverityLossModel uses for DiscretePoint and
        # DistributionStatistics), and `record struct` / `record class` captured the SECOND keyword
        # as the type name ("struct"/"class"), so two unrelated `record struct`s read as one
        # duplicate type called `struct`. Both directions matter: a missed form is a silent wall, an
        # over-eager one is a false refusal that blocks a package for nothing.
        for label, decl in [
            ("readonly record struct", "public readonly record struct Dup(double P, double V);"),
            ("record struct", "public record struct Dup(int A);"),
            ("record class", "public record class Dup(int A);"),
            ("plain record", "public record Dup(int A);"),
            ("sealed class", "public sealed class Dup { }"),
            ("readonly struct", "public readonly ref struct Dup { }"),
            ("file-local class", "file class Dup { }"),
            ("enum", "public enum Dup { A }"),
            ("interface", "public interface Dup { }"),
        ]:
            (d / "a" / "D1.cs").write_text(decl + "\n", encoding="utf-8")
            (d / "b" / "D2.cs").write_text(decl.replace("Dup", "Dup") + " // twin\n", encoding="utf-8")
            _, problems = collisions_within([str(d / "a" / "D1.cs"), str(d / "b" / "D2.cs")], d)
            case(f"a duplicated `{label}` is refused AND named",
                 len(problems) == 1 and "Dup" in problems[0],
                 f"{len(problems)} problem(s): {problems[0][:90] if problems else ''}")
            case(f"a duplicated `{label}` is never reported under a KEYWORD name",
                 not any(f" struct  <-" in p or f" class  <-" in p or f" record  <-" in p
                         for p in problems),
                 f"{problems[0][:90] if problems else ''}")

        # And a `partial record struct` split across two files must still be tolerated — the partial
        # detector had the identical keyword-capture bug, so it recorded `struct` as the partial name
        # and the real type was refused as a duplicate.
        (d / "a" / "D1.cs").write_text(
            "public partial record struct Split { public int A; }\n", encoding="utf-8")
        (d / "b" / "D2.cs").write_text(
            "public partial record struct Split { public int B; }\n", encoding="utf-8")
        _, problems = collisions_within([str(d / "a" / "D1.cs"), str(d / "b" / "D2.cs")], d)
        case("a PARTIAL `record struct` split across two files is NOT refused",
             not problems, f"{len(problems)} problem(s)")

        # 🚨 INDENTATION IS NOT A SCOPE (#4785, Copilot review of #4916). A NESTED type of the same
        # name under two DIFFERENT outer types is legal C#, and the indentation-blind regex refused
        # 10 of 243 real NodeTypes for it — all in MeshWeaver.Plugins, whose own copy could not
        # surface it because it merged a whole package into one compile. Both directions matter: the
        # nested pair must pass, and a genuinely top-level pair beside it must still be refused.
        (d / "a" / "N1.cs").write_text(
            "public sealed class OuterOne\n{\n    public sealed class FieldRow { public int A; }\n}\n",
            encoding="utf-8")
        (d / "b" / "N2.cs").write_text(
            "public sealed class OuterTwo\n{\n    public sealed class FieldRow { public int B; }\n}\n",
            encoding="utf-8")
        _, problems = collisions_within([str(d / "a" / "N1.cs"), str(d / "b" / "N2.cs")], d)
        case("a NESTED type of the same name under two DIFFERENT outer types is NOT refused",
             not problems, f"{len(problems)} problem(s): {problems[0][:120] if problems else ''}")
        case("…and the nested name is not recorded as a declaration at all",
             "FieldRow" not in {n for _m, n in declarations((d / "a" / "N1.cs").read_text())},
             str(sorted(n for _m, n in declarations((d / "a" / "N1.cs").read_text()))))
        (d / "b" / "N3.cs").write_text("public sealed class OuterOne { }\n", encoding="utf-8")
        _, problems = collisions_within([str(d / "a" / "N1.cs"), str(d / "b" / "N3.cs")], d)
        case("CONTROL: the OUTER type duplicated IS still refused, and named",
             len(problems) == 1 and "OuterOne" in problems[0],
             f"{len(problems)} problem(s): {problems[0][:120] if problems else ''}")

        # A BLOCK namespace opens a brace that is not a type scope, and so does its next-line form —
        # keying on the same-line brace alone would put every declaration in such a file at depth 1
        # and the detector would record NOTHING, refusing nothing ever, silently.
        for label, text in (
            ("same-line brace", "namespace Nm {\npublic sealed class Inside { }\n}\n"),
            ("next-line brace", "namespace Nm\n{\npublic sealed class Inside { }\n}\n"),
            ("file-scoped", "namespace Nm;\npublic sealed class Inside { }\n"),
        ):
            case(f"a type in a {label} namespace is still seen as top-level",
                 "Inside" in {n for _m, n in declarations(text)},
                 str(sorted(n for _m, n in declarations(text))))

        # Braces inside a STRING must not shift the depth — a text table full of `{` would otherwise
        # bury every declaration after it.
        case("braces inside a string literal do not shift the depth",
             "AfterTheString" in {n for _m, n in declarations(
                 'public sealed class Before { public const string S = "{{{{"; }\n'
                 'public sealed class AfterTheString { }\n')},
             str(sorted(n for _m, n in declarations(
                 'public sealed class Before { public const string S = "{{{{"; }\n'
                 'public sealed class AfterTheString { }\n'))))
        case("a declaration inside a // comment is not a declaration",
             "Commented" not in {n for _m, n in declarations(
                 "// public sealed class Commented { }\npublic sealed class Real { }\n")},
             "the comment stripper let it through")

        # An UNREADABLE declared source fails CLOSED — it used to be dropped from the set, so a run
        # could go green over source that was never compiled while the gate raises on the same path.
        missing = d / "a" / "GoneMissing.cs"
        _, problems = collisions_within([str(d / "a" / "N1.cs"), str(missing)], d)
        case("an UNREADABLE declared source is refused, naming the path",
             len(problems) >= 1 and any("GoneMissing.cs" in p for p in problems),
             f"{len(problems)} problem(s): {problems[0][:140] if problems else ''}")

    # 🚨 A HOST THAT CANNOT EXECUTE THE SET IS NOT A BROKEN TREE (MeshWeaver#5080). The case on the
    # FAILING side of the change is the first one: before it, a container-built set on macOS
    # produced `✗ BUILD FAILED  dotnet run exited 134 with no diagnostic captured` for every
    # NodeType while `compile-check.py` read the same tree clean. The two cases after it are the
    # ones that must keep working — CI runs the image set on Linux and must never be refused, and a
    # host-built sibling set runs anywhere.
    print("\n── the host is named when it cannot EXECUTE the reference set (#5080) ──")
    osx = execution_host_refusal("image", "osx-arm64")
    case("a container-built set on a non-Linux host is REFUSED, naming both the set and the host",
         osx is not None and "osx-arm64" in osx and "IMAGE" in osx and "compile-check.py" in osx,
         f"got: {osx!r}")
    case("CI's own combination — an image set on Linux — is NOT refused",
         execution_host_refusal("image", "linux-x64") is None
         and execution_host_refusal("image", "linux-arm64") is None,
         "the refusal would take out every gate lane")
    case("a host-built sibling set is NOT refused, on any host",
         all(execution_host_refusal("source-build", h) is None
             for h in ("osx-arm64", "linux-x64", "win-x64")),
         "the local loop this harness exists for would be refused")
    case("a signalled exit is read as a failure to RUN, never as a failure to build",
         (EXEC_FAILED + exit_code_reading(134)).startswith(EXEC_FAILED)
         and "SIGABRT" in exit_code_reading(134) and "SIGSEGV" in exit_code_reading(139),
         f"134 → {exit_code_reading(134)!r}")
    case("an ordinary non-zero exit still says no diagnostic could be attributed",
         "no compiler diagnostic" in exit_code_reading(1),
         f"1 → {exit_code_reading(1)!r}")
    case("this host's RID is spelled the way .NET spells it",
         re.fullmatch(r"(linux|osx|win|[a-z0-9]+)-(x64|arm64|[a-z0-9]+)", host_rid()) is not None,
         f"got {host_rid()!r}")

    print(f"\n{'✓ self-test green' if not failures else '✗ ' + str(len(failures)) + ' self-test case(s) FAILED'}")
    return 0 if not failures else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("module", nargs="?", help="module folder to test, e.g. Ifrs17")
    parser.add_argument("--self-test", action="store_true",
                        help="check the collision diagnostics still fire and still name the symbol")
    parser.add_argument("--refs", help="directory of framework assemblies (default: sibling MeshWeaver build)")
    parser.add_argument("--list", action="store_true", help="list the tests instead of running them")
    parser.add_argument("--keep", action="store_true", help="keep the generated project for inspection")
    parser.add_argument("--type", action="append", default=[],
                        help="only this NodeType (repeatable); matched on the node name or full path")
    parser.add_argument("--with-generator", action="store_true",
                        help="also feed the BusinessRules scope generator. OFF by default: proxies "
                             "are committed by gen-scope-proxies.py and the mesh runs no generator, "
                             "so the default mirrors the mesh. Turning this on when proxies are "
                             "committed produces a SECOND implementation per scope and registration "
                             "fails on a duplicate key.")
    args = parser.parse_args()

    if args.self_test:
        return self_test()
    if not args.module:
        parser.error("a module is required (or pass --self-test)")

    module_dir = ROOT / args.module
    if not module_dir.is_dir():
        print(f"✗ no such module: {args.module}")
        return 2

    cc = load_compile_check()

    # 🚨 One compilation per NODETYPE, from that type's OWN declared `sources` — the unit the mesh
    # compiles. Types resolving to an identical set are compiled once and the result attributed to
    # all of them (46 UWDeepfield types collapse to 14 distinct sets).
    types = discover_module_nodetypes(cc, module_dir)
    if not types:
        print(f"✗ {args.module}: no NodeType declares any source.")
        print("  This script runs the tests of a NodeType's OWN compilation unit; without a")
        print("  NodeType there is nothing the mesh would compile, and nothing to run.")
        return 2

    # A .cs under a Source/ or Test/ folder that NO NodeType declares is never compiled by the mesh
    # either — a real hole worth naming, because it looks tested and is not.
    # 🚨 Computed over EVERY NodeType, before any --type narrowing: an orphan count derived from a
    # filtered run would report the other types' files as unreachable, which is the same shape of
    # lie this script exists to remove.
    declared_by_all = set().union(*(s for _, s in types)) if types else set()
    orphans = sorted(
        str(p.relative_to(ROOT)) for p in module_dir.rglob("*.cs")
        if any(part in ("Source", "Test") for part in p.relative_to(module_dir).parts[:-1])
        and str(p.resolve()) not in declared_by_all)

    if args.type:
        wanted = {t.lower() for t in args.type}
        types = [(n, s) for n, s in types
                 if n.lower() in wanted or n.split("/")[-1].lower() in wanted]
        if not types:
            print(f"✗ {args.module}: no NodeType matches {', '.join(args.type)}")
            return 2

    sets: dict[frozenset, list[str]] = {}
    sourceless = []
    for name, s in types:
        if s:
            sets.setdefault(s, []).append(name)
        else:
            sourceless.append(name)

    # Only sets carrying a *Tests class are compiled: compile-check.py already compiles every type,
    # and building 33 Ifrs17 sets to run 2 suites is pure latency. The count of the rest is REPORTED.
    def has_suite(s) -> bool:
        for p in s:
            try:
                if SUITE_DECL.search(Path(p).read_text(encoding="utf-8", errors="ignore")):
                    return True
            except OSError:
                continue
        return False

    testable = {s: names for s, names in sets.items() if has_suite(s)}
    no_suite = sum(len(names) for s, names in sets.items() if s not in testable)

    print(f"{args.module}: {len(types)} NodeType(s) → {len(sets)} distinct source set(s); "
          f"{len(testable)} carry a *Tests suite")
    if sourceless:
        print(f"  {len(sourceless)} NodeType(s) declare no source at all (nothing to compile)")
    if orphans:
        print(f"  ⚠ {len(orphans)} .cs file(s) under Source/ or Test/ that NO NodeType declares — "
              f"the mesh never compiles these either:")
        for o in orphans[:8]:
            print(f"      {o}")
    if not testable:
        print(f"\n✗ {args.module}: no NodeType carries a *Tests class — nothing to run.")
        return 2

    # `discover_refs` returns ({dll: path}, search_roots, layout) — it grew the third element when
    # the platform learned to tell an image extraction from a source build. This copy still unpacked
    # two and died on `ValueError: too many values to unpack` before a single test could run; the
    # SAME drift sat in gen-scope-proxies.py (Reinsurance#222). Both call sites, swept together.
    refs, search_roots, layout = cc.discover_refs(args.refs)
    if not refs:
        print("✗ no framework assemblies found — build the sibling MeshWeaver checkout, or pass --refs")
        return 2
    # A short reference set must not pass quietly: it would run the suites against a framework
    # nobody runs. Same refusal, same function as compile-check.py.
    if (refusal := cc.short_reference_set_refusal(
            refs, layout, str(search_roots[0]) if search_roots else "the reference set")) is not None:
        print("\n" + refusal)
        return 2
    # The Microsoft.Extensions.AI assemblies are mesh-provided at runtime and are NOT in the core's
    # src/*/bin set; without them an AI-using NodeType fails CS0234 here while compiling on the mesh.
    ai_refs = cc.discover_ai_refs(search_roots)
    refs.update(ai_refs)
    ai_available = bool(ai_refs)
    # 🚨 THE REGISTRY-SERVED MODULES, AND THE GATE'S OWN REFUSAL (MeshWeaver#4785, Copilot review of
    # #4916). `MeshWeaver.AI`, `MeshWeaver.Markdown.Collaboration` and `MeshWeaver.Maps` are no
    # longer in the platform image (#2276 / #3175), so `compile-check.py` adds `discover_module_refs`
    # and REFUSES when one is still missing — because their absence does not look like an absence, it
    # looks like fifteen NodeTypes breaking, named against the CONTENT. This harness had only its own
    # best-effort `sibling_plugin_refs` overlay, so the same short set reported those as broken
    # NodeType source, on the very content the gate compiles clean. Same discovery, same refusal.
    module_refs = cc.discover_module_refs(search_roots)
    for name, path in module_refs.items():
        refs.setdefault(name, path)   # never clobber a copy the reference set already carries
    missing_modules = [m for m in cc.MODULE_REFS_NOT_IN_IMAGE if f"{m}.dll" not in refs]
    if missing_modules:
        print("\n✗ the reference set is missing "
              f"{len(missing_modules)} module assembl{'y' if len(missing_modules) == 1 else 'ies'} "
              "that the platform image no longer ships:\n"
              + "".join(f"    {m}.dll\n" for m in missing_modules)
              + "  These are registry-served, so `/app` does not carry them. Every NodeType that\n"
                "  binds one fails CS0246/CS0103 naming the CONTENT — which reads as this repo\n"
                "  breaking when the real fault is a short reference set. Refusing to run suites\n"
                "  against it, exactly as compile-check.py refuses to report those as breaks.\n"
                "\n  Build them once in the framework checkout, then re-run:\n"
              + "".join(f"    dotnet build src/{m} -c Release\n" for m in missing_modules))
        return 2
    host = host_rid()
    if (refusal := execution_host_refusal(layout, host)) is not None:
        # 🚨 EXIT 3, DISTINGUISHABLY (MeshWeaver#5080). 1 means "the content failed", 2 means "the
        # harness could not be set up"; neither is true here — the harness is fine and the content is
        # unexamined. A separate code lets a launcher tell "your tree is broken" from "this host
        # cannot answer", which is the whole distinction the old `BUILD FAILED` collapsed.
        print(refusal)
        return 3
    if layout == "image":
        # 🚨 A DIVERGENCE THAT CANNOT BE MIRRORED, SO IT IS NAMED. Given an image-shaped set the gate
        # switches to implementation frameworks (`DisableImplicitFrameworkReferences`) and compiles
        # against the image's own assemblies. This harness COMPILES AND RUNS, and `dotnet run`
        # against a Linux image's implementation assemblies does not execute on the host this loop
        # exists for — so mirroring the mode would trade a silent compile difference for a harness
        # that cannot run at all with the set CI uses. Say which compile you are looking at instead.
        print("  ⚠ this is an IMAGE-shaped reference set. The gate compiles it with "
              "DisableImplicitFrameworkReferences against the image's own framework; this harness "
              "cannot, because it also RUNS the result. A CS0012/CS1701-shaped diagnostic here is "
              "therefore a property of this csproj, not of the content — a sibling source build is "
              "the set that answers about it.")
    if not args.refs:
        # Only for AUTO-discovery. An explicit --refs (CI passes the platform image's assembly set)
        # is authoritative and is never second-guessed.
        refs, from_plugins = sibling_plugin_refs(refs)
        if from_plugins:
            print(f"  ref set: {len(refs)} assemblies "
                  f"({from_plugins} from the sibling MeshWeaver.Plugins build)")

    generator = find_generator(search_roots) if args.with_generator else None
    analyzers = ""
    if generator:
        # Copy under its own name; the analyzer is resolved by assembly identity.
        analyzers = "  <ItemGroup>\n    <Analyzer Include=\"analyzers/{}\" />\n  </ItemGroup>\n".format(
            os.path.basename(generator))

    refs_xml = "\n".join(
        f'    <Reference Include="{Path(p).stem}"><HintPath>{p}</HintPath></Reference>'
        for p in sorted(refs.values()))

    work = Path(tempfile.mkdtemp(prefix=f"node-tests-{args.module.lower()}-"))
    total = {"suites": 0, "cases": 0, "passed": 0, "failed": 0, "skipped": 0}
    build_failed: list[tuple[str, list[str]]] = []
    refused: list[tuple[str, list[str]]] = []
    unverified: list[tuple[str, list[str]]] = []
    unrunnable: list[tuple[str, list[str]]] = []
    executed_sets = 0
    t0 = time.time()
    try:
        (work / "NodeTestRunner.cs").write_text(RUNNER, encoding="utf-8")
        if generator:
            (work / "analyzers").mkdir()
            shutil.copy2(generator, work / "analyzers" / os.path.basename(generator))

        restored = False
        ordered = sorted(testable.items(), key=lambda kv: sorted(kv[1])[0])
        for i, (s, names) in enumerate(ordered, 1):
            lead = sorted(names)[0]
            extra = f" (+{len(names) - 1} sharing this set)" if len(names) > 1 else ""
            print(f"\n[{i}/{len(ordered)}] {lead}{extra} — {len(s)} file(s)")

            sources, problems = collisions_within(s, module_dir)
            if problems:
                print(f"  ✗ {lead}: this NodeType's own sources collide, so it cannot compile:")
                for p in problems:
                    for line in p.splitlines():
                        print(f"  {line}")
                refused.append((lead, problems))
                continue

            tally, errors, tail, unverifiable = run_set(
                work, sources, refs_xml, analyzers, cc, ai_available, args.list, restored)
            restored = True
            if tally is None and unverifiable:
                # Every diagnostic is the absent Microsoft.Extensions.AI assemblies, which the mesh
                # supplies at runtime. Nobody could have run this set here; say that, rather than
                # accusing the source — and count it, so a summary cannot read as full coverage.
                print(f"  ⚠ UNVERIFIABLE: needs Microsoft.Extensions.AI, which is not in this "
                      f"reference set ({len(errors)} diagnostic(s), all attributable to it):")
                for e in errors[:6]:
                    print(f"      {e}")
                unverified.append((lead, errors))
                continue
            if tally is None and errors and errors[0].startswith(EXEC_FAILED):
                # 🚨 NOT `BUILD FAILED` (MeshWeaver#5080). No compiler diagnostic at all means it
                # BUILT and died on load, and the two need opposite responses: one sends the reader
                # to the source, the other to the host or the reference set. Counted in its own
                # bucket so a run of these can never read as coverage, and the tail is shown or the
                # failure is indistinguishable from "the harness did nothing".
                print(f"  ✗ COULD NOT EXECUTE — {errors[0]}")
                print(f"      this host is {host} and the reference set is {layout}-shaped"
                      + ("; a container-built set does not load here" if layout == "image" else ""))
                print("  ── last output ──")
                for line in tail.splitlines()[-15:]:
                    print(f"      {line}")
                unrunnable.append((lead, errors))
                continue
            if tally is None:
                print(f"  ✗ BUILD FAILED ({len(errors)} distinct diagnostic(s)):")
                for e in errors[:20]:
                    print(f"      {e}")
                if len(errors) > 20:
                    print(f"      … and {len(errors) - 20} more")
                build_failed.append((lead, errors))
                continue
            for line in tail.splitlines():
                print(f"  {line}")
            if tally["suites"] == 0:
                print("  ⚠ compiled, but the runner found no usable suite "
                      "(a *Tests class must be `static` to be picked up)")
            executed_sets += 1
            for k in total:
                total[k] += tally[k]

        elapsed = time.time() - t0
        verb = "listed" if args.list else "ran"
        outcome = ("nothing executed (--list)" if args.list else
                   f"{total['passed']} passed · {total['failed']} failed")
        print(f"\n── {args.module} ──  {executed_sets}/{len(ordered)} NodeType source set(s) "
              f"{verb} · {total['suites']} suite(s) · {total['cases']} case(s) · {outcome} · "
              f"{total['skipped']} skipped (need the Tests layout area)   ({elapsed:.0f}s)")
        if no_suite:
            print(f"   {no_suite} NodeType(s) carry no *Tests class — not compiled here; "
                  f"compile-check.py covers their compilation.")
        if refused:
            print(f"\n✗ {len(refused)} NodeType(s) REFUSED (colliding sources — see above): "
                  + ", ".join(n for n, _ in refused))
        if unverified:
            print(f"\n⚠ {len(unverified)} NodeType(s) UNVERIFIABLE (need Microsoft.Extensions.AI, "
                  f"which this reference set does not carry): " + ", ".join(n for n, _ in unverified))
            print("   Their suites did NOT run. Pass --refs at a set that carries the AI assemblies "
                  "(CI's does) to cover them.")
        if unrunnable:
            print(f"\n✗ {len(unrunnable)} NodeType(s) COULD NOT EXECUTE — they compiled and the "
                  f"produced process did not start: " + ", ".join(n for n, _ in unrunnable))
            print(f"   This is a property of the HOST ({host}) or of the reference set "
                  f"({layout}-shaped), not of the content — `compile-check.py` is the tool that "
                  f"answers about the content, and the tester image is where these run "
                  f"(MeshWeaver#5080).")
        if build_failed:
            print(f"\n✗ {len(build_failed)} NodeType(s) failed to BUILD: "
                  + ", ".join(n for n, _ in build_failed))
        # 🚨 THE ✓ IS EARNED BY SOMETHING HAVING RUN (MeshWeaver#4785, Copilot review of #4916).
        # `UNVERIFIABLE` was added in this PR so an absent Microsoft.Extensions.AI stops being
        # reported as broken content — and then the verdict still printed `✓ 0 test(s) passed` and
        # returned 0 when EVERY set was unverifiable, which is the same lie one layer along: a missing
        # local framework rendering as successful coverage. So the checkmark is suppressed whenever
        # anything is unverified, the verdict names both numbers, and a run where NO set executed at
        # all is a non-zero exit. "Nothing could be verified" is not a pass.
        clean = not refused and not build_failed and not unrunnable and total["failed"] == 0
        if clean and unverified:
            print(f"\n⚠ {total['cases']} test(s) {'listed' if args.list else 'passed'} across "
                  f"{total['suites']} suite(s) — and {len(unverified)} NodeType(s) were NOT "
                  f"verified (see above). This run does not cover them.")
        elif clean:
            print(f"\n✓ {total['cases']} test(s) listed across {total['suites']} suite(s)."
                  if args.list else
                  f"\n✓ {total['passed']} test(s) passed across {total['suites']} suite(s).")
        if clean and executed_sets == 0:
            print(f"\n✗ {args.module}: NOTHING was verified — 0 of {len(ordered)} source set(s) ran. "
                  "A run that could not execute a single suite is not a pass.")
            return 1
        return 0 if clean else 1
    finally:
        if args.keep:
            print(f"\nproject kept at {work}")
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
