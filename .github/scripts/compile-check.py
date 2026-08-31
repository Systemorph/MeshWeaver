#!/usr/bin/env python3
"""compile-check.py — the COMPILING PR gate for the plugin node repos.

`validate-repos.py` checks JSON shape + Source presence but never compiles. That hole let
async-broken UWDeepfield source merge and park two production meshes (2026-07-21). This gate closes
it: it compiles every NodeType's C# `Source` against the framework assemblies EXACTLY as the mesh
does on import (all sources concatenated, `using` directives hoisted — reproduced here as a
GlobalUsings union + the framework's implicit global usings), and fails on any *regression*.

Faithful mesh reproduction (mirrors scratchpad/build-check.sh, proven against the live compile):
  * ref assemblies = every `<core>/src/*/bin/(Debug|Release)/net10.0/*.dll`, deduped by filename,
    preferring the assembly's OWN project dir + newest mtime;
  * a GlobalUsings.cs prelude = the UNION of `using X;` across the compiled files PLUS the implicit
    global usings the mesh injects (DynamicMeshNodeAttributeGenerator);
  * `Microsoft.Extensions.AI` is dropped from the union (not in the local dll set — the mesh
    supplies it); a set that genuinely needs it is reported UNVERIFIABLE rather than false-failed;
  * per-type source resolution replays the THREE source queries the mesh compile log shows: the
    type's own `Source` subtree (plus any DECLARED `sources` — never an implicit partition Source; the mesh resolves only what is declared) and the type's own `Test`
    subtree — honouring the `sources` field forms actually present in this repo;
  * …AND the type's `configuration` LAMBDA, wrapped in the same generated `ConfigureHub` method the
    mesh builds around it (`DynamicMeshNodeAttributeGenerator`). That field is C# living in JSON, so
    no compiler ever saw it: on 2026-08-09 `SocialMedia/Post`, `Profile` and `PostsHub` each called
    `AddTracking()` there after the framework deleted it, the sibling gate reported 22/22 clean, and
    all three production portals hit `REFUSING READINESS`. Sources and lambda compile TOGETHER (the
    lambda names types the sources define), so a set is keyed by both.

Ratcheting allowlist (`scripts/compile-check.allow`): the tree carries KNOWN UWDeepfield async-port
debt. An allowlisted type that still fails is reported as known-debt (no gate fail); one that now
compiles clean FAILS the gate ("remove it — it compiles now") so debt only ever shrinks. A
NON-allowlisted failure fails the gate — a real regression. Exit non-zero iff any gate-fail.
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
# 🚨 THE PLATFORM'S copy is the one implementation, every repo ("centralize scripts as best as
# possible", maintainer, 2026-09-01) — the reusable lane fetches THIS file at the pinned
# platform ref and runs it against the CALLER's tree, so the tree root and the allow-file
# (per-repo POLICY, the only piece that stays in the caller) arrive via the environment.
# Unset, both fall back to the script-beside-the-repo layout so a legacy per-repo copy keeps
# working during migration.
import os as _os
ROOT = Path(_os.environ["MW_REPO_ROOT"]).resolve() if _os.environ.get("MW_REPO_ROOT") else SCRIPTS.parent
ALLOW_FILE = (Path(_os.environ["MW_ALLOW_FILE"]).resolve() if _os.environ.get("MW_ALLOW_FILE")
              else (ROOT / "scripts" / "compile-check.allow" if _os.environ.get("MW_REPO_ROOT")
                    else SCRIPTS / "compile-check.allow"))

# Directories that are NOT node repos.
# "WhatsNew" is the satellite release-note lane (MeshWeaver#2539/#2608): a CHANGELOG, never a
# package and never module content. Must stay in step with the other scripts' SKIP sets.
SKIP = {"src", "test", "scripts", "e2e", "app", "WhatsNew", ".git", ".github", ".claude", ".worktrees"}

# The implicit global usings the mesh injects into every compiled NodeType source
# (MeshWeaver.Graph/Configuration/DynamicMeshNodeAttributeGenerator.cs). Faithful reproduction
# needs them because the authored .cs rely on them being present without an explicit `using`.
IMPLICIT_USINGS = [
    "System",
    "System.Collections.Generic",
    "System.ComponentModel",
    "System.ComponentModel.DataAnnotations",
    "System.Linq",
    "System.Reactive.Linq",
    "System.Text.Json.Serialization",
    "MeshWeaver.Mesh",
    "MeshWeaver.Messaging",
    "MeshWeaver.Data",
    "MeshWeaver.Domain",
    "MeshWeaver.Graph",
    "MeshWeaver.Graph.Configuration",
    "MeshWeaver.Layout",
    "MeshWeaver.Layout.Composition",
    "MeshWeaver.Layout.Domain",
    "MeshWeaver.Layout.Views",
    "MeshWeaver.Application.Styles",
    "MeshWeaver.ContentCollections",
    "MeshWeaver.Mesh.Services",
    "Microsoft.Extensions.DependencyInjection",
    "Microsoft.Extensions.Configuration",
]

# Not in the local dll set — the mesh provides it. Dropped from the union; a set that truly needs it
# is flagged UNVERIFIABLE instead of being counted as a compile failure.
EXTERNAL_USINGS = {"Microsoft.Extensions.AI"}


# ── discovery ────────────────────────────────────────────────────────────────────────────────────

def is_nodetype(node: object) -> bool:
    # Tolerate any JSON root — a checkout with clients/react/node_modules carries list-root files
    # (dayjs/locale.json …) and discovery must skim past them, not crash (2026-08-29).
    if not isinstance(node, dict):
        return False
    content = node.get("content") or {}
    return content.get("$type") == "NodeTypeDefinition" or node.get("nodeType") == "NodeType"


def node_dir(json_path: Path) -> Path:
    """The node's folder — a node with children exports as X/index.json, so its folder IS X/."""
    return json_path.parent if json_path.name == "index.json" else json_path.with_suffix("")


