---
Name: A Timed-Out Delivery Is Still Held by the Callee
Category: Architecture
Description: Orleans' ResponseTimeout is a caller-side give-up timer, not a cancellation — so re-sending a timed-out delivery duplicates it and pays another whole timeout per attempt. The three-predicate ladder that separates "is this transient", "may we send it again" and "should the sender keep recovering", and why the routing back-pressure report at 64 in flight is the far end of that amplifier.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/><path d="M19 5 5 19"/></svg>
---

# A Timed-Out Delivery Is Still Held by the Callee

A rejection and a timeout are both "transient". They are opposite facts about **who holds the
request**, and a retry that cannot tell them apart turns a slow destination into a message storm.

## The distinction

| | What the callee did | What a re-send does |
|---|---|---|
| **Rejection** (`OrleansMessageRejectionException` — *"… to invalid activation. Rejecting now."*) | REFUSED it. Holds nothing. | Re-resolves placement, so the message lands on a freshly activated grain. This is the case the delivery retry exists for. |
| **Response timeout** (`TimeoutException`) | ACCEPTED it and has not answered yet. The request sits in that activation's work queue. | DUPLICATES it. The queued copy still runs. |

Orleans' `ResponseTimeout` is a **caller-side give-up timer, not a cancellation** — nothing recalls a
message once it has been handed to the target activation. So a timeout says only that the caller
stopped waiting.

Nothing on the receive path can recognise a repeat. `IMessageHubGrain.DeliverMessage` ends in
`hub.DeliverMessage(delivery)` — an unconditional post onto the target hub's queue — and no reader
of `IMessageDelivery.Id` dedupes. **At-least-once delivery over a non-idempotent handler is
at-least-once handler execution.**

## Why it was an amplifier, not a nuisance

The standing rationale for letting the transient predicate match `TimeoutException` was that *"it is
bounded by a retry budget, so it can afford to be generous"*. The budget is bounded in **attempts**
(6) and its delays are sized for the fault it was written for — a rejection, which Orleans returns
instantly: 250 ms → 3 s, 9.75 s in total.

A timed-out attempt does not cost 250 ms. It costs the transport's whole `ResponseTimeout`. The same
ladder therefore means two very different things:

| fault class | attempts | wall clock per delivery | copies queued at the callee |
|---|---|---|---|
| rejection | 7 | ~10 s | 1 (each previous one was refused) |
| response timeout | 7 | **~3 m 40 s** | **7** |

And the direction of the coupling is the problem. A response timeout on a delivery leg happens
precisely when the destination is slow — a per-node hub whose `HubReady` has not emitted yet, which
for an `_Activity/compile` address means an in-mesh NodeType compile — or when the silo is CPU/thread
starved. In both cases the retry **multiplies the work of the thing that was already too slow**, at
the moment it has least capacity, while each leg holds one `RoutingGrain` dispatch slot for the whole
3 m 40 s. That is a positive feedback loop wearing a recovery's clothes.

Issue #1172 is the far end of it: `[ROUTE] Routing back-pressure … 64 route dispatches in flight`,
whose original evidence was dominated by `_Activity/compile` and `_Activity/import` targets — the
addresses whose `HubReady` takes longest, i.e. exactly the ones the ladder re-sent to seven times.

## The ladder

Three predicates, three different questions, each strictly narrower than the last. Do not collapse
them.

```
IsTransientFailure(ex)               "is another attempt CONCEIVABLE?"
  ⊇ IsResendableDeliveryFailure(ex)  "may we SEND THIS REQUEST AGAIN?"
      ⊇ ClassifyDeliveryException(ex) == ShuttingDown
                                     "should the SENDER keep its unbounded recovery armed?"
```

- **`IsTransientFailure`** — unchanged, and its classification was never wrong; it was being asked
  the wrong question. A timeout IS a transient fault.
- **`IsResendableDeliveryFailure`** = `!IsResponseTimeout(ex) && IsTransientFailure(ex)`. The gate on
  every retry over a **non-idempotent** call: `RoutingGrain.DeliverToGrainObservable` (which serves
  both `IMessageHubGrain.DeliverMessage` and `IPodHubGrain.Deliver`) and
  `OrleansRoutingService.DispatchObservable`'s `IRoutingGrain.RouteMessage`.
