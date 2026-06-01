---
Name: Static Node Providers
Description: "How the IStaticNodeProvider pattern surfaces non-persisted built-in MeshNodes, and why the old MeshConfiguration.Nodes dictionary was removed."
---

# Static Node Providers

The mesh ships with a stable set of built-in `MeshNode`s — NodeType definitions (`Markdown`, `Agent`, `Code`, …), platform agents, language-model definitions, embedded documentation, partition meta-nodes. None of these live in persistence. They are declared at configuration time and surfaced uniformly through a single, lightweight abstraction: `IStaticNodeProvider`.

## The contract

```csharp
public interface IStaticNodeProvider
{
    IEnumerable<MeshNode> GetStaticNodes();
}
```

Every provider is registered as a DI singleton. The mesh runtime enumerates all registered providers on demand through two extension methods in `MeshWeaver.Mesh.Services.StaticNodeProviderExtensions`:

```csharp
// Resolve a specific static node by path
var node = serviceProvider.FindStaticNode("Markdown");

// Walk every registered static node across all providers
foreach (var node in serviceProvider.EnumerateStaticNodes()) { … }
```

> **These are the only way application code should read static nodes.** There is no `MeshConfiguration.Nodes` dictionary or any other central registry to reach into.

## Why the central dictionary was removed

`MeshConfiguration` used to carry an `IReadOnlyDictionary<string, MeshNode> Nodes` populated by `MeshBuilder.AddMeshNodes(...)`, with a `GroupBy(Path).Last()` de-dup rule baked in. That dictionary has been removed for three interconnected reasons:

| Problem | Consequence |
|---|---|
| **Duplicated the provider abstraction** | `IStaticNodeProvider` already offered "find by path" and "iterate all". The dictionary was a parallel pipe that some sources fed (`AddMeshNodes`) and others never did (`BuiltInAgentProvider`, `DefaultPartitionProvider`). Consumers reading only the dictionary saw half the nodes. |
| **Ambiguous de-dup semantics** | `GroupBy(Path).Last()` imposed a silent "last-write-wins" ordering on all callers. Per-provider iteration lets each provider own its ordering; de-dup happens exactly once, at the call site that needs it. |
| **Coupled config to a runtime-mutable concept** | Tests that need different static nodes could not easily swap a dictionary entry; they *can* register an additional `IStaticNodeProvider`. |

## How `AddMeshNodes` still works

`MeshBuilder.AddMeshNodes(params MeshNode[])` is unchanged — all existing call sites keep compiling. At `MeshBuilder.Build` time, the accumulated list is wrapped in a `StaticMeshNodeListProvider` and registered as an `IStaticNodeProvider`:

```csharp
.AddSingleton<IStaticNodeProvider>(new StaticMeshNodeListProvider(MeshNodes))
```

`StaticMeshNodeListProvider.GetStaticNodes()` applies the same `GroupBy(Path).Last()` de-dup the old dictionary used at build time. The semantic is preserved — just deferred to iteration.

## Adding a new built-in node

Two paths are available, and the right choice depends on complexity:

**Option A — quick, for a single node.** Call `builder.AddMeshNodes(myNode)` from an extension method. The node flows through the list provider automatically and requires no extra class.

**Option B — explicit provider (preferred for any non-trivial set).**

```csharp
public sealed class MyBuiltInsProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return new MeshNode("ThingOne") { … };
        yield return new MeshNode("ThingTwo") { … };
    }
}

// Register alongside other singletons
services.AddSingleton<IStaticNodeProvider, MyBuiltInsProvider>();
```

Use option B whenever the nodes depend on configuration, embedded resources, or constructor parameters. Option A is fine for stateless, one-off registrations from a `NodeType.Add…Type()` extension method.

## What happens when a node type is missing

When a `MeshNode` references `nodeType = "X"` and no provider returns a node at path `X`, `EnrichWithNodeType` runs a 3-second existence probe (`path:X` against `IMeshQueryCore`). If the probe returns empty, activation fails fast with a clear error overlay:

> NodeType 'X' is not registered (referenced by instance '&lt;path&gt;').  
> Either register the type via `AddXxxType()` in your mesh builder, or fix the instance's `NodeType` field. Activation cannot proceed.

Before this probe existed, the slow path waited the full `SlowPathTimeout = 30s` (stacked to 60s on double-enrichment) for a typeStream emission that would never come. The stuck per-node hub then jammed the routing action block, cascading 10-second timeouts to every other activation posted through the same client.

See `test/MeshWeaver.Persistence.Test/UnregisteredNodeTypeTest.cs` for the regression guard.

## Related

| Symbol | Location |
|---|---|
| `IStaticNodeProvider` | `MeshWeaver.Mesh.Services` |
| `StaticNodeProviderExtensions` | `MeshWeaver.Mesh.Services` — `FindStaticNode` / `EnumerateStaticNodes` helpers |
| `StaticMeshNodeListProvider` | `MeshWeaver.Mesh.Services` — internal wrapper bridging `AddMeshNodes` to the provider model |
| `NodeTypeEnrichmentHelpers` | `MeshWeaver.Graph.Configuration` — existence probe and slow path |