def discover_nodetypes(root: Path):
    repos = [d for d in root.iterdir() if d.is_dir() and d.name not in SKIP]
    out = []
    for repo in sorted(repos):
        for f in sorted(repo.rglob("*.json")):
            if "node_modules" in f.parts:
                continue
            try:
                node = json.loads(f.read_text(encoding="utf-8"))
            except (json.JSONDecodeError, OSError):
                continue
            if is_nodetype(node):
                out.append((f, node))
    return out


# ── source resolution (mirror the mesh's three source queries) ─────────────────────────────────────

def resolve_spec(spec: str, nd: Path, root: Path):
    """Resolve one `sources` entry to a directory (or None). Handles the forms in this repo:
       `namespace:Source scope:subtree`, `shared=namespace:UWDeepfield/Source scope:subtree`,
       `client=namespace:.../Source scope:subtree`, `shared=@Edu/CourseInvite/Source`, `@X/Y/Source`."""
    spec = spec.strip()
    # strip an `alias=` prefix (shared= / client= / catscen= …) in front of a namespace:/@ value
    m = re.match(r"^([A-Za-z][A-Za-z0-9]*)=(.*)$", spec)
    if m and (m.group(2).startswith("@") or m.group(2).startswith("namespace:")):
        spec = m.group(2)
    if spec.startswith("@"):
        target = root / spec[1:].strip()
        # The mesh resolves a single-node shorthand to BOTH an exact-path match and a namespace-
        # subtree match (NodeTypeDefinition.Sources), so `shared=@X/Source/OneFile` names ONE Code
        # node. On disk that node is `OneFile.cs`; a directory of that name is the subtree form.
        # Without this branch a single-file share resolved to a non-existent directory and the
        # consumer failed CS0246 locally while the mesh compiled it fine (Plugins#777's
        # Hosting/PlatformBuildInbox → shared=@Hosting/Deployment/Source/PlatformBuildInboxWatcher).
        if not target.is_dir() and target.with_suffix(".cs").is_file():
            return target.with_suffix(".cs")
        return target
    m = re.match(r"^namespace:(\S+)", spec)
    if m:
        val = m.group(1)
        absolute = root / val            # absolute node path (e.g. UWDeepfield/Source)
        if absolute.is_dir():
            return absolute
        return nd / val                  # relative to the node (e.g. `Source` → <node>/Source)
    return None


def resolve_sources(json_path: Path, node: dict, root: Path) -> frozenset:
    nd = node_dir(json_path)
    repo = json_path.relative_to(root).parts[0]
    content = node.get("content") or {}
    sources = content.get("sources")
    dirs = []
    if not sources:
        # default (sources == null): own Source subtree ONLY — this matches the mesh exactly.
        # 🚨 Do NOT re-add the partition-root Source here: the gate used to include it, which let
        # 28 NodeTypes compile green locally while failing on-mesh with CS0246 (2026-07-24) — the
        # mesh resolves nothing beyond the declared `sources`. A type that needs the partition
        # model must DECLARE it (e.g. "shared=@<Plugin>/Source").
        dirs.append(nd / "Source")
    else:
        for spec in sources:
            if not isinstance(spec, str):
                continue
            d = resolve_spec(spec, nd, root)
            if d is not None:
                dirs.append(d)
    dirs.append(nd / "Test")             # the mesh ALWAYS also queries the type's own Test subtree
    cs = set()
    for d in dirs:
        if d.is_dir():
            cs.update(str(p.resolve()) for p in d.rglob("*.cs"))
        elif d.is_file() and d.suffix == ".cs":
            cs.add(str(d.resolve()))
    return frozenset(cs)


# ── the configuration lambda (NOT a .cs file — and that is exactly why it broke prod) ──────────────

def config_lambda(node: dict):
    """The NodeType's hub-configuration lambda source, or None.

    🚨 THIS IS CODE, and until 2026-08-09 nothing on earth compiled it. It lives in the node JSON
    (`content.configuration`) rather than in a `.cs` file, so a repo-wide grep finds it but no
    compiler ever sees it: not `dotnet build` (node trees are `<None>` content), and not this gate,
    which resolved only `Source/`+`Test/` subtrees. The portal compiles it at RUNTIME —
    `MeshNodeCompilationService` passes `NodeTypeDefinition.Configuration` to
    `DynamicMeshNodeAttributeGenerator.GenerateAttributeSource`, which drops it into a generated
    `ConfigureHub` method.

    That hole took down all three production portals on 2026-08-09. MeshWeaver's `eafd353ed` deleted
    the `AddTracking()` extension on `MessageHubConfiguration`; `SocialMedia/Post`, `Profile` and
    `PostsHub` each still called it from this field. CI was green — the sibling repo's copy of this
    gate reported 22/22 clean on that very tree — and the break surfaced only on the next framework
    bump, which recompiles every dynamic NodeType at once: `SocialMedia/Post` → CompileError, its
    three dependents → UpstreamFailed, and `DynamicTypePreWarmer: REFUSING READINESS`. 39 of this
    repo's NodeTypes carry such a lambda; none of them had ever been compiled either.

    `Configuration` is the field the dynamic-compile path actually reads; `HubConfiguration` is its
    sibling on the same record, so it is honoured as a fallback rather than silently ignored.
    """
    content = node.get("content") or {}
    for field in ("configuration", "hubConfiguration"):
        value = content.get(field)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return None


