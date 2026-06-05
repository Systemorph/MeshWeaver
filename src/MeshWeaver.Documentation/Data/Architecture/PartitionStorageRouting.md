---
Name: Partition Storage Routing
Category: Architecture
Description: How MeshWeaver decides which storage adapter / schema / table an object lives in — partitions exist only for queries and object-mapping; schema creation is gated to partition-owning NodeTypes (User, Space)
Icon: Database
---

# Partition Storage Routing

A **partition** is a unit of physical storage isolation. On Postgres it is a **schema** (`{partition}.mesh_nodes` + satellite tables). Partitions are NOT a pervasive abstraction the rest of the system reasons about — they matter for exactly **two** things, and everything else is derived from NodeType configuration.

## Partitions matter for two things

### 1. Queries — fan to every adapter; absent partition → empty
A query is sent to **every** storage adapter. There is **no fan-out decisioning** in the query provider (no "which schemas, pin-vs-fan-out, satellite-vs-mesh_nodes" branching). Each adapter answers for its own data; **if the partition it would read doesn't exist, it returns an empty result** (Postgres `42P01` → empty), never an error and never a slow tree walk. The union of the adapters' answers is the query result.

### 2. Mapping an object to its storage adapter — longest-prefix-match wins
To route a single object (read/write/create) to its owning storage adapter, there is **no registry and no NodeType→schema map**. Routing is purely data-driven:

> **Ask every storage adapter for its longest stored path `P` that is a prefix of the target path (the target *starts with* `P`), ordered by length descending. Across all adapters, the adapter with the maximum matching-prefix length wins** — it owns the target path and persists/reads it. (This is `IStorageAdapter.FindBestPrefixMatch` fanned across every adapter, picking the max.)

The winning adapter is simply the one already holding the closest ancestor of the path. A partition root (`rbuergi`) is held by that partition's adapter, so `rbuergi/_UserActivity/x` longest-prefix-matches it and routes there. **If NO adapter has a matching prefix**, nothing owns the path — a read returns empty and a write is refused (unless it is a partition-owning create, below).

The create/read logic layered on top:

- **(a) Create of a partition-owning object** — if the object's NodeType is configured to own a partition (see below) and the operation is *create*, the storage adapter **creates the partition** (the schema + its tables) and **makes the creator the Admin** of that partition. Partition creation happens *here and only here* — never lazily on an arbitrary write. After this, the new partition root exists, so subsequent writes under it prefix-match the new adapter.
- **(b) Lookup** — if the partition exists (an adapter prefix-matches), continue the query against it; otherwise return empty.
- **(c) Find the table to persist within the winning schema** — the **top-level path segment is the partition (schema)** (= the winning adapter). The **table** within that schema is determined by the object's **NodeType configuration**: a NodeType is configured to get *its own schema* (it is a partition root) or *its own table* (a satellite table inside the owning partition's schema), or it defaults to the partition's primary `mesh_nodes` table.

> **Invariant: it is always clear who saves where.** Schema + table are a deterministic function of the object's NodeType configuration + its top-level partition. **If the partition does not exist, the write is refused** — the storage layer never conjures a schema for an unrecognised path segment. (This is the root-cause fix for the schema-corruption where any path segment — NodeType names, reserved words, request URLs — spawned a ghost schema.)

## NodeType configuration (the source of truth)

Each NodeType declares its storage shape **once, on its NodeType definition** (its `NodeTypeDefinition` content, set in the type's builder — e.g. `SpaceNodeType`, `UserNodeType`, `UserActivityNodeType`). This is implicit, type-level info — NOT a per-instance `MeshNode` property and NOT a hard-coded central dictionary or temporary registry.

**On create, the NodeType definition is loaded and consulted directly.** A `CreateNodeRequest` ships the node (including its NodeType); the create path loads that NodeType's definition and reads off it whether the type owns a partition / which table it persists to / etc. There is nothing to look up in a side registry — the NodeType definition node is the single source, read on demand. (Routing of *existing* objects, by contrast, needs none of this — it is the longest-prefix-match in §2.)

| NodeType | Configured as | Result |
|---|---|---|
| [`Space`](../../../MeshWeaver.Blazor.Portal/SpaceNodeType.cs) | **owns a partition** | top-level Space → its own schema; creator becomes Admin |
| [`User`](../../../MeshWeaver.Graph/Configuration/UserNodeType.cs) | **owns a partition** | top-level User → its own schema; creator becomes Admin |
| [`UserActivity`](../../../MeshWeaver.Graph/Configuration/UserActivityNodeType.cs) | **owns a table** | stored in its own satellite table inside the owning partition's schema |
| `Thread` / `ThreadMessage` | owns a table | satellite table (`threads`) |
| `AccessAssignment` | owns a table | satellite table (`access`) |
| *(default)* | — | the partition's primary `mesh_nodes` table |

This replaces both the hard-coded `PartitionDefinition.StandardTableMappings` / `NodeTypeToSuffix` dictionaries **and** the `_Thread`/`_Access`/… path-suffix string-matching: a node's storage table comes from **its NodeType's configuration**, not from the shape of its path. Adding a new satellite type is a one-line configuration on that NodeType — no central map to edit, no router branch to add. *This is what makes the config easy.*

## Consequences

- **No lazy schema creation.** Removing the path-router's "first segment → CREATE SCHEMA on first write" fallback. Only a partition-owning NodeType's *create* provisions a schema.
- **No query fan-out decisioning.** The query provider stops choosing schemas / pinning / satellite-branching; it asks every adapter and absent partitions answer empty. (Replaces `NeedsFanOut` / `ResolvePinnedPartition` / `EnumerateFanOutAsync`.)
- **No routing registry.** The owning adapter is found by longest-prefix-match across adapters (`FindBestPrefixMatch`, max wins) — not a NodeType→schema lookup table.
- **Deterministic persistence.** The **schema** is the winning prefix-match adapter; the **table** comes from the object's NodeType config. An absent partition (no adapter prefix-matches, and not a partition-owning create) **refuses the write** rather than corrupting storage.
