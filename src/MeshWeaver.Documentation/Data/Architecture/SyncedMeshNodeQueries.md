---
nodeType: Markdown
name: Synced Mesh Node Queries
category: Architecture
description: The canonical workspace.GetQuery API for live, deduped, provider-fanned, gated MeshNode collections — when to use it, how it works, and what breaks when you bypass it.
icon: /static/NodeTypeIcons/document.svg
---

# Synced Mesh Node Queries

`workspace.GetQuery(id, params string[] queries)` is the single correct way to consume a live collection of `MeshNode`s in MeshWeaver. Every chat dropdown, catalog, picker, and security stream you'll write goes through it.

Get this wrong and you spend an afternoon debugging "the dropdown is empty even though MCP search returns 9 results" — a real bug we hit twice in one day, both times because someone hand-rolled the merge over `IMeshService.Query` instead of calling `workspace.GetQuery`. This page explains what the API gives you for free, when to reach for it, and exactly how it breaks when bypassed.

---

## The API at a glance

```csharp
var workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
var collection = workspace.GetQuery(
    "my-cache-id",                      // any object — used as cache key
    "namespace:Agent nodeType:Agent",   // one or more query strings
    "namespace:Provider nodeType:LanguageModel scope:descendants");

collection.Subscribe(snapshot =>
{
    // snapshot is IEnumerable<MeshNode> — the COMPLETE current set.
    // Rebuild your view from this each time. No deltas, no merging.
});
```

`collection` is `IObservable<IEnumerable<MeshNode>>`. Every emission is the full, path-keyed union of every query's result set. When any underlying node changes, you receive a fresh complete snapshot — no delta tracking, no per-query `Initial`/`Added`/`Removed` plumbing to manage.

---

## What you get for free

`SyncedQueryMeshNodes` (the engine behind `GetQuery`) plus the cache that hosts it provide guarantees that are easy to mis-implement when rolling your own:

| Guarantee | What it means |
|---|---|
| **Path-keyed dedup** | Each node appears exactly once, keyed by `MeshNode.Path`. Overlapping queries never produce duplicate rows. |
| **Initial gating** | The fold emits nothing until the upstream query has produced its first `Initial` / `Reset`. Pre-`Initial` side-channel events (a change-feed delete, an external `NotifyDeleted`) still fold into the dictionary — they are just not *emitted* early, so a `Replay(1)` consumer can never cache an empty first snapshot ("Selected agent 'X' was not found among the available agents ([])", issue #201). |
| **Provider fan-out** | Every registered `IMeshQueryProvider` contributes — including `StaticNodeQueryProvider`, which surfaces built-in agents, language models, embedded markdown, and similar. `MeshQuery` aggregates them for **both** its secured and its unsecured (`IMeshQueryCore`) surface, so the synced query — which goes through `IMeshQueryCore` itself — sees static nodes too. |
| **Typed `Content`** | Each emitted node's `Content` is round-tripped through the **caller hub's** `JsonSerializerOptions`. The process-wide cache hub knows only framework types, so a synced query built without the caller's options hands back raw `JsonElement` and every `is T` cast fails silently (the "empty typed catalog" bug). |
| **`Replay(1).AutoConnect(1)` sharing** | The first subscriber connects the upstream; later subscribers replay the cached snapshot instantly. The upstream then stays connected for the cache's lifetime — `RefCount()` was tried and **reverted**, because dropping to zero subscribers disconnected the upstream while keeping the replay buffer, so a later `Take(1)` after a runtime write served a stale snapshot. The cache key makes `workspace.GetQuery(id)` idempotent — same observable instance on every re-mount. |
| **Delete fast-path** | Deletes published via `IMeshChangeFeed` are folded in as synthetic `Removed` events, independent of the upstream provider's own `Removed` (which the persistence change-notifier and security-filter chain can debounce or stall). |

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
  <text x="295" y="40" text-anchor="middle" fill="#b0bec5" font-size="11">Replay(1).AutoConnect(1)</text>
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
| One-shot "give me all nodes matching X right now" | `IMeshService.Query<T>(request).Take(1)` (tests may bridge with the `MeshWeaver.Fixture` `QueryAsync` extension) |
| Read a specific node by path (especially after a write) | `workspace.GetMeshNodeStream(path)` — see [CqrsAndContentAccess](/Doc/Architecture/CqrsAndContentAccess) |
| Autocomplete / prefix search | `IMeshService.Autocomplete(...)` (returns `IObservable<IReadOnlyCollection<QueryResult>>`) |

> **The rule of thumb:** if you would otherwise call `IMeshService.Query` and manually merge multiple query streams' `QueryResultChange<T>` events into a path-keyed dictionary — stop. That is exactly what `GetQuery` does, correctly, already. Using `Query` directly for this purpose is **always** a bug because:
>
> - You'll forget the Initial gate → the dropdown flashes empty and a `Replay(1)` consumer caches that empty snapshot.
> - You'll re-implement the path-keyed merge slightly differently → duplicates when two queries overlap.
> - You'll deserialise `Content` with the wrong hub's `JsonSerializerOptions` → `Content` arrives as `JsonElement`, every `is T` cast fails, and the dropdown is empty even though the snapshot has items.
> - You'll open a fresh upstream per subscriber instead of sharing the cached one.

---

## Writing queries

### Common patterns

```csharp
// Single namespace, one type
workspace.GetQuery("agents", "namespace:Agent nodeType:Agent");

// Type alternation — one query matching multiple node types in the SAME namespace.
// The Provider catalog genuinely holds both providers and their nested models;
// agents are a SEPARATE top-level namespace (Agent), never nested under Provider.
workspace.GetQuery("provider-catalog",
    "namespace:Provider nodeType:ModelProvider|LanguageModel scope:descendants");

// Per-partition registry in ONE query (the canonical agent shape): platform + space + user
// /Agent namespaces, listed directly (exact membership, no graph walk).
hub.GetQuery($"agents:{space}:{user}",
    $"namespace:{user}/Agent|{space}/Agent|Agent nodeType:Agent");

// Graph navigation — the next populated level below a node (drill), live.
hub.GetQuery($"nav-below:{path}", $"namespace:{path} scope:nextLevel is:main context:search");
hub.GetQuery($"nav-above:{path}", $"path:{path} scope:ancestors is:main");
```

> `scope:nextLevel` is the drill primitive behind the Search area's graph navigator — the nearest
> real nodes below a path, skipping empty namespace segments. See
> [Query Syntax](/Doc/DataMesh/QuerySyntax) and [Mesh Search](/Doc/GUI/MeshSearch).

### Multi-query shape

Every string you pass lands in **one** `MeshQueryRequest` (`FromQueries`), and the query engine
unions their hits by path before the fold sees them — so there is a single `Initial`, and mixing
different `nodeType:` filters across the strings does **not** stall the gate. Prefer the narrowest
set of strings that expresses the union, and prefer collapsing a namespace fan-out into one string:
`BuildAgentQuery` folds its whole union into a **single** query via the `namespace:A|B|C`
exact-membership alternation (see [Query Syntax](/Doc/DataMesh/QuerySyntax)), which is cheaper than
N strings. `AgentPickerProjection.BuildModelQueries` is the worked multi-string example — one
nodeType filter, varying namespaces and scopes. See [ModelProviders.md](/Doc/Architecture/ModelProviders).

### 🚨🚨 Run `GetQuery` on a hub LOCAL to the context — never a server-side layout hub

`hub.GetQuery` / `workspace.GetQuery` apply **per-user RLS keyed off the hub's `AccessContext`**: the shared upstream runs as System (it is process-wide infrastructure), and the caller's overload wraps it in a per-subscriber filter that captures `AccessService.Context` at wrap time and drops every node that identity lacks `Read` on (`WrapWithPerUserRls`; System / no-identity callers short-circuit to the raw upstream). So **the hub you call it on decides whose data you see** — see [Access Control](/Doc/Architecture/AccessControl). A query that touches **partition-scoped** namespaces (e.g. the agent registry's `{user}/Agent` + `{space}/Agent`) MUST run on a hub that carries the right identity:

| Context | Hub to use | Identity it carries |
|---|---|---|
| GUI / Blazor circuit (combobox, `/agent` picker) | the **portal hub** — `BlazorView.Hub` (`= PortalApplication.Hub`) | the signed-in **user** |
| Thread execution (engine agent/model selection) | the **thread hub** — `ThreadExecution`'s `parentHub` (`new AgentChatClient(parentHub.ServiceProvider)`) | the thread **owner** |

> **NEVER issue a partition-scoped `GetQuery` from a server-side `LayoutAreaHost.Hub`** (a per-node layout hub). That hub's `AccessContext` is the **hub principal**, not the user, so per-user RLS strips the `{user}`/`{space}` namespaces (the combobox renders **empty**) AND the cross-partition subscribe under a denied identity **storms the portal into a wedge**. This was a 2026-06-17 production-portal outage: a server-side agent combobox in `ThreadComposerView` injected `namespace:{user}/Agent|{space}/Agent|Agent` from the composer-node layout hub. The fix: drive selection from the GUI (`ThreadChatView.OpenPicker` on the portal hub) and from the engine (`AgentChatClient` on the thread hub) — both context-local. A **public** query (`namespace:Agent`, `namespace:Skill`) is exempt — it has no partition-scoped namespace to gate, so it is safe from any hub.

---

## Caching by id

The `id` parameter is a key into the **process-wide** `IMeshNodeStreamCache` registry (the legacy
per-workspace `ConditionalWeakTable` was deleted — one registry, one set of upstream subscriptions,
no matter how many workspaces ask):

```csharp
var first  = workspace.GetQuery("my-id", "namespace:Agent nodeType:Agent");
var second = workspace.GetQuery("my-id");      // lookup-only overload — same upstream
```

Both resolve the **same cached upstream** (one `SyncedQueryMeshNodes`, one set of provider
subscriptions). Don't assert `ReferenceEquals` on what comes back: for a real user identity each
call returns a fresh per-subscriber RLS wrapper around that shared upstream.

**Pick stable ids** — `$"chat-picker:{contextPath}"`, not `Guid.NewGuid()`. Reusing the same id across re-mounts means the upstream subscription (and the provider Initial wave) is reused rather than cycled on every component re-render. A fresh Guid on every call forfeits this entirely.

### 🚨 The id is the ONLY key — `queries` are ignored on a cache hit

`GetQuery(id, queries)` is get-**or**-create. On a hit it returns the registered stream and never
looks at `queries`:

```csharp
var current = _queries;
if (current.TryGetValue(id, out var existing))
    return existing;          // ← queries not consulted
```

So two call sites that share an id but pass **different** query strings do not get two collections,
and they do not get an error: they both get whichever one subscribed first. Whether that is the
right set is decided by render order, which is why the symptom is intermittent.

The sharp edge is the `select:` projection (below). A metadata-only reader that registers
`select:path,name` first will hand a *content-reading* consumer of the same id a snapshot whose
`Content` is null — the content reader then renders empty, on some loads and not others.

- Where an id is genuinely shared, keep the query strings **byte-identical** and project for the
  most demanding consumer. `course-modules:{coursePath}` — read by both the course overview
  (needs `content` for card summaries) and the module page (needs shells only) — is the worked
  example: both sites carry `select:path,id,name,order,content`.
- Otherwise **scope the id** so unrelated readers cannot collide. The cache is process-wide, so a
  bare `nodes-in:{ns}` minted by two different modules is one entry, not two: prefix it
  (`uw-nodes-in:{ns}`, `claims-nodes-in:{ns}`).

---

## Typed content

If your nodes carry typed content (`AgentConfiguration`, `ModelDefinition`, etc.), make sure the type is registered in the hub's `TypeRegistry`. The synced query deserialises `MeshNode.Content` using the hub's `JsonSerializerOptions`. A missing TypeRegistry entry means `Content` arrives as a raw `JsonElement`, your `is T` casts fail silently, and the collection appears empty even though the snapshot has items.

### 🚨 …and `select:` decides whether `Content` is fetched at all

A registered `TypeRegistry` entry is necessary but not sufficient: the projection has to ask for the
column. A synced query runs `Query<MeshNode>`, where a `select:` narrows the **SQL** but leaves the
row a `MeshNode`. Exactly one column is conditional — `content`. Leave it out of a `select:` and the
adapter emits `NULL::jsonb AS content`; the node arrives fully formed with `Content == null`, and
every `ContentAs<T>()` returns null with no error and no empty result.

That is the *same observable symptom* as the missing-TypeRegistry bug above — "the collection
appears empty even though the snapshot has items" — from a completely different cause. When you
debug it, check the projection before the registry: it is the cheaper of the two to rule out.

```csharp
// metadata-only  → content deliberately not loaded
$"namespace:{ns} nodeType:Module select:path,id,name,order"
// content-bearing → `content` named deliberately
$"namespace:{ns} nodeType:Module select:path,id,name,order,content"
// unproven consumer chain → no select: at all (full node, the conservative default)
$"namespace:{ns} nodeType:Module"
```

See [CqrsAndContentAccess](/Doc/Architecture/CqrsAndContentAccess) → "On a synced query, `select:`
is the switch that loads `Content`" for the full rule.

See [AddingANewNodeType](/Doc/Architecture/AddingANewNodeType) → step 4 for the wiring.

---

## Wiring a settings tab or list view

When you build a settings tab that lists MeshNodes the user can act on — API tokens, access assignments, threads, etc. — use the synced query directly. Do not add a refresh counter.

```csharp
// ❌ WRONG — refresh-counter pattern. Every revoke / delete writes a tick
//   into a data stream so the view re-runs a one-shot query. Stale for ~50–200ms
//   after each write; spurious empty flashes on Initial.
const string tokenListRefreshId = "apiTokenListRefresh";
host.UpdateData(tokenListRefreshId, DateTimeOffset.UtcNow.Ticks);
stack = stack.WithView((h, _) =>
    h.Stream.GetDataStream<long>(tokenListRefreshId)
        .SelectMany(_ => tokenService.GetTokensForUser(userId)));   // re-runs the query each tick

// ✅ RIGHT — bind directly to the synced query. New tokens appear on
//   CreateNode commit, revokes flip rows when IsRevoked changes,
//   deletes drop rows on DeleteNode commit. No refresh plumbing.
stack = stack.WithView((h, _) =>
    tokenService.GetTokensForUser(userId)                            // wraps workspace.GetQuery internally
        .Select(tokens => BuildTokenList(tokens)));
```

Inside such a service, the accessor is nothing but a projected synced query (illustrative shape —
`CopilotModelCatalog.Models` and `GitHubSyncService`'s config accessors are the live examples):

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

For any code that consumes `workspace.GetQuery`, write an integration test with `MonolithMeshTestBase` that exercises the **same** `workspace.GetQuery` call. Do **not** roll a custom test harness with `IMeshService.Query` — that bypasses the exact code path under test.

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
// 🛑 Don't roll your own with IMeshService.Query
foreach (var q in queries)
{
    MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(q))
        .Subscribe(change => MergeIntoMyDictionary(change));
}
// Loses the Initial gate, the path-keyed dedup, the caller-typed Content,
// and the shared cached upstream.

// 🛑 Don't bypass workspace.GetQuery and instantiate SyncedQueryMeshNodes directly
var typeSource = new SyncedQueryMeshNodes(workspace, "id", queries);
typeSource.StreamUpdates().Subscribe(...);
// Skips the process-wide registry — every subscriber gets a fresh upstream
// wave, and Content is typed with the wrong hub's options. Use workspace.GetQuery.

// 🛑 Don't use a fresh Guid as the cache id
workspace.GetQuery(Guid.NewGuid(), "...")
// Forfeits caching entirely. Use a stable, scope-derived key.
```

---

## See also

- [AddingANewNodeType](/Doc/Architecture/AddingANewNodeType) — how to introduce a new node type so its instances surface in synced queries
- [CqrsAndContentAccess](/Doc/Architecture/CqrsAndContentAccess) — when to use synced queries vs `GetMeshNodeStream` (single-node) vs a one-shot `Query<T>(…).Take(1)`
- [AsynchronousCalls](/Doc/Architecture/AsynchronousCalls) — `IObservable` patterns and why you never `await` inside hub-reachable code
- [ModelProviders](/Doc/Architecture/ModelProviders) — worked example of `BuildAgentQueries` / `BuildModelQueries` using the multi-query pattern correctly
