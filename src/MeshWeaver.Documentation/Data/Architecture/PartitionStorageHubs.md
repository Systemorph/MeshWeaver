---
Name: Partition Storage Hubs
Category: Documentation
Description: Per-partition single-threaded MessageHub actors that own one IStorageAdapter each — bounded connection pools, registry-driven lifetime, no shared-pool exhaustion.
Icon: /static/DocContent/Architecture/icon.svg
---

Partition Storage Hubs make every storage *table* a single-threaded actor. Each `(schema, table)` pair gets one `MessageHub`, that hub owns the I/O for that one table, and the underlying resource is a `NpgsqlDataSource` with `MaxPoolSize=1`, a directory handle, an embedded-resource view, or an HTTP client. The hub's actor scheduler serialises every operation on that table — but operations on *different* tables in the same schema run in parallel, so within a partition the system has natural fine-grained concurrency.

The hub is a **queue with a TTL**, not a long-lived component bound to partition lifetime. It spawns on first use, runs the same standard handler config regardless of backend, and disposes itself after ~5 minutes idle. A subsequent request re-spawns it from cold cache.

# Why

The previous routing layer kept one shared `IStorageAdapter` per provider and let any caller touch it from any thread. For Postgres this meant every test that called `PostgreSqlFixture.CreateSchemaAdapterAsync` built a fresh `NpgsqlDataSource` whose connection pool was never disposed; CI hit `53300: sorry, too many clients already` after ~30 tests against a single shared container.

The deeper problem isn't a fixture bug — it's that **one shared multi-threaded adapter per partition forces every adapter implementation to defend itself against concurrent callers**. Postgres does this with a large connection pool. Cosmos does it with a thread-safe client. File-system adapters do it with locks. Every backend reinvents concurrency control for the same shape of work.

A single-threaded actor per partition solves it once: the dispatcher serialises, the resource shrinks to one, and lifetime maps to the partition's lifetime.

# Architecture

```
   IPartitionStorageProvider (per backend, e.g. PostgreSqlPartitionStorageProvider):
   - At startup: ObserveQuery("namespace:Admin/Partition nodeType:Partition")
                 → Dictionary<firstSegment, PartitionDefinition>. Kept live.
   - Matches(fullPath) = first segment ∈ dictionary
   - CreateAdapterForTable(def, table) → IStorageAdapter bound to (schema, table)

   PartitionStorageRouter (singleton, silo-wide):
   - ConcurrentDictionary<(schema, table), IMessageHub> hubs   (5-min idle eviction)
   - AddressFor(path):
        provider = first IPartitionStorageProvider where Matches(path)
        def      = provider.ResolveDefinition(path)
        table    = def.ResolveTable(path)
        return Address("storage/{def.Schema}/{table}")
   - EnsureHub((schema, table), provider, def):
        if hubs.TryGetValue(...) → return
        adapter = provider.CreateAdapterForTable(def, table)
        spawn MessageHub at Address("storage/{schema}/{table}") with the
        standard PartitionStorageHubConfig + adapter; restart idle timer
            │
   caller-hub.Observe(req, target = AddressFor(path))
   — direct dispatch, no routing hub on the message path
            │
            ▼
   Partition Storage Hub (one per (schema, table)):
   - standard PartitionStorageHubConfig — same for every backend
   - handlers: WriteBatchRequest(nodes[]), DeleteBatchRequest(paths[]),
               ReadRequest(path), ListChildPathsRequest(parent), …
   - hub body for a write batch:
        1. validate (permissions, schema, types)
        2. accept → update in-memory hub state if any
        3. spawn Activity entry
        4. adapter.BeginTransaction
        5. adapter.WriteBatch / adapter.DeleteBatch
        6. adapter.Commit
        7. emit Activity result (success / failure with row counts)
   - idle timer resets on every message; on timeout, hub disposes,
     adapter disposes, NpgsqlDataSource closes its single connection
```

## High parallelism within a partition

