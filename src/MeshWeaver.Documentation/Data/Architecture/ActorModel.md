---
Name: Actor Model Architecture
Category: Architecture
Description: How single-threaded message processing eliminates race conditions, why blocking the hub thread deadlocks, and the reactive patterns that avoid it
Icon: Lock
---

MeshWeaver's `MessageHub` is built on the **Actor Model**: every hub owns a private, single-threaded action block, and messages are processed one at a time in arrival order. That guarantee eliminates races and removes the need for locks on hub-local state — but it comes with a non-negotiable rule: **never block the hub thread**.

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 280" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <path d="M0,0 L8,3 L0,6 Z" fill="#90caf9"/>
    </marker>
    <marker id="arr2" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <path d="M0,0 L8,3 L0,6 Z" fill="#a5d6a7"/>
    </marker>
    <marker id="arr3" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
      <path d="M0,0 L8,3 L0,6 Z" fill="#ffcc80"/>
    </marker>
  </defs>
  <rect x="20" y="30" width="200" height="210" rx="12" fill="none" stroke="#1e88e5" stroke-opacity=".5" stroke-width="1.5"/>
  <text x="120" y="55" text-anchor="middle" fill="#90caf9" font-family="sans-serif" font-size="13" font-weight="700">Hub A</text>
  <rect x="40" y="65" width="160" height="22" rx="6" fill="#1e3a5f"/>
  <text x="120" y="80" text-anchor="middle" fill="#90caf9" font-family="sans-serif" font-size="11">msg 1</text>
  <rect x="40" y="92" width="160" height="22" rx="6" fill="#1e3a5f"/>
  <text x="120" y="107" text-anchor="middle" fill="#90caf9" font-family="sans-serif" font-size="11">msg 2</text>
  <rect x="40" y="119" width="160" height="22" rx="6" fill="#1e3a5f"/>
  <text x="120" y="134" text-anchor="middle" fill="#90caf9" font-family="sans-serif" font-size="11">msg 3</text>
  <line x1="120" y1="148" x2="120" y2="156" stroke="#90caf9" stroke-opacity=".4" stroke-width="1.5" stroke-dasharray="3 2"/>
  <rect x="40" y="163" width="160" height="34" rx="8" fill="#1e88e5"/>
  <text x="120" y="178" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Action Block</text>
  <text x="120" y="193" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="10">(single thread)</text>
  <rect x="40" y="205" width="160" height="22" rx="6" fill="#1a3050"/>
  <text x="120" y="220" text-anchor="middle" fill="#90caf9" font-family="sans-serif" font-size="10">private state (no locks)</text>
  <rect x="280" y="30" width="200" height="210" rx="12" fill="none" stroke="#43a047" stroke-opacity=".5" stroke-width="1.5"/>
  <text x="380" y="55" text-anchor="middle" fill="#a5d6a7" font-family="sans-serif" font-size="13" font-weight="700">Hub B</text>
  <rect x="300" y="65" width="160" height="22" rx="6" fill="#1a3320"/>
  <text x="380" y="80" text-anchor="middle" fill="#a5d6a7" font-family="sans-serif" font-size="11">msg 1</text>
  <rect x="300" y="92" width="160" height="22" rx="6" fill="#1a3320"/>
  <text x="380" y="107" text-anchor="middle" fill="#a5d6a7" font-family="sans-serif" font-size="11">msg 2</text>
  <rect x="300" y="119" width="160" height="22" rx="6" fill="#1a3320"/>
  <text x="380" y="134" text-anchor="middle" fill="#a5d6a7" font-family="sans-serif" font-size="11">msg 3</text>
  <line x1="380" y1="148" x2="380" y2="156" stroke="#a5d6a7" stroke-opacity=".4" stroke-width="1.5" stroke-dasharray="3 2"/>
  <rect x="300" y="163" width="160" height="34" rx="8" fill="#43a047"/>
  <text x="380" y="178" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Action Block</text>
  <text x="380" y="193" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="10">(single thread)</text>
  <rect x="300" y="205" width="160" height="22" rx="6" fill="#132b13"/>
  <text x="380" y="220" text-anchor="middle" fill="#a5d6a7" font-family="sans-serif" font-size="10">private state (no locks)</text>
  <rect x="540" y="30" width="200" height="210" rx="12" fill="none" stroke="#f57c00" stroke-opacity=".5" stroke-width="1.5"/>
  <text x="640" y="55" text-anchor="middle" fill="#ffcc80" font-family="sans-serif" font-size="13" font-weight="700">Hub C</text>
  <rect x="560" y="65" width="160" height="22" rx="6" fill="#2d1b00"/>
  <text x="640" y="80" text-anchor="middle" fill="#ffcc80" font-family="sans-serif" font-size="11">msg 1</text>
  <rect x="560" y="92" width="160" height="22" rx="6" fill="#2d1b00"/>
  <text x="640" y="107" text-anchor="middle" fill="#ffcc80" font-family="sans-serif" font-size="11">msg 2</text>
  <rect x="560" y="119" width="160" height="22" rx="6" fill="#2d1b00"/>
  <text x="640" y="134" text-anchor="middle" fill="#ffcc80" font-family="sans-serif" font-size="11">msg 3</text>
  <line x1="640" y1="148" x2="640" y2="156" stroke="#ffcc80" stroke-opacity=".4" stroke-width="1.5" stroke-dasharray="3 2"/>
  <rect x="560" y="163" width="160" height="34" rx="8" fill="#f57c00"/>
  <text x="640" y="178" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="11" font-weight="600">Action Block</text>
  <text x="640" y="193" text-anchor="middle" fill="#fff" font-family="sans-serif" font-size="10">(single thread)</text>
  <rect x="560" y="205" width="160" height="22" rx="6" fill="#2b1500"/>
  <text x="640" y="220" text-anchor="middle" fill="#ffcc80" font-family="sans-serif" font-size="10">private state (no locks)</text>
  <path d="M220,100 C250,100 250,100 280,100" stroke="#90caf9" stroke-width="1.5" fill="none" marker-end="url(#arr)"/>
  <text x="250" y="93" text-anchor="middle" fill="#90caf9" font-family="sans-serif" font-size="9">Observe</text>
  <path d="M480,130 C510,130 510,130 540,130" stroke="#a5d6a7" stroke-width="1.5" fill="none" marker-end="url(#arr2)"/>
  <text x="510" y="123" text-anchor="middle" fill="#a5d6a7" font-family="sans-serif" font-size="9">Observe</text>
  <path d="M540,100 C510,100 510,100 480,100" stroke="#ffcc80" stroke-width="1.5" fill="none" marker-end="url(#arr3)"/>
  <text x="510" y="93" text-anchor="middle" fill="#ffcc80" font-family="sans-serif" font-size="9">response</text>
  <text x="380" y="260" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-family="sans-serif" font-size="12">Each hub drains its queue on one thread — cross-hub calls are async messages, never blocking calls.</text>
