---
Name: Synced Query Data Source
Category: Documentation
Description: Live, query-backed collections in a hub's workspace — populated and kept fresh by IMeshQueryProvider.ObserveQuery, no Observe round-trip from inside the hub.
Icon: /static/DocContent/DataMesh/DataConfiguration/icon.svg
---

# Synced Query Data Source

A *synced query data source* is a virtual collection on a hub's workspace whose
contents are the live result set of a mesh query. The framework subscribes to
[`IMeshQueryProvider.ObserveQuery<T>`](xref:MeshWeaver.Mesh.Services.IMeshQueryProvider.ObserveQuery``1)
on hub initialization, seeds the workspace's `EntityStore` with the initial
match set, and folds subsequent **Added / Updated / Removed** deltas into the
same store via [`IDataChangeNotifier`](xref:MeshWeaver.Mesh.Services.IDataChangeNotifier).

**The point**: hub-internal code (validators, layout areas, compile pipeline,
access checks) reads its source data via the standard
`workspace.GetStream<T>()` /
`workspace.GetStream(new CollectionReference("name"))` — no `Observe`
round-trip, no CQRS staleness lag, no `Observable.FromAsync` at the leaf.
By the time the hub processes its first message, the synced collection is
already populated.

# When to use it

Use a synced query data source whenever a hub needs a *local view* of nodes
that live elsewhere in the mesh:

- **NodeType hubs** keep their **`Sources`** / **`Tests`** Code-node
  collections in sync. The compile pipeline reads from these collections —
  zero round-trips, source updates trigger automatic re-sync.
- **Per-data-node hubs** can synchronously expose their own
  **`AccessAssignments`** so the access pipeline never has to round-trip to
  the security service for a routine permission check.
- **Aggregator hubs** can build cross-namespace dashboards by syncing the
  query result of `nodeType:Order status:Open` and rendering from the
  workspace stream.
- **NodeTypes that ship system defaults plus mesh-level extensions**
  (Agent, Model, Role) compose this with `IStaticNodeProvider` so the
  built-ins appear in the synced collection on first subscribe and
  user-created instances stream in via Added/Updated/Removed deltas.
  See [Extensible Defaults](../Architecture/ExtensibleDefaults).

Use it for one-shot reads (`hub.GetMeshNode`) or collections you only ever
read once. For everything else — including collections you *write to* — the
synced collection is the right primitive: it's bidirectional.

# How it works

The data source is built on
[`VirtualDataSource.WithVirtualType<T>`](xref:MeshWeaver.Data.VirtualDataSource.WithVirtualType``1)
— the framework's primitive for "this collection comes from an `IObservable`
stream". The mesh adds a thin extension method
[`WithMeshQuery`](xref:MeshWeaver.Graph.SyncedQueryDataSourceExtensions.WithMeshQuery*)
that composes three pieces:

1. **Observe each mesh query.** One
   [`IMeshQueryCore.ObserveQuery<MeshNode>`](xref:MeshWeaver.Mesh.Services.IMeshQueryCore.ObserveQuery``1)
   subscription per query string (multi-query collections are fine — the
   result is the union). Each `QueryResultChange<MeshNode>` carries
   `Initial` / `Reset` / `Added` / `Updated` / `Removed` deltas and the
   matched `MeshNode` payloads.
2. **Fold deltas into a path-keyed dictionary.** A single `Scan`
   accumulates `ImmutableDictionary<string, MeshNode>` keyed by
   `MeshNode.Path` from every change event (across all queries) and
   tracks how many `Initial`/`Reset` events each query has produced. The
   dictionary is the union of every query's matches, deduplicated by path.
3. **Gate the first emission until every query has produced its `Initial`.**
   The downstream `Where(...)` suppresses emission until each query's
   per-query Initial-count reaches the provider count, so the first
   `.Take(1)` consumer sees a complete snapshot rather than a partial one.
   After the gate opens, every change re-emits the full
   `IEnumerable<MeshNode>` (i.e. `dict.Values`).

A side `BehaviorSubject<ImmutableHashSet<string>>` tracks the live path set
in parallel — the `AddSyncedQuery` reducer reads it synchronously to decide
whether this source `Owns` a given path before opening the per-node remote
stream for a write.