def config_check_source(lambda_src: str) -> str:
    """Reproduce the `ConfigureHub` method `DynamicMeshNodeAttributeGenerator` generates around the
    lambda, so Roslyn type-checks it against the SAME surface the mesh will.

    Mirrors `GenerateAttributeSource`: `var result = config;` → `result.AddMeshDataSource();` → the
    user lambda bound to `Func<MessageHubConfiguration, MessageHubConfiguration>` → applied. The
    generated `MeshNodeProviderAttribute` subclass, its `Nodes` property and the optional
    `AddContentCollections(…)` block are deliberately NOT reproduced: all three are built from node
    METADATA (path/name/icon, the `contentCollections` list), never from authored code, so they
    cannot carry a user break — while constructing them here would add ref-set requirements that
    could only produce FALSE failures. The lambda is the authored part, and the lambda is what this
    compiles.

    Global namespace, matching the mesh: node `.cs` files declare no namespace, so their types are
    resolved by simple name from here exactly as they are in the generated file.

    The lambda is emitted VERBATIM at column 0 — deliberately never re-indented. C# raw string
    literals (`\"\"\"…\"\"\"`) strip indentation relative to their closing delimiter, so shifting the
    text would silently change the VALUE of any such string inside the lambda. The mesh drops it in
    unmodified too, so verbatim is both the safe choice and the faithful one."""
    return (
        "// Generated by compile-check.py — reproduces the ConfigureHub method that\n"
        "// DynamicMeshNodeAttributeGenerator.GenerateAttributeSource builds around the NodeType's\n"
        "// `configuration` lambda, so the lambda is type-checked before it can reach a portal.\n"
        "internal static class __MeshWeaverConfigurationCheck\n"
        "{\n"
        "    private static MessageHubConfiguration ConfigureHub(MessageHubConfiguration config)\n"
        "    {\n"
        "        var result = config;\n"
        "        result = result.AddMeshDataSource();\n"
        "        Func<MessageHubConfiguration, MessageHubConfiguration> userConfig =\n"
        f"{lambda_src};\n"
        "        result = userConfig(result);\n"
        "        return result;\n"
        "    }\n"
        "}\n"
    )


# ── reference assemblies ───────────────────────────────────────────────────────────────────────────

def _refuse_if_sibling_stale(core: "Path | str") -> None:
    """Refuse a verdict when the framework checkout is behind its remote.

    Every `src/` project compiles against this checkout, so a stale one silently changes the
    compiler's inputs — and CI checks core out FRESH while a developer machine does not.
    MEASURED 2026-08-29, twice within an hour by two independent sessions:

      * 20 commits behind -> phantom RED: `Ambiguous project name
        'MeshWeaver.Markdown.Collaboration'`, because the project core had DELETED was still on
        disk here as well as in the branch under test.
      * 22 commits behind -> false GREEN: `dotnet build` and this gate both passed while being
        structurally incapable of seeing a dangling ProjectReference to that same deleted
        project. It was reported as gate coverage. `main` then failed 24 of 39 jobs and NOTHING
        published to the registry for ANY package.

    A gate that answers confidently from the wrong inputs is worse than one that refuses.

    It never fetches — a silent network call inside a gate is its own trap, and an unreachable
    remote must not read as "current". When freshness cannot be determined (no git, no
    `origin/main`, path gone) it says so and continues: refusing on "cannot tell" would break
    every offline and container run, and CI is not exposed to this hazard anyway.
    """
    # CI is not exposed to this hazard — it checks the framework out FRESH for every run — and a
    # gate that can refuse there is a gate that can break the build for a reason unrelated to the
    # change under test. So it is a no-op on a runner, by construction rather than by argument.
    if os.environ.get("CI") or os.environ.get("GITHUB_ACTIONS"):
        return
    core = Path(core)
    try:
        r = subprocess.run(["git", "-C", str(core), "rev-list", "--count", "HEAD..origin/main"],
                           capture_output=True, text=True, timeout=15)
    except Exception as exc:
        print(f"  (framework freshness unchecked for {core}: {exc})", file=sys.stderr)
        return
    if r.returncode != 0:
        return                                   # not a git repo / no origin/main — CI's case
    n = r.stdout.strip()
    if n.isdigit() and int(n) > 0:
        sys.exit(
            f"\n\u2717 REFUSING TO RUN: the framework checkout is {n} commit(s) behind its remote.\n"
            f"    {core}\n\n"
            "  Every src/ project compiles against it, so this gate's verdict would be meaningless.\n"
            "  This exact staleness has produced BOTH a phantom failure and a false pass.\n\n"
            f"  Fix:  git -C {core} pull --ff-only origin main\n")



