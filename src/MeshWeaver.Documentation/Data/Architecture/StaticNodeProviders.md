# Static Node Providers

The mesh exposes a stable set of "built-in" `MeshNode`s — NodeType definitions
(`Markdown`, `Agent`, `Code`, …), platform agents (`BuiltInAgentProvider`),
language-model definitions (`BuiltInLanguageModelProvider`), embedded
documentation, partition meta-nodes. None of these live in persistence;
they're declared at config time and surfaced uniformly through
`IStaticNodeProvider`.

## The contract

```csharp
public interface IStaticNodeProvider
{
    IEnumerable<MeshNode> GetStaticNodes();
}
```

Every provider is registered as a DI singleton. The mesh runtime enumerates
them on demand:

```csharp
// Find a specific static node by path
var node = serviceProvider.FindStaticNode("Markdown");

// Walk every registered static node
foreach (var node in serviceProvider.EnumerateStaticNodes()) { … }
```

Both extension methods live in
`MeshWeaver.Mesh.Services.StaticNodeProviderExtensions`. They are the
**only** way application code should read static nodes — there is no
`MeshConfiguration.Nodes` dictionary or any other central registry to dip
into.

## Why there is no central dictionary

Historically `MeshConfiguration` carried an
`IReadOnlyDictionary<string, MeshNode> Nodes` populated from
`MeshBuilder.AddMeshNodes(...)` (group-by-path). That dictionary has been
removed. Three reasons:

1. **It duplicated the provider abstraction.** Every consumer eventually
   wanted to iterate "all static nodes" or "find one by path" — the same
   surface `IStaticNodeProvider` already offered. The dictionary was a
   parallel pipe that some sources fed (AddMeshNodes) and others didn't
   (BuiltInAgentProvider, OrganizationNodeProvider). Consumers that read
   only the dictionary missed half the nodes.

2. **The de-dup semantics were ambiguous.** `GroupBy(Path).Last()` baked a
   "last-write-wins" rule into the type, which forced every consumer to
   trust that order. Per-provider iteration lets each provider decide its
   own ordering and a single de-dup happens at the consumption site.

3. **It coupled `MeshConfiguration` to a runtime-mutable concept.** Tests
   that need different static nodes can't easily swap the dictionary; they
   *can* register an additional `IStaticNodeProvider`.

## How `AddMeshNodes` still works

`MeshBuilder.AddMeshNodes(params MeshNode[])` is unchanged — existing call
sites keep compiling. At `MeshBuilder.Build`, the accumulated list is
wrapped in a `StaticMeshNodeListProvider` and registered as an
`IStaticNodeProvider`:

```csharp
.AddSingleton<IStaticNodeProvider>(new StaticMeshNodeListProvider(MeshNodes))
```

`StaticMeshNodeListProvider.GetStaticNodes()` applies the same
`GroupBy(Path).Last()` de-dup the dictionary used to apply at build time —
the semantic is preserved, just deferred to iteration.

## Adding a new built-in node

Two equivalent paths:

**A — quick (one node):** `builder.AddMeshNodes(myNode)` from an extension
method. The node flows through the list provider automatically.

**B — explicit provider (preferred for any non-trivial set):**

```csharp
public sealed class MyBuiltInsProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return new MeshNode("ThingOne") { … };
        yield return new MeshNode("ThingTwo") { … };
    }
}

// Register
services.AddSingleton<IStaticNodeProvider, MyBuiltInsProvider>();
```

(B) is preferred when the nodes depend on configuration / embedded resources
/ require a constructor parameter. (A) is fine for stateless one-off
registrations from a `NodeType.Add…Type()` extension method.

## What goes wrong when nodes are missing

When a `MeshNode` references `nodeType = "X"` and **no** provider returns a
node at path `X`, `EnrichWithNodeType` runs a 3-second existence probe
(`path:X` against `IMeshQueryCore`). If the probe returns empty, activation
fails fast with an error overlay:

> NodeType 'X' is not registered (referenced by instance '<path>').
> Either register the type via AddXxxType() in your mesh builder, or
> fix the instance's NodeType field. Activation cannot proceed.

Before this probe existed, the slow path waited the full
`SlowPathTimeout = 30s` (stacked to 60s on double-enrichment) for a
typeStream emission that would never come — and the stuck per-node hub
jammed the routing action block, cascading 10s timeouts to every other
activation that posted through the same client. See
`test/MeshWeaver.Persistence.Test/UnregisteredNodeTypeTest.cs` for the
regression guard.

## Related

- `MeshWeaver.Mesh.Services.IStaticNodeProvider` — the interface
- `MeshWeaver.Mesh.Services.StaticNodeProviderExtensions` — the helpers
- `MeshWeaver.Mesh.Services.StaticMeshNodeListProvider` — internal wrapper
  that bridges `AddMeshNodes` to the provider model
- `MeshWeaver.Graph.Configuration.NodeTypeEnrichmentHelpers` — the
  existence probe + slow path
