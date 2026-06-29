---
Name: Synced Query Data Source
Category: Documentation
Description: Live, query-backed collections in a hub's workspace — populated and kept fresh by IMeshQueryProvider.Query, no Observe round-trip from inside the hub.
Icon: /static/DocContent/DataMesh/DataConfiguration/icon.svg
---

# Synced Query Data Source

A *synced query data source* is a live collection that lives inside a hub's workspace and stays in sync with a mesh query. The framework subscribes to `IMeshQueryProvider.Query<T>` when the hub starts, seeds the workspace's `EntityStore` with the initial result set, and continuously folds **Added / Updated / Removed** deltas into that same store via `IDataChangeNotifier`.

The payoff: hub-internal code — validators, layout areas, compile pipelines, access checks — reads its source data through the standard `workspace.GetStream<T>()` / `workspace.GetStream(new CollectionReference("name"))` surface. No `Observe` round-trip, no CQRS staleness lag, no `Observable.FromAsync` at every leaf. By the time the hub handles its first message, the synced collection is already populated.

<svg viewBox="0 0 760 310" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity="0.6"/>
    </marker>
  </defs>
  <rect x="10" y="30" width="140" height="56" rx="10" fill="#1e88e5"/>
  <text x="80" y="55" text-anchor="middle" fill="#fff" font-weight="bold">IMeshQuery</text>
  <text x="80" y="74" text-anchor="middle" fill="#fff" font-size="11">Query&lt;MeshNode&gt;</text>
  <rect x="10" y="110" width="140" height="56" rx="10" fill="#1e88e5"/>
  <text x="80" y="135" text-anchor="middle" fill="#fff" font-weight="bold">IMeshQuery</text>
  <text x="80" y="154" text-anchor="middle" fill="#fff" font-size="11">Query&lt;MeshNode&gt;</text>
  <text x="80" y="196" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11" font-style="italic">one per query string</text>
  <line x1="150" y1="58" x2="210" y2="130" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="150" y1="138" x2="210" y2="145" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="215" y="105" width="160" height="80" rx="10" fill="#5c6bc0"/>
  <text x="295" y="130" text-anchor="middle" fill="#fff" font-weight="bold">Scan / Fold</text>
  <text x="295" y="150" text-anchor="middle" fill="#fff" font-size="11">ImmutableDictionary</text>
  <text x="295" y="167" text-anchor="middle" fill="#fff" font-size="11">keyed by Path</text>
  <line x1="375" y1="145" x2="435" y2="145" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="440" y="105" width="150" height="80" rx="10" fill="#43a047"/>
  <text x="515" y="130" text-anchor="middle" fill="#fff" font-weight="bold">Initial Gate</text>
  <text x="515" y="150" text-anchor="middle" fill="#fff" font-size="11">suppress until all</text>
  <text x="515" y="167" text-anchor="middle" fill="#fff" font-size="11">queries fire Initial</text>
  <line x1="590" y1="145" x2="650" y2="145" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="655" y="105" width="95" height="80" rx="10" fill="#f57c00"/>
  <text x="703" y="140" text-anchor="middle" fill="#fff" font-weight="bold">Hub</text>
  <text x="703" y="158" text-anchor="middle" fill="#fff" font-size="11">Workspace</text>
  <rect x="235" y="228" width="120" height="38" rx="8" fill="#e53935"/>
  <text x="295" y="244" text-anchor="middle" fill="#fff" font-size="11" font-weight="bold">Added / Updated</text>
  <text x="295" y="259" text-anchor="middle" fill="#fff" font-size="11">/ Removed deltas</text>
  <line x1="295" y1="228" x2="295" y2="188" stroke="currentColor" stroke-opacity="0.5" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr)"/>
  <text x="380" y="280" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11">re-emits full snapshot on every change</text>
</svg>

*Synced query data source pipeline: mesh queries fold into a path-keyed dictionary, gated until all Initial events arrive, then emit live snapshots into the hub workspace.*

---

## When to use it

Use a synced query data source whenever a hub needs a *local view* of nodes that live elsewhere in the mesh.

