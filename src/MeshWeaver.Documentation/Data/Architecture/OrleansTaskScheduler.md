---
NodeType: Markdown
Name: "Per-Hub TaskScheduler — Actor Isolation Across the Mesh"
Abstract: "Each MeshWeaver hub is its own actor and runs on its own TaskScheduler. The root grain hub uses the Orleans grain scheduler so Orleans attributes work to the grain. Every other hub — hosted hubs, per-node hubs, _Exec, kernel hubs — uses TaskScheduler.Default by default. WithTaskScheduler is the one knob; this page explains how it's plumbed and why each hub class gets the scheduler it does."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#455a64'/><circle cx='9' cy='10' r='3' fill='white'/><circle cx='9' cy='10' r='1.2' fill='#455a64'/><circle cx='16' cy='15' r='2.5' fill='white'/><circle cx='16' cy='15' r='1' fill='#455a64'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Threading"
  - "Orleans"
  - "ActorModel"
---

> Read first: [Asynchronous Calls](AsynchronousCalls). This page is the threading-model substrate those rules sit on.

## The model

Every hub is an actor. Each actor has:

- A single-threaded `ActionBlock<>` (`MaxDegreeOfParallelism = 1`) that processes messages serially.
- A `TaskScheduler` that the action block dispatches its work onto.

Two hubs cannot share an action block. Two hubs **can** share a `TaskScheduler` — and that's the failure mode this design exists to prevent. Two hubs sharing a single-threaded `TaskScheduler` collapse into one effective actor: any `await` inside one hub's handler queues a continuation that has to run on the same thread the other hub's handler is currently holding. Cross-hub work serializes through that thread. Tool-call responses can't be delivered while a streaming handler is awaiting them — deadlock.

The rule:

| Hub | TaskScheduler |
|---|---|
| **Root grain hub** (the hub whose address matches the Orleans grain key) | The grain's scheduler. Orleans needs continuations on this scheduler to attribute work to the grain. |
| **Every other hub** — hosted hubs, per-node hubs, `_Exec`, kernel hubs, broadcast hubs | `TaskScheduler.Default` (thread pool). Each is its own actor with its own scheduler. |

## Why the grain hub uses the grain scheduler

Orleans installs a per-grain `TaskScheduler` so that everything happening "inside the grain" runs on a single, grain-affined thread. That gives Orleans:

- **Activity attribution**: idle-deactivation timing measures actual grain work, not work that bounced through the thread pool.
- **`RequestContext` flow**: state set inside a grain method propagates through `await` continuations.
- **Distributed-tracing scopes**: spans are correctly attributed to the grain.
- **`OnActivate` / `OnDeactivate` hooks** fire on the grain scheduler so they see consistent grain state.
- **Single-threaded grain semantics** (`[Reentrant]` notwithstanding) — only one continuation at a time.

If the root grain hub's action block ran on `TaskScheduler.Default` instead, Orleans would see only the entry-point grain method and miss everything that happens after the hub picks up a message.

`MessageHubGrain.OnActivateAsync` captures `TaskScheduler.Current` at activation time and passes it via `.WithTaskScheduler(grainScheduler)` when building the root hub.

## Why every other hub uses `TaskScheduler.Default`

Hosted hubs, per-node hubs, `_Exec`, kernel hubs are not the grain. They're sibling actors created from the grain hub's perspective but live independently. If they shared the grain's scheduler, every `await` inside any of them would serialize through the grain's single thread — and the cross-hub deadlock above kicks in.

The default for `MessageHubConfiguration.TaskScheduler` is `null`, which `MessageService` resolves to `TaskScheduler.Default` (the thread-pool scheduler). The thread pool can run multiple continuations concurrently, so async work inside one hub never blocks async work inside another.

`TaskScheduler.Default` is the *static* thread-pool scheduler — fixed at process start, unaffected by the ambient `TaskScheduler.Current` of whatever code constructed the hub. So a hosted hub created from inside a grain method (where `TaskScheduler.Current == grainScheduler`) still runs on the thread pool. **No implicit capture.**

## The configuration knob — `WithTaskScheduler`

```csharp
public record MessageHubConfiguration
{
    public TaskScheduler? TaskScheduler { get; init; }

    public MessageHubConfiguration WithTaskScheduler(TaskScheduler scheduler)
        => this with { TaskScheduler = scheduler };
}
```

