---
NodeType: Markdown
Name: "Per-Hub TaskScheduler — Actor Isolation Across the Mesh"
Abstract: "Each MeshWeaver hub is its own actor running on its own TaskScheduler. The root grain hub uses the Orleans grain scheduler so Orleans can attribute work correctly. Every other hub — hosted, per-node, _Exec, kernel — runs on TaskScheduler.Default. WithTaskScheduler is the single configuration knob; this page explains how it is plumbed and why each hub class gets the scheduler it does."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#455a64'/><circle cx='9' cy='10' r='3' fill='white'/><circle cx='9' cy='10' r='1.2' fill='#455a64'/><circle cx='16' cy='15' r='2.5' fill='white'/><circle cx='16' cy='15' r='1' fill='#455a64'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Threading"
  - "Orleans"
  - "ActorModel"
---

> **Read first:** [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls). This page is the threading-model substrate that those rules rest on.

## The model

Every hub is an actor. Each actor has two components:

- A **single-threaded `ActionBlock<>`** (`MaxDegreeOfParallelism = 1`) that processes messages serially.
- A **`TaskScheduler`** onto which the action block dispatches its work.

Two hubs can never share an action block. Two hubs *can* share a `TaskScheduler` — and that sharing is precisely the failure mode this design exists to eliminate. When two hubs share a single-threaded scheduler, they collapse into one effective actor: an `await` inside one hub's handler queues a continuation that must run on the same thread the other hub's handler is already holding. Tool-call responses can't be delivered while a streaming handler is waiting for them. The result is a deadlock.

The rule is simple:

| Hub | TaskScheduler |
|---|---|
| **Root grain hub** — the hub whose address matches the Orleans grain key | The grain's own scheduler. Orleans needs continuations on this scheduler to attribute work to the grain. |
| **Every other hub** — hosted hubs, per-node hubs, `_Exec`, kernel hubs, broadcast hubs | `TaskScheduler.Default` (thread pool). Each is its own actor with its own independent scheduler. |

<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="currentColor" fill-opacity="0.55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="320" rx="12" fill="#1a1a2e" fill-opacity="0.5"/>
  <rect x="30" y="20" width="700" height="80" rx="10" fill="#1565c0" fill-opacity="0.9"/>
  <text x="380" y="48" text-anchor="middle" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff">Orleans Grain</text>
  <rect x="60" y="58" width="300" height="34" rx="8" fill="#0d47a1"/>
  <text x="210" y="78" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff">Root Grain Hub</text>
  <rect x="400" y="58" width="300" height="34" rx="8" fill="#1976d2" fill-opacity="0.8"/>
  <text x="550" y="72" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#e3f2fd">ActionBlock (MaxDOP=1)</text>
  <text x="550" y="86" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#90caf9">TaskScheduler = grain scheduler</text>
  <line x1="380" y1="100" x2="160" y2="148" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="100" x2="380" y2="148" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="100" x2="600" y2="148" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="60" y="148" width="200" height="60" rx="10" fill="#2e7d32"/>
  <text x="160" y="170" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Hosted Hub</text>
  <text x="160" y="186" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#a5d6a7">TaskScheduler.Default</text>
  <rect x="60" y="220" width="200" height="60" rx="10" fill="#1b5e20" fill-opacity="0.85"/>
  <text x="160" y="243" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#c8e6c9">ActionBlock (MaxDOP=1)</text>
  <text x="160" y="259" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#81c784">thread-pool thread</text>
  <rect x="280" y="148" width="200" height="60" rx="10" fill="#6a1b9a"/>
  <text x="380" y="170" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Per-Node / _Exec Hub</text>
  <text x="380" y="186" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#e1bee7">TaskScheduler.Default</text>
  <rect x="280" y="220" width="200" height="60" rx="10" fill="#4a148c" fill-opacity="0.85"/>
  <text x="380" y="243" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#e1bee7">ActionBlock (MaxDOP=1)</text>
  <text x="380" y="259" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#ce93d8">thread-pool thread</text>
  <rect x="500" y="148" width="200" height="60" rx="10" fill="#e65100"/>
  <text x="600" y="170" text-anchor="middle" font-family="sans-serif" font-size="12" font-weight="bold" fill="#fff">Kernel Hub</text>
  <text x="600" y="186" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#ffe0b2">TaskScheduler.Default</text>
  <rect x="500" y="220" width="200" height="60" rx="10" fill="#bf360c" fill-opacity="0.85"/>
  <text x="600" y="243" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ffe0b2">ActionBlock (MaxDOP=1)</text>
  <text x="600" y="259" text-anchor="middle" font-family="sans-serif" font-size="10" fill="#ffcc80">thread-pool thread</text>
  <line x1="160" y1="208" x2="160" y2="220" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="208" x2="380" y2="220" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="600" y1="208" x2="600" y2="220" stroke="currentColor" stroke-opacity="0.35" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="380" y="302" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity="0.55">Each child hub runs on its own independent thread-pool slot — no shared scheduler, no cross-hub deadlock.</text>
