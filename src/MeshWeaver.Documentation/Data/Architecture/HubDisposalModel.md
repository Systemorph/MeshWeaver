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
> For the policy the phases implement — accepted work finishes, wedged work is reported
> and cancelled, nothing is forced — see [Teardown Layers](/Doc/Architecture/TeardownLayers).
> For how to READ the stall report those phases emit — what each snapshot field measures, and the
> three that were read as evidence while measuring nothing — see
> [Reading a Disposal Stall Verdict](/Doc/Architecture/DisposalStallVerdicts).
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

If the budget elapses with callbacks still pending, the hub first asks whether any of
them is **owed by a sibling hub in this mesh that is itself shutting down** — resolved at
the mesh root like `HierarchicalRouting` does — and if so **re-arms the budget** rather
than cancelling (`[QUIESCE-WAIT]`): that sibling answers every delivery it accepted before
it leaves the registry, so the wait ends by construction (a cycle breaker,
`MaxQuiesceRearms`, covers two hubs holding each other's deferred requests; see
[Teardown Layers](/Doc/Architecture/TeardownLayers)). Otherwise the hub sets the sticky
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

### The stall detector — `Observable.Timer`, and it reports instead of tearing down

A reactive timer watches the teardown and **cancels itself the instant disposal
completes**. It measures a **stall**, not a duration: it is re-armed by every
`RunLevel` transition anywhere in the hosted subtree, so a slow nested teardown never
trips it (#1701), and it fires every `DisposalWatchdogTimeout` (8 s) that goes by with
no such signal:

```csharp
watchdogSubscription = DisposalProgress
    .StartWith($"{Address} Dispose() called")
    .Select(reason => Observable.Timer(DisposalWatchdogTimeout, DisposalWatchdogTimeout).Select(_ => reason))
    .Switch()                                   // re-arm on every progress signal in the subtree
    .TakeUntil(disposalCompleted)               // stop the moment disposal finishes
    .ObserveOn(DefaultScheduler.Instance)       // never on the progress path (lock-order inversion)
    .Subscribe(OnDisposalStall, _ => { });
```

`OnDisposalStall` reaches one verdict — including an explicit *unknown* — and **performs no
teardown**; how to read the one it printed is
[Reading a Disposal Stall Verdict](/Doc/Architecture/DisposalStallVerdicts). See
[Teardown Layers](/Doc/Architecture/TeardownLayers) for the table. In short: a pump that
keeps completing turns is *busy* (Information; accepted work is draining ahead of the
`ShutdownRequest`, which queues FIFO behind it); a turn that has held the block for a
whole budget is handed its cancellation token once and reported at **Error** by name;
one that ignores that is reported again every budget; a `ShutdownRequest` that is itself
on the block means a registered cleanup is blocking inside `DisposeImpl` (reported, not
cancelled — it runs with `CancellationToken.None`); and no turn at all means the stall
is in a child or a join, which the recursive diagnostics name.

Two things about its history matter, because both were once wrong the other way:

- **It no longer tears anything down out of band.** The predecessor ran
  `hostedHubs.Dispose()`, `CancelCallbacks()`, `DisposeImpl()` and
  `messageService.Dispose()` from the timer thread and signalled `Dead` while the
  wedged turn was still executing. A parent then advanced against a subtree that was
  mid-flight; production (`memex-cloud`, 2026-08-29 → 09-03) showed it firing dozens of
  times per shutdown and on pods that were not shutting down. Its own predecessor
  merely **signalled** completion and leaked every child (the 2026-07-01 zombie
  portal-hub storm). Now a hub either finishes its phases or stays *pending* and says
  why; the bound that ends a wedged teardown is the caller's, and it reports the
  snapshot rather than forcing.
- **`TakeUntil(disposalCompleted)` is what fixed the TimerQueue leak.** The old
  uncancelled `Task.Delay(25 s)` rooted the entire hub graph (cache, data sources,
  action block, subscriptions) for 25 s after *every* dispose, even a fast one. The
  reactive timer releases its scheduler entry as soon as the subject fires.

`Dispose()` also **no longer cancels the in-flight turn on entry**. Work the hub accepted
before teardown began runs to completion; cancellation is the stall verdict above, never
a reflex. The one exception is a hub still in `Starting`: its `InitializeHubRequest` is
its own bring-up, not accepted work, and is cancelled at once so a hung initialization
cannot hold its ancestry pending for a whole stall budget.

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

## Rooted Rx connections: what a `Dispose()` must release, and the one spelling for it

Every multicast chain an object builds — `Replay(1)`, `Publish()`, `PublishLast()` — has a
**connection**: the upstream subscription `Connect()` opens. Whoever holds the handle
`Connect()` returns is the only party that can close it. Two spellings hold it *nowhere an
owner can reach*, and both were live in core until 2026-09-08:

- **`.AutoConnect(1)`** keeps the handle *inside the operator*. The owner's `Dispose()`
  detaches everything it can name and never touches the chain.
- **`.Connect()` whose result is dropped** (or stored in a field nobody disposes) is the
  same thing without the operator.

That would be a leak. What makes it a **use-after-dispose** is that the connect is frequently
*not synchronous*: a chain shaped `Defer(…).SubscribeOn(TaskPoolScheduler)` only *queues* its
upstream subscribe on the first subscriber's thread, and **nothing in the teardown joins a
pool-queued Rx subscribe** — `DisposalCompleted` covers the action blocks,
`IoPoolRegistry.DrainAll()` covers `IIoPool` leaves, the `AsyncDisposeQueue` covers enqueued
cleanup. The item ran whenever the pool reached it. Measured on MeshWeaver.Plugins run
`34222933802` (2026-09-08): all 11 disposed-scope stragglers captured across three suites
were `MeshNodeStreamCache.GetQueryRaw`'s `Defer` resolving `cacheHub.GetWorkspace()` from an
Autofac scope the mesh had already closed, the FutuRe pair 4 ms after `DISPOSE_DONE`
([the chain walk](/Doc/Architecture/DebuggingNativeCrashes)).

**The rule.** A connection is owned by the object whose services the chain resolves, and
that object's `Dispose()` releases it. The one spelling is
`MeshWeaver.Messaging.OwnedConnectionExtensions`:

```csharp
// A lazily-connected shared chain (a promise cache, a synced query, a per-name collection):
var stream = Observable.Defer(() => BuildAgainst(hub.GetWorkspace()))
    .SubscribeOn(TaskPoolScheduler.Default)
    .Replay(1)
    .AutoConnectOwnedBy(hub, nameof(MyService));          // or (compositeDisposableField, releaseLane, name)

// An eager feed that must keep filling with no subscribers (a directory index, a counter):
applied.ConnectOwnedBy(hub);                              // or ConnectOwnedBy(compositeDisposableField)
```

What the helper guarantees, and what a bare `AutoConnect(1)` cannot:

| | bare `AutoConnect(1)` / dropped `Connect()` | `AutoConnectOwnedBy` / `ConnectOwnedBy` |
|---|---|---|
| The owner's `Dispose()` | cannot reach the upstream | unsubscribes it (`hub.RegisterForDisposal` → ShutDown phase, strictly before any scope closes) |
| A connect still **queued** on the pool | runs later, against whatever is left | is **cancelled** before the pool dequeues it — the registration lands in a disposed `CompositeDisposable`, whose `Add` disposes on the spot |
| A subscriber arriving **after** the release | gets the replay buffer, then silence forever ("burst then dead silence") | is **refused** with `ObjectDisposedException(ownerName)` — terminates, attributable, delivered on the owner's release lane (never on the subscribing thread — see below) |
| A subscriber **already attached** when the release happens, the one-shot still in flight | silence forever — releasing a connection unsubscribes the replay subject from its upstream and emits nothing to its observers | **terminates** with the same `ObjectDisposedException(ownerName)`, delivered on the ThreadPool (never on the disposing hub's turn), ONE AT A TIME on the owner's release lane — see below |
| A chain that **terminates** (a settled promise) | — | drops its handle, so the owner tracks *live* connections only and a faulted-then-rebuilt promise never accumulates dead handles |

**The attached subscriber was the half that stayed silent (#5135).** Refusing late subscribers
covered only callers that arrive after the release. A caller that asked while the one-shot was
still running — an MCP `upload` waiting on `ContentService.GetCollection` while the collection's
initial scan ran, when the hub it rode tore down — received no value, no error and no completion,
ever: disposing the connection detaches the replay subject from its upstream and tells its
observers nothing, and nothing UPSTREAM of the `Replay` (the eviction `Catch` in
`ContentService` included) can see an unsubscription. `AutoConnectOwnedBy` now hands out
`shared.TakeUntil(released)`, and the owned handle fires `released` only when the OWNER is
disposed — a chain that merely terminated removes its handle from a live owner and keeps replaying
its settled value. Pinned by `OwnedConnectionTest.ASubscriberAttachedWhileInFlight_IsTerminatedByTheRelease`
and, at the content-collection site, `InFlightCollectionReleasedWithItsHubTest`.

**Those terminals are delivered one at a time, never concurrently.** An owner registry is released
in ONE synchronous sweep — `MeshNodeStreamCache` disposes every query connection it holds, a hub's
ShutDown disposes every registration — and the first shape of the #5135 fix scheduled each
connection's terminal as its own ThreadPool work item, so a sweep of N connections delivered N
terminals at once. A consumer that composes several of those connections then received them on N
threads, and Rx combinators take their gate on a terminal and dispose their other sources while
holding it: the permission fold nests a `Zip` under a `CombineLatest`, one release held the
`CombineLatest` gate and waited for the `Zip` gate, another held the `Zip` gate and waited for the
`CombineLatest` gate, and neither ever let go. The service provider's disposal then parked behind
them (`UiContributionCatalog.Dispose` completing its subject into that same `CombineLatest`), so a
test whose body had passed and whose mesh had disposed cleanly timed out in teardown —
MeshWeaver.Plugins `LayoutAreaIdentityTest.AuthorizedUser_CanSubscribe_ToLayoutArea` and
`GitHubSyncSettingsTabTest.GitHubSyncTab_ShownOnSpace` on set 3.0.0-ci.9296, reproduced locally
about once in twelve full `MeshWeaver.Security.Test` runs under the MTP runner, with a stack capture
of exactly those threads. So a release now goes through a **`ReleaseLane`**: releases posted to one
lane run on the ThreadPool, serially, in the order they were posted, and a second terminal reaches a
consumer the first has already torn down. Every hub resolves the mesh's lane (registered on the
root hub, like `AccessService`), so all hub-owned connections of a mesh share it;
`MeshNodeStreamCache` uses that same mesh lane; a registry outside a hub
(`PackageListingCache`, `GitHubRepoIdentityResolver`) holds one lane for everything it owns. The
rule for choosing: **two connections a consumer may compose must release on the same lane.** The
`CompositeDisposable` overload without a lane is `[Obsolete]` — it mints a lane per connection,
which is exactly the concurrent shape. Pinned by
`OwnedConnectionTest.ReleasingTwoConnectionsAConsumerComposes_NeverDeliversTheirTerminalsConcurrently`,
which forces the two releases to meet inside their first gates (red with one work item per
release; green on a shared lane).

**A refusal is a release terminal too, so it goes on the same lane.** Serialising release
against release left one terminal off the lane: the REFUSAL a subscriber gets when it arrives after
its owner was released. It was an `Observable.Throw`, delivered synchronously on the subscribing
thread — and a consumer that composes several connections receives releases for the ones it already
holds and refusals for the ones it is still subscribing, at the same time. Measured on
MeshWeaver.Plugins scheduled run 36090164894 (job 107933985813, platform set 3.0.0-ci.9321):
`SendDocumentIdentityPanelTest.StampingARecheckOnTheDraft_ReprobesAndReplacesThePanel` passed its
body in 442 ms and then reported `teardown DIRTY — 1 pooled I/O leaf(s) … CANCELLED after the drain
grace [Layout=1 [Defer<EntityStoreAndUpdates>]]` after the whole 38 s drain. Reproduced in a Linux
container on 2 CPUs (1 run in 400 of that class) with `dotnet-stack` on the hung host: the layout
render's pooled subscribe leaf was subscribing the permission fold — a `SelectMany` whose inners are
`Zip`s of `MeshNodeStreamCache` queries — when the mesh tore down. The **lane** was delivering one
query's release: it held that inner's `Zip` gate and was entering the `SelectMany` gate. The
**render thread** was subscribing the next inner, whose query refused synchronously: it held the
`SelectMany` gate forwarding that error and was disposing the first `Zip`, which takes the `Zip`
gate. Neither returned, the leaf never left the Layout pool, and `IoPool.Drain` could only report it.

So every terminal an owned connection produces is delivered on the lane: `AutoConnectOwnedBy`
refuses through `ReleaseLane.Refuse<T>`, the release signal refuses a subscriber that attaches after
it fired on the lane too (a terminated `Subject` would replay its error synchronously on the
subscriber's thread — the same race one line later), and `MeshNodeStreamCache`'s disposed-cache
query guards refuse through the mesh's lane instead of `Observable.Throw`. A subscriber is therefore
never told anything on its own thread by a released owner, and the terminals that reach one consumer
are serialised with each other whichever kind they are. Pinned by
`OwnedConnectionTest.ARefusalAndAReleaseMeetingInOneConsumer_NeverDeadlock`, which parks the lane
inside the first `Zip`'s gate and lets the subscriber meet it in the order the CI stacks show (the
subscriber thread never returns with the refusal restored to `Observable.Throw`), and by
`ASubscriptionAfterTheRelease_IsRefusedNamingTheOwner_OnTheLane`.

**The lane drains on pooled work items, never on a dedicated thread.** `ObserveOn(TaskPoolScheduler.Default)`
takes Rx's long-running path when the scheduler offers `ISchedulerLongRunning`, which starts ONE
dedicated thread on the lane's first post and parks it in `Monitor.Wait` for as long as the
subscription lives — and the lane's subscription is never disposed. Every lane that ever delivered a
release therefore kept a thread (and, through it, itself) alive for the rest of the process: one per
torn-down mesh in a test host, visible as an `ObserveOnObserverLongRunning.Drain` frame per mesh in
any stack capture. The lane now observes on
`TaskPoolScheduler.Default.DisableOptimizations(typeof(ISchedulerLongRunning))`, which keeps the
same serial order on pooled work items. Pinned by
`OwnedConnectionTest.AReleaseRunsOnAPooledThread_NeverOnADedicatedOne`.

**Choosing the owner.** A hub-scoped service registers with its hub; a DI singleton owns a
`CompositeDisposable` field it disposes in its own `Dispose()` (the container disposes
singletons); an object that already has a disposal registry (`MeshNodeStreamCache`'s
`_queryConnections`) uses that. Never a static.

**`RefCount()` is different, deliberately.** A ref-counted chain's connection belongs to its
*subscribers* — it releases with the last of them — so there is no handle for an owner to
hold. What must be owned there is each *subscription*, through the ordinary
`RegisterForDisposal` rule (`MeshNodeTypeSource` registers its own; `VirtualDataSource`
registers with its stream). Every `RefCount()` site in `src/` is inventoried with the reason
its subscriptions are owned.

**Enforced** by `RootedRxConnectionRatchetGuard` (`test/MeshWeaver.Documentation.Test`):
a bare `.AutoConnect(` / `.Connect()` anywhere in `src/` is red with no allow-list escape
(the rooted budget is zero); a `.RefCount(` site must carry a reasoned line in
`test/RootedRxConnectionSites.allow`, and that inventory only grows together with the
guard's budget constant. Pinned end to end by `OwnedConnectionTest` (Messaging.Hub.Test —
a connect queued on a `TestScheduler` is cancelled by the owner's release, with a control
arm proving it was registered) and, at a converted site,
`ContentCollectionConnectionOwnedByHubTest` and `QueryConnectionsReleasedOnTeardownTest`.

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

🚨 **The refusal is for a host that is going away, and ONLY for that (#5592).** A null sub-hub also
comes back when its construction RAN and FAULTED on a live host: a configuration that threw, a
container that could not build, an `OutOfMemoryException` under heap exhaustion.
`HostedHubsCollection` reports that as `HostedHubOutcome.ConstructionFaulted` with the real
exception. The constructor used to refuse that case with `HubDisposingException` as well, so the
host's own init failure read *"BuildupAction faulted (HubDisposingException: Hub X is shutting
down …)"* on a hub whose `IsShuttingDown` was false. The report was then triaged as an expected
teardown race. The constructor now reads the outcome through `TryGetHostedHub` and throws an
`InvalidOperationException` that names the root fault and carries it as the inner exception. It
is deliberately NOT an `ObjectDisposedException`, so no teardown classifier can read it as
"retry". Pinned by `SyncHubConstructionFaultIsNotAShutdownTest` (Data.Test); its second case is
the control, showing the creation freeze still refuses with `HubDisposingException`.

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

## The other creation window: a hub disposed while it is still being BUILT

The window above is a live hub accepting work during its teardown. This one is the mirror
image — a hub torn down before its construction has returned — and its cost was a **user
action that vanished without a line** (#4741).

**How a hosted hub enters its parent's registry, twice.** `MessageHubConfiguration.Build`
constructs the hub, then immediately calls `HostedHubsCollection.Add` — which inserts it under
its address, arms the removal (`RegisterForDisposal(h => remove)`) and raises the first
`HubAdded` — then runs the synchronous buildup actions, then `StartMessageProcessing`. Only
after `Build` returns does `GetHubWithOutcome`'s creation `Lazy` insert the same hub a second
time and raise `HubAdded` again. So a `HubAdded` subscriber is handed a hub whose buildup has
not run and whose init request has not been posted.

**The pump does not wait for `Build`.** A `Dispose()` issued in that gap — from a `HubAdded`
subscriber, an ancestor's cascade, a probe hub created and disposed in one breath — posts a
`ShutdownRequest` that is drained the moment it lands, bypasses every init gate, and takes an
empty hub to `Dead` in about a millisecond: `Quiescing` waits only for pending callbacks,
`DisposeHostedHubs` joins no children, `ShutDown` runs the hub's `disposables` — **including the
registry removal `Add` armed** — and signals `DisposalCompleted`. All of that can complete
while the constructing thread is still inside `Build`.

**What the second insert then did.** The Lazy's bare `messageHubs[a] = created.Hub` put the
corpse back under its address, and nothing was left to take it out: the only removal had
already fired. From then on the parent-chain walk in `RouteStreamMessage` *found* a registered
hub for that stream, delivered into it, and the dead hub's intake discarded the message — no
`REFUSING …` line (that is the miss path, and there was no miss), no `Dropping …` warning, no
line at all. `AnAgedOutStreamSaysSoRatherThanClaimingItWasNeverServed` measured exactly that
shape: a click on a reaped stream, 36 s, *"The observable emitted nothing at all"* — 1 in 32
full-assembly runs locally, and the same family's `AStreamTheOwnerReaped_IsNamedAsSuch` on CI.
Every test in that family disposes the hub the first `HubAdded` handed it, so every one of them
was exposed.

**The rule: an insert never outlives its own removal.** Both inserts now go through one
`Track(hub)`, which inserts and then re-arms the removal on the hub. That is race-free without a
liveness check that could itself race: `RegisterForDisposal` disposes a registrant *at once* on
a hub whose disposal has already begun, so a corpse re-inserted after its removal fired comes
straight back out, and a live hub merely carries one redundant removal. The removal is also
value-matched (`TryRemove(KeyValuePair)`), so a late removal can only ever take out the hub it
was armed for — never a successor built under the same address.

> Deterministic repro and control: `AHubDisposedWhileBeingBuiltLeavesTheRegistryTest`
> (Messaging.Hub.Test). The dispose is issued from the first `HubAdded`; the constructing
> thread is parked in the hub's own synchronous buildup action — on that thread, outside any
> `Post` — until the hub is `Dead`, so `Build` returns to the Lazy holding a corpse every time.
> With the bare re-put restored the registry assertion fails (*"Expected <null> … but found
> MessageHub"*, park released at `RunLevel=Dead` after 1 ms); with `Track` it passes. Parking
> inside the `InitializeHubRequest` post instead was measured and rejected: the teardown guard
> skips the post pipeline once the hub is past `DisposeHostedHubs`, so that park raced the very
> shutdown it waited for.

## The third window: a hub at ShutDown, still registered

The two windows above are about a hub accepting work, and a hub disposed before it was built.
This one is a hub that is **past serving and still findable** (#5136). The removal `Add` arms
runs inside the ShutDown phase's registrant walk — after `RunLevel` flipped, after
`CancelCallbacks`, after the reactive dispose actions — and from the flip on the hub refuses
every delivery at intake and faults a direct `Observe(...)` synchronously. A teardown that wedges
anywhere in that phase (a registered cleanup blocking on a lock — #4883's shape) never reaches
the removal, and `GetHubWithOutcome` answered the corpse as `Available` for as long as the wedge
lasted: one `portal/nodeops-…` hub, four callers, 55 minutes, on a pod serving everything else.

**The rule: an `Always` lookup never answers a hub at `ShutDown`.** It retires the corpse
(value-matched, so a successor a concurrent lookup registered is untouched) and mints a successor
through the ordinary single-flight creation. The corpse stays in the collection's disposal join
until its own `DisposalCompleted` — its scope is a child of the owner's, and the owner must not
close it under a registrant walk still running. The bound is `ShutDown` and not `IsDisposing` on
purpose: below `ShutDown` the hub is still draining accepted work and the intake's transient
`ShuttingDown` NACK is the designed answer (the first window above); a successor minted there
would overlap accepted writes. `Never` probes — the router's — still find the corpse and are
refused with that NACK, which is right for a message and wrong for a caller holding the
reference. The retirement is one `Warning` line, `[HOSTED-RETIRE]`, and it is the first line
that names a ShutDown-phase wedge by address. The three registries a teardown removes itself
from are all value-matched now — the hosted-hub registry (#4741), the local route (#5159), and
the pod-hub claim, whose `Detach` leaves a claim alone when the silo still holds a live route
for the address, because that route can only be a successor's. Full account and the measured
controls: [Disposed Scopes and Dying Hubs](../DisposedScopeAndDyingHubs), R2.

> Deterministic repro and control: `AHubAtShutDownIsNotHandedOutTest` (Messaging.Hub.Test)
> parks the ShutDown turn inside a reactive dispose action — invoked before the registrant walk
> — asserts the corpse is still registered, and measures both the successor and the owner's
> join. `PodHubStaleReleaseTest` (Hosting.Orleans.Test) drives the real grain on the two-silo
> cluster: a stale release leaves a live successor served and pinned; a release with no live
> route still tombstones.

---

## A recycle owes its subscribers a goodbye — and can only say it BEFORE the teardown

A routed `DisposeRequest` is a **recycle**, not an end: the address comes back on the next
access. *Why* anyone sends one — a hub binds its configuration once at activation and is then pinned
by address, so it serves that state until it is torn down — is
[Stale State Until a Recycle](/Doc/Architecture/StaleStateUntilRecycle). The automatic ones exist
*for* the people currently looking at a page —
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

**The seam is `RecycleAnnouncement`,** hung on the hub with `hub.Set(...)` and invoked as the FIRST
statement of `Dispose()` — i.e. on whatever turn starts the teardown, whether that is a routed
`DisposeRequest`'s handler or a direct call:

```csharp
// MessageHub.Dispose() — the first statement, before IsDisposing flips
AnnounceRecycleUnlessAnAncestorIsTakingUsWithIt();
//   if (IsShuttingDown) return;            // an ancestor's cascade — see below
//   one-shot, then Get<RecycleAnnouncement>()?.Announce()
```

🚨 **It used to hang on `HandleDispose` instead, and that keyed it on the wrong fact (#3986).** The
reasoning was that a routed request means *the address is coming back* while a direct `Dispose()` means
*the whole tree is going down*. The second half is false for `MessageHubGrain.OnDeactivateAsync`, which
calls `hub.Dispose()` directly for an address Orleans reactivates on the next message — and which this
codebase calls "the largest single source of direct `Dispose()` in the mesh" (#4888). So an owner grain
that deactivated told nobody, live mirrors elsewhere kept replaying a stale snapshot, and every user
action they sent afterwards was refused *"NO sync hub for this stream was EVER registered on the
current activation"* and thrown away. `HandleDispose` still ends in `Dispose()` on the same turn, so
the routed recycle is unchanged and still announces exactly once. Full account:
[Refusing a Lost User Action](../RefusingALostUserAction).

`Workspace` registers the one real implementation, because the client-subscription registry that
knows who is listening lives there. Three properties make it safe, and each is load-bearing:

| Property | Why |
|---|---|
| **Announced BEFORE `Dispose()`** | The hub is still whole: the registry is intact and `Configuration.ParentHub` still resolves. Both are gone or unreliable a phase later. |
| **DELIVERED after `DisposalCompleted`, through the PARENT hub** | The subscriber answers with ONE bounded re-ask. Delivered earlier, that re-ask lands on the still-dying instance, is NACKed `ShuttingDown`, and burns a budget meant for a genuinely non-converging owner. Delivered by the dying hub itself, it is the up-the-tree post that resurrects the activation. The parent outlives the target and speaks to a third party. |
| **Never for an ancestor's cascade** (`IsShuttingDown` already true) | There the address is *not* coming back, every subscriber is going down with it, and telling them to re-ask is exactly the resurrection the suppression exists to prevent. `HostedHubsCollection.CloseCreation` freezes the whole subtree the instant the ancestor's own `Dispose()` starts — strictly before it disposes its children — so a child torn down that way reads `IsShuttingDown` as already true and stays silent. A ROOT hub's host teardown is silent for the second reason instead: its parent resolves to itself, so the announcement finds no carrier that outlives it and posts nothing. |

Nothing here polls, retries or times out: `DisposalCompleted` is the event, and the re-ask it
triggers is the pre-existing bounded one. A recycle of a hub nobody is subscribed to costs
exactly what it did before.

Repros: `RecycleStrandsLiveSubscriberTest` (Hosting.Monolith.Test — a live layout-area
subscription re-converges on the re-activated hub after a bare `DisposeRequest`, with no node
write anywhere), `RecycleAnnouncementTest` (Messaging.Hub.Test — announced exactly once and before
the teardown, on a routed request AND on a direct `Dispose()`; silent only for an ancestor's
cascade) and `OwnerDeactivationTellsItsLiveSubscribersTest` (Layout.Test — the deactivation route
end to end: a click after its owner deactivated still runs).

> Only the *automatic* recycles were affected. `MeshOperations.Recycle` (the MCP tool) publishes a
> `MeshChangeEvent` for the path before its `DisposeRequest`, which is what fed the change-feed
> latch and made that path look fine; `RecycleLayoutArea` is driven by the page shell, which
> navigates on its own. The self-heal posted the dispose alone.

### A hub that recycles ITSELF queues the decision behind its own initialization

`DisposeRequest` passes every initialization gate, and it has to: an **external** teardown (an
operator recycle, a node delete, an owner's cascade) must reach a hub in any state, including one
whose gates never open. The three **self**-recyclers, though, are armed inside
`WithInitialization`: the overlay self-heal, `NodeTypeRebindWatcher` and the stale-build
convergence branch. They can decide to recycle before the gates open. The overlay self-heal does
it routinely: with no type node in hand it fires on the first usable replay of the NodeType
stream. A `DisposeRequest` posted at that moment overtakes every request the activation has
already parked behind `[DataContextInit, MeshNodeInit]`, and the disposal answers each one
`ShuttingDown` with a `[DISPOSE-DISCARD]` Error. Measured on memex-cloud (#5356):
`Deployments/build` discarded a cache client's two `SubscribeRequest`s and its
`UnsubscribeRequest` in one millisecond. The hub was going down for a reason it had decided
itself, with work it had accepted still in its own queue.

**The rule: a self-recycle goes through `hub.RecycleSelfAfterAcceptedWork(reason, logger)`,
never a direct self-post.** The helper posts the decision as an ordinary execution turn, as the
hub itself (the watchers' callbacks run on a publisher's thread with no `AccessContext`). The
turn is not a lifecycle message, so the gates defer it in arrival order with everything else.
When the last gate opens the backlog is restored in that order, the turn runs, and only then is
the `DisposeRequest` posted, behind every restored request. The ordinary quiesce drains the
rest, and the recycle's goodbye (above) re-converges the subscribers on the fresh activation.
Nothing waits on a timer: the gates settle through their own time-boxes (opened, or failed so the
backlog is answered), so the turn either runs or is answered. If some other teardown gets there
first, the parked turn is discarded as the hub's own delivery (Debug, not Error), and a turn
that finds the hub already disposing posts nothing. This does not replace
`hub.RecycleNode(path)`: that is how a surviving hub recycles *another* address.

> Deterministic repro and control: `OverlaySelfHealRecycleDrainsParkedWorkTest`
> (Compiler.Pipeline.Test) fires the self-heal's real recycle action,
> `NodeTypeEnrichmentHelpers.OverlaySelfHealRecycle`, at a hub whose gate the test holds closed,
> with a request parked before it and, in a second case, after it. With the action posting its
> `DisposeRequest` directly (the pre-fix body) both cases fail on a `ShuttingDown`
> `DeliveryFailureException`. With the helper, both requests are served, the hub still reaches
> `DisposalCompleted`, and no event 7301 Error is logged. A third case is the positive control: on
> an initialized hub the self-heal still recycles.

---

## A recycle of the main bit takes its dependency network with it — and instantiates nothing

A routed `DisposeRequest` on a **NodeType definition** is a recycle of everything built on that
definition, not of one hub. The definition's hub carries a second seam beside `RecycleAnnouncement`:

```csharp
// MessageHub.HandleDispose — on the recycle's own turn, BEFORE Dispose()
if (startsTheTeardown && request.Message.CascadedFrom is null)
    CascadeRecycle(request.Message);   // Get<RecycleCascade>()?.Cascade(request)
Dispose();                             // whose first statement makes the announcement
```

The cascade stays on `HandleDispose` because it needs the REQUEST — `DisposeRequest.CascadedFrom` is
what stops a cascade fanning out again — while the announcement needs only the hub, which is why the
two now sit in different places.

`NodeTypeNodeType` installs the one real `RecycleCascade` (`NodeTypeRecycleCascade`). It derives the
**dependency network** from the index — the NodeTypes whose sources reach into this type's tree
(`shared=@Type/Source/…`, transitively, the reverse of `NodeTypeDependencyGraph.Build`) and every
instance of the type and of each dependent — and posts one `DisposeRequest { CascadedFrom = type }`
per address **from the mesh's node-operation issuing hub**, a survivor. The definition hub computes;
it never delivers: a dying hub cannot deliver its own last frame.

Three properties, each load-bearing:

| Property | Why |
|---|---|
| **Derived ONCE, at the main node** | Every fanned-out request carries `CascadedFrom`, and `HandleDispose` never cascades a request that carries it. A cycle among NodeTypes (`A` shares from `B`, `B` from `A`) is therefore one wave, not a storm. |
| **Only what was instantiated is recycled** | The set comes from the index — every instance the mesh knows — but WHICH of them is live is a fact of the routing layer, and it is answered there: `MonolithRoutingService.RouteImpl` returns a `DisposeRequest` to an address with no registered stream `Ignored` without creating a hub, and `MessageHubGrain.DeliverMessage` answers one that reaches an activation which never built its hub `Ignored` and releases the activation. So a type with ten thousand instances and three open pages recycles three hubs. |
| **The check reads the ENVELOPE, not the type** | A delivery that crossed a hub boundary is still `RawJson` at the router and at the grain — it is deserialised only inside the target's `MessageService` — so `delivery.Message is DisposeRequest` matches an in-process post and nothing else (measured: the typed test never fired on Orleans and a cold grain built its hub for a dispose after all). Both sites go through `DisposeRequestEnvelope.TryRead`, which reads `$type`, `reason` and `cascadedFrom` off the packaged frame. 🚨 It matches the discriminator EXACTLY — see below. |
| **The hub build is deferred to the first non-dispose delivery** | Orleans activates a grain for ANY call; until this change that call also built the hub (node resolution, NodeType binding, assembly load). `OnActivateAsync` now COMPOSES the build and `EnsureActivationStarted` RUNS it on the first delivery that needs a hub. The self-routed own-address read the build issues arrives at `DeliverMessage` after the build has started, so it parks on `HubReady` exactly as before. |

### 🚨 The envelope matches the registry's discriminators, never a suffix

`DisposeRequestEnvelope` runs where **no hub has read the frame yet**, so it is the only thing between
an arbitrary sender's JSON and a routing decision taken on that sender's word. Stripping the namespace
off `$type` and accepting any final segment accepted `Attacker.DisposeRequest` as readily as the real
thing — and the router's dispose branch deliberately builds **no hub**, so naming your own type was a
way to suppress the activation a delivery to a cold address would otherwise cause. (It could not reach
`HandleDispose`, which is a different and larger claim: that path needs the registry to RESOLVE the
discriminator, and an unregistered one is failed in `MessageService.DeserializeDelivery` as *"type 'X'
is not registered in this hub's TypeRegistry"*.)

The accepted set is therefore exactly the two names `TypeRegistry` serves for the type, derived from
the type rather than written out: the **canonical** one it emits (`typeByName` is keyed by
`Type.Name`) and the dot-joined **full name** it accepts on the way in
(`TypeRegistry.IndexFullNameAlias`). Suffix-matching a type name is not authentication.

### 🚨 An enumeration leg that failed is not a leg that answered "none"

The network is read from the index in legs — one for the NodeType definitions, one per type for its
instances. A leg that times out or errors used to be caught into an **empty list**, which made
"this type has no live instances" and "nobody could find out" the same value: the cascade reported
success while every live hub for that type stayed on the old assembly, and a partial failure was
indistinguishable from a clean pass — the one outcome nobody re-checks.

Each leg now answers an `EnumerationLeg` that carries its `Failure`, and `NodeTypeRecycleCascade.Compose`
folds them into a `DependencyNetworkResult` with an `Incomplete` list beside the addresses. The cascade
still fans out to what it DID derive — those activations are genuinely stale, and a partial recycle
beats none — but an incomplete one logs at **`Error`**, naming every leg it lost and saying that an
unknown number of live hubs must be recycled by hand. An empty `Incomplete` is the only value that
means "this was the whole network".

What this does NOT do, deliberately: it recompiles nothing (the Recycle tool's forced release stamp on
the definition still does that, for the definition alone — a dependent recompiles through
`ReleaseAffectedNodeTypes` when its shared sources change), it clears no data, and it touches
neither other replicas nor any process-wide cache — the same limits every recycle has.

Repros: `RecycleCascadeTest` (Messaging.Hub.Test — once, with the request, before the teardown; a
cascaded request never cascades; a direct `Dispose()` never cascades), `NodeTypeRecycleCascadeTest`
(Graph.Test — the network: transitive dependents, instances, deterministic order, a cycle),
`ADisposeRequestNeverInstantiatesAHubTest` (Hosting.Test, Monolith — a cold address stays cold; a
live one is recycled and reactivates) and `AColdGrainAnswersADisposeWithoutBuildingItsHubTest`
(Hosting.Orleans.Test — the grain answers `Ignored` and registers no stream).

Maintainer, 2026-09-18: *"everything involved must be properly disposed ⇒ implement logic mesh side
and just recycle main bit. all sub-bits (dependency network) should only be recycled if instantiated
in the first place ⇒ handle dispose request on grain level if hub is not instantiated. don't
instantiate in this case."*

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

## 🚨 Testing that something is UNREACHABLE after disposal

The consequence of everything above, stated where the cause lives: **`Dispose()` returning is not
the teardown having happened.** It freezes hosted-hub creation, posts `ShutdownRequest(Quiescing)`
and returns; the phases run afterwards as fresh messages. So on return the hub is still rooted by
its own in-flight shutdown — the action block, the scheduler, the registry entry.

A test that takes a `WeakReference`, calls `Dispose()`, forces a GC and asserts unreachability is
therefore not measuring the reference graph; it is measuring whether the teardown happened to finish
first. That produces the worst available failure mode — **a green one**. Measured on
`StreamReleasesItsHubTest` (#3321): it passed inside a 485-test project and failed the moment it ran
alone, same binary, nothing rebuilt.

Join `DisposalCompleted` and only then collect. After that signal the hub is `Dead` and its own
machinery has let go, so the only thing that can still hold it is a reference somebody kept — which
is the claim such a test exists to make. The mechanics (the non-inlined helper, and why holding
`DisposalCompleted` itself cannot root the hub) are in
[Writing Tests](/Doc/Architecture/WritingTests); the leak-hunting recipe is in
[Debugging Disposal and Leaks](/Doc/Architecture/DebuggingDisposalAndLeaks).

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
