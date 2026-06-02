---
nodeType: Markdown
name: Synced Mesh Node Queries
category: Architecture
description: The canonical workspace.GetQuery API for live, deduped, provider-fanned, gated MeshNode collections — when to use it, how it works, and what breaks when you bypass it.
icon: /static/NodeTypeIcons/document.svg
---

# Synced Mesh Node Queries

`workspace.GetQuery(id, params string[] queries)` is the single correct way to consume a live collection of `MeshNode`s in MeshWeaver. Every chat dropdown, catalog, picker, and security stream you'll write goes through it.

Get this wrong and you spend an afternoon debugging "the dropdown is empty even though MCP search returns 9 results" — that's a real bug we hit twice in one day, both times because someone replaced `workspace.GetQuery` with `IMeshService.ObserveQuery`. This page explains what the API gives you for free, when to reach for it, and exactly how it breaks when bypassed.

---

## The API at a glance

```csharp
var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
var collection = workspace.GetQuery(
    "my-cache-id",                      // any object — used as cache key
    "namespace:Agent nodeType:Agent",   // one or more query strings
    "namespace:Model nodeType:LanguageModel");

collection.Subscribe(snapshot =>
{
    // snapshot is IEnumerable<MeshNode> — the COMPLETE current set.
    // Rebuild your view from this each time. No deltas, no merging.
});
```

`collection` is `IObservable<IEnumerable<MeshNode>>`. Every emission is the full, path-keyed union of every query's result set. When any underlying node changes, you receive a fresh complete snapshot — no delta tracking, no per-query `Initial`/`Added`/`Removed` plumbing to manage.

---

## What you get for free

`SyncedQueryMeshNodes` (the engine behind `GetQuery`) provides five guarantees that are easy to mis-implement when rolling your own:

| Guarantee | What it means |
|---|---|
| **Path-keyed dedup** | Each node appears exactly once, keyed by `MeshNode.Path`. Overlapping queries never produce duplicate rows. |
| **All-Initial gating** | The snapshot only emits once *every* underlying query has produced its first `Initial` event. You never see partial state; empty-snapshot filtering in your subscriber is unnecessary. |
| **Provider fan-out** | Every `IMeshQueryProvider` registered on the hub contributes — including `StaticNodeQueryProvider`, which surfaces built-in agents, language models, embedded markdown, and similar. **This is the property that broke when someone used `IMeshQueryCore.ObserveQuery` directly** — `Core` only invokes the in-memory provider, silently hiding all static nodes. |
| **`Replay(1).RefCount()` sharing** | The first subscriber triggers upstream per-query subscriptions; later subscribers replay the cached snapshot instantly. All subscribers going away pauses the upstream. The cache key makes `workspace.GetQuery(id)` idempotent — same observable instance on every re-mount. |
| **Delete fast-path** | Deletes published via `IMeshChangeFeed` bypass the per-provider debounce and update the synced collection synchronously. Without this, cache-driven queries can stay stale after a delete for several seconds. |

