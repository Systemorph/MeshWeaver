---
Name: Error Propagation & Wedges
Category: Architecture
Description: "Wedges (silent hangs) must be driven to 0. Every error propagates outward until it reaches a graceful sink — activity log, GUI error area, or thread output cell — and every layer in between forwards it, never swallows or hangs."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z"/><path d="M12 9v4"/><path d="M12 17h.01"/></svg>
---

# Error Propagation & Wedges

A **wedge** is a silent hang: a request that never gets a response, a stream that never emits or errors, a spinner — *"Subscribing to {path}…"*, *"Rendering Overview… awaiting first data"* — that never resolves. The portal serves HTTP 200, but one operation is dead and the user waits forever with no diagnostic.

**Wedges must be driven to 0.** There is exactly one rule:

> An error **propagates outward** until it reaches a layer that can present it **gracefully**, and that layer records or renders it. Every layer in between **forwards** the error — it never swallows, drops, or hangs on it.

A wedge is always a *missing edge* in that propagation: somewhere an error was caught-and-ignored, a `Subscribe` had no `onError`, or a request handler finished without answering. Find that edge and route it to a sink.

## The graceful-error sinks

Propagation terminates at the sink that owns the user-visible surface for the operation's context:

| Context | Sink | How |
|---|---|---|
| **Activity** | the **activity log** | write the failure onto the activity node (`Status = Error` + the message), see [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) |
| **GUI** | the **error area** | the layout area surfaces it — `NamedAreaView`'s control-stream `onError`, `LayoutAreaHost.FailRendering`, or the modal `PortalErrorSink`. The page shows the error, never an endless spinner |
| **Thread / agent** | the **output cell** | `ThreadExecution.PushToResponseMessage` writes `Status = Error` into the response cell + emits the completion notification, see [Thread Operations](/Doc/Architecture/ThreadOperations) |
| **Everywhere else** | — | **forward**: NACK the request (a typed `DeliveryFailure`), propagate `OnError`, or rethrow — so the error reaches one of the sinks above |

The job of every non-sink layer is to *forward, faithfully*. A router NACKs the caller; a stream propagates `OnError`; a handler that can fail must answer with a `DeliveryFailure`. Nothing in the middle is allowed to absorb the error — absorbing it *is* the wedge.

## The forbidden wedge-makers

These are the edges that turn an error into a wedge. Each is a defect:

- **`catch { }` / swallow-and-continue** — the caller is still waiting; nothing ever answers it.
- **`.Catch(Observable.Empty())`** — completes the stream silently; the subscriber's `onNext` never fires and its `onError` never fires either → eternal spinner.
- **`.Subscribe(onNext)` with no `onError`** on a hub / GUI / activity / thread stream — a fault propagates unobserved on the Rx scheduler (and tears down the Blazor circuit), or is simply lost.
- **A request handler that can finish *without sending a response*** — no success, no NACK → the caller's `Observe(...)` parks until the framework timeout, then the GUI re-issues → a NotFound/Failed **storm** (see [Action-Block Wedge Prevention](/Doc/Architecture/ActionBlockWedgePrevention)).
- **A timeout / watchdog that resets-and-retries without surfacing** — papering over the hang instead of forwarding it. This is a band-aid; the fix is to make the error reach its sink.

The invariants that close these holes: **a request type always answers** (success or a typed `DeliveryFailure`); **a subscribe always surfaces `OnError`**; **a render always reaches `FailRendering` / the error area**.

## Worked scenarios

Every wedge we have diagnosed is a missing edge resolved by routing to a sink.

### Skill / slash-command selection (GUI → error area)