Each Postgres schema has multiple tables (`mesh_nodes`, `threads`, `activities`, `annotations`, `access`, `code`, `user_activities`). Splitting the actor along the table boundary means a thread message write does not block a permission update or an activity log in the same schema — each runs on its own dispatcher with its own connection. Cross-table reads (e.g. `Read(path)` where the table is determined by the path's satellite suffix) resolve to one specific hub via `PartitionDefinition.ResolveTable(path)`.

Total open Postgres connections per silo = `schemas × tables` (≈10 × 7 = 70 for a realistic deployment), bounded statically. Postgres's default `max_connections = 100` covers this with margin.

## No intermediate routing hub

The router is a singleton **registry**, not a hub. When a hub's handler calls `adapter.Write(node)`, the per-hub `RoutingProxyAdapter` resolves the partition-hub address and posts directly:

```csharp
public IObservable<MeshNode> Write(MeshNode node, JsonSerializerOptions opt)
{
    var addr = router.AddressFor(node.Path);
    return callerHub.Observe<WriteResponse>(
        new WriteRequest(node, opt),
        o => o.WithTarget(addr))
        .Select(r => r.Node);
}
```

The message goes from the caller hub directly to the partition-storage hub. There is no intermediate "storage routing hub" on the path — that would be a useless serialization point and an extra dispatcher hop.

## Backends

Each backend gets its own `IPartitionStorageProvider`. The contract is the same for all; only `CreateAdapterForTable` behaviour differs.

| Backend | Table dimension | `CreateAdapterForTable` |
|---|---|---|
| **Postgres** | Real — one table per satellite (`mesh_nodes`, `threads`, `activities`, …) | Builds fresh `NpgsqlDataSource(MaxPoolSize=1, SearchPath=schema)` per `(schema, table)`. One hub per `(schema, table)`. |
| **Cosmos DB** | Real — one container per logical table | Builds fresh `CosmosContainer` client per `(database, container)`. One hub per `(database, container)`. |
| **Azure Blob** | Degenerate — blob paths are the only namespace | Returns one shared `AzureBlobStorageAdapter` per container regardless of `table`. One hub per container. |
| **FileSystem** | Degenerate — directory paths only | Returns one shared `FileSystemStorageAdapter` per directory regardless of `table`. One hub per directory. |
| **Embedded resource** | Degenerate | Returns the same shared adapter; one hub per namespace. |
| **Static node** | Degenerate | Returns the same shared adapter; one hub per namespace. |
| **HTTP / remote mesh** | Degenerate | Returns the remote-proxy adapter; one hub per remote endpoint. |
| **In-memory** | Degenerate (tests) | Returns the same shared adapter; one hub per partition. |

The `(schema, table)` dimension only carries weight where the backend has real per-table I/O resources to bound. For everything else `table` is ignored and the hub count equals the partition count.

## Three-level resolution

A storage call resolves in three steps:

1. **Adapter type** — pick the first `IPartitionStorageProvider` whose `Matches(fullPath)` returns true. The check takes the **full path**, not just the first segment, so a provider can route `Admin/Partition/*` to Postgres while another provider routes `Admin/Settings/*` to embedded resources. First match wins; registration order is the routing table.
2. **Schema** — within the matched provider, the `PartitionDefinition` identifies the schema (Postgres), container (Cosmos), or directory (FileSystem).
3. **Table** — `PartitionDefinition.ResolveTable(path)` picks the specific table the node belongs to (`threads` for `_Thread` paths, `activities` for `_Activity`, `mesh_nodes` for primary content, etc.). For backends with no internal table concept (Embedded, FileSystem, StaticNode), there is exactly one logical table per schema.

The partition hub is keyed by `Address("storage/{schema}/{table}")`. Same `(schema, table)` → same hub → same adapter → same connection.

## Batch writes

`Write(IEnumerable<MeshNode>)` groups nodes by `(schema, table)` and forwards each group as a single `WriteRequest(nodes)` message to that group's hub. Each hub handles its slice sequentially. No cross-partition coordination, no two-phase commit — table-level actors are independent by construction, and the natural concurrency is along the table dimension.

## Lifetime

| Event | Reaction |
|---|---|
| First storage call for `(schema, table)` | Router resolves provider via `Matches`, calls `CreateAdapterForTable`, spawns hub at `storage/{schema}/{table}` with standard config, starts 5-min idle timer |
| Subsequent calls | Re-use the live hub, reset its idle timer |
| 5 minutes idle | Hub disposes itself; adapter disposes; underlying connection closes; entry removed from router dict |
| Partition definition changes (e.g. schema rename) | Provider's internal partition dictionary updates via its `ObserveQuery` subscription; future `Matches` reflects the change. Existing hubs are not actively destroyed — they idle out and the new definition takes effect on next spawn. |
| Silo shutdown | All live partition hubs disposed via parent-hub disposal chain |

A partition hub's resources never outlive the partition. There is no idle-timeout, no LRU cache — the registry stream is the single source of truth.

## Per-silo, not per-process

In Orleans, every silo runs its own `PartitionStorageRouter` and therefore spawns its own table-hubs. Total open Postgres connections from the mesh = `silos × schemas × tables`, fixed, not call-dependent. With ~7 tables per partition and 10 partitions, a 3-silo Orleans deployment opens roughly 210 connections globally — still small enough to tune `max_connections` once on the cluster, never call-dependent.

# Single-threaded as a primitive

Inside a partition hub, code can assume:

- **No concurrent calls** — the dispatcher serialises message handling.
- **No need for locks** — a hub-private `Dictionary` is safe; `ConcurrentDictionary` adds no value.
- **No connection pool** — `MaxPoolSize=1` is sufficient and forces fast queries.
- **Cold-observable execution is sequential** — `adapter.Write(node).Subscribe(...)` inside a handler runs to completion before the next message is dispatched.

The hub does **not** turn synchronous code async or async code single-threaded — it just guarantees the handler chain for one message completes before the chain for the next message starts.

# Provider contract

```csharp
public interface IPartitionStorageProvider
{
    string Name { get; }

    /// First-match-wins. fullPath is the candidate node path. Postgres
    /// implementation: extract first segment, check it against the live
    /// partition dictionary (populated via ObserveQuery on Admin/Partition/*
    /// at startup). Static providers: exact / prefix match on first segment.
    bool Matches(string fullPath);

    /// Returns the PartitionDefinition that owns this path. The router
    /// uses def.Schema and def.ResolveTable(path) to key the hub.
    /// Returns null if Matches(fullPath) would also be false (paired).
    PartitionDefinition? ResolveDefinition(string fullPath);

    /// Build a per-table adapter scoped to (def, table). For Postgres each
    /// (schema, table) pair gets a fresh NpgsqlDataSource with MaxPoolSize=1
    /// and SearchPath set to def.Schema. For static/read-only providers
    /// (Embedded, StaticNode) the table dimension is degenerate and the
    /// provider may return the same shared adapter for all tables.
    IStorageAdapter CreateAdapterForTable(PartitionDefinition def, string table);

    ImmutableHashSet<string> Contexts { get; }
}
```

The old `Adapter { get; }` getter on `IPartitionStorageProvider` is removed — there is no "the adapter" anymore, only adapters bound to a specific `(schema, table)`.

# Migration notes for callers

- **Don't construct `IStorageAdapter` directly outside of providers.** Adapters are owned by per-table hubs; bypassing the hub means bypassing the actor scheduler and the pool-size guarantee.
- **Don't share `NpgsqlDataSource` across schemas or tables.** Each `(schema, table)` hub builds its own with `MaxPoolSize=1`. Sharing is what produced the leak this design fixes.
- **`IStorageAdapter` is registered per hub, not per silo.** Each hub's `WithServices` registers a `RoutingProxyAdapter(IMessageHub, PartitionStorageRouter)`. The proxy forwards via `hub.Observe(req, target=partitionAddress)` — caller-hub posts directly to partition-hub, no intermediate routing hub on the message path.
- **Tests use `PartitionRegistry`, not direct `CreateSchemaAdapterAsync`.** Test fixtures boot a mini-mesh with a Postgres provider and register `Admin/Partition` nodes; the router's reactive subscription spawns hubs naturally.

See [Partitioned Persistence](PartitionedPersistence.md) for the original routing model, [Asynchronous Calls](AsynchronousCalls.md) for the actor-pattern primitives this builds on, and [Postgres Schema Architecture](PostgresSchemaArchitecture.md) for the schema layout.
