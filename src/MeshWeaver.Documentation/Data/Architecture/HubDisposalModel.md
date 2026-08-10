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
> reactive dispose action that flushes in-flight writes. Those are expressed as
> `IObservable` too, but note *who* waits for them: the hub **starts** them and does
> not block, and it is the **mesh-level** teardown
> ([Mesh Lifecycle](/Doc/Architecture/MeshLifecycle)) that joins them before the
> service scope closes. Everything else is an observable.
>
> For *debugging* a disposal that hangs or leaks, see
> [Debugging Disposal, Storms and Leaks](/Doc/Architecture/DebuggingDisposalAndLeaks).
> This page is the **model** — how shutdown is built and how to add to it.

---

## Why disposal can't `await`

A hub processes every message on a single-threaded `ActionBlock` (the actor turn).
`ShutdownRequest` is one of those messages. If the shutdown handler `await`s — for a
response to drain, for hosted hubs to finish, for a `Task.Delay` poll tick — it
**blocks the very thread that has to dequeue the thing it is waiting for.** That is
the same self-deadlock described in
[Asynchronous Calls](/Doc/Architecture/AsynchronousCalls), and it is exactly why
disposal used to wedge under load.

The fix is structural, not a bigger timeout: **the shutdown handler returns
immediately and the waits happen reactively, off the action block.**

```
Dispose()  (sync, void)                     ← caller never blocks
   │  hostedHubs.CloseCreation()  (freezes the whole subtree, first)
   │  messageService.CancelExecution()
   │  posts ShutdownRequest(Quiescing)
   │  arms the reactive watchdog (Observable.Timer, TakeUntil(disposalCompleted))
   ▼
HandleShutdownCore  (sync IMessageDelivery) ← runs on the action block, returns at once
   ├─ Quiescing          → Observable.Interval poll (off-thread), Amb'd with a
   │                       Observable.Timer(QuiesceTimeout) deadline
   │                       → OnQuiesceComplete → posts DisposeHostedHubs
   ├─ DisposeHostedHubs  → hostedHubs.Dispose() + subscribe hostedHubs.DisposalCompleted
   │                       → posts ShutDown
   └─ ShutDown           → CancelCallbacks + DisposeImpl + messageService.Dispose() (sync)
                           → RunLevel = Dead → SignalDisposalCompleted()
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
`QuiescingTimedOut` flag, records `QuiescingTimeoutDetail` and force-cancels them.
**Tests treat `AnyHubQuiescingTimedOut()` as a dispose failure** — a leaked `Observe`
subscription that never got its reply is a real bug, not a teardown oddity. Either
path then posts `DisposeHostedHubs` from `OnQuiesceComplete`'s `finally`.

> **There is no separate "dispose-action drain" phase.** Registered cleanups —
> including the reactive `Func<IMessageHub, IObservable<Unit>>` ones — are run in the
> **ShutDown** phase by `DisposeImpl`, and the reactive ones are **fired and not
> awaited** (see the next-but-one section). Nothing between Quiescing and
> DisposeHostedHubs waits for them.

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

`HostedHubsCollection` itself is reactive (`DisposeHubsReactive`): it disposes each
child, then joins their `DisposalCompleted` streams with `Observable.CombineLatest`
(per-child `Catch` so one wedged child can't stall the join) under a **5 s** `Timeout`,
and completes its own `ReplaySubject`. It joins one extra leg — an **in-flight-creation
drain** that waits for `inflightCreations` to reach zero and then disposes whatever a
late construction produced, so a hub built during the teardown window is never leaked
outside the snapshot. On completion **or** the cap, the owner advances to ShutDown — a
hung child never blocks the parent.

### ShutDown — tear down and signal

Runs on the action block, fully synchronous: `CancelCallbacks()` (push
`ObjectDisposedException` to any still-pending subjects), `DisposeImpl()` (run the
registered sync dispose actions), `messageService.Dispose()` (**sync** —
`IMessageService : IDisposable`), then `SignalDisposalCompleted()` and
`RunLevel = Dead`. The disposal-phase subscriptions are disposed in the `finally`
(each has already self-completed).

### The watchdog — `Observable.Timer`, and it tears down for real

A safety net runs the teardown if the state machine ever wedges. It is a reactive
timer that **cancels itself the instant disposal completes**:

```csharp
watchdogSubscription = Observable
    .Timer(DisposalWatchdogTimeout)            // 8 s, default scheduler (off action block)
    .TakeUntil(disposalCompleted)              // cancel the moment disposal finishes
    .Subscribe(
        _ => { if (!DisposalSignalled) ForceTeardownAfterWatchdog(); },
        _ => { });                             // a faulted disposalCompleted needs no watchdog