def _report_framework_provenance(refs: dict, ref_roots) -> None:
    """Say WHICH framework this verdict was measured against, on every run.

    🚨 A GATE'S VERDICT IS ONLY MEANINGFUL WITH ITS INPUT. This gate's answer depends on framework
    assemblies that move INDEPENDENTLY of the diff under test, so the same commit can be red and
    then green with nothing changed — and without the input printed, that is indistinguishable
    from "the diagnosis was wrong".

    MEASURED 2026-08-29, and it cost two sessions hours:

      * A core carve-out deleted `CommentsExtensions` from source, but the ACR image still carried
        it inside `MeshWeaver.Graph`. The gate reported `CS0433: exists in both …` — a duplicate
        that existed only in the stale INPUT. It went SUCCESS on the identical diff once a
        post-carve-out image published. Same diff, different input, opposite verdict.
      * A sibling core checkout 22 commits behind produced a false GREEN; one 20 behind produced a
        phantom RED. See `_refuse_if_sibling_stale`.

    The staleness REFUSAL above covers the local checkout, where freshness is knowable. A container
    image's contents cannot be compared to a remote the same way, so the honest guard there is
    provenance: print what was resolved, so a surprising verdict can be checked against its input
    in one line instead of being argued about.
    """
    roots = [str(r) for r in (ref_roots or [])]
    where = roots[0] if roots else "(unknown)"
    digest = os.environ.get("MW_IMAGE_DIGEST") or os.environ.get("MW_TEST_IMAGE") or ""
    line = f"framework: {len(refs)} ref assemblies from {where}"
    if digest:
        line += f"  image={digest}"
    print(line)

def discover_refs(refs_arg):
    """Return ({name.dll: path}, search_roots). If --refs is a dir, glob its *.dll. Else
       auto-discover the sibling core build (Debug preferred, else Release), deduped preferring the
       OWN project dir + newest. `search_roots` is the base dir(s) `discover_ai_refs` then scans for
       the Microsoft.Extensions.AI assemblies (which live under `test/*/bin`, not `src/*/bin`)."""
    if refs_arg:
        # Prefer the PRECISE per-project output glob (the same clean set auto-discovery uses below):
        # <refs>/<proj>/bin/(Debug|Release)/net10.0/*.dll. A bare recursive **/*.dll also sweeps
        # obj/ intermediates, bin/.../ref/ metadata-only reference assemblies and runtimes/<rid>/
        # copies; on some CI runners that larger set deduped by filename to a mix that OMITTED the
        # real framework assemblies, so every NodeType failed CS0246 'MeshWeaver' could not be found
        # (green locally, red in CI with the SAME ~100 ref count). Fall back to recursive when refs
        # points at a FLAT assemblies dir (no src/*/bin layout), the other documented --refs usage.
        dlls = []
        for cfg in ("Debug", "Release"):
            dlls = glob.glob(os.path.join(refs_arg, "*", "bin", cfg, "net10.0", "*.dll"))
            if dlls:
                break
        if not dlls:
            dlls = glob.glob(os.path.join(refs_arg, "**", "*.dll"), recursive=True)
        # `--refs <core>/src` is the documented form, so the checkout is its parent.
        _refuse_if_sibling_stale(Path(refs_arg).resolve().parent)
        search_roots = [refs_arg]
    else:
        # Auto-discover the sibling built core. In a worktree ROOT is under `.worktrees/<name>`, so
        # try the main checkout's sibling too, and the canonical ~/code/MeshWeaver.
        candidates = [
            ROOT.parent / "MeshWeaver",                       # standalone checkout sibling
            ROOT.parent.parent / "MeshWeaver",               # from .worktrees/<name>
            ROOT.parent.parent.parent / "MeshWeaver",        # deeper worktree nesting
            Path(os.path.expanduser("~/code/MeshWeaver")),
        ]
        dlls = []
        core_used = None
        for core in candidates:
            for cfg in ("Debug", "Release"):
                dlls = glob.glob(str(core / "src" / "*" / "bin" / cfg / "net10.0" / "*.dll"))
                if dlls:
                    core_used = core
                    break
            if dlls:
                break
        if core_used is not None:
            _refuse_if_sibling_stale(core_used)
        search_roots = [core_used] if core_used else list(candidates)
    seen = {}
    for dll in dlls:
        n = os.path.basename(dll)
        proj = os.path.basename(os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(dll)))))
        own = (proj + ".dll") == n
        if n not in seen:
            seen[n] = (dll, own)
        else:
            cur_dll, cur_own = seen[n]
            if (own and not cur_own) or (own == cur_own and os.path.getmtime(dll) > os.path.getmtime(cur_dll)):
                seen[n] = (dll, own)
    # Absolutize: refs are written as <HintPath> into a check.csproj that `dotnet build` runs from a
    # TEMP dir, so a RELATIVE path (what a relative `--refs ../MeshWeaver/src` glob yields — the CI
    # invocation) would resolve against that temp dir, load nothing, and every NodeType would fail
    # CS0246 'MeshWeaver' could not be found (green locally with an ABSOLUTE --refs, red in CI). The
    # source .cs files are already resolve()d (see collect); do the same for refs.
    return {n: os.path.abspath(p) for n, (p, _) in seen.items()}, search_roots


