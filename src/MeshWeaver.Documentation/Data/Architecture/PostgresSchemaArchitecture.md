---
Name: Postgres Schema Architecture
Category: Documentation
Description: Per-partition Postgres schemas, satellite tables, path-based routing, triggers, and the dual-gate access model. Authoritative reference for anyone querying or migrating MeshWeaver's Postgres database.
Icon: /static/DocContent/Architecture/icon.svg
---

> 🚨 **TL;DR — `public.mesh_nodes` is empty by design.**
> Every mesh node lives in a **per-partition schema** (`acme.mesh_nodes`, `user.mesh_nodes`, `dav.mesh_nodes`, …). The `public` schema holds only infrastructure tables (`partition_access`, `searchable_schemas`, `user_effective_permissions`, …). Querying `public.mesh_nodes` always returns zero rows, no matter how full the mesh is.

This page is the deep companion to [Partitioned Persistence](/Doc/Architecture/PartitionedPersistence). That doc covers the routing layer that sits in front of the database; this one covers what is actually in the database.

<svg viewBox="0 0 760 440" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="12">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity="0.6"/>
    </marker>
    <marker id="arr2" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#f57c00" fill-opacity="0.9"/>
    </marker>
  </defs>
  <rect x="270" y="8" width="220" height="40" rx="10" fill="#1e88e5"/>
  <text x="380" y="24" text-anchor="middle" fill="#fff" font-weight="bold" font-size="13">Mesh Node Write</text>
  <text x="380" y="40" text-anchor="middle" fill="#ffffffbb" font-size="11">e.g. ACME/Project/Foo/_Access/G1</text>
  <line x1="380" y1="48" x2="380" y2="78" stroke="currentColor" stroke-opacity="0.5" marker-end="url(#arr)"/>
  <rect x="250" y="80" width="260" height="40" rx="10" fill="#5c6bc0"/>
  <text x="380" y="96" text-anchor="middle" fill="#fff" font-weight="bold">Path Segment Routing</text>
  <text x="380" y="112" text-anchor="middle" fill="#ffffffbb" font-size="11">first segment → schema  |  path suffix → table</text>
  <line x1="200" y1="120" x2="130" y2="155" stroke="currentColor" stroke-opacity="0.5" marker-end="url(#arr)"/>
  <line x1="380" y1="120" x2="380" y2="155" stroke="currentColor" stroke-opacity="0.5" marker-end="url(#arr)"/>
  <line x1="560" y1="120" x2="630" y2="155" stroke="currentColor" stroke-opacity="0.5" marker-end="url(#arr)"/>
  <rect x="20" y="157" width="200" height="40" rx="10" fill="#43a047"/>
  <text x="120" y="173" text-anchor="middle" fill="#fff" font-weight="bold">acme schema</text>
  <text x="120" y="189" text-anchor="middle" fill="#ffffffbb" font-size="11">path starts with ACME/…</text>
  <rect x="270" y="157" width="220" height="40" rx="10" fill="#43a047"/>
  <text x="380" y="173" text-anchor="middle" fill="#fff" font-weight="bold">user schema</text>
  <text x="380" y="189" text-anchor="middle" fill="#ffffffbb" font-size="11">path starts with User/…</text>
  <rect x="530" y="157" width="200" height="40" rx="10" fill="#43a047"/>
  <text x="630" y="173" text-anchor="middle" fill="#fff" font-weight="bold">dav schema</text>
  <text x="630" y="189" text-anchor="middle" fill="#ffffffbb" font-size="11">path starts with DAV/…</text>
  <line x1="120" y1="197" x2="120" y2="225" stroke="currentColor" stroke-opacity="0.5" marker-end="url(#arr)"/>
  <line x1="380" y1="197" x2="380" y2="225" stroke="currentColor" stroke-opacity="0.5" marker-end="url(#arr)"/>
  <rect x="20" y="227" width="200" height="100" rx="10" fill="#263238" stroke="currentColor" stroke-opacity="0.25"/>
  <text x="120" y="246" text-anchor="middle" fill="#ffffffcc" font-weight="bold" font-size="11">Satellite Tables</text>
  <rect x="32" y="253" width="176" height="16" rx="5" fill="#1e88e5" fill-opacity="0.7"/>
  <text x="120" y="265" text-anchor="middle" fill="#fff" font-size="10">access  ← _Access/ suffix</text>
  <rect x="32" y="273" width="176" height="16" rx="5" fill="#8e24aa" fill-opacity="0.7"/>
  <text x="120" y="285" text-anchor="middle" fill="#fff" font-size="10">threads ← _Thread/_ThreadMessage</text>
  <rect x="32" y="293" width="176" height="16" rx="5" fill="#f57c00" fill-opacity="0.7"/>
  <text x="120" y="305" text-anchor="middle" fill="#fff" font-size="10">annotations ← _Comment/_Approval</text>
  <text x="120" y="322" text-anchor="middle" fill="#ffffffaa" font-size="10">+ activities / code / mesh_nodes</text>
  <rect x="270" y="227" width="220" height="100" rx="10" fill="#263238" stroke="currentColor" stroke-opacity="0.25"/>
  <text x="380" y="246" text-anchor="middle" fill="#ffffffcc" font-weight="bold" font-size="11">Satellite Tables</text>
  <rect x="282" y="253" width="196" height="16" rx="5" fill="#1e88e5" fill-opacity="0.7"/>
  <text x="380" y="265" text-anchor="middle" fill="#fff" font-size="10">access  ← _Access/ suffix</text>
  <rect x="282" y="273" width="196" height="16" rx="5" fill="#8e24aa" fill-opacity="0.7"/>
  <text x="380" y="285" text-anchor="middle" fill="#fff" font-size="10">threads ← _Thread/_ThreadMessage</text>
  <rect x="282" y="293" width="196" height="16" rx="5" fill="#f57c00" fill-opacity="0.7"/>
  <text x="380" y="305" text-anchor="middle" fill="#fff" font-size="10">annotations ← _Comment/_Approval</text>
  <text x="380" y="322" text-anchor="middle" fill="#ffffffaa" font-size="10">+ activities / code / mesh_nodes</text>
  <line x1="120" y1="327" x2="120" y2="358" stroke="#f57c00" stroke-opacity="0.8" marker-end="url(#arr2)"/>
  <line x1="380" y1="327" x2="380" y2="358" stroke="#f57c00" stroke-opacity="0.8" marker-end="url(#arr2)"/>
  <rect x="140" y="360" width="480" height="66" rx="10" fill="#37474f" stroke="currentColor" stroke-opacity="0.3"/>
  <text x="380" y="378" text-anchor="middle" fill="#ffffffdd" font-weight="bold" font-size="12">public schema — Two-Gate Access Check</text>
  <rect x="152" y="385" width="210" height="32" rx="8" fill="#e53935" fill-opacity="0.8"/>
  <text x="257" y="399" text-anchor="middle" fill="#fff" font-size="11" font-weight="bold">Gate 1: partition_access</text>
  <text x="257" y="412" text-anchor="middle" fill="#ffffffcc" font-size="10">user has ANY access to partition?</text>
  <rect x="378" y="385" width="230" height="32" rx="8" fill="#e53935" fill-opacity="0.8"/>
  <text x="493" y="399" text-anchor="middle" fill="#fff" font-size="11" font-weight="bold">Gate 2: user_effective_permissions</text>
  <text x="493" y="412" text-anchor="middle" fill="#ffffffcc" font-size="10">longest-prefix match on node path</text>
  <line x1="362" y1="401" x2="378" y2="401" stroke="#fff" stroke-opacity="0.5" stroke-dasharray="3,3"/>
  <text x="370" y="398" text-anchor="middle" fill="#ffffffaa" font-size="10">+</text>