Selecting a `/agent` or `/model` command in the chat composer runs the skill flow: resolve the `nodeType:Skill`, open the picker, then **write the pick onto the composer node**. These steps are deferred behind Rx hops (`ObserveSnapshot(...).Subscribe(... InvokeAsync ...)`), and the Blazor inbound-activity `finally` has by then nulled the circuit `AccessContext`. The composer write (`GetMeshNodeStream(path).Update(...)`) captured a **null** identity → the owning hub's `PostPipeline` failed closed → the user got *"Saving your selection: Access denied"* (and worse, the skill query under null identity could return nothing → *"Unknown command"*).

Two edges were missing: the deferred read/write **dropped** the user identity, and the write's failure had to reach the GUI's error area rather than vanish. The fix re-establishes the **durable circuit user** (`ICircuitContextAccessor.UserContext`, which survives the hops) on every deferred read/write, and the write error surfaces via `SurfaceError` / `PortalErrorSink`. See [AccessContext Propagation](/Doc/Architecture/AccessContextPropagation).

### "Subscribing…" on a broken NodeType (GUI → error area)

Opening a node whose NodeType won't compile subscribes to its layout area. The grain churned (compile fault → `DeactivateOnIdle`); the in-flight `SubscribeRequest` hit *"invalid activation. Rejecting now."* and the router **dead-ended it onto a subscriber-less memory stream** → 60 s timeout → *"Subscribing to {path}…"* forever. The missing edge was the router silently dropping a transient rejection. The fix retries the delivery so the grain **reactivates** (a fresh instance answers), and on a terminal failure **NACKs the sender** — never the silent dead-end. Once the grain is reached, the compilation-error overlay renders the error into the **Overview area** (the GUI sink). See [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) and [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow).

### A delivery that ROUTING failed while passing through (router → requester)

A hub that is not the delivery's target only *forwards* it. When forwarding failed, `MessageService`
returned the failed delivery to its own turn loop and nothing else happened — the state was never
inspected on that branch, so **no `DeliveryFailure` was posted and the requester's `hub.Observe(...)`
never resolved**. The tell is a request that was dequeued and handled, every queue empty, nothing
wedged on an action block, and a caller waiting anyway.

The routing sites that hit it are all disposal races, and none of them NACKs on its own: a hosted hub
that quiesces *after* its parent reached `DisposeHostedHubs` still posts to that parent, and the
reply can never come. The missing edge was the router keeping the failure to itself.

