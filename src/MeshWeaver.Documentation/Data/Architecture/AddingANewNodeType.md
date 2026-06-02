---
nodeType: Markdown
name: Adding a New Node Type
category: Architecture
description: Step-by-step recipe for introducing a new MeshNode type with its own partition, content type, and static provider. Mirrors how Agent and LanguageModel are wired.
icon: /static/NodeTypeIcons/document.svg
---

# Adding a New Node Type

Every built-in type — `Agent`, `LanguageModel`, `Thread` — follows the same six-step recipe. The pattern is strict by design: miss any one piece and the symptoms cascade in ways that look unrelated (empty dropdowns, deserialization falling back to raw `JsonElement`, sticky cluster errors). Follow all six steps in order and the type just works.

> **Before you start**, look at the reference implementations listed at the bottom of this page. Reading one concrete example end-to-end takes five minutes and prevents the most common mistakes.

---

## The Six Required Pieces

A new node type needs **all six** of the following. The table below is a quick orientation; the sections that follow give the full detail.
<svg viewBox="0 0 760 370" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="10" markerHeight="7" refX="9" refY="3.5" orient="auto">
      <polygon points="0 0, 10 3.5, 0 7" fill="#90a4ae" fill-opacity="0.7"/>
    </marker>
  </defs>
  <rect width="760" height="370" rx="12" fill="#1a1a2e" fill-opacity="0.0"/>
  <rect x="10" y="10" width="140" height="52" rx="10" fill="#1e88e5"/>
  <text x="80" y="31" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">① Content Record</text>
  <text x="80" y="47" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#bbdefb">ModelDefinition</text>
  <rect x="190" y="10" width="160" height="52" rx="10" fill="#5c6bc0"/>
  <text x="270" y="31" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">② NodeType Definition</text>
  <text x="270" y="47" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c5cae9">discriminator + meta-node</text>
  <rect x="390" y="10" width="160" height="52" rx="10" fill="#8e24aa"/>
  <text x="470" y="31" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">③ Add{Type} Extension</text>
  <text x="470" y="47" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#e1bee7">partition + access + provider</text>
  <rect x="590" y="10" width="160" height="52" rx="10" fill="#26a69a"/>
  <text x="670" y="31" text-anchor="middle" font-family="sans-serif" font-size="11" font-weight="bold" fill="#fff">④ TypeRegistry Entry</text>
  <text x="670" y="47" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#b2dfdb">$type → runtime type</text>
  <line x1="150" y1="36" x2="188" y2="36" stroke="#90a4ae" stroke-opacity="0.7" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="350" y1="36" x2="388" y2="36" stroke="#90a4ae" stroke-opacity="0.7" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="550" y1="36" x2="588" y2="36" stroke="#90a4ae" stroke-opacity="0.7" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="260" y="155" width="240" height="60" rx="12" fill="#f57c00"/>
  <text x="380" y="179" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">⑤ Module Entry Point</text>
  <text x="380" y="198" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#fff3e0">AddAI() / AddGraph() umbrella</text>
  <line x1="270" y1="62" x2="345" y2="154" stroke="#90a4ae" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="470" y1="62" x2="430" y2="154" stroke="#90a4ae" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="670" y1="62" x2="490" y2="154" stroke="#90a4ae" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="80" y1="62" x2="310" y2="154" stroke="#90a4ae" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="490" y="155" width="200" height="60" rx="12" fill="#43a047"/>
  <text x="590" y="179" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">⑥ Static Node Provider</text>
  <text x="590" y="198" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#c8e6c9">built-in instances (optional)</text>
  <line x1="500" y1="36" x2="510" y2="155" stroke="#90a4ae" stroke-opacity="0.5" stroke-width="1.5" stroke-dasharray="4 3" marker-end="url(#arr)"/>
  <rect x="280" y="285" width="200" height="60" rx="12" fill="#e53935"/>
  <text x="380" y="309" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">MeshBuilder.Build()</text>
  <text x="380" y="328" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#ffcdd2">type is live in the mesh</text>
  <line x1="380" y1="215" x2="380" y2="283" stroke="#90a4ae" stroke-opacity="0.7" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="590" y1="215" x2="480" y2="283" stroke="#90a4ae" stroke-opacity="0.6" stroke-width="1.5" marker-end="url(#arr)"/>
