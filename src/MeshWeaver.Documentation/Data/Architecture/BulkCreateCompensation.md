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
   no longer the one this request wrote. Remove it manually before retrying."*

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
| `Undetermined` | the read or the delete failed; whether a row remains is UNKNOWN | quoted verbatim — never reported as present or absent |

`Undetermined` is the one that used to be wrong in both directions: the old text asserted *"the
partially-created node is still present"* from a branch that may have been entered **because the read
failed** — the same distinction [Undetermined Is Not No](../UndeterminedIsNotNo) draws for a gate.

## What the lineage check does NOT guarantee

The `CreatedDate` comparison makes the rollback refuse to delete a node it did not create, and that
is worth having. It is not a transactional guarantee, and two limits are worth stating plainly rather
than discovering later (both raised in review on #4503, both **pre-existing and identical on the
singular path since #638** — neither is introduced by the batch rollback):

1. **The read and the delete are not atomic.** A concurrent create that replaces the row between the
   lineage check and `DeleteAndPublish` would have its replacement deleted. Closing it needs a
   conditional delete against a server-owned token in the `IStorageAdapter` contract — every backend —
   not a change in this caller.
2. **`CreatedDate` is caller-supplied when the caller supplies one.** Both create paths stamp
   `CreatedDate = n.CreatedDate == default ? now : n.CreatedDate`, so it is a lineage *hint*, not a
   server-owned identity token.

The window is narrow (the rollback follows the write by milliseconds, on a path that by construction
did not exist when the batch began), and the alternative — leaving the ghosts — is the defect this
page exists for. But "safe because it compares `CreatedDate`" should be read as *"it will not delete
a row it can see is not ours"*, never as *"it cannot delete someone else's row"*.

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

Related: [Copy Completeness](../CopyCompleteness) — the same question asked of a copy, where the answer
is deliberately different (it stops rather than rolling back, and says what it could not read).