</svg>

*Hub scheduler topology: the root grain hub stays on the Orleans grain scheduler; every child hub gets its own independent `TaskScheduler.Default` action block.*

## Why the root grain hub uses the grain scheduler

Orleans installs a per-grain `TaskScheduler` so that everything happening "inside the grain" runs on a single, grain-affined thread. This affinity gives Orleans several important guarantees:

- **Activity attribution** — idle-deactivation timing measures actual grain work, not work that bounced through the thread pool.
- **`RequestContext` flow** — state set inside a grain method propagates through `await` continuations.
- **Distributed-tracing scopes** — spans are correctly attributed to the grain.
- **Lifecycle hooks** — `OnActivate` / `OnDeactivate` fire on the grain scheduler and see consistent grain state.
- **Single-threaded grain semantics** — only one continuation runs at a time (`[Reentrant]` notwithstanding).

If the root grain hub's action block ran on `TaskScheduler.Default` instead, Orleans would observe only the entry-point grain method call and miss all subsequent work the hub processes after picking up a message.

`MessageHubGrain.OnActivateAsync` captures `TaskScheduler.Current` at activation time and passes it via `.WithTaskScheduler(grainScheduler)` when building the root hub.

## Why every other hub uses `TaskScheduler.Default`

Hosted hubs, per-node hubs, `_Exec`, and kernel hubs are not the grain. They are sibling actors created from the grain hub's perspective but live independently. If any of them shared the grain's scheduler, every `await` inside them would serialize through the grain's single thread — exactly the cross-hub deadlock scenario described above.

The default for `MessageHubConfiguration.TaskScheduler` is `null`. `MessageService` resolves that to `TaskScheduler.Default` (the thread-pool scheduler). The thread pool can run multiple continuations concurrently, so async work inside one hub never blocks async work inside another.

`TaskScheduler.Default` is the *static* thread-pool scheduler — fixed at process start and unaffected by the ambient `TaskScheduler.Current` of whatever code constructed the hub. A hosted hub created from inside a grain method (where `TaskScheduler.Current == grainScheduler`) still runs on the thread pool. **There is no implicit capture.**

## The configuration knob — `WithTaskScheduler`

```csharp
public record MessageHubConfiguration
{
    public TaskScheduler? TaskScheduler { get; init; }

    public MessageHubConfiguration WithTaskScheduler(TaskScheduler scheduler)
        => this with { TaskScheduler = scheduler };
}
```

Inside `MessageService`, both action blocks are wired to the same resolved scheduler:

```csharp
var blockOptions = new ExecutionDataflowBlockOptions
{
    TaskScheduler          = hub.Configuration.TaskScheduler ?? TaskScheduler.Default,
    MaxDegreeOfParallelism = 1   // actor semantics — one message at a time
};

deliveryAction = new ActionBlock<Func<Task<IMessageDelivery>>>(async x =>
    {
        try { await x.Invoke(); }
        catch (Exception ex) { logger.LogError(ex, "...", address); }
    },
    blockOptions);

executionBlock = new ActionBlock<Func<CancellationToken, Task>>(
    f => f.Invoke(default),
    blockOptions);
```

