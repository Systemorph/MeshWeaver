---
Name: Satellite Entity Patterns
Category: Architecture
Description: Data model, handler, and test patterns for satellite entities (Comments, Threads, Tracked Changes) — parent-child tracking, synchronous handler rules, access control, and Orleans verification.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M12 2v7M5 20l5.5-7M19 20l-5.5-7"/><circle cx="12" cy="2" r="2"/><circle cx="5" cy="20" r="2"/><circle cx="19" cy="20" r="2"/></svg>
---

Satellite entities — Comments, Threads, Tracked Changes — are secondary nodes that live under a primary content node. They share a consistent set of patterns for data modeling, handler implementation, access control, and reactive testing. This page is the canonical reference for all three.

## Data Model: Parent Tracks Children

A parent node holds an `ImmutableList<string>` of child IDs. The layout area reads this list from the workspace stream and renders a `LayoutAreaControl` for each entry — it never issues a query to discover children.

| Entity | Parent field | Child node type |
|--------|-------------|-----------------|
| Thread | `Messages: ImmutableList<string>` | `ThreadMessage` |
| Comment | `Replies: ImmutableList<string>` | Reply `Comment` |

When a child is created, the handler appends its ID to the parent list via `workspace.UpdateMeshNode()`. The update flows immediately into every subscribed Blazor client through the sync stream — no polling, no page refresh.

### Top-level vs. reply detection

A comment is top-level when its namespace ends with `_Comment` (e.g. `Doc/MyDoc/_Comment`). A reply's namespace is the parent comment's path (e.g. `Doc/MyDoc/_Comment/c1`). No parent load is required — the shape of the path is the signal.

---

## Handler Pattern: Synchronous, Reactive, Error-Safe

Satellite entity handlers **must be fully synchronous**. Running `await` inside the hub execution pipeline causes deadlocks in Orleans distributed mode. The right shape is: start an `IObservable` chain, subscribe, and return immediately — the response is posted from inside the callback.

### Reference implementation

```csharp
// SubmitMessageRequest was deleted 2026-05-25. The submission watcher invokes
// ExecuteMessageAsync directly after writing PendingUserMessages via stream.Update
// on the thread node — no wire message, no handler dispatch.
internal static void ExecuteMessageAsync(
    IMessageHub hub,
    RoundParams request,
    AccessContext? userAccessContext)
{
    var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
    var workspace = hub.GetWorkspace();  // Capture BEFORE Subscribe

    // 1) Start both node creates concurrently (IObservable — cold until Subscribe)
    var inputObs  = meshService.CreateNode(new MeshNode(...));
    var outputObs = meshService.CreateNode(new MeshNode(...));

    // 2) Zip waits for both, then fires the callback exactly once
    inputObs.Zip(outputObs).Subscribe(
        pair =>
        {
            // 3) Update parent — append child IDs to tracking list
            workspace.UpdateMeshNode(node =>
            {
                var thread = node.Content as Thread ?? new Thread();
                return node with
                {
                    Content = thread with
                    {
                        Messages = thread.Messages.AddRange([userMsgId, responseMsgId])
                    }
                };
            });

            // 4) Post response INSIDE the callback — nodes exist before caller is notified
            hub.Post(new Response { Success = true }, o => o.ResponseFor(delivery));
        },
        error =>
        {
            hub.Post(new Response { Success = false, Error = error.Message },
                o => o.ResponseFor(delivery));
        });

    // 5) Return immediately — response is posted from the callback above
    return delivery.Processed();
}
```

### Handler rules

