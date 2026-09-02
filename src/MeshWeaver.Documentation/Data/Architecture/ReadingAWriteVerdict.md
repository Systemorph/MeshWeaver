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
    TimeSpan.FromSeconds(10),                          // ② durable flush
    AckOnce, d => hub.RegisterForDisposal(d), hubPath);
```

`ArmPatchAckWatcher` subscribes the echo with all three arms (see "The ack watcher's completion arm"
below); the two bounds are unchanged.

**Which one fired is readable from the elapsed time** between the user's action and the log line:

- **~20 s** → ① the commit echo never arrived. The merge turn did not reach the stream, or the
  reduced stream's fan-out is starved.
- **~25–30 s** → ② the commit landed and `IPostCommitFlush` (i.e. `StoragePostCommitFlush` →
  `storage.WriteAndPublishUpdated`, a Postgres write) did not finish in 10 s.
- **~31 s** → neither: this is the writer's `OwnerUnreachable` window, and the `Code` will say so.

🚨 **In case ② the write is already committed in memory when the NACK is sent.** The caller is told
the write failed, and the node's `Version` has nonetheless advanced. Check the version before
retrying anything that accumulates — an append, a counter, a `with { X = X + 1 }`. This is the
write-side twin of the read-side hazard in
[Durable But Unreadable](/Doc/Architecture/DurableButUnreadable): the durable state and the
reported outcome disagree, in opposite directions.

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
| either stream faults | NACK with `ClassifyPatchException`'s code (a timeout stays `Unknown` + `TimeoutException`) |

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
   all; for `Unknown` + `TimeoutException`, ask what made the commit echo or the storage flush slow.

## See also

- [MeshNodeStreamCache](/Doc/Architecture/MeshNodeStreamCache) — the write path, its serial queue,
  the late-response registry and the fail-closed verdict.
- [Durable But Unreadable](/Doc/Architecture/DurableButUnreadable) — the other way a write's durable
  state and its reported outcome disagree.
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — finding the exact broken edge
  once you know which hub to inspect.
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why the index can answer while
  the owning per-node hub cannot.
