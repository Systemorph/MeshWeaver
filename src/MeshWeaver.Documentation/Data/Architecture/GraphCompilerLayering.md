---
Name: Graph / Compiler Layering
Category: Architecture
Description: Why NodeType compilation lives in four assemblies and not two — the cycle that makes a two-way split impossible, and the full-MVID rule that decides which side of the toolchain the pipeline sits on.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h18M3 12h18M3 17h18M7 3v18"/></svg>
---

# Graph / Compiler Layering

NodeType compilation spans **four** assemblies. Two of the boundaries look like they could be
collapsed and neither can. This page records why, because both were re-derived wrongly at least
once.

```
MeshWeaver.Graph              the graph model: NodeTypes, layout areas, hub composition (AddGraph)
        |
        v
MeshWeaver.Compiler.Pipeline  the MESH-ACTOR half: the compile actor, the assembly cache,
        |                     prebuilt adoption, the park registry, bake status, the LSP service
        v
MeshWeaver.Compiler           the pure TOOLCHAIN: skeleton generation, source-query resolution,
        |                     reference set, generators, Roslyn emit, framework identity
        v
MeshWeaver.Graph.Contract     the vocabulary the model and the pipeline share
```

`MeshWeaver.Compiler` and `MeshWeaver.Graph.Contract` do **not** reference `MeshWeaver.Graph`.

## Why the model and the pipeline cannot simply be two projects

They were mutually recursive. Before the split an **8-file, 11,059-line strongly-connected
component straddled the seam**:

| pipeline side | model side |
|---|---|
| `MeshNodeCompilationService`, `NodeTypeCompilationHelpers`, `NodeTypeBakeStatus`, `NodeTypeContractHandler`, `PrebuiltAssemblySeeder` | `MeshDataSource`, `MeshNodeExtensions`, `MeshNodeTypeSource` |

`MeshDataSource` wires the compile handlers onto every per-node hub
(`.WithHandler<DispatchCompileTrigger>(NodeTypeCompilationHelpers.HandleDispatchCompile)`), and
`NodeTypeCompilationHelpers` called back into `MeshDataSourceExtensions.TryCreateReleaseNode`. In a
strongly-connected component **no assignment of the files to two projects is acyclic** — a cycle
that a namespace tolerates cannot be expressed across an assembly boundary at all.

Taking the transitive closure in either direction confirms it. A valid "compiler" set must be
closed under predecessors one way and successors the other:

| direction | minimal compiler set | what it drags in |
|---|---|---|
| `Compiler -> Graph` | 69 files / 35,011 lines | `CreateLayoutArea`, `MarkdownLayoutAreas`, `SettingsLayoutArea`, `SpaceNodeType` |
| `Graph -> Compiler` | 74 files / 32,520 lines | `SlideNodeType`, `DeckNodeType`, `MeshNodeCardControl`, `NodeIconPickerDialog` |

Neither is a compiler. **`MeshWeaver.Graph.Contract` is the structural answer**: the types both
halves speak are owned by neither, so neither can close a cycle through the other — and, unlike a
closure that merely happens to be acyclic today, that property survives the next commit.

It is a contract, and it has to stay one: the NodeType declaration and its build/release state
(`NodeTypeDefinition`, `BuildState`, `NodeTypeRelease`, `ReleaseArtifact`, `ServedBuildIdentity`),
the node-type name literals the pipeline reads and writes (`GraphNodeTypeNames`), the synced-query
helpers, and `ICompileFailureNotifier`. **No NodeType implementations and no layout areas** — a
contract carrying `SlideNodeType` is not a contract.

## 🚨 Why the pipeline is NOT inside `MeshWeaver.Compiler`

This is the boundary that looks most collapsible and is the most costly to collapse.

`MeshWeaver.Compiler` is a **full-MVID toolchain root** — see
[`FrameworkBuildIdentity.ToolchainRoots`](/Doc/Architecture/ModuleVersioning). For an assembly in
that closure the **whole implementation MVID** feeds the framework build identity, not its public
surface. So a **body-only** change to any file in it — a log line, a renamed local — changes the
identity, and **every NodeType on every mesh re-compiles.** That is deliberate: the toolchain shapes
what gets compiled, so a body change there really can change the output with no API change.

The consequence is a size rule. Issue #1707 factored the toolchain out of `MeshWeaver.Graph`
*precisely* so the full-MVID rule pins a **small, low-churn** assembly.

The compile pipeline is the opposite: it is the highest-churn code in the platform. Folding it into
`MeshWeaver.Compiler` tripled that assembly (5,542 → 18,891 lines) and turned every pipeline commit
into a fleet-wide re-bake. It also measurably widened the closure —
`FrameworkBuildIdentityTest.FullMvidClosure_IsExactly_TheKnownSet` went red, with
`MeshWeaver.Graph.Contract` and `MeshWeaver.Kernel.Hub` newly inside it.

So the pipeline sits **above** the toolchain, in `MeshWeaver.Compiler.Pipeline`, and is
**surface-hashed** exactly as it was while it lived in `MeshWeaver.Graph`. The full-MVID closure is
unchanged by the split.

**The rule to carry forward:** adding a `ProjectReference` to `MeshWeaver.Compiler` — or moving code
into it — is a fleet-wide re-bake decision, not a refactor. `FullMvidClosure_IsExactly_TheKnownSet`
is the gate; when it goes red, the question is whether the toolchain genuinely needs what was just
added, never how to update the expected set.

## Both new assemblies are on the content surface

In-mesh plugin source binds these types — `Store/Publishing/Source/Provisioning.cs` uses
`NodeTypeDefinition` — and in-mesh source may only reference
`FrameworkBuildIdentity.ContentSurfaceAssemblies`. `MeshWeaver.Compiler.Pipeline` and
`MeshWeaver.Graph.Contract` are both on that list (surface-hashed), which is what keeps that source
compiling. `CanonicalList_MatchesTheTesterClosure` computes the list from the csproj graph and fails
naming the drift, so it cannot silently fall out of date.

## Moving a type between these assemblies

Namespaces did **not** change in the split — `MeshWeaver.Graph` and
`MeshWeaver.Graph.Configuration` still name types in all four assemblies. That is intentional twice
over: no in-mesh source or sibling repo sees an API change, and **a type forwarder cannot rename**.

Any public type that changes assembly needs `[assembly: TypeForwardedTo(typeof(…))]` left behind in
its original one. A module published earlier binds `MeshWeaver.Graph!<name>` by assembly-qualified
name; without the forwarder it dies with `TypeLoadException` on the first portal that adopts the new
platform (#2370 took down a production `/mcp` surface exactly this way). The split left **42**
forwarders in `src/MeshWeaver.Graph/TypeForwarders.cs`. Use a **forwarder, never a shim** — a
forwarder keeps one type identity, so `is`/`as`, serialization and reference equality survive the
boundary; a re-declared compatibility type mints a second identity.

`scripts/check-type-forwards.py --base <merge-base>` is the gate, and it runs locally.

## Where the tests live

Each suite sits beside its subject: `MeshWeaver.Compiler.Pipeline.Test` (the pipeline and the
toolchain) and `MeshWeaver.Graph.Test` (the model, the layout areas, hub composition). Assignment is
by **subject**, not by whether a test spins up a mesh — splitting unit from integration would have
put `FrameworkBuildIdentityTest` further from `FrameworkBuildIdentity`, not closer.
