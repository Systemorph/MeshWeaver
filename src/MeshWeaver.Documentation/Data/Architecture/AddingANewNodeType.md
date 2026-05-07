---
nodeType: Markdown
name: Adding a New Node Type
category: Architecture
description: Step-by-step recipe for introducing a new MeshNode type with its own partition, content type, and static provider. Mirrors how Agent and LanguageModel are wired.
icon: /static/NodeTypeIcons/document.svg
---

# Adding a New Node Type

This is the canonical recipe for introducing a new built-in node type with
its own partition (e.g. `Agent`, `LanguageModel`, `Thread`). The pattern is
the same in every case — get one wrong and the symptoms cascade
(empty dropdowns, missing partition, types deserialised as raw
`JsonElement`, sticky cluster errors). Following all six steps in order
makes the type Just Work.

## The six pieces

A new node type needs **all six** of the following. Skipping any one
leaves a partial registration that fails in subtle ways.

### 1. Content record

The deserialised payload that lives in `MeshNode.Content`. A plain
record. Keep it data-only — no behaviour, no DI. Carries the
content-shape contract for the type.

```csharp
// src/MeshWeaver.AI/ModelDefinition.cs
public record ModelDefinition
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public required string Provider { get; init; }
    public string? Endpoint { get; init; }
    public string? ApiKeySecretRef { get; init; }
    public int Order { get; init; }
}
```

### 2. NodeType definition class

A static class that holds the discriminator constant and the partition
meta-node. Mirrors `AgentNodeType.cs` / `LanguageModelNodeType.cs`.

```csharp
public static class LanguageModelNodeType
{
    public const string NodeType = "LanguageModel";
    public const string RootNamespace = "Model";

    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Language Model",
        Icon = "/static/NodeTypeIcons/sparkle.svg",
        AssemblyLocation = typeof(LanguageModelNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<ModelDefinition>())
    };
}
```

The `HubConfiguration` lambda is what tells per-node hubs to deserialise
`Content` as `ModelDefinition` rather than leaving it as
`JsonElement`. **Skipping `WithContentType<T>()` is the #1 cause of
"my dropdown is empty even though the synced query returned 9 nodes"
bugs** — Content arrives unparsed and downstream `Content is T` casts
fail silently.

### 3. `Add{NodeType}Type` extension on MeshBuilder

Wires four things at builder time:
- The partition meta-node (so `path:LanguageModel` lookup hits)
- Public-read access policy for the type
- A static-node provider (only if the type has built-in static instances)
- Any ancillary singletons the provider depends on

```csharp
public static TBuilder AddLanguageModelType<TBuilder>(this TBuilder builder)
    where TBuilder : MeshBuilder
{
    builder.AddMeshNodes(CreateMeshNode());
    builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
    builder.ConfigureServices(services =>
    {
        services.TryAddSingleton<LanguageModelCatalogOptions>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStaticNodeProvider, BuiltInLanguageModelProvider>());
        return services;
    });
    return builder;
}
```

**`AddMeshNodes(CreateMeshNode())` is mandatory.** It's what registers
the partition meta-node so a `path:Model` lookup returns
`{ Path: "Model", NodeType: "LanguageModel" }`. Without it the
partition exists conceptually but isn't discoverable; the chat
dropdown's `namespace:Model` query returns nothing.

### 4. Type registry entry

The TypeRegistry maps `$type` JSON discriminators to runtime types.
Without an entry, polymorphic deserialisation falls through to
`JsonElement` and downstream `Content is T` checks fail.

```csharp
// src/MeshWeaver.AI/AIExtensions.cs — AddAITypes()
public static ITypeRegistry AddAITypes(this ITypeRegistry typeRegistry)
    => typeRegistry
        .WithType(typeof(AgentConfiguration), nameof(AgentConfiguration))
        .WithType(typeof(ModelDefinition), nameof(ModelDefinition))   // ← this line
        ...;
```

Then `AddAITypes()` itself must be called on **every hub that reads
the content** — at minimum the mesh hub AND every per-user portal
hub. Look at `AIExtensions.AddAI()` for the canonical wiring:

```csharp
.ConfigureHub(config => { config.TypeRegistry.AddAITypes(); return config; })
.ConfigureDefaultNodeHub(config => { config.TypeRegistry.AddAITypes(); ... })
```

A type registered on the mesh hub but missed on the portal hub
deserialises in queries that hit the mesh, but appears as raw JSON in
queries scoped to the portal. That's the symptom that looks like "the
dropdown is full when I navigate but empty after a reload".

### 5. Wire-up call in `AddAI()` (or your equivalent module entry)

The point at which everything comes together. **Every type belongs
under one umbrella extension** (`AddAI()`, `AddGraph()`, etc.) — never
register a type directly from app code. That keeps the type catalog
auditable and prevents "I added the type but forgot the registry"
half-states.

