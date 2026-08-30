# MeshWeaver.Compiler.Cli (`mw-compiler` / `mw-plugin-test`)

The MeshWeaver content compiler CLI — the same binary the platform CI runs to gate and bake mesh
content (#1707). It stages content from a git checkout, compiles NodeTypes with the
`MeshWeaver.Compiler` toolchain (the identical code path the portals use at runtime), and — with
`--bake-output` — emits prebuilt-assembly bundles keyed by the framework build identity, so
portals adopt instead of recompiling.

Distributed two ways, same binary:

- **dotnet tool** — `dotnet tool install -g MeshWeaver.Compiler.Cli` (command: `mw-compiler`; use
  `--tool-path`/`--local` for CI-scoped installs), for satellite content repos' CI lanes: install
  the version matching your platform, run the bake, no platform-repo artifact download.
- **container image** — the `mw-plugin-test` image the plugin gates run.

Useful entry points:

```
mw-compiler compile <root> --output <dir>   # BAKE: build-step compile, NO mesh (#1763)
mw-compiler <checkout-root>                 # GATE: mesh run — render + Tests areas
mw-compiler <root> --bake-output <dir>      # legacy: the gate's mesh ALSO produces the bundles
mw-compiler --print-framework-identity      # one-line identity + provenance diagnostic
mw-compiler build <root> [<pkg>...|all]     # BUILD: compile + test per package, dependency cascade
mw-compiler build-project <csproj|dir>     # BUILD A .csproj: no dotnet SDK, no NuGet restore
```

## `build-project` — compile a `.csproj` with NO SDK and NO NuGet (2026-08-31)

> *"The platform builds dll completely without any external dotnet kit or nuget."* — maintainer,
> 2026-08-30

```
mw-plugin-test build-project <csproj|dir> [--output <dir>] [--app <dir>] [--extra-refs <dir>]... \
    [--generators <dir|dll>]... [--razor-generators <dir>] \
    [--accept <construct>]... [-p:Name=Value]... [--allow-warnings] [--max-parallel <n>]
```

Runs INSIDE the image (`memex build project --image …` is the trip in). Three parts:

- **`ProjectFile`** evaluates the `.csproj` without MSBuild — properties, items, the default
  `**/*.cs` glob minus `bin`/`obj`, `Compile Include/Remove`, `ProjectReference`,
  `PackageReference`, implicit usings, the target-framework symbol ladder, the nearest
  `Directory.Build.props` / `.targets` / `Directory.Packages.props`, and every `<Import>` whose
  condition holds, plus every **`<EmbeddedResource>`** with the manifest NAME the SDK would give it.
  🚨 **Anything it cannot reproduce FAILS the load by name** — an unknown element or item type, a
  `Condition` outside its grammar, an `<Import>` of a missing file (MSB4019's behaviour,
  deliberately), a `<Target>`, a resource construct whose name cannot be matched exactly.
  `--accept <construct>` acknowledges one. The alternative is worse than no build: a dropped
  `Nullable`, `NoWarn` or `DefineConstants` produces a green build that is not the build the SDK
  would have produced.
- **`ContainerReferenceSet`** reads `/app`, the image's own `*.deps.json` and the shared frameworks
  installed in the container. The C# port of `MeshWeaver.Plugins/scripts/container-refs.py`, read
  from disk instead of an extracted image. 🚨 **Fails closed** on an unreadable `/app`, a missing or
  ambiguous `.deps.json`, or MeshWeaver assemblies that disagree on their binding identity. 🚨 **A
  package is matched by the ASSEMBLY FILE on disk, never by its id alone** — a metapackage whose
  version the image records but whose assembly is not there is NOT supplied. A package the container
  does not supply is an ADDITIONAL library, reported by name; `--extra-refs` is the only way in.
- **`ProjectBuild`** sequences the `ProjectReference` graph on the same `Cascade` the `build` verb
  uses (a cycle is refused up front and named), compiles with Roslyn, and emits through the
  platform's own `EmitPipeline` — the verified emit-to-memory-then-write, so the file on disk is
  provably the image that was emitted (#1412). 🚨 It builds its **own** `CSharpCompilationOptions`
  and never touches `EmitPipeline.CreateCompilationOptions`, which feeds
  `GeneratedInputIdentity.OptionsFingerprint` — the key every cached NodeType assembly is filed
  under.

**A `ProjectReference` inside the SOURCE ROOT** (the nearest `Directory.Build.props` ancestor of the
entry project) is built from source, in dependency order. **Outside it, the container supplies the
assembly** — which is exactly the `$(MeshWeaverRoot)/src/…` shape every `MeshWeaver.Plugins/src`
project carries, and it resolves to the assembly the image ships rather than to a checkout.

### 🔴 The emitted assembly carries the SDK's binding identity

`GenerateAssemblyInfo` is a TARGET, and this builder runs no targets — so it emitted Roslyn's own
default, **`AssemblyVersion=0.0.0.0`**, while the whole fleet binds `3.0.0.0`. Nothing went red: the
compile was green, the DLL loaded, and the failure would have arrived in a different repo, at
runtime, as `FileNotFoundException: Could not load file or assembly '…, Version=3.0.0.0'` — the
shape of Systemorph/MeshWeaver#143, which CrashLoopBackOff'd a migration.

The identity is now **evaluated** from the project and synthesized into the compilation as one more
source document, exactly as the SDK's generated `<Project>.AssemblyInfo.cs` is one more Compile
item: `AssemblyVersion` (explicit, else the numeric core of `$(Version)` padded to four fields,
which is what the SDK's `GetAssemblyVersion` task does), `FileVersion` (following `AssemblyVersion`,
never `$(Version)`), `InformationalVersion` (following `$(Version)`), the descriptive attributes
with the SDK's `$(AssemblyName)` → `$(Authors)` → `$(Company)` fallback chain, and the
`InternalsVisibleTo` / `AssemblyMetadata` / `AssemblyAttribute` items — all of which this evaluator
used to drop as "metadata that changes nothing", true of the COMPILE and false of the ASSEMBLY.
`GenerateAssemblyInfo=false` synthesizes nothing (the project supplies its own; a second set is
CS0579), and each `Generate…Attribute` switch is honoured individually.

🚨 **A property it cannot derive is a NAMED failure, never a plausible substitute** — an
unparseable `$(Version)`, or `PublishRepositoryUrl=true` with no `$(RepositoryUrl)` (the SDK reads
that one off the git remote).

**Two things it deliberately does not reproduce, and both are said out loud:**

- **`$(SourceRevisionId)`** — the SDK appends `+<sha>` to `InformationalVersion` from git; there is
  no git here, so every build reports the absent suffix by name. `-p:SourceRevisionId=<sha>` gives
  exact parity. `-p:Name=Value` (or `--property Name=Value`) is MSBuild's global property, and it is
  immutable during evaluation exactly as MSBuild makes it: a `<PropertyGroup>` cannot overwrite one.
- **`TargetFrameworkAttribute`** — written by a different target
  (`GenerateTargetFrameworkMonikerAttribute`) with its own switches and its own
  `TargetPlatform`/`SupportedOSPlatform` companions for a platform-suffixed TFM this evaluator
  cannot compute. Emitting half of that set would be worse than the gap.

Measured, not assumed: every rule above was produced by building the same project with SDK 10.0.400
and reading the generated `*.AssemblyInfo.cs`, and the container output for
`MeshWeaver.Plugins/src/MeshWeaver.Speech.Contract` was compared attribute-for-attribute against a
real `dotnet build` of it — identical but for `TargetFrameworkAttribute`.

**The diagnostic standard is the SDK's.** Nullable analysis follows the project;
`DocumentationMode.Diagnose` is ALWAYS on so doc-QUALITY defects surface (CS1574, CS0419, CS1570),
while the doc-COMPLETENESS family (CS1591/CS1573/CS1712) is suppressed exactly when the project asked
for no doc file — which is when csc would not raise it either. The SDK's default `NoWarn`
(`1701;1702`) is seeded first. **Warnings fail the build**; `--allow-warnings` opts out.

**Everything streams.** Every progress line and every diagnostic is appended to an `ActivityLog`
(`ActivityCategory.Compilation`) and pushed to the caller's observer the moment it is produced; the
console is a rendering of that stream, which is why nothing is batched to the end.

**Razor/Blazor compiles** (2026-08-31). `.razor` and `.cshtml` are turned into C# by the SDK's
Roslyn **source generator**, which the image now ships in `razor-generators/<rid>/` beside the
builder: `Microsoft.CodeAnalysis.Razor.Compiler.dll` + `Microsoft.AspNetCore.Razor.Utilities.Shared.dll`
— the measured closure, everything else the compiler references binds to the host.

- 🚨 **Per RID.** The SDK crossgens its Razor compiler for the SDK's own runtime identifier, so ONE
  copy cannot serve a multi-arch image: the same 10.0.400 file carries PE machine `0xFD1D` on
  linux-x64 and `0xD11D` on linux-arm64, and the wrong one throws `BadImageFormatException`. CD
  stages both; `--razor-generators <dir>` names another copy and REPLACES the search rather than
  heading it.
- 🚨 **Loaded into a host-first load context.** The generator is built against the SDK's Roslyn
  (10.0.400's wants `Microsoft.CodeAnalysis` 5.9.0.0) and the image carries this repo's pin (5.6.0);
  the default context refuses the lower version, so every assembly the host already has is bound to
  the host's copy with the version ignored — which is also the only way the generator and the driver
  agree on the identity of `ISourceGenerator`.
- **Refused by name rather than skipped:** a project with Razor files and no generator (it says what
  the CS0115 wall would have been); a generator that runs and emits nothing; `*.razor.css` CSS
  isolation, whose `b-…` scope comes from an MSBuild task this builder does not run
  (`--accept razor-css-scope` builds without it); and `.razor` files under a project whose `Sdk`
  does not process them (`--accept razor-not-compiled`).

**Still not supported, and each says so:** SDK source generators (they live in the SDK, not the
runtime; `--generators` supplies them), embedded resources, `<Protobuf>`, MSBuild `<Target>`s and
`<Sdk>` elements. Full measurements:
**Not supported, and each says so:** Razor/Blazor compilation (needs
`Microsoft.CodeAnalysis.Razor.Compiler`, `Microsoft.AspNetCore.Razor.Utilities.Shared` and
`Microsoft.Extensions.ObjectPool` in the image — it ships none), SDK source generators (they live in
the SDK, not the runtime; `--generators` supplies them), `<Protobuf>`, MSBuild `<Target>`s and
`<Sdk>` elements. **Embedded resources ARE supported** — under the SDK's own manifest names, every
rule established by building probe projects with the real SDK and reading the names back out of the
emitted PE; `.resx`, a culture in a file name (which the SDK routes to a SATELLITE assembly),
`DependentUpon` and `ManifestResourceName` are each refused BY NAME rather than guessed, because a
plausible-looking wrong resource name is the one defect nothing downstream can see. Full
measurements:
[In-Mesh Build and Test](https://github.com/Systemorph/MeshWeaver/blob/main/src/MeshWeaver.Documentation/Data/Architecture/InMeshBuildAndTest.md).

## `build` — compile AND test, per package, as a dependency cascade (2026-08-30)

`build` is the build process for node repos. **Build always means compile and run tests.** The
input is one or many packages, or `all` (the default) for the full rebuild a platform rebuild
needs; the selection is the named packages plus their transitive `requires` inside the repo.

```
mw-compiler build <repo-root> [<package>... | all] [--module <dll>]... [--out <dir>] \
    [--report <file>] [--max-parallel <n>] [--case-timeout <s>] [--no-tests] [--source-sha <sha>]
```

- **A cascade, not a schedule.** Every package has a result stream; a package OBSERVES the
  streams of the packages it requires and starts itself the moment the last one completes green.
  Packages with no edge between them build at the same time (bounded by `--max-parallel`).
  **On red we break**: a package whose dependency did not end green never starts and is reported
  `blocked by <dependency>`; **on green we continue**. A failure is reported once, where it
  happened. A cycle is refused up front and named.
- **No mesh, no import.** Sources come from the checkout on disk (the same node loader as
  `compile`), composed exactly as the portal composes a NodeType compile (`NodeSetCompiler`: the
  same skeleton, the same options, this image's `/app` as the reference set) — and each package
  compiles against the assemblies its dependency packages just emitted. Grains cannot carry a
  Roslyn workload; a build process can.
- **Tests without a mesh.** The `Test/*.cs` convention — static classes whose public static
  parameterless methods throw on failure — runs straight from the emitted assembly in a
  collectible load context, each case timed and capped by `--case-timeout`. A case that takes a
  host (a `Tests` layout-area aggregator, anything needing a hub) is COUNTED and NAMED as
  `needs-mesh`, never dropped: the gate (`mw-compiler <root> --seed <out>`) still runs those,
  seeded from `--out` so nothing is compiled twice.
- **Timings are the point.** Every package reports ready / queued / work, its compile and test
  splits and per-type compile times; the summary prints the critical path (the chain whose serial
  length is the wall-clock floor) and the parallel speed-up. `--report` writes all of it as JSON.
- **Parity flag.** The portal reaches other packages' types by `shared=` source inclusion, never
  by referencing their emitted assemblies. A type whose emitted assembly turns out to BIND a
  dependency package's assembly is therefore green here on grounds the portal does not have; the
  report marks it `binds-dependency-assembly` so that difference is visible, not discovered as a
  CompileError in production.

Exit codes: `0` every selected package green · `1` any red or blocked · `2` usage · `70` fatal.

## `compile` — the bake, as a build step

`compile` is the compiler-driven bake (#1763). It reads the node files out of the checkout,
resolves each NodeType's `Sources`/`Tests` queries and `@@` includes against that in-memory node
set, compiles with the `MeshWeaver.Compiler` toolchain, emits **DLL + PDB**, and writes the same
`<package>.zip` bundles + `framework-mvid.txt` as before. **No `MeshBuilder`, no `AddGraph()`, no
content import, no hub.** Consumers cannot tell which producer wrote a bundle — and must not.

```
mw-compiler compile <checkout-root> --output <dir> [--source-sha <sha>]
```

Everything else in this binary is the **gate**, which legitimately stands up a mesh: rendering a
layout area and executing a `Tests` area are runtime behaviours; producing an assembly is not. The
gate's own `--bake-output` still works, so lanes can migrate one at a time.

🚨 The two bakes are held equivalent by `BakeEquivalenceTest`
(`test/MeshWeaver.PluginTester.Test`), which bakes one content set BOTH ways and compares the
resolved source sets, the per-type dependency records, the framework identity and the emitted
assemblies' surface. A baker that resolves sources differently fails NOTHING until a page renders
empty in production, so that test — not the speed — is the point. See
`Doc/Architecture/CiContentBake` → "BAKE is a build step; GATE is a mesh run that CONSUMES one".

The framework build identity resolves from the `meshweaver-surface.manifest` packed beside the
binaries — equal by construction with the portals' identity (see
`Doc/Architecture/CiContentBake`), which is what makes the bundles this tool produces adoptable.

## Exit codes — and why nothing may throw out of `Main`

| code | meaning |
|---|---|
| `0` | green |
| `1` | the gate ran and something failed (or an allow entry went stale) |
| `2` | bad usage / bad configuration — an unknown argument, an option with no value, an `--allow` path that does not exist or does not parse |
| `70` | an unanticipated failure; the whole exception is printed above it |

🚨 **An exception must never escape `Main` (#1741).** Every consumer runs this binary as a
container's PID 1 (`docker run … --entrypoint /app/mw-plugin-test`). There, an unhandled exception
does not end the process — the runtime prints the trace and calls `abort()`, whose SIGABRT the
kernel **discards** for a PID-namespace init with the default disposition (`SIGNAL_UNKILLABLE`);
`abort()` falls through to its trap instruction, the runtime's SIGTRAP handler returns to the
instruction that trapped, and the main thread re-traps forever. Measured 2026-08-17: two containers
"Up" 36 and 57 minutes at ~100% CPU each, whose entire output was one `FileNotFoundException`
printed in their first second. On CI that reads as a **hang**, burning the job's whole timeout and
reporting nothing about the bad argument that caused it.

So every failure becomes a message plus an exit code, `Program.cs` carries a top-level guard for the
ones nobody anticipated, and the workflows pass **`docker run --init`** — which covers what a
`catch` cannot reach (a stack overflow, an OOM abort, an unhandled throw on a background thread).
`StartupFailureProcessTest` pins all of this on the real process, with a bounded wait: an unbounded
one would reproduce the bug instead of catching it.

## `--allow` — a MISSING ratchet is a configuration error, not an empty one

`--allow <file>` names the known-debt ratchet. A path that does not exist is refused (exit `2`) with
one actionable line rather than read as an empty list. Substituting an empty list would be the
*stricter* verdict — with no entries every failure is a new failure, so it could never turn a red run
green — but it would make the gate's own configuration unverifiable: `known-debt allowlist: 0
entr(ies)` would then mean either "the ratchet is empty" or "the gate never found the ratchet you
passed", and nothing in the run could tell those apart. That is the shape the node-repo CI policy
exists to forbid: *a gate that cannot read its input must not look like a gate that passed.*

An empty ratchet has two honest spellings — an **empty file** at that path, or **omitting `--allow`**
(which the reusable `node-repo-gate.yml` already does when a repo configures no allow file).