There is no per-path read subscription. Read content comes from the
`MeshNode` payloads carried by the query events; the synchronization
protocol re-pushes those payloads on every owning-hub change, so the
synced collection re-emits as soon as the query layer notices.

# Configuration

```csharp
config.AddData(data => data
    .WithVirtualDataSource("$mesh-sources", vs => vs.WithMeshQuery(
        query: $"namespace:{hubPath}/Source scope:subtree nodeType:Code",
        collectionName: "Sources")));
```

Multiple synced collections per hub are fine — each goes into its own
virtual data source. The framework's `MeshDataSource` registration in
[`MeshDataSourceExtensions.AddMeshDataSource`](xref:MeshWeaver.Graph.MeshDataSourceExtensions)
is the canonical example: every per-node hub automatically gets `Sources`
and `Tests` synced collections derived from the hub's path.

## API reference — extension methods

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
| `query` | Mesh query string in the standard syntax (see [Query Syntax](QuerySyntax)). Common shapes: `namespace:X scope:subtree nodeType:Y`, `path:X`, `path:X scope:descendants`. |
| `collectionName` | Workspace collection name. Defaults to `typeof(T).Name` (or `nameof(MeshNode)` on the non-generic overload). Required when the same `T` appears in multiple synced collections (e.g. `Sources` + `Tests` both hold `MeshNode`). |

The non-generic overload is the everyday case (collection of `MeshNode`).
The generic overload accepts a content type and projects via `OfType<T>` —
useful when the query selects a single content shape.

# Reading and writing

The synced collection has two distinct read and write surfaces. The
**read** surface is the dict-of-MeshNode snapshot the data source emits;
the **write** surface goes through the per-node hub via the workspace's
`(address, reference)` remote-stream cache.

## Read — subscribe to the snapshot observable

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

Every emission carries the full current collection, not deltas — the
Scan inside `SyncedQueryMeshNodes` already merged them. The observable
is `Replay(1).RefCount()`, so a late subscriber gets the cached latest
snapshot immediately and the upstream subscriptions are shared across
every consumer of the same id.

For a single-node read by path, use
[`workspace.GetMeshNodeStream(path)`](xref:MeshWeaver.Mesh.MeshNodeStreamExtensions.GetMeshNodeStream*)
— `GetQuery` is for *collections*, not for a known-path lookup.

## Write — Update on the per-node remote stream

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