| Use case | What you sync |
|---|---|
| **NodeType hubs** | `Sources` and `Tests` Code nodes — the compile pipeline reads these collections directly, zero round-trips |
| **Per-data-node hubs** | `AccessAssignments` — the access pipeline checks permissions synchronously without touching the security service |
| **Aggregator hubs** | Cross-namespace dashboards built from `nodeType:Order status:Open`, rendered straight from the workspace stream |
| **System defaults + extensions** | Agent, Model, Role — static built-ins appear on first subscribe via `IStaticNodeProvider`; user-created instances stream in as Added / Updated / Removed deltas |

For system-default / mesh-extension composition, see [Extensible Defaults](/Doc/Architecture/ExtensibleDefaults).

> **Tip:** Use `workspace.GetMeshNodeStream(path)` for one-shot single-node reads and for nodes you never keep in a collection. The synced collection shines for sets you read repeatedly — and it is bidirectional, so writes work through the same surface.

---

## How it works

The data source is built on `VirtualDataSource.WithVirtualType<T>` — the framework primitive for "this collection comes from an `IObservable` stream." The mesh adds a thin extension method `WithMeshQuery` that composes three pieces:

**1. Subscribe to each mesh query**

One `IMeshQueryCore.Query<MeshNode>` subscription per query string. Multi-query collections are fine — the result is their union. Each `QueryResultChange<MeshNode>` carries `Initial` / `Reset` / `Added` / `Updated` / `Removed` deltas together with the matching `MeshNode` payloads.

**2. Fold deltas into a path-keyed dictionary**

A single `Scan` accumulates an `ImmutableDictionary<string, MeshNode>` keyed by `MeshNode.Path`, spanning every change event from every query. It also tracks how many `Initial` / `Reset` events each query has produced. The result is always the union of every query's current matches, deduplicated by path.

**3. Gate the first emission until every query has sent its `Initial`**

A `Where(...)` clause suppresses emission until each query's Initial count reaches the configured provider count. The first `.Take(1)` consumer therefore sees a complete snapshot, not a partial one. After the gate opens, every change re-emits the full `IEnumerable<MeshNode>` — the dictionary's values.

A companion `BehaviorSubject<ImmutableHashSet<string>>` tracks the live path set in parallel. The `AddSyncedQuery` reducer reads it synchronously to decide whether this source `Owns` a given path before opening the per-node remote stream for a write.

> **Note:** There is no per-path read subscription. Read content comes from the `MeshNode` payloads carried by the query events. The synchronization protocol re-pushes those payloads on every owning-hub change, so the synced collection re-emits as soon as the query layer notices.

---

## Configuration

```csharp
config.AddData(data => data
    .WithVirtualDataSource("$mesh-sources", vs => vs.WithMeshQuery(
        query: $"namespace:{hubPath}/Source scope:subtree nodeType:Code",
        collectionName: "Sources")));
```

Multiple synced collections per hub are fine — each goes into its own virtual data source. The canonical example is `MeshDataSource` registration in `MeshDataSourceExtensions.AddMeshDataSource`: every per-node hub automatically gets `Sources` and `Tests` synced collections derived from the hub's path.

### API reference — extension methods

```csharp
public static VirtualDataSource WithMeshQuery(
    this VirtualDataSource ds,
    string query,
    string? collectionName = null);

public static VirtualDataSource WithMeshQuery<T>(
    this VirtualDataSource ds,
    string query,
    string? collectionName = null) where T : class;
```

| Parameter | Description |
|---|---|
| `query` | Mesh query string in the standard syntax (see [Query Syntax](/Doc/DataMesh/QuerySyntax)). Common shapes: `namespace:X scope:subtree nodeType:Y`, `path:X`, `path:X scope:descendants`, `namespace:X scope:nextLevel` (the next populated level — graph navigation). |
| `collectionName` | Workspace collection name. Defaults to `typeof(T).Name` (or `nameof(MeshNode)` on the non-generic overload). Required when the same `T` appears in multiple synced collections — for example, `Sources` and `Tests` both hold `MeshNode`. |

The non-generic overload is the everyday case (a collection of `MeshNode`). The generic overload accepts a content type and projects via `OfType<T>` — useful when the query selects a single content shape.

---

## Reading and writing

The synced collection has two distinct surfaces. **Reads** come from the dict-of-MeshNode snapshot the data source emits. **Writes** route through the per-node hub via the workspace's `(address, reference)` remote-stream cache.

### Read — subscribe to the snapshot observable

