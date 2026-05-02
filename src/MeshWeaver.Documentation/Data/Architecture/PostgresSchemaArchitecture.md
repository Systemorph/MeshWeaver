---
Name: Postgres Schema Architecture
Category: Documentation
Description: Per-partition Postgres schemas, satellite tables, triggers, and the dual-gate access model. Authoritative reference for anyone querying or migrating MeshWeaver's Postgres database.
Icon: /static/DocContent/Architecture/icon.svg
---

> 🚨 **TL;DR — `public.mesh_nodes` is empty by design.**
> Every mesh node lives in a **per-partition schema** (`acme.mesh_nodes`, `user.mesh_nodes`, `dav.mesh_nodes`, …). The `public` schema only holds infrastructure tables (`partition_access`, `searchable_schemas`, `user_effective_permissions`, …). If you `SELECT … FROM public.mesh_nodes` you will always get zero rows, no matter how full the mesh is.

This doc is the deep companion to [Partitioned Persistence](xref:Architecture/PartitionedPersistence). The other doc covers the routing layer that sits in front of the database; this one covers what's actually in the database.

# Per-partition schema model

The first segment of a node's path (lowercased and SQL-sanitised) becomes the **Postgres schema name**:

| Path | Schema |
|---|---|
| `ACME/Project/Foo` | `acme` |
| `User/rbuergi/Notes` | `user` |
| `DAV/Underwriting/AlpenLloyd2026` | `dav` |
| `123-org/Foo` | `_123_org` |
| `org.with.dots/Foo` | `org_with_dots` |

The sanitiser is `PostgreSqlPartitionedStoreFactory.SanitizeSchemaName` — lowercases, replaces non-alphanumeric with `_`, prefixes leading digits with `_`.

Schemas excluded from partition discovery (they are infrastructure or satellite-only):

```
admin, portal, kernel,
_access, _address_, _graph, _settings, _tracking, _thread, _source, _test,
login, markdown, onboarding, welcome, settings, storage,
mesh, thread, agent, partition, organization, vuser,
public, information_schema, pg_catalog, pg_toast,
*_versions
```

The canonical discovery query (used by `DiscoverPartitionsAsync` and the migration script):

```sql
SELECT schema_name FROM information_schema.schemata s
WHERE EXISTS (
    SELECT 1 FROM information_schema.tables t
    WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes')
  AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
  AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\';
```

Implementation: `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPartitionedStoreFactory.cs:269`.

# Per-schema table layout

Every partition schema contains:

| Table | Purpose | Routes for |
|---|---|---|
| `mesh_nodes` | Primary entities | All "main" node types |
| `activities` | Satellite | `Activity` |
| `user_activities` | Satellite | `UserActivity` (high-volume time-series) |
| `threads` | Satellite | `Thread`, `ThreadMessage` |
| `access` | Satellite | `AccessAssignment` |
| `code` | Satellite | `Code` (under `Source/` and `Test/` namespaces) |
| `annotations` | Satellite | `Comment`, `Approval`, `TrackedChange` |
| `partition_objects` | Internal | Non-mesh partition data |
| `change_logs` | Bundled activity log | (internal) |
| `user_activity` | Per-user access patterns | (internal) |

Optional sibling schema `{schema}_versions` for partitions with `Versioned = true`:

| Table | Purpose |
|---|---|
| `mesh_node_history` | Append-only history of every `mesh_nodes` write |

The mesh DDL plus all triggers and stored procedures are emitted by `PostgreSqlSchemaInitializer.GetMeshSchemaScript` (`src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSchemaInitializer.cs:277`).

# NodeType → table routing

A write does **not** pick its destination table from the C# `NodeType` string alone — it picks based on **the path itself**, by longest-suffix match. Defined in `PartitionDefinition.StandardTableMappings` (`src/MeshWeaver.Mesh.Contract/PartitionDefinition.cs:66`):

```
"_Activity"      -> activities
"_UserActivity"  -> user_activities
"_Thread"        -> threads
"_ThreadMessage" -> threads
"_Access"        -> access
"_Tracking"      -> annotations
"_Approval"      -> annotations
"_Comment"       -> annotations
"Source"         -> code
"Test"           -> code
```

