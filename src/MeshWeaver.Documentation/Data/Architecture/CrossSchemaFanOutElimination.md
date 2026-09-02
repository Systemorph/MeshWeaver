---
Name: Cross-Schema Fan-Out Elimination
Category: Architecture
Description: Why an unanchored mesh query is a lock bomb, the measured 2026-08-31 evidence (LWLock/LockManager contention seizing both production portals), the census of unanchored callers, and the per-caller elimination plan — no query on a render path may UNION every partition schema.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v4"/><path d="M12 18v4"/><path d="M4.93 4.93l2.83 2.83"/><path d="M16.24 16.24l2.83 2.83"/><path d="M2 12h4"/><path d="M18 12h4"/><circle cx="12" cy="12" r="3"/></svg>
---

# Cross-Schema Fan-Out Elimination

**Maintainer directive (Roland, 2026-08-31): "we should not be doing any cross schema stuff …
we need to stop these fan-outs — find them and eliminate."** This page is the census, the measured
mechanism, and the elimination plan. It exists because the evidence was expensive to assemble —
live `pg_stat_activity` sampling from inside a production portal, Loki correlation across two
meshes, and a call-site hunt across two repos — and must not be re-derived.

## What a cross-schema fan-out is

Every mesh partition (top-level space: `Store`, `Doc`, `rbuergi`, …) is its own PostgreSQL
**schema** with its own `mesh_nodes` and satellite tables (`access`, `notifications`, `threads`,
`activities`, …). A query whose `path:`/`namespace:` first segment names a partition is **pinned**
to that one schema. A query with **no** concrete first segment cannot know where its answer lives,
so `PostgreSqlCrossSchemaQueryProvider` generates **one `UNION ALL` over every row of
`public.searchable_schemas`** — 188 schemas on memex-cloud as of 2026-08-31 — and pays for all of
them regardless of where the rows are.

Routing detail that matters for the census: `PostgreSqlPartitionedMeshQuery.NeedsFanOut` routes
**every satellite-table query** through the fan-out provider even when anchored (the pedestrian
provider cannot see satellite tables); an anchored query then runs against a **one-element schema
list** (the pinned fast path). So an anchored satellite query is fine — the population to eliminate
is exactly the queries with no concrete first segment.

## Why it is a lock bomb, not just a slow query (measured 2026-08-31)