</svg>

*Per-partition schema layout: path segment selects the Postgres schema, path suffix routes to the satellite table, and reads require passing both public-schema gates.*

---

## Per-partition schema model

The first path segment of any mesh node (lowercased and SQL-sanitised) becomes the **Postgres schema name**:

| Path | Schema |
|---|---|
| `ACME/Project/Foo` | `acme` |
| `User/rbuergi/Notes` | `user` |
| `DAV/Underwriting/AlpenLloyd2026` | `dav` |
| `123-org/Foo` | `_123_org` |
| `org.with.dots/Foo` | `org_with_dots` |

The sanitiser is `PostgreSqlPartitionedStoreFactory.SanitizeSchemaName` — it lowercases, replaces non-alphanumeric characters with `_`, and prefixes leading digits with `_`.

The following schema names are excluded from partition discovery because they are infrastructure or satellite-only:

```
admin, portal, kernel,
_access, _address_, _graph, _settings, _tracking, _thread, _source, _test,
login, markdown, onboarding, welcome, settings, storage,
mesh, thread, agent, partition, organization, vuser,
public, information_schema, pg_catalog, pg_toast,
*_versions
```

The canonical discovery query — used by the migration script and every "which partitions exist?" sweep (there is no `DiscoverPartitionsAsync` API; the router does not enumerate schemas):

