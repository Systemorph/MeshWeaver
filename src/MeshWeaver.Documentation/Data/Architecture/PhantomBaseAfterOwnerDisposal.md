---
Name: The Phantom Base After Owner Disposal
Category: Architecture
Description: The owner echoes a merge to every mirror BEFORE it flushes it, so a teardown fault on the flush leg produces a "never applied" NACK whose re-attempt finds its own write already in the mirror — diffs to nothing, posts nothing, and reports success for a write that reached no store. Only the owner can tell that phantom from a merge that survived.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2a7 7 0 0 0-7 7v11l3-2 2 2 2-2 2 2 3-2V9a7 7 0 0 0-5-6.7"/><path d="M9 10h.01"/><path d="M15 10h.01"/></svg>
---

# The Phantom Base After Owner Disposal

**A mirror is not evidence that a write is durable.** This page is about the one place where the
write path treated it as if it were, and lost writes in silence for it — issue #3477.

It is the fourth member of a family this write path has closed one member at a time: #2661 (a bound
expiring is not a commit), #3001 (a base read that ends empty must not complete), #2543 (a bound
placed where it can never fire), #3550 / #3603 (a source that never terminates is not one that
completes). Each was a **fail-open**: a write reported as saved that was not. This is the last one
that decided from local state.

## The two facts that meet

### 1. On the owner, the echo precedes the durable flush

`MeshWeaver.Data/DataExtensions.cs` answers a `PatchDataRequest` in this order:

```
PATCH_MERGE_STAMPED v=N refused=0     ← the merge committed IN MEMORY
PATCH_ECHO_SEEN                       ← the change went out to every mirror
IPostCommitFlush.Flush(committed)     ← persist, THEN ack
```

That order is deliberate — readers see a commit as soon as it commits — and it means there is a real
window in which **every mirror of the node carries a value that no store holds**.

### 2. A teardown fault anywhere in that sequence is answered `OwnerDisposing`

`ClassifyPatchException` maps every owner-side teardown fault — a closed lifetime scope, a disposed
subject, a disposed CTS, `HubDisposingException` — to `MeshNodeErrorCode.OwnerDisposing`, which by
the enum's own contract means *"the patch never applied; safe to re-apply"*. That classification is
right, and #3499 deliberately widened it: the alternative (`Unknown`) is TERMINAL, and a false
terminal loses the write **every time**, whereas a false retryable was believed to cost only an
idempotent re-diff.

But the faults that widening routes here are, by construction, the ones raised **after**
`PATCH_ECHO_SEEN` — on the durability leg. So the population taking the `OwnerDisposing` re-enqueue
arm is precisely the population whose merge is already in the mirrors while storage is untouched.

## What the re-attempt then did

`MeshNodeStreamHandle.UpdateRemote`'s re-enqueue passes `refusedBaseVersion = 0` for
`OwnerDisposing` / `OwnerNotReady`, which reduces `RebaseSource` to `mirror.Take(1)`. Its comment
stated the intent exactly:

> the re-attempt re-runs the caller's `update` FUNCTION against whatever **the fresh activation
> serves** — pre-merge state, so the patch is recomputed and lands; or, if the commit did survive, an
> identical value, so the re-diff is an empty no-op. Both are the outcome the caller asked for.

The mirror is not the fresh activation. The re-attempt read **this hub's mirror**, found its own
value already there (the echo), computed an **empty** merge patch, posted **nothing**, and completed
the caller as a **success carrying the marker it had never persisted**.

Every log line on that path is `LogDebug`. Nothing is warned. Nothing is posted. The caller is told
the write is saved. Storage never changes.

## Why "the commit did survive" cannot be read off the mirror

The comment's disjunction has three cases, not two:

| what happened on the owner | mirror shows the write | storage holds it | empty re-diff is |
|---|---|---|---|
| merge never ran | no | no | — (a real diff is computed; the write lands) |
| merge committed **and flushed**, ack lost to teardown | yes | **yes** | **correct** — the write is durable |
| merge committed, **flush died with the hub** | yes | **no** | **a lost write reported as success** |

Rows 2 and 3 are indistinguishable from the mirror: both look like *"my value is already there"*.
**Only the owner can tell them apart**, because only the owner reads the store.

## The rule

