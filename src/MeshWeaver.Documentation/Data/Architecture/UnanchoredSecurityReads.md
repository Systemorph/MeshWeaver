---
Name: Unanchored Security Reads
Category: Architecture
Description: Why the permission fold's mesh-wide reads cannot be anchored to the viewer's partition — anchoring them is a silent security regression, not an optimisation — plus the measured core census of unanchored render-path queries and the per-caller verdicts for #2640.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M9 12h6"/></svg>
---

# Unanchored Security Reads

**The companion to [Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination).**
That page says *eliminate the fan-outs*. This one says which ones you must not eliminate the
obvious way, and why — because the obvious way is invisible when it is wrong.

Issue #2640 measures a ~2 s floor on every page render, caused by unanchored mesh queries that
`UNION ALL` every partition schema. Its body proposes, first candidate:

> anchor the permission/notification/thread queries to the viewer's partition (`{user}/…`) + the
> group-grant partitions

The `access` bucket is the largest of the four (465 of 1 030 measured fan-outs, 45%), so it is the
natural place to start. **It is the one that must not be done that way.** Three separate sessions
reached that conclusion independently; this page exists so a fourth does not have to.

## The rule

🚨 **In the security fold, "no result" and "not allowed" are the same value — so a read that comes
back SHORT is indistinguishable from a read that came back EMPTY, and both read as "denied".**

That is why `SecurityQueries` exists at all, and why several of its reads carry no `path:` and no
`namespace:`. From the class itself:

> Several of these reads are GLOBAL by necessity — a `GroupMembership` lives under the group node,
> which may sit in a different partition than the grant that names the group, so the query carries
> no `path:` and no `namespace:`.

**Anchoring the membership read to the viewer's partition IS truncation.** A membership record
living in the *group's* partition is not in the viewer's, so it is not returned. Two failures
follow, and they point in opposite directions:

| Direction | What happens | What you see |
|---|---|---|
| Grant | A group-derived permission simply vanishes; every surface gated on it disappears at once | Nothing. No log, no exception, no red test |
| **Deny** | A group-scoped `AccessAssignment` with `Denied = true` is applied only to the viewers the membership read *says* are in the group — so a revocation **FAILS OPEN** | Nothing. The viewer keeps reading content the deny was written to take away |

Issue #2011 is the first half. The second half is worse and is the reason paging is off the table
too: `SecurityQueries.Enumeration` **overwrites** any `limit:` in a fold query rather than honouring
it, deliberately, because in this fold a page IS the bug.

**The trigger is GROWTH, not a change.** It fires the moment a mesh's `Role` or `GroupMembership`
set outgrows whatever bound was introduced — so it appears on the largest install first, the one
where it is most expensive, and nobody will have touched anything.

## The census, and why each entry is global

Pinned executably by `SecurityQueryShapesTest` (`test/MeshWeaver.Hosting.Test`), which parses every
shape with the real `QueryParser` and fails when a NEW unanchored security read appears without a
declared reason. The test also carries a positive control — the anchored per-scope legs, which are
the overwhelming majority at runtime — so "everything is unanchored" cannot pass vacuously.

| Shape | Why it cannot be anchored |
|---|---|
| `SecurityQueries.Roles` | a custom `Role` definition may live in any partition; a truncated role set silently drops every permission derived from the missing role |
| `SecurityQueries.Memberships` | the group and the grant that names it live in different partitions (above) |
| `SecurityQueries.GatedNodes(type)` | instances of a gated NodeType are authored wherever their owner lives, and the gate map is matched against target paths from every partition |
| root `namespace:_Access` | the ROOT scope's anchor is the satellite segment `_Access`, which resolves to **no partition** — there is no partition name to write |
| root `namespace:` + `id:_Policy` | the root scope's policy leg carries the EMPTY namespace by construction, for the same reason |

Note what is NOT on the list: every *scoped* leg (`namespace:{scope}/_Access`,
`namespace:{scope} id:_Policy`) already pins its partition through its first path segment, and the
fold's scope recursion means those are the queries it issues most often. The global legs are five
process-wide cached subscriptions, not a per-render storm — which is why the ordering below matters.

## What IS tractable, and what needs a decision

Measured on `origin/main`, core only. The provider (`MeshWeaver.Hosting.PostgreSql`) left this
repo in the carve-out, so the provider-side levers — skipping empty schemas, a `public`
materialization of the fold's global sets on the `partition_access` precedent — belong to
`MeshWeaver.Plugins`.

### Tractable in core

- **The `mesh_nodes` misses.** A point read of a node that may not exist is a framework defect in
  its own right, not merely slow: the owner answers a routing NotFound that terminates the stream
  AND opens the storm-breaker on that path — and the breaker fast-fails **writes** too. Fixing the
  miss beats making the miss cheaper. The `Admin/Menu/{X}` per-render probes named in #2640's body
  were fixed this way (`83b1892be`, an anchored existence `GetQuery`), 50 minutes after the
  measurement window in the issue closed.
