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
3. one query per container: `path:{container} scope:subtree limit:all`. `scope:subtree` from the
   container carries what is nested beneath it **in the same table**, so a `_Thread`'s
   `_ThreadMessage` children ride along in the same read;
4. de-duplicate against what the main query already returned, retarget, create.

### A nested satellite in its own table gets its own container

A satellite query is answered from **one** table, chosen from the query path by the longest mapped
segment (`PartitionDefinition.ResolveTable`; the Postgres query resolves it the same way). So the
first-satellite container is not enough for a row whose own table is chosen by a deeper segment. An
indexed chat transcript is the case that bit: its parts sit at
`X/_Thread/t/transcripts/_Documents/a.md/_DocumentPart/000000` in `document_parts`, while
`path:X/_Thread scope:subtree` reads `threads`. The sweep never read the parts, the completeness guard
found them in storage, and the move was refused with `the copy cannot carry N node(s) … _DocumentPart/…`
— for every node that had a chat thread with an indexed transcript. (In memory every row sits in one
store, so the in-memory query returned the parts and no in-memory test could see it.)

The sweep therefore maps each stored path through `SatelliteTableMapping.StorageContainerOf`: the
shortest prefix that ends at a satellite segment **and resolves to the path's own table**. A row that
shares its first container's table keeps that container (`X/_Thread/t/_ThreadMessage/m` → `X/_Thread`,
one read for all of a node's threads); a part gets `…/a.md/_DocumentPart`, which reads
`document_parts`. Regression guard: `DocumentPartPlacementTest.StorageContainer_ReadsTheTableTheRowIsStoredIn`.

Containers are the unit rather than individual satellite rows because it makes the sweep **one query
per container that actually exists** — typically none — instead of one per satellite. A container
query that fails is *not* caught: it fails the copy, so the move's delete leg never runs. Swallowing
it would rebuild the same defect one level down.

The storage inventory can complete on an IO thread. The main and satellite queries run **after**
that emission, when the request's ambient `AccessContext` may be gone. Each query therefore carries
the viewer ID captured from the copy request itself; it never guesses from the callback thread.
An unresolved viewer, a query error, or an incomplete first snapshot is a refusal, not proof that
the source is absent. This distinction matters for a move from a private user space into a shared
Inbox: reporting “source not found” for a node storage still holds leaves the submitted record in
the user's space and prevents its destination watcher from handing it over.

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

## A move is answerable to everything a delete is

