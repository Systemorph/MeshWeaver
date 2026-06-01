---
Name: No Static State
Description: "Why static collection and cache fields are forbidden in MeshWeaver, and how to scope every cache as a mesh-owned instance singleton so it can never bleed across tests or partitions."
---

# No Static Collections — Ever

> **The rule in one sentence:** any `static` field that holds a collection or cache is forbidden. Every cache and every repository must be an **instance owned by the mesh**, so its lifetime is bounded and it can never bleed across tests, users, or partitions.

This is an absolute architectural invariant — equal in weight to "nothing async ever" and "`GetMeshNodeStream().Update()` is the only mutation API". It is enforced at build time by **`NoStaticCollectionsTest`** (in `MeshWeaver.PathResolution.Test`), which reflects over every `MeshWeaver.*` assembly and fails the build on any static mutable-collection field not recorded in the classified allow-list. The allow-list is the single source of truth for permitted static state; this document explains the categories and shows you what to do instead.

---

## Why static state is dangerous here

The mesh is an actor system that is **stood up and torn down many times in a single process** — once per test run, and once per tenant or partition in production. A `static` field lives for the lifetime of the *process*, not the mesh, so it persists across those boundaries in two harmful ways:

- **Cross-test bleed.** One test's writes are visible to the next test in the same process. The tell-tale symptom is a `Clear()` method added "for test isolation" — which papers over the structural problem without fixing it and makes parallel test execution unsafe.
- **Cross-partition bleed in prod.** A process-wide cache is shared by every tenant in the same worker. One partition's writes become visible in another partition's reads.

An instance owned by the correct scope has neither problem: it is created when that scope starts, disposed when it ends, and is invisible to every other concurrent scope.

---

## Scoping caches correctly

Choose the **narrowest** scope that owns the state, then hold the backing collection as an **instance field** on a type with that lifetime.

| Scope | Register / own it as | Backing field | Example |
|---|---|---|---|
| **Mesh** | `AddSingleton<T>()` in `MeshBuilder.ConfigureServices` | instance `ConcurrentDictionary` / `IMemoryCache` | a node-type repository registered per hub container |
| **Per-hub** | service in a node-type's `HubConfiguration`, or hub-owned object | instance field | `CachingStorageAdapter._snapshot`; `SearchHub._pending` |
| **Per-session / component** | per-render object the framework creates | instance field | `LayoutAreaHost.TryMarkEditStateInitialized` |
| **App** | ASP.NET `AddSingleton` middleware | instance field | `UserContextMiddleware._loginDedup` |
| **Per execution context** | `AsyncLocal<T>` (a single value, not a collection) | `AsyncLocal<T>` | `XUnitFileOutputRegistry` (active test's output helper) |

### The canonical mesh-scoped repository

```csharp
// ❌ FORBIDDEN — process-wide, survives mesh disposal, bleeds across tests
public static class NodeTypeRegistry
{
    private static readonly ConcurrentDictionary<string, MeshNode> Nodes = new();
    public static void Clear() => Nodes.Clear();   // ← "for test isolation" = the tell
}

// ✅ REQUIRED — instance repo, dies with the mesh, no Clear() needed
public sealed class NodeTypeRepository
{
    private readonly ConcurrentDictionary<string, MeshNode> nodes = new();   // instance field
    public void Register(MeshNode node) => nodes[node.Path] = node;
    public bool TryGet(string path, out MeshNode? node) => nodes.TryGetValue(path, out node);
}

// Register once in MeshBuilder — lifetime IS the mesh
builder.ConfigureServices(s => s.AddSingleton<NodeTypeRepository>());
```

For caches that need TTL or eviction, hold an `IMemoryCache` as the instance field and implement `IDisposable` so the cache is disposed with its owner.

---

## What static state IS allowed

The build guard classifies permitted static fields into four buckets. Everything else must become an instance.

### CONST — Immutable lookup tables

`static readonly` collections initialized once and **never written at runtime**: media-type maps, reserved-word sets, built-in role tables, SQL-keyword lists, parser character sets. If nothing calls `Add`, `[]=`, `Remove`, or `Clear` on it after construction, it is a *constant*, not a cache — it is safe.

### MEMO — Pure memoization on process-global keys

Process-global memoization keyed by a **process-global identity** (`Type`, `MethodInfo`, or deterministic content), where the cached value is a pure function of the key so cross-mesh sharing is always correct.

Current MEMO caches:

- `GenericCaches.TypeCaches` / `MethodCaches` — keyed by `Type` / `MethodInfo`
- `AccessControlPipeline.AttributeCache` — keyed by `Type`
- `MessageHubConfiguration._systemMessageCache` — keyed by `Type`
- `MarkdownExtensions.PipelineCache` — keyed by content
- `DynamicTypeGenerator.TypeCache` — keyed by property schema
- `DefaultImplementationOfInterfacesExtensions.NonVirtualInvocationThunks` — keyed by `MethodInfo`

### PROC — Process-global resource registrations

A registry tied to a **process-global resource** where per-mesh scoping makes no sense and there is no possibility of cross-partition bleed.

Current PROC cache: `KernelExecutor._probingDirs`, which backs the single process-wide `AssemblyLoadContext.Default.Resolving` hook.

### TESTPERF — Cross-method fixture sharing within a test class

`MonolithMeshTestBase._sharedProviders`: a service-provider cache keyed by **test-class `Type`**, so it isolates across classes and only shares within a single class's methods (which xUnit serializes). `IClassFixture` is the idiomatic long-term form.

---

> **Adding a new static collection?** If it genuinely fits one of the four buckets above, add it to the allow-list with the one-word bucket label. If it does not fit, make it an instance — that is always the right answer.

---

## Token validation: reads, not caches

API-token validation deliberately does **not** cache. Each token lives as a node at `{userId}/Token/{tokenHash}` under the user partition, mirrored into the `auth` schema by the per-partition trigger (alongside `User`, `Group`, `Role`, and `VUser`). Validation is a single live query:

```csharp
workspace.GetQuery("auth:tokenByHash:{hash}", "nodeType:ApiToken content.tokenHash:{hash} limit:1")
```

Because the query is backed by `IMeshNodeStreamCache`, it is always live. When a token is revoked (the `IsRevoked` field flips on the node), the change propagates immediately — there is no cache to hold a stale answer and no cross-hub invalidation to coordinate. The same shape is used by `OnboardingMiddleware.FindUserByEmail` (`nodeType:User content.email:{email}`).