```csharp
public TBuilder AddAI()
{
    return (TBuilder)builder
        .AddThreadMessageType()
        .AddThreadType()
        .AddAgentType()
        .AddLanguageModelType()        // ← new line
        .ConfigureServices(services => services.AddAgentChatServices())
        .ConfigureHub(config => { config.TypeRegistry.AddAITypes(); return config; })
        .ConfigureDefaultNodeHub(config => { config.TypeRegistry.AddAITypes(); ... });
}
```

### 6. Static-node provider (optional, for built-in instances)

If the type ships with built-in nodes (built-in agents, platform
models, embedded markdown), implement `IStaticNodeProvider` and emit
them from `GetStaticNodes()`. Two patterns:

**Direct (ships from embedded resources):**
```csharp
public class BuiltInAgentProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read embedded .md resources, parse frontmatter, emit MeshNodes.
    }
}
```

**Config-driven (reads `IConfiguration` at runtime):**
```csharp
public class BuiltInLanguageModelProvider : IStaticNodeProvider
{
    public BuiltInLanguageModelProvider(
        IConfiguration configuration,
        LanguageModelCatalogOptions options)
    { ... }
}
```

Note the constructor takes **plain singleton** options, not
`IOptions<T>`. The `IOptions<>` pipeline doesn't propagate Configure
delegates across the mesh hub's DI scope; live `namespace:Model`
queries returned only the access policy because Sources was empty at
provider-resolve time. Use a direct singleton + idempotent `Add()` and
mutate it from `ConfigureServices` blocks. See
`LanguageModelNodeType.AddLanguageModelCatalogSource` for the helper.

## Common pitfalls (and how to avoid them)

| Symptom | Skipped step |
|---|---|
| `Content` is `JsonElement` instead of typed record | (4) TypeRegistry entry, OR (4) `AddAITypes` not called on the consuming hub |
| `path:Foo` returns nothing | (3) `AddMeshNodes(CreateMeshNode())` not called |
| Partition exists but dropdown is empty | (6) Provider didn't emit; or `IConfiguration` is missing the section it reads from |
| `nodeType:Foo|Bar` query returns Bar but not Foo | (4) Foo's TypeRegistry entry missing in one of the queried hubs |
| New type works in dev but not in prod | Type registered in monolith config but not in the AppHost's per-process config (env vars, `Parameters:*`) |

## Tests you should write

For each new node type, write **two** test layers:

**Unit test for the provider:**
```csharp
public class FooProviderTest
{
    [Fact]
    public void Provider_OneSection_OneNodePerEntry() { ... }
}
```

**Integration test for the synced-query path** (the one the chat /
catalog actually uses) — backed by `MonolithMeshTestBase`:

```csharp
public class FooSyncedQueryTest : MonolithMeshTestBase
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder.UseMonolithMesh()
            .ConfigureServices(s => s.AddInMemoryPersistence(new InMemoryPersistenceService()))
            .AddAI();

    [Fact]
    public async Task SyncedQuery_NodeTypeFoo_ReturnsConfiguredCatalog() { ... }
}
```

The provider unit test catches logic regressions; the integration test
catches *registration-pipeline* regressions — the hardest class of bug
because the runtime symptom (empty dropdowns) is far from the cause
(missing wiring on one of the six pieces above). See
`test/MeshWeaver.Hosting.Monolith.Test/LanguageModelSyncedQueryTest.cs`
for the canonical example.

## Consuming the new type's instances

Anything that reads `nodeType:LanguageModel` (or any synced collection of
MeshNodes) **must** go through `workspace.GetQuery(id, queries...)` —
the `SyncedQueryMeshNodes` API. See **[Synced Mesh Node Queries](SyncedMeshNodeQueries.md)**
for the rationale, the canonical patterns, and what NOT to do. The short
version:

- ✅ `workspace.GetQuery(id, "namespace:Foo nodeType:Foo")` — gives you
  provider fan-out (incl. static nodes), all-Initial gating, path-keyed
  dedup, and workspace-level caching.
- 🛑 Don't roll your own merge with `IMeshService.ObserveQuery` —
  loses every one of those properties. Most notably,
  `IMeshQueryCore.ObserveQuery` does NOT see static-node-provider
  entries; your dropdown will be silently empty even though MCP shows
  9 nodes.

## Reference implementations

- **Agent** (with embedded-markdown static provider):
  `src/MeshWeaver.AI/AgentNodeType.cs` + `BuiltInAgentProvider.cs`
- **LanguageModel** (with config-driven static provider):
  `src/MeshWeaver.AI/LanguageModelNodeType.cs` + `BuiltInLanguageModelProvider.cs`
- **Thread / ThreadMessage** (no static provider, content-only):
  `src/MeshWeaver.AI/ThreadNodeType.cs`