</svg>

*Each `MessageHub` is an isolated actor: one thread, one queue, private state. Inter-hub calls are non-blocking message passes.*

---

## Single-Threaded Processing

Each hub drains its internal queue sequentially. No two handlers run concurrently inside one hub.

```mermaid
flowchart LR
    subgraph Hub["MessageHub Queue"]
        direction TB
        Q1[Message 1] --> P[Processor]
        Q2[Message 2] --> P
        Q3[Message 3] --> P
    end
    P --> H[Handler]
    H --> R[Response / Side-effect]
```

The benefits fall out naturally:

| Guarantee | What it means in practice |
|---|---|
| No intra-hub races | State mutations inside a hub need no locks |
| Predictable order | Messages arrive and execute in FIFO order |
| Simple state management | Immutable record updates are enough |
| Safe reactive composition | `IObservable<T>` chains run in the same serialised block |

---

## The Deadlock Trap

### Why `AwaitResponse` to Self Deadlocks

`AwaitResponse` (now **obsolete** — see below) posts a request and synchronously blocks the calling thread until the reply arrives:

```csharp
// ⚠️ Obsolete API — do not use in new code
var response = await hub.AwaitResponse(
    new CreateNodeRequest(node),
    o => o.WithTarget(hub.Address));
```

When the caller *is* the hub's own handler, every step of that sequence fights itself:

```mermaid
sequenceDiagram
    participant Handler as Current Handler
    participant Queue as Hub Queue
    participant Target as Target Handler

    Handler->>Queue: Post CreateNodeRequest
    Handler->>Handler: Block — waiting for response…
    Note over Queue: CreateNodeRequest sits in queue
    Note over Handler: Handler holds the one thread
    Note over Queue,Handler: DEADLOCK — neither can proceed
```

1. Handler A is running (it owns the single thread).
2. Handler A posts a `CreateNodeRequest` targeting the same hub.
3. Handler A blocks, waiting for the response.
4. The request sits in the queue — but the queue's thread is blocked by Handler A.
5. Neither side can proceed. The hub is permanently wedged.