</svg>
*Six pieces wired together: steps ①–④ feed the module entry point ⑤, the optional static provider ⑥ emits built-in instances, and MeshBuilder.Build() activates the type.*

| # | What | Why it matters |
|---|---|---|
| 1 | Content record | The typed payload for `MeshNode.Content` |
| 2 | NodeType definition class | Discriminator constant + partition meta-node |
| 3 | `Add{NodeType}Type` extension | Wires the partition, access policy, and provider into the mesh |
| 4 | TypeRegistry entry | Maps the `$type` discriminator to the runtime type |
| 5 | Call in the module entry point | Keeps every type under one auditable umbrella |
| 6 | Static-node provider *(if needed)* | Emits built-in instances (agents, platform models, …) |

---

### 1. Content record

The deserialized payload that lives in `MeshNode.Content`. Keep it a plain record — data only, no behavior, no DI. This is the content-shape contract for the type.

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

---

### 2. NodeType definition class

A static class that holds the discriminator constant and the partition meta-node. Mirrors `AgentNodeType.cs` and `LanguageModelNodeType.cs`.

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

The `HubConfiguration` lambda tells per-node hubs to deserialize `Content` as `ModelDefinition` instead of leaving it as a raw `JsonElement`.

> **Skipping `WithContentType<T>()` is the #1 cause of "my dropdown is empty even though the synced query returned 9 nodes" bugs.** Content arrives unparsed and all downstream `Content is T` casts fail silently.

---

### 3. `Add{NodeType}Type` extension on MeshBuilder

This extension wires four things at builder time:

- The partition meta-node (so a `path:LanguageModel` lookup succeeds)
- Public-read access policy for the type
- A static-node provider (only if the type has built-in instances)
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

> **`AddMeshNodes(CreateMeshNode())` is mandatory.** It registers the partition meta-node so `path:Model` returns `{ Path: "Model", NodeType: "LanguageModel" }`. Without it the partition exists conceptually but is undiscoverable — the chat dropdown's `namespace:Model` query returns nothing.

---

### 4. TypeRegistry entry

The TypeRegistry maps `$type` JSON discriminators to runtime types. Without an entry, polymorphic deserialization falls through to `JsonElement` and all downstream `Content is T` checks fail silently.

```csharp
// src/MeshWeaver.AI/AIExtensions.cs — AddAITypes()
public static ITypeRegistry AddAITypes(this ITypeRegistry typeRegistry)
    => typeRegistry
        .WithType(typeof(AgentConfiguration), nameof(AgentConfiguration))
        .WithType(typeof(ModelDefinition), nameof(ModelDefinition))   // ← this line
        ...;
```

`AddAITypes()` must then be called on **every hub that reads the content** — at minimum the mesh hub and every per-user portal hub. See `AIExtensions.AddAI()` for the canonical wiring:

```csharp
.ConfigureHub(config => { config.TypeRegistry.AddAITypes(); return config; })
.ConfigureDefaultNodeHub(config => { config.TypeRegistry.AddAITypes(); ... })
```

> A type registered on the mesh hub but missed on the portal hub deserializes correctly in queries that hit the mesh, but appears as raw JSON in queries scoped to the portal. The symptom: "the dropdown is full when I navigate but empty after a reload."

---

### 5. Wire-up call in `AddAI()` (or your module entry point)

This is where everything comes together. **Every type belongs under one umbrella extension** (`AddAI()`, `AddGraph()`, etc.) — never register a type directly from app code. This keeps the type catalog auditable and prevents "I added the type but forgot the registry" half-states.

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

