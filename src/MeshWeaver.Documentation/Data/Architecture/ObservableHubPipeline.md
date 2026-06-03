---
Name: Observable Hub Pipeline (migration design)
Description: "Target architecture and staged plan for converting the message-hub delivery pipeline from TPL-Dataflow + Task-based AsyncDelivery to a synchronous single-threaded queue with IObservable handlers end-to-end; async survives only at IIoPool leaves."
---

# Observable Hub Pipeline — Migration Design

> **Status: DESIGN / NOT YET IMPLEMENTED.** This is the target for converting the
> message-hub core away from `Task`-based handlers + TPL Dataflow to `IObservable`
> end-to-end with a synchronous single-threaded turn loop. Until it ships, the live
> pipeline is still `AsyncDelivery` (`Task<IMessageDelivery>`) driven by a Dataflow
> `ActionBlock` (`MessageService`). Do not describe the system as observable-core
> until each stage below is merged and green.

## Why

The hub already exposes `IObservable<T>` on its public surface (`IRoutingService.DeliverMessage`,
`RoutingServiceBase`, `hub.Observe(...)`), but the **internal** delivery pipeline is still
`Task`-based:

- `AsyncDelivery` (`Messaging.Contract/IMessageHandler.cs`) = `delegate Task<IMessageDelivery> (IMessageDelivery, CancellationToken)`.
- `MessageHubConfiguration.AsyncPipelineConfig` composes handlers as `async (d, ct, next) => await next(...)`.
- `MessageService` drives delivery through a TPL **`ActionBlock`** and `await`s routing
  (`hierarchicalRouting.RouteMessageAsync`) and the pipeline (`deliveryPipeline.Invoke`).
- `RouteConfiguration.Handlers` is `ImmutableList<AsyncDelivery>`; the observable-handler
  form bridges back to `Task` with `.FirstAsync().ToTask(ct)` (`RouteConfiguration.cs:27`).

The `ActionBlock` exists for exactly one reason: to **serialize asynchronous handler
continuations** onto a single logical thread. Once every handler is a *synchronous*
`IObservable` that completes inline on `Subscribe` (the case after this migration — see
[AsynchronousCalls.md](xref:Architecture/AsynchronousCalls) rule #1 and #9), there are no
async continuations to serialize, and a plain lock-guarded queue (one turn at a time) is
sufficient and simpler. Genuine async (Postgres, blob, file, compile) is already isolated
behind [`IIoPool`](xref:Architecture/AsynchronousCalls) — those leaves stay async and bridge
to `IObservable` at the pool, never on the turn loop.

## Target architecture

### 1. Handler delegate — `IObservable`, not `Task`

```csharp
// Messaging.Contract/IMessageHandler.cs
public delegate IObservable<IMessageDelivery> DeliveryHandler(IMessageDelivery request);
public delegate IObservable<IMessageDelivery> DeliveryHandler<in TMessage>(IMessageDelivery<TMessage> request);
public delegate IMessageDelivery SyncDelivery(IMessageDelivery request);   // unchanged — lifts via Observable.Return
```

`AsyncDelivery` / `AsyncRouteDelivery` are deleted. The `CancellationToken` parameter goes
away from the handler surface (cancellation is a subscription concern — `Timeout`, dispose).

### 2. Pipeline composition — `SelectMany`, not `await next`

```csharp
// MessageHubConfiguration pipeline link
this with { Handler = d => pipeline(d, Handler) };          // pipeline: (d, next) => IObservable<...>
// a pass-through link:
(d, next) => Precheck(d) is {} stop ? Observable.Return(stop) : next(d);
```

Sync handlers: `WithHandler<T>((h,d) => result)` → `d => Observable.Return(handler(h,d))`.
Genuine-async handlers: the body returns `ioPool.Invoke(...)`/`hub.Observe(...)` — already `IObservable`.

### 3. Routing — reactive fold

`HierarchicalRouting.RouteMessage` returns `IObservable<IMessageDelivery>`; the handler loop
becomes a reactive fold (`Handlers.Aggregate(Observable.Return(delivery), (acc, h) => acc.SelectMany(h))`)
then `.Select(RouteAlongHostingHierarchy)`. The observable-handler `WithHandler` stores the
handler directly — the `.FirstAsync().ToTask()` bridge is deleted.

### 4. Turn loop — synchronous queue, no TPL Dataflow

`MessageService` replaces the `ActionBlock`/`deferredBuffer`/`executionBuffer` Dataflow blocks
with a single lock-guarded FIFO queue and a drain loop:

```
Post(delivery):
    lock(q): q.Enqueue(delivery)
    if (!draining) Drain()        // re-entrancy guarded; one turn at a time

Drain():
    while (q.TryDequeue(d)):
        Route(d)                  // IObservable; subscribe-inline, completes synchronously
          .Subscribe(processed => { /* gates/deferral/pipeline as today, but sync */ },
                     ex => ReportFailure(d, ex))
```

The deferral/gate machinery (`gates`, `deferredBuffer`, `ScheduleDeferralTimeout`,
`ProcessDeferredMessage`) maps 1:1 onto the queue — a deferred message is re-enqueued when its
gate opens, instead of `deferredBuffer.Post(...)`. **Ordering invariant to preserve:** a hub
processes exactly one turn at a time, FIFO, and a handler that posts to *itself* enqueues
behind the current turn (never re-enters). This is the single hardest property to get right —
every message-flow test (`Messaging.Hub.Test`, hub-handler tests, the Orleans propagation
suite) exists to pin it.

## Staged plan (green CI gate per stage — do NOT merge a red stage)

1. **Routing chain** — `RouteConfiguration.Handlers` + `HierarchicalRouting` → `IObservable`;
   keep a thin `Task` bridge at the `MessageService` edge so this stage is self-contained.
   Gate: `MeshWeaver.Messaging.Hub.Test`.
2. **Handler delegate + pipeline** — `AsyncDelivery` → `DeliveryHandler` (`IObservable`),
   `MessageHubConfiguration` pipeline links via `SelectMany`, every `WithHandler` overload +
   the ~41 registration sites. Sync handlers via `Observable.Return`; async bodies via `IIoPool`.
   Gate: full hub + handler test suites.
3. **Turn loop** — delete the TPL Dataflow blocks in `MessageService`; replace with the
   lock-guarded queue + drain loop; remove the `Task` bridge from stage 1. Gate: the FULL suite,
   incl. Orleans propagation + deferral/gate tests, run repeatedly to shake out ordering races.
4. **Sweep** — `OrleansRoutingService` / `MonolithRoutingService` / `RoutingGrain` (the Orleans
   grain boundary stays `Task` — that's the silo contract, see AsynchronousCalls.md line ~187),
   `IMessageHandlerRegistry`, and any remaining `Task`-returning hub-reachable surface.

## Preconditions

- **A green CI baseline.** A reactive rewrite of the turn loop cannot be verified against a
  flaky-red suite — you can't distinguish a broken-ordering regression from a shard flake.
- **A dedicated branch** off a green point, NOT the currently-deployed branch. Stabilize +
  verify the branch end-to-end before merge/deploy.

## Docs to update prominently when each stage lands

- [AsynchronousCalls.md](xref:Architecture/AsynchronousCalls) — the routing/pipeline section,
  and the line ~187 table (routing moves from "leave as-is" to "observable by composition").
- [MessageBasedCommunication.md](xref:Architecture/MessageBasedCommunication) — handler return type.
- `CLAUDE.md` reactive-pattern rules — `DeliveryHandler` replaces `AsyncDelivery`.
