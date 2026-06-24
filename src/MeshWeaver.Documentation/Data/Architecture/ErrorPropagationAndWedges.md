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

### Long-running operations (activity → activity log)

An import / compile / mirror runs as an activity. A fault must not strand the activity "Running" forever — it writes `Status = Error` with the message onto the activity node, which the activity log and any progress reader render. Persistence at the bottom of the stack never re-gates and never fail-closes a write that was already approved; it forwards. See [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) and [Activity Operations](/Doc/Architecture/ActivityOperations).

## Where this sits

This is the *what must always happen* — errors reach a sink. The *why a single thread saturates* is [Action-Block Wedge Prevention](/Doc/Architecture/ActionBlockWedgePrevention) (amplification on the single-threaded action block). The *how to trace a live hang* is [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow). The reactive rules that keep forwarding intact across hops are [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) and [AccessContext Propagation](/Doc/Architecture/AccessContextPropagation).