1. **Synchronous signature** — return `IMessageDelivery`, never `async Task<IMessageDelivery>`.
2. **No `await` anywhere in the message pipeline** — `await` deadlocks in Orleans. This applies to handlers, Blazor components, layout areas, and any code on the hub execution path. Use `Post` + `RegisterCallback` instead of `AwaitResponse`.
3. **No permission checks in handlers or layout areas** — access control is enforced by the delivery pipeline via partition access policies. If a user lacks the `Comment` or `Thread` permission, the request is rejected before reaching the handler. Handlers assume the caller is authorized.
4. **Never use `IMeshStorage` or persistence directly** — use `IMeshService` for CRUD and `workspace.UpdateMeshNode()` for in-memory workspace updates.
5. **Capture `workspace` before `Subscribe`** — `var workspace = hub.GetWorkspace()` must be called on the handler thread, not inside the callback closure.
6. **Post the response inside the callback** — the caller needs to know the operation completed; posting before the observable fires gives a false success.
7. **Wrap the `onNext` body in `try/catch`** — if `workspace.UpdateMeshNode()` or any callback code throws and you do not catch it, the caller hangs indefinitely waiting for a response that never comes.
8. **Use `meshService.CreateNode()`** — returns `IObservable<MeshNode>`; internally uses `Post` + `RegisterCallback`.
9. **Use `workspace.UpdateMeshNode()`** — updates the in-memory stream and triggers persistence via the debounced `MeshNodeTypeSource`.

### Blazor component pattern

Blazor components follow the same rule: `Post` + `RegisterCallback`, never `AwaitResponse`.

```csharp
// CORRECT: fire-and-forget with callback
var delivery = Hub.Post(new CreateCommentRequest { ... },
    o => o.WithTarget(new Address(hubAddress)));
Hub.RegisterCallback<CreateCommentResponse>(delivery!, response =>
{
    if (!response.Message.Success)
        logger?.LogWarning("Failed: {Error}", response.Message.Error);
    return response;
});

// WRONG: AwaitResponse blocks the Blazor circuit if response never arrives
await Hub.AwaitResponse(request, o => o.WithTarget(address), default);  // DEADLOCK
```

### Anti-patterns to avoid

```csharp
// WRONG: await in handler — deadlocks in Orleans
private static async Task<IMessageDelivery> Handle(IMessageHub hub, ...)
{
    await persistence.GetNodeAsync(path, ct);  // DEADLOCK
}

// WRONG: using persistence directly
var persistence = hub.ServiceProvider.GetService<IMeshStorage>();
await persistence.SaveNodeAsync(node, ct);  // WRONG

// WRONG: posting response before async operation completes
hub.Post(response, o => o.ResponseFor(request));  // too early
meshService.CreateNode(node).Subscribe(...);       // not done yet

// WRONG: no error handling — caller hangs forever if callback throws
meshService.CreateNode(node).Subscribe(_ =>
{
    workspace.UpdateMeshNode(...);  // throws? response never posted!
    hub.Post(response, o => o.ResponseFor(request));
});

// CORRECT: always post a response, success or failure
meshService.CreateNode(node).Subscribe(_ =>
{
    try
    {
        workspace.UpdateMeshNode(...);
        hub.Post(successResponse, o => o.ResponseFor(request));
    }
    catch (Exception ex)
    {
        hub.Post(failureResponse, o => o.ResponseFor(request));
    }
});
```

---

## MainNode: Access Control for Satellite Nodes

> **Every satellite node MUST set `MainNode` to the content entity it belongs to.** Without this, access control fails because the hub uses the node's own path as its identity — a path that has no permissions attached.

For example, a thread under `PartnerRe/AiConsulting` must carry `MainNode = "PartnerRe/AiConsulting"`, not the thread's own path. The same requirement applies to sub-threads, thread messages, comments, and tracked changes.

```csharp
// CORRECT: MainNode points to the owning content entity
var threadNode = new MeshNode(threadId, ns)
{
    NodeType = "Thread",
    MainNode  = contextPath,   // e.g., "PartnerRe/AiConsulting"
    Content   = new Thread()
};

var msgNode = new MeshNode(msgId, threadPath)
{
    NodeType = "ThreadMessage",
    MainNode  = contextPath,   // same content entity — NOT the thread path
    Content   = new ThreadMessage { ... }
};

// WRONG: MainNode defaults to the node's own path — no permissions, access denied
var node = new MeshNode(id, threadPath) { NodeType = "ThreadMessage" };
// MainNode = "PartnerRe/.../threadId/msgId" — not a real entity; has no permissions
```

