---
Name: Virtual Data Sources
Category: Documentation
Description: Hub data sources whose contents come from a live IObservable stream — computed views, query mirrors, cross-hub subscriptions, polled external systems.
Icon: /static/DocContent/DataMesh/DataConfiguration/icon.svg
---

# Virtual Data Sources

A *virtual data source* bridges the reactive world of `IObservable<T>` and the workspace's `EntityStore`. Instead of seeding data from a static snapshot or a persistence read, the hub subscribes to a live stream — and every emission is folded directly into the workspace. Code inside the hub reads the result through the standard `workspace.GetStream<T>()` API, with no idea whether the backing data came from a database or a computed reactive pipeline.

The infrastructure lives in [`VirtualDataSource`](xref:MeshWeaver.Data.VirtualDataSource), built on [`VirtualTypeSource<T>`](xref:MeshWeaver.Data.VirtualTypeSource`1). Two registration helpers ship with it:
<svg viewBox="0 0 760 200" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
<defs>
<marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
</marker>
</defs>
<rect x="10" y="60" width="160" height="80" rx="10" fill="#1565c0" opacity="0.92"/>
<text x="90" y="96" text-anchor="middle" fill="#fff" font-weight="bold">Stream Provider</text>
<text x="90" y="114" text-anchor="middle" fill="#bbdefb" font-size="11">IObservable&lt;IEnumerable&lt;T&gt;&gt;</text>
<rect x="300" y="40" width="160" height="120" rx="10" fill="#1b5e20" opacity="0.92"/>
<text x="380" y="80" text-anchor="middle" fill="#fff" font-weight="bold">Virtual</text>
<text x="380" y="98" text-anchor="middle" fill="#fff" font-weight="bold">Data Source</text>
<text x="380" y="120" text-anchor="middle" fill="#a5d6a7" font-size="11">stream.Update(...)</text>
<text x="380" y="138" text-anchor="middle" fill="#a5d6a7" font-size="11">Replay(1).RefCount()</text>
<rect x="585" y="20" width="160" height="55" rx="10" fill="#4a148c" opacity="0.9"/>
<text x="665" y="44" text-anchor="middle" fill="#fff" font-weight="bold">EntityStore</text>
<text x="665" y="62" text-anchor="middle" fill="#ce93d8" font-size="11">workspace cache</text>
<rect x="585" y="125" width="160" height="55" rx="10" fill="#e65100" opacity="0.9"/>
<text x="665" y="149" text-anchor="middle" fill="#fff" font-weight="bold">Consumers</text>
<text x="665" y="167" text-anchor="middle" fill="#ffcc80" font-size="11">workspace.GetStream&lt;T&gt;()</text>
<line x1="170" y1="100" x2="298" y2="100" stroke="#90a4ae" stroke-width="2" marker-end="url(#arr)"/>
<text x="234" y="94" text-anchor="middle" fill="currentColor" fill-opacity="0.6" font-size="11">subscribe</text>
<line x1="460" y1="75" x2="583" y2="55" stroke="#90a4ae" stroke-width="2" marker-end="url(#arr)"/>
<text x="522" y="58" text-anchor="middle" fill="currentColor" fill-opacity="0.6" font-size="11">fold emissions</text>
<line x1="460" y1="125" x2="583" y2="148" stroke="#90a4ae" stroke-width="2" marker-end="url(#arr)"/>
<text x="522" y="148" text-anchor="middle" fill="currentColor" fill-opacity="0.6" font-size="11">latest snapshot</text>
<line x1="665" y1="75" x2="665" y2="123" stroke="#90a4ae" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr)"/>
</svg>
*Virtual data source pipeline: a live observable stream is folded into the workspace's EntityStore, where hub consumers read it via the standard `GetStream<T>()` API.*

| Helper | Use when |
|---|---|
| [`WithVirtualType<T>(...)`](xref:MeshWeaver.Data.VirtualDataSource.WithVirtualType``1) | General case — any `IObservable<IEnumerable<T>>` is fair game. |
| [`WithMeshQuery<T>(query)`](xref:MeshWeaver.Graph.SyncedQueryDataSourceExtensions.WithMeshQuery``1) | Most common case — a mesh-query result set kept live in the workspace. Documented in [Synced Query Data Source](SyncedQueryDataSource). |

---

## When to reach for a virtual data source

A virtual data source earns its keep when a hub needs a *local view* of something that:

- changes over time, **and**
- is too hot (or too frequently accessed) to round-trip on every read, **and**
- has a natural reactive source — a mesh query, a polled API, a parent hub's collection, or an in-process event subject.

**Real examples shipping in MeshWeaver today:**

- **NodeType hubs** sync their `Sources` and `Tests` Code-node collections; the compile pipeline reads from them without hitting storage.
- **Per-node hubs** (access-control design) sync their `LocalAccessAssignments` and the parent hub's `EffectiveAssignments`, then merge them into a combined `EffectiveAssignments` that child hubs in turn inherit.

> **Not a fit for virtual data sources?**
> - Need a one-shot read of a single `MeshNode`? Use [`hub.GetMeshNode`](xref:MeshWeaver.Mesh.MeshNodeStreamExtensions.GetMeshNode).
> - Need to *write* via a collection? Post `UpdateNodeRequest` / `CreateNodeRequest` through `IMeshService` — virtual collections are read-only mirrors.

---

## Registration

```csharp
config.AddData(data => data
    .WithVirtualDataSource("$my-source", vs => vs
        .WithVirtualType<MyType>(
            workspace => GetMyTypeStream(workspace),
            collectionName: "MyCollection")));
```

The stream provider receives the hub's [`IWorkspace`](xref:MeshWeaver.Data.IWorkspace), so it can compose with other workspace state. The table below shows the most common shapes:

| Pattern | Stream provider expression |
|---|---|
| Mesh query mirror | `ws => provider.Query<T>(MeshQueryRequest.FromQuery(q), opts).Select(c => c.Items)` — or just `WithMeshQuery<T>(q)`. |
| Cross-hub subscription | `ws => ws.GetRemoteStream<TReduced, TRef>(siblingAddress, ref).Select(c => Project(c.Value))` |
| Polled external API | `ws => Observable.Interval(TimeSpan.FromSeconds(30)).SelectMany(_ => Observable.FromAsync(FetchFromGitHub)).Select(items => (IEnumerable<T>)items)` |
| Computed projection | `ws => ws.GetStream<RawA>().CombineLatest(ws.GetStream<RawB>(), Compose)` |
| In-process event subject | `ws => myEventSubject.Scan(ImmutableList<T>.Empty, (acc, e) => acc.Add(e))` |

Multiple virtual types per data source, and multiple virtual data sources per hub, are both fine. Each registered collection gets its own slot in the workspace's `EntityStore`.

---

## Lifecycle

Understanding what happens under the hood makes it easier to reason about timing and disposal:

1. **Hub starts** — `DataContext` initialises all registered data sources.
2. **Subscription opens** — [`SetupDataSourceStream`](xref:MeshWeaver.Data.VirtualDataSource.SetupDataSourceStream*) subscribes to the stream provider and folds every emission into the workspace via `stream.Update(...)`. The subscription stays open for the life of the data source.
3. **Consumers subscribe** — hub-internal code subscribes to `workspace.GetStream(...)` and remains subscribed; no `Take(1)`, no draining after the first value.
4. **Hub disposes** — the subscription is torn down automatically.

The framework wraps the observable in `Replay(1).RefCount()`, so multiple consumers within the hub share a single underlying subscription and the latest emission is always immediately available to a new subscriber.

---

## Reading from a virtual collection

Inside a hub handler, service, or layout area:

```csharp
var workspace = hub.GetWorkspace();

// Single-collection-of-T — long-lived subscription.
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

Subscribers receive the latest snapshot immediately on subscribe (thanks to `Replay(1)`), then every subsequent update. Keep the subscription alive for the life of the consumer.

---

## Cross-hub virtual data sources (parent-sync pattern)

A virtual data source's stream provider can subscribe to any other hub via [`workspace.GetRemoteStream<TReduced, TRef>`](xref:MeshWeaver.Data.IWorkspace.GetRemoteStream*). This is how the access-control system is wired: every per-node hub pulls its parent's `EffectiveAssignments` collection and then surfaces a merged view — `parent ∪ local` — for its own children to consume in turn.

```csharp
var parentAddress = new Address(parentPath);

// Subscribe to the parent hub's "EffectiveAssignments" collection.
var inherited = workspace
    .GetRemoteStream<InstanceCollection, CollectionReference>(
        parentAddress,
        new CollectionReference("EffectiveAssignments"))
    .Select(change => change.Value!.Instances.Values.Cast<AccessAssignment>());

// Merge with the local collection and surface as a new virtual collection.
config.AddData(data => data
    .WithVirtualDataSource("$inherited-access", vs => vs
        .WithVirtualType<AccessAssignment>(
            ws => inherited,
            collectionName: "InheritedEffectiveAssignments")));
```

In an Orleans cluster the remote-stream subscription crosses silos via the routing grain — the same delivery path used for `MeshNodeReference` reads.

---

## Why this is safe — the actor model

The hub is a **single-threaded actor**. The data source's subscription, the workspace cache, and every reader that calls `workspace.GetStream(...)` all run on that same single thread. There are no concurrent updates, no torn reads, and no locking needed. The actor model *is* the integrity guarantee; the in-memory cache just benefits from it.

---

## Caveat — RAM footprint

The only real cost is memory: a synced virtual collection replicates the underlying state inside the hub's address space. Choose the source stream (or query predicate) narrow enough that the live set genuinely belongs in RAM, rather than a full-table mirror.

---

## Live example

The snippet below renders a live summary of the available stream shapes so you can compare them at a glance.

```csharp --render VirtualDataSourcePatterns --show-code
MeshWeaver.Layout.Controls.Markdown(@"
| Pattern | Stream provider expression |
|---|---|
| Mesh query mirror | `WithMeshQuery<T>(query)` |
| Cross-hub subscription | `workspace.GetRemoteStream<TReduced, TRef>(addr, ref)` |
| Polled external API | `Observable.Interval(30s).SelectMany(_ => FetchAsync())` |
| Computed projection | `GetStream<A>().CombineLatest(GetStream<B>(), Compose)` |
| In-process event | `mySubject.Scan(ImmutableList.Empty, (acc, e) => acc.Add(e))` |
")
```

---

## Related

- [Synced Query Data Source](SyncedQueryDataSource) — `WithMeshQuery<T>`, the most common virtual-source shape.
- [Data Configuration](DataConfiguration) — broader data-source patterns (`AddSource`, `AddHubSource`, `WithInitialData`).
- [Access Control](../Architecture/AccessControl) — the per-node-hub cache pattern in production use.
- [CQRS & Content Access](../Architecture/CqrsAndContentAccess) — why query mirrors are preferred over re-querying on every read.
- [Asynchronous Calls](../Architecture/AsynchronousCalls) — why `workspace.GetStream(...)` is the right primitive in hub-reachable code.