Plumbing inside `MessageService`:

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

Both ActionBlocks use the same scheduler. `MaxDegreeOfParallelism = 1` keeps the actor invariant.

## What this fixes — the cross-hub deadlock

The failure mode this design rules out:

1. Hub A starts processing a message; the handler `await`s on a hub round-trip to hub B.
2. Without `await ... .ConfigureAwait(false)`, the await captures `TaskScheduler.Current` to resume on.
3. If A and B share that scheduler, the response from B has to be dispatched through it. The scheduler is single-threaded and currently holding A's handler. The handler is blocked waiting on the response. The response can't run because the thread is busy. **Deadlock.**

With per-hub schedulers, A and B each have their own thread-pool slots. The await releases A's slot; the response runs on B's slot; the continuation resumes on A's slot. No collision.

## What this does NOT fix — the same-hub `await` problem

Per-hub schedulers fix cross-hub deadlocks. They do not fix this:

```csharp
// Inside hub X's handler
async IMessageDelivery Handle(...) {
    await foreach (var update in someStream) { ... }   // long-running
}
```

The `await foreach` blocks hub X's action block from processing the next message. If the response that completes the next `MoveNextAsync` needs to be processed by hub X, X is busy holding its own handler's task — same-hub deadlock.

The current mitigation is `Task.Run` to detach the long-running body from the action block (see `ThreadExecution.ExecuteMessageAsync`). That's a sanctioned pattern for streaming bodies that contain awaits which need the actor's own scheduler to complete. See [Thread Execution Streaming](ThreadExecutionStreaming) for the worked example.

## `SubscribeOn(TaskPoolScheduler.Default)` inside a grain-hosted service

`IMeshNodeStreamCache.GetQuery` and `MeshQuery.ObserveQuery` / `.Query` wrap their inner observables with `SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)`. When the cache hub runs inside an Orleans grain, this is **intentional and correct** — here's what actually happens:

1. **The grain method call (`GetQuery(id, queries)`) runs on the grain scheduler.** Orleans serializes; the `Interlocked.CompareExchange` on the cache's `_queries` dictionary is safe by virtue of the grain's single-threaded execution. The method returns an `IObservable<T>` description and the grain releases.

2. **`Subscribe` is called outside the grain method's scope** — typically from a downstream consumer (a layout area, a validator, another service). The grain doesn't hold its lock for this.

3. **`SubscribeOn(TaskPoolScheduler.Default)`** then shifts the subscribe-time work — constructing `SyncedQueryMeshNodes`, opening the upstream `IMeshQueryCore.ObserveQuery` subscription, opening DB connections / change feeds — onto a thread-pool thread. That's the right place for I/O. Without this offload, those subscriptions would otherwise run on whatever thread happened to call `Subscribe`, including the grain scheduler if the caller is mid-handler.

4. **Emissions** (`OnNext` to the cache's `Replay(1).RefCount()` and onward to downstream subscribers) flow on whatever thread the upstream emits from — PostgreSQL change-feed threads, Orleans observer dispatchers, etc. The cache's internal `Replay(1)` buffer is thread-safe; reads from it by other subscribers are also safe.

### The bug shape this does **not** create — and the bug shape it does **not** fix

The pattern is safe with respect to the cache's own state. The risk that remains is a *consumer-side* bug: if a downstream observer's `OnNext` directly mutates grain state (without going through a grain interface call), that's a single-threading violation. The fix lives at the consumer, not at the cache:

```csharp
// In a consumer that holds grain affinity:
cache.GetQuery(id, queries)
    .ObserveOn(grainContext.Scheduler)   // ← re-enter the grain for callbacks
    .Subscribe(snapshot => /* now safe to touch grain state */);
```

The `SubscribeOn` at the cache layer doesn't widen this risk — the upstream change-feed emissions were already coming from background threads regardless of `SubscribeOn`. The offload only shifts the *subscribe-time work*, which is exactly what you want off the grain.

## Cross-references

- [Asynchronous Calls](AsynchronousCalls) — the actor-model rules this implements.
- [Thread Execution Streaming](ThreadExecutionStreaming) — the streaming-loop pattern that depends on this isolation.
- [Debugging Message Flow](DebuggingMessageFlow) — how to recognise a scheduler-sharing deadlock in trace logs.
