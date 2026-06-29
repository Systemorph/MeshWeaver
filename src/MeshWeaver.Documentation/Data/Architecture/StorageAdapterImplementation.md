---
Name: Storage Adapter Implementation
Category: Documentation
Description: How partition storage routing works (no Matches predicate, try-then-claim writes, fan-out reads) and how to implement IStorageAdapter and IPartitionStorageProvider correctly.
Icon: /static/DocContent/Architecture/icon.svg
---

# Storage Adapter Implementation

MeshWeaver's storage layer routes reads and writes across multiple backends without a central registry. There is **no global map of "which partition owns which path"** — routing is implicit, driven entirely by each adapter's return value.

This document explains how that routing works and how to implement the two contracts correctly.

<svg viewBox="0 0 760 340" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
<defs>
<marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
</marker>
<marker id="arr-ok" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#43a047"/>
</marker>
<marker id="arr-null" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#ef5350"/>
</marker>
</defs>
<rect x="290" y="10" width="180" height="44" rx="10" fill="#1e88e5"/>
<text x="380" y="37" text-anchor="middle" fill="#fff" font-weight="bold">PersistenceService</text>
<text x="380" y="52" text-anchor="middle" fill="#fff" font-size="11">Read / Write / Delete / Fan-out</text>
<line x1="380" y1="54" x2="380" y2="80" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
<rect x="60" y="80" width="150" height="40" rx="8" fill="#5c6bc0"/>
<text x="135" y="105" text-anchor="middle" fill="#fff">Adapter 1</text>
<rect x="305" y="80" width="150" height="40" rx="8" fill="#5c6bc0"/>
<text x="380" y="105" text-anchor="middle" fill="#fff">Adapter 2</text>
<rect x="550" y="80" width="150" height="40" rx="8" fill="#5c6bc0"/>
<text x="625" y="105" text-anchor="middle" fill="#fff">Adapter N</text>
<text x="258" y="103" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="11">→ try</text>
<text x="503" y="103" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="11">→ try</text>
<line x1="305" y1="100" x2="210" y2="100" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="455" y1="100" x2="550" y2="100" stroke="#90a4ae" stroke-width="1.5" stroke-dasharray="5,3"/>
<line x1="380" y1="80" x2="380" y2="80" stroke="#90a4ae" stroke-width="1.5"/>
<line x1="135" y1="120" x2="135" y2="170" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-ok)"/>
<text x="148" y="153" fill="#43a047" font-size="11">node (owned)</text>
<line x1="380" y1="120" x2="380" y2="170" stroke="#ef5350" stroke-width="1.5" marker-end="url(#arr-null)"/>
<text x="393" y="153" fill="#ef5350" font-size="11">null → skip</text>
<line x1="625" y1="120" x2="625" y2="170" stroke="#ef5350" stroke-width="1.5" marker-end="url(#arr-null)"/>
<text x="638" y="153" fill="#ef5350" font-size="11">null → error</text>
<rect x="60" y="170" width="150" height="40" rx="8" fill="#43a047"/>
<text x="135" y="195" text-anchor="middle" fill="#fff">Result returned</text>
<rect x="305" y="170" width="150" height="40" rx="8" fill="#5c6bc0" fill-opacity=".35"/>
<text x="380" y="190" text-anchor="middle" fill="currentColor" fill-opacity=".7">Adapter 2 not</text>
<text x="380" y="206" text-anchor="middle" fill="currentColor" fill-opacity=".7">reached</text>
<rect x="550" y="170" width="150" height="40" rx="8" fill="#e53935"/>
<text x="625" y="195" text-anchor="middle" fill="#fff">"could not save"</text>
<line x1="280" y1="80" x2="220" y2="100" stroke="none"/>
<rect x="60" y="260" width="155" height="60" rx="8" fill="#26a69a" fill-opacity=".25" stroke="#26a69a" stroke-width="1.2"/>
<text x="137" y="282" text-anchor="middle" fill="#26a69a" font-weight="bold" font-size="12">READ / WRITE</text>
<text x="137" y="298" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">Sequential (Concat)</text>
<text x="137" y="312" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">First non-null wins</text>
<rect x="300" y="260" width="155" height="60" rx="8" fill="#f57c00" fill-opacity=".2" stroke="#f57c00" stroke-width="1.2"/>
<text x="377" y="282" text-anchor="middle" fill="#f57c00" font-weight="bold" font-size="12">EXISTS / LIST</text>
<text x="377" y="298" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">Fan-out (all adapters)</text>
<text x="377" y="312" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">Results aggregated</text>
<rect x="540" y="260" width="175" height="60" rx="8" fill="#8e24aa" fill-opacity=".2" stroke="#8e24aa" stroke-width="1.2"/>
<text x="627" y="282" text-anchor="middle" fill="#8e24aa" font-weight="bold" font-size="12">READ-ONLY providers</text>
<text x="627" y="298" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">Skipped on Write</text>
<text x="627" y="312" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">IsReadOnly = true</text>
</svg>

