---
Name: A Bulk Create Compensates Per Node
Category: Architecture
Description: A bulk create writes every row before any post-creation handler runs, so one critical handler failure left TWO populations of ghost rows — the node that failed, and every node after it whose handlers never ran and which nothing can tell apart from a success. What the rollback removes, what it keeps, why it walks the batch backwards, and the measured reason the stop is a fault rather than a Take(1).
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h13a4 4 0 0 1 0 8H8"/><path d="m11 12-3 3 3 3"/><path d="M19 19h2" opacity="0.35"/></svg>
---

# A Bulk Create Compensates Per Node

A create is **all-or-nothing**, and it has been since #638 on the singular path: when a post-creation
handler that declares `INodePostCreationHandler.FailsCreateOnError` fails, `CompensateFailedCreate`
deletes the row the create just wrote, and the response carries the original cause plus the rollback
outcome.

The reason is stated in the code and is worth repeating, because it is the whole point: **reporting
`Fail` while LEAVING the row is unrecoverable for the caller.** A retry answers *"Node already
exists"*, and nobody can clean it up either — the handler that failed is typically the one that
grants the creator ownership of a brand-new partition, so the row that remains is a partition root
that RLS denies everybody, including the person who asked for it.

`CreateNodesRequest`, the bulk sibling, ran the same handlers and carried a comment claiming *"same
semantics as the singular create"*. It had no compensation at all (#4449 item 2).

## It leaves TWO populations of ghosts, not one

The bulk verb is *validate-all, then write*: phase 6 is ONE ordered `WriteMany`, and only then do the
post-creation handlers run, sequentially, per node. So every row is already durable when the first
handler starts, and a critical fault at index *k* leaves:

| nodes | state | before |
|---|---|---|
| `0 … k-1` | created, handlers COMPLETED | fine — keep |
| `k` | created, its critical handler FAILED | row kept (the singular path would delete it) |
| `k+1 … n` | created, handlers **NEVER RAN** | row kept, nothing their type's contract requires was ever done |

The third row is the larger hazard and the one the original filing does not mention: those nodes are
**indistinguishable from successfully-created ones by inspection**. Nothing about the row says that
no owner was granted, no `Admin/Partition` definition was announced, no seed was written. A Space
among them looks exactly like a Space that works.

## What the rollback does

`RunBulkPostCreationHandlers` is phase 7. On the first critical failure it compensates `k … n` and
keeps `0 … k-1` — **exact parity with the singular path, per node** — and answers the caller with a
`CreateNodesResponse.Fail` carrying the original cause, the rollback outcome, the failing path, and
the surviving nodes in `Created`.

**It walks the batch backwards (`n … k`).** Caller order is the bulk create's contract — parents
before children — so removing back-to-front takes a child out before its parent.

**Two facts make deleting rows safe here, and both are load-bearing:**

1. Everything in the written list is **by construction this request's own**. Phase 1 filters every
   pre-existing path out into `existingPaths`, which is reported and never touched.
2. `CompensateFailedCreate` **re-reads the row and compares `CreatedDate`** before deleting. A path a
   concurrent writer has re-created in the meantime is left alone and SAID SO — *"the stored row is
   no longer the one this request wrote. Remove it manually before retrying."* That comparison is
   only meaningful because the stamp is minted at a resolution the row can hold — read *"The check
   first has to RUN"* below before treating "it compares `CreatedDate`" as a guarantee.

Partition artifacts are deliberately **not** dropped, for the same reason the singular path does not
drop them: provisioning is idempotent, so leaving an empty schema costs the retry nothing, whereas
DROPping one is not reversible and would destroy data whenever the schema was not introduced by this
create.

## The stop is a FAULT, not a `Take(1)` — measured

The obvious shape is to catch each node's fault into a failure element, `Concat` them, and take the
first:

```csharp
// ❌ MEASURED NOT TO STOP THE CHAIN
.Select((node, index) => RunPostCreationHandlersObs(...)
    .IgnoreElements()
    .Select(_ => (Index: index, Error: (Exception?)null))
    .Catch<…, Exception>(ex => Observable.Return((Index: index, Error: ex))))
.Concat()
.Take(1)
```

