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

## The only framework partitions: `public`, `admin`, `auth`

Beyond per-User/Space partitions, the clean model keeps exactly three system schemas, all created **eagerly by the migration** (never lazily, never by an app write):

| Schema | Purpose | Who writes |
|---|---|---|
| `public` | shared tables + central main-node index + the `ensure_partition_schema` stored proc | migration |
| `admin` | version tracking + global catalogs (agents / models / roles) | system, via normal persistence |
| `auth` | access-object lookup **mirror** (User/Group/Role/VUser/ApiToken/Space rows) | **trigger only — application code NEVER writes to `auth`** (`PartitionWriteGuardValidator` rule 1 blocks it). The V27 `mirror_access_object_to_auth_schema` trigger populates it; the migration creates the schema so the trigger has a destination. |

**Legacy partitions are gone (full cut).** `Portal` / `Kernel` session partitions are removed — compilation / script execution is an **Activity** in the owning partition's `activities` table, not a `kernel` schema (the standalone `kernel/*` address was retired; the kernel runs inside the Activity MeshNode hub). The global `_Access` / `_Activity` / `_UserActivity` / `_Thread` satellite partitions and their global `AccessAssignment`s are removed too: per-partition `_Access` holds grants, and the system identity gets `Permission.All` from the `PermissionEvaluator` fast-path (no data-model grant). `DefaultPartitionProvider` now seeds only `Admin` + `Auth`.

## Implementation status (2026-06-05)

**Done (the ghost-schema corruption fix):**
- `NodeTypeDefinition.OwnsPartition` / `StorageTable` — the declarative, type-level config (set on `Space`, `User`; `StorageTable="user_activities"` on `UserActivity`).
- [`OwnsPartitionProvisioningValidator`](../../../MeshWeaver.Graph/Security/OwnsPartitionProvisioningValidator.cs) — the **one** schema-creation trigger. Reads `OwnsPartition` off the NodeType definition (via `FindStaticNode`), requires top-level, and provisions before the root write. Replaces `SpaceTopLevelValidator` (deleted) and User-onboarding's reliance on lazy create. Registered centrally in `AddRowLevelSecurity`.
- [`IPartitionStorageProvider.EnsurePartitionProvisioned`](../../../MeshWeaver.Mesh.Contract/Services/IPartitionStorageProvider.cs) — reactive (`IObservable<Unit>`), promise-cached per schema, bridged through a per-adapter `IIoPool` (`pg:Postgres`, cap 1). **No `Observable.FromAsync`** (see [ControlledIoPooling](ControlledIoPooling.md)).
- [`PostgreSqlPathRoutingAdapter`](../../../MeshWeaver.Hosting.PostgreSql/PostgreSqlPathRoutingAdapter.cs) — the lazy `EnsureSchemaForPartitionSync` is **deleted** from both write paths (`RouteWrite` + `CreateAdapterForTable`). A write to an unprovisioned partition now faults `42P01` ("no partition, no write") instead of conjuring a ghost schema.
- `auth` created eagerly by the migration; system-identity activity tracking suppressed (it would otherwise write a `system-security` ghost partition).

**Still design / migration debt (the broader query redesign, tracked separately):**
- Longest-prefix-match cross-adapter routing (§2) — `FindBestPrefixMatch` exists; the PG router still resolves by first-segment.
- Retiring `NodeTypeToSuffix` / `StandardTableMappings` path-suffix matching in favour of reading `StorageTable` everywhere (the `StorageTable` field is declared and provisioned, not yet the sole routing source).
- Query fan-to-all replacing `NeedsFanOut` / `ResolvePinnedPartition` / `EnumerateFanOutAsync`.
