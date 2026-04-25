---
NodeType: "Doc/Article"
Title: "Per-Hub TaskScheduler — Actor Isolation Across the Mesh"
Abstract: "Each MeshWeaver hub is its own actor and runs on its own TaskScheduler. The root grain hub uses the Orleans grain scheduler so Orleans attributes work to the grain. Every other hub — hosted hubs, per-node hubs, _Exec, kernel hubs — uses TaskScheduler.Default by default. WithTaskScheduler is the one knob; this page explains how it's plumbed and why each hub class gets the scheduler it does."
Icon: "Cogs"
Published: "2026-04-25"
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

## Cross-references

- [Asynchronous Calls](AsynchronousCalls) — the actor-model rules this implements.
- [Thread Execution Streaming](ThreadExecutionStreaming) — the streaming-loop pattern that depends on this isolation.
- [Debugging Message Flow](DebuggingMessageFlow) — how to recognise a scheduler-sharing deadlock in trace logs.
