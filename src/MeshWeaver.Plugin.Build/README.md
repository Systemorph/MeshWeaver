# MeshWeaver.Plugin.Build

Builds a plugin's **in-mesh C#** the way the portal compiles it at runtime — in CI, before it ships.

```bash
dotnet tool install -g MeshWeaver.Plugin.Build
meshweaver-plugin-build ./ThreeBody --framework-version 3.0.0-rc2 --pack ./nupkgs
```

The tool is versioned **in lockstep with the framework** on purpose: it encodes how a given
MeshWeaver version compiles (using set, reference set, closure semantics). A separately-versioned
tool would drift from the runtime it exists to mirror.

## Packaging

`--pack` writes `MeshWeaver.Plugin.<Name>.<Version>.nupkg` containing the plugin's node content,
one prebuilt assembly per unit, and a manifest binding node paths to assemblies. The nuspec is a
projection of the mesh manifest that authors already write — `"requires": ["Store@^1.0.0"]` becomes
`<dependency id="MeshWeaver.Plugin.Store" version="[1.0.0,2.0.0)" />`.

Assemblies go under `meshweaver/assemblies/`, **not** `lib/net10.0/`. Under `lib/` NuGet would
surface every unit as a compile-time reference of any consumer, colliding the duplicate type names
above and unifying CLR identity the runtime deliberately keeps separate. They are payload for the
assembly store, not a reference set.

## Why

Source stored in mesh nodes (`Source/*.cs` under a NodeType) compiles **at runtime, in the portal**
— never in `dotnet build`, never in a test. Across the four plugin repos that is ~700 files in 242
compilation units that no build has ever type-checked. The consequences are routine: a framework
symbol gets deleted, every check is green, and the breakage appears as `CompileError` overlays in
production against code the compiler was never shown.

This tool closes that gap. It resolves each unit's closure from the node's own declared `sources`,
supplies the ambient environment the portal supplies, and compiles.

## What a "unit" is

**A `Source/` directory owned by a NodeType — not a plugin, and not every `Source/` directory.**

UWDeepfield has eleven units: its root plus one per NodeType. They compile *separately* at runtime,
so two may legitimately declare the same type name (`TaskAssignmentService` exists in both
`UwPortfolio/Source` and `UWDeepfieldHome/Source`). Merging a plugin's units into one assembly
yields ~200 spurious `CS0111`s.

Of the 774 `Source/` directories across the four repos, only ~221 are units. The rest are
**shared-source libraries** — `Claims/SampleData/Source` has no node at all and is pulled into its
consumers via `shared=@Claims/SampleData/Source`. Building one standalone reports `CS0246` for every
type it legitimately borrows from the consumer: a false alarm on healthy content, which is worse
than no check. A unit's owner declares `NodeTypeDefinition` or `PluginContent`; anything else
(a Markdown page describing the sample data, a course exercise authored as `index.md`) is skipped.

## The three things a plain `.csproj` gets wrong

1. **Includes have three syntaxes.** `shared=@Store/Coupon/Source` (path),
   `shared=namespace:UWDeepfield/Source scope:subtree` (query), and an *aliased*
   `client=namespace:UWDeepfield/ReinsuranceClient/Source scope:subtree`. Each unmatched form is a
   silent under-resolution — the build then reports a thoroughly convincing `CS0246`/`CS0103` on a
   symbol that does exist.
2. **`Test/` is part of the same assembly.** The live `Store/Plugin` node's `compiledSources` lists
   `Store/Plugin/Test/*` beside its Source. Omit it and production code that references a test type
   (an area rendering its own results, as `IndustryNewsFeed` does) fails `CS0103` in CI while
   compiling perfectly in the portal.
3. **The ambient environment is not implicit.** In-mesh code compiles against every assembly the
   portal has loaded, plus the generated skeleton's using preamble. Omit `MeshWeaver.Domain` and
   `[MeshNode(…)]` binds to the `MeshNode` *record*, giving
   `CS0616 'MeshNode' is not an attribute class`. See `CompilationEnvironment`.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | every unit built (or the plugin is content-only — six of twenty carry no C#) |
| 1 | at least one unit failed; the first five errors and the unit's declared `sources` are printed |
| 2 | bad arguments |

Failure is all-or-nothing on purpose: a partially built plugin is worse than an unbuilt one,
because a consumer resolving a mixed set gets an ABI mismatch that semver says is fine.