It compensates the right nodes, and it does **not** stop the chain.
`Observable.Concat(IEnumerable<IObservable<T>>)` went on to subscribe every later node although the
downstream had already completed and disposed: in the reproduction, the handler recorded having run
for all five nodes of a five-node batch that failed at index 2.

That is not merely wasted work. Those handlers write side effects — a creator grant, an
`Admin/Partition` definition, onboarding seeds — **for rows this request is about to remove**, and
the rollback would leave them orphaned in exactly the shape #638 exists to prevent.

So the per-index `Catch` **re-throws**, tagged with the index (`BulkPostCreationFault`). A fault
terminates a `Concat` by construction, which is the stop; the tag carries which node failed, which is
the cut. The single `Catch` that turns the fault into a decision then sits DOWNSTREAM of the
`Concat` — deliberately, and safely, because there is no later node left to fault, so it can run at
most once per batch.

> **The general rule.** A `Catch` downstream of a sequential composition sees every later element's
> fault too. Attribution has to be attached upstream, per element; the decision may be taken
> downstream only when the first fault has already ended the sequence.

### …and the tag only covers what is INSIDE the observable

`RunPostCreationHandlersObs` does real work on the way to *returning* its observable: it resolves the
registered handlers and asks each one's `Matches`. That runs while the `Concat` is ENUMERATING —
outside the observable the per-index `Catch` is attached to — so a handler whose `Matches` throws
reached the outer error arm **untagged**, and the batch reported a partial landing having compensated
nothing. Measured, as the negative control for the fix: *"failed AFTER 5 node(s) were persisted —
reporting the partial landing"*, all five rows left, no path attributed.

`Observable.Defer` around the call moves it inside the subscription, so a synchronous throw and a
reactive fault take the **same** rollback path. Pinned by
`AHandlerThatThrowsWhileMatching_IsCompensatedLikeAnyOtherCriticalFailure`.

## A rollback reports FOUR states, because a boolean lies

A batch rollback reports a **count**, and a count built from "removed, or anything else" asserts
things nothing established. So `CompensateFailedCreate` returns a `RollbackDisposition`:

| state | what it means | what the batch does with it |
|---|---|---|
| `Removed` | this rollback deleted the row this create wrote | counted as rolled back |
| `NothingToRemove` | no row of ours was there to remove | reported separately, never as a removal |
| `LeftInPlace` | a row was READ and is no longer ours, so it was deliberately kept | quoted verbatim — a human decides |
| `Undetermined` | the read or the delete failed, **or the row came back with no creation stamp to compare**; whether a row remains ours is UNKNOWN | quoted verbatim — never reported as present or absent |

`Undetermined` is the one that used to be wrong in both directions: the old text asserted *"the
partially-created node is still present"* from a branch that may have been entered **because the read
failed** — the same distinction [Undetermined Is Not No](../UndeterminedIsNotNo) draws for a gate.

## The check first has to RUN — the stamp must survive its own row

The comparison is exact, and it compares an **in-memory** value against the **same value after a
round trip through a column**. So before asking what it excludes, ask whether it recognises its own
row at all — and until #4506 it usually did not.

A `DateTimeOffset` tick is 100 ns. PostgreSQL `timestamptz` — where `created_date` and
`last_modified` live — holds **microseconds**. A stamp taken straight from `DateTimeOffset.UtcNow`
therefore comes back from its own row *different*, the lineage check reads that as "somebody else's
node", and the rollback stands down on the row it had just written. The caller is then told the
partially-created node is still present and must be removed by hand — the exact unrecoverable ghost
#638 exists to prevent, now wearing the costume of a safety feature.

**Measured, not reasoned.** On memex.meshweaver.cloud (2026-09-16), one node —
`Doc/_Activity/import-f7f86c9f5020ab36` — carries both halves of the proof in a single payload:

| where the timestamp lives | value | precision |
|---|---|---|
| `content` (JSON — lossless) | `…T12:59:47.9123072Z`, `…9123038Z`, `…9123109Z` | 7 digits, **all three** ending in a non-zero sub-microsecond digit |
| `createdDate` / `lastModified` (column) | `…T12:51:02.077625+00:00`, `…T12:59:47.914802+00:00` | exactly 6 digits |

