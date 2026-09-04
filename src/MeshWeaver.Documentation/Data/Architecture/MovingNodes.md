---
NodeType: Markdown
Name: "Moving Nodes — what a move must carry"
Abstract: "A move is copy-to-target plus delete-the-source, and the two legs used to enumerate the subtree through different surfaces: the copy read the content query, the delete read storage. Everything in the difference — every _Comment, _Thread, _Approval and _Access row — was destroyed, and the move reported success."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#6a1b9a'/><path d='M6 8h5l1.5 2H18v7H6z' fill='none' stroke='white' stroke-width='1.6' stroke-linejoin='round'/><path d='M9.5 13.5h5M13 12l1.5 1.5L13 15' stroke='white' stroke-width='1.6' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Move"
  - "Satellites"
  - "Data Loss"
---

A **move** is not a primitive. `MoveNodeRequest` is copy-to-target followed by delete-the-source,
orchestrated by `HandleMoveNodeRequest` in `src/MeshWeaver.Mesh.Contract/MeshExtensions.cs`. That
makes the operation's correctness a question about **two enumerations of one subtree**: what the copy
leg carries, and what the delete leg removes. They must be the same set. When they were not, the
difference was destroyed without a word.

## The defect (#3272)

The two legs read different surfaces:

| Leg | Enumeration | Sees satellites? |
|---|---|---|
| **copy** | the content query `path:{source} scope:subtree` | **no** |
| **delete** | `IStorageAdapter.ListDescendantPaths(source)` | **yes** |

`ListDescendantPaths` is documented as *"a native prefix enumeration across every table of the
partition"* — it is the authoritative store read, and it was chosen deliberately (issue #839: planning
a recursive delete off the eventually-consistent catalog left survivors). The content query is
something else entirely: a **primary-table** read.

That is not an in-memory quirk to be patched in one adapter — it is the same answer on both backends,
arrived at from both directions:

- **in-memory** — `StorageAdapterMeshQueryProvider.IsExcludedFromResults` strips satellite-path rows
  from every non-satellite-targeted query *on purpose*, so the single-store adapter matches
  Postgres' table separation rather than leaking rows PG could never return;
- **Postgres** — a query resolves to ONE table (`PostgreSqlPartitionedMeshQuery.NeedsFanOut` →
  `ResolveTable`), taken from the query path's satellite segment or a satellite `nodeType` filter.
  `path:{main} scope:subtree` names neither, so it reads `mesh_nodes`. (It *does* union the CONTENT
  satellite tables — `Source`/`Test` → `code` — because those rows are primary content; the metadata
  satellites are deliberately not in that union.)

So `CopyNodeRequest.IncludeSatellites = true`, which the move has always passed, was filtering a set
that was already empty. Copy skipped them; delete removed them; `MoveNodeResponse.Success` was
`true`. Measured on the core in-memory mesh:

```text
BEFORE descendants of TestData/sat-1c7e839f: [TestData/sat-1c7e839f/_Comment/c1]
MOVE success=True error=
AFTER  descendants of TestData/satmoved-1c7e839f: []
OLD satellite path still present? False
NEW satellite path present?       False
```

The blast radius is the whole subtree — every satellite of every descendant, not just of the node
named in the request — and the satellite prefixes are exactly where the durable, hard-to-recreate
context lives: `_Comment`, `_Thread`, `_Approval`, `_Tracking`, `_Activity`, and `_Access`, so a move
could silently drop the grants that made a subtree reachable. There is no version history behind a
satellite to recover from.

## Why the copy leg cannot simply read storage

The obvious symmetry — have the copy enumerate `ListDescendantPaths` too — is a **privilege
escalation**. The content query is the read that row-level security filters; copying straight out of
the store would let a caller duplicate rows they cannot read into a location they control. The delete
leg gets to be raw because deleting is gated by the move's own permission check on the subtree; the
copy leg's output is *readable afterwards*, which is a different question.