*PersistenceService dispatches via try-then-claim for reads/writes (first non-null wins) and fans out across all adapters for existence/listing queries.*

---

## How Routing Works

`PersistenceService` is the singleton that coordinates all storage operations. It does not consult a registry or predicate before dispatching — it simply sequences adapters and lets their return values speak.

### The two contracts at a glance

| Contract | Method | Return | Meaning |
|---|---|---|---|
| `IStorageAdapter.Read(path)` | per-adapter | `IObservable<MeshNode?>` | Emits the node if owned; `null` if not. |
| `IStorageAdapter.Write(node)` | per-adapter | `IObservable<MeshNode?>` | Emits the saved node if accepted; `null` if declined. |
| `IStorageAdapter.Delete(path)` | per-adapter | `IObservable<string>` | Emits the deleted path; containment is per-adapter. |
| `IPartitionStorageProvider.IsReadOnly` | per-provider | `bool` | `true` excludes this provider's adapter from the write-claim chain. |

### Dispatch behaviour by operation

- **Read** — walks adapters sequentially (`Observable.Concat`); picks the first non-null result.
- **Write** — walks *writable* adapters (`IsReadOnly == false`) sequentially; first non-null wins. Throws "could not save" if every adapter returned `null`.
- **Delete** — containment-check and delete on every writable adapter; throws if no adapter held the path.
- **Exists / FindBestPrefixMatch / ResolvePath / ListChildPaths** — fan-out across *all* adapters (writable and read-only); aggregate results.

> **There is no `Matches(path)` predicate.** The question "is this mine?" is answered entirely inside each adapter's `Read` and `Write` implementation. That predicate was removed; see [Migration notes](#migration-notes-from-the-old-matches-design) below.

---

## Implementing IStorageAdapter

There are two archetypes. Choose the one that matches your adapter's role.

### A. The adapter owns its own data

This covers InMemory, FileSystem, PostgreSQL, Cosmos, and Blob adapters — any adapter that is the terminal store for a set of paths.

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

The key decision is in `ShouldAccept`. For an InMemory wildcard adapter that accepts everything, it is simply `!string.IsNullOrEmpty(GetFirstSegment(path))`. For a per-partition adapter scoped to one schema, it is `path.StartsWith(_schema + "/")`. There is no external routing layer that calls a `Matches` predicate — the decision is encapsulated here.

### B. The adapter routes to other adapters

Examples: `PostgreSqlPathRoutingAdapter` (one schema-bound adapter per partition), `VersionWritingStorageAdapter` (a decorator that chains a version-write).

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

The routing decision here is driven by the partition cache, not a path-match predicate.

---

## Implementing IPartitionStorageProvider

The provider is the thin wrapper that wires an adapter into the `PersistenceService` chain.

```csharp
public sealed class MyPartitionStorageProvider : IPartitionStorageProvider
{
    public string Name => "MyBackend";
    public bool IsReadOnly => false;          // false for InMemory/FS/PG/Cosmos/Blob
    public IStorageAdapter Adapter { get; }   // the actual adapter
    public PartitionDefinition? PartitionDefinition => null;  // null = backend-wide
}
```

