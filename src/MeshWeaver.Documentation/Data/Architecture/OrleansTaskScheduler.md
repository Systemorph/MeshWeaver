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

> **Read first:** [Asynchronous Calls](AsynchronousCalls). This page is the threading-model substrate that those rules rest on.

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

The sanctioned mitigation is `Task.Run` to detach the long-running body from the action block. That pattern is used by `ThreadExecution.ExecuteMessageAsync` for streaming bodies that contain awaits needing the actor's own scheduler to complete. See [Thread Execution Streaming](ThreadExecutionStreaming) for the worked example.

## `SubscribeOn(TaskPoolScheduler.Default)` inside a grain-hosted service

`IMeshNodeStreamCache.GetQuery` and `MeshQuery.ObserveQuery` / `.Query` wrap their inner observables with `SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)`. When the cache hub lives inside an Orleans grain, this is **intentional and correct**. Here is what happens step by step:

1. **The grain method call (`GetQuery(id, queries)`) runs on the grain scheduler.** Orleans serialises access; the `Interlocked.CompareExchange` on the cache's `_queries` dictionary is safe by virtue of single-threaded grain execution. The method returns an `IObservable<T>` description and the grain releases immediately.

2. **`Subscribe` is called outside the grain method's scope** — typically from a downstream consumer such as a layout area, a validator, or another service. The grain holds no lock at this point.

3. **`SubscribeOn(TaskPoolScheduler.Default)`** shifts the subscribe-time work — constructing `SyncedQueryMeshNodes`, opening the upstream `IMeshQueryCore.ObserveQuery` subscription, opening database connections and change feeds — onto a thread-pool thread. That is the right place for I/O. Without this offload, those subscriptions would run on whatever thread called `Subscribe`, which could be the grain scheduler if the caller is mid-handler.

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

> This `SubscribeOn` offload is the **query-construction** half of keeping the grain free. Its leaf-execution counterpart is the [Controlled I/O Pooling](ControlledIoPooling) primitive (`IIoPool`), which applies the same `SubscribeOn(TaskPoolScheduler.Default)` move plus a concurrency bound so actual file / blob / HTTP leaf work both runs off the grain *and* cannot fan out unboundedly.

## Cross-references

- [Asynchronous Calls](AsynchronousCalls) — the actor-model rules this scheduling model implements.
- [Controlled I/O Pooling](ControlledIoPooling) — bounds the leaf I/O that this scheduling model offloads.
- [Thread Execution Streaming](ThreadExecutionStreaming) — the streaming-loop pattern that depends on this isolation.
- [Debugging Message Flow](DebuggingMessageFlow) — how to recognise a scheduler-sharing deadlock in trace logs.
