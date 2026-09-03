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
| 3 | **Root-scope grants/policies** — `SecurityQueries.RootAssignments` / `.RootPolicy` | `namespace:_Access nodeType:AccessAssignment …` / `path:_Policy nodeType:PartitionAccessPolicy …` | `system_access.access` / — | **Done** 2026-09-02 (#2194) — the grants leg never fanned out (the router pins `_Access` to its registered schema); the policy leg was `namespace: id:_Policy`, path-less, and DID fan out 179×/5 min for a row that cannot exist on Postgres — now read by path, see below |
| 4 | `node_type ILIKE $1` wildcard (seen live; caller not yet named) | suffix/wildcard nodeType | `mesh_nodes` | **Identify via the shape log** (Plugins #1035), then anchor or fold into plan 2 |
| 5 | `Admin/Menu/{X}` per-render route misses | point probes | `mesh_nodes` | **Fixed** 2026-08-29 (`83b1892be`, anchored existence query) |

Each of these small sets is **tiny and rarely changing** — the fold's global reads return under
~50 rows; the bell's thousands of rows are its own defect — fetched the most expensive way the
storage layer has, per render.

### What #3093 changed underneath this census

The fold's ANCHORED legs were per-SCOPE, and that made their *count* — not their fan-out — grow with
the mesh's read volume: a node's own path is the leaf of its own scope chain, so every node ever
permission-checked minted its own live `$security-access:{path}` + `$security-policy:{path}` query.
They are now per-PARTITION (`path:{partition} scope:descendants …`), which is where `_Access` and
`_Policy` actually live. Measured: RLS-filtering a 4-node listing opened 13 security queries and a
32-node listing 69; both are 5 now. Rows 2 and 3 of the census are unchanged — the *global* legs
still fan out, and [Unanchored Security Reads](/Doc/Architecture/UnanchoredSecurityReads) says why
they must.

### The 2026-09-02 census — memex-cloud on ci.7616, after #3125 (#2194)

Maintainer directive (2026-09-02 19:40Z): *"profile it and improve"* — the portal was still slow
after rolling to ci.7616, which carries #3125's per-partition fold. Measured on the new pods: the
portal pods were **light (0.1–1.3 cores each)** while Azure Postgres `memexaks-pg`
(Standard_D8ds_v5) ran at **94–98 % CPU with 225–292 active connections**; `[CrossSchema] SLOW`
averaged **4.0 s (max 9.8 s), 2 917 lines in five minutes across 8 pods**. So the bottleneck had
moved entirely into the database, and the fan-outs were what it was doing. The shapes, by count in
that window (the shape is what `DescribeQueryShape` logs — `nodeType path scope`), each attributed
to its reader:

| Lines / 5 min | Shape | Reader | Verdict |
|---|---|---|---|
| 444 | `Notification path:- scope:Exact` | the bell — `NotificationCenter.razor:45` + `NotificationCenterPanel.razor:247` (MeshWeaver.Plugins, per CIRCUIT, live), plus `NotificationTriageService.cs:75` | **Plan 1** (recipient delivery) — a Plugins change; nothing in core issues this shape |
| 313 | `AccessAssignment path:- scope:Subtree` | **unattributed.** Not the fold: `SecurityQueryShapesTest.TheFoldNeverIssuesAMeasuredFanOutShape` pins that no fold shape describes to it. No `.cs`/`.razor` in this repo or in MeshWeaver.Plugins builds it (the two `scope:subtree nodeType:AccessAssignment` builders — `PackageInstaller.ContradictingDenies`, `Store/Licensing/Source/PluginGate.SnapshotQueries` — both carry `path:{partition}`); a `search_chunks` sweep of the live mesh answered `"searched": false` (no embedding provider on that MCP endpoint), i.e. the in-mesh sweep FAILED and is still owed | **Open** — find the caller (Plugins #1035's shape log names it per line; correlate with `select:`/`limit:` on the same line) |
| 278 | `Email path:- scope:Exact` | **unattributed** — no source in either repo spells a path-less `nodeType:Email` (`EmailInboundProcessor.cs:339` is `namespace:`-anchored); the in-mesh sweep failed as above | **Open** |
| 179 | `PartitionAccessPolicy path:- scope:Children` | the fold's ROOT policy leg — `PermissionEvaluator.ObserveScopePolicies`, spelled `namespace: id:_Policy` | **Fixed here** — see "What moved" |
| 172 | `GroupMembership path:- scope:Subtree` | `SecurityQueries.Memberships` (`PermissionEvaluator.ObserveAllMembershipNodes`) | **Cannot be anchored** — memberships live under the GROUP node in any partition ([Unanchored Security Reads](../UnanchoredSecurityReads)); the count is the multiplier below, not the subscription count |
| 103 | `User path:- scope:Exact` | `UserIdentityCache.DirectoryQuery` (`nodeType:User`, process-wide), `SpaceInviteService.cs:66` / `GroupInviteExtensions.cs:93` (`content.email:` filtered), `EventSubscriptionRunner.Reconcile`/`WatchTriggerNodeType` for `TriggerNodeType = "User"` | **Already pinned**: `UserNodeType.cs:76` registers the `nodeType:User` → `Auth` routing rule and `PostgreSqlPartitionedMeshQuery.EnumerateFanOutAsync` consumes it (the "inert hint" note on the companion page is out of date). These lines are therefore the provider's *"ALREADY PINNED … look at the statement itself"* variant — slow because the database was saturated, not because they fanned out. Inferred from the code paths; the census counted lines by shape without separating the two variants |
| 53 | `Store/Plugin path:- scope:Subtree` | `SecurityQueries.GatedNodes("Store/Plugin")` | Cannot be anchored (gate map matched against every partition) — multiplier below |
| 49 | `Thread path:- scope:Exact` | `ThreadQueries.cs:43/50`, `ChatHistorySelector.razor:236` (Plugins, `createdBy:` filtered) | Plugins; a thread lives at `{owner}/_Thread` in every partition |
| 39 | `UiContribution path:- scope:Exact` | `UiContributionCatalog.cs:95` (process-wide live) | contributions are authored wherever the plugin lives |

**The multiplier — why a "process-wide cached subscription" shows up 170 times in five minutes.**
A fan-out live query re-runs when a change notification is *relevant*, and
`PostgreSqlPartitionedMeshQuery.FanOutQuery`'s relevance filter classifies a notification by its
`Entity`: a `MeshNode` is matched against the query (`_evaluator.Matches`, node type included), but
`n.Entity is not MeshNode → return true` — *"unclassifiable — re-query rather than miss it"*. Every
notification that arrives from ANOTHER process is unclassifiable by construction:
`PostgreSqlChangeListener.cs:211` builds `new DataChangeNotification(path, kind, null, …)` because
the `pg_notify('mesh_node_changes', …)` payload carries only `path` and `op`. On an 8-pod portal
7/8 of all writes arrive that way, so **every write anywhere in the fleet re-runs every unanchored
live query on every pod** — memberships, roles, gated types, the root policy leg, the user
directory, the UI-contribution catalog, and every circuit's bell. That is the 172 / 179 / 53 / 39
above, and it is why the fold's globals were "the single biggest DB load" on code that reads them
once per process. The lever is in MeshWeaver.Plugins: put `node_type` on the notify payload and
classify the cross-process notification by it (a node type that no fan-out query names cannot
enter its result set), keeping the fail-safe re-query for a payload without one. It removes the
multiplier from every fan-out that survives, and it is independent of anchoring.

**What moved (this change, core):**

- The root **policy** leg is spelled `path:_Policy nodeType:PartitionAccessPolicy`
  (`SecurityQueries.RootPolicy`) instead of `namespace: id:_Policy …`. The two name the same node
  (namespace `""` + id `_Policy` IS the path `_Policy`), but the old spelling had no first segment,
  so the router UNION-ed every partition schema for it — 179 times in the window, every one
  necessarily empty: an unregistered `_`-prefixed first segment is unroutable, so no write can land
  a root `_Policy` row on Postgres at all. With a first segment the router never fans out; today it
  answers empty (nothing registered for `_Policy`), and registering `_Policy` as a global satellite
  the way `_Access` is would make the read pinned without touching the query.
- The root **grants** leg is `SecurityQueries.RootAssignments` and is reclassified: `_Access` is a
  REGISTERED global satellite (`DefaultPartitionProvider` → schema `system_access`), and the router
  resolves a `_`-prefixed first segment through that registry — so this leg was served by one schema
  all along. The census on both pages called it a fan-out on the strength of a comment; it never
  appeared in the Loki shape counts, which is the measurement that should have been consulted.
- `SecurityQueryShapesTest` now classifies every fold shape into the router's three real outcomes
  (`Pinned` / `Unroutable` / `FanOut`), pins the four measured `path:-` shapes as never-the-fold's,
  and mirrors the global-satellite registry; `SecurityQueryRootLegRegistryTest` asserts that mirror
  against the real registry of a running mesh.

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

🚨 **This plan is now worked out in full, with the write-side measurement it was missing:
[Addressed Notifications](../AddressedNotifications) (#3156).** What it adds — the live distribution
(of the newest 200 notifications on memex-cloud, 124 are plugin-update notices under
`Plugins/{pkg}`, 60 are startup-import failures under a space, 12 are thread completions under a
thread's *context* partition, and **six** are in a user's own partition), the fact that
`Notification` has **no** `SatelliteAccessRule` so visibility is path-based and not MainNode-derived,
the two-namespace anchor (`{viewer}` + `Admin`) the alternation resolver can narrow, the four product
rulings the change needs, and the derivable migration. It also records that the shape is no longer
merely expensive: Plugins #1231 refuses an unanchored query at runtime, and Plugins #1263
grandfathers this one as the FIRST line of the shrink-only
`src/MeshWeaver.Hosting.PostgreSql/unanchored-queries.allow` — deleting that line is the acceptance
test.

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
