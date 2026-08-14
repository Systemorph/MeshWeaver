---
Name: No Static State
Description: "Why static collection and cache fields are forbidden in MeshWeaver, and how to scope every cache as a mesh-owned instance singleton so it can never bleed across tests or partitions."
---

# No Static Collections — Ever

> **The rule in one sentence:** any `static` field that holds a collection or cache is forbidden. Every cache and every repository must be an **instance owned by the mesh**, so its lifetime is bounded and it can never bleed across tests, users, or partitions.

This is an absolute architectural invariant — equal in weight to "nothing async ever" and "`GetMeshNodeStream().Update()` is the only mutation API". It is checked by **`NoStaticCollectionsTest`** (`test/MeshWeaver.PathResolution.Test/NoStaticCollectionsTest.cs`), which reflects over the `MeshWeaver.*.dll` files and fails on any static mutable-collection field not recorded in its `Allowed` map, each entry carrying a one-word `CONST`/`MEMO`/`CACHE`/`PROC` reason. That map is the single source of truth for permitted static state; this document explains the categories and shows you what to do instead.

🚨 **The check is a test, not a compiler rule, and its reach is the test project's own output directory** — i.e. the transitive closure of `MeshWeaver.PathResolution.Test`'s project references, not all 76 `MeshWeaver.*` projects. A static cache added in an assembly that closure does not pull in **will not be caught**. Treat the rule as binding everywhere and the test as a backstop over part of the tree; if you add a static field in a peripheral project, the absence of a red test is not evidence you are allowed to.

<svg viewBox="0 0 760 310" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#888"/>
    </marker>
    <marker id="arr-red" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#e53935"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#43a047"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="310" rx="12" fill="#1a1a2e" opacity="0.85"/>
  <text x="190" y="28" text-anchor="middle" font-size="14" font-weight="bold" fill="#e53935">❌  Static (FORBIDDEN)</text>
  <text x="570" y="28" text-anchor="middle" font-size="14" font-weight="bold" fill="#43a047">✅  Instance-scoped (REQUIRED)</text>
  <line x1="380" y1="10" x2="380" y2="300" stroke="currentColor" stroke-opacity="0.2" stroke-dasharray="6,4"/>
  <rect x="20" y="40" width="320" height="52" rx="8" fill="#b71c1c" fill-opacity="0.35" stroke="#e53935" stroke-width="1.2"/>
  <text x="180" y="61" text-anchor="middle" fill="#ef9a9a" font-weight="bold">static ConcurrentDictionary&lt;…&gt;</text>
  <text x="180" y="81" text-anchor="middle" fill="#ef9a9a" font-size="11">process lifetime · shared by ALL meshes</text>
  <rect x="20" y="122" width="140" height="44" rx="8" fill="#1e3a5f" stroke="#5c6bc0" stroke-width="1.2"/>
  <text x="90" y="140" text-anchor="middle" fill="#c5cae9" font-weight="bold">Test mesh A</text>
  <text x="90" y="158" text-anchor="middle" fill="#9fa8da" font-size="11">writes key "X"</text>
  <rect x="200" y="122" width="140" height="44" rx="8" fill="#1e3a5f" stroke="#5c6bc0" stroke-width="1.2"/>
  <text x="270" y="140" text-anchor="middle" fill="#c5cae9" font-weight="bold">Test mesh B</text>
  <text x="270" y="158" text-anchor="middle" fill="#ef9a9a" font-size="11">sees stale "X" ← BLEED</text>
  <line x1="90" y1="92" x2="90" y2="122" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <line x1="270" y1="92" x2="270" y2="122" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="30" y="200" width="300" height="40" rx="8" fill="#4a1515" stroke="#e53935" stroke-width="1" stroke-dasharray="5,3"/>
  <text x="180" y="218" text-anchor="middle" fill="#ef9a9a" font-size="12">Prod: tenant A's data visible in tenant B</text>
  <text x="180" y="234" text-anchor="middle" fill="#ef9a9a" font-size="11">Requires Clear() → proves the bug exists</text>
  <rect x="400" y="40" width="340" height="52" rx="8" fill="#1b5e20" fill-opacity="0.35" stroke="#43a047" stroke-width="1.2"/>
  <text x="570" y="61" text-anchor="middle" fill="#a5d6a7" font-weight="bold">class NodeTypeRepository  { dict = new(); }</text>
  <text x="570" y="81" text-anchor="middle" fill="#a5d6a7" font-size="11">AddSingleton → lifetime IS the mesh</text>
  <rect x="410" y="122" width="145" height="55" rx="8" fill="#0d3321" stroke="#43a047" stroke-width="1.2"/>
  <text x="482" y="141" text-anchor="middle" fill="#c8e6c9" font-weight="bold">Mesh A</text>
  <text x="482" y="157" text-anchor="middle" fill="#a5d6a7" font-size="11">own instance</text>
  <text x="482" y="171" text-anchor="middle" fill="#a5d6a7" font-size="11">disposed on teardown</text>
  <rect x="595" y="122" width="145" height="55" rx="8" fill="#0d3321" stroke="#43a047" stroke-width="1.2"/>
  <text x="667" y="141" text-anchor="middle" fill="#c8e6c9" font-weight="bold">Mesh B</text>
  <text x="667" y="157" text-anchor="middle" fill="#a5d6a7" font-size="11">own instance</text>
  <text x="667" y="171" text-anchor="middle" fill="#a5d6a7" font-size="11">disposed on teardown</text>
  <line x1="482" y1="92" x2="482" y2="122" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <line x1="667" y1="92" x2="667" y2="122" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <rect x="415" y="205" width="325" height="40" rx="8" fill="#0d3321" stroke="#43a047" stroke-width="1" stroke-dasharray="5,3"/>
  <text x="577" y="222" text-anchor="middle" fill="#a5d6a7" font-size="12">No bleed · No Clear() · Safe parallel tests</text>
  <text x="577" y="238" text-anchor="middle" fill="#a5d6a7" font-size="11">Each partition gets its own isolated cache</text>
  <line x1="425" y1="177" x2="425" y2="205" stroke="#43a047" stroke-width="1" stroke-opacity="0.6" marker-end="url(#arr-green)"/>
  <line x1="725" y1="177" x2="725" y2="205" stroke="#43a047" stroke-width="1" stroke-opacity="0.6" marker-end="url(#arr-green)"/>
  <text x="380" y="295" text-anchor="middle" fill="currentColor" fill-opacity="0.45" font-size="11">Process boundary</text>
