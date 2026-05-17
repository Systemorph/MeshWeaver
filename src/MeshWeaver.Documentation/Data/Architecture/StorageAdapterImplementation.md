---
Name: Storage Adapter Implementation
Category: Documentation
Description: How partition storage routing works (no Matches predicate, try-then-claim writes, fan-out reads) and how to implement IStorageAdapter and IPartitionStorageProvider correctly.
Icon: /static/DocContent/Architecture/icon.svg
---

# How storage routing works

There is **no central registry** of "which partition owns which path". Routing is implicit — every adapter knows its own scope and signals via its return value whether a given operation applies to it.

## Two contracts, two flavours

| Contract | Method | Return | Meaning |
|---|---|---|---|
| `IStorageAdapter.Read(path)` | per-adapter | `IObservable<MeshNode?>` | Emits the node if owned, `null` if not. |
| `IStorageAdapter.Write(node)` | per-adapter | `IObservable<MeshNode?>` | Emits the saved node if accepted, `null` if declined. |
| `IStorageAdapter.Delete(path)` | per-adapter | `IObservable<string>` | Emits the deleted path. Containment is per-adapter — see notes below. |
| `IPartitionStorageProvider.IsReadOnly` | per-provider | `bool` | `true` excludes the provider's adapter from the write-claim chain. |

`PersistenceService` is the singleton fan-out:

- **Read**: walks adapters sequentially (`Observable.Concat`), picks the first non-null result.
- **Write**: walks **writable** adapters (`IsReadOnly == false`) sequentially; first non-null wins; throws "could not save" if every adapter returned `null`.
- **Delete**: containment-check + delete on every writable adapter; throws if no adapter held the path.
- **Exists / FindBestPrefixMatch / ResolvePath / ListChildPaths**: fan-out across **every** adapter (writable and read-only); aggregate.

There is no `Matches(path)` predicate on either contract. The "is this mine?" decision lives entirely inside each adapter's read/write method.

# How to implement IStorageAdapter

Two minimum-viable shapes:

## A. The adapter holds its own data (InMemory, FileSystem, PG, Cosmos, Blob)

```csharp
public sealed class MyStorageAdapter : IStorageAdapter
{
    private readonly ConcurrentDictionary<string, MeshNode> _nodes = new();

    public IObservable<MeshNode?> Read(string path, JsonSerializerOptions opts)
        => Observable.Defer(() =>
        {
            _nodes.TryGetValue(Normalize(path), out var node);
            return Observable.Return(node);   // null if not present — caller's chain skips us
        });

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions opts)
        => Observable.Defer(() =>
        {
            // Decision point: does this adapter accept this path?
            if (!ShouldAccept(node.Path))
                return Observable.Return<MeshNode?>(null);   // try-then-claim falls through
            _nodes[Normalize(node.Path)] = node;
            return Observable.Return<MeshNode?>(node);
        });

    public IObservable<string> Delete(string path)
        => Observable.Defer(() =>
        {
            // Containment check happens here; if we don't own it we still
            // emit the path (PersistenceService.Delete reads back to decide
            // who actually deleted).
            _nodes.TryRemove(Normalize(path), out _);
            return Observable.Return(path);
        });
    // … remaining methods follow the same shape.
}
```

The crucial line is the `ShouldAccept` predicate inside `Write`. For an InMemory wildcard, that's `!string.IsNullOrEmpty(GetFirstSegment(path))`. For a per-partition adapter (one schema), it's `path.StartsWith(_schema + "/")`. There is **no external `Matches`** the routing layer asks.

## B. The adapter is a router that delegates to other adapters

Examples: `PostgreSqlPathRoutingAdapter` (one schema-bound adapter per partition), `VersionWritingStorageAdapter` (decorator that chains a version-write).

```csharp
public sealed class MyRoutingAdapter : IStorageAdapter
{
    private readonly IPartitionStateCache _cache;

    public IObservable<MeshNode?> Write(MeshNode node, JsonSerializerOptions opts)
        => _cache.GetOrProbe(GetFirstSegment(node.Path)).Take(1).SelectMany(state =>
            state switch
            {
                PartitionState.Exists e        => GetOrCreateSchemaAdapter(e.Def).Write(node, opts),
                PartitionState.PendingCreate p => LazyCreateThenWrite(p, node, opts),
                _                              => Observable.Return<MeshNode?>(null),
            });
}
```

