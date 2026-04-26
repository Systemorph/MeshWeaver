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

Don't use it for one-shot reads (use [`hub.GetMeshNode`](xref:MeshWeaver.Mesh.MeshNodeStreamExtensions.GetMeshNode))
or for collections you write to (synced collections are read-only mirrors —
writes go through `IMeshService.CreateNode` / `UpdateNode`).

# How it works

The data source is built on
[`VirtualDataSource.WithVirtualType<T>`](xref:MeshWeaver.Data.VirtualDataSource.WithVirtualType``1)
— the framework's primitive for "this collection comes from an `IObservable`
stream". The mesh adds a thin extension method
[`WithMeshQuery<T>(query, collectionName)`](xref:MeshWeaver.Graph.SyncedQueryDataSourceExtensions.WithMeshQuery``1)
that wires `IMeshQueryProvider.ObserveQuery` into the stream provider.

The lifecycle:

1. Hub starts → DataContext initializes data sources.
2. Synced data source's `InitializeAsync` subscribes to
   `IMeshQueryProvider.ObserveQuery<T>(MeshQueryRequest.FromQuery(q))`.
3. Initial emission (`ChangeType.Initial`) seeds the `EntityStore` —
   the workspace now holds every matching node.
4. `IDataChangeNotifier` pushes write notifications across the bus.
5. Subsequent emissions (`Added` / `Updated` / `Removed`) fold into the
   same store via the existing `VirtualDataSource` stream-update plumbing.
6. Hub-internal code reads via `workspace.GetStream(new CollectionReference("name"))`.

# Configuration

```csharp
config.AddData(data => data
    .WithVirtualDataSource("$mesh-orders", vs => vs.WithMeshQuery<Order>(
        query: $"namespace:{hubPath}/Orders scope:subtree nodeType:Order",
        collectionName: "Orders")));
```

Multiple synced collections per hub are fine — each goes into its own
virtual data source. The framework's `MeshDataSource` registration in
[`MeshDataSourceExtensions.AddMeshDataSource`](xref:MeshWeaver.Graph.MeshDataSourceExtensions)
is the canonical example: every per-node hub automatically gets `Sources`
and `Tests` synced collections derived from the hub's path.

## API reference — extension method

```csharp
public static VirtualDataSource WithMeshQuery<T>(
    this VirtualDataSource ds,
    string query,
    string? collectionName = null) where T : class
```

| Parameter | Description |
|---|---|
| `query` | Mesh query string in the standard syntax (see [Query Syntax](QuerySyntax)). Common shapes: `namespace:X scope:subtree nodeType:Y`, `path:X`, `path:X scope:descendants`. |
| `collectionName` | Workspace collection name. Defaults to `typeof(T).Name`. Required when the same `T` appears in multiple synced collections (e.g. `Sources` + `Tests` both hold `MeshNode`). |

# Reading from the synced collection

Inside a hub handler / service / layout area, read like any other workspace
collection:

```csharp
var workspace = hub.GetWorkspace();
var sources = workspace.GetStream(new CollectionReference("Sources"));
sources
    .Take(1)
    .Subscribe(change =>
    {
        var meshNodes = change.Value!.Instances.Values.OfType<MeshNode>();
        foreach (var node in meshNodes)
            // ...
    });
```

For typed access via `workspace.GetStream<T>()`, only one collection per `T`
is allowed in the workspace. If you have multiple synced collections of the
same `T` (e.g. `Sources` + `Tests` both `MeshNode`), use the
`CollectionReference("name")` form above to disambiguate.

# Live updates without polling

You don't need to re-query: the synced data source is *live*. When a node
matching the query is created / updated / deleted anywhere in the mesh,
`IDataChangeNotifier` pushes the change to the data source, which folds it
into the workspace's `EntityStore`. Anything subscribed to
`workspace.GetStream(...)` sees the new state on the next tick.

Practical implication: a NodeType hub's compile pipeline reads from
`Sources`. When a developer edits `Source/Foo.cs`, the change notifier
delivers the `Updated` delta to the synced collection — the next compile
already sees the new content. **No cache invalidation needed**; the cache
key is the *contents* of the synced collection (specifically, the maximum
`LastModified` across the items).

# Caveats

- **Initialization order**: the synced collection isn't populated until
  the hub's data context finishes initializing. Hub flows that read from
  it during `WithInitialization(...)` must compose with `Take(1)`
  on the workspace stream rather than expecting synchronous availability.
- **Read-only**: the synced collection mirrors the *external* mesh state.
  Writing to it locally does not propagate back to the source nodes. To
  modify the underlying nodes, post `UpdateNodeRequest` /
  `CreateNodeRequest` via `IMeshService` — the change notifier will push
  the result back into the synced collection.
- **No cross-silo round-trip in the read path**: the mesh-query providers
  resolve locally per silo. In Orleans, a query for nodes in another
  silo's partition fans out via the routing layer; the synced data source
  on your local hub holds the snapshot returned from that fan-out plus
  any subsequent live updates pushed via the change notifier.
- **One subscription per hub per query**: the framework caches the
  observable per `(hub, query)` pair via `Replay(1).RefCount()`, so
  multiple consumers within the hub share a single underlying subscription.

# Related

- [Query Syntax](QuerySyntax) — the query strings you pass to `WithMeshQuery`.
- [Data Configuration](DataConfiguration) — broader data-source patterns
  (`AddSource`, `AddHubSource`, `WithInitialData`).
- [CQRS](../Architecture/CqrsAndContentAccess) — why this is preferred
  over re-querying on every read.
- [Asynchronous Calls](../Architecture/AsynchronousCalls) — why
  `workspace.GetStream(...)` is the right primitive in hub-reachable code.
