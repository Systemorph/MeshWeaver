# 🚨 No Static Collections — Ever

> **The rule, in one line:** a `static` field that holds a collection or cache is forbidden. Every cache and every repository is an **instance owned by the mesh**, registered in `MeshBuilder` as a **singleton**, so its lifetime is the mesh's and it is disposed when the mesh is disposed.

This is an absolute architectural invariant, equal in weight to "nothing async ever" and "`GetMeshNodeStream().Update()` is the only mutation API". It is repeated at the top of [CLAUDE.md](../../../../CLAUDE.md) → "No static collections — ever".

---

## Why

The mesh is an actor system that is **stood up and torn down many times in one process** — once per test, and per partition/tenant in production. A `static` field lives for the lifetime of the *process (AppDomain)*, not the mesh. So any `static` collection:

1. **Bleeds across tests.** Test A writes an entry; test B — running in the same process — sees it. The tell-tale symptom is a `Clear()` / `Reset()` method documented *"call this for test isolation"*. **That method is not the fix — it is the proof of the bug.** It only papers over the leak for the tests that remember to call it, and silently corrupts the ones that don't (the "passes alone, fails in the full suite" class of flake).
2. **Bleeds across users/partitions in prod.** A static token-validation cache keyed by token hash, or a static node registry, is shared by every tenant in the process. One partition's state leaks into another's reads. This is how the prod "revoked token still valid for 5 minutes" and "stale node after move" bugs happened.
3. **Cannot be disposed deterministically.** GC reclaims it whenever; until then it is a permanent memory floor. The test base even grew a reflection-based watchdog (`KnownStaticCaches` in `MonolithMeshTestBase`) to trend static-cache sizes across the suite — that watchdog exists *because* of this anti-pattern.

A mesh-scoped instance has none of these problems: it is created when the mesh is built, disposed when the mesh hub is disposed, and is invisible to every other mesh in the process.

---

## The pattern: a repo/cache registered in `MeshBuilder`

```csharp
// ❌ FORBIDDEN — process-wide, survives mesh disposal, bleeds across tests
public static class NodeTypeRegistry
{
    private static readonly ConcurrentDictionary<string, MeshNode> Nodes = new();
    public static void Register(MeshNode node) => Nodes[node.Path] = node;
    public static void Clear() => Nodes.Clear();   // ← the "for test isolation" tell
}
```

```csharp
// ✅ REQUIRED — instance repo, registered in MeshBuilder, lifetime = mesh
public sealed class NodeTypeRepository
{
    private readonly ConcurrentDictionary<string, MeshNode> nodes = new();   // instance field, not static
    public void Register(MeshNode node) => nodes[node.Path] = node;
    public bool TryGet(string path, out MeshNode? node) => nodes.TryGetValue(path, out node);
    public IEnumerable<MeshNode> GetAll() => nodes.Values;
}

// register where the mesh is configured:
builder.ConfigureServices(services => services.AddSingleton<NodeTypeRepository>());

// consume by resolving from the hub's service provider:
var repo = hub.ServiceProvider.GetRequiredService<NodeTypeRepository>();
```

`AddSingleton` here means **one instance per mesh hub** — the hub owns its own `IServiceProvider`, so a singleton in that container lives and dies with the hub. No `Clear()` is needed or wanted; isolation is structural.

### When the cache wants eviction / TTL → `IMemoryCache`

For caches that need size bounds, TTL, or LRU eviction (token validation, compiled artifacts, agent instances), use `Microsoft.Extensions.Caching.Memory.IMemoryCache` **as an instance owned by a mesh-scoped singleton** — *not* a `static MemoryCache`:

```csharp
public sealed class AgentCache : IDisposable
{
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    public TAgent GetOrCreate(string key, Func<ICacheEntry, TAgent> factory) => cache.GetOrCreate(key, factory)!;
    public void Dispose() => cache.Dispose();          // disposed with the mesh
}
builder.ConfigureServices(s => s.AddSingleton<AgentCache>());
```

Because the singleton implements `IDisposable`, the hub's container disposes it (and the `MemoryCache`) on mesh teardown.