---
<svg viewBox="0 0 760 340" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="340" rx="12" fill="#1a1a2e" opacity="0.0"/>
  <rect x="20" y="20" width="160" height="44" rx="10" fill="#1565c0"/>
  <text x="100" y="39" text-anchor="middle" fill="#fff" font-weight="bold">workspace</text>
  <text x="100" y="57" text-anchor="middle" fill="#cfd8dc" font-size="11">.GetQuery(id, queries…)</text>
  <rect x="220" y="10" width="150" height="36" rx="8" fill="#283593"/>
  <text x="295" y="24" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Per-workspace cache</text>
  <text x="295" y="40" text-anchor="middle" fill="#b0bec5" font-size="11">Replay(1).RefCount()</text>
  <rect x="220" y="58" width="150" height="26" rx="8" fill="#283593"/>
  <text x="295" y="75" text-anchor="middle" fill="#b0bec5" font-size="11">key → same IObservable</text>
  <line x1="180" y1="42" x2="218" y2="42" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="410" y="10" width="150" height="36" rx="8" fill="#1b5e20"/>
  <text x="485" y="24" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">StaticNodeQueryProvider</text>
  <text x="485" y="40" text-anchor="middle" fill="#a5d6a7" font-size="11">Agents, Models, Docs…</text>
  <rect x="410" y="58" width="150" height="36" rx="8" fill="#1b5e20"/>
  <text x="485" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">PersistenceQueryProvider</text>
  <text x="485" y="88" text-anchor="middle" fill="#a5d6a7" font-size="11">Postgres / In-Memory</text>
  <rect x="410" y="106" width="150" height="36" rx="8" fill="#1b5e20"/>
  <text x="485" y="120" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">…other providers</text>
  <text x="485" y="136" text-anchor="middle" fill="#a5d6a7" font-size="11">IMeshQueryProvider [ ]</text>
  <line x1="372" y1="42" x2="408" y2="28" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="372" y1="42" x2="408" y2="76" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="372" y1="42" x2="408" y2="124" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="600" y="10" width="145" height="36" rx="8" fill="#4a148c"/>
  <text x="672" y="24" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">All-Initial gate</text>
  <text x="672" y="40" text-anchor="middle" fill="#ce93d8" font-size="11">emit only when all ready</text>
  <rect x="600" y="58" width="145" height="36" rx="8" fill="#4a148c"/>
  <text x="672" y="72" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Path-keyed dedup</text>
  <text x="672" y="88" text-anchor="middle" fill="#ce93d8" font-size="11">1 node per MeshNode.Path</text>
  <rect x="600" y="106" width="145" height="36" rx="8" fill="#4a148c"/>
  <text x="672" y="120" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Delete fast-path</text>
  <text x="672" y="136" text-anchor="middle" fill="#ce93d8" font-size="11">sync on IMeshChangeFeed</text>
  <line x1="562" y1="28" x2="598" y2="28" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="562" y1="76" x2="598" y2="76" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="562" y1="124" x2="598" y2="124" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="220" y="185" width="490" height="50" rx="10" fill="#b71c1c"/>
  <text x="465" y="206" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">IObservable&lt;IEnumerable&lt;MeshNode&gt;&gt;</text>
  <text x="465" y="226" text-anchor="middle" fill="#ffcdd2" font-size="11">complete snapshot on every change — no deltas, no merging in subscriber</text>
  <line x1="672" y1="144" x2="672" y2="184" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="295" y1="86" x2="295" y2="184" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="20" y="260" width="220" height="60" rx="10" fill="#1565c0" opacity="0.75"/>
  <text x="130" y="280" text-anchor="middle" fill="#fff" font-size="12">Subscriber A</text>
  <text x="130" y="298" text-anchor="middle" fill="#b0bec5" font-size="11">UI dropdown / picker</text>
  <text x="130" y="314" text-anchor="middle" fill="#b0bec5" font-size="11">replays cached snapshot</text>
  <rect x="270" y="260" width="220" height="60" rx="10" fill="#1565c0" opacity="0.75"/>
  <text x="380" y="280" text-anchor="middle" fill="#fff" font-size="12">Subscriber B</text>
  <text x="380" y="298" text-anchor="middle" fill="#b0bec5" font-size="11">derived synced collection</text>
  <text x="380" y="314" text-anchor="middle" fill="#b0bec5" font-size="11">same upstream — no extra wave</text>
  <rect x="520" y="260" width="220" height="60" rx="10" fill="#1565c0" opacity="0.75"/>
  <text x="630" y="280" text-anchor="middle" fill="#fff" font-size="12">Subscriber C</text>
  <text x="630" y="298" text-anchor="middle" fill="#b0bec5" font-size="11">security / settings tab</text>
  <text x="630" y="314" text-anchor="middle" fill="#b0bec5" font-size="11">no refresh counter needed</text>
  <line x1="350" y1="237" x2="130" y2="258" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="465" y1="237" x2="380" y2="258" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="580" y1="237" x2="630" y2="258" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
</svg>
*`workspace.GetQuery` fans out across all `IMeshQueryProvider` implementations, gates on every Initial event, deduplicates by path, and multicasts a complete snapshot to all subscribers via a single shared upstream.*

---

## When to use it

| Use case | Correct API |
|---|---|
| Live list of MeshNodes for a UI dropdown / picker | `workspace.GetQuery(id, queries...)` ← here |
| Live list of MeshNodes for a derived synced collection | `workspace.GetQuery(id, queries...)` ← here |
| One-shot "give me all nodes matching X right now" | `IMeshService.QueryAsync` |
| Read a specific node by path (especially after a write) | `workspace.GetMeshNodeStream(path)` — see [CqrsAndContentAccess](CqrsAndContentAccess.md) |
| Autocomplete / prefix search | `IMeshService.AutocompleteAsync` |