Read-only providers (`EmbeddedResource`, `StaticNode`) set `IsReadOnly = true`. They still participate in **reads** — their adapter's `Read` method returns seed data — but `PersistenceService.Write` skips them entirely.

---

## The "No Async Ever" Rule

Every method on `IStorageAdapter` returns `IObservable<T>`. Use `Observable.FromAsync(ct => ...)` to bridge async-leaf calls (Npgsql, Azure SDK, HTTP client) inside the adapter. There is no `Task<T>` on the public surface and no `await` between adapters and the routing layer.

---

## Postgres-Specific: `PgPartitionCache`

The PostgreSQL provider does not enumerate schemas at startup. Instead, per first-segment, it maintains a `ReplaySubject<PartitionState>` seeded by an `information_schema.schemata` probe on first access. There are three states:

| State | Meaning | TTL |
|---|---|---|
| **Exists(def)** | Schema present — reads and writes route to its adapter. | 15 min |
| **PendingCreate(seg)** | Schema missing — lazy-create triggered on next write. | 1 min |
| **Absent** | Probe failed — reads/writes return `null` so try-then-claim falls through. | — |

**Cross-silo invalidation** is handled via PostgreSQL's `NOTIFY/LISTEN` mechanism. Migration V23 adds a Postgres trigger that fires `NOTIFY partition_changes` on every write to `admin.mesh_nodes` whose `namespace = 'Admin/Partition'`. `PgPartitionNotifyListener` (an `IHostedService` running on every silo) listens on that channel and calls `cache.Invalidate(ns)` on each event — the next access on any silo re-probes and picks up the new state automatically.

---

## Common Mistakes

> **Returning a thrown observable instead of `null` on decline.**
> `Observable.Throw<MeshNode>(...)` propagates up to the caller and breaks the chain. `Observable.Return<MeshNode?>(null)` lets the next provider try. Decline means `null`; throw means a real error.

> **Doing the containment check in the routing layer.**
> Adapters self-check. `PersistenceService` only sequences and aggregates — it never inspects path shapes itself.

> **Loading all partitions at startup.**
> The routing layer never enumerates. `PgPartitionCache` probes lazily per first-segment on first access.

> **Calling a `Matches()` method.**
> There is no `Matches`. That predicate was removed; routing is now driven entirely by `Read`/`Write` return values.

---

## Migration Notes from the Old `Matches` Design

| Old | New |
|---|---|
| `IObservable<bool> Matches(string)` | Removed — adapters self-decide via `Read`/`Write` return value. |
| `IObservable<PartitionDefinition?> ResolveDefinition(string)` | Removed — internal to each provider's cache. |
| `int Priority` on the provider | Removed — registration order determines try-then-claim sequence. |
| `PostgreSqlPartitionStorageProvider._partitionSubjects` per first-segment | Replaced by `PgPartitionCache` (single class, TTL-aware, pg_notify-invalidated). |
| `PostgreSqlPartitionSubscriptionHostedService` eagerly enumerated schemas | Now only seeds framework partitions from `IStaticNodeProvider`; lazy otherwise. |

---

## References

- `src/MeshWeaver.Mesh.Contract/Services/IStorageAdapter.cs` — the adapter contract.
- `src/MeshWeaver.Mesh.Contract/Services/IPartitionStorageProvider.cs` — the provider contract.
- `src/MeshWeaver.Hosting/Persistence/PersistenceService.cs` — try-then-claim write, fan-out read/delete.
- `src/MeshWeaver.Hosting.PostgreSql/PgPartitionCache.cs` — the per-namespace `ReplaySubject` cache.
- `src/MeshWeaver.Hosting.PostgreSql/PgPartitionNotifyListener.cs` — cross-silo pg_notify listener.
- `memex/aspire/Memex.Database.Migration/Migrations/V23_PartitionChangesNotify.cs` — the trigger migration.