`MaxDegreeOfParallelism = 1` is what enforces the actor invariant. The scheduler determines *where* those serial tasks run.

## What this fixes — the cross-hub deadlock

The sequence this design rules out:

1. Hub A starts handling a message; its handler `await`s a round-trip to hub B.
2. Without `.ConfigureAwait(false)`, the await captures `TaskScheduler.Current` as the resumption scheduler.
3. If A and B share that scheduler and it is single-threaded, the response from B must be dispatched through it. But the scheduler is busy holding A's handler, which is blocked waiting for the response. The response can't run. **Deadlock.**

With per-hub schedulers, A and B each have their own thread-pool slots. The await releases A's slot; B's response runs on its own slot; A's continuation resumes when the slot is available. No collision.

## What this does NOT fix — the same-hub `await` problem

Per-hub schedulers resolve *cross-hub* deadlocks. They do not resolve this pattern:

```csharp
// Inside hub X's handler
async IMessageDelivery Handle(...) {
    await foreach (var update in someStream) { ... }   // long-running
}
```

The `await foreach` holds hub X's action block and prevents it from processing the next message. If the response that advances `MoveNextAsync` also needs to be processed by hub X, the hub is busy holding its own handler's task — a same-hub deadlock.

The sanctioned mitigation is `Task.Run` to detach the long-running body from the action block. That pattern is used by `ThreadExecution.ExecuteMessageAsync` for streaming bodies that contain awaits needing the actor's own scheduler to complete. See [Thread Execution Streaming](/Doc/Architecture/ThreadExecutionStreaming) for the worked example.

## `SubscribeOn(TaskPoolScheduler.Default)` inside a grain-hosted service

`IMeshNodeStreamCache.GetQuery` and `MeshQuery.Query` / `.Query` wrap their inner observables with `SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)`. When the cache hub lives inside an Orleans grain, this is **intentional and correct**. Here is what happens step by step:

1. **The grain method call (`GetQuery(id, queries)`) runs on the grain scheduler.** Orleans serialises access; the `Interlocked.CompareExchange` on the cache's `_queries` dictionary is safe by virtue of single-threaded grain execution. The method returns an `IObservable<T>` description and the grain releases immediately.

2. **`Subscribe` is called outside the grain method's scope** — typically from a downstream consumer such as a layout area, a validator, or another service. The grain holds no lock at this point.

3. **`SubscribeOn(TaskPoolScheduler.Default)`** shifts the subscribe-time work — constructing `SyncedQueryMeshNodes`, opening the upstream `IMeshQueryCore.Query` subscription, opening database connections and change feeds — onto a thread-pool thread. That is the right place for I/O. Without this offload, those subscriptions would run on whatever thread called `Subscribe`, which could be the grain scheduler if the caller is mid-handler.

