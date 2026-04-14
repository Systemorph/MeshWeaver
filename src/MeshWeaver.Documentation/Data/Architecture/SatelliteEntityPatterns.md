---
Name: Satellite Entity Patterns
Category: Architecture
Description: Implementation and test patterns for satellite entities (comments, threads, tracked changes) — data model, handlers, workspace updates, and Orleans testing
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M12 2v7M5 20l5.5-7M19 20l-5.5-7"/><circle cx="12" cy="2" r="2"/><circle cx="5" cy="20" r="2"/><circle cx="19" cy="20" r="2"/></svg>
---

Satellite entities (Comments, Threads, Tracked Changes) follow a specific pattern for both handler implementation and testing. This document covers the non-blocking handler pattern, the parent-child tracking pattern, and the reactive test verification approach.

## Data Model Pattern: Parent Tracks Children

Satellite entities use an `ImmutableList<string>` on the parent to track child IDs. This is how the layout area knows which children to render — it reads the list from the workspace stream, not from queries.

| Entity | Parent Field | Children |
|--------|-------------|----------|
| Thread | `Messages: ImmutableList<string>` | ThreadMessage nodes |
| Comment | `Replies: ImmutableList<string>` | Reply Comment nodes |

When a child is created, the handler updates the parent's list via `workspace.UpdateMeshNode()`. The layout area reads the list from the node stream and renders `LayoutArea` controls for each child path.

### Top-level vs Reply detection

A comment is top-level when its namespace ends with `_Comment` (e.g., `Doc/MyDoc/_Comment`). A reply's namespace is the parent comment path (e.g., `Doc/MyDoc/_Comment/c1`). No need to load the parent — just inspect the path.

## Handler Pattern

Satellite entity handlers must be **fully synchronous** (no `await`). They run inside the hub execution pipeline where blocking causes deadlocks in Orleans distributed mode.

### Reference Implementation: `ThreadExecution.HandleSubmitMessage`

```csharp
internal static IMessageDelivery HandleSubmitMessage(
    IMessageHub hub,
    IMessageDelivery<SubmitMessageRequest> delivery)
{
    var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
    var workspace = hub.GetWorkspace();  // Capture BEFORE Subscribe

    // 1) Create child nodes via Observable (fire-and-forget)
    var inputObs = meshService.CreateNode(new MeshNode(...));
    var outputObs = meshService.CreateNode(new MeshNode(...));

    // 2) Zip concurrent creates, handle result in Subscribe callback
    inputObs.Zip(outputObs).Subscribe(
        pair =>
        {
            // 3) Update parent node — add child IDs to tracking list
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

            // 4) Post response INSIDE the callback (after nodes exist)
            hub.Post(new Response { Success = true }, o => o.ResponseFor(delivery));
        },
        error =>
        {
            hub.Post(new Response { Success = false, Error = error.Message },
                o => o.ResponseFor(delivery));
        });

    // 5) Return immediately — response posted from callback
    return delivery.Processed();
}
```

### Rules

1. **Synchronous signature**: `IMessageDelivery` return type, never `async Task<IMessageDelivery>`
2. **Never await — anywhere in the message pipeline**: `await` == deadlock in Orleans. This applies to handlers, Blazor components sending requests, layout areas, and any code on the hub execution path. Use `Post` + `RegisterCallback` instead of `AwaitResponse`.
3. **No permission checks in handlers or layout areas**: Access control is handled by the delivery pipeline via partition access policies. If a user lacks the `Comment` permission (for comments) or `Thread` permission (for threads), the request is rejected before reaching the handler. Handlers and layout areas assume the caller is authorized. This is symmetrical: threads use `Thread` permission, comments use `Comment` permission.
4. **Never use IMeshStorage/persistence directly**: Use `IMeshService` for CRUD, `workspace.UpdateMeshNode()` for in-memory updates
5. **Capture workspace before Subscribe**: `var workspace = hub.GetWorkspace()` must be called before entering the Subscribe callback
6. **Post response in callback**: The response must be posted inside `Subscribe(onNext)`, not before — the caller needs to know the operation completed
7. **Wrap onNext in try/catch**: If `workspace.UpdateMeshNode()` or any code in the Subscribe callback throws, catch the exception and post a negative response. Otherwise the caller hangs forever waiting.
8. **Use `meshService.CreateNode()`**: Returns `IObservable<MeshNode>` — internally uses `Post` + `RegisterCallback`
9. **Use `workspace.UpdateMeshNode()`**: Updates the in-memory workspace stream, which triggers persistence via the debounced `MeshNodeTypeSource`