---

### 6. Static-node provider *(optional — for built-in instances)*

If the type ships with built-in nodes (built-in agents, platform models, embedded markdown), implement `IStaticNodeProvider` and emit them from `GetStaticNodes()`. Two patterns are in common use:

**Direct — ships from embedded resources:**

```csharp
public class BuiltInAgentProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        // Read embedded .md resources, parse frontmatter, emit MeshNodes.
    }
}
```

**Config-driven — reads `IConfiguration` at runtime:**

```csharp
public class BuiltInLanguageModelProvider : IStaticNodeProvider
{
    public BuiltInLanguageModelProvider(
        IConfiguration configuration,
        LanguageModelCatalogOptions options)
    { ... }
}
```

Note that the constructor takes **a plain singleton** options object, not `IOptions<T>`. The `IOptions<>` pipeline does not propagate `Configure` delegates across the mesh hub's DI scope — live `namespace:Model` queries returned only the access policy because `Sources` was empty at provider-resolve time. Use a direct singleton with idempotent `Add()` and mutate it from `ConfigureServices` blocks. See `LanguageModelNodeType.AddLanguageModelCatalogSource` for the helper.

---

## Common Pitfalls

| Symptom | Root cause |
|---|---|
| `Content` is `JsonElement` instead of a typed record | TypeRegistry entry missing, **or** `AddAITypes` not called on the consuming hub |
| `path:Foo` returns nothing | `AddMeshNodes(CreateMeshNode())` not called in the `Add{NodeType}Type` extension |
| Partition exists but dropdown is empty | Provider didn't emit; or `IConfiguration` is missing the section the provider reads |
| `nodeType:Foo\|Bar` query returns Bar but not Foo | Foo's TypeRegistry entry missing on one of the queried hubs |
| New type works in dev but not in prod | Type registered in the monolith config but not in the AppHost's per-process config (env vars, `Parameters:*`) |

---

## Tests to Write

For each new node type, write **two** test layers — one for provider logic, one for the registration pipeline.

**Unit test for the provider** — catches logic regressions:

```csharp
public class FooProviderTest
{
    [Fact]
    public void Provider_OneSection_OneNodePerEntry() { ... }
}
```

**Integration test for the synced-query path** — catches registration-pipeline regressions. This is the hardest class of bug because the runtime symptom (empty dropdowns) is far removed from the cause (a missing wiring step). Back it with `MonolithMeshTestBase`:

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

See `test/MeshWeaver.Hosting.Monolith.Test/LanguageModelSyncedQueryTest.cs` for the canonical example.

---

## Consuming the New Type's Instances

Anything that reads `nodeType:LanguageModel` (or any synced collection of MeshNodes) **must** go through `workspace.GetQuery(id, queries...)` — the `SyncedQueryMeshNodes` API. See **[Synced Mesh Node Queries](SyncedMeshNodeQueries.md)** for the full rationale and canonical patterns.

The short version:

- `workspace.GetQuery(id, "namespace:Foo nodeType:Foo")` — gives you provider fan-out (including static nodes), all-Initial gating, path-keyed dedup, and workspace-level caching.
- **Do not** roll your own merge with `IMeshService.ObserveQuery` — you lose every one of those properties. Most notably, `IMeshQueryCore.ObserveQuery` does NOT see static-node-provider entries; your dropdown will be silently empty even though MCP shows 9 nodes.

---

## Reference Implementations

| Type | Files | Notes |
|---|---|---|
| **Agent** | `src/MeshWeaver.AI/AgentNodeType.cs` + `BuiltInAgentProvider.cs` | Embedded-markdown static provider |
| **LanguageModel** | `src/MeshWeaver.AI/LanguageModelNodeType.cs` + `BuiltInLanguageModelProvider.cs` | Config-driven static provider |
| **Thread / ThreadMessage** | `src/MeshWeaver.AI/ThreadNodeType.cs` | No static provider; content-only |