4. **Emissions** (`OnNext` to the cache's `Replay(1).RefCount()` and onward to downstream subscribers) flow on whatever thread the upstream emits from — PostgreSQL change-feed threads, Orleans observer dispatchers, and so on. The cache's internal `Replay(1)` buffer is thread-safe; downstream subscribers reading from it are also safe.

### The risk this does not create — and the risk it does not eliminate

The pattern is safe with respect to the cache's own state. The risk that remains is a *consumer-side* concern: if a downstream observer's `OnNext` directly mutates grain state without going through a grain interface call, that is a single-threading violation. The fix lives at the consumer, not at the cache:

```csharp
// In a consumer that holds grain affinity:
cache.GetQuery(id, queries)
    .ObserveOn(grainContext.Scheduler)   // ← re-enter the grain for callbacks
    .Subscribe(snapshot => /* now safe to touch grain state */);
```

The `SubscribeOn` at the cache layer does not widen this risk — upstream change-feed emissions were already arriving from background threads regardless. The offload shifts only the *subscribe-time work*, which is precisely what you want off the grain.

> This `SubscribeOn` offload is the **query-construction** half of keeping the grain free. Its leaf-execution counterpart is the [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) primitive (`IIoPool`), which applies the same `SubscribeOn(TaskPoolScheduler.Default)` move plus a concurrency bound so actual file / blob / HTTP leaf work both runs off the grain *and* cannot fan out unboundedly.

## 🚨 The layout-area render pipeline subscribes OFF the hub turn — query-in-render is safe

**This is the third — and highest-leverage — place the framework makes the same `SubscribeOn` move**,
and it closes the deadlock class that took down multiple production meshes.

A layout area's view generator returns an `IObservable<UiControl?>`. The framework's render pipeline
(`LayoutAreaHost.BuildInitialization`, and every nested container / dialog / editor sub-area render)
**subscribes** to it to drive content to the client. That subscribe runs on the layout-area's own
synchronisation-stream hub **action block** — a single-threaded actor. When the layout area belongs to
a node hosted as an Orleans grain, its owning workspace hub is the **root grain hub on the grain
scheduler** (the table above). So, before the fix, the generator body — and the subscribe to whatever
observable it returned — ran **on the grain turn**.

That is the query-in-render trap:

1. A view generator does, in-render, `IMeshService.Query(...)` (or `hub.Observe`, `GetRemoteStream`, a
   workspace query — any mesh round-trip).
2. The render pipeline subscribes it **on the grain turn**.
3. The query must route through Orleans and come back to **this grain** to resolve. But the grain turn
   is held inside the subscribe → the response can never be processed → **the hub deadlocks.**
4. On startup prerender, many such grains block at once → thread-pool starvation → the whole silo
   wedges (even `/healthz`). Confirmed offenders: `Doc/DataMesh/SocialMedia/Post` (List area) and
   `Doc/DataMesh/PythonPandasNode/PandasExplorer`.

**The fix — one reactive seam.** `LayoutAreaHost` wraps the render subscription with
`.SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)` — the identical move made at
`MeshQuery.Query` and `IMeshNodeStreamCache.GetQuery` (the section just above). The generator, and
every observable it subscribes in-render, now runs **off the grain turn**, which is immediately free
to route + answer the round-trip. A pure Rx scheduler operator — **no `async` / `await` / `Task`.**

```csharp
// LayoutAreaHost.BuildInitialization — the top-level render subscription:
var renderSubscription = BuildRenderObservable(context, baseStore)
    .SubscribeOn(TaskPoolScheduler.Default)   // ← subscribe OFF the owning (grain) hub's turn
    .Subscribe(PushRenderResult, FailRendering);

// The same hop guards every nested container/dialog/editor sub-area render in LayoutAreaHost.RenderArea,
// because those subscribes are reached from UpdateArea, which runs ON the hub turn (Stream.Update →
// UpdateStreamRequest). A nested area that queries in-render must be off-hub too.
```

**Why it can't reintroduce ordering bugs.** The render *output* (`PushRenderResult` / `UpdateArea`)
never touches the hub directly — it calls `Stream.Update(...)`, which posts an `UpdateStreamRequest`
to the hub's action block (`hub.Post` is the actor inbox: safe from any thread, re-serialised in
order). So emissions arriving on a pool thread are re-marshalled onto the owning hub's single-threaded
turn exactly as before — the offload moves only the **subscribe-time** work off the hub, never the
state writes. Data-before-control ordering is preserved (regression-guarded by the full
`MeshWeaver.Layout.Test` suite, incl. `EditorTest` / `ContainerControlAreaNestingTest`).

