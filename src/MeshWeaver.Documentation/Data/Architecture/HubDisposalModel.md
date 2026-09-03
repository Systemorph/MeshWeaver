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
where a `Task` is legitimately the calling convention, bridge the observable *once*,
through `ReactiveCompletion.ObserveCompletion`:

```csharp
// Grain deactivation / test teardown — the ONLY place a Task appears, at the edge:
await hub.DisposalCompleted
    .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))  // fault → "done"
    .FirstOrDefaultAsync()
    .ObserveCompletion(
        ex => logger.LogWarning(ex, "disposal faulted AFTER the wait settled"),
        ct);
```

🚨 **Not `.ToTask(ct)`, and not a bare `await` on the observable either** — both are
forbidden repo-wide as of 2026-08-30 (*"no ToTask ever"*), and for this signal in
particular they are the deadlock: Rx completes its `TaskCompletionSource` without
`RunContinuationsAsynchronously`, so the awaiter resumes INLINE on the thread that
signalled — the hub's own disposal thread, or the grain's turn scheduler — and the
rest of the teardown then runs there. `ObserveCompletion` queues the continuation
instead, and keeps its error arm attached so a fault arriving after the wait settled
is reported rather than orphaned as an `UnobservedTaskException`.

That `ObserveCompletion` is the framework-lifecycle Task boundary (the same place tests
`await`). Nowhere inside `src/` hub-reachable code does a Task appear at all.

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

