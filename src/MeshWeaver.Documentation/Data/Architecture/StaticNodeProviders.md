---
Name: Static Node Providers
Description: "How the IStaticNodeProvider pattern surfaces non-persisted built-in MeshNodes, and why the old MeshConfiguration.Nodes dictionary was removed."
---

# Static Node Providers

The mesh ships with a stable set of built-in `MeshNode`s — NodeType definitions (`Markdown`, `Agent`, `Code`, …), platform agents, language-model definitions, embedded documentation, partition meta-nodes. None of these live in persistence. They are declared at configuration time and surfaced uniformly through a single, lightweight abstraction: `IStaticNodeProvider`.

<svg viewBox="0 0 760 300" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
<defs>
<marker id="arr" markerWidth="8" markerHeight="6" refX="7" refY="3" orient="auto">
<polygon points="0 0,8 3,0 6" fill="#90a4ae"/>
</marker>
</defs>
<rect x="0" y="0" width="760" height="300" rx="12" fill="#1a1f2e" opacity="1"/>
<text x="380" y="26" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#cfd8dc" opacity="0.85">Static Node Provider Architecture</text>
<rect x="20" y="48" width="160" height="54" rx="10" fill="#1e88e5"/>
<text x="100" y="70" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">StaticMeshNode</text>
<text x="100" y="86" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">ListProvider</text>
<text x="100" y="100" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#bbdefb">(AddMeshNodes)</text>
<rect x="20" y="118" width="160" height="54" rx="10" fill="#43a047"/>
<text x="100" y="140" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">BuiltInAgent</text>
<text x="100" y="156" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">Provider</text>
<text x="100" y="170" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c8e6c9">(platform agents)</text>
<rect x="20" y="188" width="160" height="54" rx="10" fill="#8e24aa"/>
<text x="100" y="210" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">DefaultPartition</text>
<text x="100" y="226" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">Provider</text>
<text x="100" y="240" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#e1bee7">(meta-nodes)</text>
<text x="20" y="275" text-anchor="start" font-family="sans-serif" font-size="10" fill="#90a4ae" opacity="0.7">+ MyBuiltInsProvider, …</text>
<text x="200" y="69" text-anchor="middle" font-family="sans-serif" font-size="20" fill="#90a4ae" opacity="0.6">}</text>
<text x="198" y="146" text-anchor="middle" font-family="sans-serif" font-size="20" fill="#90a4ae" opacity="0.6">}</text>
<text x="198" y="220" text-anchor="middle" font-family="sans-serif" font-size="20" fill="#90a4ae" opacity="0.6">}</text>
<text x="210" y="160" text-anchor="start" font-family="sans-serif" font-size="10" fill="#90a4ae" opacity="0.7">IStaticNodeProvider</text>
<text x="210" y="172" text-anchor="start" font-family="sans-serif" font-size="10" fill="#90a4ae" opacity="0.7">(DI singletons)</text>
<line x1="185" y1="75" x2="295" y2="130" stroke="#90a4ae" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="185" y1="145" x2="295" y2="145" stroke="#90a4ae" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="185" y1="215" x2="295" y2="160" stroke="#90a4ae" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
<rect x="300" y="105" width="180" height="80" rx="10" fill="#f57c00"/>
<text x="390" y="135" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">StaticNodeProvider</text>
<text x="390" y="151" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Extensions</text>
<text x="390" y="168" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#ffe0b2">FindStaticNode / EnumerateStaticNodes</text>
<line x1="484" y1="125" x2="570" y2="90" stroke="#90a4ae" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="484" y1="165" x2="570" y2="200" stroke="#90a4ae" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
<rect x="575" y="55" width="160" height="54" rx="10" fill="#26a69a"/>
<text x="655" y="78" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">EnrichWithNodeType</text>
<text x="655" y="95" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#b2dfdb">(node activation)</text>
<rect x="575" y="170" width="160" height="54" rx="10" fill="#5c6bc0"/>
<text x="655" y="193" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">Application code</text>
<text x="655" y="210" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c5cae9">&amp; other consumers</text>
</svg>
*Multiple `IStaticNodeProvider` singletons are fanned out through two extension-method helpers; all consumers read through these — there is no central dictionary.*

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