Same process, same instant, two different values. Fifteen further column timestamps sampled across
`Doc` were 6 digits without exception.

**The fix is at the mint, not at the comparison.** Both create paths (and the installer's direct
write) stamp through `MeshNode.StorageStableNow()`, and a caller-supplied stamp is floored the same
way by `MeshNode.StorageStable(...)` — so the node a create emits carries a timestamp its own row can
hold exactly, and the check compares one value to itself. Teaching the comparison a tolerance instead
would have left every other reader of a freshly-created node comparing a value against a truncation
of itself.

> A value that is minted at one precision and stored at another is not "nearly the same value". Every
> exact comparison across that boundary is wrong, and it is wrong silently.

**Why no existing test could see it.** All of them run on `InMemoryStorageAdapter`, which holds the
CLR instance and hands the same object back — so the lineage check compared a `DateTimeOffset`
against *itself* and no round trip happened. `RollbackLineageSurvivesTheStoresTimestampResolutionTest`
closes that by modelling a microsecond-resolution column over the real store, and it drives the
sub-microsecond tick in as a caller-supplied `CreatedDate` rather than sampling the clock —
`DateTimeOffset.UtcNow`'s resolution is a property of the OS (measured 2026-09-16: this macOS host
mints whole microseconds, 200 of 200; the Linux portal above plainly does not), so a repro resting on
the ambient clock would be real on CI and **vacuous** on a developer's machine.

### A row the store returns with NO stamp establishes nothing

On Postgres the authorship columns exist only on `mesh_nodes`; every satellite table (`_Access`,
`_Thread`, `_Activity`, `_Comment`, `Source`, …) is read with `NULL::timestamptz AS created_date`. A
rollback aimed at a satellite path therefore reads `default` for **every** row, its own included.