> 🚨 **Known blind spot: the verdict does not see children that have already LEFT.**
> A hosted hub removes itself from its parent's registry in its own ShutDown phase —
> before the parent's teardown completes — so `AnyHubQuiescingTimedOut()`, which walks
> the parent's *live* children, finds none of them by the time a test base asks. That
> is how a per-NodeType hub could time out its 2 s budget on 22 consecutive fixture
> teardowns (#3026) while the fixture's leak gate read the mesh as clean on all 22.
> Making the verdict keep each departing child's summary is straightforward, but it
> immediately surfaces pre-existing hosted-hub timeouts in unrelated test classes
> (measured: two in `MeshWeaver.Graph.Test` alone), so it is tracked as its own change
> rather than folded into the watcher fix.

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

## A recycle owes its subscribers a goodbye — and can only say it BEFORE the teardown

A routed `DisposeRequest` is a **recycle**, not an end: the address comes back on the next
access. The automatic ones exist *for* the people currently looking at a page —
`NodeTypeEnrichmentHelpers.WithOverlaySelfHeal` recycles an instance hub the moment its NodeType
reaches a usable build, so the compile-progress overlay is replaced by the real page;
`NodeTypeRebindWatcher` and the stale-build convergence branch do the same for a superseded
binding.

**But the teardown itself is structurally mute.** `JsonSynchronizationStream` emits
`StreamEndedEvent` from the server-side stream's disposal, and that emission is *deliberately
suppressed* once the owning hub is winding down — a dying owner that reaches up the hub tree for
a last word re-activates the very Orleans activation it is retiring. It delegates the
owning-hub-tearing-down case to two other recoveries, and **neither can fire for a recycle**:

- the **recycle re-arm** needs an in-flight `SubscribeRequest` to be NACKed — an *established*
  subscription has none;
- the **change-feed latch** needs a WRITE to the owner's path — and a recycle is not a write.

So the subscriber received no frame, no completion and no error, and its mirror served the last
snapshot it ever saw for the life of the page. The visible symptom was a page frozen on the
compile-progress overlay while the compile had succeeded seconds earlier — and because a
framework-identity bump recompiles every dynamic NodeType at once, that is every open page in the
portal on one deploy (Systemorph/MeshWeaver#2533 / #2551).

**The seam is `RecycleAnnouncement`,** hung on the hub with `hub.Set(...)` and invoked by
`HandleDispose` on the recycle's own turn:

```csharp
// MessageHub.HandleDispose
if (!IsShuttingDown)          // an ancestor's cascade is NOT a recycle — see below
    AnnounceRecycle();        // Get<RecycleAnnouncement>()?.Announce()
Dispose();
```

`Workspace` registers the one real implementation, because the client-subscription registry that
knows who is listening lives there. Three properties make it safe, and each is load-bearing:

| Property | Why |
|---|---|
| **Announced BEFORE `Dispose()`** | The hub is still whole: the registry is intact and `Configuration.ParentHub` still resolves. Both are gone or unreliable a phase later. |
| **DELIVERED after `DisposalCompleted`, through the PARENT hub** | The subscriber answers with ONE bounded re-ask. Delivered earlier, that re-ask lands on the still-dying instance, is NACKed `ShuttingDown`, and burns a budget meant for a genuinely non-converging owner. Delivered by the dying hub itself, it is the up-the-tree post that resurrects the activation. The parent outlives the target and speaks to a third party. |
| **Never for an ancestor's cascade** (`IsShuttingDown` already true) | There the address is *not* coming back, every subscriber is going down with it, and telling them to re-ask is exactly the resurrection the suppression exists to prevent. A direct `Dispose()` — how `HostedHubsCollection` tears its children down — never reaches `HandleDispose` at all, so it stays silent by construction. |

Nothing here polls, retries or times out: `DisposalCompleted` is the event, and the re-ask it
triggers is the pre-existing bounded one. A recycle of a hub nobody is subscribed to costs
exactly what it did before.

Repros: `RecycleStrandsLiveSubscriberTest` (Hosting.Monolith.Test — a live layout-area
subscription re-converges on the re-activated hub after a bare `DisposeRequest`, with no node
write anywhere) and `RecycleAnnouncementTest` (Messaging.Hub.Test — announced once, before the
teardown; silent on a direct `Dispose()`).

> Only the *automatic* recycles were affected. `MeshOperations.Recycle` (the MCP tool) publishes a
> `MeshChangeEvent` for the path before its `DisposeRequest`, which is what fed the change-feed
> latch and made that path look fine; `RecycleLayoutArea` is driven by the page shell, which
> navigates on its own. The self-heal posted the dispose alone.

---

## The first instant of teardown: `ShuttingDown`

`Dispose()` is not the first moment a hub is part of a shutdown. An **ancestor's**
`Dispose()` freezes hosted-hub creation across the whole subtree, synchronously,
before it returns (see "The creation window" above);
a descendant's own `Dispose()` — its `DisposeRequest` — arrives only in the ancestor's
DisposeHostedHubs phase, after the ancestor has spent up to its whole `QuiesceTimeout`
draining its own callbacks. `IsShuttingDown` reports that window as a property;
`RunLevelChanged` reports only this hub's own phases; `DisposalCompleted` reports the
end. Nothing reported the *beginning* as an event, so a watcher could only sample it.

**`IMessageHub.ShuttingDown`** is that event: it fires once, at the first instant the
hub becomes part of a shutdown — its own `Dispose()` or the ancestor cascade
(`CloseHostedHubCreation`), whichever comes first — then completes, and it replays to
a late subscriber (an `AsyncSubject`, the same contract as `DisposalCompleted` at the
other end).

### Why it exists: a watcher that outlived the start of teardown (#3026)

Every per-NodeType hub installs watchers over its own node — compile, release-request,
sources / `IsDirty`, adopted-stamp — and hands each one's `IDisposable` to
`hub.RegisterForDisposal`. That disposes them in the **ShutDown** phase: the last one.
Through the entire window before it — the ancestor's quiesce, the ancestor's hosted
join, this hub's own Quiescing — the watchers kept working. The sources watcher in
particular recomputes the source fingerprint on every emission, and resolving the
`@@`-include closure issues cross-hub `GetDataRequest`s **from the hub that is
tearing down**. Those requests are exactly the pending callbacks Quiescing then waits
for; when the budget elapsed, `CancelCallbacks` errored them:

```
DISPOSE_INVOKED
  … 2 052 ms …
[FAULT] [Warning] SourcesWatcher: the @@-include closure for FutuRe/LocalAnalysis could not be established
  SourceIncludeUnavailableException ---> ObjectDisposedException:
  Hub FutuRe/LocalAnalysis was disposed before the response arrived
  (GetDataRequest, target FutuRe/GroupAnalysis/Source/ExternalDependencies)
DISPOSE_DONE elapsed=2088ms teardown clean — all pooled I/O joined
```

Twenty-two times in one `MeshWeaver.FutuRe.Test` shard — one per fixture teardown,
each exactly one quiesce budget after `DISPOSE_INVOKED` — and once a sibling of that
callback ran on a raw scheduler thread against a hub whose scope was gone and killed
the host (exit 139). The teardown verdict read *clean* every time because the
timed-out hub had already left its parent's registry (the blind spot noted under the
Quiescing phase above).

### The rule for hub-owned watchers

Install every watcher a hub owns through
`ActivityControlPlaneExtensions.SubscribeHubWatcher(hub, source, onNext, …)` — the
hub-aware form of `SubscribeWithReEstablish`. It differs in three ways, all of them
this signal:

1. **It stops at `ShuttingDown`.** The live subscription *and* any pending
   re-establish are disposed at the first instant of teardown. Because that disposal
   propagates into the watched pipeline, an in-flight cross-hub read is *unsubscribed*
   — its pending callback leaves the hub's response registry — rather than waited on,
   so Quiescing drains at once instead of timing out on the watcher's own traffic.
2. **Every delivery is gated on `IsShuttingDown`.** An emission already dispatched on
   another thread when the signal fires is dropped, never run against a disposing hub.
   Dropping is correct: the fresh activation installs a fresh watcher and reads the
   current state anew.
3. **The source factory runs under `Observable.Defer`.** A re-establish evaluates the
   factory on the 1 s `Observable.Timer` tick — a scheduler thread with nothing above
   it — so a factory that throws synchronously there (a `GetMeshNodeStream()` on a
   hub whose scope is gone) used to be an unhandled exception on a timer thread. Under
   `Defer` it is a stream fault the classifier owns.

The address-only `SubscribeWithReEstablish(source, onNext, address, …)` remains for
watchers with no owning hub. It knows nothing of teardown, so for a hub-owned watcher
it *is* the #3026 defect.

Pinned by `HubWatcherStopsAtTeardownStartTest` (the primitive, with the action block
deliberately parked so no disposal phase can run during the assertions),
`ShuttingDownSignalTest` (the signal on a whole subtree, synchronously inside the
ancestor's `Dispose()`) and `SourcesWatcherStopsAtTeardownTest` (the sources watcher
with a deterministically in-flight `@@`-include read, disposed mid-read: no quiesce
timeout, no `[FAULT]`).

### The rule for initialization: a BuildupAction never starts after `ShuttingDown`

The init turn (`InitializeHubRequest`, which runs the `WithInitialization` observables as
a `Concat`) is queued at `Build` and runs whenever the action block reaches it. That can
be **after this hub's own `Dispose()`** — a transient probe is created and disposed in
one breath (`ContentTypeRegistration.ProbeRegister` at boot, the schema probes in
`MeshOperations`) — or after an ancestor's cascade froze the subtree before any
descendant had initialised. Every BuildupAction is a piece of the per-node control
plane: a watcher over the own node, an eagerly created child hub, a ticker. Installed on
a hub that is already leaving, each one is born dead and faults on the way out:
`HostedHubsCollection` refuses the child with a Warning (`Rejecting hosted hub creation
… during disposal - collection is disposing`), the teardown errors the watcher's stream.

Measured on the Thread NodeType's boot-time registration probe: its `AddThreadExecution`
chain reached the `_Exec` child creation after the probe's own `Dispose()` in **159 of
643** test logs of one CI run (MeshWeaver CD 33619142646) — a Warning each time, at a
random offset from boot — and the one test that asserts a fault-free probe teardown
(`ProbeHubCostTest.ValidateContentWithSchema_OnInvalidContent_BuildsOneProbeNotTwo`,
MeshWeaver.Plugins) red whenever the late creation landed inside its recording window.
The same interleaving does not reproduce on an idle developer machine, which is why it
cannot be bisected locally and must be reasoned from the CI evidence.

`HandleInitialize` therefore checks `IsShuttingDown` **at every action boundary**: once
teardown has begun, the remaining actions are skipped (one Debug line), the Initialize
gate still opens so the disposal state machine flows, and nothing is installed. This is
the init-turn form of the watcher rule above, and the same policy the existing `.Catch`
already applies one step later ("teardown ENDED an action" is a recognised shutdown, not
a failure). A single action that is already running when the signal fires is not
interrupted — the boundary is the granularity, exactly as a watcher's in-flight delivery
is the granularity of `SubscribeHubWatcher`.

Pinned by `InitializationStopsAtTeardownStartTest`: the first action is parked on the
action block, `Dispose()` is called, the park is released, and the second action — which
creates a child hub, the `_Exec` shape — must not have run by the time
`DisposalCompleted` fires; a control arm on a live hub proves the same chain runs to the
end and the child exists.

**What the skip does NOT touch — and how to observe a probe.** Only the OBSERVABLE
`WithInitialization` actions run on the init turn. The synchronous overload
(`SyncBuildupActions`) runs inside `Build`, before message processing starts and therefore
before any caller can `Dispose()` the hub — which is where content-type registration lives:
`AddMeshDataSource` composes its `WithContentType` into `AddData`, whose
`RegisterWorkspaceTypes` runs `DataContext.Initialize` synchronously and records the type in
the mesh-wide `IMeshContentTypeRegistry`. So `ContentTypeRegistration.ProbeRegister`'s
build-and-dispose shape registers every swept type whether or not its init turn ever runs;
the skip removes only the born-dead control plane. Two consequences for tests: a test that
needs to know a probe was swept must NOT take its signal from an observable init action
(that action is legitimately skipped whenever `Dispose()` beats the init turn — the
determinism `ContentTypeProbeControlPlaneTest` lost when this rule landed), it captures the
probe's `DisposalCompleted` from a synchronous `WithInitialization` and waits on that; and an
assertion that a probe wrote no fault line is complete once `DisposalCompleted` fires,
because the `ShutdownRequest` is queued behind the init turn and the teardown has written
whatever it writes by then.

## 🚨 A disposal path never resolves from DI — and never truncates itself

Two rules, one incident. Both are enforced in `MessageHub`, and code that registers
disposal work has to obey the first.

**1. Never call DI from a disposal path.** By the time a hub's registered clean-ups run,
its lifetime scope — or an ancestor of it — may already be closed:
`HostedHubsCollection.CloseScopeWhenDisposed` closes a hosted hub's scope on
`DisposalCompleted`, and the watchdog's `ForceTeardownAfterWatchdog` runs
`hostedHubs.Dispose()` *before* `DisposeImpl()`. A resolve then throws

```
ObjectDisposedException: Instances cannot be resolved and nested lifetimes cannot be
created from this LifetimeScope as it (or one of its parent scopes) has already been disposed.
```

`hub.Configuration.ParentHub` counts as a resolve — it re-reads `ParentServiceProvider`.
So does `GetService<ILoggerFactory>()`. **Capture what the clean-up needs at REGISTRATION
time**, on the handler's turn, and close over it:

```csharp
// ✅ resolved while the scope is provably alive, used later
var sink = hub.ServiceProvider.GetService<ILatePatchVerdictSink>();
var parent = hub.Configuration.ParentHub;
hub.RegisterForDisposal(_ => { if (!sink!.Dispatch(id, verdict)) parent?.Post(verdict); });

// ❌ resolved on the disposal path — throws once the scope is closed
hub.RegisterForDisposal(_ =>
    hub.ServiceProvider.GetService<ILatePatchVerdictSink>()!.Dispatch(id, verdict));
```

**2. A failing clean-up is isolated, not fatal to the rest.** The hub's synchronous
clean-ups live in one Rx `CompositeDisposable`, and `CompositeDisposable.Dispose` walks
its list with **no per-item guard**: the first registrant that throws ends the walk, and
every clean-up registered behind it is skipped in silence. `RegisterForDisposal` therefore
wraps every registrant (`MessageHub.GuardRegistrant`): a fault is logged as
`[DISPOSE-REGISTRANT] {Address}: a registered cleanup ({Registrant}) faulted…` and the
walk continues. This is the same per-leg isolation the *reactive* dispose actions already
had — it is isolation, not tolerance: nothing is swallowed, and a registrant that throws
is still a bug in that registrant.

**What it cost, measured.** Main shard 4, 2026-09-02 (run 33630685580): a per-node owner
hub went down through the watchdog's out-of-band teardown; one clean-up resolved from an
already-closed scope; the walk stopped there; and the `OwnerDisposing` NACK behind it — the
verdict that tells a writer "this did not apply, retry against the fresh activation" — was
never minted. Its writer heard nothing and burned the full 31 s `WriteVerdictBound` before
reporting `OwnerUnreachable`. An acked write lost to a teardown that had truncated itself.
Pinned by `DisposalRegistrantFaultIsolationTest`.

## Adding disposal work — the rule

- **Anything the clean-up needs from DI?** Resolve it at REGISTRATION time and close over
  it — see the rule above. A disposal action that calls `GetService`, `GetRequiredService`
  or `Configuration.ParentHub` is a defect even when it happens to work today.
- **Installing a WATCHER the hub owns?** `SubscribeHubWatcher(hub, …)`, then hand the
  result to `RegisterForDisposal` as before. The registration is the backstop; the
  `ShuttingDown` signal is what actually ends the watcher — see the section above.
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
  `hub.DisposalCompleted`. Only at the test / grain edge may you bridge it once, and
  only with `.FirstOrDefaultAsync().ObserveCompletion(reportLateFault, ct)` — never
  `.ToTask()`. To ask "is it shutting down?", read `IsShuttingDown` (which also sees
  an ancestor's teardown; `IsDisposing` sees only this hub's own). To *react* to it
  beginning, subscribe to `hub.ShuttingDown`.
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