### Blazor Component Pattern

Blazor components must also use `Post` + `RegisterCallback`, not `AwaitResponse`:

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

// WRONG: AwaitResponse blocks the Blazor circuit if response never comes
await Hub.AwaitResponse(request, o => o.WithTarget(address), default);  // DEADLOCK
```

### Anti-Patterns

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

// WRONG: no error handling in Subscribe callback — caller hangs forever
meshService.CreateNode(node).Subscribe(_ =>
{
    workspace.UpdateMeshNode(...);  // throws? response never posted!
    hub.Post(response, o => o.ResponseFor(request));
}, ...);

// CORRECT: wrap in try/catch, always post response
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
}, ...);
```

## MainNode: Access Control for Satellite Nodes

**Every satellite node MUST set `MainNode` to the content entity it belongs to.** Without this, access control fails because the hub uses the node's own path as identity.

Example: A thread under `PartnerRe/AiConsulting` must have `MainNode = "PartnerRe/AiConsulting"`, not the thread's own path. Same for sub-threads, thread messages, and comments.

```csharp
// CORRECT: MainNode = content entity
var threadNode = new MeshNode(threadId, ns)
{
    NodeType = "Thread",
    MainNode = contextPath,  // e.g., "PartnerRe/AiConsulting"
    Content = new Thread()
};

var msgNode = new MeshNode(msgId, threadPath)
{
    NodeType = "ThreadMessage",
    MainNode = contextPath,  // same content entity, not the thread path
    Content = new ThreadMessage { ... }
};

// WRONG: MainNode defaults to self (node.Path) — access denied for sub-threads
var node = new MeshNode(id, threadPath) { NodeType = "ThreadMessage" };
// MainNode = "PartnerRe/.../threadId/msgId" — not a real entity, no permissions
```

This applies to all satellite types: Thread, ThreadMessage, Comment, TrackedChange, Approval.

## SwitchAccessContext: Scoped Identity in Callbacks

When code runs outside the hub delivery pipeline (e.g., in `Subscribe` callbacks), the `AccessContext` is not set. Use `SwitchAccessContext` for scoped identity:

```csharp
var accessService = hub.ServiceProvider.GetService<AccessService>();
childStream.Subscribe(change =>
{
    using var _ = accessService?.SwitchAccessContext(userAccessContext);
    // ... operations here run under the correct user identity
    workspace.UpdateMeshNode(node => { ... });
});
```

## Remote Stream Subscription Pattern (Delegation)

When a thread delegates to a sub-thread, subscribe to the child's MeshNode and keep the subscription alive. **Never await completion — use `TaskCompletionSource` instead.**

```csharp
var tcs = new TaskCompletionSource<DelegationResult>();

// 1. Create node (Observable, no await)
meshService.CreateNode(subThreadNode).Subscribe(_ =>
{
    // 2. Subscribe to child — RegisterForDisposal keeps it alive
    var childStream = workspace.GetRemoteStream<MeshNode>(
        new Address(subThreadPath), new MeshNodeReference());
    workspace.AddDisposable(childStream);

    childStream.Subscribe(change =>
    {
        using var _ = accessService?.SwitchAccessContext(userContext);
        var childThread = change.Value?.Content as Thread;
        if (childThread == null) return;

        // Update parent's progress
        workspace.UpdateMeshNode(node => { ... merge child progress ... });

        // On completion, resolve TCS
        if (!childThread.IsExecuting)
            tcs.TrySetResult(new DelegationResult { ... });
    });

    // 3. Submit message (Post + RegisterCallback, NOT AwaitResponse)
    var delivery = Hub.Post(new SubmitMessageRequest { ... },
        o => o.WithTarget(childAddress).WithAccessContext(userContext));
    Hub.RegisterCallback(delivery, response => { ... return response; });
},
error => tcs.TrySetResult(new DelegationResult { Success = false }));

return tcs.Task; // AI framework awaits this — our code never does
```