def discover_ai_refs(search_roots):
    """Locate the `Microsoft.Extensions.AI*` assemblies the mesh supplies at runtime but that are
       NOT in the core `src/*/bin` set discover_refs globs (they live under the core's `test/*/bin`
       output and in NuGet). Returns {name.dll: path} for net10.0, or {} if none can be found — in
       which case the AI-using sets stay UNVERIFIABLE. Prefers a build-output dll over a NuGet one,
       newest mtime within a tier."""
    found = {}   # name -> (path, from_build_output, mtime)

    def consider(dll, build_output):
        n = os.path.basename(dll)
        try:
            mt = os.path.getmtime(dll)
        except OSError:
            return
        cur = found.get(n)
        if cur is None or (build_output and not cur[1]) or (build_output == cur[1] and mt > cur[2]):
            found[n] = (dll, build_output, mt)

    for root in search_roots:
        if not root:
            continue
        root = Path(root)
        for cfg in ("Debug", "Release"):
            for dll in glob.glob(str(root / "**" / "bin" / cfg / "net10.0" /
                                    "Microsoft.Extensions.AI*.dll"), recursive=True):
                consider(dll, True)
        # a flat --refs directory
        for dll in glob.glob(str(root / "Microsoft.Extensions.AI*.dll")):
            consider(dll, True)
    # NuGet fallback (net10.0) only if nothing was found in the build output
    if not found:
        for dll in glob.glob(os.path.expanduser(
                "~/.nuget/packages/microsoft.extensions.ai*/**/lib/net10.0/"
                "Microsoft.Extensions.AI*.dll"), recursive=True):
            consider(dll, False)
    # Absolutize — same reason as discover_refs: these paths become <HintPath>s in a check.csproj
    # that `dotnet build` runs from a TEMP dir, so a RELATIVE path (what a relative `--refs ../refs`
    # glob yields — the CI invocation) resolves against that temp dir, RAR silently DROPS the
    # reference, and every AI-using set fails CS0234 on the Microsoft.Extensions.AI types
    # (green locally with an absolute --refs, red in CI — MeshWeaver.Reinsurance PR #33 hit
    # exactly this with the flat image ref layout).
    return {n: os.path.abspath(p) for n, (p, _, _) in found.items()}


# ── compile ─────────────────────────────────────────────────────────────────────────────────────

def usings_union(cs_files, ai_available: bool) -> tuple:
    """Return (union_usings sorted, needs_external bool).

    `Microsoft.Extensions.AI` is never hoisted into the global-usings union (the source files keep
    their own `using` line). If the AI assemblies WERE located and added to the ref set
    (`ai_available`), that using resolves for real and the set is compiled normally. Only if the AI
    assemblies genuinely could not be found do we flag `needs_external` so the set can be reported
    UNVERIFIABLE — and even then only when EVERY diagnostic is attributable to the missing AI types
    (see `_is_ai_error`); any non-AI error still FAILS the set."""
    usings = set(IMPLICIT_USINGS)
    needs_external = False
    for f in cs_files:
        try:
            text = Path(f).read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for line in text.splitlines():
            m = re.match(r"\s*using\s+(?:static\s+)?([A-Za-z_][\w.]*)\s*;", line)
            if m:
                ns = m.group(1)
                if ns in EXTERNAL_USINGS or ns.startswith("Microsoft.Extensions.AI"):
                    if not ai_available:
                        needs_external = True
                    continue
                usings.add(ns)
    return sorted(usings), needs_external


def write_project(work: Path, cs_files, refs: dict):
    (work / "GlobalUsings.cs").write_text("", encoding="utf-8")  # placeholder, filled per-set
    ref_xml = "\n".join(
        f'    <Reference Include="{os.path.splitext(n)[0]}"><HintPath>{p}</HintPath></Reference>'
        for n, p in sorted(refs.items()))
    (work / "refs.txt").write_text(ref_xml, encoding="utf-8")


def build_csproj(work: Path, cs_files, ref_xml: str, with_config_check: bool = False,
                 impl_frameworks: bool = False) -> str:
    compiles = '    <Compile Include="GlobalUsings.cs" />\n'
    if with_config_check:
        compiles += '    <Compile Include="ConfigCheck.cs" />\n'
    compiles += "\n".join(
        f'    <Compile Include="{f}" />' for f in sorted(cs_files))
    # 🚨 IMPLEMENTATION-framework mode: when the refs dir carries the platform image's shared
    # frameworks (System.Private.CoreLib present), compile EXACTLY the way the mesh does — against
    # implementation assemblies, no SDK ref pack. The ref pack cannot resolve container-built
    # module assemblies (mw-plugin-test build-project compiles against /app, so its output
    # references System.Private.CoreLib's identity DIRECTLY, not System.Runtime's) — 11 NodeTypes
    # failed CS0012 'System.Private.CoreLib not referenced' on Plugins#1032 the moment the floor
    # bundles were container-built, while the portal compiled the same nodes fine. AspNetCore.App
    # and System.Reactive come from the same refs dir in this mode.
    impl_props = ("    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>\n"
                  if impl_frameworks else "")
    framework_items = ("" if impl_frameworks else
                       '    <FrameworkReference Include="Microsoft.AspNetCore.App" />\n'
                       '    <PackageReference Include="System.Reactive" Version="6.1.0" />\n')
    return f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- Disable the SDK's implicit usings: the MESH compiler injects ONLY the explicit prelude
         (IMPLICIT_USINGS, lifted from DynamicMeshNodeAttributeGenerator) plus the per-set union of
         authored `using`s. `ImplicitUsings=enable` would auto-add System.IO / System.Net.Http /
         System.Threading.Tasks / System.Text / … that the mesh does NOT inject — a source missing a
         `using System.IO;` would compile here but fail on the mesh (a false PASS). -->
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <NoWarn>$(NoWarn);CS1591;CS1998;CS8618;CS8602;CS8604;CS0618</NoWarn>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <RestoreSources>$(RestoreSources);https://api.nuget.org/v3/index.json</RestoreSources>
{impl_props}  </PropertyGroup>
  <ItemGroup>
{framework_items}{compiles}
  </ItemGroup>
  <ItemGroup>
{ref_xml}
  </ItemGroup>
