# Asynchronous Calls in MeshWeaver

MeshWeaver uses a **truly asynchronous** message-passing model. This is fundamentally different from C#'s `async/await` pattern, which is better described as "fake async" — you still block the calling context waiting for a result.

## The T-Shirt Analogy

When you order a t-shirt online, you don't stand next to the mailbox until it arrives. Your life continues. The t-shirt shows up later, and you deal with it then.

**Truly async (MeshWeaver pattern):**
```csharp
// Post the request — fire and forget
hub.Post(new MyRequest(), o => o.WithTarget(address));

// Register a callback — triggered when the answer returns
hub.RegisterCallback(delivery, response =>
{
    // Handle response here — your "mailbox notification"
    return response;
});

// Your code continues immediately — no blocking
return delivery.Processed();
```

**Fake async (C# async/await):**
```csharp
// You ARE standing at the mailbox
var response = await hub.AwaitResponse<MyResponse>(request);
// Nothing else happens until the response arrives
```

## Why `await` Deadlocks in Hub Handlers

The message hub processes messages sequentially through a single-threaded `ActionBlock`. When a handler calls `await`, it blocks the action block waiting for a response. But that response is itself a message that needs to be processed by the same action block — which is blocked. **Deadlock.**

```
Handler runs on ActionBlock
    → await AwaitResponse(request)
        → ActionBlock is blocked waiting
            → Response message arrives
                → Cannot be processed — ActionBlock is busy
                    → DEADLOCK
```

This applies to:
- `await hub.AwaitResponse(...)` — blocks the hub
- `await someTask` — blocks the hub scheduler
- `hub.InvokeAsync(...)` — schedules work on the blocked scheduler
- `workspace.GetStream().Subscribe(...)` — if the stream observes on the hub scheduler, the emission is queued behind the blocked handler

## The Observable Pattern

Use `IMeshService` to get into reactive/observable contexts. Observables are inherently truly async — you subscribe and get notified when data is available.

### Creating Nodes (Non-Blocking)

Fire-and-forget node creation. State updates go in the **handler body** (runs on the grain scheduler), not in the Subscribe callback:

```csharp
// Fire-and-forget — no callback needed for state updates
meshService.CreateNode(new MeshNode(id, namespace)
{
    NodeType = "MyType",
    Content = new MyContent { ... }
}).Subscribe(
    _ => logger.LogInformation("Node created"),
    error => logger.LogError(error, "Node creation failed"));

// State update in the handler body (grain scheduler) — safe
hub.GetWorkspace().UpdateMeshNode(node => node with
{
    Content = content with { Messages = content.Messages.Add(id) }
});

// Handler returns immediately
return delivery.Processed();
```

### CRITICAL: Never Do State Updates in Subscribe Callbacks

Subscribe callbacks run on **arbitrary threads**. State updates (`workspace.UpdateMeshNode`) require the hub's scheduler. Mixing these causes deadlocks — this is not framework-specific, it's a fundamental consequence of truly async programming: you don't control which thread a callback runs on.

```csharp
// WRONG — callback runs on unknown thread, state update needs hub scheduler:
meshService.CreateNode(node).Subscribe(_ =>
{
    workspace.UpdateMeshNode(n => ...); // ← deadlock: wrong thread
});

// CORRECT — separate concerns: fire-and-forget for I/O, state update in handler body:
meshService.CreateNode(node).Subscribe();  // fire-and-forget
hub.GetWorkspace().UpdateMeshNode(n => ...);  // handler body = hub scheduler
```

The principle: **I/O is fire-and-forget, state changes happen where you control the thread.** This is true for any actor-based or message-passing system.

## Post + RegisterCallback Pattern

For request-response flows where you need the result but can't block:

```csharp
// 1. Post the request (returns immediately)
var delivery = hub.Post(new CreateNodeRequest(node), o => o.WithTarget(address));

// 2. Register a callback for the response (non-blocking)
hub.RegisterCallback((IMessageDelivery)delivery, response =>
{
    if (response is IMessageDelivery<CreateNodeResponse> cnr)
    {
        // Handle success
        tcs.TrySetResult(cnr.Message);
    }
    return response;
});

// 3. Return immediately — callback fires later
return delivery.Processed();
```

## Workspace Updates (Non-Blocking)

`workspace.UpdateMeshNode` applies an update function to the current node state. It posts the update to the data stream — no blocking, no subscription:

```csharp
// Read current state and update atomically — no stream subscription needed
workspace.UpdateMeshNode(node =>
{
    var content = node.Content as MyContent ?? new MyContent();
    return node with
    {
        Content = content with { Status = "updated" }
    };
});
```

## Rules Summary

| Pattern | Safe in Handlers? | Notes |
|---------|-------------------|-------|
| `hub.Post(...)` | Yes | Fire-and-forget, safe from any thread |
| `hub.RegisterCallback(...)` | Yes | Non-blocking callback registration |
| `meshService.CreateNode(...).Subscribe()` | Yes | Fire-and-forget, no callback logic |
| `workspace.UpdateMeshNode(...)` in handler body | Yes | Runs on grain scheduler |
| `workspace.UpdateMeshNode(...)` in Subscribe callback | **NO** | Wrong thread in Orleans, deadlocks |
| `meshService.QueryAsync(...)` | **NO** | Blocks waiting for response |
| `await hub.AwaitResponse(...)` | **NO** | Deadlocks the hub scheduler |
| `await someTask` | **NO** | Blocks the hub scheduler |
| `hub.InvokeAsync(...)` | **NO** | Schedules on potentially blocked scheduler |
| `stream.Subscribe(...)` | **Risky** | May deadlock if stream observes on hub scheduler |

## When async/await IS Safe

`async/await` is safe in contexts that don't run on the hub's scheduler:
- Blazor component event handlers (`OnClick`, `OnInitializedAsync`)
- HTTP middleware and API controllers
- Background services and hosted services
- Test code

The rule is simple: **if your code runs inside a hub message handler (registered via `.WithHandler<T>()`), never await.**

## Blocking Execution (AI Streaming)

Sometimes you genuinely need long-running I/O — for example, streaming an AI response. This uses a **hosted hub** (`_Exec`) that runs the blocking work on its own thread via `hub.InvokeAsync`. But even here:

- All **state updates** (workspace, thread content) go through the **parent hub** (`parentHub.GetWorkspace().UpdateMeshNode(...)`, `parentHub.Post(...)`)
- All **messages** go through the parent hub — never post to the execution hub
- The execution hub is purely for hosting the blocking I/O — it should never own state

```csharp
// In HandleSubmitMessage (runs on thread hub):
var executionHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), ...);
executionHub.Post(request);  // Only message to execution hub: start the work

// In ExecuteMessageAsync (runs on _Exec hub):
var parentHub = hub.Configuration.ParentHub!;
parentHub.Post(new UpdateThreadMessageContent { ... });  // State via parent hub
parentHub.GetWorkspace().UpdateMeshNode(...);              // Workspace via parent hub
```

The parent hub's scheduler is free (the handler returned `delivery.Processed()` immediately). State updates and callbacks process normally on it.
