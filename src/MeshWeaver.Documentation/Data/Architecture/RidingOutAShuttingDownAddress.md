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
| `ADrainingSiloDoesNotSilenceItsSubscribersTest` (`MeshWeaver.Hosting.Orleans.Test`) | a grain on a stopping silo accepting a subscribe it can only answer into the void (#5256) |

## See also

- [Hub Disposal Model](../HubDisposalModel) — the phase machine, and when a hub stops being routable
- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — the wedge classes a mis-classified failure produces
- [MeshNode Stream Cache](../MeshNodeStreamCache) — the transient-fault breaker that must never cache this one
- [Debugging Message Flow](../DebuggingMessageFlow) — read this before re-running a timed-out test

## A re-ask's verdict is a verdict too (#3498)

The contract above has two arms in `JsonSynchronizationStream`, and until #3498 they disagreed. The
INITIAL `SubscribeRequest` treats every classification but `ShuttingDown` as terminal: the subscribers
are faulted with the error and the keep-alive (heartbeat + change-feed resubscribe) is disposed, so a
stream opened on an absent path stops at once and re-asks fresh only if the node later appears. The
RE-ASK arm — the one the change-feed latch and the recycle ride-out use — pushed a `ShuttingDown` back
through the re-arm carrier, and logged everything else as `resubscribe failed`, cleared its in-flight
flag, and did nothing more. No `OnError`, no teardown.

That is the shape CD run 7946 died on (`MeshPluginTest.FullCrudWorkflow_CreateGetUpdateDelete`,
*"never reported the node gone within 20s"*): the delete's own change-feed event latched a re-ask on
the reader's cached stream, routing answered the authoritative `NotFound` 0.1 s after the delete, the
arm logged it and dropped it, the heartbeat kept posting to the deleted owner every interval (the second
`NotFound` five seconds later is that heartbeat), and the reader waited out its whole budget for an
answer that was already in the log. It is #1029's silence on the door #1029 did not close, and it is
intermittent only because the owner's own teardown notice usually reaches the reader before the
change-feed event latches the re-ask.

## 🚨 The promise lives in the TEXT, not in the envelope (#4866)

Everything above assumes the rider can *tell* that it was handed the promise. It cannot do that from
`ErrorType`. Every classifier that decides "re-probe the fresh activation" against "take this answer
as final" reads the **message text**:

| Classifier | Where | Reads |
|---|---|---|
| `MeshNodeStreamCache.IsTransientOwnerFailure` | `MeshWeaver.Hosting` | `"is shutting down"`, `"Rejecting now"`, `"invalid activation"`, … |
| `AreaErrorClassifier.IsTransientHubFailure` / `IsHubRecycling` | `MeshWeaver.Layout` | the same markers |
| `OrleansRoutingService.ClassifyRoutedFailure` | `MeshWeaver.Connection.Orleans` | the same markers |
| `ShutdownNack.IsAnsweredByOwner` | `MeshWeaver.Messaging.Contract` | `ShutdownNack.Banner` — *"Hub X is shutting down"* |

`ShutdownNack.IsAnsweredByOwner` is deliberately **not** an `ErrorType` test, and its own doc comment
says why: the routing layer mints that same classification off the very same text, so an
`ErrorType` test answers the question with the routing layer's echo of it. The banner is the only
evidence that names the speaker.

So an owner-side refusal that is *stamped* `ErrorType.ShuttingDown` but *worded* without the banner
is a promise nobody can collect. The envelope is transient, every reader is terminal, and there is
nothing to grep: no compiler error, no exception, no log line — just a consumer that quietly stops
asking.

**That is what the disposal drain did.** The seam that answers deliveries still parked behind an
initialization gate when the hub goes down hand-wrote its sentence — *"Hub X **was disposed** while
GetDataRequest (id=…) was still deferred …"* — instead of composing it through `ShutdownNack`. It
carried no banner and matched no marker, so `IsAnsweredByOwner` reported *"the routing layer refused
you"* and every rider took a recycling hub's answer as final. Measured in #4866: one pod, one
sub-second window, **105** accepted-and-deferred `GetDataRequest`/`SubscribeRequest` deliveries
discarded when every hub bound to a `Store/Plugin` overlay self-healed at once — each one answered
with a sentence that told the sender to *"retry to get the authoritative answer"* in wording the
sender could not recognise as retryable. The recycle itself was correct; the answer to the work it
displaced was not.

The fix is the seam, not a new drain: `ShutdownNack.RetryForTheAuthoritativeAnswer` is documented for
exactly this case — *"work this hub ACCEPTED and can no longer finish — a queued turn that came too
late, machinery that can no longer be created, **a gate that can never open**"* — and it carries the
banner and the activation tag by construction. Every fact the old sentence had (the gates recorded at
deferral, who asked for the teardown, the reason they stated) is preserved inside its `what` clause.

**The general rule: an owner-side refusal is COMPOSED by `ShutdownNack`, never written at the call
site.** A hand-written one looks right in review — it names the address, it explains itself, it even
ends with the retry sentence — and it is invisible to every reader that has to act on it.

**And the guard over the producers is an enumeration, which is the same trap one level up.**
`OwnerAnswerRecognitionGuard` calls each producer and runs the real predicate over what it says, but
the producer *list* is written by hand: #3017 was filed because a fifth terminal was missing from
such a list, a sixth was missing at the same time, and the class's own guard passed throughout. This
drain was the seventh, and it stayed unlisted through both. Adding a refusal seam means adding it
there — and moving the floor in `TheProducerSetCoversEverySeam`, so the shrink is a decision rather
than an accident.

Pinned live by `DeferredDeliveryNackedOnDisposeTest.ADiscardedDeferredDelivery_IsRecognisedAsTheOwnersAnswer`,
which discards a real deferred delivery and requires the refusal the sender actually receives to be
recognised as the owner's, by `Address` and by path, with an activation tag on it — with a different
address as the negative control, and the gate name and stated reason as positive controls so a
rewording cannot trade one fact for another.

Since #3498 the re-ask arm mirrors the initial one: `ShuttingDown` is still the promise it rides out;
any other classification faults the stream's subscribers with the classified error and disposes the
keep-alive. A reader of a deleted node learns "gone" milliseconds after the delete, and a stream never
parks with no value and no error. `AReAsksNotFoundIsAVerdictTest` pins the sequence with the framework's
own parts: an initial subscribe refused by a real corpse (so the carrier re-asks), the re-ask answered
with routing's `NotFound`, and the fault required within the convergence budget — unfixed, nothing else
in that mesh can ever emit on the stream, so the test is red by construction rather than by chance.

## 🚨 A leaving HOST must refuse, not answer into the void (#5256)

Every rider above needs to be *told* something. On a pod that is being replaced, nothing was told.

From `ApplicationStopping` on, `OrleansRoutingService` refuses every **outbound** delivery from the
process (*"Host is shutting down, cannot route to …"*): once the silo leaves `Active` it can no longer
place the local `RoutingGrain`, and the stopping signal is the router's early warning of that. But the
silo's grains went on **accepting inbound** deliveries for the whole window. The silo is still `Active`
in membership while it lingers, so the directory keeps routing to its activations, and on a pod that
window is the termination grace period. An owner there received a `SubscribeRequest`, answered it with
a `SubscribeAck` and its first Full within a millisecond, and the router refused both. The subscriber
got no frame, no NACK and no `StreamEndedEvent`: the goodbye rides the same refused router. It waited
out its own budget. That is the *"no initial state arrived … within 30s … NO change item at all
reached the mirror"* of #5256 and #1114, and the 60 s request timeouts of #5230 and #2254. Every sample
fell inside a roll window.

The hypothesis first written on #5256 was that the owner deactivated between the ack and its first
frame and that the teardown announcement was suppressed. That is **not** what the repro shows. The owner
never deactivated, and its frame was produced; the frame just could not leave the process.
`ADrainingSiloDoesNotSilenceItsSubscribersTest` pins it. Silo B is stopped with `StopApplication` and
left lingering, and a cold subscriber on silo A subscribes to an owner B hosts. Before the fix the test
timed out at its 10 s bound, and the route trace showed the Ack and the Full answered `SHUTTING_DOWN`
at B's router. After the fix the answer arrives in about 50 ms, from a new activation on A.

**The fix is symmetry, on the one leg that still works.** A grain call's **result** travels back to
the *calling* silo's `RoutingGrain`, which NACKs the sender from a healthy process. So
`MessageHubGrain.DeliverMessage` now refuses on a leaving host (`meshHub.IsLeaving()`, the same
signal as the router's outbound gate). It returns a failed acknowledgement composed through
`ShutdownNack.RejectingNow`: the owner banner, this activation's tag and the reactivation promise. It
also hands the address off with `MigrateOnIdle` and a placement hint that names another active silo.
It does **not** use a plain `DeactivateOnIdle`. The leaving silo is still `Active` while it lingers,
and other silos' directory caches still point at it, so a deactivated address was re-placed locally
(`[PreferLocalPlacement]`). The first bulk run measured five fresh activations on the leaving silo in
five seconds, and they spent the subscriber's distinct-activation budget. A migration moves the
directory entry together with the activation. The sender rides the refusal out through the machinery
this page describes. The activation tag makes repeat refusals from the same grain count as **one**
teardown on the time axis, not as a recycle loop. No timer, no retry and no widened bound.

It stands aside while a **long-running operation** holds the activation (an activity, a round). That
operation may still take a one-way instruction such as a cancel, which is better applied than refused.
Cutting it short is the keep-alive policy's decision, not this gate's.

What this does **not** cover: a subscriber that was already **warm** when the silo began stopping still
gets no goodbye from the leaving host, because its `StreamEndedEvent` rides the refused router like any
other outbound message. It recovers on its next request to the address, which is now refused
honestly and handed off.
