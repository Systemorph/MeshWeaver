---
Name: No Static State
Description: "Explains why static collection/cache fields are forbidden and how every cache must be a mesh-scoped instance singleton to prevent cross-test and cross-partition bleed."
---

# 🚨 No Static Collections — Ever

> **The rule, in one line:** a `static` field that holds a collection or cache is forbidden. Every cache and every repository is an **instance owned by the mesh** (or by a hub, component, request, or app), so its lifetime is bounded and it never bleeds across tests, users, or partitions.

This is an absolute architectural invariant, equal in weight to "nothing async ever" and "`GetMeshNodeStream().Update()` is the only mutation API". It is enforced by the **`NoStaticCollectionsTest`** build guard (in `MeshWeaver.PathResolution.Test`), which reflects over every `MeshWeaver.*` assembly and fails on any static mutable-collection field that isn't in its classified allow-list. The allow-list is the single source of truth for what static state exists; this document explains the categories it permits.

---

## Why

The mesh is an actor system stood up and torn down many times in one process — once per test, and per partition/tenant in production. A `static` field lives for the lifetime of the *process*, not the mesh, so a static collection:

- **bleeds across tests** — one test's writes are visible to the next (the tell-tale is a `Clear()` "for test isolation" method, which papers over the leak rather than fixing it), and makes parallel test execution unsafe;
- **bleeds across users/partitions in prod** — a process-wide cache is shared by every tenant, so one partition's state leaks into another's reads.

An instance owned by the right scope has neither problem: it is created and disposed with that scope and is invisible to every other.

---

## How caches are scoped

Pick the **narrowest** scope that owns the state, and hold the backing collection as an **instance field** on a type with that lifetime:

| Scope | Owner | Backing field | Example |
|---|---|---|---|
| **Mesh** | a singleton registered in `MeshBuilder.ConfigureServices` (one instance per hub container) | instance `ConcurrentDictionary` / `IMemoryCache` | a repo registered `AddSingleton<T>()` |
| **Per-hub** | a service in a node-type's `HubConfiguration`, or a hub-owned object | instance field | `CachingStorageAdapter._snapshot` (the adapter is `AddSingleton<IStorageAdapter>` per hub); `SearchHub._pending` (per hosted hub) |
| **Per-session / component** | a per-render object the framework already creates | instance field | `LayoutAreaHost.TryMarkEditStateInitialized` (per layout-area session) |
| **App** | an ASP.NET singleton (e.g. middleware) | instance field | `UserContextMiddleware._loginDedup` (one app-lifetime middleware instance) |
| **Per execution context** | `AsyncLocal<T>` (a single value, not a collection) | `AsyncLocal<T>` | `XUnitFileOutputRegistry` (the active test's output helper) |

```csharp
// the canonical mesh-scoped repo
public sealed class NodeTypeRepository
{
    private readonly ConcurrentDictionary<string, MeshNode> nodes = new();   // instance, not static
    public void Register(MeshNode node) => nodes[node.Path] = node;
    public bool TryGet(string path, out MeshNode? node) => nodes.TryGetValue(path, out node);
}
builder.ConfigureServices(s => s.AddSingleton<NodeTypeRepository>());          // dies with the mesh — no Clear()
```

For caches that need TTL or eviction, the instance field is an `IMemoryCache` (and the owner implements `IDisposable` so the cache is disposed with it).

---

## The static state that *is* allowed

The guard permits four buckets. Everything else must become an instance.

- **CONST** — `static readonly` immutable lookups initialized once and **never written at runtime**: media-type maps, reserved-word sets, built-in role tables, SQL-keyword lists, parser char-sets. If nothing `Add`/`[]=`/`Remove`/`Clear`s it after construction, it's a constant, not a cache.
- **MEMO** — process-global memoization keyed by a **process-global identity** (`Type`, `MethodInfo`, or deterministic content), where the cached value is a pure function of the key, so cross-mesh sharing is correct. Current MEMO caches: `GenericCaches.TypeCaches`/`MethodCaches` (`Type`/`MethodInfo`), `AccessControlPipeline.AttributeCache` (`Type`), `MessageHubConfiguration._systemMessageCache` (`Type`), `MarkdownExtensions.PipelineCache` (content), `DynamicTypeGenerator.TypeCache` (property schema), `DefaultImplementationOfInterfacesExtensions.NonVirtualInvocationThunks` (`MethodInfo`).
- **PROC** — a registry tied to a **process-global resource** where per-mesh makes no sense and there is no bleed. Current PROC cache: `KernelExecutor._probingDirs`, which backs the single process-wide `AssemblyLoadContext.Default.Resolving` hook.
- **TESTPERF** — `MonolithMeshTestBase._sharedProviders`: a service-provider cache keyed by **test-class `Type`**, so it isolates across classes and only shares within a class's own methods (which xUnit serializes). `IClassFixture` is the eventual idiomatic form.

A new static collection that doesn't fit one of these fails the build. Add it to the allow-list with a one-word bucket only if it genuinely belongs to one — otherwise make it an instance.

---

## Reads, not caches: token validation

API-token validation does **not** cache. The token is a node at `{userId}/Token/{tokenHash}` under the user partition, mirrored into the `auth` schema by the per-partition trigger (same as `User`/`Group`/`Role`/`VUser`). Validation is a single live query against that schema:

```csharp
workspace.GetQuery("auth:tokenByHash:{hash}", "nodeType:ApiToken content.tokenHash:{hash} limit:1")
```

The synced query is live (`IMeshNodeStreamCache`-backed), so a revoked token (`IsRevoked` flips on the node) is rejected immediately — no cache to hold a stale answer, no cross-hub invalidation. This is the same shape as `OnboardingMiddleware.FindUserByEmail` (`nodeType:User content.email:{email}`).
