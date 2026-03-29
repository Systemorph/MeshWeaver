---
Name: Satellite Entity Handler & Test Patterns
Category: Architecture
Description: How to implement handlers and write tests for satellite entities (comments, threads, tracked changes)
---

Satellite entities (Comments, Threads, Tracked Changes) follow a specific pattern for both handler implementation and testing. This document covers the non-blocking handler pattern and the reactive test verification approach.

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
            // 3) Update parent node via workspace stream (synchronous, in-memory)
            workspace.UpdateMeshNode(node => node with { Content = ... });

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
2. **Never await**: No `await` inside handlers. Use `Observable.Subscribe()` for async operations
3. **Never use IMeshStorage/persistence directly**: Use `IMeshService` for CRUD, `workspace.UpdateMeshNode()` for in-memory updates
4. **Capture workspace before Subscribe**: `var workspace = hub.GetWorkspace()` must be called before entering the Subscribe callback
5. **Post response in callback**: The response must be posted inside `Subscribe(onNext)`, not before — the caller needs to know the operation completed
6. **Use `meshService.CreateNode()`**: Returns `IObservable<MeshNode>` — internally uses `Post` + `RegisterCallback`
7. **Use `workspace.UpdateMeshNode()`**: Updates the in-memory workspace stream, which triggers persistence via the debounced `MeshNodeTypeSource`

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
