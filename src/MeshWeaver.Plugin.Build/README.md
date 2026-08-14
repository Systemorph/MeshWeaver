# MeshWeaver.Plugin.Build

Builds a plugin's **in-mesh C#** the way the portal compiles it at runtime — in CI, before it ships.

```bash
dotnet tool install -g MeshWeaver.Plugin.Build
meshweaver-plugin-build ./ThreeBody --framework-version 3.0.0-rc2
```

## Why

Source stored in mesh nodes (`Source/*.cs` under a NodeType) compiles **at runtime, in the portal**
— never in `dotnet build`, never in a test. Across the four plugin repos that is ~700 files in 242
compilation units that no build has ever type-checked. The consequences are routine: a framework
symbol gets deleted, every check is green, and the breakage appears as `CompileError` overlays in
production against code the compiler was never shown.

This tool closes that gap. It resolves each unit's closure from the node's own declared `sources`,
supplies the ambient environment the portal supplies, and compiles.

## What a "unit" is

**A `Source/` directory — not a plugin.** UWDeepfield has eleven: its root plus one per NodeType.
They compile *separately* at runtime, so two units may legitimately declare the same type name
(`TaskAssignmentService` exists in both `UwPortfolio/Source` and `UWDeepfieldHome/Source`). Merging
a plugin's units into one assembly yields ~200 spurious `CS0111`s.

## The two things a plain `.csproj` gets wrong

1. **`shared=` has two syntaxes.** `shared=@Store/Coupon/Source` (path) *and*
   `shared=namespace:UWDeepfield/Source scope:subtree` (query). Resolving only the first silently
   drops the include — the build then reports a thoroughly convincing `CS0246` on a symbol that
   does exist.
2. **The ambient environment is not implicit.** In-mesh code compiles against every assembly the
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