---

## What is still allowed

`static readonly` is fine **only** for immutable, read-only constant lookups that are initialized once at type-load and never written at runtime:

- media-type / MIME maps, file-extension sets
- reserved-word / keyword sets used by parsers
- built-in role tables, SQL-keyword allow-lists, property-name maps

Litmus test: **does anything `Add`/`[]=`/`Remove`/`Clear` it after construction?** If no, it's a constant — keep it. If yes, it's a cache — move it to a mesh-scoped instance.

Process-global *memoization* keyed by a process-global identity (`Type` → reflection result, `MethodInfo` → compiled thunk, deterministic content hash → parsed pipeline) is **correct** as shared state — the value is a pure function of a key whose identity is itself process-wide — but it still violates the letter of this rule and should be moved to a DI-owned singleton when touched. Until migrated, such caches are tolerated *only* if they are provably pure-by-key and never hold mesh/user/partition data.

---

## Migration checklist (per cache)

1. Turn the `static class` (or the `static` field) into an instance type with the field as a non-`static` instance field.
2. If it needs TTL/eviction, back it with an instance `IMemoryCache` and implement `IDisposable`.
3. Register it in `MeshBuilder` via `ConfigureServices(s => s.AddSingleton<T>())`.
4. Replace every `Registry.Method(...)` call with `hub.ServiceProvider.GetRequiredService<T>().Method(...)` (or constructor-inject `T` into the consuming service).
5. **Delete the `Clear()`/`Reset()` "for test isolation" method** — isolation is now structural.
6. Remove the cache's entry from `MonolithMeshTestBase.KnownStaticCaches` (the watchdog) once it is gone.

---

## Live hit list (status)

Surfaced by the repo audit + the `KnownStaticCaches` watchdog. **C = mutable mesh/runtime state (must fix), B = process-safe memoization (migrate when touched).**

| Field | Project | Bucket | Status |
|---|---|---|---|
| `NodeTypeRegistry.Nodes` | Graph | C | ✅ deleted (dead code) |
| `ThreadExecution.AgentCache` | AI | C | ⬜ → mesh-scoped `IMemoryCache` |
| `ThreadExecution.ExecutionCancellations` | AI | C | ⬜ → mesh-scoped instance |
| `ThreadExecution.CompletionCallbacks` | AI | C | ⬜ → mesh-scoped instance |
| `ApiTokenNodeType.ValidationCache` | Graph | C | ⬜ → mesh-scoped `IMemoryCache` (TTL) |
| `CachingStorageAdapter.SharedSnapshots` | Hosting | C | ⬜ → instance on the adapter's owner |
| `EditorExtensions.InitializedEditStates` | Layout | C | ⬜ → per-layout-area state |
| `SearchHub.Pending` | Blazor.Portal | C | ⬜ → mesh-scoped instance |
| `UserContextMiddleware._loginDedup` | Blazor | C | ⬜ → mesh-scoped instance |
| `DynamicTypeGenerator.TypeCache` | Blazor | C | ⬜ → mesh-scoped instance |
| `KernelExecutor._probingDirs` | Kernel.Hub | C | ⬜ → instance / one-time install |
| `TestAccessNodeProvider.Nodes` | Test base | C | ⬜ → per-mesh seed repo |
| `XUnitFileOutputRegistry._activeOutputHelpers` | Fixture | C | ⬜ → test-infra review |
| `GenericCaches.TypeCaches` / `MethodCaches` | Reflection | B | ⬜ pure-by-`Type`/`MethodInfo` |
| `AccessControlPipeline.AttributeCache` | Hosting | B | ⬜ pure-by-`Type` |
| `MarkdownExtensions.PipelineCache` | Markdown | B | ⬜ pure-by-content |
| `DefaultImplementationOfInterfacesExtensions.NonVirtualInvocationThunks` | BusinessRules | B | ⬜ pure-by-`MethodInfo` |

Immutable constant lookups (media-type maps, reserved-word sets, role tables, SQL config) are **not** caches and are out of scope.