> **The rule of thumb:** if you would otherwise call `IMeshService.ObserveQuery` and manually merge multiple query streams' `QueryResultChange<T>` events into a path-keyed dictionary — stop. That is exactly what `GetQuery` does, correctly, already. Using `ObserveQuery` directly for this purpose is **always** a bug because:
>
> - You'll forget to gate on every per-query Initial → the dropdown flashes empty before the slowest query finishes.
> - You'll re-implement the path-keyed merge slightly differently → duplicates when two queries overlap.
> - You'll subscribe to `IMeshQueryCore` (or a single provider) rather than the full `IMeshQueryProvider` enumeration → static nodes are invisible. **This produced an empty Agent dropdown even though MCP `nodeType:Agent` returned 9 entries.**

---

## Writing queries

### Common patterns

```csharp
// Single namespace, one type
workspace.GetQuery("agents", "namespace:Agent nodeType:Agent");

// Type alternation (agents OR models from any namespace)
workspace.GetQuery("agents-and-models",
    "namespace:Agent nodeType:Agent|LanguageModel",
    "namespace:Model nodeType:Agent|LanguageModel");

// Path hierarchy + global scope
workspace.GetQuery($"agents:{contextPath}",
    "namespace:Agent nodeType:Agent",                                       // global
    $"namespace:{contextPath} scope:selfAndAncestors nodeType:Agent");      // path-local
```

### Multi-query gating rule

> 🚨 **CRITICAL — Every query in a single `GetQuery` call MUST carry the same `nodeType:` filter.** Vary only namespace and scope. Mixing different `nodeType:` filters across queries in one call breaks the all-Initial gate — the synced collection never emits its first snapshot.
>
> The canonical shape is what `AgentPickerProjection.BuildAgentQueries` / `BuildModelQueries` produce: one nodeType filter, varying namespaces and scopes. See [ModelProviders.md](ModelProviders.md) for a worked example.

---

## Caching by id

The `id` parameter is a key into a per-workspace registry:

```csharp
var first  = workspace.GetQuery("my-id", "namespace:Agent nodeType:Agent");
var second = workspace.GetQuery("my-id");      // no-args overload — same instance
ReferenceEquals(first, second).Should().BeTrue();
```

**Pick stable ids** — `$"chat-picker:{contextPath}"`, not `Guid.NewGuid()`. Reusing the same id across re-mounts means the upstream subscription (and the provider Initial wave) is reused rather than cycled on every component re-render. A fresh Guid on every call forfeits this entirely.

---

## Typed content

If your nodes carry typed content (`AgentConfiguration`, `ModelDefinition`, etc.), make sure the type is registered in the hub's `TypeRegistry`. The synced query deserialises `MeshNode.Content` using the hub's `JsonSerializerOptions`. A missing TypeRegistry entry means `Content` arrives as a raw `JsonElement`, your `is T` casts fail silently, and the collection appears empty even though the snapshot has items.

See [AddingANewNodeType](AddingANewNodeType.md) → step 4 for the wiring.

---

## Wiring a settings tab or list view

When you build a settings tab that lists MeshNodes the user can act on — API tokens, access assignments, threads, etc. — use the synced query directly. Do not add a refresh counter.

```csharp
// ❌ WRONG — refresh-counter pattern. Every revoke / delete writes a tick
//   into a data stream so the view re-queries QueryAsync. Stale for ~50–200ms
//   after each write; spurious empty flashes on Initial.
const string tokenListRefreshId = "apiTokenListRefresh";
host.UpdateData(tokenListRefreshId, DateTimeOffset.UtcNow.Ticks);
stack = stack.WithView((h, _) =>
    h.Stream.GetDataStream<long>(tokenListRefreshId)
        .SelectMany(_ => tokenService.GetTokensForUser(userId)));   // re-fires QueryAsync each tick

// ✅ RIGHT — bind directly to the synced query. New tokens appear on
//   CreateNode commit, revokes flip rows when IsRevoked changes,
//   deletes drop rows on DeleteNode commit. No refresh plumbing.
stack = stack.WithView((h, _) =>
    tokenService.GetTokensForUser(userId)                            // wraps workspace.GetQuery internally
        .Select(tokens => BuildTokenList(tokens)));
```

Inside the service, `GetTokensForUser` simply wraps the synced query:

```csharp
public IObservable<IReadOnlyList<ApiTokenInfo>> GetTokensForUser(string userId)
    => workspace.GetQuery(
        $"api-tokens:{userId}",                                       // stable cache key
        $"namespace:{userId}/ApiToken nodeType:ApiToken",
        $"namespace:ApiToken nodeType:ApiToken")                      // legacy fallback
       .Select(snapshot => ProjectToInfo(snapshot, userId));
```

### Cross-hub writes and pre-warm

Subscribing to a synced query registers the result-set paths in the workspace's live synced-query set. That set is the lookup table the `MeshNodeReference` reducer uses when a caller does `workspace.GetMeshNodeStream(remote_path).Update(...)`. Without an active synced subscription that includes the path, `Update` opens a fresh `GetRemoteStream` subscription that races the `SubscribeResponse` — the lambda fires with `current=null` before the per-node hub's initial frame arrives.

In a UI that renders the list before exposing per-row buttons, the synced subscription is already established by the time the user clicks Revoke and the Update succeeds. **In tests or one-shot scripts that skip the list render**, pre-warm the synced query explicitly:

```csharp
// Test setup mirroring UI lifecycle
await service.GetTokensForUser(userId)
    .Where(list => list.Any(t => t.NodePath == newPath))
    .Take(1)
    .ToTask(ct);   // synced subscription now registers newPath in the workspace

var outcome = await service.RevokeToken(newPath);   // GetMeshNodeStream(newPath).Update resolves correctly
```

`MeshNodeStreamHandle.Update` waits up to 30 s for the initial frame and throws a precise `TimeoutException` with the path embedded if it never arrives — but the fast path is to have the synced query active.

---

## Testing

For any code that consumes `workspace.GetQuery`, write an integration test with `MonolithMeshTestBase` that exercises the **same** `workspace.GetQuery` call. Do **not** roll a custom test harness with `IMeshService.ObserveQuery` — that bypasses the exact code path under test.

Canonical example: `test/MeshWeaver.Hosting.Monolith.Test/LanguageModelSyncedQueryTest.cs`.

```csharp
public class FooSyncedQueryTest : MonolithMeshTestBase
{
    private IWorkspace Workspace => Mesh.GetWorkspace();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => builder.UseMonolithMesh()
            .ConfigureServices(s => s.AddInMemoryPersistence(new InMemoryPersistenceService()))
            .ConfigureHub(c => c.AddData())   // registers IWorkspace
            .AddAI();                          // or your equivalent

    [Fact]
    public async Task SyncedQuery_DeliversTypedContentWithName()
    {
        var snapshot = await Workspace.GetQuery(
            "test-id",
            "namespace:Agent nodeType:Agent")
            .Where(s => s.Any())
            .Take(1)
            .Timeout(15.Seconds())
            .ToTask();

        snapshot.Should().AllSatisfy(n =>
        {
            n.Name.Should().NotBeNullOrWhiteSpace();             // Empty Name = invisible UI rows
            n.Content.Should().BeOfType<AgentConfiguration>();   // JsonElement = silently dropped
        });
    }
}
```

---

## What NOT to do

```csharp
// 🛑 Don't roll your own with IMeshService.ObserveQuery
foreach (var q in queries)
{
    MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(q))
        .Subscribe(change => MergeIntoMyDictionary(change));
}
// Loses provider fan-out, all-Initial gating, dedup, and the workspace
// cache. Will be silently broken for static nodes.

// 🛑 Don't bypass workspace.GetQuery and instantiate SyncedQueryMeshNodes directly
var typeSource = new SyncedQueryMeshNodes(workspace, "id", queries);
typeSource.StreamUpdates().Subscribe(...);
// Skips the per-workspace registry — every subscriber gets a fresh
// upstream wave. Use workspace.GetQuery instead.

// 🛑 Don't use a fresh Guid as the cache id
workspace.GetQuery(Guid.NewGuid(), "...")
// Forfeits caching entirely. Use a stable, scope-derived key.
```

---

## See also

- [AddingANewNodeType](AddingANewNodeType.md) — how to introduce a new node type so its instances surface in synced queries
- [CqrsAndContentAccess](CqrsAndContentAccess.md) — when to use synced queries vs `GetMeshNodeStream` (single-node) vs `QueryAsync` (one-shot)
- [AsynchronousCalls](AsynchronousCalls.md) — `IObservable` patterns and why you never `await` inside hub-reachable code
- [ModelProviders](ModelProviders.md) — worked example of `BuildAgentQueries` / `BuildModelQueries` using the multi-query pattern correctly
