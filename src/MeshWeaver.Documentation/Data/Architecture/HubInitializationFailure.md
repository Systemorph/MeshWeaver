# Hub initialization failure — fail gracefully, never wedge

A message hub initializes by running its **BuildupActions** (the observables registered via
`WithInitialization(...)`) and then opening the **`Initialize` gate**. Until that gate opens, every
message targeted at the hub is **deferred** (held in the deferred queue). The gate opening is what
lets the hub start processing real traffic.

## The failure mode this guards against

If a BuildupAction **throws**, the naive composition propagates the error out of the
`Observable.Concat`, so the step that calls `OpenGate(Initialize)` **never runs**. The gate stays
closed *forever*. Every subsequent message then sits in the deferred queue until the
**30-second deferral timeout** (`MessageService.DeferralTimeout`) fires a generic
`DeliveryFailure` ("deferred >30s without opening init gates …").

To a user this is an **unrecoverable wedge**: the node is reachable (HTTP 200) but every interaction
times out at 30s, and the GUI shows a useless "Area unavailable — did not become addressable after N
retries." The root error (what actually went wrong in init) is **invisible**.

> **Production incident (2026-06-16).** Selecting an agent in the chat composer triggered a
> hub whose init threw. The `AgenticPension` grain's action block was stuck behind the closed gate,
> so every `DeliverMessage` to it timed out at 30s and the whole node went dark.

## The rule: a faulting init must FAIL GRACEFULLY

A hub whose initialization throws must:

1. **Record the failure as status.** `MessageHub.InitializationError` is set to the init exception.
   The hub stays `RunLevel.Started` (it is *not* a new run level — the lifecycle enum is strictly
   ordered) but is now in a FAILED state.
2. **Still react to messages.** The `Initialize` gate is opened **anyway**, so the hub can answer
   traffic and be torn down. A closed gate is the wedge; an open gate on a failed hub is recoverable.
3. **Refuse requests with a proper status.** Every non-lifecycle request is answered immediately with
   a typed `DeliveryFailure { ErrorType = ErrorType.Failed, Message = "Hub '<addr>' initialization
   failed: <reason>" }`. Callers get a `DeliveryFailureException` carrying the *real* reason — FAST,
   not a 30s timeout.
4. **Let lifecycle/control traffic through.** `DeliveryFailure`, `ShutdownRequest`, `DisposeRequest`,
   `InitializeHubRequest`, `HeartBeatEvent` are **not** refused — disposal must still work, keep-alive
   must not deactivate the grain, and a `DeliveryFailure` must never beget another (storm). This is
   the same bypass set `MessageService` applies at the gate.

## Where it lives

`MessageHub.HandleInitialize` wraps the BuildupAction composition in a single high-level `.Catch`:

```csharp
return Observable
    .Concat(actions.Select(a => a(this).DefaultIfEmpty(Unit.Default).Take(1)))
    .ToList()
    .Select(_ => { OpenGate(InitializeGateName); return request.Processed(); })
    .Catch((Exception ex) =>
    {
        EnterInitializationFailedState(ex);     // status = failed + register the refusal rule
        OpenGate(InitializeGateName);           // ALWAYS open — a closed gate is the wedge
        return Observable.Return(request.Failed($"Hub '{Address}' initialization failed: {ex.Message}"));
    });
```

`EnterInitializationFailedState` sets `InitializationError` and registers a front-of-chain rule that
refuses every non-lifecycle request with the typed `DeliveryFailure`.

This **generalizes** the per-context guard that already lived in `DataContext.OpenInitializationGate`
(which opens its own `DataContextInit` gate even on fault) up to the hub level, so **every**
BuildupAction — not just the DataContext one — fails gracefully.

## How it shows on the GUI

Because the failure is now a `DeliveryFailure` flowing back through the subscriber rather than a silent
wedge, a layout-area subscription receives a `DeliveryFailureException` carrying
`"… initialization failed: <reason>"`. The area binding (`AreaErrorClassifier` / `NamedAreaView`)
renders that message instead of spinning to the generic "did not become addressable" timeout. The user
sees **what** broke.

## What this does NOT cover

A BuildupAction that **hangs** (never completes, never throws) still relies on the per-message 30s
deferral timeout to surface a `DeliveryFailure` — there is no throw to catch. Hangs are a separate
concern (e.g. a cross-hub read of a *different* FAILED hub that defers); fix the hung dependency, don't
paper over it with a timer.

## Test

`test/MeshWeaver.Messaging.Hub.Test/InitializationErrorSurfacedTest.cs` pins the contract: a hub with a
faulting BuildupAction answers a probe request with a `DeliveryFailureException` carrying
`"initialization failed: <reason>"` **fast** (a `TimeoutException` would mean the gate never opened —
the regression), and exposes the `InitializationError` status marker.

## Related

- [AsynchronousCalls](../AsynchronousCalls) — why init is reactive (`IObservable`, no `await`).
- [InitializationGates](../InitializationGates) — the gate model and the framework-bypassed messages.
- [DebuggingMessageFlow](../DebuggingMessageFlow) — diagnosing a hub that won't process messages.