Sampling `pg_stat_activity` from inside the systemorph portal during ordinary evening load, with
the 08-31 rebake (issue #2895) writing concurrently:

- **Every slow SELECT was waiting on `LWLock/LockManager`** — not I/O, not rows.
- The rebake's `INSERT INTO "…"."mesh_nodes" … ON CONFLICT` sat in `Lock/relation`, **blocked by
  the fan-out SELECTs' shared relation locks** (blocking pids listed four SELECT backends).
- **Pinned point-reads (`WHERE namespace=$1 AND LOWER(id)=$2 …`) waited 2–5 s** on
  `LWLock/LockManager` — innocent victims.

The mechanism: one 188-schema `UNION ALL` takes heavyweight locks on ~500+ relations (188 tables
plus their indexes). A backend has **16 fast-path lock slots**; everything beyond goes through the
shared lock manager's partition LWLocks. A handful of concurrent fan-outs means thousands of
colliding lock acquisitions — the LockManager serializes, **and every other query on the database
queues behind it**, pinned or not. This is why "the portal is completely unresponsive" (memex,
2026-08-31 ~18:30Z: 399 SubscribeRequest 60 s timeouts in 40 min, `Store/Plugin` hub starved,
instance-key resolution failing, `/api/plugins` 503) and "every page has a ~2 s floor" (#2640) are
**one defect**. Eliminating the fan-outs removes both the direct cost and the collateral.

Loki, memex-cloud, 40 min, capped sample: **2 583 × `[CrossSchema] SLOW`** — `mesh_nodes` ~1.7 s,
`access` ~1.9 s, `notifications` ~2.0 s (2 286–4 203 rows), `threads` — each "188 of 188 partition
schema(s)".

## The census — who issues unanchored queries on a render path

| # | Caller | Query shape | Table | Status |
|---|---|---|---|---|
| 1 | **Notification bell + panel** — `NotificationCenter.razor` / `NotificationCenterPanel.razor` (MeshWeaver.Plugins, `MeshWeaver.Blazor.Portal`) | `nodeType:Notification sort:CreatedAt-desc` — unanchored, unbounded, LIVE (re-queries on matching change) | `notifications` | **To eliminate** — see plan 1 |
| 2 | **Security fold globals** — `SecurityQueries.Roles` / `.Memberships` / `.GatedNodes(type)` (per gated type!) via `PermissionEvaluator` | `nodeType:Role scope:subtree … complete`, `nodeType:GroupMembership …`, `nodeType:{gated} …` | `mesh_nodes` | **To eliminate** — see plan 2 |
| 3 | **Root-scope grants/policies** — `SecurityQueries.Scoped` on `namespace:_Access` / root `_Policy` (the root scope resolves to no partition, falls through to fan-out) | `namespace:_Access nodeType:AccessAssignment …` | `access` | **To eliminate** — see plan 2 |
| 4 | `node_type ILIKE $1` wildcard (seen live; caller not yet named) | suffix/wildcard nodeType | `mesh_nodes` | **Identify via the shape log** (Plugins #1035), then anchor or fold into plan 2 |
| 5 | `Admin/Menu/{X}` per-render route misses | point probes | `mesh_nodes` | **Fixed** 2026-08-29 (`83b1892be`, anchored existence query) |

Each of these small sets is **tiny and rarely changing** — the fold's global reads return under
~50 rows; the bell's thousands of rows are its own defect — fetched the most expensive way the
storage layer has, per render.

## The elimination plan

### 1. The bell: deliver notifications to the RECIPIENT's partition

Today `NotificationService.CreateNotification` writes `{mainNodePath}/_Notification/{id}` — under
the **entity that notified**, scattered mesh-wide — and the bell reads *every notification the
viewer can see* with an unanchored, unbounded, live query. There is no recipient-side store
(`NotificationRule`/`NotificationChannel` under the user feed the triage agent, not routing).

The fix is the data model: a notification is **addressed** — deliver a copy (or the record itself)
to `{recipient}/_Notification/{id}` at creation, and the bell becomes
`namespace:{viewer}/_Notification nodeType:Notification` — pinned, one schema, small, and the live
subscription's change feed narrows with it. Open questions the implementation must settle: who the
recipients of an entity-scoped notification are (watchers? grant-holders?), migration of existing
rows, and the panel's grouping (it already groups by source path, which survives).

### 2. The security fold: one materialized global set, invalidated by the change feed

`SecurityQueries`' own doc explains why its globals are path-less (a `GroupMembership` lives under
the group, the grant that names the group elsewhere) and why they must never be truncated (a paged
membership read makes a group **deny fail open** — #2011). So neither anchoring nor paging is
available; **caching/materializing is the only lever**, and it must be invalidation-correct or a
revoked viewer keeps their old permissions.

The precedent already in the schema: **`public.partition_access`** — a public, write-maintained
table the per-schema access clause reads on every query. The same shape serves the fold: a
`public` materialization of the security-relevant rows (Roles, GroupMemberships, root-scope
`AccessAssignment`s/`_Policy`s, gated-type identity rows), maintained on the owning hubs' writes,
read by the fold as ONE single-table query. Invalidation is the write path itself (the table IS
the store), so there is no staleness window to reason about — the delicate part is backfill and
the write-path coverage test: every code path that writes one of those node types must also land
in the materialization, pinned by a test that enumerates the types (`SecurityQueries.AllShapes`
already exists as the census of shapes to cover).

### 3. The guardrail so the population never grows back

Once 1–3 land: the fan-out log line (which since Plugins #1035 names the query shape) feeds a
**ratchet** — a periodic check (or a `pg_stat_statements`-based gate once the extension is enabled)
that fails loudly when a NEW unanchored shape appears on a render path. The provider's own
`FanOutQuery` live re-query on change notifications multiplies every surviving fan-out by the
mesh's write rate — which is how the 08-31 rebake turned a floor into an outage — so the ratchet is
not optional hygiene.

## Order of work

1. ~~Plugins #1035 — the shape on the log line~~ (landed; identifies population #4 and any stragglers).
2. Plan 2 (fold materialization) — core; removes the per-render `access` + `mesh_nodes` globals.
3. Plan 1 (recipient delivery) — core `NotificationService` + Plugins bell/panel; removes the
   `notifications` fan-out and its thousands-of-rows reads.
4. The ratchet (plan 3).

🚨 **Read [Unanchored Security Reads](../UnanchoredSecurityReads) before touching plan 2.** It is
the companion to this page and it says which of these fan-outs must NOT be eliminated the obvious
way: anchoring the fold's global reads to the viewer's partition is truncation, which makes a
group-derived permission vanish AND a group-scoped deny fail open, with nothing logged and nothing
failing. It also carries the per-lever verdicts (what is tractable, what needs a decision) and the
executable census `SecurityQueryShapesTest` pins.

## Declared instance locations (#3039) — enabling infrastructure, NOT the fix for the seven

Plugins#1127 added a **fourth fan-out narrowing** to `PostgreSqlPartitionedMeshQuery`: a NodeType
DECLARES where its instances live, and an unanchored `nodeType:X` query intersects the declared
partitions with the schemas it was going to UNION. Core #3039 supplies what that planner was
missing — the declaration and its projection:

- **`NodeTypeDefinition.InstanceLocations`** (`MeshWeaver.Graph.Contract`): `namespace:`/`path:`
  query strings, authored on the type, shipped with the package that owns it, round-tripping through
  JSON and a package install like every other field.
- **`INodeTypeInstanceLocations`** (`MeshWeaver.Graph.Contract`) and its in-box implementation
  `NodeTypeInstanceLocations` (`MeshWeaver.Graph`, registered by `AddGraph()`): the static fold over
  the builder-registered definitions plus a dynamic lane fed by each definition node's OWN hub while
  it is live on the process — an entry follows every edit and is forgotten with the hub, so the
  projection can never serve a stale (under-stated) declaration. Undeclared, unknown, or
  another-silo definitions answer `null`: **fail-open, the query fans out in full — slow, never
  partial.**
- **The authoring gate.** `NeverNarrowedNodeTypes` (`MeshWeaver.Mesh.Contract`, hoisted from
  Plugins so there is ONE list) names the fold's types — `Role`, `GroupMembership`,
  `AccessAssignment`, `PartitionAccessPolicy`, plus every `MeshConfiguration.NodeTypeGates` type.
  `InstanceLocationDeclarationValidator` refuses a declaration on any of them at Create/Update, and
  the static fold throws for an in-process one, both naming the reason: in the fold a short read is a
  vanished grant (#2011) or a deny that fails OPEN ([Unanchored Security Reads](../UnanchoredSecurityReads)).
  The planner refuses the same set again at query time, so a declaration that slipped past both
  would still be inert — the gate exists so it is a red import instead.

🚨 **This does not remove any of the fan-outs in the census above, and cannot.** The seven shapes
measured on 2026-09-01 (Plugins `Hosting/NodeTypeInstanceLocations`) live in per-partition
satellite containers — `{any}/_Access`, `{owner}/_Thread`, `{mainNode}/_Notification`, per-user
`_Email` — or ARE the fold (refused outright). No honest declaration narrows them: `namespace:*/_X`
correctly resolves to "cannot narrow". What removes them is still this page's own plan, in its order:
the fold materialization (plan 2), recipient-side notification delivery (plan 1), then anchoring
`Thread`/`UiContribution`/`Email` at their call sites. The declaration serves the types that DO have
a home — an `Admin/Menu` entry, a package's own dimension types — and any future type whose author
can say where it lives.

**Plugins follow-up** (the planner already resolves the interface with `GetService`, so nothing is
wired until this lands): `src/MeshWeaver.Hosting.PostgreSql/NodeTypeInstanceLocations.cs` drops its
own `INodeTypeInstanceLocations` and `NeverNarrowedNodeTypes` in favour of core's
(`using MeshWeaver.Graph.Configuration;` / `using MeshWeaver.Mesh.Security;`), keeping only
`DeclaredNodeTypeInstanceLocations`, the test fixture.

Related: issue #2640 (the per-page floor this eliminates), #2876 (a transient connect timeout took
a whole area render down — the same fan-out, from the render side), #2895 (the rebake write storm
whose mutual blocking with the fan-outs produced the 08-31 outages), #2011/#2048 (why the fold must
never be paged), Plugins #1035 (the shape log).
