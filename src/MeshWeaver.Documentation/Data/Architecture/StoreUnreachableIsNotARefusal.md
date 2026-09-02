---
Name: An Unreachable Store Is Not a Refusal
Category: Architecture
Description: One classification of "the database could not be REACHED", three layers that consume it — a bounded retry, a render frame, and a node operation's rejection reason. Reporting an availability failure as a verdict is how a create becomes a duplicate.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="9" cy="5" rx="7" ry="3"/><path d="M2 5v6c0 1.66 3.13 3 7 3 .69 0 1.35-.04 1.98-.12"/><path d="M2 11v6c0 1.66 3.13 3 7 3 .34 0 .67-.01 1-.03"/><path d="M18 13v4"/><path d="M18 21h.01"/><circle cx="18" cy="17" r="5.5"/></svg>
---

# An Unreachable Store Is Not a Refusal

**A store that could not be REACHED means the operation was never EVALUATED.** That is a different
outcome from "the operation was evaluated and refused", and the two demand opposite responses from
the caller:

| | Refused | Unavailable |
|---|---|---|
| Was it evaluated? | Yes | **No** |
| Was anything written? | No | No |
| Is a retry meaningful? | **No** — the same request refuses again | **Yes** — and it must reuse the *same* identifiers |
| Who should look at it? | The caller | The operator |

