---
Name: Reading a Write Verdict
Category: Architecture
Description: A failed cross-hub write names the site that minted its verdict. How to tell an owner that never answered from an owner that answered with its own timeout — they look identical to a user and lead to opposite investigations.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# Reading a Write Verdict

A cross-hub write through `GetMeshNodeStream(path).Update(...)` fails closed: on silence past its
confirmation window it faults rather than optimistically succeeding
(see [MeshNodeStreamCache](/Doc/Architecture/MeshNodeStreamCache)). Every user-visible failure
therefore arrives as the same thing — a `MeshNodeStreamException` in a log line — and the
investigation it should trigger depends entirely on **which site minted the error inside it**.

Get that wrong and you spend the day on the wrong hub. This page is the decision procedure.

## The exception is a formatted verdict, not a description

`MeshNodeStreamException` renders its structured `MeshNodeError` verbatim:

```csharp
// src/MeshWeaver.Data.Contract/MeshNodeError.cs
public MeshNodeStreamException(MeshNodeError error)
    : base($"MeshNode {error.Code} at '{error.Path}': {error.Message}")
```

So a log line has the shape `MeshNode {Code} at '{Path}': {Message}`, and **`Code` alone separates
the two investigations.**

## The four shapes, and what each one means

| `Code` | Message shape | What actually happened | Where to look |
|---|---|---|---|
| `OwnerUnreachable` | `The owner of '…' returned no verdict for this update within Ns. The patch was posted and may still apply… Request trail: …` | The owner produced **no terminal at all**. The writer's late-response window expired. | The **owner's** activation: is it alive, mid-recycle, pinned dead? |
| `Unknown` | `{ExceptionTypeName}: {message}` — e.g. `TimeoutException: The operation has timed out.` | The owner **answered**, with a NACK carrying its own internal exception. | The owner's **ack chain**: which of its two internal bounds expired. |
| `AccessDenied` / `Deserialization` / `Validation` | `Access denied: …` / `Patch deserialization failed: …` / `Validation failed: …` | The owner answered with a classified application-level refusal. | The patch itself. |
| `OwnerDisposing` | `the owner's stream ended before this patch's commit echo arrived — … the write's fate is UNKNOWN; safe to retry …` | The owner **answered**: the stream its ack watcher was on completed before the echo — mirror eviction **while the hub lives**. The writer auto-retries it (a re-enqueue re-diffs, so a merge that did commit is a no-op). | The owner's **stream lifetime** (`ReclaimIfUnheld`, `SynchronizationStream.Dispose`), not its bounds. |
| `OwnerDisposing` | `owner activation disposing before the merge turn ran — the patch was NOT applied; safe to retry against the fresh activation` | The owner **answered from its ShutDown phase**: the patch was in flight when the owner hub tore down. This is the disposal NACK (`RegisterOwnerDisposingNack`), handed to the armed late watch directly; the writer's re-enqueue lands on the fresh activation. | The owner's **teardown** (`HubDisposalModel`): why did the activation go down under a live patch? |

Rows two and three come from one classifier on the **owner**:

```csharp
// src/MeshWeaver.Data/DataExtensions.cs — ClassifyPatchException
_ => (MeshNodeErrorCode.Unknown, ex.GetType().Name),
…
return new MeshNodeError(code, path, $"{prefix}: {ex.Message}", ex.StackTrace);
```

That default arm is the only site in `src/` that mints a `MeshNodeError` whose message is
`{TypeName}: {message}`. **A `MeshNode Unknown at '…': TimeoutException: …` line is therefore proof
that the owner answered** — the writer's own no-verdict path cannot produce it, because it always
stamps `OwnerUnreachable` and always writes a long sentence with a request trail.

> 🚨 **`TimeoutException` in the message does not mean the write timed out.** It means the *owner*
> hit one of its own bounds and told you so. The word is the same; the failing component is not.

## The owner's two internal ack bounds

When the owner accepts a `PatchDataRequest` for a `MeshNode`, it does not ack immediately. It
watches for its own commit echo and then flushes durably — two bounded steps, and **both surface as
`Unknown` + `TimeoutException`**:

```csharp
// src/MeshWeaver.Data/DataExtensions.cs
var postSub = ArmPatchAckWatcher(
    stream
        .Where(c => ChangeContainsStampedWrite(...))   // identity-based echo detection
        .Take(1)
        .Timeout(TimeSpan.FromSeconds(20)),            // ① commit echo
    committed => hub.ServiceProvider.GetService<IPostCommitFlush>()?.Flush(committed.Value!),
    TimeSpan.FromSeconds(10),                          // ② flush bound — a WAIT bound on the ack, not a fate
    AckOnce, d => hub.RegisterForDisposal(d), hubPath, () => hub.IsShuttingDown, patchAckLogger);
```

`ArmPatchAckWatcher` subscribes the echo with all three arms (see "The ack watcher's completion arm"
below). **Only bound ① can mint the timeout NACK.** Bound ② no longer produces a verdict at all —
see "The flush bound is not a verdict" below.

**Which one fired is readable from the elapsed time** between the user's action and the log line:

- **~20 s** and a NACK `Unknown` + `TimeoutException` → ① the commit echo never arrived. The merge turn
  did not reach the stream, or the reduced stream's fan-out is starved. The write's fate is genuinely
  unknown: read the node's `Version` before retrying anything that accumulates.
- **~10 s after the merge**, a SUCCESS, and an owner-side `FLUSH_OUTLIVED_BOUND` warning → ② the commit
  landed and `IPostCommitFlush` (i.e. `StoragePostCommitFlush` → `storage.WriteAndPublishUpdated`) had
  not finished in 10 s. The write applied; the storage behind that owner is slow. This is the signal
  to read when a bulk operation drags — never a reason to retry the write.
- **~31 s** → neither: this is the writer's `OwnerUnreachable` window, and the `Code` will say so.

## The flush bound is not a verdict (#3112)

Reaching the flush at all means the owner's reduced stream emitted the echo **containing this write**:
the merge landed, the `Version` advanced, every mirror is already receiving the new state. Whatever
happens to the durable flush afterwards decides *when* the ack is posted, never *whether* the write
applied. Until #3112 the code disagreed with that: `durable.Take(1).Timeout(10 s)` fed the fault arm, so
a flush still queued at the bound became a NACK `Unknown` + `TimeoutException` — read by the writer as
`LATE_NACK_TERMINAL … the write did NOT apply and is not auto-retryable` and by its caller as a failed
write — and the `Timeout` also **disposed** the flush, cancelling a storage write that had been queued
for the whole bound and handing the row to the persistence sampler, which queued it again. Under the
congestion that made the flush slow, every slow flush became two writes and one false failure.

**The measurement.** MeshWeaver.Manufacturing PR #48, run 33623113056, `test-repos / Compile + render
node repos`: the gate's disposable mesh installs 37 upstream packages and, per installed NodeType,
re-seeds the sealed bake's prebuilt assembly through one cross-hub `Update` on the type's node. For
`Radzen/Gallery` the trace reads `ADVANCE_WITHOUT_HANDOFF … bound=5000ms` at +5 s, then at
+10.02 s `LATE_NACK_TERMINAL … code=Unknown … msg=TimeoutException` — the owner **answered**, ten
seconds after the post, which is the flush bound measured from an echo that arrived within
milliseconds. The seed logged `seeding Radzen/Gallery … did not complete — the sweep compiles it
instead`, the sweep compiled the type over the adoption stamp that had in fact committed, and the
consumption postcondition reported `adopted 101 of 102 … DECLINED: Radzen/Gallery`. The control arm —
Reinsurance run 33623801785, same seal, same bytes, minutes later — adopted `Radzen.zip 1/1`. Same
store, same code; only the queue depth differed. In the same failing log, `portal/nodeops` held a
`CreateOrUpdateNodeRequest` for 60 s (`[STALE-CALLBACK] … AWAITING`), which is the depth of the queue
the flush was sitting in.

**The shape now.** On the bound the owner acks **Success** — the commit is what "saved" means (#2661),
so this is the truthful verdict — logs `[PatchAck] FLUSH_OUTLIVED_BOUND path=… bound=10000ms` on the
`MeshWeaver.Data.PatchAck` channel, and **leaves the flush running**. The flush's own terminal then
finds the once-only gate claimed: an emission is a no-op; a fault is logged as
`FLUSH_FAULTED_AFTER_ACK` and the sampler — whose claim `StoragePostCommitFlush` releases in `Finally`
— stays the writer of record for that version. A flush that faults **inside** the bound still NACKs
with its classified code: a storage refusal is a fact the caller should hear; slowness is not a
verdict. `PatchAckTotalityTest` pins all three paths on a virtual clock.

Under a genuinely slow store the writer therefore sees the ack arrive late — `LATE_ACK`, the caller
completes with success — and `ADVANCE_WITHOUT_HANDOFF` at 5 s still says the successor diffed against
the mirror. That warning is now the one to read for storage pressure; it is not a failed write.

## The ack watcher's completion arm (the gap closed by #3033)

`postSub` used to subscribe with **`onNext` and `onError` only**. If the filtered stream *completed
without emitting* — which `SynchronizationStream.Dispose` does by calling `Store.OnCompleted()`, and
which mirror eviction can do while the hub still lives — then `Take(1)` ended empty, the `Timeout` was
cancelled, **neither arm ran, and no ack was ever posted**. The writer then waited out its full
window and reported `OwnerUnreachable` for a write the owner may already have committed. This was the
exact defect fixed on the *writer* side by `RequireBaseState`
([Write Verdict Totality](../WriteVerdictTotality)), one hub over; `RegisterOwnerDisposingNack` covered
a *disposing hub*, not a stream that completes while the hub lives.

The watcher is now armed through one pure composition, `DataExtensions.ArmPatchAckWatcher`, built on
the `WhenCompletesEmpty` operator — "run this callback if the source completes without ever having
emitted" — so every path posts exactly one terminal:

| Path | Verdict |
|---|---|
| commit echo arrives, flush emits | `Success` — posted once, on the flush's emission |
| **echo stream ends without the echo, owner alive** | NACK `OwnerDisposing`, message `the owner's stream ended before this patch's commit echo arrived — … the write's fate is UNKNOWN; safe to retry …` |
| **echo stream ends without the echo, owner shutting down** (`hub.IsShuttingDown`) | **nothing from the watcher** — the ShutDown-phase disposal NACK (`RegisterOwnerDisposingNack`) is the verdict; see the third point below |
| flush ends without emitting | `Success` — `IPostCommitFlush.Flush` is contracted to complete empty for entity types it does not persist, so the in-memory commit is the durable state (the same verdict as when no hook is registered) |
| **flush outlives its 10 s bound** | `Success` on the bound, `FLUSH_OUTLIVED_BOUND` logged, the flush keeps running (#3112 — see "The flush bound is not a verdict") |
| the echo stream faults, or the flush faults inside its bound | NACK with `ClassifyPatchException`'s code (the echo bound's timeout stays `Unknown` + `TimeoutException`) |