So the two responsibilities are split, and the split is the design:

> **Storage says which CONTAINERS exist. The query says what may be carried out of them.**

`IStorageAdapter.ListDescendantPaths` contributes a **path set and nothing else** — no content ever
crosses from it into the copy. Each satellite container it reveals is then read back through the same
RLS-filtered `IMeshService.Query`, in the one shape every backend resolves to the satellite's own
table.

## The container sweep

`SatelliteTableMapping.SatelliteContainerOf(path)` returns everything up to **and including** a path's
first satellite segment — `Doc/_Thread/t1/_ThreadMessage/m1` → `Doc/_Thread` — and its existing
sibling `OwnerOfSatellitePath` returns the main node the container hangs off (`Doc`). The two split
one path at the same seam, which is what makes the sweep expressible:

1. `ListDescendantPaths(source)` → the authoritative path inventory;
2. map it to the distinct **containers**, keeping only those whose owner is a main node this copy is
   actually carrying (so `IncludeDescendants = false` keeps the root's satellites and not a child's,
   and a main node the caller may not read never has its satellites swept in behind it);
3. one query per container: `path:{owner}/_Segment scope:subtree limit:all`. `scope:subtree` from the
   container carries whatever is nested beneath it, so a `_Thread`'s `_ThreadMessage` children ride
   along in the same read;
4. de-duplicate against what the main query already returned, retarget, create.

Containers are the unit rather than individual satellite rows because it makes the sweep **one query
per container that actually exists** — typically none — instead of one per satellite. A container
query that fails is *not* caught: it fails the copy, so the move's delete leg never runs. Swallowing
it would rebuild the same defect one level down.

## The refusal — `RequireComplete`

Carrying satellites fixes the case we know about. The rule the operation actually needs is stronger,
because a move DESTROYS the source:

> **A move relocates the node and everything that belongs to it, or it refuses.**

`CopyNodeRequest.RequireComplete` states that, and the move is its only caller. Before the copy
creates anything, the set it is about to carry is compared against the same
`ListDescendantPaths(source)` inventory the delete leg is planned from. Anything in the difference is
named in the error, and the copy fails with `NodeCopyRejectionReason.ValidationFailed`:

```text
Refused: the copy cannot carry 1 node(s) stored under {source}: {source}/_Comment/c1
```

Nothing is written at the target, nothing is removed at the source. The check has no way to pass
vacuously either: a `RequireComplete` copy with no `IStorageAdapter` behind it — nothing to enumerate,
so nothing to compare against — refuses rather than reporting a completeness it never established.

This closes the class rather than
the instance: a node the caller cannot read, a satellite kind added later, a backend whose query
surface diverges again — each becomes a loud refusal instead of a silent deletion. A plain **copy**
never sets the flag: it deletes nothing, so carrying less than everything loses nothing.

The trade-off is deliberate and worth stating plainly: a move that used to "succeed" destructively can
now fail. A refusal is recoverable and legible; the alternative was not.

## What this means for callers

- **Moving a node moves its comments, threads, approvals and grants.** A satellite's `MainNode` is
  retargeted with it (`RetargetNode`), so its permissions keep delegating to the node it belongs to
  and its grants keep projecting at the right prefix.
- **A move can now be refused.** Handle `MoveNodeResponse.Success == false` with
  `NodeMoveRejectionReason.ValidationFailed`; the message names the paths.
- **`CopyNodeRequest.IncludeSatellites` costs an enumeration, not a filter.** It is not free, and it
  is not a flag over a set the query already returned.

## Related

- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a query row is neither the node nor
  the whole set.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — the per-partition schema and the
  satellite-table routing this page's asymmetry falls out of.
- [Moved-Node Redirects](/Doc/Architecture/NodeRedirects) — keeping links alive across a move.
- [Access Control](/Doc/Architecture/AccessControl) — satellite permissions delegate to `MainNode`.