Collapsing the second into the first is not a cosmetic reporting bug. Where a caller mints its
target id **per attempt**, a false "refused" makes it give up on the id it was using and start over
with a new one — so the retry lands as a **duplicate** rather than as the same write. That is the
shape [#2229](https://github.com/Systemorph/MeshWeaver/issues/2229) is about, arriving through the
reporting layer instead of through a stale query.

## One rule, three consumers

There is exactly ONE definition of "the data store could not be reached", and it lives in
`MeshWeaver.Data.Contract` — the one assembly all three consumers can see:

```csharp
// src/MeshWeaver.Data.Contract/StorageFaults.cs
public static bool IsTransientConnectFault(Exception? ex)
```

It is typed on the BCL `System.Data.Common.DbException` surface rather than on `NpgsqlException`,
because the storage adapters live in provider packages that core cannot reference. Every ADO.NET
driver derives its faults from `DbException` and carries `DbException.SqlState`, which is enough.

| Layer | Asks the rule | To decide |
|---|---|---|
| Query fan-in (`TransientStorageFaults.RetryTransientConnect`) | is a bounded retry worth attempting? | whether to resubscribe the provider observable |
| Layout render (`AreaErrorClassifier.IsStorageUnavailable`) | what does an area SHOW once that retry is spent? | the `StorageUnavailable` area frame instead of "this area failed to render" |
| Node operations (`StoreReachability.IsStoreUnreachable`) | what does a create ANSWER? | `NodeCreationRejectionReason.Unavailable` instead of `Unknown` |

**Two copies of that rule would drift silently, in either direction** — a fault the fan-in retries
but a create reports as a defect, or an outage a create excuses that the fan-in never retried. Both
failures are invisible from outside. That is why `TransientStorageFaults.IsTransientConnectFault`
and `StoreReachability.IsStoreUnreachable` are both thin forwards, and why what stays local to each
layer is the *policy* (the retry budget, the frame copy, the rejection reason), never the
classification.

## The ladder: retry ONCE, then answer

```
provider observable
  └─ RetryTransientConnect ── 250 ms → 500 ms → 1000 ms ── the last error surfaces
       └─ the consumer's terminal arm ── CLASSIFY and ANSWER.  Never retry again.
```

🚨 **A fault that reaches a handler's terminal error arm is one whose retry budget is honestly
spent.** Retrying it there would aim a second, unbounded-in-aggregate retry at the resource that is
already the bottleneck — and a database unreachable for 21 seconds outlives 1.75 seconds of budget
no matter how many layers spend it. The correct answer to a spent budget is to report the condition
accurately, not to spend more of someone else's. See
[Error Propagation & Wedges](../ErrorPropagationAndWedges) for why the retry that "recovers from a
state that shouldn't happen" is the shape that takes portals down.

The same reasoning rules out the other two reflexes:

- **Do not downgrade the log.** An availability failure stays at `Error`, where an operator sees it.
  What changes is the *wording*: it names the store, not the operation.
- **Do not call it transient in a sense that promises recovery.** Nothing fires when a database
  becomes reachable again, so a caller told "this will resolve itself" waits forever. `Unavailable`
  says *retry is meaningful*, not *a retry will happen*.

## What the node operations answer

`NodeCreationRejectionReason.Unavailable` already existed for a different unreachable dependency —
a security-policy read that starved
([#1446](https://github.com/Systemorph/MeshWeaver/issues/1446)) — and its documented meaning is
exactly right here: *"The create was NOT evaluated … an availability failure, not a verdict."* The
2026-09-02 change is the **wire between the classification and that value**, on both create verbs:

```csharp
// MeshExtensions.HandleCreateNodeRequest — terminal onError arm
if (StoreReachability.IsStoreUnreachable(ex))        // ← condition, so it outranks the type tests
    Respond(CreateNodeResponse.Fail(
        StoreReachability.DescribeNotAttempted($"Node creation at '{node.Path}'"),
        NodeCreationRejectionReason.Unavailable));
else if (ex is InvalidOperationException) { /* ValidationFailed */ }
else if (CancellationClassifier.IsCooperativeCancellation(ex)) { /* Unavailable, #2152 */ }
else { /* Unknown — a genuine unexpected failure */ }
```

Three properties of that ordering are load-bearing:

- **The condition outranks the type.** An `InvalidOperationException` *wrapping* a driver connect
  fault is still the store being unreachable; answering it `ValidationFailed` would tell the caller
  their request was invalid because a database was down. `CancellationClassifier` makes the same
  argument in the other direction — a cancellation carrying a `TimeoutException` cause is a fault,
  not a cancellation, *despite* the type.
- **The bulk verb carries the same branch.** `CreateNodesRequest` is what every installer and
  static-repo import travels; a guard on one create verb and not the other is a guard on neither.
- **…but below its partial-landing branch.** "Nothing was written" is half of what this answer
  asserts, and it is FALSE once a batch has committed a window (Postgres writes each `WriteMany`
  window in its own transaction). A partially-landed batch keeps its own, louder report.

### The falsification boundary

The classification is only worth anything if it can say **no**. These are deliberately NOT matched
and keep their loud "unexpected failure" treatment:

| Fault | Why it is not an outage |
|---|---|
| `42P01` undefined_table, `23505` unique_violation, a syntax error | A defect. Telling an operator to wait for a table that is never coming back hides it. |
| `40001` / `40P01` serialization failure, deadlock | An in-query race. It belongs to the layer that owns the statement — the adapter's own retry — not to a fan-in or a rejection reason. |
| A bare `TimeoutException` with no `DbException` in the chain | A hub/request timeout, with its own policy (`AreaErrorClassifier.IsTransientHubFailure`). Double-classifying it would double-retry it. |

`CreateWhenTheStoreIsUnreachableTest` pins both directions: the connect timeout must answer
`Unavailable`, and `42P01` travelling the identical path must still answer `Unknown`. Without that
second case, "answer `Unavailable` for every exception" would pass every other assertion in the
file.

## 🚨 "Unreachable" does not mean the store was down

The classification above is about **what this process could reach**, and that is deliberately all it
claims. A connect timeout is *not* evidence that the database was unavailable — the identical
exception is produced when **this** process is the one that stalled, because the connect timeout is
wall-clock and does not pause for GC.

That is not a theoretical caveat. It is what actually happened in the incident that produced
[#3050](https://github.com/Systemorph/MeshWeaver/issues/3050) /
[#3051](https://github.com/Systemorph/MeshWeaver/issues/3051), whose bodies both concluded
"database-host unavailability … high confidence". Measured over the 3 h 5 min of the affected pod's
retained log:

| | |
|---|---|
| `Npgsql.NpgsqlException` lines | 329 |
| `.NET Runtime Platform stalled for …` reports | 2 378 |
| Npgsql exceptions emitted **within 100 ms after** a stall report | **294 / 329 = 89.4 %** |
| Share of the window those 100 ms windows cover | **1.77 %** |
| **Enrichment over chance** | **≈ 50×** |

The coverage figure is the control, and it is what makes this more than "both lines are logged when
the process resumes": if the database had genuinely been unreachable, its timeouts would be spread
across the other 98 % of the window. They are not. Only 11 of 329 (3.3 %) fall outside even a
5-second shadow of a stall.

The same pod was at 96.4 % of its managed-heap hard limit with 38-second thread-pool delays, and was
evicted from the Orleans cluster ~4 minutes later for exactly that reason — see
[Reading a Silo Eviction](../ReadingASiloEviction), which is the *same host condition* wearing a
different incident number.

**So the reading rule is:**

- The **answer** the fix above produces is correct either way — "this process could not reach the
  store, so the create was not attempted" is true whichever side stalled, and it is all the handler
  can honestly assert.
- The **investigation** the answer points to is not. Before opening a ticket against the database,
  check the calling process's own `.NET Runtime Platform stalled` / `Thread Pool is exhibiting
  delays` lines in the same window. The cheap discriminator is the adjacency above; the cheaper one
  is whether *other* clients of the same database timed out at the same moment.

## Known edge: the in-process exception mapping still collapses the two

`IMeshService.CreateNode` and `HubNodePersistence.CreateNode` turn a failed response into an
exception, and both map `Unavailable` and `Unknown` to the same `InvalidOperationException`:

```csharp
return Observable.Throw<MeshNode>(r.RejectionReason switch
{
    NodeCreationRejectionReason.ValidationFailed  => new UnauthorizedAccessException(…),
    NodeCreationRejectionReason.NodeAlreadyExists => new InvalidOperationException(…),
    _                                             => new InvalidOperationException(r.Error ?? …)
});
```

So an **in-process** caller distinguishes the two only by reading the message; a caller that reads
the response directly (MCP, the API, a test) gets `RejectionReason` and does not have to.

This is a genuine gap, and it is left open deliberately rather than patched here. There is no
node-operation "unavailable" exception type today, and `NodeUpdatePipeline` documents why the
obvious alternative is worse: `Unavailable` sits on the `InvalidOperationException` side of its fork
**on purpose**, because mapping it to `UnauthorizedAccessException` "would tell the caller they lack
a permission they may well hold". Introducing a third type is an API change with a call-site sweep
attached — a design, not a branch — and it should be taken together with the same question for the
delete, move and upsert verbs, whose terminal arms have not been swept for this condition at all.

## The trail

| | |
|---|---|
| [#2521](https://github.com/Systemorph/MeshWeaver/issues/2521) | A single timed-out connector open failed a whole layout render → the bounded retry in the query fan-in |
| [#2876](https://github.com/Systemorph/MeshWeaver/issues/2876) / [#3031](https://github.com/Systemorph/MeshWeaver/pull/3031) | What an area SHOWS when that retry is spent → the `StorageUnavailable` frame, and `StorageFaults` extracted as the one rule |
| [#3050](https://github.com/Systemorph/MeshWeaver/issues/3050) / [#3051](https://github.com/Systemorph/MeshWeaver/issues/3051) | What a CREATE answers when that retry is spent → `Unavailable`, on both create verbs |

All three were reported as separate incidents with different top frames, and all three are the same
sentence: *the store could not be reached, and the layer that found out said the wrong thing about
it.* When the next one arrives — and it will, from a verb not yet swept — the fix is another
consumer of the same rule, never another copy of it.

## See also

- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — why a stale negative on a
  may-not-exist path produces duplicate data, the other road to #2229
- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — surface errors to a real sink; never
  swallow, never retry a "shouldn't happen"
- [Debugging Postgres](../DebuggingPostgres) — when the store really is the problem
- [Unanchored Security Reads](../UnanchoredSecurityReads) — #3031's other half, and why the
  `Catalog` render died inside the call that enumerates schemas
