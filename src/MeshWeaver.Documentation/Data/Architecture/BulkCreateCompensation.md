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
