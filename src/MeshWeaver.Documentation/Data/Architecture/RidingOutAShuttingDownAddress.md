---
Name: Riding Out a ShuttingDown Address
Category: Architecture
Description: ShuttingDown is the one transient NACK a long-lived consumer must ride out — and the two axes a ride-out has to bound separately, because collapsing them spends the whole budget inside one teardown window.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 3-6.7"/><path d="M3 4v5h5"/><circle cx="12" cy="12" r="2"/></svg>
---

# Riding Out a ShuttingDown Address

`ErrorType.ShuttingDown` is the only delivery failure in the mesh that is a **promise**. Every other
classification is a verdict about the target — `NotFound` says the address does not exist,
`Unauthorized` says you may not have it, `Failed` says the work broke. `ShuttingDown` says something
different, and its own message spells it out:

> *"the address may reactivate (recycle / restart). Rejecting now."*

A hub mints it while it is going away, at a moment when it genuinely **cannot know** whether the
address is gone for good (the node was deleted) or is about to come back (a recycle, a restart, a
redeploy). Handing the sender a terminal answer there would be a confident wrong answer, so the
sender is handed a transient one instead, and the contract that comes with it is: **consumers with
their own recovery machinery ride it out.** [Error Propagation & Wedges](../ErrorPropagationAndWedges)
covers what happens when they do not.

This page is about what "riding it out" actually costs to implement correctly — because the obvious
implementation has a failure mode that looks, from the outside, exactly like a slow read.

## The two riders

Two places in the platform ride out a `ShuttingDown` address, and they are the whole population:

| Rider | Where | Shape |
|---|---|---|
| The **point read** | `MeshNodeStreamExtensions.GetMeshNodeOutcome` | one immediate re-probe, then paced re-probes inside the caller's budget |
| The **sync stream** | `JsonSynchronizationStream`'s recycle re-arm latch | one re-ask per rejection, gated on the rejecting hub's teardown |

Everything else treats the classification as *information* rather than as something to recover from:
`MeshNodeStreamCache.IsTransientOwnerFailure` refuses to poison its negative cache with it,
`AreaErrorClassifier` renders a "coming back" state instead of an error, `PackageInstaller` retries
its install step. Those are one-line policies. The two riders above are the ones that must actually
*converge*, and they are where the design work is.

## Why a re-ask needs a JOIN, not a retry

The naive rider re-asks immediately. That does not work, and the reason is a fact about hub disposal
rather than about timing:

`MessageService` NACKs from `RunLevel >= DisposeHostedHubs` — a phase in which the dying hub is
**still registered** in its parent's `HostedHubsCollection`, because it removes itself later, in the
`ShutDown` phase. So routing resolves an immediate re-ask to the *same dying instance*, which NACKs
it identically and immediately. A bounded budget then burns end to end inside one teardown window
(measured: four rejections in 11 ms, MeshWeaver.Plugins run 31645120599) and the subscriber is
orphaned for good.

The cure is to **spend each attempt on a state that can answer** — join on the rejecting instance's
own `DisposalCompleted` before re-asking. That is not a retry, a backoff or a watchdog: nothing
polls, no timer runs, and the re-ask fires once, on an event that was always going to happen. See
[Hub Disposal Model](../HubDisposalModel) for the phase machine the join reads.

## 🚨 …and the join can be satisfied by a state that still cannot answer

Here is the part that cost issue #2986 an hour of "the read is slow".

`DisposalCompleted` is signalled **after** `RunLevel = Dead`. So an activation that has already
reached `Dead` answers that join *instantly* — and it can still be the instance routing hands the
delivery to. The join is then a no-op, the re-ask returns to the same corpse at memory speed, and a
budget sized for "three chances at a reactivated address" is spent in one millisecond.

The CI transcript (run 33523142249, `ImportTypeBeforeInstanceTest`) is unambiguous:

```text
outcome=Imported count=14 failed=0 blocked=[]
15:04:56.211 [Warning] Stream heCb5oZx…: resubscribe failed.
  DeliveryFailureException: Hub Tb666188a0/Inst is shutting down (RunLevel=Dead, activation #017DA86C) …
15:04:56.212 [Warning] Stream heCb5oZx…: resubscribe failed.   (activation #017DA86C)
15:04:56.212 [Warning] Stream heCb5oZx…: resubscribe failed.   (activation #017DA86C)
15:05:51.119 === TEST FAILED: The operation has timed out.
```

Three refusals, from **one** activation, inside **one millisecond**, and then nothing at all for the
remaining 55 seconds. The import had already succeeded; the recycle was the overlay/stale-assembly
self-heal doing exactly what it is supposed to do. The only thing wrong was the reader.

**Three chances that all fall inside one millisecond are one chance.**

## The two axes a ride-out must bound separately

The mistake underneath that transcript is that one counter was being asked to bound two different
things. They are genuinely different, and they need different bounds:

| Axis | Question it answers | What it must bound |
|---|---|---|
| **Activation** | "Is the address recycling in a *loop*?" | how many DISTINCT activations may refuse us before we stop |
| **Time** | "Is this *one* teardown still draining?" | how long we ride out ONE activation, and how fast we re-ask it |

A rejection from a **new** activation is a new recycle — a succession of those is the degenerate
loop a budget exists to stop, and each one costs a unit. A rejection from the activation that
**already** refused us is not a new recycle at all; it is the same teardown, still in progress,
which the join failed to wait through. Charging it against the activation budget is the bug.