Because `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, ref)`
caches per `(address, reference)`, every caller in the hub gets the same
instance — write-paths share one upstream subscription per node, and a
write made through this stream propagates to the owning per-node hub via
the synchronization protocol. The owning hub's update echoes back to the
query layer (and from there to the synced collection's next emission).

# Live updates without polling

When a node is updated anywhere in the mesh, the change reaches the
synced collection via the upstream `IMeshQueryProvider.ObserveQuery`
stream — the query layer already mirrors per-hub change notifications
into `Updated` / `Removed` events. The Scan folds those into the
dictionary and re-emits the snapshot on the next tick. There is no
manual cache to invalidate — the query stream *is* the cache.

Practical implication: a synced collection of `nodeType:Code` nodes
re-emits within ~tens of ms of a developer's save reaching persistence;
consumers (compile pipeline, side-menu listings) re-render off the new
snapshot.

# Why this is safe — the actor model

A hub is a single-threaded actor. At any moment, **one** message is in
flight on the hub, and the data source's stream subscription, the
workspace's per-node remote-stream cache, and any user code that reads
or writes through them all run on that single thread. There are no
"other callers", no concurrent updates to the cache, no torn reads.
The actor model is the integrity guarantee — that is why the cache can
be plain in-memory state with no locks, no compare-and-swap, no
reconciliation logic.

Concretely: when your handler does
`workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, ref).Update(...)`,
the synchronization protocol routes the patch to the owning per-node
hub, which is itself a single-threaded actor that processes the update
in order with every other write to that node. No two writes ever
collide. No reader ever observes a half-applied state.

## Workspace remote-stream cache — one stream per `(address, reference)`

The thing that makes the synced data source's write path share a single
subscription per node with any other writer in the same hub is
`Workspace._remoteStreamCache` (`src/MeshWeaver.Data/Workspace.cs`). It
is a plain
`ConcurrentDictionary<(Address, WorkspaceReference), ISynchronizationStream>`
keyed by exactly the inputs to `workspace.GetRemoteStream<TReduced, TReference>(addr, ref)`.
The first call for a given key opens an external client subscription and
stores the stream; every subsequent call returns the same instance.

This is what the `MeshNodeReference(path)` reducer registered by
`AddSyncedQuery` relies on: when the reducer returns
`workspace.GetRemoteStream<MeshNode, MeshNodeReference>(new Address(path), new MeshNodeReference())`,
every writer through the synced collection — and any other code in the
workspace asking for the same `(addr, ref)` — gets back the same stream
instance. One upstream pump per node, regardless of how many writers
share it.

The cache evicts entries lazily:

- When the cached stream's hub leaves `MessageHubRunLevel.Started`
  (disposed / failed) the next `GetRemoteStream` call replaces it with
  a fresh instance.
- When an explicit `IMeshChangeFeed` event reports a path change
  (eviction tied to ownership churn — see `Workspace.EvictForPath`),
  open subscribers stay attached to their stream while new callers get
  a fresh one bound to the re-activated owner.

That is the entire integrity story: a plain dictionary, single-threaded
access, shared-by-default semantics. The actor model is what lets it be
plain — every reader and writer for `(addr, ref)` reaches the same
stream because they all run on the same hub.

The `RemoteStreamCacheTest` (`test/MeshWeaver.Query.Test`) pins this
contract: two `GetRemoteStream(...)` calls for the same key are
reference-equal; a disposed stream is evicted before the next caller
hands it out.

# Caveat — RAM footprint

The only real cost is memory: every synced collection holds the full
result-set dictionary in the hub's address space. A query that selects
100 nodes pins 100 MeshNodes' worth of memory on every subscriber hub.
Pick the query narrowly enough that the live set actually belongs in
RAM.

# Testing

The synced query data source is a **production wiring**, not a
side-channel: tests must exercise it as it ships, not through
test-only handlers. The pattern is fixed and short.

## 1. Define a NodeType that uses `WithMeshQuery`

A test class registers a static <see langword="MeshNode"/> via
`AddMeshNodes` whose `HubConfiguration` adds the synced data source —
exactly the way a production NodeType (`Sources` / `Tests` / `AccessAssignments`)
does it.

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

The subscriber's per-node hub now mirrors every matching node via the
upstream mesh-query subscription — same wiring as production.

## 2. Drive the test from the **client**, never from `Mesh`

`Mesh` is a virtual coordinator. Real players are hubs created via
`Mesh.ServiceProvider.CreateMessageHub(...)` (the test base does this
through `GetClient()`). Tests post messages from the client so
routing, serialization, and the synchronization protocol all run end
to end.

## 3. Use **standard data-layer messages** — not custom request handlers

Reads and writes go through the framework's data-layer messages.

- `DataChangeRequest { Updates = [updatedNode] }` posted to the
  subscriber address writes through the synced data source's cached
  per-node remote stream → owning per-node hub persists.
- `GetDataRequest(new MeshNodeReference())` to a per-node hub address
  returns its current `MeshNode`.
- `IMeshService.UpdateNode(node)` updates a source per-node hub.

**Do not write `GetMyThingRequest` / `WriteMyThingRequest` test-only
handlers.** They route around the contract you are trying to test.

## 4. Verify with the existing test base helpers

`MonolithMeshTestBase` exposes:

- `NodeFactory.CreateNode(node)` — create a source node.
- `NodeFactory.UpdateNode(node)` — write at the source side.
- `ReadNodeAsync(path)` — read a `MeshNode` via `GetDataRequest +
  MeshNodeReference` on a dedicated reader hub.

Verification is a `Task.Delay(_) → assert` rhythm (mirrors
`ObservableQueryTests`); the synced collection stays subscribed the
whole time — no `Take(1)`, no draining.

## End-to-end test sketch

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

# Related

- [Query Syntax](QuerySyntax) — the query strings you pass to `WithMeshQuery`.
- [Data Configuration](DataConfiguration) — broader data-source patterns
  (`AddSource`, `AddHubSource`, `WithInitialData`).
- [CQRS](../Architecture/CqrsAndContentAccess) — why this is preferred
  over re-querying on every read.
- [Asynchronous Calls](../Architecture/AsynchronousCalls) — why
  `workspace.GetStream(...)` is the right primitive in hub-reachable code.