</svg>

*Static fields outlive every mesh instance and bleed across tests and partitions; instance singletons registered in `MeshBuilder` are isolated to their own mesh lifetime.*

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

> 🚨 **"Never written" is about the ELEMENTS, not just the collection.** A write-once list of objects
> that carry their own mutable, lazily-materialized state is a process-wide cache, and CONST does not
> cover it. This exemption was applied twice to
> `MeshNodeCompilationService._references` — a `static readonly IReadOnlyList<MetadataReference>` —
> on the grounds that the list is never mutated. It isn't; but each `PortableExecutableReference`
> owns lazily memory-mapped, `IDisposable` metadata and Roslyn hangs its derived assembly/symbol
> tables off that same instance, so the field is a mutable process-wide cache in `static readonly`
> clothing. It survived two reviews and is the shared state
> [#890](https://github.com/Systemorph/MeshWeaver/issues/890) came down to.
>
> Ask **"could two meshes observe each other through an element of this?"** — not "does anything call
> `Add`". Roslyn `MetadataReference`s in particular are now rejected by their own guard in
> `NoStaticCollectionsTest`, whatever the declared collection type, because the declared-type check
> could not see an `IReadOnlyList<>`.
>
> The field itself is still there, listed as a `CACHE` debt with its issue number rather than
> silently exempt — removing it gives every mesh its own reference set, which measurably costs ~15 MiB
> per compiling mesh, and whether that buys anything is decided by #890's emit canary. **Landing the
> detector does not require paying for the fix**, and an allowlist entry with a reason is what turns
> "nobody noticed" into "we know, and here is the open question".

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