The routing decision is driven by the partition cache, not a path-matches predicate.

# How to implement IPartitionStorageProvider

```csharp
public sealed class MyPartitionStorageProvider : IPartitionStorageProvider
{
    public string Name => "MyBackend";
    public bool IsReadOnly => false;          // false for InMemory/FS/PG/Cosmos/Blob
    public IStorageAdapter Adapter { get; }    // the actual adapter
    public PartitionDefinition? PartitionDefinition => null;  // null = backend-wide
}
```

Read-only providers (`EmbeddedResource`, `StaticNode`) set `IsReadOnly = true`. They still participate in **reads** (their `Adapter.Read` returns the seed data), but `PersistenceService.Write` skips them.

# The "no async ever" rule

Every method on `IStorageAdapter` returns `IObservable<T>`. `Observable.FromAsync(ct => ...)` bridges an async-leaf (Npgsql, Azure SDK, HTTP client) inside the adapter. No `Task<T>` on the public surface, no `await` between adapters and the routing layer.

# Postgres-specific: `PgPartitionCache`

The PG provider doesn't enumerate schemas. Per first-segment, a `ReplaySubject<PartitionState>` is seeded by an `information_schema.schemata` probe on first access. Three states:

- **Exists(def)** — schema present; reads + writes route to its adapter (positive TTL 15 min).
- **PendingCreate(seg)** — schema missing, lazy-create on next write (negative TTL 1 min).
- **Absent** — probe failed; reads/writes return null so the try-then-claim chain falls through.

Cross-silo invalidation: migration V23 adds a Postgres trigger that fires `NOTIFY partition_changes` on every `admin.mesh_nodes` write whose `namespace = 'Admin/Partition'`. `PgPartitionNotifyListener` (an `IHostedService` on every silo) LISTENs the channel and calls `cache.Invalidate(ns)` on each event. The next access on any silo re-probes and picks up the new state.

# Common mistakes

- **Returning a thrown observable instead of null on decline.** `Observable.Throw<MeshNode>(...)` propagates up to the caller; `Observable.Return<MeshNode?>(null)` lets the next provider try. Decline = null. Throw = real error.
- **Doing the containment check in the routing layer.** Adapters self-check. The routing layer (`PersistenceService`) only sequences and aggregates.
- **Loading all partitions at startup.** The routing layer never enumerates. `PgPartitionCache` probes lazily per first-segment on first access.
- **Using `Matches()`.** There is no `Matches`. That predicate was removed; routing is now driven by `Read`/`Write` return values.

# Migration notes from the old `Matches` design

| Old | New |
|---|---|
| `IObservable<bool> Matches(string)` | _removed_ — adapters self-decide via Read/Write return value |
| `IObservable<PartitionDefinition?> ResolveDefinition(string)` | _removed_ — internal to each provider's cache |
| `int Priority` on the provider | _removed_ — registration order determines try-then-claim sequence |
| `PostgreSqlPartitionStorageProvider._partitionSubjects` per first-segment | `PgPartitionCache` (single class, TTL-aware, pg_notify-invalidated) |
| `PostgreSqlPartitionSubscriptionHostedService` eagerly enumerated schemas | Only seeds framework partitions from `IStaticNodeProvider`; lazy otherwise |

# References

- `src/MeshWeaver.Mesh.Contract/Services/IStorageAdapter.cs` — the contract.
- `src/MeshWeaver.Mesh.Contract/Services/IPartitionStorageProvider.cs` — the provider contract.
- `src/MeshWeaver.Hosting/Persistence/PersistenceService.cs` — try-then-claim Write, fan-out Read/Delete.
- `src/MeshWeaver.Hosting.PostgreSql/PgPartitionCache.cs` — the per-namespace ReplaySubject cache.
- `src/MeshWeaver.Hosting.PostgreSql/PgPartitionNotifyListener.cs` — cross-silo pg_notify listener.
- `memex/aspire/Memex.Database.Migration/Migrations/V23_PartitionChangesNotify.cs` — the trigger migration.