The delete leg removes the source straight from storage (`IStorageAdapter.DeleteMany`) — by then the
copy has re-created the subtree, so a cascade of `DeleteNodeRequest`s that could still refuse half-way
would be the wrong tool. But until the move validated its source, that leg was the ONLY removal of
the source, and it consulted no `INodeValidator` at all. Every delete guard was bypassable by moving
the node instead: moving `Admin/Tiers/pro` out of the ladder dropped an in-use plan tier past
`TierInUseDeletionGuard` (#5938), and the same held for the last-admin invariant, the partition-root
guard and a node-level Delete denial — `MoveNodePermissionAttribute` checks Delete only on the source's
NAMESPACE.

`HandleMoveNodeRequest` now runs the source through the **same pre-flight a recursive delete runs**,
before the copy writes anything:

1. the read-only-provider probe a delete runs on its root (`FindDeleteBlockingProvider`);
2. one `ValidateDeleteRequest(path, rootPath: source)` per stored path of the source subtree —
   **root included** (`PreValidateDescendantsObs(..., includeRoot: true)`) — answered by that node's
   own hub under the mover's identity. That runs the `[RequiresPermission(Delete)]` gate on the NODE
   and the node's whole delete-validator chain, with the move's source as the cascade root (so a
   validator that exempts "the whole subtree is going away" behaves exactly as it does for a delete
   of the source).

Any refusal answers `MoveNodeResponse.Fail(..., ValidationFailed)` (or `Unavailable` when a check
could not be established) naming the blocking path and the validator's own reason; nothing is copied
and nothing is removed. The TARGET half needs nothing extra: the copy leg creates every node through
`CreateNodeRequest`, which runs the create permission and create validators per node. A delete-only
validator therefore covers moves with no move-specific code.

**The planning window is closed with the delete's own subtree write scope.** The pre-flight
validates the subtree as storage lists it when the move starts; the delete leg re-enumerates after the
copy, so a node created under the source in between would be carried by the copy and removed without
having been asked. The move therefore holds `RecentlyDeletedRegistry.BeginSubtreeDeletion(source)` —
the scope the recursive delete holds from planning to commit — from BEFORE the pre-flight enumerates
until the delete leg has committed, through `RecentlyDeletedRegistry.WithinSubtreeDeletion`. While it
is held, `SubtreeDeletionGuardStorageAdapter` refuses every in-process write at or under the source
(*"the subtree '…' is currently being deleted"*). The scope is released BEFORE the response is posted,
so a caller acting on the answer — writing under the old path again — is never refused by it.

One consequence worth knowing: an edit to a node of the source while it is being moved is refused
rather than silently lost with the delete leg — and it is refused **when the owner commits it**, not
only when the owner stores it. A per-node hub commits a write to its in-memory node first and stores it
afterwards, so a guard at the store alone refused the FLUSH of a write that had already committed: the
writer heard "not applied", the owner went on serving the change, and its next save stored it once the
scope closed (measured, Systemorph/MeshWeaver#6953: two writes to a feedback record under its hand-over
move, refused at `SaveMeshNodeHandler` and stored after the move failed). Both commit turns now ask the
scope before anything is committed. The owner's own write (`UpdateOwn`) is DEFERRED exactly as it is
behind a version-guarded delete's seal (#6856): the turn commits nothing and re-runs when the scope
lifts (`RecentlyDeletedRegistry.ActiveDeletionLifted`), and the tombstone check then refuses it if the
node went away, or applies it if the operation aborted. A cross-hub patch (`ApplyMeshNodePatchInTurn`,
through `IActiveDeletionScopes`) is refused with *"Node being deleted"* — its acknowledgement is bounded,
so deferring it past a long move would answer the writer before the write — and the writer retries. Pinned by
`AWriteUnderADeletionInFlightIsRefusedAtCommitTest`. What the scope does not cover is the same thing the recursive delete's scope does not cover: a writer in
ANOTHER process. The delete answers that with its storage-verified drain; the move has no drain, so a
cross-process write landing in the window is still carried and removed without a validator having been
asked. Pinned by `MoveHoldsTheSubtreeDeletionScopeTest`, which writes under the source from inside the
source root's own delete validator — after the pre-flight's enumeration, deterministically inside the
window — and fails without the scope (the late write lands and is removed unvalidated).

**Other lifecycle paths** (swept with this change): a recursive delete pre-validates every
descendant; GitSync prune, `StaticRepoImporter` orphan removal, installer clean-up and registry
reconcile all delete through `IMeshService.DeleteNode` and are validated. `PartitionTeardown` drops a
whole partition store directly — deliberately, system-only, behind a governed `DeleteSpace`. The two
paths that used to remove a node without the delete-validator chain now run it: the
`DeleteMeshNodeRequest` handler on every per-node hub (once a raw `IStorageAdapter.Delete` with no
permission check, reachable from every ingress that forwards deliveries) forwards to the validated
`DeleteNodeRequest` under the delivery's own identity and refuses a delivery with none; and a
`MeshNode` leaving a workspace through a `DataChangeRequest` (`MeshNodeTypeSource` →
`DeleteAndPublish`) is checked by `MeshNodeDeletionDataValidator`, which runs the same chain beside
`RlsDataValidator`.

## A relocation carries what is on disk

The copy leg re-creates the source through `CreateNodeRequest`, so every create validator judges it —
including the content-schema guard, which refuses shapes it would not accept as NEW content. A record
stored before such a guard existed is not new content; it is what storage holds. The guard therefore
admits a relocation create (`AuthorshipFrom` naming the source) whose content equals the source's
stored content — see [Content Is Validated Against Its Declared Shape On Write](../ContentSchemaOnWrite),
rule 5. Without it, a legacy record could be repaired in place but never moved (#6951).

**A refused copy leg says WHY it was refused.** A per-node create carries its own typed verdict; the
copy now reads it instead of the error's wording, so a create a validator refused reaches the move's
caller as `NodeMoveRejectionReason.ValidationFailed` — a verdict every retry meets again — and no longer
as `Unknown`, which a caller has to treat as worth retrying. The feedback hand-over retried such a move
on every activation for exactly that reason (#6952).

## Where the target may lie — and the case-only rename (#6951)

The mesh keys paths **case-insensitively**: the deletion registry, the in-memory store and the file
system store all compare with `OrdinalIgnoreCase`, while a Postgres row's `path` is case-sensitive
inside its (lower-cased) partition schema. A target that equals the source ignoring case is therefore
the SAME key in some stores and a DIFFERENT key in others, and copy-then-delete is wrong for it in both:

- **case-insensitive store** — the copy's create finds the node already there, the relocation resume
  (#6310) can recognise it as "this source, already relocated", and the delete leg's plan then names
  the only copy. Measured on the in-memory mesh before the fix: `TestData/vhvgruppe-… →
  TestData/VHVGruppe-…` answered `success=True` naming the node at the OLD casing, and a move onto
  itself also answered success — a move that reports success while relocating nothing;
- **case-sensitive store** — the copy writes a new row, but the source's subtree-deletion scope compares
  ignoring case and refuses the move's own target writes: *"Cannot write 'VHVGruppe/BirgitSalzwedel':
  the subtree 'VhvGruppe/BirgitSalzwedel' is currently being deleted"* — the production refusal.

`HandleMoveNodeRequest` now classifies the target before it reads or writes anything
(`MovePaths.Classify`, on whole segments, ignoring case):

| Relation | Example | Outcome |
|---|---|---|
| `Same` | `A/b → A/b` | refused, `ValidationFailed` — nothing to move |
| `IntoOwnSubtree` | `A/b → A/b/c`, also `A/b → a/B/c` | refused, `ValidationFailed` — the copy would write into what the delete removes |
| `CaseOnlyRename` | `VhvGruppe/x → VHVGruppe/x` | renamed through a staging sibling (below) |
| `Distinct` | everything else | the ordinary move |

**A case-only rename is two ordinary relocations**: source → `{target}-recase-{token}` → target. The
staging sibling sits under the TARGET's parent (so it needs exactly the Create grant the target does)
and differs from both ends in every store, so each leg writes new keys and removes old ones, and each
runs the whole move contract above — the delete pre-flight, the subtree-deletion scope of that leg's
source, satellites, authorship and completeness. Both legs share the one move deadline. If the second
leg fails, the subtree is moved back to the source and the answer says so (reason: the second leg's
own); if moving it back fails too, the answer is `Unknown` and names the staging path that holds the
subtree. A stall after the subtree started to leave the source answers `Unknown` naming all three paths
(`activity.node.move.caseRenameStalled`). In no outcome is the subtree left at two casings.

On a case-insensitive **file system** (macOS, Windows) the two-step is what makes the new casing reach
the disk: the first leg removes the old files and their now-empty directories, so the second leg's
writes create entries with the new names. Only the segments the moved subtree OWNS are re-cased; an
ancestor segment whose casing differs (`VhvGruppe/x → VHVGruppe/x` where `VhvGruppe` is a node of its
own) stays as that ancestor's directory is named. Pinned by `MoveRenamesByCaseOnlyTest` (in-memory:
the rename, the refusals, the classifier) and `MoveRenamesByCaseOnlyOnTheFileSystemTest`, which reads
the names the volume actually holds and passes on case-sensitive and case-insensitive volumes alike.

## What this means for callers

- **Moving a node moves its comments, threads, approvals and grants.** A satellite's `MainNode` is
  retargeted with it (`RetargetNode`), so its permissions keep delegating to the node it belongs to
  and its grants keep projecting at the right prefix.
- **A move can now be refused.** Handle `MoveNodeResponse.Success == false` with
  `NodeMoveRejectionReason.ValidationFailed`; the message names the paths. That includes every
  refusal a DELETE of the source would get — if you may not delete it, you may not move it — and every
  create validator that refuses the copy leg. `ValidationFailed` is a verdict: do not retry it
  unchanged.
- **Renaming by case is a move.** Send `MoveNodeRequest(old, newCasing)`; a move onto itself or into
  its own subtree is refused with `ValidationFailed`.
- **Do not write to a node while it is being moved.** The write is refused (*"Node being deleted"*)
  and nothing is committed; retry once the move has answered.
- **`CopyNodeRequest.IncludeSatellites` costs an enumeration, not a filter.** It is not free, and it
  is not a flag over a set the query already returned.

## Related

- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a query row is neither the node nor
  the whole set.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — the per-partition schema and the
  satellite-table routing this page's asymmetry falls out of.
- [Moved-Node Redirects](/Doc/Architecture/NodeRedirects) — keeping links alive across a move.
- [Access Control](/Doc/Architecture/AccessControl) — satellite permissions delegate to `MainNode`.