Measured on memex.meshweaver.cloud (2026-09-16): `Store/_Activity/4ad7e757`, a real satellite-table
row, comes back carrying `lastModified` — that column does exist there — and **no `createdDate` and
no `createdBy` at all**. It is only the authorship trio that is missing, and it is missing for every
row of every satellite table, so there is nothing for the lineage check to compare.
That is not a mismatch — nothing was compared — so it is now reported `Undetermined` rather than
`LeftInPlace`, which used to tell the operator a specific and false thing ("the stored row is no
longer the one this request wrote") about a row nobody had looked at. **It still deletes nothing:** an
unestablished lineage must never widen what a rollback removes. Giving those tables a real
`created_date` is a storage-side change and is not this page's.

## What the lineage check does NOT guarantee

With the stamp round-trip-stable, the check does what it claims: it refuses to delete a row whose
`CreatedDate` differs from the one this request wrote. It is still **not** a transactional guarantee,
and the limits are worth stating plainly rather than discovering later (both raised in review on
#4503, both **pre-existing and identical on the singular path since #638** — neither is introduced by
the batch rollback):

1. **The read and the delete are not atomic, and the window is REACHABLE.** A delete-then-recreate
   that lands between the lineage read and `DeleteAndPublish` has its *replacement* deleted. Nothing
   narrows it structurally: the create handler returns `Processed()` immediately and the chain runs
   on the IO pool rather than inside a hub turn, so no actor serialises the path; the read and the
   delete are two separate storage turns with nothing holding the row between them. What makes it
   *unlikely* is arithmetic, not design — a few milliseconds, and a concurrent actor that must delete
   and re-create the very same path inside it. On the canonical #638 case it is close to unreachable
   for a second reason: the row is a partition root whose ownership grant is exactly what failed, so
   RLS denies everybody the delete that would have to come first. The bulk rollback is the wider
   exposure, because nodes *k+1…n* are ordinary nodes in an existing partition where others do hold
   rights. Closing it needs a conditional delete in the `IStorageAdapter` contract — the DELETE-side
   twin of `WriteIfVersion` — implemented by every backend, which is the cost #4506 weighs.
2. **`CreatedDate` is caller-supplied when the caller supplies one.** Both create paths preserve an
   authored stamp, so it is a lineage *hint*, not a server-owned identity token. Concretely, it
   admits two interleavings with **no race at all**: two imports carrying the same authored
   `CreatedDate` for the same path, and two creates of the same path within one microsecond. Note
   what the round-trip fix did *not* change here — the stamp never had more than microsecond entropy
   once it reached the row, so flooring the mint costs the key nothing it actually had.

What the check therefore means is *"it will not delete a row it can SEE is not ours"* — never *"it
cannot delete someone else's row"*.

### The conditional delete was weighed, and DECLINED — #4506, closed 2026-09-17

Both limits above stand as stated. The remedy they point at — a `DeleteIfCreatedAt` in the
`IStorageAdapter` contract, the DELETE-side twin of `WriteIfVersion` — was weighed and is **not being
built**. The reasoning, so nobody re-derives it:

- **The precedent makes it cheaper than #4506 assumed, and that still is not enough.**
  `IStorageAdapter` already carries `WriteIfVersion` and `DeleteIfExists`, both with an honest
  non-atomic default and overrides in the two backends that can express the condition — so this would
  be a third instance of an accepted pattern, not a new concept. What it adds is a third method
  carrying the footgun both of those carry: *"Decorators MUST forward … or the atomicity is silently
  lost at the outermost decorator that falls back to the default."* That failure is silent and
  fleet-wide, and it has already happened twice on this interface (`Changes`; `ReadMany`, #4200).
- **`Version` cannot stand in for `CreatedDate`,** which removes the cheap version of the fix. The
  replacement in this race arrives via delete-then-recreate, so its version sequence starts again
  where ours did and the two rows can carry the same version. `CreatedDate` is the only field that
  necessarily differs — which is precisely why the check already compares it, and why the remedy
  inherits limit 2's weakness rather than fixing it.
- **On every satellite path the new primitive would have no input at all.**
  `PostgreSqlStorageAdapter.AuthorCols` projects the authorship trio only for `mesh_nodes`; every
  satellite table (`_Access`, `_Thread`, `_Activity`, `_Comment`, `Source`, …) is read with
  `NULL::timestamptz AS created_date` (MeshWeaver.Plugins#1971). A conditional delete keyed on the
  creation stamp would be handed `default` for every row there, its own included — unusable exactly
  where lineage already cannot be established.
- **The behaviour it replaces already fails closed.** Where lineage cannot be established the
  rollback deletes nothing and says which of the two it is (`Undetermined` / `LeftInPlace`); the
  operator gets a specific sentence rather than a silent ghost.

🚨 **What that argument does NOT rest on: "nobody else has rights".** That holds for the canonical
#638 case — a partition root whose grant is what failed — and **not** for the bulk rollback, where
nodes *k+1…n* are ordinary nodes in an existing partition. The bulk case is narrow because of the
arithmetic stated in limit 1 (two storage turns, a few milliseconds, and a concurrent actor that must
delete *and* re-create that exact path inside the window), not because of access control. Anyone
re-opening this should argue against the arithmetic, not against RLS.

**What would reopen it:** the create rollback becoming a routine path rather than a handler-failure
compensation, or a server-owned row identity token arriving for some other reason — at which point
conditioning the delete on it is nearly free and should simply be done.

## What is asserted

`BulkCreateCompensatesAFailedCriticalHandlerTest` (MeshWeaver.Graph.Test) drives a five-node batch
through the real bulk verb with a critical handler that faults on index 2, and asserts:

- the store — read back in ONE `ReadMany` off the storage adapter, not a cache or a query index —
  holds `N0` and `N1` and nothing else;
- the response is a failure naming `N2`, quoting the handler's own message, reporting the rollback,
  and listing `N0`/`N1` as created;
- the handler ran for `N0`, `N1`, `N2` **and never for `N3`/`N4`** — which is what pins the stop, and
  what would go red if the `Take(1)` shape above ever came back;
- a control batch with nothing faulting lands in full, with nothing rolled back.

`RollbackLineageSurvivesTheStoresTimestampResolutionTest` (same project) asserts the half above —
that the rollback recognises its OWN rows through a microsecond-resolution column, that every stamp
a create mints is one its column can hold, that a row returned without a creation stamp is
`Undetermined` **and is not deleted**, and — the control that keeps the rest honest — that a clean
batch still lands in full through the same modelled column.

Related: [Copy Completeness](../CopyCompleteness) — the same question asked of a copy, where the answer
is deliberately different (it stops rather than rolling back, and says what it could not read).
