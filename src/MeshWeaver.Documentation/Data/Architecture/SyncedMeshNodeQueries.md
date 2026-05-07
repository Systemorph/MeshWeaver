---
nodeType: Markdown
name: Synced Mesh Node Queries
category: Architecture
description: The canonical workspace.GetQuery API for live, deduped, gated MeshNode collections. How it works, when to use it, what NOT to do.
icon: /static/NodeTypeIcons/document.svg
---

# Synced Mesh Node Queries

`workspace.GetQuery(id, params string[] queries)` is **the** way to consume
a live collection of `MeshNode`s in MeshWeaver. Every chat dropdown,
catalog, picker, and security stream you'll write goes through it. Get
this wrong and you spend an afternoon debugging "the dropdown is empty
even though MCP search returns 9 results" — that's a real bug we hit
twice in one day, both times because someone re-rolled the merge with
`IMeshService.ObserveQuery` instead of using the synced query.

## The shape

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

`collection` is `IObservable<IEnumerable<MeshNode>>`. Every emission is
the full current path-keyed collection (the union of every query's result
set). When the underlying nodes change, you get a new full snapshot.
That's the entire surface — there's no `QueryResultChange<T>`, no
delta tracking, no per-query `Initial`/`Added`/`Removed` plumbing.

## The properties you get for free

`SyncedQueryMeshNodes` (the engine behind `GetQuery`) gives you:

1. **Path-keyed dedup.** Every node appears exactly once in the snapshot,
   keyed by `MeshNode.Path`. Two queries that overlap don't produce
   duplicate rows.

2. **All-Initial gating.** The snapshot is only emitted once **every**
   underlying query has produced its first `Initial` event. So you never
   see partial state — you don't have to filter empty snapshots in your
   subscriber.

3. **Provider fan-out.** Every `IMeshQueryProvider` registered on the
   hub contributes to the result. That includes `StaticNodeQueryProvider`,
   which is what surfaces built-in agents, language models, embedded
   markdown, etc. **This is the property that broke when someone rolled
   their own with `IMeshQueryCore.ObserveQuery` directly** — `Core` only
   invokes the in-memory provider, missing all static nodes.

4. **`Replay(1).RefCount()` upstream sharing.** The first subscriber
   triggers the per-query subscriptions; later subscribers replay the
   cached latest snapshot. When all subscribers go away, the upstream
   subscriptions pause. The cache key (`id`) makes
   `workspace.GetQuery(id)` idempotent across re-mounts — same observable
   instance returned every time.

5. **Hub-level delete fast-path.** Deletes published via
   `IMeshChangeFeed` (the canonical post-`HandleDeleteNodeRequest`
   dispatch) bypass the per-provider change-notifier debounce and update
   the synced collection synchronously. Without this, cache-driven
   queries can stay stale after a delete for several seconds.

## When to use it (and when not)

| Use case | API |
|---|---|
| Live list of MeshNodes for a UI dropdown / picker | `workspace.GetQuery(id, queries...)` ← this |
| Live list of MeshNodes for a synced derived collection | `workspace.GetQuery(id, queries...)` ← this |
| One-shot "give me all nodes matching X right now" | `IMeshService.QueryAsync` |
| Read a specific node by path (writes followed by reads) | `workspace.GetMeshNodeStream(path)` (see [CqrsAndContentAccess](CqrsAndContentAccess.md)) |
| Autocomplete / prefix search | `IMeshService.AutocompleteAsync` |

**The rule:** if you'd otherwise call `IMeshService.ObserveQuery` and
manually merge multiple query streams' `QueryResultChange<T>` events into
a path-keyed dictionary — stop. That's exactly what `GetQuery` does, with
the dedup and gating already correct. Using `ObserveQuery` directly for
this is **always** a bug because:

- You'll forget to gate on every per-query Initial. Result: dropdown
  flashes empty before the slowest query finishes its Initial.
- You'll re-implement the path-keyed merge slightly differently. Result:
  dupes when two queries overlap.
- You'll subscribe to `IMeshQueryCore` (or a single provider) instead of
  the full `IMeshQueryProvider` enumeration. Result: static nodes are
  invisible, and only persisted ones surface. **This is the bug that
  produced an empty Agent dropdown even though MCP `nodeType:Agent`
  returned 9 entries.**

## Common queries you'll write

```csharp
// Single namespace, one type
workspace.GetQuery("agents", "namespace:Agent nodeType:Agent");

// Type alternation (agents OR models from any namespace)
workspace.GetQuery("agents-and-models",
    "namespace:Agent nodeType:Agent|LanguageModel",
    "namespace:Model nodeType:Agent|LanguageModel");

// Path hierarchy + global
workspace.GetQuery($"agents:{contextPath}",
    "namespace:Agent nodeType:Agent",                                       // global
    $"namespace:{contextPath} scope:selfAndAncestors nodeType:Agent");      // path-local
```

## Caching by id

The `id` parameter is just a key into a per-workspace registry:

```csharp
var first  = workspace.GetQuery("my-id", "namespace:Agent nodeType:Agent");
var second = workspace.GetQuery("my-id");      // no-args overload
ReferenceEquals(first, second).Should().BeTrue();
```

Pick stable ids — `$"chat-picker:{contextPath}"`, not `Guid.NewGuid()`.
Re-using the id across re-mounts means you reuse the upstream
subscription instead of cycling through provider Initial waves on every
component re-render.

## Content arrives typed

If your nodes carry typed content (`AgentConfiguration`, `ModelDefinition`,
etc.), make sure the type is registered in the hub's `TypeRegistry`. The
synced query reads `MeshNode.Content` deserialised against the hub's
`JsonSerializerOptions`. Missing TypeRegistry entry = `Content` arrives
as raw `JsonElement`, your `is T` casts fail, your collection looks
empty even though the snapshot has items. See
[AddingANewNodeType](AddingANewNodeType.md) → step 4 for the wiring.

## Tests

For any code that consumes `workspace.GetQuery`, write an integration
test with `MonolithMeshTestBase` that exercises the **same**
`workspace.GetQuery` call. Do **not** roll your own test harness with
`IMeshService.ObserveQuery` — that bypasses the exact code path you're
trying to test. Canonical example:
`test/MeshWeaver.Hosting.Monolith.Test/LanguageModelSyncedQueryTest.cs`.

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
            n.Name.Should().NotBeNullOrWhiteSpace();        // Empty Name = invisible UI rows
            n.Content.Should().BeOfType<AgentConfiguration>();  // JsonElement = silently dropped
        });
    }
}
```

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
// Forfeits caching. Use a stable scope-derived key.
```

## See also

- [AddingANewNodeType](AddingANewNodeType.md) — how to introduce a new
  node type so its instances surface in synced queries
- [CqrsAndContentAccess](CqrsAndContentAccess.md) — when to use synced
  queries vs `GetMeshNodeStream` (single-node) vs `QueryAsync` (one-shot)
- [AsynchronousCalls](AsynchronousCalls.md) — `IObservable` patterns and
  why you never `await` inside hub-reachable code