```sql
SELECT schema_name FROM information_schema.schemata s
WHERE EXISTS (
    SELECT 1 FROM information_schema.tables t
    WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes')
  AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
  AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\';
```

Implementation: `MeshNodeEmbeddingBackfill` / `SchemaInitialization` in `memex/aspire/Memex.Database.Migration/Migrations/` use exactly this shape.

---

## Per-schema table layout

Every partition schema contains a consistent set of tables. The primary table holds general-purpose entities; the satellite tables exist to separate high-volume or functionally distinct data into dedicated stores with purpose-built triggers.

| Table | Purpose | Routes for |
|---|---|---|
| `mesh_nodes` | Primary entities | All "main" node types |
| `activities` | Satellite | `Activity` |
| `user_activities` | Satellite | `UserActivity` (high-volume time-series) |
| `threads` | Satellite | `Thread`, `ThreadMessage` |
| `access` | Satellite | `AccessAssignment` |
| `code` | Satellite | `Code` (under `Source/` and `Test/` namespaces) |
| `annotations` | Satellite | `Comment`, `Approval`, `TrackedChange` (legacy — no longer written) |
| `partition_objects` | Internal | Non-mesh partition data |
| `change_logs` | Bundled activity log | (internal) |
| `user_activity` | Per-user access patterns | (internal) |

Partitions with `Versioned = true` also get a sibling `{schema}_versions` schema:

| Table | Purpose |
|---|---|
| `mesh_node_history` | Append-only history of every `mesh_nodes` write |

The mesh DDL plus all triggers and stored procedures are emitted by `PostgreSqlSchemaInitializer` (`src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSchemaInitializer.cs`).

---

## NodeType → table routing

Writes do **not** pick their destination table from the C# `NodeType` string alone — they pick based on **the path itself**, by longest-segment match. The defaults live in `SatelliteTableMapping.Defaults` (`src/MeshWeaver.Mesh.Contract/SatelliteTableMapping.cs`) — a `static readonly` immutable **list**, i.e. a constant lookup, not a mutable static dictionary. (The old static `PartitionDefinition.StandardTableMappings` / `NodeTypeToSuffix` dictionaries are deleted.)

| Segment | Table | NodeTypes that resolve to it |
|---|---|---|
| `_Activity` | `activities` | `Activity` |
| `_UserActivity` | `user_activities` | `UserActivity` |
| `_Thread` | `threads` | `Thread`, `ThreadComposer` |
| `_ThreadMessage` | `threads` | `ThreadMessage` |
| `_Access` | `access` | `AccessAssignment` |
| `_Tracking` | `annotations` | `TrackedChange` *(legacy, read-only)* |
| `_Approval` | `annotations` | `Approval` |
| `_Comment` | `annotations` | `Comment` |
| `_Notification` | `notifications` | `Notification` |
| `Source` | `code` | *(none — path-matched only)* |
| `Test` | `code` | *(none — path-matched only)* |

