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

### 4. Turn loop — a single-threaded queue of `IObservable`, not a TPL `ActionBlock`

> **The core mechanism (a).** The inbox is a queue of **`IObservable<IMessageDelivery>`**
> (one per message — the lazy routing→gates→pipeline→handler chain for that delivery), NOT a
> queue of `Task`. The loop dequeues one, **starts it (`.Subscribe(...)`) with error handling**,
> and moves on. The queue itself is the single-thread guarantee — exactly one turn drains at a
> time — so the `ActionBlock` (whose only job was to serialize async continuations) is gone.

`MessageService` replaces the `ActionBlock`/`deferredBuffer`/`executionBuffer` Dataflow blocks
with a single lock-guarded FIFO queue and a re-entrancy-guarded drain loop:

```csharp
// q : Queue<Func<IObservable<IMessageDelivery>>>   — each item is the lazy turn for one delivery
void Post(IMessageDelivery d)
{
    lock (gate) { q.Enqueue(() => RouteAndDeliver(d)); }   // RouteAndDeliver returns IObservable
    Drain();                                                // no-op if a drain is already running
}

void Drain()
{
    lock (gate) { if (draining) return; draining = true; }  // one turn at a time, ever
    try
    {
        while (TryDequeue(out var turn))
            turn().Subscribe(                               // START the turn's observable
                _ => { },                                   // terminal: state was mutated INLINE
                ex => ReportFailure(ex));                   // error handling per turn
    }
    finally { lock (gate) { draining = false; } }
}
```

For a **synchronous** handler the turn's `IObservable` emits and completes *inline on
`Subscribe`*, so all of its hub-state mutation happens on this single thread before `Drain`
advances — strict FIFO, no overlap. A handler that `hub.Post`s to its own hub enqueues behind
the current turn (the `while` picks it up; `draining` blocks nested re-entry). The
deferral/gate machinery (`gates`, `ScheduleDeferralTimeout`, `ProcessDeferredMessage`) maps 1:1:
a deferred message is **re-enqueued** when its gate opens instead of `deferredBuffer.Post(...)`.

**Ordering invariant to preserve (the single hardest property):** one turn at a time, FIFO,
self-posts go to the back. Every message-flow test (`Messaging.Hub.Test`, hub-handler tests,
the Orleans propagation suite) exists to pin exactly this.

### 5. Promises for genuine async — `ReplaySubject` + `IIoPool` (mechanism b)

A handler must **never** block the turn thread on I/O. When it hits a genuinely-async leaf
(Postgres, blob, file, compile, a cross-hub round-trip), it **returns a promise — a
`ReplaySubject<T>(1)` — and outsources the async work to the [`IIoPool`](xref:Architecture/AsynchronousCalls)**.
The synchronous part of the turn completes immediately; the async result resolves later and
**re-enters the hub as a posted message**, so hub state is still only ever touched on the single
turn thread:

```csharp
IObservable<IMessageDelivery> Handle(IMessageDelivery<FooRequest> d)
{
    var promise = new ReplaySubject<Result>(1);     // the "promise": buffers the 1 result for
    _ioPool.Invoke(ct => DoIo(d.Message))           // late subscribers. async outsourced to pool.
           .Subscribe(promise);                      // pool thread pushes the result in

    // The continuation subscribes to the promise and POSTS the outcome back (re-enters the
    // queue on the turn thread — never mutates state from the pool thread). See
    // AsynchronousCalls.md "Subscribe callbacks post to the hub".
    promise.Subscribe(
        result => hub.Post(new FooResponse(result), o => o.ResponseFor(d)),
        ex     => hub.Post(new FooResponse(error: ex.Message), o => o.ResponseFor(d)));

    return Observable.Return(d.Processed());          // turn completes synchronously — loop advances
}
```

Why `ReplaySubject(1)` and not a plain `Subject`: the pool may resolve *before* the continuation
subscribes; `ReplaySubject(1)` buffers the single result so the late subscriber still observes it
(this is the "promise" semantics). It is also the bridge type for any caller that wants to
`hub.Observe(...)` the eventual answer — they get the value whenever they attach.

**The rule:** the turn thread runs only synchronous, in-memory work and *starts* observables;
every real wait is a `ReplaySubject` promise fed by an `IIoPool` leaf, never an `await` on the
loop. This is the same actor-model boundary as today (state on one thread, I/O off-thread,
results re-enter as messages) — just expressed in `IObservable` + a plain queue instead of
`Task` + a Dataflow `ActionBlock`.

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