**Why this is the robust fix and not "author around it."** The safety lives in the portal/framework
binary, so **existing deployed nodes — whose cached assemblies never recompile — become safe with no
code change.** The prior guidance ("move mesh queries into virtual data sources, never
`IMeshService.Query` in a view") was a per-node *workaround*; this is the framework making the whole
class safe at one seam. Regression-pinned by `QueryInRenderDeadlockTest` (a layout area doing an
in-render mesh round-trip on a hub-turn-blocking subscribe: **hangs without the offload, renders with
it**).

> **Reactive, not blocking, still required.** The offload subscribes you off the hub turn; it does not
> make a *blocking* subscribe free. A generator that bridges a round-trip back to a blocking `Task`
> (`.Result` / `.Wait()` / `.ToTask()` + wait) still burns a pool thread. Compose reactively and
> return the `IObservable<UiControl?>` — see [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls).

## Offloading a CPU leaf off the action block — `Task.Run`, identity, and why the gated pool can wedge

A long *synchronous* leaf (Roslyn `Emit`, a big reflection/type-load) that runs
**inline on a hub handler** wedges that hub for its whole duration. The canonical
trap: `CompileAsync(...).ToObservable()`. For an in-memory compile `CompileAsyncCore`
has **no `await` before the synchronous `Emit`**, so `CompileAsync()` runs the *entire*
compile synchronously, and `.ToObservable()` runs it on **whatever thread subscribed**
— the activity hub's action block — freezing the mesh (≈50 s of trace silence). Three
rules came out of fixing it:

1. **Never `Task.ToObservable()` a leaf you want off the action block.** The Task's
   continuation resumes on the **current** `TaskScheduler` (the action block when you
   subscribed mid-handler), so it comes right back onto the blocked turn.

2. **`Task.Run`, not the IoPool, for a CPU leaf that re-enters or must not gate.**
   `_ioPool.Run` was *worse* here for two reasons: (a) its `SemaphoreSlim` gate
   serialised the compile against itself — the activity-driven and request-driven
   compiles both took the `Compile` pool slot and the synchronous single-flight
   re-entered / parked on `WaitAsync` (an **idle** wait — `dotnet-stack` shows no
   blocked thread, the missed-observation signature); (b) `SubscribeOn(TaskPoolScheduler.Default)
   + ConfigureAwait(false)` hops threads and **drops the AccessContext** exactly like
   `Task.Run`. `Task.Run` schedules on `TaskScheduler.Default` (the thread pool) with no
   gate and no current-scheduler capture. Bridge it to `IObservable` with
   `Observable.Create` + `Task.Run` + `ContinueWith(…, TaskScheduler.Default)` — never
   `FromAsync`, never `Task.ToObservable`.

3. **🚨 Re-establish the login *inside* the offload.** Whichever way you hop threads
   (`Task.Run` or the IoPool), the `AccessService` identity (an `AsyncLocal`) does **not**
   flow across the hop, and the handler's `ImpersonateAsSystem` scope is long disposed by
   the time the offloaded work runs. So compile source-reads / write-backs run
   **unauthenticated** off-thread. Wrap the offloaded body: `using (accessService.ImpersonateAsSystem())
   return await work();` — inside the async lambda so the scope spans every await.
   *"Why doesn't the IoPool just work — it's the correct abstraction?"* It is the correct
   abstraction for **bounded async I/O**; for a **CPU leaf with grain-identity needs** it
   adds a gate you don't want and still drops the identity. The pool offloads the work;
   it does **not** carry the actor's `AccessContext` — that is the caller's job at the seam.

> The flip side (per the section above): work that **touches hub state** after the
> offload must `.ObserveOn(grainScheduler)` back onto the owning hub's turn — the CPU
> runs off-grain, the state mutation runs on-grain.

## Cross-references

- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — the actor-model rules this scheduling model implements.
- [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — bounds the leaf I/O that this scheduling model offloads.
- [Thread Execution Streaming](/Doc/Architecture/ThreadExecutionStreaming) — the streaming-loop pattern that depends on this isolation.
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — how to recognise a scheduler-sharing deadlock in trace logs.
