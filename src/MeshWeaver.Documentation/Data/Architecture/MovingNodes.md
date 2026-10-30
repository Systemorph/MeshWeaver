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

### Every path in a query is a quoted literal (#7542)

The source and container paths are spliced into the copy's queries through
`QueryParser.PathLiteral` — `path:"P/License Email Rubin" scope:subtree`, never the bare path. The
query tokenizer splits on whitespace, so a bare `path:P/License Email Rubin` reads as the filter
`path:P/License` plus two free-text words: a query for a different node. Storage, enumerated by raw
prefix, still lists the source, so the two surfaces disagreed on every attempt and each move of a
node with a space in its path was refused as *"Copy reached no verdict: … is stored, but the subtree
query under the caller did not return it yet"* — including the rename meant to remove the space.
Regression guard: `MoveRenamesOffAWhitespacePathTest` (a spaceless move of the same shape is its
control). A path that carries BOTH quote characters has no quoted literal (the query language
has no escape). `PathLiteral` then writes it bare, as it always was: a bare value runs to the next
whitespace and reads a quote inside it as data, so such a path still copies. A bare value still
carries query syntax, though, so the bare form is used only when the path holds none of `| * ( )` AND
reads back as exactly one exact path anchor equal to itself — `a'b"c|other` would otherwise be two
path alternatives, a copy aimed at a different set of nodes. Anything else (whitespace, that syntax,
a leading quote) cannot be written at all and is refused by name. Guards: the copy theory in the same
test class, with a mixed-quote leaf, and `PathLiteralReadsBackAsOnePathTest`.

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

