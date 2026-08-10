---
Name: Observable Hub Pipeline (migration design)
Description: "Target architecture and staged plan for converting the message-hub delivery pipeline from TPL-Dataflow + Task-based AsyncDelivery to a synchronous single-threaded queue with IObservable handlers end-to-end; async survives only at IIoPool leaves."
---

# Observable Hub Pipeline — Migration Design

> **Status: LANDED (stages 1–3).** This page was written as a migration design; the
> migration has since shipped, so read it as **the record of a completed change plus the
> invariants it must preserve**, not as future work. Verify against the code before
> quoting any snippet below — several were written as *targets* and the shipped shape
> differs in detail (notably: `AsyncDelivery` kept its NAME and its `CancellationToken`
> parameter, and the turn loop trampolines through `TaskScheduler` rather than draining
> in a bare `while`).
>
> What is true of the live pipeline today:
>
> - `AsyncDelivery` (`Messaging.Contract/IMessageHandler.cs`) is
>   `delegate IObservable<IMessageDelivery> (IMessageDelivery, CancellationToken)` — **not**
>   `Task<IMessageDelivery>`. `SyncDelivery` still returns `IMessageDelivery` and lifts via
>   `Observable.Return`.
> - `MessageService` has **no TPL Dataflow blocks**. The inbox is
>   `Queue<Func<IObservable<IMessageDelivery>>> mainQueue` (plus a `deferredQueue` for
>   gate-deferred turns) guarded by a `Lock turnGate` + a `draining` re-entrancy flag.
> - `MessageHub.HandleMessageAsync` folds the rule chain with `SelectMany` over an
>   `Observable.Return(delivery)` seed and returns `IObservable<IMessageDelivery>`.
> - `RouteConfiguration.Handlers` is `ImmutableList<AsyncDelivery>` (now observable-typed);
>   the old `.FirstAsync().ToTask(ct)` bridge is **deleted** — `RouteConfiguration`'s own
>   doc comment says callers "must NOT bridge manually with `.FirstAsync().ToTask()`".
>
> Still `Task`-shaped by design (stage 4's remainder): `HierarchicalRouting.RouteMessageAsync`
> is **synchronous** (`IMessageDelivery` in, `IMessageDelivery` out — it never became
> `IObservable`), and the Orleans grain boundary stays `Task` because that is the silo
> contract.

## Why

The hub already exposed `IObservable<T>` on its public surface (`IRoutingService.DeliverMessage`,
`RoutingServiceBase`, `hub.Observe(...)`) while the **internal** delivery pipeline was still
`Task`-based. The `ActionBlock` existed for exactly one reason: to **serialize asynchronous
handler continuations** onto a single logical thread. Once every handler is a *synchronous*
`IObservable` that completes inline on `Subscribe` (see
[AsynchronousCalls.md](/Doc/Architecture/AsynchronousCalls)), there are no async
continuations to serialize, and a plain lock-guarded queue (one turn at a time) is
sufficient and simpler. Genuine async (Postgres, blob, file, compile) is isolated
behind [`IIoPool`](/Doc/Architecture/ControlledIoPooling) — those leaves stay async and bridge
to `IObservable` at the pool, never on the turn loop.

## Target architecture (as designed — see the status note for what shipped)

### 1. Handler delegate — `IObservable`, not `Task`

The design proposed renaming the delegate to `DeliveryHandler` and dropping the
`CancellationToken`. **What shipped keeps both the name and the token:**

```csharp
// Messaging.Contract/IMessageHandler.cs — the ACTUAL shipped shape
public delegate IObservable<IMessageDelivery> AsyncDelivery(IMessageDelivery request, CancellationToken cancellationToken);
public delegate IObservable<IMessageDelivery> AsyncDelivery<in TMessage>(IMessageDelivery<TMessage> request, CancellationToken cancellationToken);
public delegate IMessageDelivery SyncDelivery(IMessageDelivery request);   // unchanged — lifts via Observable.Return
public delegate IObservable<IMessageDelivery> AsyncRouteDelivery(Address routeAddress, IMessageDelivery request, CancellationToken cancellationToken);
```

So `AsyncDelivery` / `AsyncRouteDelivery` were **retyped, not deleted** — the return type
changed from `Task<IMessageDelivery>` to `IObservable<IMessageDelivery>`. Do not go looking
for a `DeliveryHandler` type; it does not exist.

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

The observable-handler `WithHandler` now stores the handler directly and the
`.FirstAsync().ToTask()` bridge **is** deleted. The reactive fold landed in
`MessageHub.HandleMessageAsync` (a `foreach` accumulating `result.SelectMany(...)` over a
snapshot of the rule chain), **not** in routing:
`HierarchicalRouting.RouteMessageAsync` remained a synchronous
`IMessageDelivery RouteMessageAsync(IMessageDelivery, CancellationToken)`.

### 4. Turn loop — a single-threaded queue of `IObservable`, not a TPL `ActionBlock`

> **The core mechanism (a).** The inbox is a queue of **`IObservable<IMessageDelivery>`**
> (one per message — the lazy routing→gates→pipeline→handler chain for that delivery), NOT a
> queue of `Task`. The loop dequeues one, **starts it (`.Subscribe(...)`) with error handling**,
> and moves on. The queue itself is the single-thread guarantee — exactly one turn drains at a
> time — so the `ActionBlock` (whose only job was to serialize async continuations) is gone.

`MessageService` replaced the `ActionBlock`/`deferredBuffer`/`executionBuffer` Dataflow blocks
with `mainQueue` + `deferredQueue` (both `Queue<Func<IObservable<IMessageDelivery>>>`), a
`Lock turnGate`, and a `draining` re-entrancy flag. The shipped drain
(`EnqueueTurn` / `KickDrain` / `ScheduleDrainOne` / `DrainOne`) differs from the sketch below
in one important way: **each drain run is started on `turnScheduler`**
(`Task.Factory.StartNew(DrainOne, …, TaskCreationOptions.DenyChildAttach, turnScheduler)`), so
a handler still observes `TaskScheduler.Current == ` the hub's configured scheduler — the
invariant `WithTaskScheduler` and the Orleans grain hub depend on. `DrainOne` then
**trampolines**: a synchronous turn completes inline during `Subscribe` and the loop picks up
the next turn on the same pool task; only a genuinely-async turn returns before completing, and
its terminal callback re-schedules the drain.

```csharp
// Sketch of the design (the shipped code adds the turnScheduler hop + trampoline described above)
void Post(IMessageDelivery d)
{
    lock (gate) { q.Enqueue(() => RouteAndDeliver(d)); }   // RouteAndDeliver returns IObservable
    Drain();                                                // no-op if a drain is already running
}

void Drain()
{
    lock (gate) { if (draining) return; draining = true; }  // one turn at a time, ever
    while (TryDequeue(out var turn))
        turn().Subscribe(                                   // START the turn's observable
            _ => { },                                       // terminal: state was mutated INLINE
            ex => ReportFailure(ex));                       // error handling per turn
}
```

For a **synchronous** handler the turn's `IObservable` emits and completes *inline on
`Subscribe`*, so all of its hub-state mutation happens on this single thread before the drain
advances — strict FIFO, no overlap. A handler that `hub.Post`s to its own hub enqueues behind
the current turn (`draining` blocks nested re-entry). The
deferral/gate machinery (`gates`, `ScheduleDeferralTimeout`, `ProcessDeferredMessage`) maps 1:1:
a deferred message is **re-enqueued** when its gate opens instead of `deferredBuffer.Post(...)`.

**Ordering invariant to preserve (the single hardest property):** one turn at a time, FIFO,
self-posts go to the back. Every message-flow test (`Messaging.Hub.Test`, hub-handler tests,
the Orleans propagation suite) exists to pin exactly this.

### 5. Promises for genuine async — `ReplaySubject` + `IIoPool` (mechanism b)

A handler must **never** block the turn thread on I/O. When it hits a genuinely-async leaf
(Postgres, blob, file, compile, a cross-hub round-trip), it **returns a promise — a
`ReplaySubject<T>(1)` — and outsources the async work to the [`IIoPool`](/Doc/Architecture/AsynchronousCalls)**.
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

## Staged plan (historical — stages 1–3 are merged; stage 4 is partly done)

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

## Docs that describe the shipped pipeline

- [AsynchronousCalls.md](/Doc/Architecture/AsynchronousCalls) — the routing/pipeline section.
- [MessageBasedCommunication.md](/Doc/Architecture/MessageBasedCommunication) — handler return type.
- [OrleansTaskScheduler.md](/Doc/Architecture/OrleansTaskScheduler) — how `turnScheduler` is
  resolved and why the grain hub needs the scheduler hop the trampoline preserves.