This applies to all satellite types: `Thread`, `ThreadMessage`, `Comment`, `TrackedChange`, `Approval`.

---

## SwitchAccessContext: Scoped Identity in Callbacks

Code that runs outside the hub delivery pipeline — such as inside `Subscribe` callbacks — does not automatically inherit the caller's `AccessContext`. Use `SwitchAccessContext` to establish a scoped identity for the duration of the callback body.

```csharp
var accessService = hub.ServiceProvider.GetService<AccessService>();
childStream.Subscribe(change =>
{
    using var _ = accessService?.SwitchAccessContext(userAccessContext);
    // Operations here run under the correct user identity
    workspace.UpdateMeshNode(node => { ... });
});
```

---

## Remote Stream Subscription Pattern (Delegation)

When a thread delegates to a sub-thread, subscribe to the child's `MeshNode` stream and keep the subscription alive for the lifetime of the operation. Never await completion inline — the AI framework owns the `await`; MeshWeaver code only supplies a `Task` via `TaskCompletionSource`.

```csharp
var tcs = new TaskCompletionSource<DelegationResult>();

// 1. Create sub-thread node (Observable — no await)
meshService.CreateNode(subThreadNode).Subscribe(_ =>
{
    // 2. Subscribe to child stream — AddDisposable keeps it alive
    var childStream = workspace.GetRemoteStream<MeshNode>(
        new Address(subThreadPath), new MeshNodeReference());
    workspace.AddDisposable(childStream);

    childStream.Subscribe(change =>
    {
        using var _ = accessService?.SwitchAccessContext(userContext);
        var childThread = change.Value?.Content as Thread;
        if (childThread == null) return;

        // Mirror child progress into the parent node
        workspace.UpdateMeshNode(node => { ... merge child progress ... });

        // On completion, resolve the TCS
        if (!childThread.IsExecuting)
            tcs.TrySetResult(new DelegationResult { ... });
    });

    // 3. Submit via stream.Update on the sub-thread (SubmitMessageRequest
    //    deleted 2026-05-25). The sub-thread's submission watcher reacts to
    //    PendingUserMessages and invokes ExecuteMessageAsync directly.
    ThreadInput.AppendUserInput(workspace, childAddress.Path, userMessage);
},
error => tcs.TrySetResult(new DelegationResult { Success = false }));

return tcs.Task;  // The AI framework awaits this — our code never does
```

---

## Test Pattern: Reactive Verification

Tests for satellite entities verify state through **reactive streams**, not `QueryAsync`. This exercises the same code path as the GUI and eliminates timing-dependent polling.

### Orleans test setup

Both the silo and the client must register domain types. Without `AddGraph()` on the client, type names diverge and deserialization silently fails.

```csharp
// Silo: registers handlers and persistence
public class MySiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .AddFileSystemPersistence(dataPath)
            .ConfigurePortalMesh()
            .AddGraph()   // Registers ALL domain types
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}

// Client: MUST also register domain types for serialization alignment
public class MyClientConfigurator : IHostConfigurator
{
    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshClient()
            .AddGraph();  // Required for type registry alignment
    }
}
```

### Verification via `GetRemoteStream`

Subscribe to the node's workspace stream **before** triggering the operation, then reactively wait for the expected state to appear.

