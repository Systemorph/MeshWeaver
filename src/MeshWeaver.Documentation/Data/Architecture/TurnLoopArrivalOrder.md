---
Name: Turn-Loop Arrival Order
Description: "A hub has two queues and a delivery moves between them at TURN time, so a turn can be in neither when the last initialization gate opens. Restoring the backlog cannot reach it, and it runs first. Why FIFO is the contract, how it broke twice, and the two invariants that hold it."
---

# Turn-loop arrival order — the two queues, and the delivery that is in neither

A message hub processes messages **in the order they arrived**. That is not a nicety; it is what
makes the actor model usable. The kernel relies on it (submission #1 *defines* `sharedValue`,
submission #2 *uses* it), and so does every handler that reads state a previous message wrote.

The guarantee is easy to state and was, twice, wrong in ways that no reading of the queue code
revealed — because **the delivery that broke it was in no queue at all**.

## The machinery, in one paragraph

`MessageService` holds two queues, both guarded by `turnGate`:

| queue | holds |
|---|---|
| `mainQueue` | the inbox — every delivery, in arrival order, waiting for its turn |
| `deferredQueue` | turns parked because an initialization gate was closed |

Exactly one turn runs at a time. `DrainLoop` dequeues from the head of `mainQueue`, subscribes to
the turn, and only takes the next one when that turn completes. `EnqueueTurn` appends.

🚨 **Deferral is decided at TURN time, not at arrival.** A delivery is enqueued to `mainQueue`
unconditionally; whether it defers is decided inside `NotifyAsync`, *after* the turn has been
dequeued, unpacked and routed. That single fact is the source of both defects below, and it is also
what makes the fix correct — because it means everything in `deferredQueue` is, by construction,
strictly **older** than everything still waiting in `mainQueue`. A message that has not been turned
yet cannot have arrived before one that has.

## Defect 1 — the restore went to the wrong end (MeshWeaver#3408)

`OpenGate` used to drain the backlog by appending:

```csharp
while (deferredQueue.Count > 0)
    mainQueue.Enqueue(deferredQueue.Dequeue());
```

under a comment promising the deferred turns run *"before any message that arrives after the gate
opens"*. That is only the easy half. A message that arrived **before** the open and was still
sitting un-turned in `mainQueue` — because the loop was busy — was already ahead of the deferred
turns being put behind it. The parked message ran **last**.

Observed on CI as **`B, C, A`**, where `A` was posted first and released first.

The fix rebuilds the queue as deferred-then-waiting, which is total arrival order across the two
queues. Pinned by `DeferredTurnsResumeAheadOfLaterArrivalsTest`.

## Defect 2 — the turn that is in neither queue (Plugins#1394, residual)

#3408 restores the order of everything that is **in a queue**. At the instant of the restore there
can be a delivery that is in neither: dequeued from `mainQueue`, and somewhere between its dequeue
and its gate check. No rebuild of the two queues can reach it. It is younger than every entry the
drain just restored, and it runs first.

The window is not narrow. Between the dequeue and the gate decision, `NotifyAsync` adds to the
routing path, computes `isOnTarget`, **deserializes the delivery** (`UnpackIfNecessary` — a
delivery that crossed a hub boundary arrives as `RawJson`) and calls hierarchical routing. On a
loaded runner that is milliseconds, and the gate open runs on a different thread.

That is why the symptom was load-sensitive and green in isolation, and why it survived a
805-test single-process run: what is needed is not load in general but *the turn loop being busy
across the gate open* — which a saturated CI shard produces and an idle machine does not.

### It presented as a different permutation every time

| permutation | what straddled the gate open | when |
|---|---|---|
| `A, C, B` | — (the #1145 routing hole, a different defect) | not observed since |
| `B, C, A` | — (defect 1, the append) | 2026-09-02 → 09-06, 6 occurrences |
| `C, A, B` | `C`'s turn | 2026-09-06 |
| `B, A, C` | `B`'s turn | 2026-09-07, 09-08 |

🚨 **The permutation names WHICH message straddled the window, not which defect is live.** Reading
it as a defect identifier is what sent three separate investigations to `RoutingServiceBase`, where
nothing was wrong. The discriminator is the number of distinct permutations, not their identity: a
single reorder produces one permutation; a *window* produces one per message that can be in it.

### Measured

Over 2026-09-01 → 2026-09-08, `ActivationBacklogFifoTest` failed **9 times in 965 runs in which it
demonstrably executed — 0.93%, about 1 in 107.** The permutation flips cleanly at ~2026-09-06T14:00Z
(when the #3408 fix reached the pin) and **the rate does not move**: 6/628 before, all `B, C, A`;
3/337 after, none of them `B, C, A`. #3408 changed which permutation you get, not how often.

## Why the fix is two changes

The gate decision has a **lock-free fast path** — `bool shouldDefer = !gates.IsEmpty;` — and takes
`gateStateLock` only when that reads "not empty". So a running turn escapes ordering in two
distinct ways, and they have different remedies.

### Window 1 — the fast path reads "empty" while the backlog is still parked

`OpenGate` removed the gate from `gates` and only then restored the backlog. Between those two
instants the hub advertises a state that contradicts its own queue: *no gate, so process
immediately* — while older messages are still parked. A turn whose fast-path read lands there skips
the lock entirely and there is nothing left to notice.

**Closed by ordering: the restore happens BEFORE the removal.** A turn's fast-path read then either
precedes the removal (so it blocks on `gateStateLock` and finds the queue already restored) or
follows it (so the queue is already restored). There is no third state.

This is safe because the defer *decision* and its `deferredQueue.Enqueue` are inside the **same**
`gateStateLock` that `OpenGate` holds throughout — so restoring first cannot strand a deferral that
lands during the window; no deferral can land during the window.

### Window 2 — the turn blocks on the lock, and the gate is open when it gets in

Now the turn has done everything right and still overtakes: it was dequeued while the gate was
closed, it waited on the lock, and by the time it is inside, the backlog has been restored to the
front of the queue it already left.

**Closed by an arrival-order barrier.** Every turn carries a monotonic **arrival sequence** issued
under `turnGate` at enqueue, and `mainQueue` is ordered by ascending sequence. Under FIFO dequeue
the head is *always* younger than the running turn — so a head that is **older** can only mean a
restore happened underneath it. When that is true the turn re-queues itself in place and returns,
and the drain runs the older work first.

```csharp
if (!bypassesGate && TryRequeueBehindOlderTurns(asReceived, cancellationToken, turnSeq, fate))
    return Observable.Return(asReceived.Forwarded());
```

Three details are load-bearing:

- **It re-enters with the delivery AS RECEIVED.** `NotifyAsync` early-returns on any state other
  than `Submitted`; re-queueing the routed/unpacked local would drop the message silently on its
  second turn.
- **It terminates.** Sequences are monotonic and no later turn can be issued a smaller one, so each
  re-queue is followed by at least one older turn running. Nothing else is draining while it
  executes — the drain flag is latched by the very turn that called it — so the queue it rebuilds
  cannot be consumed concurrently.
- **It exempts the gate bypasses.** `ShutdownRequest`, `DisposeRequest`, `DeliveryFailure`,
  `InitializeHubRequest`, `HeartBeatEvent` and awaited responses are exempt from gate ordering *by
  design* — deferring them deadlocks the hub — so ordering them behind a backlog would reintroduce
  exactly the teardown hang the bypass exists to prevent.

## The invariant to preserve

> **`mainQueue` is ordered by ascending arrival sequence, and `deferredQueue` holds only turns
> older than every entry in `mainQueue`.**

Every producer preserves it: `EnqueueTurn` appends the largest sequence ever issued; the `OpenGate`
restore concatenates deferred-then-waiting; the barrier inserts in place. Anything new that touches
either queue must too — and the cheap way to check is the barrier itself, which fires exactly when
the invariant is about to be violated.

## Reading a future occurrence

The per-delivery fate trail records which queue a delivery entered and how deep (added for this
investigation):

| stage | says |
|---|---|
| `QUEUED queue=main depth=N` | position in `mainQueue` at enqueue |
| `DEFERRED gates=[…]` + `QUEUED queue=deferred pos=P mainBehind=M` | it left `mainQueue`; `mainBehind` is what can now overtake it |
| `DEFERRED_DRAINED` + `QUEUED queue=main depth=N` | it came back, and whether those messages are still waiting |
| `GATE_DRAIN gate=… deferred=D behind=B` | the restore point |
| `REQUEUED_IN_ARRIVAL_ORDER seq=S behind=N` | **the barrier fired** — a turn was about to overtake `N` older turns |

🚨 A fate stage's **leading token is API**, not log text: stages render as `{stage}@{hub}` and two
suites wait on that literal substring. Append a new stage; never edit an existing token. Pinned by
`FateStageTokensAreAContractTest`.

## Tests

| test | pins |
|---|---|
| `DeferredTurnsResumeAheadOfLaterArrivalsTest` (core) | defect 1 — the restore goes to the FRONT |
| `GateOpenMustNotLetARunningTurnOvertakeTheBacklogTest` (core) | defect 2 — **both** windows; each half reverted alone reproduces `B, A` |
| `ActivationBacklogFifoTest` (MeshWeaver.Plugins) | the #1145 routing hole. It *detects* the above but cannot discriminate them — keep it, do not read its `because` string as naming a cause |

The core test forces the interleaving structurally rather than hoping for it: the turn loop is
strictly FIFO, a handler posting to its own hub enqueues behind its own turn, and `OpenGate` holds
`gateStateLock` across the whole restore. The gate opener runs on its own thread and is parked
inside that critical section through the service's own `ILogger` — the "decorate the real dependency
to control WHEN, never WHAT" technique, the same one `ActivationBacklogFifoTest` uses on
`IPathResolver`. No delay, no poll-for-time, no gate primitive.

## Related

- [Debugging Message Flow](../DebuggingMessageFlow) — the trace tags and the fate trail.
- [Action-Block Wedge Prevention](../ActionBlockWedgePrevention) — the other way a single turn loop
  goes wrong.
- [Actor Model](../ActorModel) — why one turn at a time is the whole design.
- [Asynchronous Calls](../AsynchronousCalls) — why nothing on the turn path may block or await.