- **`ClassifyDeliveryException`** — unchanged, and deliberately narrowest: a bare `TimeoutException`
  stays the TERMINAL `ErrorType.Failed`, because telling a consumer "transient" arms an *unbounded*
  resubscribe against a target that is plausibly wedged.

`IsResponseTimeout` walks the exception **graph** (`ExceptionChain`), not the `InnerException` line.
These faults arrive through Rx `Catch` arms and two-transport `AggregateException`s where which fault
sits at index 0 is a race, so a walker that only followed `InnerException` would re-send or not
depending on the ordering.

### The one caller that keeps the wider predicate

`OrleansRoutingService.AttachWithBoundedRetry`'s `IPodHubGrain.Attach` claim is **idempotent** — it
sets flags and re-pins an activation — so re-sending it after a timeout costs nothing and is how the
claim converges (#2633). That is why `IsTransientFailure` is left intact rather than narrowed in
place: narrowing it would have silently disarmed the pod-hub claim retry.

## Declining to re-send suppresses nothing

The fault reaches the **same** arm it reached after the retries were exhausted, and
`ClassifyDeliveryException` gives the sender the **same** verdict it always got. The only change is
*when*: one `ResponseTimeout` after the first attempt instead of seven of them later, with one copy
of the delivery at the callee instead of seven. The sender's own recovery — `SynchronizationStream`'s
resubscribe latch, `MeshNodeStreamCache`'s transient-owner rule, an `Observe(...)` subject firing
`OnError` — decides what happens next, as before.

## A hub that is still being built no longer holds the call open (#5286, #5417)

The ladder above decides what to do once a timeout has happened. The commonest reason one
happened was that the callee made its caller wait on something that is **allowed** to take
longer than the caller will wait.

`MessageHubGrain.DeliverMessage` used to return a task that completed only when the grain's
`HubReady` signal emitted — so the Orleans call's acknowledgement waited on the **whole hub
activation**: node resolution, NodeType binding, assembly load, a cold compile. Activation is
deliberately allowed to run past 30 s (`FirstNodeResolutionTimeout` bounds only the *first* node
emission, and `WaitForCompileSettled` switches its own timer off while a compile is running). Orleans'
`ResponseTimeout` is 30 s. So every delivery to a hub that took longer than that to build reached
`RoutingGrain` as a `TimeoutException` and was reported to the sender as the terminal *"Delivery to
'Store' failed: Response did not arrive on time in 00:00:30"* — while the delivery was still parked
in the grain and was posted to the hub moments later. The sender had already been told it failed,
and a Blazor view's control stream (`NamedAreaView`) was torn down on that answer.

Production logged exactly that for `messagehub/Store`, `messagehub/Underwriting` and
`messagehub/AgenticEngineering`: a young activation (`Total Enqueued=5; Total processed=5`),
`NumRunning=5`, `QueuedWorkItems=0`, `IdlenessTimeSpan=00:00:00`. That is five reentrant
`DeliverMessage` calls in flight, each one waiting on `HubReady`, and none queued behind a busy
turn.

**The rule now:** the grain answers the call when it has accepted the delivery, not when the hub has
handled it.

| When `HubReady` answers | The acknowledgement | A failure reaches the sender through |
|---|---|---|
| **Synchronously** — the hub is built (the steady state) | the hub's own verdict, as before (#3045: state, `SenderWasNacked`, failure text) | `RoutingGrain.DeliverToGrainRoute`'s result arm, unchanged |
| **Later** — the hub is still being built | `Submitted`: accepted and parked, in the same ordered `HubReady` subscription as before | the grain itself: `NackParkedDelivery` posts the `DeliveryFailure` through the mesh (`o => o.ResponseFor(delivery)`) — the shape the Monolith router already uses for an activation fault |

Classifications do not change with the path: an activation fault is still `Unavailable`, a hub
disposed before delivery is still `ShuttingDown`, a hub-side refusal carries the verdict the hub
recorded (falling back to the same `ClassifyRoutedFailure` text rule `RoutingGrain` applies), and an
exception thrown while posting to the hub is classified by `RoutingGrain.ClassifyDeliveryException`
exactly as when it used to fault the grain call. Answer-once holds because a parked acknowledgement
never carries `Failed`, so `RoutingGrain` never sends a second NACK. `HubReady` emits on the
activation chain's thread rather than the grain turn, so which side reached the verdict first is
decided by a compare-and-swap (`DeliverySettlement`), not by timing.

**The build pins the activation.** The parked grain calls used to be the only thing keeping a
building activation from idle collection. Once they are answered on acceptance, collection
mid-build would complete `HubReady` and NACK every accepted delivery as `ShuttingDown`. The
activation chain therefore takes a `HoldActivation("hub build")` for as long as it is subscribed.
This is the same counter the long-running-operation keep-alive renews, bounded by the #147 cap, and
`Observable.Using` releases it on every terminal and on deactivation.

**`Ignored` is treated the same on both paths.** A late `Ignored` verdict (the storm breaker or the
aggregate shedder refusing intake) is not NACKed, which is exactly what the synchronous path
does: `RoutingGrain`'s result arm acts on `Failed` only. Those refusals deliberately mint no
`DeliveryFailure`, because answering them feeds the loop they break (`WasAcceptedForDelivery`,
#1174). Whether an intake refusal should answer its sender is one question for both paths. It is
not decided by which path happened to carry the delivery.

**Not changed:** `ResponseTimeout`. Raising it would only move the point at which a slow build turns
into a false failure. A hub that never finishes building still ends in an activation fault, which
NACKs the sender. That was always true, and it no longer depends on a timer that belongs to the
caller.

## What this does NOT explain

The back-pressure report is a **gauge, not a bound** — nothing throttles, queues or refuses at 64;
`RoutingGrain.ReportSaturation` carries the whole reasoning, including why the number it prints is
always exactly the threshold. So this change makes the report rarer and each episode shorter; it does
not make the counter mean something new. Two shapes remain at that log site and neither is closed by
this:

- **`deepest per-destination queue 0`** — pure breadth. Legs in flight, nothing waiting on anything.
  Reduced by a factor of up to 7 on the timeout path, but still reachable under genuine load, and
  still inflated by the unbounded wait for a ThreadPool thread that a slot is held across *before*
  the leg's own timeouts start.
- **`deepest per-destination queue 62` on one `cache/…` destination** — the FIFO's key was the
  **destination address** while the ordering invariant it protects (`SynchronizationStream`'s
  receive-side monotonicity guard) is **per stream**, so all traffic to one process's cache hub was
  serialised whether or not any ordering relationship existed between two frames. That
  over-broadness is what let one destination stack ~62 legs. Measured on memex-cloud 2026-09-19 in
  two independent episodes (`fcf31578#14`, `67341d12#7`) and again 2026-09-20 on both pods. Not
  addressed here — addressed in
  [Ordered Route Channels](../OrderedRouteChannels), which narrows the channel key to
  (destination, stream) and re-words this field of the report accordingly. Note that this page's own
  fix lowered the *arrival rate* of that lane, which moves the threshold a single-lane channel
  saturates at but cannot remove it.

The earlier root already fixed on this path was O(node-size) JSON patch construction on hub action
blocks (#1341), and the earlier slot LEAK was `IoPool.SubscribeThroughPool` terminating an observer
in neither direction when the drain cancelled it (#1358). This is the third mechanism at the same
log site, and the first one that was a *classification* error rather than a cost.

## Where it lives

| | |
|---|---|
| `OrleansRoutingService.IsResponseTimeout` | the one definition of "the callee may still hold it" |
| `OrleansRoutingService.IsResendableDeliveryFailure` | the gate, client side (`RouteMessage`) |
| `RoutingGrain.IsResendableDeliveryFailure` | the gate, router side (both forward delivery legs) |
| `MessageHubGrain.DeliverMessage` / `NackParkedDelivery` | accept-and-park while the hub is built; the late NACK |
| `ASlowActivationDoesNotTimeOutItsDeliveriesTest` | a hub build held for 3× the cluster's `ResponseTimeout` and 5× its `CollectionAge`: the parked request is neither timed out nor collected during the hold, and is answered after it |
| `TimedOutDeliveryIsNotResentTest` | 6 facts: the delivery is sent exactly once, a rejection still spends its whole budget, both aggregate orderings agree, and all three rungs of the ladder |