```csharp
// 0) Subscribe BEFORE sending the request — never after
var markersAppeared = workspace.GetRemoteStream<MeshNode>(docAddress)
    .Select(nodes => nodes?.FirstOrDefault(n => n.Path == docPath))
    .Select(node => (node?.Content as MarkdownContent)?.Content ?? "")
    .Where(content => content.Contains($"<!--comment:{markerId}"))
    .FirstAsync()
    .ToTask(ct);

// 1) Send request
var response = await client.AwaitResponse(request, o => o.WithTarget(address), ct);

// 2) Wait for the stream to reflect the change
var updatedContent = await markersAppeared;
```

### Verification via `GetDataRequest`

For verifying the content of an individual node (mirrors the pattern in `OrleansChatTest`):

```csharp
private async Task<T?> GetHubContentAsync<T>(IMessageHub client, string path, CancellationToken ct)
    where T : class
{
    var nodeId = path[(path.LastIndexOf('/') + 1)..];
    var response = await client.AwaitResponse(
        new GetDataRequest(new EntityReference(nameof(MeshNode), nodeId)),
        o => o.WithTarget(new Address(path)), ct);

    var node = response.Message.Data as MeshNode;
    if (node == null && response.Message.Data is JsonElement je)
        node = je.Deserialize<MeshNode>(hub.JsonSerializerOptions);

    return node?.Content is T typed ? typed
        : node?.Content is JsonElement contentJe
            ? contentJe.Deserialize<T>(hub.JsonSerializerOptions)
            : null;
}
```

### Complete test flow (Comment example)

```
1.  Deploy Orleans cluster with domain types registered on silo AND client
2.  Create client hub, register for streaming
3.  Ping target grain to activate it
4.  Subscribe to markdown stream via GetRemoteStream (before sending request)
5.  Send CreateCommentRequest
6.  Assert CreateCommentResponse.Success == true
7.  Await markdown stream to contain comment markers
8.  GetDataRequest on comment path to verify Comment content
9.  (Optional) Subscribe to comment layout area to verify rendering
10. (Optional) Send reply CreateCommentRequest, verify via stream
```

---

## Type Registration

All satellite entity types must be registered in the mesh builder so Orleans serialization works end-to-end.

```csharp
// In AddCommentType() — called by AddGraph()
builder.ConfigureHub(config => config
    .WithType<Comment>(nameof(Comment))
    .WithType<CreateCommentRequest>(nameof(CreateCommentRequest))
    .WithType<CreateCommentResponse>(nameof(CreateCommentResponse))
    // ... all request/response types
);
```

Both the **silo** and **client** must call `AddGraph()` (or the equivalent domain registration). Without this, the client serializes types with fully qualified names that the silo cannot match.

---

## Cross-Grain Live Updates (Critical for Orleans)

Pushing data from one grain to another — for example, a thread execution grain updating the response message node — requires care. Two approaches that appear to work but **do not trigger live updates** to Blazor clients:

- `workspace.GetRemoteStream().Update()` — creates a local proxy; change does not propagate to other subscribers.
- `DataChangeRequest` posted to another grain — updates the entity store but does not fire the sync stream.

Both persist data correctly (the change is visible after a page refresh) but the UI does not update in real time.

### Correct pattern: custom message + local workspace update

```csharp
// 1. Define a message type
public record UpdateMyContent { public string Text { get; init; } }

// 2. Register a handler ON the target hub (runs on the target grain)
config.WithHandler<UpdateMyContent>((hub, delivery) =>
{
    hub.GetWorkspace().UpdateMeshNode(node =>
        node with { Content = /* updated content */ });
    return delivery.Processed();
});

// 3. Post FROM the calling grain
hub.Post(new UpdateMyContent { Text = "hello" },
    o => o.WithTarget(new Address(targetPath)));
```

**Why this works:** `workspace.UpdateMeshNode()` updates the **local** data source stream on the target grain, which fires a `DataChangedEvent` on the sync stream. Blazor clients that subscribed via `GetRemoteStream` receive this event over SignalR and re-render without any polling. The update path is: grain workspace → sync stream → Orleans routing → Blazor SignalR → UI render.