The `ShuttingDown` NACK carries the activation identity for exactly this discrimination — see
[Naming the Recycling Shape](#naming-the-recycling-shape) below — so both riders can tell the two
apart from the message they already receive.

### What the sync stream does now

```text
rejection arrives
  ├─ activation differs from the last one (or is unknown)
  │     → charge the ACTIVATION budget (MaxRecycleReArms = 3)
  │     → join on the rejecting instance's DisposalCompleted, then re-ask
  └─ activation is the SAME one that refused us last time
        → charge the TIME budget (MaxSameActivationReAsks = 16)
        → REST first (SyncStreamOptions.RecycleReAskPace, 500 ms), then re-ask
```

`16 × 500 ms` is sized to reach `MessageHub.DisposalWatchdogTimeout` (8 s): at that point a wedged
teardown is force-torn-down and the address is gone, so a re-ask that *still* meets the same
activation is meeting something no amount of further waiting can rescue.

Nothing here is a watchdog or a poll. No timer exists unless a real rejection arrived; exactly one
re-ask is ever outstanding (`Resubscribe`'s in-flight guard plus the `Concat` on the carrier); the
whole ride-out is bounded on both axes; and it stops the instant the owner answers or its activation
changes — a successful re-ask resets both counters, because an answer is proof this was never the
degenerate loop.

### Defer the probe, don't project it

One more trap in the same few lines. The carrier is

```csharp
rejectedByRecycle
    .Select(ChargeReArmBudget)                 // null ⇒ a budget is spent; stop
    .Where(decision => decision is not null)
    .Select(decision => OwnerReadyForReAsk(decision!).Select(_ => decision!.Rejection.Reason))
    .Concat()
```

`Select` projects **eagerly**; `Concat` only defers *subscription*. Without an `Observable.Defer`
inside `OwnerReadyForReAsk`, every rejection in a burst takes its "is the owner still disposing?"
snapshot at **arrival** time, and the `Concat` then replays those stale snapshots one at a time. The
join has to read the world when its attempt is about to run, not when its rejection landed.

For the same reason the *verdict* travels with the attempt (`ReArmDecision`) instead of being
re-derived inside the join: the counters keep moving while an attempt waits its turn in the `Concat`,
so an attempt that must act on the state that charged **it** cannot go looking at whatever the newest
rejection left behind.

## Naming the recycling shape

Every `ShuttingDown` NACK embeds a stable per-activation token:

```text
Hub {address} is shutting down (RunLevel={runLevel}, activation #017DA86C) — cannot process {type};
the address may reactivate (recycle / restart). Rejecting now.
```

The token is `RuntimeHelpers.GetHashCode(hub)` — stable for one activation's lifetime, different
across activations. It exists because a probe **count** cannot tell the two failures apart, and they
have opposite fixes:

- **One owner, many probes** → one hub is wedged in teardown; the address never reactivates. Look at
  *that hub's disposal*.
- **Many owners, many probes** → a recycle storm; each successor dies before it can answer. Look at
  *whatever is asking for the recycles*.

`MeshNodeStreamExtensions.RecyclingShape(distinctOwners)` renders that sentence for the point-read
rider, and `AddressRecyclingException` carries it to the caller.

Minting and parsing both live in **`ShutdownNack`** (`MeshWeaver.Messaging.Contract`) — one marker,
one formatter, one parser, in the assembly both riders reference. That consolidation is not tidiness:
the minting sites had already drifted once (#2376 review found one NACK with no identity at all and
another pairing the tag with a per-*delivery* id that changes on every retry against the same
activation), and each drift defeats the counter in a different direction.

## Rules for a new rider

If you write code that must survive a `ShuttingDown` answer:

1. **Never treat it as terminal.** Do not `OnError` a long-lived stream on it. The sync stream keeps
   the stream, its keep-alive and its resubscribe latch ALIVE on this classification — erroring there
   killed the latch and wedged every read of a mid-recycle NodeType (CI 30003419841).
2. **Never re-ask immediately without a join.** The dying hub is still routable while it NACKs.
3. **Never treat `DisposalCompleted` as "the address can answer again."** It is signalled after
   `RunLevel = Dead`, and it does not, on its own, mean the corpse has stopped being resolved.
4. **Bound both axes, separately.** Distinct activations on one counter; consecutive re-asks at one
   activation on another, paced.
5. **Say so when you give up.** A rider that stops trying in silence turns a refused read into a
   timeout, and a timeout points the next engineer at the wrong system entirely. Both give-up paths
   log one Warning naming the owner, the activation and which bound was hit.

## Where this is pinned

| Test | What it fails on |
|---|---|
| `RecycleReAskRidesOutTheDyingActivationTest` (`MeshWeaver.Data.Test`) | a read whose owner is at `RunLevel=Dead` and still routable never converges after the address reactivates |
| `ShutdownFailureRideOutTest` (`MeshWeaver.Data.Test`) | the sync stream faulting on `ShuttingDown` — or swallowing any OTHER failure kind |
| `SubscribeDuringRecycleTest` (`MeshWeaver.Layout.Test`) | a layout area subscribing into the recycle window rendering an error instead of a "coming back" state |
| `RecyclingShapeDiagnosticTest` (`MeshWeaver.Graph.Test`) | the diagnostic naming both shapes at once, or inventing one from zero observations |
| `ChangeFeedResubscribeCoalesceTest` (`MeshWeaver.Data.Test`) | a burst of owner change events producing one resubscribe per event |

## See also

- [Hub Disposal Model](../HubDisposalModel) — the phase machine, and when a hub stops being routable
- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — the wedge classes a mis-classified failure produces
- [MeshNode Stream Cache](../MeshNodeStreamCache) — the transient-fault breaker that must never cache this one
- [Debugging Message Flow](../DebuggingMessageFlow) — read this before re-running a timed-out test
