---
Name: Virtual Data Sources
Category: Documentation
Description: Hub data sources whose contents come from a live IObservable stream — computed views, query mirrors, cross-hub subscriptions, polled external systems.
Icon: /static/DocContent/DataMesh/DataConfiguration/icon.svg
---

# Virtual Data Sources

A *virtual data source* is a hub data source whose contents come from an
`IObservable<IEnumerable<T>>` stream rather than a static seed or a
direct persistence read. The framework subscribes to the stream on hub
init, seeds the workspace's `EntityStore` with the initial emission,
and folds every subsequent emission into the same store. Hub-internal
code reads via the standard `workspace.GetStream(...)` /
`workspace.GetStream<T>()` API — the synced collection looks identical
to any other registered type.

The infrastructure piece is
[`VirtualDataSource`](xref:MeshWeaver.Data.VirtualDataSource), built on
[`VirtualTypeSource<T>`](xref:MeshWeaver.Data.VirtualTypeSource`1). Two
companion pieces ship with it:

- [`WithVirtualType<T>(...)`](xref:MeshWeaver.Data.VirtualDataSource.WithVirtualType``1)
  — the general shape: any `IObservable<IEnumerable<T>>` is fair game.
- [`WithMeshQuery<T>(query)`](xref:MeshWeaver.Graph.SyncedQueryDataSourceExtensions.WithMeshQuery``1)
  — the convenience shape for the most common case: mesh-query result
  set as a live workspace collection. Documented in detail in
  [Synced Query Data Source](SyncedQueryDataSource).

# When to use them

Reach for a virtual data source whenever a hub needs a *local view* of
something that:

- changes over time, **and**
- is too small / too hot to round-trip on every read, **and**
- has a natural reactive source (a query, a poll, a parent hub's
  collection, an in-process event subject).

Concrete cases shipping in MeshWeaver today:

- **NodeType hubs** sync `Sources` and `Tests` Code-node collections —
  the compile pipeline reads from them.
- **Per-node hubs** (in the new access-control design) sync their
  `LocalAccessAssignments`, their parent's `EffectiveAssignments`, and
  combine them into their own `EffectiveAssignments` — the access
  pipeline reads from there and never round-trips storage.

If your need is "one-shot read of a single MeshNode" use
[`hub.GetMeshNode`](xref:MeshWeaver.Mesh.MeshNodeStreamExtensions.GetMeshNode)
instead. If you need to *write* via a collection, post
`UpdateNodeRequest` / `CreateNodeRequest` through `IMeshService` —
virtual collections are read-only mirrors.

# Registration

```csharp
config.AddData(data => data
    .WithVirtualDataSource("$my-source", vs => vs
        .WithVirtualType<MyType>(
            workspace => GetMyTypeStream(workspace),
            collectionName: "MyCollection")));
```

The stream provider receives the hub's [`IWorkspace`](xref:MeshWeaver.Data.IWorkspace)
so it can compose with other workspace state if it wants. Common shapes:

| Shape | Stream provider |
|---|---|
| Mesh query mirror | `workspace => provider.ObserveQuery<T>(MeshQueryRequest.FromQuery(q), opts).Select(c => c.Items)` — or just use `WithMeshQuery<T>(q)`. |
| Cross-hub subscription | `workspace => workspace.GetRemoteStream<TReduced, TRef>(siblingAddress, reference).Select(c => Project(c.Value))` |
| Polled external API | `workspace => Observable.Interval(TimeSpan.FromSeconds(30)).SelectMany(_ => Observable.FromAsync(FetchFromGitHub)).Select(items => (IEnumerable<T>)items)` |
| Computed projection | `workspace => workspace.GetStream<RawA>().CombineLatest(workspace.GetStream<RawB>(), Compose)` |
| In-process event source | `workspace => myEventSubject.Scan(...)` |

Multiple virtual types per data source are fine. Multiple virtual data
sources per hub are fine. Each registered virtual collection gets its
own slot in the workspace's `EntityStore`.

# Lifecycle

1. Hub starts → DataContext sets up data sources.
2. The virtual data source's
   [`SetupDataSourceStream`](xref:MeshWeaver.Data.VirtualDataSource.SetupDataSourceStream*)
   subscribes to the stream provider and folds every emission into the
   workspace stream via `stream.Update(...)`. The subscription is hot
   for the life of the data source.
3. Hub-internal code subscribes to `workspace.GetStream(...)` and
   stays subscribed — no `Take(1)`, no draining the stream after the
   first value.
4. Hub disposal tears down the subscription.

The framework caches the stream observable per `(hub, source)` via
`Replay(1).RefCount()`, so multiple consumers within the hub share a
single underlying subscription and the latest emission is always
available to a new subscriber without a fresh round-trip.

# Reading from a virtual collection

Inside a hub handler, service, or layout area:

```csharp
var workspace = hub.GetWorkspace();

// Single-collection-of-T case — long-lived subscription.
workspace.GetStream<MyType>()
    ?.Subscribe(items => /* react to every snapshot */);

// Multiple collections of the same T — disambiguate by name.
workspace.GetStream(new CollectionReference("Sources"))
    .Subscribe(change =>
    {
        var nodes = change.Value!.Instances.Values.OfType<MeshNode>();
        /* react to every snapshot */
    });
```

Subscribers receive the latest snapshot on subscribe (`Replay(1)`)
and every subsequent update — keep the subscription alive for the
life of the consumer.

# Cross-hub virtual data sources (parent-sync pattern)

A virtual data source's stream provider can subscribe to another hub
via [`workspace.GetRemoteStream<TReduced, TRef>`](xref:MeshWeaver.Data.IWorkspace.GetRemoteStream*).
This is how the new access-control system works: every per-node hub
syncs its parent's `EffectiveAssignments` collection, then exposes a
merged `EffectiveAssignments = parent ∪ local` for *its* children to
consume in turn.

```csharp
var parentAddress = new Address(parentPath);

// Subscribe to the parent hub's "EffectiveAssignments" collection.
var inherited = workspace
    .GetRemoteStream<InstanceCollection, CollectionReference>(
        parentAddress,
        new CollectionReference("EffectiveAssignments"))
    .Select(change => change.Value!.Instances.Values.Cast<AccessAssignment>());

// Merge with own local collection and surface as a new virtual collection.
config.AddData(data => data
    .WithVirtualDataSource("$inherited-access", vs => vs
        .WithVirtualType<AccessAssignment>(
            ws => inherited,
            collectionName: "InheritedEffectiveAssignments")));
```

In Orleans, the remote-stream subscription crosses silos via the
routing grain — same delivery path as `MeshNodeReference` reads.

# Why this is safe — the actor model

The hub is a single-threaded actor. The data source's subscription to
its source observable, the workspace cache, and any reader that reads
through `workspace.GetStream(...)` all run on the same single thread.
There are no other callers, no concurrent updates to the workspace,
no torn reads. That is why caching is just plain in-memory state: the
actor model is the integrity guarantee.

# Caveat — RAM footprint

The only real cost is memory: the synced collection replicates the
underlying state in the hub's address space. Pick the source stream
(or query) narrow enough that the live set actually belongs in RAM.

# Related

- [Synced Query Data Source](SyncedQueryDataSource) — `WithMeshQuery<T>`,
  the most common shape.
- [Data Configuration](DataConfiguration) — broader data-source
  patterns (`AddSource`, `AddHubSource`, `WithInitialData`).
- [Access Control](../Architecture/AccessControl) — the per-node-hub
  cache pattern in production use.
- [CQRS & Content Access](../Architecture/CqrsAndContentAccess) — why
  query mirrors are preferred over re-querying on every read.
- [Asynchronous Calls](../Architecture/AsynchronousCalls) — why
  `workspace.GetStream(...)` is the right primitive in hub-reachable
  code.