`PartitionDefinition.ResolveTable(path)` scans the path for the longest matching suffix. Fallbacks:

- If no path-segment match and `nodeType` is provided: `ResolveTableByNodeType(nodeType)` (`PartitionDefinition.cs:105`).
- Otherwise: `mesh_nodes`.

Implementation: `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlStorageAdapter.cs:45`.

> ⚠ **Footgun — wrong segment, wrong table.**
> If you write an `AccessAssignment` whose namespace does **not** end in `_Access` (e.g. you write `Admin/Groups/G1` instead of `Admin/Groups/_Access/G1`), the row lands in `mesh_nodes` instead of `access`. The `access_changed` trigger will never fire, and `rebuild_user_effective_permissions` will not see the assignment. This was the bug behind Repair v1 (`memex/aspire/Memex.Database.Migration/Program.cs:133`).

# `public` schema — infrastructure only

| Table | Purpose |
|---|---|
| `partition_access` | Binary "user X has any access to partition P" gate. PK `(user_id, partition)`. Populated by per-schema `rebuild_user_effective_permissions`. |
| `searchable_schemas` | Schemas that cross-schema search (`search_across_schemas`) iterates over. Repopulated on every migration run. |
| `node_type_permissions` | `(node_type, public_read)`. Allows certain node types to skip per-row permission checks (still subject to partition gate). |
| `user_effective_permissions` and `_shadow` | Denormalised cache of every (user, path-prefix, permission) tuple. Shadow is rebuilt then atomically swapped (`PostgreSqlSchemaInitializer.cs:542`). |
| `change_logs` | Partition-level change feed. |

These five tables are the entire job of the `public` schema. **No mesh nodes ever live here.**

# Triggers and the permission-rebuild chain

```
INSERT/UPDATE/DELETE on {schema}.access
        │
        ▼
trg_access_changed()           ← extracts accessObject from new/old content
        │
        ├── if accessObject IS NOT NULL:
        │       SELECT {schema}.rebuild_user_permissions_for(accessObject)
        │       (per-user fast path, won't lock other users)
        │
        └── else:
                SELECT {schema}.rebuild_user_effective_permissions()
                (full rebuild: locks shadow table for the whole partition)
                Repopulates partition_access for every user that ends up
                with Read at any path in this partition.
```

```
INSERT/UPDATE on {schema}.mesh_nodes
        │
        ▼
trg_mesh_node_to_history()     ← cross-schema INSERT into {schema}_versions.mesh_node_history
        │
        ▼ (separate trigger, conditional on subscriber)
notify_mesh_node_changes()     ← LISTEN/NOTIFY for live subscribers
```

Source: `PostgreSqlSchemaInitializer.cs:717` (access), `:796` (notify), `:827` (history).

# Two-gate access model

A read of `acme.mesh_nodes` must pass **both** gates:

1. **Partition gate** — `EXISTS (SELECT 1 FROM public.partition_access WHERE user_id = $me AND partition = 'acme')`.
   - If you have no row here, you cannot read **anything** in the partition, even rows you would otherwise be allowed to read.
2. **Node gate** — `user_effective_permissions` row in the partition's own schema (`acme.user_effective_permissions`) with the longest-matching `node_path_prefix`.
   - `public_read = true` in `node_type_permissions` skips this gate but **not** the partition gate.

Cross-schema search (`public.search_across_schemas`) iterates `searchable_schemas`, applies both gates per schema, and returns only rows where both pass. See `PostgreSqlSchemaInitializer.cs:34`.

# Versioning schemas

Partitions with `Versioned = true` (default for content partitions) get a sibling `{schema}_versions` schema holding only `mesh_node_history`. Primary key is `(namespace, id, version)`; an extra `changed_by` column records authorship. Written by the cross-schema `mesh_node_copy_to_history` trigger on every primary-table write. Direct INSERTs into `mesh_node_history` (e.g. during a migration) bypass the trigger and preserve audit fidelity.

# Repair migrations

`memex/aspire/Memex.Database.Migration/Program.cs` runs idempotent **schema initialisation** on every start (`PostgreSqlSchemaInitializer.InitializeAsync`) plus **versioned data repairs** that run once per DB. Current versions:

| Version | Fix |
|---|---|
| v1 | Move misrouted `AccessAssignment` rows from `mesh_nodes` to `access` and add `/_Access` to namespace |
| v2 | Re-run schema init per partition + populate `partition_access` |
| v3 | Drop rogue schemas accidentally created from path segments |
| v4 | Upgrade user self-assignments from `Viewer` to `Admin` |
| v5 | Ensure every `User` node has an Admin self-assignment + rebuild permissions |
| v6 | Fix `search_across_schemas` to enforce `partition_access` |
| v7 | Deploy per-user permission-rebuild trigger function |
| v8 | Fix `ThreadMessage.MainNode` to point at the thread's content node, not the thread path |
| v9 | Rename `_Source/_Test` namespace segments to `Source/Test` |

DB version is stored in `admin.mesh_nodes` at `(namespace='', id='db_version')`.

# 🚨 Footguns — read this once, never trip again

> 🚨 **`public.mesh_nodes` is empty.** Every "I queried Postgres and the row isn't there" report has come from looking in `public.*` instead of the partition schema. Use the discovery query above first.

> 🚨 **Satellite tables are routed by path segment, not nodeType.** If you bulk-insert via SQL or via `mesh_nodes` directly bypassing the storage adapter, double-check the path contains the satellite suffix. Otherwise the row lands in `mesh_nodes` and the corresponding triggers (especially `access_changed`) never fire.

> 🚨 **`rebuild_user_effective_permissions` is per partition.** It runs against `SET LOCAL search_path = {schema}, public` and only updates that schema's `user_effective_permissions` plus `public.partition_access`. There is no global rebuild — you call it once per partition.

> 🚨 **Both `partition_access` and `user_effective_permissions` are required.** A user with row-level permissions but no `partition_access` row sees nothing in the partition. A user with `partition_access` but no row-level permissions sees only `public_read = true` node types. Forgetting either produces silent denials.

> 🚨 **`access_changed` falls back to a full rebuild when `accessObject` is null.** Always populate `accessObject` in `AccessAssignment` content; missing it triggers a full `rebuild_user_effective_permissions` instead of the fast per-user variant, locking the partition's shadow table.

> 🚨 **The `namespace` column keeps the partition prefix — do NOT strip it.** Inside `{partition}.mesh_nodes`, the `namespace` column stores the FULL namespace including the partition prefix (e.g. `rbuergi/ApiToken`, not bare `ApiToken`). The generated `path` column is `namespace || '/' || id` — there is no auto-prepending of the partition. Stripping the prefix to "make namespaces relative to the partition" silently breaks: dashboard listings (`namespace:rbuergi/ApiToken nodeType:ApiToken`), `ApiTokenIndex.tokenPath` lookups, `MainNode` references, and anything else that builds full-path queries. Exception: the user-identity row and a small set of root-level Markdown nodes legitimately live at `namespace='', id=X` (full path = just `X`); those are special, not the rule.

> 🚨 **Direct SQL UPDATE on a running portal leaves stale workspace caches.** `BEGIN; UPDATE {partition}.mesh_nodes …; COMMIT;` against a running `Memex.Portal.Distributed` does NOT propagate to the in-memory workspace streams reliably — symptoms: MCP `get` returns "not found" while search hits the new path, API token validation 401s after the 5-minute `ValidationCache` expires, recompile-on-edit doesn't fire. Migrations should run via `Memex.Database.Migration` (Repair vN block) before the portal starts. If you must SQL-edit a live portal, restart `Memex.Portal.Distributed` afterwards (Aspire respawns it). For namespace/path rewrites specifically, prefer `MoveNodeRequest` over raw SQL — it goes through the proper hub and updates the workspace stream.

# Critical files to start from

- `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSchemaInitializer.cs` — DDL + stored procs + triggers (~1400 lines).
- `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPartitionedStoreFactory.cs` — partition discovery and routing.
- `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlStorageAdapter.cs` — write-side table resolution.
- `src/MeshWeaver.Mesh.Contract/PartitionDefinition.cs` — `StandardTableMappings` and `ResolveTable`.
- `memex/aspire/Memex.Database.Migration/Program.cs` — versioned migrations + idempotent schema init harness.