```csharp
var workspace = hub.GetWorkspace();
var collection = workspace.GetQuery(
    "my-collection-id",
    "namespace:Agent nodeType:Agent");

var sub = collection.Subscribe(snapshot =>
{
    // snapshot is IEnumerable<MeshNode> — the COMPLETE current set,
    // path-keyed and deduplicated. Rebuild your view from this each time.
});
```

Every emission is the full current collection, not individual deltas — the `Scan` inside `SyncedQueryMeshNodes` already merged them. The observable is `Replay(1).RefCount()`, so a late subscriber gets the cached latest snapshot immediately, and upstream subscriptions are shared across every consumer of the same id.

> For a single-node read by path, use `workspace.GetMeshNodeStream(path)`. `GetQuery` is for *collections*, not for known-path lookups.

### Write — Update on the per-node remote stream

```csharp
// AddSyncedQuery's reducer routes any
// workspace.GetStream(new MeshNodeReference(path)) call to
// GetRemoteStream<MeshNode, MeshNodeReference>(new Address(path), ref)
// when `path` is in the source's live path set (Owns(path)).
var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(path), new MeshNodeReference());

stream.Update(current => current is null
    ? null
    : new ChangeItem<MeshNode>(
        current with { Name = "New Name" },
        changedBy: hub.Address.ToString(),
        stream.StreamId,
        ChangeType.Full,
        stream.Hub.Version,
        Updates: null));
```

Because `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, ref)` caches per `(address, reference)`, every caller in the hub gets the same instance — write-paths share one upstream subscription per node. A write through this stream propagates to the owning per-node hub via the synchronization protocol, and the owning hub's update echoes back through the query layer to the next synced-collection emission.

---

## Live updates without polling

When any node is updated anywhere in the mesh, the change arrives in the synced collection through the upstream `IMeshQueryProvider.Query` subscription. The query layer already mirrors per-hub change notifications into `Updated` / `Removed` events; the `Scan` folds those into the dictionary and re-emits the snapshot on the next tick.

There is no manual cache to invalidate — the query stream *is* the cache.

In practice, a synced collection of `nodeType:Code` nodes re-emits within tens of milliseconds of a developer's save reaching persistence. Consumers such as the compile pipeline and side-menu listings re-render off the new snapshot automatically.

---

## Why this is safe — the actor model

A hub is a single-threaded actor. At any moment exactly **one** message is in flight, and the data source's stream subscription, the workspace's per-node remote-stream cache, and all user code that reads or writes through them run on that same thread. There are no concurrent callers, no torn reads, no partial state.

The actor model is the integrity guarantee. That is why the cache can be plain in-memory state — no locks, no compare-and-swap, no reconciliation logic.

Concretely: when your handler calls `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, ref).Update(...)`, the synchronization protocol routes the patch to the owning per-node hub, which is itself a single-threaded actor. It processes the update in order with every other write to that node. No two writes collide, and no reader ever observes a half-applied state.

### Workspace remote-stream cache — one stream per `(address, reference)`

The mechanism that makes the write path share a single subscription per node across all writers in the same hub is `Workspace._remoteStreamCache` (`src/MeshWeaver.Data/Workspace.cs`). It is a plain `ConcurrentDictionary<(Address, WorkspaceReference), ISynchronizationStream>` keyed by the inputs to `workspace.GetRemoteStream<TReduced, TReference>(addr, ref)`. The first call for a given key opens an external client subscription and stores the stream; every subsequent call returns the same instance.

This is exactly what the `MeshNodeReference(path)` reducer registered by `AddSyncedQuery` relies on: when it returns `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(new Address(path), new MeshNodeReference())`, every writer through the synced collection — and any other code in the workspace asking for the same `(addr, ref)` — gets back the same stream instance. One upstream pump per node, no matter how many writers share it.

**Cache eviction** is lazy and happens in two cases:

- When the cached stream's hub leaves `MessageHubRunLevel.Started` (disposed or failed), the next `GetRemoteStream` call replaces it with a fresh instance.
- When an explicit `IMeshChangeFeed` event reports a path change (ownership churn — see `Workspace.EvictForPath`), open subscribers stay attached to their existing stream while new callers bind to a fresh one tied to the re-activated owner.

The full integrity story is a plain dictionary, single-threaded access, and shared-by-default semantics — made safe entirely by the actor model.

The `RemoteStreamCacheTest` (`test/MeshWeaver.Query.Test`) pins this contract: two `GetRemoteStream(...)` calls for the same key are reference-equal, and a disposed stream is evicted before the next caller receives it.

