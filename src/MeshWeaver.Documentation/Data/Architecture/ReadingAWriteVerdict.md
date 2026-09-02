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

## The three shapes, and what each one means

| `Code` | Message shape | What actually happened | Where to look |
|---|---|---|---|
| `OwnerUnreachable` | `The owner of '…' returned no verdict for this update within Ns. The patch was posted and may still apply… Request trail: …` | The owner produced **no terminal at all**. The writer's late-response window expired. | The **owner's** activation: is it alive, mid-recycle, pinned dead? |
| `Unknown` | `{ExceptionTypeName}: {message}` — e.g. `TimeoutException: The operation has timed out.` | The owner **answered**, with a NACK carrying its own internal exception. | The owner's **ack chain**: which of its two internal bounds expired. |
| `AccessDenied` / `Deserialization` / `Validation` | `Access denied: …` / `Patch deserialization failed: …` / `Validation failed: …` | The owner answered with a classified application-level refusal. | The patch itself. |

The last three rows all come from one classifier on the **owner**:

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
var postSub = stream
    .Where(c => ChangeContainsStampedWrite(...))   // identity-based echo detection
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(20))             // ① commit echo
    .Subscribe(
        committed =>
        {
            var flush = hub.ServiceProvider.GetService<IPostCommitFlush>();
            if (flush is null) { AckOnce(true); return; }
            var flushSub = flush.Flush(committed.Value!)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(10))  // ② durable flush
                .Subscribe(_ => AckOnce(true),
                           ex => AckOnce(false, ClassifyPatchException(ex, hubPath)));
            hub.RegisterForDisposal(flushSub);
        },
        ex => AckOnce(false, ClassifyPatchException(ex, hubPath)));
```

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

## A known gap: the ack watcher has no completion arm

`postSub` above subscribes with **`onNext` and `onError` only**. If the filtered stream *completes
without emitting* — which `SynchronizationStream.Dispose` does by calling `Store.OnCompleted()`, and
which mirror eviction can do while the hub still lives — then `Take(1)` ends empty, the `Timeout` is
cancelled, **neither arm runs, and no ack is ever posted**. The writer then waits out its full
window and reports `OwnerUnreachable`.

This is the exact defect that was fixed on the *writer* side by giving a write with no base a
verdict (`RequireBaseState`), and it is still open on the owner side. `RegisterOwnerDisposingNack`
covers the case where the hub is disposing, but not a stream that completes while the hub lives.

The fix shape is a completion arm guarded on "the commit echo was never observed" — it must **not**
be a bare `AckOnce(false)` on completion, because `Take(1)` completes immediately after `onNext` on
the *successful* path, while the inner flush is still in flight; latching a failure there would NACK
writes that are about to succeed.

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
