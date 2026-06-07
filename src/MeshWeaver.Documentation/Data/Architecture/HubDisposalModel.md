---
NodeType: Markdown
Name: "Hub Disposal Model"
Abstract: "How a MeshWeaver hub shuts down: disposal is SYNCHRONOUS and reactive end-to-end — Dispose() returns immediately, the Quiescing → DisposeHostedHubs → ShutDown → Dead state machine drives off the action block via Observable.Interval/Timer, and completion is a ReplaySubject (observe DisposalCompleted, never await). The only async on the whole path is the mesh-level IO-pool drain. Why every await here used to deadlock the action block, and the rule for adding disposal work."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00695c'/><path d='M7 6h10M9 6V5h6v1M8 6l1 12h6l1-12' fill='none' stroke='white' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Disposal"
  - "Lifecycle"
  - "Reactive"
---

# Hub Disposal Model

> **One rule:** **hubs dispose synchronously.** `Dispose()` is `void`, returns at
> once, and the whole shutdown state machine runs reactively (`IObservable`) — no
> `async`, no `await`, no `Task.Run`, no `Task.Delay`, no `TaskCompletionSource`,
> and **no `Task` anywhere on the disposal surface**. There is no `Disposal` task —
> the probe is `IsDisposing` (a `bool`) and completion is `DisposalCompleted` (an
> `IObservable<Unit>`). The **only** async that survives shutdown is the **draining
> of a genuine async pipeline** — the mesh-level **IO-pool drain** (`IIoPool`) and a
> reactive dispose action that flushes in-flight writes — and even those are
> expressed as `IObservable`, awaited *reactively* by the state machine. Everything
> else is an observable.
>
> For *debugging* a disposal that hangs or leaks, see
> [Debugging Disposal, Storms and Leaks](xref:Architecture/DebuggingDisposalAndLeaks).
> This page is the **model** — how shutdown is built and how to add to it.

---

## Why disposal can't `await`

A hub processes every message on a single-threaded `ActionBlock` (the actor turn).
`ShutdownRequest` is one of those messages. If the shutdown handler `await`s — for a
response to drain, for hosted hubs to finish, for a `Task.Delay` poll tick — it
**blocks the very thread that has to dequeue the thing it is waiting for.** That is
the same self-deadlock described in
[Asynchronous Calls](xref:Architecture/AsynchronousCalls), and it is exactly why
disposal used to wedge under load.

The fix is structural, not a bigger timeout: **the shutdown handler returns
immediately and the waits happen reactively, off the action block.**

```
Dispose()  (sync, void)                     ← caller never blocks
   │  posts ShutdownRequest(Quiescing)
   ▼
HandleShutdownCore  (sync IMessageDelivery) ← runs on the action block, returns at once
   ├─ Quiescing          → Observable.Interval poll (off-thread)
   │                       → drain reactive dispose actions (CombineLatest, bounded)
   │                       → posts DisposeHostedHubs
   ├─ DisposeHostedHubs  → subscribe hostedHubs.DisposalCompleted → posts ShutDown
   └─ ShutDown           → CancelCallbacks + DisposeImpl + messageService.Dispose() (sync)
                           → SignalDisposalCompleted()  → RunLevel = Dead
```

Each phase transition is a fresh `ShutdownRequest` posted back to the hub, so the
action block stays free between phases and the slow waits never sit on it.

---

## The completion source is a `ReplaySubject`, not a `TaskCompletionSource`

Disposal completion is published through a single
`ReplaySubject<Unit> disposalCompleted` (buffer 1), completed **exactly once** under
an `Interlocked` CAS guard:

```csharp
private readonly ReplaySubject<Unit> disposalCompleted = new(1);
private int disposalSignalled;   // 0→1 CAS — fire the subject once

private void SignalDisposalCompleted()
{
    if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0) return;
    disposalCompleted.OnNext(Unit.Default);
    disposalCompleted.OnCompleted();
}

private void SignalDisposalFaulted(Exception error)
{
    if (Interlocked.CompareExchange(ref disposalSignalled, 1, 0) != 0) return;
    disposalCompleted.OnError(error);
}
```

`ReplaySubject(1)` is what makes this safe for **late subscribers**: anyone who
attaches *after* disposal has already finished still receives the terminal
notification immediately. There is no `TaskCompletionSource` and no
`Task.ToObservable()` bridge anywhere on the path — the subject is the source of
truth.

### There is no `Disposal` Task — `IsDisposing` + `DisposalCompleted`

The hub exposes exactly two disposal surfaces, neither a `Task`:

| Surface | Shape | Answers |
|---|---|---|
| `IMessageHub.IsDisposing` | `bool` (a flag set the moment `Dispose()` begins) | "Is this hub shutting down?" — the routing/stream "is-shutting-down" guards. |
| `IMessageHub.DisposalCompleted` | `IObservable<Unit>` (the native subject) | "Tell me when it's done." — subscribe; the OnNext+OnCompleted fires when disposal finishes. |

Application / hub-reachable code **subscribes** to `DisposalCompleted`; it never
awaits. The old `Task? Disposal` property is **gone** — a `Disposal is not null`
check becomes `IsDisposing`, and an `await hub.Disposal` becomes a subscription.

**At a genuine async edge** — xUnit teardown, `MessageHubGrain.OnDeactivateAsync` —
where a `Task` is legitimately the calling convention, bridge the observable *once*:

```csharp
// Grain deactivation / test teardown — the ONLY place a Task appears, at the edge:
await hub.DisposalCompleted
    .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))  // fault → "done"
    .FirstOrDefaultAsync()
    .ToTask(ct);
```

That `.ToTask()` is the framework-lifecycle Task boundary (the same place tests
`await`). Nowhere inside `src/` hub-reachable code does it appear.

---

## The phases

### Quiescing — drain pending response callbacks (reactive poll)

On entry the hub may still have `Observe(...)` response subjects awaiting replies.
Quiescing gives them a bounded budget (`Configuration.QuiesceTimeout`, default 2 s)
to drain. The wait is an `Observable.Interval` poll on the default scheduler — **off
the action block** — so responses keep being dequeued while we watch them clear:

```csharp
var drained = Observable
    .Interval(QuiescePollInterval)                 // ticks off the action block
    .StartWith(-1L)                                // probe once inline: already-drained → no hop
    .Select(_ => { lock (responseSubjects) return responseSubjects.Count == 0; })
    .Where(empty => empty)
    .Take(1)
    .Select(_ => true);
var quiesceDeadline = Observable.Timer(QuiesceTimeout).Select(_ => false);
quiescingSubscription = drained
    .Amb(quiesceDeadline)                          // first to fire wins: drained=true, deadline=false
    .Take(1)
    .Subscribe(drainedOk => OnQuiesceComplete(drainedOk, …));
```

> **Why `Amb` and not `.Timeout()`:** `Observable.Interval` emits every
> `QuiescePollInterval`, so a *between-emissions* `.Timeout(QuiesceTimeout)` never
> trips (the gap is always 50 ms, never 2 s). The deadline must be a **separate
> total-duration `Observable.Timer`**, raced against the drain signal with `Amb`.

If the budget elapses with callbacks still pending, the hub sets the sticky
`QuiescingTimedOut` flag and force-cancels them. **Tests treat
`AnyHubQuiescingTimedOut()` as a dispose failure** — a leaked `Observe` subscription
that never got its reply is a real bug, not a teardown oddity. Either path then
runs the **reactive dispose-action drain** before posting `DisposeHostedHubs`.

### Dispose-action drain — await the registered cleanups reactively

`OnQuiesceComplete` drains the reactive dispose actions registered via
`RegisterForDisposal(Func<IMessageHub, IObservable<Unit>>)` and only advances to
`DisposeHostedHubs` once they **all complete** (or a `DisposeActionDrainTimeout` cap
elapses). This is where a cleanup that *must finish before teardown* — e.g. a final
persistence flush of in-flight writes — is awaited, **reactively**:

```csharp
var completions = actions.Select(action =>
    action(this)
        .DefaultIfEmpty(Unit.Default)              // each contributes exactly one emission…
        .Take(1)
        .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default)));  // …so one fault can't stall the join
disposeActionsSubscription = Observable
    .CombineLatest(completions).Take(1)
    .Timeout(DisposeActionDrainTimeout)
    .Subscribe(_ => Advance(timedOut: false), _ => Advance(timedOut: true));
```

The action block is never blocked — `CombineLatest` subscribes the flush observables
on the scheduler and `Advance` posts `DisposeHostedHubs` when they converge.

### DisposeHostedHubs — join the children reactively

The hub disposes its `HostedHubsCollection` (each child disposes synchronously) and
**observes** the collection's completion — no `await hostedHubs.Disposal`,
no `Task.Run`:

```csharp
hostedHubs.Dispose();
hostedHubsDisposalSubscription = hostedHubs.DisposalCompleted
    .Take(1)
    .Subscribe(_ => { }, _ => PostShutDownPhase(sw), () => PostShutDownPhase(sw));
```