> When the owner has just stated a write **never applied**, a mirror base that already **carries**
> that write is self-contradictory. It is not a no-op — it is an echo of state the owner does not
> have. Re-read the base from the owner, and diff against that.

`MeshNodeStreamHandle.ReattemptBaseSource` is that rule, at one seam:

- `ownerSaidNeverApplied` is set only by the `OwnerDisposing` / `OwnerNotReady` re-enqueues — the two
  codes whose contract is that no merge the owner kept ever happened. **`Conflict` is excluded**: it
  is the owner asserting it is provably PAST our base, so its mirror rebase (#1910) stays.
- The mirror base is used unchanged unless the caller's own lambda is a **no-op against it**. That is
  the ambiguity, and it is the only case that pays anything.
- In that case one authoritative `GetMeshNode` read is issued — routed to the OWNER, and its paced
  re-probe stands aside for a still-shutting-down activation, so what answers is the fresh one.
  Owner has the value ⇒ the no-op is real and the caller's success is earned. Owner does not ⇒ the
  diff is non-empty and the write lands.
- If that read cannot produce a base, the re-attempt **raises** — the `RequireBaseState` doctrine. It
  never resolves back onto the phantom it was called in to reject.

A first attempt, a `Conflict` re-attempt, and any "never applied" re-attempt whose mirror yields a
real diff are untouched: no extra read, no extra bound, no new failure mode.

## How this was established rather than guessed

The only sighting is `LateNackReenqueueTest.LateOwnerDisposingNack_AfterOptimisticEmit_ReenqueuesAndLands`,
1 of 330 on MeshWeaver.Plugins shard 3 (run `34059060596`). Its whole transcript after the NACK is:

```
[20:56:25.860] [Warning] … LATE_NACK_REENQUEUE … attempt=1 code=OwnerDisposing
[20:57:10.860] === TEST FAILED: The operation has timed out.
```

45.000 s of nothing, and the failing wait is the **durable-storage** poll, not the caller's verdict.

The cause follows by **elimination over the re-attempt's own terminals**, every one of which is
bounded below the 45 s the test waited:

| terminal | what it logs | inside 45 s? | storage |
|---|---|---|---|
| base-state wait expires (30 s) | `LATE_NACK_REENQUEUE failed` — Warning | yes | unchanged |
| base read ends empty | same Warning, at once | yes | unchanged |
| second NACK | `LATE_NACK_REENQUEUE` / `OWNER_NACK_REENQUEUE` — Warning | yes | unchanged |
| non-retryable NACK | `LATE_NACK_TERMINAL` — Warning | yes | unchanged |
| denial / delivery failure | `LATE_OWNER_DENIED` / `LATE_DELIVERY_FAILURE` — Warning | yes | unchanged |
| silence after the post | `VERDICT_TIMEOUT` (31 s) — Warning | yes | unchanged |
| early or late ACK | Debug | — | **written** |
| **empty diff — no post at all** | **Debug** | — | **unchanged** |

Exactly one terminal is both silent and leaves storage unchanged, and it is the one that requires the
mirror to already carry the marker. That is the reading; the code that produces it is above.

🚨 The corroboration is that `ClassifyPatchException`'s own remarks already predicted the shape
without naming the mechanism: *"Routing more faults here makes that class MORE likely, not less, and
it is silent when it happens."* The faults it routes are the post-echo ones.

## Reading the next occurrence

`PHANTOM_BASE` is the one line to grep, and it is a **Warning**:

```
[UpdateRemote] PHANTOM_BASE hub=… target=… attempt=1 corr=… — the owner NACKed this write as
NEVER APPLIED, yet this hub's mirror already carries it …
```

It fires only when a write was about to be lost, which is exactly when the information is worth its
cost. Paired with `corr=` (#3532) one grep yields the whole chain: the parent's NACK, the child's
registration, the phantom, and the authoritative re-read that resolved it.

## Verification

`ReattemptBaseIsAuthoritativeTest` pins the rule deterministically — both bases are seams, so no hub,
no cluster, no scheduler and no wall clock are involved:

- a phantom mirror + an owner that does **not** have the write ⇒ the owner's state is the base;
- a phantom mirror + an owner that **does** ⇒ the no-op stands (faulting here would report failure
  for a durable write);
- a mirror that is behind ⇒ the mirror is used and the authoritative read is **never subscribed**;
- an ordinary write ⇒ untouched, even when its lambda is a legitimate no-op;
- an authoritative read that cannot answer ⇒ raises, never falls back.

🚨 It was falsified by planting the pre-fix behaviour (`=> mirrorBase`, unconditionally) and watching
it go red. It is deliberately **not** verified by re-running `LateNackReenqueueTest` hoping to catch
the 1/330 — the race stays unreproduced, and a test that can only pass by luck proves nothing.

## Correlation-test timeout is a separate observation

Static review of core `e29d540a038f55d6f41f3cc298d236db8bb60654` on 2026-09-09
identifies a test-contract defect in `LateNackReenqueueCorrelationTest`. This does not establish
the cause of every failure recorded under #3477, including the original durable-storage timeout
and the later 90-second method timeout. No new test runs were used for this finding.

The test parks an owner merge until either its release flag is set or `owner.IsShuttingDown`
becomes true. Disposing the owner therefore releases accepted work, which may finish with an ACK.
`LateNackReenqueueTest` explicitly documents that ACK as the normal graceful-shutdown outcome;
a retryable NACK is only a safety net. The correlation test nevertheless waits unconditionally
for `LATE_NACK_REENQUEUE`. A successful durable write can therefore leave this log wait unsatisfied.

Its timing fence also does not establish a late response. `LatePatchResponseRegistry.ArmedCount`
is the dictionary entry count. `UpdateRemote` registers that entry before posting through
`Hub.Observe` and starting `UpdateResponseWaitBound`. Seeing an entry proves neither expiration
of that bound nor owner acceptance of the patch. An early retryable NACK takes the separate
`OWNER_NACK_REENQUEUE` branch, whose warning also cannot satisfy the test's regex.

The logger lifetime does not supply a demonstrated alternative explanation. `UpdateRemote`
captures the cache workspace logger on entry and emits the late-NACK warning immediately before
the recursive retry. Owner shutdown closes the owner's child scope, not the cache/root scope.
The test's capturing provider has a no-op `Dispose`, and its sink accepts the emitted category.
Persistence alone cannot prove that the late-NACK branch ran or that a warning was lost.

### Deterministic correlation regression

The corrected test constructs a late verdict rather than racing for a shutdown NACK:

1. Capture the specific patch request ID and correlation ID, and fence on the unique path's
   response-timeout transition rather than the global registry count. That path has only the
   original attempt until the controlled NACK is supplied.
2. Dispatch an explicit late `OwnerDisposing` verdict through the existing registry seam.
   `ArmedRequestIds` documents this purpose: construct a late verdict for an actual in-flight patch.
3. Assert that the resulting child attempt retains the correlation ID, has a distinct request ID,
   and is actually armed in the registry. The previous test only checked that the warning's
   correlation value was nonblank and had no spaces.
4. Release the parked merge in cleanup and verify caller termination and durable persistence with
   an idempotent update. Keep graceful-disposal coverage accepting either valid terminal verdict.

Before parking the merge, a test-local Debug sentinel through the actual cache logger factory
proves that this capture sees the diagnostics. The regression requires no production log-level
changes or increased timing bounds. Synthetic dispatch does not cancel the original accepted patch;
both pending assignments are idempotent, and the gate is released before awaiting completion.
This exercises correlation propagation through the late callback, not natural shutdown verdict
generation. The corrected correlation test and its unchanged disposal sibling both pass locally
under the two-processor CI shape, with Release warnings-as-errors builds.
As a negative control, temporarily replacing the late retry's inherited correlation with a newly
minted one makes the corrected test fail at the parent/child equality assertion in two seconds;
the control is reverted and is not part of the change.
The queue-owned delayed conflict retry fixed by #3818 is a different path from `UpdateRemote`'s
direct recursive late-NACK retry; its passing regressions do not close #3477.

## Related

- [Write Verdict Totality](../WriteVerdictTotality) — the sibling fail-open on the base read
- [Silent Completion](../SilentCompletion) — why an empty completion is invisible to every timeout
- [CQRS — Queries vs. Content Access](../CqrsAndContentAccess) — the general form: local replicas are
  not authoritative about what a store holds