## Test Pattern

Tests for satellite entities must verify through **reactive streams**, not `QueryAsync`. This ensures the test exercises the same path the GUI uses.

### Orleans Test Setup

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
            .AddGraph()  // Registers ALL domain types
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}

// Client: MUST also register domain types for serialization
public class MyClientConfigurator : IHostConfigurator
{
    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshClient()
            .AddGraph();  // Required for type registry alignment
    }
}
```

### Verification via GetRemoteStream

Subscribe to the node's workspace stream **before** triggering the operation, then reactively wait for the expected state:

```csharp
// 0) Subscribe to stream BEFORE sending the request
var markersAppeared = workspace.GetRemoteStream<MeshNode>(docAddress)
    .Select(nodes => nodes?.FirstOrDefault(n => n.Path == docPath))
    .Select(node => (node?.Content as MarkdownContent)?.Content ?? "")
    .Where(content => content.Contains($"<!--comment:{markerId}"))
    .FirstAsync()
    .ToTask(ct);

// 1) Send request
var response = await client.AwaitResponse(request, o => o.WithTarget(address), ct);

// 2) Wait for stream to reflect the change
var updatedContent = await markersAppeared;
```

### Verification via GetDataRequest

For verifying individual node content (same pattern as `OrleansChatTest`):

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

### Complete Test Flow (Comment Example)

```
1. Deploy Orleans cluster with domain types registered on both silo and client
2. Create client hub, register for streaming
3. Ping target grain to activate it
4. Subscribe to markdown stream (GetRemoteStream)
5. Send CreateCommentRequest
6. Assert CreateCommentResponse.Success == true
7. Await markdown stream to contain comment markers
8. GetDataRequest on comment path to verify Comment content
9. (Optional) Subscribe to comment layout area to verify rendering
10. (Optional) Send reply CreateCommentRequest, verify via stream
```

## Type Registration

All satellite entity types must be registered in the mesh builder so Orleans serialization works:

```csharp
// In AddCommentType() — called by AddGraph()
builder.ConfigureHub(config => config
    .WithType<Comment>(nameof(Comment))
    .WithType<CreateCommentRequest>(nameof(CreateCommentRequest))
    .WithType<CreateCommentResponse>(nameof(CreateCommentResponse))
    // ... all request/response types
);
```

Both the **silo** and **client** must call `AddGraph()` (or equivalent) to share the same type registry. Without this, the client serializes types with full namespace names that the silo can't match.

## Cross-Grain Live Updates (Critical for Orleans)

When pushing data from one grain to another (e.g., thread execution updating the response message), **never** use:
- `workspace.GetRemoteStream().Update()` — creates a local proxy, doesn't propagate to other clients
- `DataChangeRequest` posted to another grain — updates entity store but doesn't trigger the sync stream

These methods persist data correctly (visible on page refresh) but **do not trigger live updates** to Blazor clients.

### Correct Pattern: Custom Message + Local Workspace Update

```csharp
// 1. Define a message type
public record UpdateMyContent { public string Text { get; init; } }

// 2. Register handler ON the target hub (runs on the target grain)
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

**Why this works:** `workspace.UpdateMeshNode()` updates the **local** data source stream on the target grain. This triggers `DataChangedEvent` on the sync stream that clients subscribe to via `GetRemoteStream`. The update flows: grain workspace → sync stream → Orleans routing → Blazor SignalR → UI render.