Three things about that shape are load-bearing:

- 🚨 **The completion arm is guarded on "no emission was ever observed".** A bare
  `onCompleted => AckOnce(false)` would NACK every *successful* write: on the happy path `Take(1)`
  emits the echo and completes immediately, while the inner flush is still in flight, and `AckOnce`
  latches — the completion arm would win the race against the flush's later `AckOnce(true)`.
  `WhenCompletesEmpty` fires only for a completion that no emission preceded.
- **`OwnerDisposing`, not `OwnerUnreachable` and not a new code.** A stream ending under a live patch
  *is* the owner's stream going away, which is what the code means; it is the code the writer
  auto-retries (bounded — `MaxOwnerDisposingReenqueues`), and a re-enqueue re-runs the update lambda
  against the **fresh** state and re-diffs, so a merge that did commit before the stream ended becomes a
  no-op. "Fate unknown, safe to retry" is the honest verdict — and because the timeout verdict stays
  `Unknown`, the two are separable by `Code`.
- 🚨 **The completion arm stands aside when the OWNER is shutting down** (`hub.IsShuttingDown`, passed
  to `ArmPatchAckWatcher` as `ownerIsShuttingDown`). An owner hub's teardown completes the very same
  stream — its sync hub disposes in the `DisposeHostedHubs` phase and `Store.OnCompleted()`s — but that
  completion is not the eviction case, and answering it from the watcher broke both teardown-NACK tests
  on 2026-09-02 (`LateNackReenqueueTest`, `NackReachesTheWaiterDuringTeardownTest`). Two defects in one
  claim of the once-only ack gate: **one phase too early** — a hosted hub leaves its parent's registry
  only in its ShutDown phase ([Hub Disposal Model](../HubDisposalModel)), so a "safe to retry against
  the fresh activation" minted at DisposeHostedHubs sent the writer's immediate re-enqueue into the
  *dying* activation, which rejected it `ShuttingDown`, and the write failed `Unknown`; and **the wrong
  transport** — `hub.Post` from a hub past Quiescing is dropped under a whole-mesh teardown, and with
  the gate already claimed the disposal registrant's direct `ILatePatchVerdictSink.Dispatch` (the one
  route that still reaches the armed waiter) was skipped, so the caller burned its whole verdict budget
  in silence (#2778's shape again). The ShutDown-phase `RegisterOwnerDisposingNack` — registered on the
  same hub, total for a disposing hub — therefore owns the verdict for a patch in flight at owner
  teardown, and the watcher's arm fires only while the owner lives.

The generic deferred path (`ApplyJsonMergePatchAndUpdate`, non-MeshNode data hubs) had the same gap
twice over — its initial `stream.Take(1)` read had *neither* an error nor a completion arm, and its
`Skip(1).Take(1)` echo watcher had no completion arm — and is closed by the same two seams. An empty
initial read NACKs `OwnerDisposing` with `the patch was NOT applied` (there the write provably never
ran).

## Procedure

1. **Read the `Code`, not the prose.** `OwnerUnreachable` and `Unknown` lead to different hubs.
2. **Measure the elapsed time.** It names which bound expired.
3. **Read the node's `Version`.** If it advanced, the write landed and only the ack failed — do not
   retry a non-idempotent write.
4. **Only then look at the owner.** For `OwnerUnreachable`, ask whether the activation was alive at
   all; for `Unknown` + `TimeoutException`, ask what kept the commit echo from the owner's reduced
   stream for 20 s (the flush bound cannot produce this code any more — a slow flush is a
   `FLUSH_OUTLIVED_BOUND` warning beside a successful write).

## See also

- [MeshNodeStreamCache](/Doc/Architecture/MeshNodeStreamCache) — the write path, its serial queue,
  the late-response registry and the fail-closed verdict.
- [Durable But Unreadable](/Doc/Architecture/DurableButUnreadable) — the other way a write's durable
  state and its reported outcome disagree.
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — finding the exact broken edge
  once you know which hub to inspect.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the index can answer while
  the owning per-node hub cannot.