</Project>'''


def compile_set(work: Path, cs_files, ref_xml: str, restored: bool, ai_available: bool,
                lambda_src: str | None = None, impl_frameworks: bool = False):
    """Return (errors, needs_external, infra_error).

    `infra_error` is set when `dotnet build` exits NONZERO but produced NO `CS` diagnostic — a
    restore/SDK/MSBuild failure (NU… / NETSDK… / MSB… codes). Those must FAIL the set, not pass it:
    an unavailable restore or broken ref set otherwise leaves `errors` empty and the set reads OK,
    silently letting anything through. We capture a representative non-CS line as the reason.

    `lambda_src` is the NodeType's `configuration` lambda (see `config_lambda`). When present it is
    compiled ALONGSIDE the sources, in the same compilation, exactly as the mesh does — the sources
    define the types the lambda names, so the two only type-check together."""
    union, needs_external = usings_union(cs_files, ai_available)
    (work / "GlobalUsings.cs").write_text(
        "\n".join(f"global using {u};" for u in union) + "\n", encoding="utf-8")
    config_check = work / "ConfigCheck.cs"
    if lambda_src:
        config_check.write_text(config_check_source(lambda_src), encoding="utf-8")
    elif config_check.exists():
        config_check.unlink()       # the work dir is reused across sets — never leak a stale lambda
    (work / "check.csproj").write_text(
        build_csproj(work, cs_files, ref_xml, with_config_check=bool(lambda_src),
                     impl_frameworks=impl_frameworks), encoding="utf-8")
    cmd = ["dotnet", "build", "check.csproj", "--nologo", "-v", "q"]
    if restored:
        cmd.insert(2, "--no-restore")
    proc = subprocess.run(cmd, cwd=work, capture_output=True, text=True)
    out = proc.stdout + proc.stderr
    errors = []
    for line in out.splitlines():
        m = re.search(r"error (CS\d+): (.*?)(?: \[|$)", line)
        if m:
            errors.append(f"{m.group(1)}: {m.group(2).strip()}")
    # dedup preserving order
    seen = set()
    errors = [e for e in errors if not (e in seen or seen.add(e))]
    infra_error = None
    if proc.returncode != 0 and not errors:
        # nonzero exit with no CS diagnostic ⇒ restore/SDK/MSBuild failure. Capture the first
        # actionable non-CS diagnostic (NU/NETSDK/MSB/error) so the failure names its cause.
        for line in out.splitlines():
            m = re.search(r"error ((?:NU|NETSDK|MSB)\w*\d*|[A-Z]+\d+): (.*?)(?: \[|$)", line)
            if m:
                infra_error = f"{m.group(1)}: {m.group(2).strip()}"
                break
        if infra_error is None:
            for line in out.splitlines():
                if re.search(r"\berror\b", line, re.IGNORECASE):
                    infra_error = line.strip()[:200]
                    break
        if infra_error is None:
            infra_error = f"dotnet build exited {proc.returncode} with no diagnostic captured"
    return errors, needs_external, infra_error


# ── diagnostics classification ─────────────────────────────────────────────────────────────────

def _is_ai_error(err: str) -> bool:
    """True iff a diagnostic is attributable to the absent Microsoft.Extensions.AI assembly (used
       only when the AI dlls could not be located — see usings_union)."""
    return ("Microsoft.Extensions.AI" in err
            or "namespace name 'AI'" in err
            or "'AI' does not exist" in err)


# ── allowlist ────────────────────────────────────────────────────────────────────────────────────

def failure_fingerprint(errors) -> str:
    """A stable fingerprint of a set's failure: the sorted multiset of error codes (e.g.
       `CS0121:1` or `CS0246:2,CS8602:1`). Pins WHAT fails, so a NEW/changed error inside an
       already-allowlisted type is detected even though the type stays allowlisted."""
    from collections import Counter
    codes = Counter()
    for e in errors:
        if not e:
            continue
        head = e.split(":", 1)[0].strip()
        codes[head] += 1
    return ",".join(f"{c}:{n}" for c, n in sorted(codes.items()))


def read_allow():
    """Return {name: fingerprint-or-None}. Line format: `Type/Name  fp=<fingerprint>  # comment`.
       A missing `fp=` (legacy line) maps to None → fingerprint check is skipped for that entry."""
    if not ALLOW_FILE.exists():
        return {}
    out = {}
    for line in ALLOW_FILE.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        parts = line.split()
        name = parts[0]
        fp = None
        for p in parts[1:]:
            if p.startswith("fp="):
                fp = p[len("fp="):]
        out[name] = fp
    return out


# ── main ───────────────────────────────────────────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser(description="Compiling PR gate for the plugin node repos.")
    ap.add_argument("--refs", help="directory of framework ref assemblies (CI); "
                                    "else auto-discover ../MeshWeaver built src/*/bin")
    ap.add_argument("--image", action="store_true",
                    help="compile against the assemblies from the PLATFORM IMAGE (what CI uses and "
                         "what actually ships), cached per digest under ~/.cache/meshweaver/refs/ — see fetch-refs.py")
    ap.add_argument("--gen-allow", action="store_true",
                    help="regenerate scripts/compile-check.allow from the current failures")
    args = ap.parse_args()

    refs_arg = args.refs
    if args.image:
        if args.refs:
            print("error: --image and --refs name two different reference sets — pass one")
            return 1
        import importlib.util
        spec = importlib.util.spec_from_file_location("fetch_refs", SCRIPTS / "fetch-refs.py")
        fetch_refs = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(fetch_refs)
        refs_arg = str(fetch_refs.resolve(quiet=False))

    refs, ref_roots = discover_refs(refs_arg)
    _report_framework_provenance(refs, ref_roots)
    if not refs:
        # The image is the ZERO-BUILD path and the one CI uses; say so here rather than leaving
        # "build ../MeshWeaver" (11 minutes) as the only visible answer.
        print("error: no framework ref assemblies found.\n"
              "  python3 scripts/compile-check.py --image   # take them from the platform image "
              "(cached under ~/.cache/meshweaver/refs/, pulls once per pin)\n"
              "  …or pass --refs <dir>, or build a core checkout next to this repo.")
        return 1
    # Supply the Microsoft.Extensions.AI assemblies (mesh-provided at runtime, not in src/*/bin) so
    # AI-using sets compile for real instead of being masked as UNVERIFIABLE. If they genuinely can't
    # be found, ai_available stays False and those sets fall back to UNVERIFIABLE (AI-only errors).
    ai_refs = discover_ai_refs(ref_roots)
    refs.update(ai_refs)            # AI names aren't in the core src set, so this never clobbers
    ai_available = bool(ai_refs)
    ref_xml = "\n".join(
        f'    <Reference Include="{os.path.splitext(n)[0]}"><HintPath>{p}</HintPath></Reference>'
        for n, p in sorted(refs.items()))

    # The refs dir carrying the platform's shared frameworks (System.Private.CoreLib present)
    # switches every set to implementation-framework mode — see build_csproj.
    impl_frameworks = "System.Private.CoreLib.dll" in refs
    if impl_frameworks:
        print("mode: IMPLEMENTATION frameworks — compiling against the image's own assemblies "
              "(no SDK ref pack), exactly as the mesh compiles a NodeType")

    types = discover_nodetypes(ROOT)
    # Group by (resolved source set, configuration lambda). The lambda is part of the KEY, not just
    # of the payload: two NodeTypes can share a source set while carrying different lambdas, and one
    # compile can only prove the lambda it actually contains. Types that share BOTH still collapse to
    # a single build, so the grouping keeps its speed-up.
    sets = {}                       # (frozenset, lambda|None) -> list of node-path str
    node_set = {}                   # node-path str -> frozenset
    empty_declared = {}             # node-path str -> reason (declares/expects sources but got 0 .cs)
    for json_path, node in types:
        s = resolve_sources(json_path, node, ROOT)
        nd = node_dir(json_path)
        name = nd.relative_to(ROOT).as_posix()
        repo = json_path.relative_to(ROOT).parts[0]
        node_set[name] = s
        lam = config_lambda(node)
        # A lambda is compiled even with no .cs of its own — the mesh generates ConfigureHub either
        # way, so a source-less NodeType's configuration is still code that has to type-check.
        if s or lam:
            sets.setdefault((s, lam), []).append(name)
        if not s:
            # No .cs resolved. Distinguish a genuinely source-less type (a pure empty-record
            # dimension: no `sources` field AND no Source folder — correctly skipped) from a type
            # that DECLARES/expects sources but resolves to nothing (a typo'd `shared=@…` path or a
            # missing/empty Source folder — a real hole the mesh compile would hit).
            content = node.get("content") or {}
            declared = content.get("sources")
            own_source = (nd / "Source").is_dir()
            root_source = (ROOT / repo / "Source").is_dir()
            if declared:
                empty_declared[name] = ("declares a `sources` list that resolves to no .cs files — "
                                        "check for a typo'd shared=@ path or a missing Source folder")
            elif own_source or root_source:
                empty_declared[name] = "has a Source/ folder that resolves to zero .cs files"
            # else: no sources declared and no Source folder → legitimately source-less; skip.

    import tempfile
    work = Path(tempfile.mkdtemp(prefix="compile-check-"))
    (work / "GlobalUsings.cs").write_text("", encoding="utf-8")

    with_lambda = sum(1 for (_, lam) in sets if lam)
    print(f"compile-check: {len(types)} NodeType(s), {len(sets)} distinct source set(s) "
          f"({with_lambda} with a configuration lambda), {len(refs)} ref assemblies\n")

    # compile each distinct set; map result back to every node sharing it
    result = {}                     # node-path -> ("ok"|"fail"|"unverifiable", [errors])
    restored = False
    import time
    t0 = time.time()
    for i, ((s, lam), names) in enumerate(
            sorted(sets.items(), key=lambda kv: sorted(kv[1])[0]), 1):
        errors, needs_external, infra_error = compile_set(
            work, s, ref_xml, restored, ai_available, lambda_src=lam,
            impl_frameworks=impl_frameworks)
        restored = True
        if infra_error:
            # nonzero build with no CS diagnostic (restore/SDK/MSBuild) — a real, gating failure.
            status, errors = "fail", [infra_error]
        elif not errors:
            status = "ok"
        elif needs_external and all(_is_ai_error(e) for e in errors):
            # AI dlls not locatable AND every diagnostic is attributable to the missing AI types.
            status = "unverifiable"
        else:
            # any error (incl. a non-AI error in an AI-using set) is a genuine compile failure.
            status = "fail"
        for name in names:
            result[name] = (status, errors)
        tag = {"ok": "OK", "fail": "FAIL", "unverifiable": "UNVERIFIABLE"}[status]
        lead = sorted(names)[0]
        extra = f"  (+{len(names)-1} sharing)" if len(names) > 1 else ""
        print(f"  [{i}/{len(sets)}] {tag:12} {lead}{extra}"
              + (f"    {errors[0]}" if errors else ""))
    elapsed = time.time() - t0

    # NodeTypes that declare/expect sources but resolved to zero .cs — a real hole (never compiled),
    # so they FAIL the gate (subject to the same allowlist ratchet as compile failures).
    for name, reason in sorted(empty_declared.items()):
        result[name] = ("fail", [f"no resolvable source: {reason}"])
        print(f"  [--] {'FAIL':12} {name}    no resolvable source: {reason}")

    allow = read_allow()

    # ── gate evaluation ──
    clean = [n for n, (st, _) in result.items() if st == "ok"]
    unverifiable = [n for n, (st, _) in result.items() if st == "unverifiable"]
    failing = {n for n, (st, _) in result.items() if st == "fail"}

    if args.gen_allow:
        lines = [
            "# compile-check.allow — the compile-debt backlog (RATCHET: must only ever SHRINK).",
            "#",
            "# Each NodeType listed here fails to compile against the current core. They are",
            "# grandfathered so the gate can go green on the existing tree, but the ratchet is",
            "# one-way: a NEW non-allowlisted compile failure FAILS the PR, and fixing an",
            "# allowlisted type means REMOVING its line here (the gate FAILS if an allowlisted",
            "# type now compiles CLEAN). Debt only shrinks, never silently lingers.",
            "#",
            "# The obligation these encode is AGENTS.md's 'Reactive, never async' rule plus the",
            "# node-native self-containment rule (Source must compile from core framework types",
            "# only). Two known debt categories seed this file:",
            "#   * UWDeepfield async-port debt — any UWDeepfield type whose Source still uses",
            "#     async/await/Task in hub- or view-reachable code (the 2026-07-21 two-mesh park).",
            "#   * vendored-framework symbol collision — a plugin that VENDORS a core framework's",
            "#     source (BusinessRules, awaiting the core injection seam, PR #54) collides with",
            "#     that framework's dll when compiled against the FULL local core; it compiles on",
            "#     the mesh (which does not load that assembly). Remove once core ships the seam.",
            "#",
            "# Each entry pins a FINGERPRINT of the expected failure (`fp=<codes>` — the sorted",
            "# multiset of error codes). A name-only allowlist would let NEW debt grow inside an",
            "# already-listed type; the fingerprint fails the gate if the type's CURRENT failure",
            "# differs from the recorded one (a new or changed error), even while it stays listed.",
            "#",
            "# Generated by: python3 scripts/compile-check.py --gen-allow",
            "#",
        ]
        for n in sorted(failing):
            errs = result[n][1]
            err = errs[0] if errs else "(no CS error captured)"
            fp = failure_fingerprint(errs)
            lines.append(f"{n}  fp={fp}  # {err}")
        ALLOW_FILE.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"\nwrote {ALLOW_FILE.relative_to(ROOT)} with {len(failing)} known-broken type(s).")
        return 0

    allow_names = set(allow)
    new_breaks = sorted(failing - allow_names)
    # allowlisted-and-still-failing: split into genuine known-debt (fingerprint matches) vs.
    # fingerprint-drift (allowlisted, but a NEW/changed error the entry does not cover → gate FAIL).
    known_debt, fp_drift = [], []
    for n in sorted(failing & allow_names):
        cur_fp = failure_fingerprint(result[n][1])
        exp_fp = allow[n]
        if exp_fp is not None and cur_fp != exp_fp:
            fp_drift.append((n, exp_fp, cur_fp))
        else:
            known_debt.append(n)
    stale_allow = sorted(n for n in allow_names if result.get(n, (None,))[0] == "ok")

    print(f"\n── summary ──  {len(clean)} clean · {len(known_debt)} known-debt · "
          f"{len(new_breaks)} NEW break(s) · {len(fp_drift)} fingerprint-drift · "
          f"{len(stale_allow)} stale-allow · {len(unverifiable)} unverifiable   ({elapsed:.0f}s)")

    gate_fail = False

    if new_breaks:
        gate_fail = True
        print(f"\n✗ {len(new_breaks)} NON-allowlisted NodeType(s) FAIL to compile (regression):")
        for n in new_breaks:
            print(f"  - {n}\n      {result[n][1][0] if result[n][1] else '(no CS error captured)'}")

    if fp_drift:
        gate_fail = True
        print(f"\n✗ {len(fp_drift)} allowlisted type(s) have a CHANGED failure fingerprint "
              f"(new/uncovered error — allowlist does not cover it):")
        for n, exp_fp, cur_fp in fp_drift:
            print(f"  - {n}: allow pins fp={exp_fp} but now fp={cur_fp}")
            print(f"      {result[n][1][0] if result[n][1] else ''}")
            print(f"      fix the new error, or re-baseline with --gen-allow if it is expected debt")

    if stale_allow:
        gate_fail = True
        print(f"\n✗ {len(stale_allow)} allowlisted type(s) now COMPILE CLEAN — remove from "
              f"{ALLOW_FILE.name} (ratchet):")
        for n in stale_allow:
            print(f"  - remove {n} from compile-check.allow — it compiles now")

    if known_debt:
        print(f"\n⚠ {len(known_debt)} known-debt type(s) still failing (allowlisted, not gating):")
        for n in known_debt:
            print(f"  - {n}: {result[n][1][0] if result[n][1] else ''}")

    if unverifiable:
        print(f"\n? {len(unverifiable)} UNVERIFIABLE (needs Microsoft.Extensions.AI, absent locally):")
        for n in unverifiable:
            print(f"  - {n}")

    # allowlisted types that vanished from discovery (renamed/removed) — warn, don't gate
    ghosts = sorted(n for n in allow if n not in node_set)
    if ghosts:
        print(f"\n⚠ {len(ghosts)} allowlisted type(s) no longer exist (clean up allow): "
              + ", ".join(ghosts))

    if not gate_fail:
        print(f"\n✓ compile gate GREEN: {len(clean)} clean, {len(known_debt)} known-debt "
              f"(shrinking), no new breaks.")
    return 1 if gate_fail else 0


if __name__ == "__main__":
    sys.exit(main())