This is not a timing edge case — it is a structural certainty whenever a handler awaits a response from its own hub.

---

## The Fix: Reactive Streams (the Modern API)

> **The `AwaitResponse` and `RegisterCallback` APIs are `[Obsolete]`.** All hub-reachable code must use `IObservable<T>` end-to-end — no `await`, no `Task<T>`, no `TaskCompletionSource`. Tests may use `.FirstAsync().ToTask()` at the boundary; production code never does.

The correct pattern for any write that produces a side effect is `stream.Update(...)`, which returns a cold `IObservable<T>`. Subscribe in the handler; the framework serialises the write through the owning hub's action block without blocking anything.

```csharp
// CORRECT — reactive, non-blocking
.WithClickAction(ctx =>
{
    workspace.GetMeshNodeStream(nodePath)
        .Update(node => node with { Content = updatedContent })
        .Subscribe(
            _ => ctx.NavigateTo(overviewUrl),
            ex => logger.LogWarning(ex, "Update failed for {Path}", nodePath));
    // Returns immediately; the subscribe callback fires when the update lands
    return Task.CompletedTask;
});
```

**How this resolves the deadlock problem:**

1. The click handler builds the observable chain and subscribes — then returns immediately.
2. The hub's action block is free to process the next message.
3. When the `Update` reaches the owning hub (which may be the same hub), it runs as an ordinary queued message — no thread blocked, no deadlock possible.

For waiting on work completion, observe the resulting node state rather than blocking:

```csharp
// Wait for a node to reach a target state — no blocking
workspace.GetMeshNodeStream(nodePath)
    .Where(node => node.Content is MyContent c && c.Status == MyStatus.Done)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(30))
    .Subscribe(
        node => HandleCompletion(node),
        ex => logger.LogWarning(ex, "Timed out waiting for {Path}", nodePath));
```

---

## Cross-Hub Calls

When calling a *different* hub, there is no single-thread conflict. Use `hub.Observe(request, o => o.WithTarget(otherAddress))`:

```csharp
// Safe — different hub address, no shared single thread
hub.Observe<CreateNodeResponse>(
    new CreateNodeRequest(node),
    o => o.WithTarget(otherHubAddress))
.Subscribe(
    response => HandleResponse(response),
    ex => logger.LogError(ex, "Cross-hub call failed"));
```

The response arrives as an observable emission; the subscriber runs inside the *receiving* hub's action block, not the caller's — so neither side blocks.

---

## Decision Guide

| Scenario | Correct pattern |
|---|---|
| Mutate a node on this hub | `workspace.GetMeshNodeStream(path).Update(n => n with {...}).Subscribe(...)` |
| Mutate a node on a different hub | Same API — `GetMeshNodeStream` auto-dispatches cross-hub |
| Wait for a state change | `GetMeshNodeStream(path).Where(predicate).Take(1).Timeout(...).Subscribe(...)` |
| Call a different hub and handle the reply | `hub.Observe<TResponse>(request, o => o.WithTarget(addr)).Subscribe(...)` |
| Test boundary only | `await stream.FirstAsync().ToTask()` is acceptable |

---

## Debugging a Wedged Hub

### Symptoms

- The application silently hangs on a specific action (button click, form submit).
- No exception is thrown; execution simply stops.
- Logs show a message was posted but no handler fired.

### Finding the Cause

Search for patterns that block the hub thread:

```csharp
// Red flags — search for these in hub-reachable code
await hub.AwaitResponse(...)           // Obsolete + deadlock risk
hub.AwaitResponse(...).Result          // Even worse — sync-over-async
Task.Result / Task.Wait()              // Blocks the action block
```

Check [DebuggingMessageFlow.md](/Doc/Architecture/DebuggingMessageFlow) for trace tags that reveal where a message stopped flowing.

### Prevention

- **No `async`/`await` in hub handlers** — return `IObservable<T>`, not `Task<T>`.
- **No `TaskCompletionSource` in hub code** — if you find one, replace it with an observable chain.
- **Subscribe immediately in the handler body** — cold observables silently do nothing if not subscribed (the framework logs a `MeshWeaver.Mesh.RequireSubscribe` warning at GC time).

> Full patterns and the mistake ledger live in [AsynchronousCalls.md](/Doc/Architecture/AsynchronousCalls).

---

## Summary

The Actor Model gives MeshWeaver hubs thread-safety and predictable ordering for free — as long as handlers never block the single thread. The rule is simple: **return `IObservable<T>`, subscribe, and let the framework serialise writes**. Every deadlock in this codebase traces back to a handler that broke that rule.