```

Two things matter here, and both were once wrong:

- **It force-*tears down*, it does not merely signal.** `ForceTeardownAfterWatchdog`
  flips `RunLevel` to `ShutDown` first (so heartbeats and hosted-hub creation stop
  feeding the storm), then runs the SAME teardown the phases would have —
  `hostedHubs.Dispose()`, `CancelCallbacks()`, `DisposeImpl()`,
  `messageService.Dispose()` — each in its own `try/catch`, then sets `Dead` and
  signals. The predecessor only **signalled** completion here: the caller unblocked
  and every child leaked, which is how one dead Blazor circuit's portal hub kept
  ~7k sync-stream hubs alive heartbeating at ~1.2 cores forever (the 2026-07-01
  zombie portal-hub storm).
- **`TakeUntil(disposalCompleted)` is what fixed the TimerQueue leak.** The old
  uncancelled `Task.Delay(25 s)` rooted the entire hub graph (cache, data sources,
  action block, subscriptions) for 25 s after *every* dispose, even a fast one. The
  reactive timer releases its scheduler entry as soon as the subject fires. (The
  current budget is `DisposalWatchdogTimeout` = **8 s**; the 25 s figure is the
  deleted `Task.Delay`, not today's bound.)

---

## The one async carve-out: draining a genuine async pipeline

The **only** thing that stays async in shutdown is the *draining of a genuine async
pipeline* — and even it is expressed as `IObservable`, awaited reactively:

1. **Mesh-level IO pools** (`IIoPool` / `IoPoolRegistry`) — the in-flight DB / blob /
   file / HTTP work. These drain at the mesh boundary, not per hub. The sanctioned
   async edge (see [Controlled IO Pooling](/Doc/Architecture/ControlledIoPooling)).
2. **A reactive dispose action that flushes** — e.g. `MeshNodeTypeSource` registers
   `RegisterForDisposal(_ => FlushPendingWrites().Timeout(10s)…)` so a per-node hub
   disposing mid-write doesn't lose data. `FlushPendingWrites()` is *already* an
   `IObservable`; the leaf I/O inside it pools through the persistence layer, which is
   **mesh-scoped and outlives this hub**.

   > 🚨 **The hub does NOT wait for it.** `DisposeImpl` composes the registered
   > reactive actions with `Observable.Merge(legs).Subscribe(…)` and moves straight on
   > to `disposables.Dispose()` — fire-and-forget, deliberately not held in
   > `disposables` so the synchronous teardown can't cancel an in-flight pool leaf.
   > What actually guarantees the flush has landed before the service scope dies is the
   > **mesh-level teardown**, not the hub: `IoPoolRegistry.DrainAll()` +
   > `AsyncDisposeQueue.DrainAsync(...)` in
   > [Mesh Lifecycle](/Doc/Architecture/MeshLifecycle). Do not rely on
   > `DisposalCompleted` alone to mean "my flush finished".

There is no `Func<…, Task>` dispose action and no `IAsyncDisposable` registration any
more — the `RegisterForDisposal` surface takes `Func<IMessageHub, IObservable<Unit>>`.
Everything else — the state machine, the polls, the joins, the completion signal — is
an observable.

---

## Teardown-safe writes: `Post` drops, incoming streams error

Disposal is reactive and bounded, but it is not instantaneous — and **background
producers don't observe the action block.** A `FileSystemWatcher`, a remote sync
subscription, or a timer can fire a write *while the hub is tearing down*, after the
Autofac `LifetimeScope` (the hub's `ServiceProvider`) has already been disposed. That
write used to crash the process: it reached `stream.Update` → `CaptureCallerAccessContext`
→ `hub.ServiceProvider.GetService<AccessService>()` on a disposed scope → Autofac throws
`ObjectDisposedException` synchronously on the **producer's threadpool thread**, with no
observer → xUnit `[FATAL ERROR]` / a prod `Catastrophic`. Three layers make this safe, in
order of how early they stop the work:

1. **Close the incoming stream at the source.** A producer wrapper disposes its source
   *and* flips a `volatile` guard that in-flight callbacks check, so a callback already
   dispatched on a threadpool thread no-ops instead of pushing into a disposed hub.
   Canonical: `FileSystemStreamProvider.WatcherHandle` — `Dispose()` sets `stopped = true`,
   then `EnableRaisingEvents = false`, then disposes the watcher; every event handler
   early-returns on `handle.Stopped`.

2. **Incoming streams error on a disposing target.** A write to a dead/disposed
   `SynchronizationStream` does **not** silently no-op — it errors back to the producer
   via its `exceptionCallback` with an `ObjectDisposedException`
   (`SynchronizationStream.SignalDisposedToProducer`). The producer reacts by tearing down
   its own source — e.g. `ContentCollection.UpdateArticle`'s callback disposes the
   monitor — so the feed stops at the root rather than retrying into the void.

3. **`Post` is teardown-safe.** `MessageService.Post` short-circuits to a dropped
   (`Failed`) delivery **before** invoking the post pipeline once
   `RunLevel >= DisposeHostedHubs` (mirroring `ScheduleNotify`'s existing drop, just hoisted
   ahead of the pipeline). The pipeline stamps `AccessContext` by resolving from the
   `ServiceProvider`; running it during teardown is what threw. For live hubs this is a
   no-op — `ScheduleNotify` already drops these messages — so there is **zero behavioral
   change** except that the throwing pipeline never runs during teardown.
   `SynchronizationStream.CaptureCallerAccessContext` additionally swallows a disposed-scope
   `ObjectDisposedException` (returning `null`, its documented no-context path) for the
   narrow window where the scope is gone but the stream isn't yet marked disposed.

> **The principle:** a teardown is a terminal signal that must propagate *outward* to
> producers — silently dropping their writes leaves them spinning, and letting their write
> throw kills the process. Error the write, let the producer stop, and make the drop layers
> below it inert. Repros: `DeadStreamSafetyTest.Update_OnDisposedStream_SignalsDisposedToProducer`
> and `Post_OnDisposingHost_DropsWithoutInvokingPipeline`.

## The creation window: a disposing hub accepts work it can no longer perform

There is a gap the phases above open by design, and every stream-creating request falls
into it:

- **Hosted-hub creation is frozen on the FIRST statement of `Dispose()`**
  (`HostedHubsCollection.CloseCreation`), *before* the `ShutdownRequest` that moves
  `RunLevel` off `Started` is even posted — and the freeze **cascades through the whole
  subtree**, so a child hub is frozen while its own `RunLevel` still reads `Started`.
- **Message intake stays open until `DisposeHostedHubs`** — `ScheduleNotify`'s shutdown
  gate. The entire `Quiescing` drain (up to `QuiesceTimeout`) sits inside the gap.

So from the first instant of disposal until several phases later the hub **accepts requests
it structurally cannot serve**. A `SubscribeRequest` for a layout area is the canonical one:
serving it means constructing a `SynchronizationStream`, and a synchronization stream owns a
hosted sub-hub.

**The rule: refuse, typed and transient — never fabricate a half-built object.**
`SynchronizationStream`'s constructor throws `HubDisposingException` (an
`ObjectDisposedException`, so teardown-aware callers such as Blazor's
`catch (ObjectDisposedException)` around `BindStream()` keep working), and
`JsonSynchronizationStream.CreateExternalClient` throws the same type for the same reason.
`MessageService` then classifies any handler exception that *is or wraps* one as
`ErrorType.ShuttingDown` — the same transient "the address may reactivate, ask again" answer
the intake and deferred-queue NACKs give — so `SynchronizationStream`'s keep-alive and
change-feed resubscribe latch **stay armed** and the subscriber rehydrates after the recycle.

> The predecessor built a "dead stream" here instead: `isDisposed`, completed store, and
> `Hub = null!`, with a comment requiring every consumer to go through `TryGetActiveHub`. No
> consumer did — `grep -rn TryGetActiveHub src` matched only the stream itself, against ~96
> sites dereferencing the interface's **non-nullable** `Hub` — and `ISynchronizationStream`
> exposes no liveness member for a consumer to check even if it wanted to. The result was a
> `NullReferenceException` in `LayoutAreaHost`'s constructor whenever a page subscribed during
> a recycle (the overlay self-heal posts a self-`DisposeRequest`), surfacing to the subscriber
> as a **terminal** `DeliveryFailure`. Deterministic repros:
> `SubscribeDuringRecycleTest` (Layout.Test, end-to-end) and
> `HubDisposalFailureClassificationTest` (Messaging.Hub.Test, the classification, including
> the reflection-wrapped case).

---

## Adding disposal work — the rule

- **Need to run sync cleanup on dispose?** `hub.RegisterForDisposal(IDisposable)`
  (the common case) or `RegisterForDisposal(Action<IMessageHub>)`. These run in the
  ShutDown phase on the action block.
- **Need cleanup that performs I/O (an async flush)?**
  `hub.RegisterForDisposal(Func<IMessageHub, IObservable<Unit>>)` — return an
  observable that completes when done. There is no `Func<…, Task>` overload and no
  `IAsyncDisposable` overload. The hub **starts** it in ShutDown and does not wait;
  its leaf must run on a mesh-scoped resource (the persistence layer / `IIoPool`) so
  the mesh-level teardown drain can join it.
- **Need to wait for the hub to finish disposing?** Subscribe to
  `hub.DisposalCompleted`. Only at the test / grain edge may you bridge it once with
  `.FirstOrDefaultAsync().ToTask()`. To ask "is it shutting down?", read `IsDisposing`.
- **Tempted to `await` something during disposal?** Don't — it deadlocks the action
  block. Express the wait as an `Observable` (`Interval` poll, `Timer`/`Amb` deadline,
  subscribe to a child's `DisposalCompleted`) and post the next phase from its
  terminal callback, exactly as the phases above do.
- **Tempted to add a `TaskCompletionSource` to signal "done"?** That is the smell
  the `ReplaySubject` replaced. Use a subject and a CAS-guarded `Signal…` helper.
- **Armed a timer, interval or debounce?** Its subscription must reach this disposal
  chain — `hub.RegisterForDisposal(serialDisposable)`, then arm into the
  `SerialDisposable`. A pending `TimerQueue` entry is a strong GC root, so an
  unregistered one keeps the hub alive past its own teardown, and holding it in a
  field is not the same as owning it. See
  [Subscription Ownership](/Doc/Architecture/SubscriptionOwnership).

Canonical implementation: `MessageHub.Dispose` / `MessageHub.HandleShutdownCore` /
`MessageHub.OnQuiesceComplete` / `MessageHub.DisposeImpl` /
`MessageHub.ForceTeardownAfterWatchdog` / `HostedHubsCollection.DisposeHubsReactive`
in `src/MeshWeaver.Messaging.Hub`.