The set is **configurable**, not hardcoded: per host via `PostgreSqlStorageOptions.SatelliteTables`, and per namespace via `PartitionDefinition.TableMappings` / `NodeTypeTableMappings` (populated from `PartitionDefinition.DefaultSegmentTableMappings()` / `DefaultNodeTypeTableMappings()`).

`PartitionDefinition.ResolveTable(path)` scans the path for the longest matching segment. The fallback chain is:

1. If a path-segment match is found → use the mapped table.
2. If no match but a `nodeType` is provided → `ResolveTableByNodeType(nodeType)`.
3. Otherwise → `mesh_nodes`.

Implementation: `PostgreSqlStorageAdapter.ResolveTable` (`src/MeshWeaver.Hosting.PostgreSql/PostgreSqlStorageAdapter.cs`).

> ⚠ **Footgun — wrong segment, wrong table.**
> If you write an `AccessAssignment` whose namespace does **not** end in `_Access` (e.g. you write `Admin/Groups/G1` instead of `Admin/Groups/_Access/G1`), the row lands in `mesh_nodes` instead of `access`. The `access_changed` trigger will never fire, and `rebuild_user_effective_permissions` will not see the assignment. This was the bug behind Repair v1 (`memex/aspire/Memex.Database.Migration/Program.cs:133`).

### The `_` prefix means *hidden*, not *satellite*

A leading-underscore path segment is a **hidden ("dotfile") namespace** — like a Unix dot-folder. It is **decoupled from satellite-table routing**: ONLY the registered suffixes above route to a satellite table. A *new* `_`-prefixed segment that isn't in the mapping (e.g. `_Memex`) falls through to `mesh_nodes` for **both** the write and the path-based read — no satellite mismatch, no extra table needed.

What the `_` prefix *does* buy you, everywhere, is **visibility hiding**: any node whose path contains a `_`-prefixed segment is excluded from the `search` context (`MeshNodeVisibility.IsHiddenPath` / `IsExcludedFromContext`, consulted by every query backend — Postgres, Cosmos, storage-adapter, static). This is the same `search`-context exclusion that `MeshNode.ExcludeFromContext` provides per-type, but applied by path convention so framework/default state never has to opt out individually.

#### `_Memex` — per-user / global Memex defaults

`_Memex` is the namespace for **Memex defaults and global Memex data** — framework-owned state that isn't user content. Per-user defaults live at `{user}/_Memex/…`; the canonical example is the side-panel chat composer's singleton **`{user}/_Memex/ChatInput`** (draft text + selected harness/agent/model). Because `_Memex` is a dotfile namespace that is *not* a registered satellite suffix:

- write + path-read both hit `mesh_nodes` (the selection actually persists — contrast the dead `_ThreadTemplate`/`nodeType:Thread` approach, which split write→`threads` from read→`mesh_nodes` and silently lost the selection);
- the nodes are auto-hidden from search;
- **never reuse `_ThreadTemplate`** — it matched the `_Thread`→`threads` satellite prefix by `nodeType` and is the cautionary tale.

---

## `public` schema — infrastructure only

The `public` schema plays a single, well-defined role: it holds the cross-partition infrastructure that the storage adapter and permission system need at query time. No mesh nodes ever live here.