- **The degraded render when the store cannot be reached** (#2876) — see below.

### Needs a decision, not a patch

- **`UserActivityLayoutAreas.ObserveSharedTargets`** — `nodeType:AccessAssignment
  content.accessObject:{owner}`, the home page's "Shared with me" band. This is the one core query
  that is unanchored **and uncached** **and on a per-render path**, so it is the largest single core
  contributor to the `access` bucket. It cannot be anchored (a share grant lives in the GRANTING
  partition — that is what makes it a share; pinning it to `{user}/…` returns only the grants the
  user made to themselves, and everything shared *with* them silently disappears). The remaining
  levers each change something a test must first pin:
  - Routing it through `IMeshNodeStreamCache.GetQuery` like its twin on the identity path moves the
    read from source-side RLS (`user_effective_permissions`) to consumer-side `PermissionEvaluator`
    — two independent implementations that [AccessControl](/Doc/Architecture/AccessControl) is
    explicit must agree. A divergence would silently change what "Shared with me" shows.
  - A per-viewer cache keyed on `(viewer, owner)` holds one live mesh-wide subscription per pair for
    the mesh's lifetime, and a bare `ConcurrentDictionary<key, IObservable<T>>` is the shape that
    latches a transient `OnError` forever (#1369). It needs an `IIoPool` promise slot and an
    invalidation contract, which is a design, not a patch.
- **Collapsing `GatedNodes(type)` into one `nodeType:A|B|C` fan-out.** Arithmetically attractive —
  three gated types are three mesh-wide UNIONs where one would do. But `ParsedQuery.ExtractNodeType`
  returns `null` for an alternation (`QueryOperator.In`), and that value drives satellite-TABLE
  routing and the Admin-partition routing rule. Collapsing therefore silently changes which TABLE a
  query reads for any gated type that is also a satellite type. The prerequisite is an
  alternation-aware `ExtractNodeTypes` whose consumers route only when every value agrees.
- **Making `QueryRoutingHints` live.** `MeshConfiguration.ResolveRoutingHints` registers rules that
  would pin `nodeType:Role`, `nodeType:Partition`, `nodeType:GlobalSettings` to `Admin`, and the
  in-repo comment on `InvitationNodeType` states plainly that *"the PostgreSQL query router routes
  purely by the path's first segment and does NOT consume these QueryRoutingHints yet, so this rule
  is currently inert"*. Several docstrings in this repo describe their query as pinned on the
  strength of a rule that does not run. Honouring the hints would remove real fan-outs — and would
  ALSO silently truncate any `Role` authored outside `Admin`, which is the failure this whole page
  is about. It is a deliberate scope decision, and it belongs with the fold materialization
  (plan 2 of the elimination page), not ahead of it.

## The other half: what an area SHOWS when the store cannot be reached

Issue #2876 is the same story from the render side. A `Catalog` render died on an `NpgsqlException`
raised while opening a connector, twice in 21 seconds on one pod.

**The retry was not missing.** `MeshQuery.MergeProviderObservables` wraps every provider observable
in `TransientStorageFaults.RetryTransientConnect` (#2521, merged three days *before* that capture):
250 → 500 → 1000 ms of backoff, then the last error surfaces. A database unreachable for 21 s
outlives 1.75 s of budget, so the fault reached the render exactly as designed.

What was missing is the answer to **"what does the area show when the bounded retry is honestly
spent"**. It showed the generic panel — `⚠️ This area failed to render.` plus `ex.Message`, i.e. the
driver's own text and the database host the pod could not reach, rendered to an end user — under a
log line naming the AREA as the thing that failed. Both halves are wrong about what happened, and
the log line sends whoever reads it hunting for a bug in the Catalog view.

The fix is a fifth area frame, `AreaFrameClassifier.StorageUnavailableId`:

- **Classified once**, on the typed BCL `DbException` surface — `StorageFaults.IsTransientConnectFault`
  in `MeshWeaver.Data.Contract`, which BOTH the query fan-in's retry and the render frame forward to.
  Two copies of that rule would drift silently in either direction, so `MeshQueryTransientRetryTest`
  asserts the two surfaces agree on a corpus.
- **NOT retried on the render path.** The fan-in's budget is already spent; a second retry composed
  here would be an unbounded resubscribe aimed at the resource that is already the bottleneck.
- **NOT downgraded in the log.** An availability failure must stay at `Error` where an operator sees
  it (#974) — what changes is the wording, which now names the store rather than the area.
- **NOT part of `IsTransientFrame`.** That predicate promises "this WILL be replaced without anyone
  acting". Nothing fires when a database becomes reachable again, so a waiter told this frame was
  transient would wait forever — while a waiter told it was a verdict would give up on an area that
  is perfectly fine. It is its own state precisely because it is neither.

## Why these two issues are one story

The `Catalog` render in #2876 failed inside `GetSchemasWithTableAsync` — the call that enumerates
the schemas a **fan-out** is about to UNION. An anchored query never makes it. So the render-path
fan-outs of #2640 are not only the ~2 s floor: they are the reason a single transient connect
timeout can take a whole area down, and (per the 2026-08-31 `pg_stat_activity` sampling on the
elimination page) the reason ordinary pinned point-reads queue for seconds behind
`LWLock/LockManager` while a handful of 192-schema UNIONs hold relation locks.

Eliminating the fan-outs removes both. Anchoring the ones on this page removes a permission instead.

## Related

- [Cross-Schema Fan-Out Elimination](/Doc/Architecture/CrossSchemaFanOutElimination) — the census,
  the measured lock mechanism, and the per-caller elimination plan.
- [Access Control](/Doc/Architecture/AccessControl) — the two RLS implementations that must agree.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a point read of a node
  that may not exist is a defect rather than a slow path.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — one schema per
  partition, and what a satellite segment routes to.