---

## Caveat — RAM footprint

The only real cost is memory. Every synced collection holds its full result-set dictionary in the hub's address space. A query matching 100 nodes pins 100 MeshNodes' worth of memory on every subscriber hub. Keep queries narrow enough that the live set genuinely belongs in RAM.

---

## Testing

The synced query data source is a **production wiring**, not a side-channel. Tests must exercise it as it ships, not through test-only handlers. The pattern is fixed and short.

### 1. Define a NodeType that uses `WithMeshQuery`

Register a static `MeshNode` via `AddMeshNodes` whose `HubConfiguration` adds the synced data source — exactly the way a production NodeType (`Sources` / `Tests` / `AccessAssignments`) does it.

```csharp
protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    => base.ConfigureMesh(builder)
        .AddMeshNodes(new MeshNode("Subscriber", TestPartition)
        {
            Name      = "Subscriber",
            NodeType  = "Markdown",
            State     = MeshNodeState.Active,
            HubConfiguration = config => config.AddData(data =>
                data.WithVirtualDataSource("$mesh-subjects",
                    vs => vs.WithMeshQuery(
                        query: $"namespace:{SubjectsNamespace} scope:subtree nodeType:Markdown")))
        });
```

The subscriber's per-node hub now mirrors every matching node via the upstream mesh-query subscription — same wiring as production.

### 2. Drive the test from the client, never from `Mesh`

`Mesh` is a virtual coordinator. Real players are hubs created via `Mesh.ServiceProvider.CreateMessageHub(...)` (the test base does this through `GetClient()`). Post messages from the client so routing, serialization, and the synchronization protocol all run end to end.

### 3. Use standard data-layer messages — not custom request handlers

Reads and writes go through the framework's data-layer messages.

- `DataChangeRequest { Updates = [updatedNode] }` posted to the subscriber address writes through the synced data source's cached per-node remote stream → owning per-node hub persists.
- `GetDataRequest(new MeshNodeReference())` to a per-node hub address returns its current `MeshNode`.
- `IMeshService.UpdateNode(node)` updates a source per-node hub.

> **Do not write `GetMyThingRequest` / `WriteMyThingRequest` test-only handlers.** They route around the contract you are trying to test.

### 4. Verify with the existing test base helpers

`MonolithMeshTestBase` exposes:

- `NodeFactory.CreateNode(node)` — create a source node.
- `NodeFactory.UpdateNode(node)` — write at the source side.
- `ReadNodeAsync(path)` — read a `MeshNode` via `GetDataRequest + MeshNodeReference` on a dedicated reader hub.

Verification follows an observe-until-condition rhythm (mirrors `ObservableQueryTests`). The synced collection stays subscribed the whole time — no `Take(1)`, no draining.

### End-to-end test sketch

```csharp
[Fact]
public async Task DataChangeRequestOnSubscriber_PropagatesToOwningHub()
{
    var ct   = TestContext.Current.CancellationToken;
    var path = $"{SubjectsNamespace}/alpha";

    // Source-side state.
    await NodeFactory.CreateNode(MakeSubject("alpha", "Original"))
        .FirstAsync().ToTask(ct);
    await Task.Delay(500, ct);                 // synced collection picks it up.

    // Read the current value (standard data-layer read).
    var current = await ReadNodeAsync(path);

    // Write at the SUBSCRIBER via DataChangeRequest. The subscriber's
    // synced data source routes the update through its cached per-node
    // remote stream → owning per-node hub.
    await client.Observe(
            new DataChangeRequest { Updates = [current! with { Name = "Updated" }] },
            o => o.WithTarget(new Address(SubscriberPath)))
        .FirstAsync().ToTask(ct);
    await Task.Delay(500, ct);

    // Source side reflects the write.
    var reread = await ReadNodeAsync(path);
    reread!.Name.Should().Be("Updated");
}
```

---

## Related

- [Query Syntax](/Doc/DataMesh/QuerySyntax) — the query strings you pass to `WithMeshQuery`.
- [Data Configuration](/Doc/DataMesh/DataConfiguration) — broader data-source patterns (`AddSource`, `AddHubSource`, `WithInitialData`).
- [CQRS](/Doc/Architecture/CqrsAndContentAccess) — why this is preferred over re-querying on every read.
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — why `workspace.GetStream(...)` is the right primitive in hub-reachable code.