The invariant restored: **routing failures report like every other failure.** The failing site records
two things ON the delivery — the `ErrorType` verdict (decided where the condition is known, never
re-derived downstream from message text) and whether it already answered the sender — and
`MessageService` NACKs every unanswered one. Disposal races report as the transient
`ErrorType.ShuttingDown`, so a `SynchronizationStream` rides them out instead of tearing down; a
routing loop reports as `ErrorType.RoutingLoop`. A site that posts its own NACK (the routing
services' `NotFound`, on a hot path) marks the delivery answered so the sender never gets two.

#### Recording a verdict is not the same as delivering one (#2346)

Both halves above are carried in the delivery's `Properties`, and both leaked between the site that
wrote them and the site that acts on them. Worth knowing, because the failure is **silent in both
directions** and the symptom appears somewhere else entirely — as a rotating cast of Orleans
teardown flakes, which is how it was eventually found:

- **The verdict did not survive a hub boundary** — the only trip it exists for. `Properties` is
  `IReadOnlyDictionary<string, object>`, serialized by writing each value by its RUNTIME type, so an
  `ErrorType` goes out as the enum NAME and comes back as a `string`. `GetFailureErrorType`'s
  `value is ErrorType` test then failed and every consumer read its FALLBACK — the carefully-recorded
  `ShuttingDown` reached the router as "no verdict was reached". It now recovers the enum, its name,
  an integral value or a `JsonElement`; an unrecognised value still falls back, which is honest
  rather than invented.
- **The Orleans routing layer read neither.** `RoutingGrain.DeliverToGrainWithRetry` — the ONLY site
  that reports a grain-routed delivery's failure, because `RouteMessage` returns `Forwarded`
  unconditionally and delivers on a background route — hard-coded the terminal `ErrorType.Failed`
  and ignored `SenderWasNacked`. So a disposal race was reported as permanent, *and* the sender got
  a second, contradictory answer racing the hub's own.

The rule the two together produce, and the one to apply at any new reporting site: **honour the
answered flag first, then read the CARRIED verdict, and fall back to the shared text rule
(`ClassifyRoutedFailure` — the same phrase list `AreaErrorClassifier.IsTransientHubFailure` and
`MeshNodeStreamCache.IsTransientOwnerFailure` use) rather than to a terminal default.** A verdict
that cannot be read is not evidence of a permanent failure.

#### Who refused? The BANNER, not the classification (#3017)

A caller that gets `ErrorType.ShuttingDown` still has a second question to answer: **did the OWNER
refuse me, or did the routing layer fail to reach it?** They call for opposite responses — re-probe
the address that announced its own return, versus stop asking about one nothing can route to — and
the classification cannot tell them apart, because the routing layer mints `ShuttingDown` too, off
the owner's own text (`ClassifyRoutedFailure`).

The evidence that does separate them is the **banner**: `Hub {address} is shutting down`. Only the
owner makes THIS address the subject of it. The routing layer says `No node found at 'x'`,
`No route to 'x'`, `Mesh is shutting down, cannot route to x`, `Host is shutting down, cannot route
to x` — the mesh or the host is the subject there, never the address you asked about.

**One seam composes every owner-side refusal, and the same seam recognises one.**
`ShutdownNack.RejectingNow` (a delivery turned away at the door) and
`ShutdownNack.RetryForTheAuthoritativeAnswer` (work the hub accepted and can no longer finish) build
the sentence; `ShutdownNack.IsAnsweredByOwner` reads the banner back out. The producers are the
intake gate and the late turn (`MessageService`), the access gate (`AccessControlPipeline`, which
runs INSIDE the owner and is its own intake — not the routing layer), the typed
`HubDisposingException` a handler throws, and the `DataContext` gate that can never open.

**Why it is a seam and not a convention.** Recognition used to be a LIST of the refusal sentences
somebody had thought of, and the list lost a race with the source three times. #1599 removed the
first version (pinning one arm's free text — 21 failures in 60 on unmodified `main`). A fourth
terminal was added after it reddened a suite in 2026-08. A fifth did the same in #3017 — the access
gate's refusal, rejected as "not from the owner" although it is the owner's own gate — and a sixth
was live and unlisted the whole time, the intake gate's `Rejecting now.` form. Each round the
enumeration's own guard passed, because **a guard over an enumeration can only assert the members
already written down**: it cannot discover the terminal nobody listed. A derived predicate follows
the producers instead of trailing them, and a seventh terminal is recognised the day it is written.

The wording of these sentences is contract in the other direction too — the transient classifiers
(`MeshNodeStreamCache.IsTransientOwnerFailure`, `AreaErrorClassifier.IsTransientHubFailure`,
`OrleansRoutingService.ClassifyRoutedFailure`) match on `is shutting down` / `Rejecting now`, and a
casual reword silently restores #2727: nothing fails to compile, the delivery is still refused, and
the caller simply stops retrying. Composing through the seam is what makes both contracts hold by
construction rather than by memory. `OwnerAnswerRecognitionGuard` calls every reachable producer and
runs the real predicate and the real classifier over what they actually say; `DisposalRaceNackTest`
pins the same recognition on a NACK that travelled the real path.

### Long-running operations (activity → activity log)

An import / compile / mirror runs as an activity. A fault must not strand the activity "Running" forever — it writes `Status = Error` with the message onto the activity node, which the activity log and any progress reader render. Persistence at the bottom of the stack never re-gates and never fail-closes a write that was already approved; it forwards. See [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) and [Activity Operations](/Doc/Architecture/ActivityOperations).

### The one leg that cannot forward yet (Orleans streams)

Every rule above assumes a transport that can *tell you* it failed. One cannot: delivery to a
**pod-process hub** — `mesh`, `portal`, `client`, `cache`, `import` — is an Orleans stream publish,
and **a publish to a stream with no live subscriber succeeds**. Nothing faults, the continuation
never sees `IsFaulted`, and the message is gone; the requester then waits out its full 60 s budget
for an answer the router believes it sent. Because a reply is just a delivery addressed back at the
requester, this is the failure mode of every cross-silo reply to those hubs.

Two things have been done about it and one has not. `RoutingGrain.PostFailure` now answers a
**co-hosted** sender over the local route instead of the stream (#1486), and the pub-sub subscription
registry is **durable** so the registry no longer evaporates on every deploy (#1729) — but neither
makes an undeliverable reply *report*. Closing that gap means taking replies off streams entirely;
see [Orleans Stream Pub-Sub Durability](/Doc/Architecture/OrleansStreamPubSubDurability) for the
mechanism, what durability does and does not buy, and the shape of the remaining fix.

#### A dead subscriber is a verdict the owner must act on (#2426, #2546)

The router does ask before it publishes — `RoutingGrain.RefuseNoSubscriber` NACKs `NotFound` when
the destination stream has no live subscriber — and for a long time that was the *end* of the story
rather than the start of one. An owner hub serving a data stream to a subscriber whose **process
died** (a restarted portal's circuits, a disconnected `node/` gRPC participant) keeps that
server-side stream forever: only an `UnsubscribeRequest` disposes one, and a corpse sends none. So
the owner fanned every change out to the dead address, the router refused each one at `Error`, and
the NACK went to a **per-node grain sender that had no stream subscription to receive it on** — the
one signal that could end the loop was produced and thrown away. That is the 20,718-lines-in-3-h
storm, and it is not a retry loop: nothing retries, the fan-out simply never learns.

Three edges close it, and each follows a rule already stated on this page:

- **The verdict is stamped where it is known.** The refusal carries
  `DeliveryFailure.TargetUnserved = true` — *deliberately* distinct from `ErrorType.NotFound`, which
  a live hub also answers for an unhandled request. Only the router, which asked the cluster-wide
  subscription registry, may stamp it; absence means "do not evict".
- **The NACK reaches the sender.** A grain-hosted sender (a per-node hub) is answered over the
  **grain transport** — the same `IMessageHubGrain.DeliverMessage` every forward delivery to it
  takes — with the stream publish kept as the fallback for a sender that is not node-backed.
- **The owner evicts.** `Workspace.EvictClientSubscriptions` disposes every server-side stream it
  still serves for the unserved address, through the stream's own sync hub — the exact route an
  `UnsubscribeRequest` takes. Evict-only: no watchdog, no re-probe, no resubscribe. A subscriber that
  was in fact alive loses only its server-side half and re-asks through its own change-feed latch,
  exactly as after an owner recycle.

What is *not* done, on purpose: the delivery path carries no negative cache. The subscriber probe is
authoritative and measured cheap (0.010 ms warm), and a fast-refuse window would NACK a subscriber
that has just re-attached, manufacturing an evict/resubscribe loop out of the "optimisation". What
is bounded instead is the **log**: a known-dead address earns one full `Error` line per window
(`DeadTargetRefusalLog`, one minute), repeats inside it log at `Debug` and are counted into the next
full line — the storm's volume stays on the record while Loki stops paying per delivery. Every
delivery is still refused, traced and NACKed; only the line is windowed.

## Where this sits

This is the *what must always happen* — errors reach a sink. The *why a single thread saturates* is [Action-Block Wedge Prevention](/Doc/Architecture/ActionBlockWedgePrevention) (amplification on the single-threaded action block). The *how to trace a live hang* is [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow). The reactive rules that keep forwarding intact across hops are [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) and [AccessContext Propagation](/Doc/Architecture/AccessContextPropagation).