The delete leg retires every source path it removed: it invalidates the path's stream-cache entry
and disposes the per-node hub activated there, as the delete commit does (#7171). That holds when the
batch fails part-way too: `DeleteMany` then faults with a `BatchDeletePartiallyAppliedException`
naming the rows that left storage, and the move retires exactly those before it answers `Unknown`. A
source row the batch did not remove keeps its hub (#7396; pinned by
`APartWayBatchDeleteRetiresWhatItRemovedTest`, see
[Recursive Delete Drain](../RecursiveDeleteDrain) for the storage contract).

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

## A half-landed move is finished by re-issuing it (#6310)

Copy-then-delete is not atomic. A move whose reply is lost (#6107), or whose copy leg fails part-way
— the copy creates the root before its descendants, so a refused descendant leaves the root behind —
ends with the copy (or part of it) at the target and the source untouched. Re-issuing the same
`MoveNodeRequest` CONVERGES: it finishes the move instead of refusing the target it wrote itself.

In the copy leg of a move (a preserving copy), a create refused with `NodeAlreadyExists` is checked
against what is stored at the target, and the existing node **stands in for the create** only when
both hold:

1. **It is the same node** — same `NodeType`, `CreatedBy` and `CreatedDate`. A preserving create
   copies these verbatim from the stored source, and no other create can mint them.
2. **The source is unchanged since the copy landed** — the source's `LastModified` is one the target
   has carried: its current stamp (nobody wrote to the target since), or one of its recorded versions
   (somebody did; the target's later state is then its NEWER state, and it is kept).

Descendants the earlier attempt never reached are created, and then the delete leg removes the source.
The resume never writes over the target and never deletes at it. Every other case keeps the
`TargetAlreadyExists` refusal. A refusal never writes over or deletes a node that already existed at
the target, and never deletes anything at the source. It is NOT a no-op at the target, though: the
copy creates the root before its descendants and runs every create it issued to its verdict, so a
collision on one descendant can leave NEW partial copies behind — nodes this attempt itself created
(the root, siblings that landed). They are named in the copy's `Written`, never deleted by the
refusal, and the next attempt resumes them, so re-issuing the move once the collision is cleared
finishes it:

| Between the attempts | Store keeps history | Retry |
|---|---|---|
| nothing written | either | resumes; source removed |
| a foreign node cleared from a descendant's target | either | resumes, carries the rest; source removed |
| the TARGET edited | yes | resumes; the target's edit is kept, the stale source removed |
| the TARGET edited | no (in-memory) | refused — "unchanged" cannot be established; both kept |
| the SOURCE edited | either | refused — resuming would discard the source's edit; both kept |
| a different node at the target | either | refused — a genuine collision |

Once a retry has converged, a further one answers `SourceNotFound` and touches nothing. "The same
node" needs the store to keep `CreatedBy` and `CreatedDate`, and every store does: Postgres in its
columns, the file-system store in a JSON node file and, for a node it keeps as `.md`, in the front
matter (`CreatedBy:` / `CreatedDate:`, #7352). The front-matter keys are written only for a node that
has them and are never invented on read, so an authored `.md` file that carries neither stays
byte-identical on save — but such a node is unattributed, so a half-landed move of it is refused like
any collision rather than resumed. The keys are for the STORE, not for a git mirror: a sync export
writes `.md` (and `.cs`) files without the two stamps, exactly as before, so a mirror of hand-edited
pages never gains a creator id and an instant per page. JSON files are the full node and keep them.
Pinned by
`MoveResumesAHalfLandedMoveTest` (the copy leg alone landed, root only; a source edit; a foreign
target), `MoveConvergesAfterAFailedCopyLegTest` (a real move whose copy leg failed on a descendant,
retried to convergence and once more; a target edit on a store without history) and
`MoveResumesOverAnEditedTargetWithHistoryTest` (the file-system store, which keeps versions: a target
edit resumes and is kept, a source edit is refused, a half-landed `.md` node resumes, a different
`.md` node at the target is refused); the front-matter round trip is pinned by
`MarkdownFrontMatterTolerantYamlTest`.

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

**A case-only rename is two ordinary relocations**: source → `{target's parent}/recase-{token}` →
target. The staging sibling sits under the TARGET's parent (so it needs exactly the Create grant the
target does), its leaf is `recase-` plus a 12-character random token — a fixed length, so a long
target leaf never pushes the staging name (or the atomic write's temporary file name beside it) past
a file system's 255-byte component limit — and it differs from both ends in every store, so each leg
writes new keys and removes old ones, and each
runs the whole move contract above — the delete pre-flight, the subtree-deletion scope of that leg's
source, satellites, authorship and completeness. Both legs share the one move deadline.

**A failed case-only rename never deletes at its target.** It either restores — when the failed
leg's copy provably wrote nothing — or answers `Unknown` naming source, staging and target, and the
operator resolves. Nothing on the platform proves that a row at the target is still the one the copy
inserted: storage writes are not insert-only (a competing create between the create handler's
existence read and its write is returned as success), the deletion scope
(`RecentlyDeletedRegistry.WithinSubtreeDeletion`) blocks in-process writers only, and no
compare-and-delete checks identity across silos (another silo can delete and re-create the target at
version 1). A compensating delete could therefore remove somebody else's node, so the undo fails
closed. Four review rounds of #6976 each found another way a compensating delete went wrong before
this rule replaced it.

| The failed leg… | What happens | Answer |
|---|---|---|
| was refused before it wrote anything | leg 1: nothing to undo; leg 2: the subtree is moved back from staging | leg 1: its own refusal; leg 2: `caseRenameRestored`, the leg's own reason |
| failed in its COPY having provably written nothing (`Written` empty, `WrittenUnsettled` false) | leg 1: nothing to undo; leg 2: the subtree is moved back from staging | leg 1: the copy's own verdict; leg 2: `caseRenameRestored` carrying the copy's transcript |
| failed in its COPY after writing at its target | nothing is deleted, nothing is moved back; target and staging stay as they are | `Unknown`, `caseRenameCopyWrote`, naming the written paths and where the complete subtree is |
| failed in its COPY without being able to say what it wrote | nothing is deleted, nothing is moved back | `Unknown`, `caseRenameCopyUnsettled`, naming where the complete subtree is |
| copied completely, then failed DELETING its source | nothing is moved back: that leg's source may now be incomplete | `Unknown`, `caseRenameDeleteFailed`, naming the possibly incomplete path and where the complete subtree is |
| the move back itself failed | nothing more is done | `Unknown`, `caseRenameRestoreFailed` |

**Where the complete subtree is depends on WHICH leg failed and WHERE**, and every `Unknown` answer
says it ("The complete subtree is at …"):

| Failed in | Source | Staging | Target |
|---|---|---|---|
| leg 1, copy | complete, untouched | partial (what the copy wrote) | untouched |
| leg 1, deleting the source | may be partial | **complete** | untouched |
| leg 2, copy | gone | **complete**, untouched | partial (what the copy wrote) |
| leg 2, deleting staging | gone | may be partial | **complete** |

The table is the expectation, not a verdict: an operator resolving an `Unknown` reads ALL THREE paths
the answer names — source, staging and target — before deciding which to keep, and never assumes
staging is complete merely because the second leg failed. Each cause has its own catalog key (`activity.node.move.caseRename…`),
so the whole answer renders in the viewer's language; only upstream text — an exception's own message,
the failed leg's English verdict, the listed paths — travels as a raw argument, and the failed copy's
keyed transcript is carried before it.

**What a failed copy reports** (`CopyNodeResponse`): `Written` — every target path a create landed at,
or a resumed relocation (#6310) stood in for — and `WrittenUnsettled` when that set may be incomplete:
creates still outstanding when it ran out of time, or a create that failed after it may have written.
A failed create is not proof of no write: the create handler writes the row before its post-creation
steps, and when a critical step fails and the rollback fails too (or a step runs past the deadline) it
answers a failure over a row that stays. So `NodeAlreadyExists` counts as no write; a judged refusal
(`ValidationFailed`, `InvalidPath`, `InvalidNodeType`) is confirmed by reading the path — a row there
is possibly ours; anything else is possibly written. For `Written` to be complete the copy runs EVERY
create it issued to its own verdict before it answers (a first failure no longer cuts the rest off
mid-flight). `Written` is never a list to delete — only its emptiness is used.

A stall after the subtree started to leave the source answers `Unknown` naming all three paths
(`activity.node.move.caseRenameStalled`). The refusals of `Same` and `IntoOwnSubtree` are keyed too
(`activity.node.move.ontoItself`, `activity.node.move.intoOwnSubtree`).

On a case-insensitive **file system** (macOS, Windows) the two-step is what makes the new casing reach
the disk: the first leg removes the old files and their now-empty directories, so the second leg's
writes create entries with the new names. That depends on the delete pruning the node's OWN directory:
`FileSystemStorageAdapter` starts pruning empty directories at `{path}/` itself — which, in the
JSON + `index.md` split pattern, held the node's `index.md` — rather than at the parent of its file;
starting at the parent left an empty old-cased directory that the second leg then reused on a
case-insensitive volume, reporting success with the descendants under the old casing. Only the segments the moved subtree OWNS are re-cased; an
ancestor segment whose casing differs (`VhvGruppe/x → VHVGruppe/x` where `VhvGruppe` is a node of its
own) stays as that ancestor's directory is named. Pinned by `MoveRenamesByCaseOnlyTest` (in-memory:
the rename, the refusals, the classifier) and `MoveRenamesByCaseOnlyOnTheFileSystemTest`, which reads
the names the volume actually holds — the target's exact directory and node file, and no staging
entry — and passes on case-sensitive and case-insensitive volumes alike, for a plain source, a
JSON + `index.md` split source, and a 200-character id. The failure paths are pinned
by isolated faults: `CaseOnlyRenameNeverDeletesAtAFailedSecondLegsTargetTest` (one create refused at
the target's exact casing: a refused DESCENDANT leaves the root the copy wrote — `Unknown`, target and
staging untouched; a refused ROOT means nothing was written — the subtree is moved back and reads back
at the source's casing), `CaseOnlyRenameReportsAPartialFirstLegDeleteTest`
(`FaultInjectingStorageAdapter.FailDeleteMany` stops the first leg's delete part-way; the answer names
the staging path, which holds the whole subtree),
`CaseOnlyRenameDoesNotRestoreOverACreateThatFailedAfterWritingTest` (a critical post-creation step fails
for one target descendant and `FailDeletes` kills that create's own rollback, so its row stays; before
the fix the rename answered "moved back" over it — now `Unknown`, nothing moved back, staging intact),
`CaseOnlyRenameKeepsWhatAPostCreationHandlerWroteTest` (a handler writes a child under the target root,
then a descendant is refused; the child and root stay) and
`CaseOnlyRenameKeepsATargetEditedBeforeItsCleanupTest` (an ordinal-path create validator parks the
second leg while the test edits the landed target root and creates a child beside it; before the fix
the undo deleted the edit and answered "moved back" — now both survive and the answer is `Unknown`).

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