`HostedHubsCollection` itself is reactive: it disposes each child, then joins their
`DisposalCompleted` streams with `Observable.CombineLatest` (per-child `Catch` so one
wedged child can't stall the join) under a 10 s `Timeout`, and completes its own
`ReplaySubject`. On completion **or** the cap, the owner advances to ShutDown — a
hung child never blocks the parent.

### ShutDown — tear down and signal

Runs on the action block, fully synchronous: `CancelCallbacks()` (push
`ObjectDisposedException` to any still-pending subjects), `DisposeImpl()` (run the
registered sync dispose actions), `messageService.Dispose()` (**sync** —
`IMessageService : IDisposable`), then `SignalDisposalCompleted()` and
`RunLevel = Dead`. The disposal-phase subscriptions are disposed in the `finally`
(each has already self-completed).

### The watchdog — `Observable.Timer`, not `Task.Delay`

A safety net force-completes disposal if the state machine ever wedges. It is a
reactive timer that **cancels itself the instant disposal completes**:

```csharp
watchdogSubscription = Observable
    .Timer(DisposalWatchdogTimeout)            // 25 s, default scheduler (off action block)
    .TakeUntil(disposalCompleted)              // cancel the moment disposal finishes
    .Subscribe(_ => { if (!DisposalSignalled) SignalDisposalCompleted(); }, _ => { });
```

`TakeUntil(disposalCompleted)` is what fixed the **TimerQueue leak**: the old
uncancelled `Task.Delay(25s)` rooted the entire hub graph (cache, data sources,
action block, subscriptions) for 25 s after *every* dispose, even a fast one. The
reactive timer releases its scheduler entry as soon as the subject fires.

---

## The one async carve-out: draining a genuine async pipeline

The **only** thing that stays async in shutdown is the *draining of a genuine async
pipeline* — and even it is expressed as `IObservable`, awaited reactively:

1. **Mesh-level IO pools** (`IIoPool` / `IoPoolRegistry`) — the in-flight DB / blob /
   file / HTTP work. These drain at the mesh boundary, not per hub. The sanctioned
   async edge (see [Controlled IO Pooling](xref:Architecture/ControlledIoPooling)).
2. **A reactive dispose action that flushes** — e.g. `MeshNodeTypeSource` registers
   `RegisterForDisposal(hub => FlushPendingWrites().Timeout(10s)…)`. This **must**
   finish before teardown (a per-node hub disposing mid-write would otherwise lose
   data and the next test could read stale persistence). It is awaited by the
   dispose-action drain above — reactively, never on the action block, never as a
   `Task`. `FlushPendingWrites()` is *already* an `IObservable`; the leaf I/O inside
   it pools through the persistence layer.

There is no `Func<…, Task>` dispose action and no `IAsyncDisposable` registration any
more — the `RegisterForDisposal` surface takes `Func<IMessageHub, IObservable<Unit>>`.
Everything else — the state machine, the polls, the joins, the completion signal — is
an observable.

---

## Adding disposal work — the rule

- **Need to run sync cleanup on dispose?** `hub.RegisterForDisposal(IDisposable)`
  (the common case) or `RegisterForDisposal(Action<IMessageHub>)`. These run in the
  ShutDown phase on the action block.
- **Need cleanup that must finish before teardown (an async flush)?**
  `hub.RegisterForDisposal(Func<IMessageHub, IObservable<Unit>>)` — return an
  observable that completes when done; the dispose-action drain awaits it reactively.
  There is no `Func<…, Task>` overload and no `IAsyncDisposable` overload.
- **Need to wait for the hub to finish disposing?** Subscribe to
  `hub.DisposalCompleted`. Only at the test / grain edge may you bridge it once with
  `.FirstOrDefaultAsync().ToTask()`. To ask "is it shutting down?", read `IsDisposing`.
- **Tempted to `await` something during disposal?** Don't — it deadlocks the action
  block. Express the wait as an `Observable` (`Interval` poll, `Timer`/`Amb` deadline,
  subscribe to a child's `DisposalCompleted`) and post the next phase from its
  terminal callback, exactly as the phases above do.
- **Tempted to add a `TaskCompletionSource` to signal "done"?** That is the smell
  the `ReplaySubject` replaced. Use a subject and a CAS-guarded `Signal…` helper.

Canonical implementation: `MessageHub.HandleShutdownCore` /
`MessageHub.Dispose` / `HostedHubsCollection.DisposeHubsReactive` in
`src/MeshWeaver.Messaging.Hub`.