| Table | Purpose |
|---|---|
| `partition_access` | Binary "user X has any access to partition P" gate. PK `(user_id, partition)`. Populated by per-schema `rebuild_user_effective_permissions`. |
| `searchable_schemas` | Schemas that cross-schema search (`search_across_schemas`) iterates over. Repopulated on every migration run. |
| `node_type_permissions` | 🪦 **Legacy, always empty, read by nothing** (issue #953). Kept for one release only so a rolling deploy's older replicas don't fault on the table name; a follow-up migration drops it. See "Why there is no node-type public read" below. |
| `user_effective_permissions` and `_shadow` | Denormalised cache of every `(user, path-prefix, permission)` tuple. The shadow is rebuilt then atomically swapped (`PostgreSqlSchemaInitializer.cs:542`). |
| `change_logs` | Partition-level change feed. |

---

## Triggers and the permission-rebuild chain

Two independent trigger chains keep permissions and audit history consistent.

**Permission chain** — fires on every change to `{schema}.access`:

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

**History and notification chain** — fires on every change to `{schema}.mesh_nodes`:

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

---

## Two-gate access model

Reading from a partition schema requires passing **both** gates in sequence. A row that passes one but not the other is invisible to the caller.

**Gate 1 — partition gate**
```sql
EXISTS (SELECT 1 FROM public.partition_access WHERE user_id = $me AND partition = 'acme')
```
No row here means the user cannot read **anything** in the partition, regardless of any row-level grants.

**Gate 2 — node gate**
A matching row in `{schema}.user_effective_permissions` with the longest-prefix match against the node's path, folded per subject and OR'd across subjects. There is **no bypass** of this gate.

Cross-schema search (`public.search_across_schemas`) iterates `searchable_schemas`, applies both gates per schema, and returns only rows where both pass. See `PostgreSqlSchemaInitializer.cs:34`.

### Why there is no node-type public read

Both gates used to carry a third term: `EXISTS (SELECT 1 FROM {schema}.node_type_permissions WHERE node_type = n.node_type AND public_read)`, OR'd in front of gate 2. **It was removed in issue #953 rather than wired up**, and it must not come back in that shape:

- **It never did anything.** The table's only writer, `SyncNodeTypePermissionsAsync`, hung off `InitializePostgreSqlSchemaAsync`, which had **zero callers** — the migration container calls `PostgreSqlSchemaInitializer.InitializeAsync` directly. Every deployment's copy of the table was empty, so the term was a constant `false`. Removing it is a provable no-op.
- **Wiring it up would have been a breach, not a fix.** ~24 node types declared public read, among them `Thread` and `ThreadMessage` (every user's private conversations), `Markdown`, `Code` and `Document` (the bulk of all content), and `Course`, `Module`, `Exercise`, `ExerciseAttempt` (paid course content and learners' own submissions).
- **The shape was wrong even for a safe type list.** The term was an unconditional `OR` in front of gate 2, so it short-circuited the longest-prefix fold — which is exactly where the store/course paywall's DENY rows live. A grant that cannot be denied is not a grant, it is a hole.
- **It had no counterpart in the evaluator.** `PermissionEvaluator` has no node-type-keyed term, so SQL listing and exact reads would have diverged — the failure mode the evaluator's own comments record from memex-cloud 2026-07-19.

**To make content publicly readable, use a mechanism both read paths honour:**

| Need | Mechanism |
|---|---|
| A whole partition/subtree world-readable | `PartitionAccessPolicy` `_Policy` node with `PublicRead = true` (issue #603). Projected into `user_effective_permissions` as allow-`Read` rows for `Public`/`Anonymous`, so it **participates in** the longest-prefix fold — a deeper deny still wins. |
| A type that opens a short list of surfaces on its own subtree (storefront cover, course landing page) | `NodeTypeGate` via `ConfigureNodeTypeAccess(a => a.WithGate(...))` (issue #701). |

### Which schemas are searchable (and the catalog-partition rule)

`searchable_schemas` is (re)discovered by `PostgreSqlCrossSchemaQueryProvider.SyncSearchableSchemasAsync`: every schema that has a `mesh_nodes` table, **minus** the `ExcludedSchemas` denylist (the `auth` access-object mirror — to avoid double-surfacing; `admin`/`portal`/`kernel`; `_`-prefixed satellite/global schemas; and a set of legacy reserved route words).

🚨 **A public catalog partition MUST NOT be in `ExcludedSchemas`.** The platform AI catalogs — `agent`, `skill`, `model`, `_provider`, `harness`, `command` — are real `publicRead` partitions whose nodes are listed by the **per-partition registry fan-out**: a single multi-namespace query of the form `namespace:{user}/Agent|{space}/Agent|Agent nodeType:Agent` (see `AgentPickerProjection`). That query is **unscoped** (a `namespace IN (...)` membership filter, no concrete first path segment), so it routes through the **cross-schema fan-out**, which only visits schemas in `searchable_schemas`. If a catalog schema is excluded, the fan-out silently skips it and the registry comes back **empty** (the chat agent/model/skill picker shows nothing). A **single**-namespace query (`namespace:Agent`) masks the bug: it is *scoped* — it resolves the one schema directly via the registered-partition cache, bypassing `searchable_schemas`. The agent picker was empty on prod (2026-06-20) for exactly this reason: `"agent"` was a stale entry in `ExcludedSchemas` from before the per-partition agent-registry migration, so `skill`/`model` worked but `agent` did not.

---

## Versioning schemas

Partitions with `Versioned = true` (the default for content partitions) get a sibling `{schema}_versions` schema containing only `mesh_node_history`. The primary key is `(namespace, id, version)`; a `changed_by` column records authorship. The cross-schema `mesh_node_copy_to_history` trigger writes a new row on every primary-table change. Direct INSERTs into `mesh_node_history` during a migration bypass the trigger and preserve audit fidelity.

---

## Repair migrations

`memex/aspire/Memex.Database.Migration/Program.cs` runs idempotent **schema initialisation** on every start (`PostgreSqlSchemaInitializer.InitializeAsync`) and **versioned data repairs** that execute once per database. The DB version is stored in `admin.mesh_nodes` at `(namespace='', id='db_version')`.

| Version | Fix |
|---|---|
| v1 | Move misrouted `AccessAssignment` rows from `mesh_nodes` to `access`; add `/_Access` to namespace |
| v2 | Re-run schema init per partition + populate `partition_access` |
| v3 | Drop rogue schemas accidentally created from path segments |
| v4 | Upgrade user self-assignments from `Viewer` to `Admin` |
| v5 | Ensure every `User` node has an Admin self-assignment + rebuild permissions |
| v6 | Fix `search_across_schemas` to enforce `partition_access` |
| v7 | Deploy per-user permission-rebuild trigger function |
| v8 | Fix `ThreadMessage.MainNode` to point at the thread's content node, not the thread path |
| v9 | Rename `_Source/_Test` namespace segments to `Source/Test` |
| v10 … | see below |

The table above is the **early history only**. Migrations now live as one file per version in `memex/aspire/Memex.Database.Migration/Migrations/` (`V01_…` … `V51_…` at the time of writing) — **read that directory, not this table**, for the current head version and for what each step does. Notable later ones: `V10_PerUserPartitions`, `V27_RenameUserSchemaToAuthAndMirrorApiTokens`, `V28_RenameOrganizationToSpace`, `V38_DropLegacyProviderSchema`, `V45_AddNodeAuthorshipColumns`, `V50_RescopePlatformAdminGrants`, `V51_DropInvalidPartitionSchemas`.

🚨 **Fresh databases fast-forward.** `MigrationRunner` skips the legacy `user`-schema repair chain (V05/V10/V14/V15/V17/V18/V20/V22/V25/V27/V31 — all reference the long-gone `user` schema) when `SchemaInitialization.DetectFreshDbAsync` reports no CONTENT partition schemas. Framework schemas (`admin`/`auth`/`system_*`) are excluded from that count so they can never make a fresh DB look non-fresh.

---

## 🚨 Footguns — read once, never trip again

> 🚨 **`public.mesh_nodes` is empty.** Every "I queried Postgres and the row isn't there" report has come from looking in `public.*` instead of the partition schema. Run the discovery query above first.

> 🚨 **Satellite tables are routed by path segment, not nodeType.** If you bulk-insert via SQL or write directly to `mesh_nodes` bypassing the storage adapter, verify the path contains the satellite suffix. A missing suffix lands the row in `mesh_nodes` and silently prevents the corresponding triggers — especially `access_changed` — from firing.

> 🚨 **`rebuild_user_effective_permissions` is per partition.** It runs against `SET LOCAL search_path = {schema}, public` and updates only that schema's `user_effective_permissions` plus `public.partition_access`. There is no global rebuild — call it once per partition.

> 🚨 **Both `partition_access` and `user_effective_permissions` are required.** A user with row-level permissions but no `partition_access` row sees nothing in the partition. A user with `partition_access` but no row-level permissions sees **nothing** — the old `public_read` node-type escape hatch was deleted (issue #953); there is no node-type public read. Public read is declared with a `PartitionAccessPolicy` `_Policy` node (`PublicRead = true`) or a `NodeTypeGate`, both of which materialise rows that *participate in* the prefix fold. Forgetting either table produces silent denials.

> 🚨 **`access_changed` falls back to a full rebuild when `accessObject` is null.** Always populate `accessObject` in `AccessAssignment` content. A missing value triggers `rebuild_user_effective_permissions` over the entire partition instead of the fast per-user variant, locking the shadow table.

> 🚨 **The `namespace` column keeps the partition prefix — do NOT strip it.** Inside `{partition}.mesh_nodes`, `namespace` stores the full namespace including the partition prefix (e.g. `rbuergi/ApiToken`, not bare `ApiToken`). The generated `path` column is `namespace || '/' || id` — the partition is not auto-prepended. Stripping the prefix to "make namespaces relative" silently breaks dashboard listings (`namespace:rbuergi/ApiToken nodeType:ApiToken`), `ApiTokenIndex.tokenPath` lookups, `MainNode` references, and anything else that builds full-path queries. Exception: the user-identity row and a small set of root-level Markdown nodes legitimately live at `namespace='', id=X` (full path = just `X`) — those are special, not the rule.

> 🚨 **Direct SQL UPDATE on a running portal leaves stale workspace caches.** `BEGIN; UPDATE {partition}.mesh_nodes …; COMMIT;` against a running `Memex.Portal.Distributed` does NOT propagate to in-memory workspace streams reliably — symptoms: MCP `get` returns "not found" while search hits the new path, API token 401s after the 5-minute `ValidationCache` expires, recompile-on-edit doesn't fire. Migrations should run via `Memex.Database.Migration` (Repair vN block) before the portal starts. If you must SQL-edit a live portal, restart `Memex.Portal.Distributed` afterwards (Aspire respawns it automatically). For namespace/path rewrites, prefer `MoveNodeRequest` over raw SQL — it goes through the hub and updates the workspace stream correctly.

---

## Key source files

| File | Contents |
|---|---|
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSchemaInitializer.cs` | DDL, stored procedures, triggers (~2 500 lines) |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPathRoutingAdapter.cs` | First-segment → schema/table routing (no probe, no cache) |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPartitionStorageProvider.cs` | `EnsurePartitionProvisioned` — the ONE schema-creation entry point |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlStorageAdapter.cs` | Write-side table resolution (`ResolveTable`) |
| `src/MeshWeaver.Mesh.Contract/SatelliteTableMapping.cs` | The configurable satellite defaults |
| `src/MeshWeaver.Mesh.Contract/PartitionDefinition.cs` | `TableMappings` / `NodeTypeTableMappings` and `ResolveTable` |
| `memex/aspire/Memex.Database.Migration/Migrations/` | One file per versioned migration (`V01_…` … `V51_…`) |
| `memex/aspire/Memex.Database.Migration/Program.cs` | Migration harness + idempotent schema init + embedding backfills |
