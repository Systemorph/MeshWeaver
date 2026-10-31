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
| The **cross-hub write** | `MeshNodeStreamHandle.UpdateRemote` | the same bounded never-applied re-enqueue as `OwnerDisposing`, gated on the rejecting hub's teardown (see *Node writes*, below) |

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
rejection arrives (a ShuttingDown NACK, or the owner's StreamEndedEvent)
  ├─ NO activation tag AND no owner banner — the ROUTING LAYER refused, not the owner (#6729)
  │     → charge the TIME budget under the "(route)" key (MaxSameActivationReAsks = 16)
  │     → REST first (RecycleReAskPace), then re-ask
  ├─ activation not yet seen since the last answer (or unknown, from the owner)
  │     → charge the ACTIVATION budget (MaxRecycleReArms = 3)
  │     → join on the rejecting instance's DisposalCompleted, then re-ask
  ├─ activation already refused us since the last answer
  │     → charge the TIME budget (MaxSameActivationReAsks = 16)
  │     → REST first (SyncStreamOptions.RecycleReAskPace, 500 ms), then re-ask
  └─ a budget is spent
        → GIVE UP: one Warning, and the stream ENDS with SyncStreamOwnerRefusedException
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
5. **Say so when you give up — to the log AND to the reader.** A rider that stops trying in silence
   turns a refused read into a timeout, and a timeout points the next engineer at the wrong system
   entirely. Both give-up paths log one Warning naming the owner, the activation and which bound was
   hit, and END the stream (see [A give-up ends the stream](#a-give-up-ends-the-stream-6602)). Rule 1
   is about ONE `ShuttingDown` answer; a spent budget is a different fact.
6. **A re-ask can run NESTED inside the ask it replaces — install its slot before you subscribe.**
   `hub.Observe` posts at call time and answers through an `AsyncSubject`, so a NACK dispatched before
   the `Subscribe` call returns is replayed SYNCHRONOUSLY into the error arm, which re-asks right
   there. Writing `slot.Disposable = Observe(…).Subscribe(…)` then completes the OUTER assignment
   after the nested re-ask installed itself, and a `SerialDisposable` disposes the re-ask: its
   response subject is removed, the owner's answer is dropped as "No subject found for response",
   and the read spends its whole budget reporting "still recycling after 1 probe(s)" (#7395). Give
   each attempt its own `SingleAssignmentDisposable`, put it in the serial slot FIRST, then subscribe
   into it — a late assignment into a slot a re-ask has already replaced disposes the finished
   attempt, never its successor.
7. **Count the asks you ISSUED, not only the refusals you received, and say which ending it was.**
   A budget can end two ways after a `ShuttingDown`: every ask was refused (a recycler outlasted the
   budget), or the LAST ask was never answered at all, neither refused nor served. They point at
   different places, and reporting the second as the first is a false diagnosis. After #7418 rolled,
   memex-cloud still logged "still recycling (ShuttingDown) after 1 probe(s) over 15.0s": one refusal
   at +0.7 s, then a re-probe outstanding for 14 s. `GetMeshNodeOutcome` now reports that ending as
   "answered ShuttingDown N time(s), and then the re-probe issued at T was NEVER ANSWERED" with the
   probes issued, and attaches the reader and target diagnostics the plain timeout prints, so the
   reading can tell the dying activation from a successor still activating from a lost reply (#7395).
   The target half looks the owner up on the MESH hub, where every per-node hub is hosted, and
   prints its run level, queue, `IsShuttingDown` and activation tag. Looked up on the caller's own
   hub (a sibling, such as `portal/reads-{meshId}`), the owner was always reported as "NO LOCAL HUB",
   even when its activation was on the same pod. So the reading of the next occurrence is:
   - the owner is `IsShuttingDown=True` with the refusing activation's tag: the dying activation
     accepted the re-probe and has not answered it;
   - a `Starting` owner with a new tag: a successor is still initializing, and the re-probe may
     be parked behind its init gates (the `deferred` count says whether anything is);
   - a `Started` owner with a new tag: a successor is up and running, so slow activation does NOT
     explain the silence. The re-probe was either never delivered to it or answered and lost; a
     new tag alone does not prove the successor holds it, so read its queue and pending callbacks;
   - "NO LOCAL HUB": nothing on this pod holds the address, so the re-probe is with another silo
     or Orleans.

## A give-up ends the stream (#6602)

Until #6602 the sync stream's give-up disarmed the re-ask and left the stream **alive on its last
value**, "the pre-existing orphaned-stream behaviour". The Warning was the only trace. Every reader
held a frozen snapshot that rendered exactly like a live one, with no error and no completion, so its
own recovery never ran. On memex-cloud during rolls this hit `Hosting/PlatformBuilds`,
`Hosting/Modules` and `Ops/Status/*`, and it is the candidate cause for the fleet watch reading a
stale `Ops/Status/build` while the store held fresh rows (#5011).

Now the give-up ends the stream the way an owner **verdict** does: `OnError` with the typed
`SyncStreamOwnerRefusedException` (it names the stream and the owner), and the keep-alive stops. The
stream itself still does not retry. The reader decides when to read again, on its own signal:

- `MeshNodeStreamCache` marks the entry faulted and evicts it, so the next read opens a fresh
  subscription. `IsTransientOwnerFailure` classifies the type as transient, so the negative cache stays clean.
- A layout area classifies it as transient (`AreaErrorClassifier.IsTransientHubFailure`) and
  re-subscribes, bounded by `RetryAreaWithBackoff`.
- The fleet watch (`FleetWatchHeartbeat.HeldReadings`, MeshWeaver.Plugins) reports the read as lost
  and opens it again on the node's next mention by its listing: a write (`Updated`) or a fresh frame.
  It does not re-open while the read is live, and it does not use a timer.

### Why a roll spent the budget

The activation budget counts distinct activations since the last answered re-ask. Two carriers made one
teardown count as more than one:

- `StreamEndedEvent` carried no activation identity, so every announcement was charged as a NEW
  activation, and it also broke the same-activation chain the NACKs had built. It now carries
  `Activation` (`ShutdownNack.ActivationToken(hub)`), stamped by all three producers: the recycle
  announcement, the owner's end of a served stream, and the heartbeat answer for a stream the
  activation does not serve.
- `MessageHubGrain.RefuseBecauseTheHostIsLeaving` tagged its NACK with the GRAIN's identity, while
  the hub's own NACKs tag the HUB's. So one activation on a leaving silo had two tags. It now tags
  the hub when there is one.

The budget also counts against the **set** of activations seen since the last answer, not only "the
previous one": a NACK and an announcement from one teardown can interleave with another carrier's,
and A, B, A is two activations.

Pinned by `ASyncStreamThatGivesUpOnItsOwnerEndsTest` (MeshWeaver.Data.Test). Its negative controls
were run by hand. With the terminal removed, the distinct-activations case never sees the stream end.
With the announcement's activation ignored, the one-activation case gives up on a teardown that was
only settling.

### A routing refusal is not an owner activation (#6729)

Measured on memex-cloud on 2026-10-13: pods `…-m2jn9` (19:52:18Z) and `…-54lxb` (20:01:57Z) each
logged `Drain: GIVING UP … 5 circuit(s) are STILL OPEN` and `Application is shutting down...`, and in
that same second every open circuit's sync streams ended with *"4 DISTINCT owner activations have
refused this stream (budget 3)"* — for owners that had nothing to do with one another (a user home,
`Admin`, threads, a payment record). Some twenty incidents were filed against "an owner recycling in a
loop". No owner had recycled. The subscriber's OWN router answered every re-ask *"Host is shutting
down, cannot route to …"* (`OrleansRoutingService.DeliverMessage`), a `ShuttingDown` NACK with no
activation tag. Untagged meant "a distinct activation", there was no local teardown to join, so the
re-ask went out at once, and four router refusals inside a millisecond spent the activation budget.

The latch now asks who spoke, with the predicate that already exists for that question,
`ShutdownNack.IsAnsweredByOwner`: a refusal with neither an activation tag nor the owner's banner came
from the routing layer — this host stopping, the silo directory in flux during a roll, a pod hub not
yet claimed. That is ONE condition, the route being unavailable, so it is charged on the TIME axis
under its own `(route)` key, paced like a teardown ride-out, and its give-up names the ROUTE and says
no owner recycle loop is implied. An untagged refusal that does carry the owner's banner keeps the old
accounting (one distinct activation each), because the owner did speak.

Pinned by three cases in `ASyncStreamThatGivesUpOnItsOwnerEndsTest`: six router refusals are ridden
out and the stream recovers (red before the fix: it ended on the fourth); a route that never serves
again still ends the stream, naming the route; and untagged OWNER refusals still give up on the
activation budget (the control that the discrimination is by speaker, not by tag alone).

**The same give-up came back after that fix, and that time it was right.** On 2026-10-15 at 00:49Z the
pods running the fix logged "4 DISTINCT owner activations" again, for many unrelated owners on both
pods. This time the owner HAD spoken. Each refusal carried the owner's banner and a different
activation tag, and named its recycler: a module live swap's recycle wave, plus a `RecycleCascade` from
every NodeType definition the wave had disposed. Each of them tore down the successor activation that
the previous one had made room for. The latch counted correctly, and the defect was the recycler's. It
is fixed there: one landing is one wave, and the wave does not cascade. See
[Live Module Update](../LiveModuleUpdate) → *One landing, one recycle wave*.

## One request type, several producers: `GetDataRequest`

`[ReaskedOnShutdown]` makes a claim about every producer of a type. `GetDataRequest` cannot carry it
yet, because its producers split:

| Producer | Reference | On a `ShuttingDown` answer |
|---|---|---|
| `GetMeshNodeOutcome` (the point read) | `MeshNodeReference` | re-asks at once, then on the pace |
| `HubStreamProviderFactory.Create` | `ContentCollectionReference` | faults the provider |
| `ContentStaging`, `ContentFileResolver` | `ContentCollectionReference` | takes it as the answer |
| `MeshOperations` (`get`, collection reads) | any | takes it as the answer |

So a bring-up discard of a `GetDataRequest` stays an Error (`MessageService.Dispose`, event 7301).
The sender **is** answered: the drain posts a `ShutdownNack.RetryForTheAuthoritativeAnswer` to every
deferred delivery that is not self-addressed or declared unanswerable. Whether that answer loses
the read depends on which producer sent it.

The type name cannot tell the producers apart. The Error therefore carries the request's
**declared** description, bounded to 300 characters (`Request: GetDataRequest { <reference> }`),
and the reference names the producer. A message opts in by implementing `IDescribedForDiagnostics`
(explicitly, so the wire shape does not change); every other message is named by its type alone.
The line never prints a message's own `ToString()`: a record renders every member, so a discarded
token-validation request would put its raw token in the log. Measured on one deployment, 2026-10-11 19:16Z (#5718): about 200 such Errors in one module
live-update, each a partition root discarding a `GetDataRequest` from one of its own NodeType hubs.
None of those lines could name the read.

Once every producer rides the answer out, mark the type `[ReaskedOnShutdown]` and the line moves to
Debug by the rule above. Producers in other repositories and in mesh-compiled code count too.

**Until then, a producer that rides the answer out says so on its own request.**
`PostOptions.ReaskingOnShutdown()` stamps the delivery property `ReasksOnShutdown`. This is the
per-delivery form of the attribute: one producer's claim about one request, never a claim about the
type. `MessageService.Dispose` reads either form, and a discard of a delivery that carries the
property is teardown-normal (Debug), the same as a marked type. The NACK is unchanged, and the
producer's re-ask runs on it. `GetMeshNodeOutcome` stamps it on its point read. The other producers
in the table do not, so their discards stay Errors. With that, the bring-up discard Errors that kept
reopening #5718, partition roots recycled by a live module update while a NodeType hub's point read
(`GetDataRequest { MeshNodeReference }`) was parked, move to Debug. Stamp it only from code that
really re-asks: a stamped producer that takes the NACK as final would silence a real loss.

**A reply never inherits the stamp.** `PostOptions.ResponseFor` and `WithRequestIdFrom` copy the
request's property bag onto the reply, and they drop `ReasksOnShutdown` while doing it (#6717). The
stamp is one producer's claim about its own request. A reply discarded by a teardown has no producer
that asks again, so it keeps the level its own type and answer policy give it.

## Node writes: `PatchDataRequest`

A node write has one producer: `UpdateRemote`, behind `GetMeshNodeStream(path).Update(...)`. It
rides a `ShuttingDown` answer out on both arms. On the early arm it logs
`OWNER_SHUTTING_DOWN_REENQUEUE`, and on the late watch it logs `LATE_SHUTTING_DOWN_REENQUEUE`. Each
re-runs the caller's original update function against the address's next activation, up to
`MaxOwnerDisposingReenqueues` attempts. When the budget is spent, the caller gets a failure, never a
silent success. A write parked behind a young activation's gates is therefore **not lost** when that
activation is torn down mid-bring-up. The discard answers it `ShuttingDown`, the write is re-asked,
and it lands on the next activation.

### The write's re-ask joins the teardown too (#7326)

Until #7326 the write rider broke rule 2: it re-asked AT ONCE. A re-attempt diffs against the same
leased mirror as the attempt it replaces, and that mirror already has state, so the re-attempt posts
immediately. The refusing owner is still registered under its address until its own teardown
removes it, so routing handed every re-ask straight back to it. Measured with
`AWriteRefusedByARecyclingOwner_LandsOnTheNextActivation`, before the fix: three refusals from one
activation inside 5 ms, then a terminal `MeshNode Unknown … is shutting down`. That is the
retype-then-repair upsert of #4484 and #7326: the retype makes the rebind watcher recycle the owner,
and a repair whose mirror was answered just before that recycle meets an owner that is already
refusing.

Both re-enqueue arms (`OWNER_SHUTTING_DOWN_REENQUEUE`, `LATE_SHUTTING_DOWN_REENQUEUE`) now start the
re-attempt only once the refusing LOCAL instance has finished its teardown
(`LocalTeardownJoin.WhenLocalTeardownSettled`, the same lookup the sync stream's join uses). With no
local instance to wait for (the owner is on another silo, or it is already gone), the re-attempt
starts at once, as before. The join has no timer of its own. The write's late-verdict window stays
the bound, and the write CARRIES it across the join (`LocalTeardownJoin.ReaskOnceSettled`). The
disposal watchdog names a wedged teardown and tears nothing down around it, so `DisposalCompleted`
may never fire. Without the carried bound, a write refused by such an owner stayed pending for as
long as the stall lasted: the early refusal never armed the verdict timer, and the late dispatch had
already removed the registry entry that timer claims. When the window passes, the write ends with
`OwnerUnreachable` (`TEARDOWN_JOIN_TIMEOUT` in the log), is not re-sent to the owner that may still
be registered, and releases its queue hand-off. The re-attempt is subscribed under the writer's
AccessContext, because after the join it runs on whichever thread completed the owner's disposal.

The producer did not say so, though. The patch carried neither the attribute nor the property, so
that routine roll-time discard was reported at Error as stranded work. One measured case was #6733:
an Orleans deactivation (`DirectoryFailure`, the directory owner's silo stopping) took down an
`Admin/Threads` activation while a cache client's patch was parked behind
`[DataContextInit,MeshNodeInit]`. `UpdateRemote` now stamps `ReaskingOnShutdown()` on its patch, so
the discard reports at Debug. It does not stamp the last attempt: once the re-enqueue budget is
spent, a `ShuttingDown` answer fails the caller, so that discard stays an Error. The type stays
unmarked, because producers outside this repository may post it too.

## Where this is pinned

| Test | What it fails on |
|---|---|
| `RecycleReAskRidesOutTheDyingActivationTest` (`MeshWeaver.Data.Test`) | a read whose owner is at `RunLevel=Dead` and still routable never converges after the address reactivates |
| `ShutdownFailureRideOutTest` (`MeshWeaver.Data.Test`) | the sync stream faulting on `ShuttingDown` — or swallowing any OTHER failure kind |
| `SubscribeDuringRecycleTest` (`MeshWeaver.Layout.Test`) | a layout area subscribing into the recycle window rendering an error instead of a "coming back" state |
| `RecyclingShapeDiagnosticTest` (`MeshWeaver.Graph.Test`) | the diagnostic naming both shapes at once, or inventing one from zero observations |
| `ChangeFeedResubscribeCoalesceTest` (`MeshWeaver.Data.Test`) | a burst of owner change events producing one resubscribe per event |
| `ASyncStreamThatGivesUpOnItsOwnerEndsTest` (`MeshWeaver.Data.Test`) | a give-up that leaves the stream alive on its last value, or one activation's NACK and announcement charged as two activations (#6602), or routing-layer refusals charged as owner activations (#6729) |
| `ADrainingSiloDoesNotSilenceItsSubscribersTest` (`MeshWeaver.Hosting.Orleans.Test`) | a grain on a stopping silo accepting a subscribe it can only answer into the void (#5256) |
| `DeferredDeliveryNackedOnDisposeTest.ADiscardError_NamesTheRequest_SoItsProducerCanBeTold` (`MeshWeaver.Messaging.Hub.Test`) | a bring-up discard Error that names the request type but not its declared description (#5718), or that prints an undeclared payload |
| `DeferredDeliveryNackedOnDisposeTest.ARequestWhoseProducerDeclaresTheReask_IsTeardownNormal_NotAnError` (`MeshWeaver.Messaging.Hub.Test`) | a delivery stamped `ReasksOnShutdown` still reported at Error when a teardown discards it, or no longer answered `ShuttingDown` (#5718) |
| `DeferredDeliveryNackedOnDisposeTest.ADeferredReplyToTheDisposingHub_IsTeardownNormal_AndItsReplierIsNotNacked` (`MeshWeaver.Messaging.Hub.Test`) | a reply parked behind a young activation's gates reported at Error, or reported as answering its replier `ShuttingDown`, or a drain that NACKs the replier (#6791) |
| `AWriteDuringItsOwnersRecycleLandsTest` (`MeshWeaver.Graph.Test`) | a node write refused `ShuttingDown` by an owner that stays registered through its teardown, whose re-asks go back to that same owner instead of waiting for it to be gone (#7326) |
| `PatchDiscardedByABringUpTeardownIsReaskedTest` (`MeshWeaver.Graph.Test`) | a node write parked behind a young activation's gates that does not land on the next activation after a direct teardown, or whose discard is reported at Error (#6733) |
| `PointReadDiscardedByABringUpTeardownIsReaskedTest` (`MeshWeaver.Graph.Test`) | the PRODUCTION claimant: a `GetMeshNodeOutcome` request — the initial one or the re-probe issued from the `ShuttingDown` NACK — that arrives without the `ReasksOnShutdown` stamp, whose bring-up discard is reported at Error, or a read that does not settle `Present` after two such discards (#6717) |
| `GetMeshNodeReProbeKeepsCallerIdentityTest` (`MeshWeaver.Data.Test`) | a re-probe posted without the read's identity (#5227), or a re-probe issued nested inside the first probe's `Subscribe` that the first probe's late slot assignment disposes, dropping the owner's answer (#7395) |
| `PostOptionsReaskStampTest` (`MeshWeaver.Messaging.Hub.Test`) | a reply built with `ResponseFor` / `WithRequestIdFrom` that inherits the request's `ReasksOnShutdown` stamp (#6717) |
| `UnansweredReassertOnRecycleTest` (`MeshWeaver.Data.Test`) | an owner recycled while it holds the answer to a re-ask it accepted dropping that answer, so the subscriber's in-flight re-ask blocks every later one until the request timeout (#6759) |

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

## A held re-assert is answered when its stream ends (#6759)

A re-ask that reaches an owner already serving the stream is a RE-ASSERT: the owner re-sends the
stream's current state, then acknowledges. When the stream has no state yet, the owner holds its
answer until the stream first emits. That hold used to be registered on the stream's disposal and do
nothing there. So an owner recycled before its stream ever emitted dropped the answer: no ack, no
refusal. The subscriber's re-ask stayed in flight until the request timeout (60 s), and its in-flight
guard dropped every later re-ask in that window, including the one the owner's own end-of-stream
announcement triggers. The read emitted nothing for the whole request timeout.

The overlay self-heal makes this reachable: it recycles a per-node hub the moment its NodeType
compiles. A reader whose initial subscribe and change-feed re-ask both queued behind that activation
can have both processed, and the recycle run, before the fresh stream emits.

The stream ending is now a terminal of its own. If the held answer is still unclaimed when the stream
completes or is disposed, the owner refuses it with a transient `ShuttingDown` composed through
`ShutdownNack` (banner and activation tag). The owner posts the refusal itself while it can, or else
its parent posts it, the same route as the read and write NACKs above. The subscriber then rides the
recycle out like any other refusal, and its next re-ask lands on the fresh activation.

When the whole tree is torn down, the owner AND its parent are past `DisposeHostedHubs`, so neither
route is open. The owner still posts the refusal itself: the post seam hands a correlated NACK to a
requester in the same process directly (`MessageHub.TryDeliverNackInProcess`, #4072). A sibling
subscriber is `Quiescing` on exactly that callback. Before this was added, the owner only logged that
it had no route, and the subscriber's teardown waited out its whole quiesce budget.
`UnansweredReassertOnRecycleTest.WholeTreeTornDownWhileHoldingAReassert_TheQuiescingSubscriberIsAnswered`
pins it: with the refusal dropped, the subscriber's teardown does not complete within the convergence
wait.

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

The refusal is **unconditional**, including while a hub build or a long-running operation holds the
activation. Whatever such an activation would do with a delivery, its answer takes the same refused
router, so accepting it would bring back the silent wait. The migration waits for the activation to go
idle, so held work is not cut short by the hand-off.

**Known limit:** membership cannot tell a lingering peer from a healthy one, because a leaving silo
stays `Active`. When two silos stop at once, the hand-off target can be the other leaving silo. The
migrated activation there refuses and hands off again, and each hop is a distinct activation on the
subscriber's bounded re-arm budget. That is bounded and loud (the give-up logs a Warning), never a
silent wait. A cluster-wide drain signal would remove it.

What this section does not cover, the next one does: work the grain accepted **before** the stop
(#7470).

What this does **not** cover: a subscriber that was already **warm** when the silo began stopping still
gets no goodbye from the leaving host, because its `StreamEndedEvent` rides the refused router like any
other outbound message. It recovers on its next request to the address, which is now refused
honestly and handed off.

## 🚨 …and work it accepted BEFORE the stop is refused too (#7470)

The intake refusal above covers what arrives **after** `ApplicationStopping`. It did not cover what the
leaving host accepted just **before** it. Measured on a roll: a cache hub's `SubscribeRequest` for a
per-node hub reached the leaving silo about 2 s before its SIGTERM. The grain accepted it as
`Submitted`, parked it on a hub still being built, and its trail ended *"Forwarded … no handler was
ever entered"*. Nothing NACKed it, and the subscriber waited out its full 60 s. About 330 sibling
subscribes from the same cache hub stalled the same way at the same moment. Accepted work reaches a
leaving host in two shapes:

1. **Parked on a hub still being built.** When the build finished after the stop, the delivery was
   posted to the hub, and its `SubscribeAck` and first Full were refused at the router.
2. **Queued behind a busy hub, or answered after the stop.** The hub handled the request after the
   stop, and its reply was refused at the router.

**The fix: the leaving host answers what it accepted, on the one outbound path the gate still
allows.** The router's outbound gate stays where it is. Two pieces close the gap:

- **A parked delivery is refused the moment the host begins to stop.** `MessageHubGrain` parks a
  delivery on `HubReadyUnlessTheHostLeaves`: the hub's readiness raced against `ApplicationStopping`.
  A built hub replays synchronously, so it always wins when it exists. An unbuilt one loses to the
  stop, and the delivery is settled with the same `ShuttingDown` refusal the intake gate gives
  (`LeavingHostRefusal`). `NackParkedDelivery` posts it to the sender. The sender re-asks, the re-ask
  meets the intake gate on a grain turn, and the address is handed off.
- **A reply the host can no longer deliver becomes a refusal to its requester.** When the router
  refuses an outbound delivery that carries a `RequestId`, it also posts a `ShuttingDown`
  `DeliveryFailure` to that delivery's target (the requester), correlated to the requester's own
  request id (`RefuseUndeliverableReply`). A `SubscribeAck` produced after the stop therefore reaches
  the subscriber as a refusal it rides out. The same conversion runs when a reply passed the gate
  before the stop and is refused at the dispatch seam after it. A reply that is itself a
  `DeliveryFailure` (or a `[CanBeIgnored]` message) is never converted (`AnswerPolicy.MayAnswer`):
  it is already a verdict, often a terminal one, and must not become a retryable `ShuttingDown`.

**Which path the refusal takes, and why it is safe.** Both refusals carry
`OrleansRoutingService.LeavingHostRefusalProperty`, and the stop gate lets a stamped delivery through.
The stamp is honoured only while membership still reports this silo `Active`
(`LocalSiloMayStillPlace`), and that is the gate's own premise. The gate exists because a silo that has
**left** the active set cannot place `IRoutingGrain`, and Orleans logs an error per attempt (#1107).
While the silo is still `Active` (the linger window, in which the drain hand-off runs) placement
works. After it leaves, a stamped refusal is dropped exactly as before, and by then membership has
moved and the sender's own transport fails over. The membership verdict is re-read immediately before
every `RouteMessage` attempt, retries included, because a refusal can wait on its sender's attach or
claim while the silo leaves the active set.

Sending the refusals before the gate closes was rejected. It would race the router's own
`ApplicationStopping` callback, and it could not reach a reply that a hub produces after the stop.
The exemption cannot feed a loop: a refusal is never itself answered (`AnswerPolicy`), and a stamped
delivery is never converted again. The volume is bounded by the work the host had accepted.

Pinned by `AParkedSubscribeOnALeavingSiloIsRefusedTest` (MeshWeaver.FaultInjection.Test, on the
[fault-injection harness](../FaultInjectionHarness)):

- **The case.** The owner's grain is activated on silo 1 with its hub build held
  (`FaultInjectionCluster.HubBuilds`). A subscribe from silo 0 is parked there, and the harness's
  `Parked` signal confirms it. Silo 1 is then told to stop and lingers. The subscriber must be served
  within 10 s by a new activation on silo 0 while silo 1's build is still held.
- **Without the router exemption.** The test is red at its 10 s bound: the refusal is dropped at the
  gate, so nothing answers the parked subscribe.
- **The stop race on its own.** `AParkedRequestOnALeavingSiloWithNoHandOffIsRefusedTest` isolates it.
  Every other silo is recorded as draining (`FaultInjectionCluster.LeaveNoHandOffTarget`), so the
  lingering grain has no hand-off target and nothing migrates or deactivates it. Its build stays held
  for the whole case: the long cold build. A request from silo 0 parked there must be refused
  `ShuttingDown` within 10 s. Without the race it is red at that bound, because nothing ever answers
  it. In the two-silo case above, the hand-off's deactivation settles the parked delivery even without
  the race, which is why that case alone could not show the race was needed.
- **Negative control.** `ASubscribeParkedOnASiloThatStaysUpIsAnsweredTest`: the same parked subscribe
  on a silo that stays up is answered by that silo once its build is released.
- **The router half.** It is pinned in `OrleansRoutingShutdownClassificationTest` and
  `SiloShutdownActivationGateTest`:
  - a stamped refusal still reaches grain placement while the host stops, but not once the silo
    has left the active set before the dispatch;
  - an undeliverable reply answers its requester `ShuttingDown`, including when it is refused after
    the gate;
  - a refused verdict (`DeliveryFailure`) and a delivery that is not a reply are not converted.

## A write is a rider too (#5011, MeshWeaver.Plugins#2403)

The refusal #5256 introduced tells the sender the truth — *"… is shutting down (activation #…, its host
is stopping) … the address may reactivate … Rejecting now"* — and hands the address off. The read
riders above ride it out. The cross-hub **write** did not: `UpdateRemote`'s response wait mapped a
`DeliveryFailure{ShuttingDown}` to `MeshNodeErrorCode.Unknown` and faulted the caller. The very same
fact carried as a `PatchDataResponse` with `OwnerDisposing` was re-driven against the next activation.
So every write that met an owner on a pod being rolled was lost with a terminal error: the fleet
watch's `Ops/Status/*` rows, an instance action's status record, any `stream.Update` whose owner sat on
the old ReplicaSet for the length of the termination grace period.

`AWriteDuringAPodRollIsReDrivenTest` (MeshWeaver.FaultInjection.Test, on the
[fault-injection harness](../FaultInjectionHarness)) pins it. Silo 1 hosts the owner and is told to
stop and left lingering (`FaultInjectionCluster.Linger`). Silo 0 holds the node's stream open (the
fleet-watch heartbeat and running-action resumer shape) and writes the node; the cross-process
invalidation arrives through the harness's LISTEN model, as it does on every replica in production.
Before the fix the **write** failed in about a second with `MeshNode Unknown … Rejecting now`, so no
reader could ever see it. After it, the write lands on the handed-off activation and the held read
delivers it. Its siblings pin the rest of the roll: `AHeldReadSurvivesItsSourceSiloBeingKilledTest`
(kill) and `AHeldReadSurvivesItsSourceSiloDrainingTest` (graceful stop), each asserting that a write
after the move reaches the **already-held** read, not only that the owner re-activates (the half
`AKilledOwnerSiloIsReactivatedByItsHoldersTest` covers). They replaced the hand-published
`AHeldReadKeepsDelivering…` trio in MeshWeaver.Hosting.Orleans.Test.

**The fix is classification, not a retry.** `MeshNodeStreamHandle.IsShuttingDownRefusal` recognises
the routed refusal, and both arms of the response wait (the prompt one and the late
`LatePatchResponseRegistry` one) send it down the existing `OwnerDisposing` path. The budget is the
same (`MaxOwnerDisposingReenqueues`), with `ownerSaidNeverApplied: true`, because a refusal issued
before any handler ran is the strongest form of "never applied". Hence `LatePatchResponseRegistry.WriteTotalBound`
is unchanged: a ShuttingDown re-attempt spends exactly what an `OwnerDisposing` one already spent.
