---
Name: Satellite Entity Patterns
Category: Architecture
Description: Data model, handler, and test patterns for satellite entities (Comments, Threads, Tracked Changes) — parent-child tracking, synchronous handler rules, access control, and Orleans verification.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M12 2v7M5 20l5.5-7M19 20l-5.5-7"/><circle cx="12" cy="2" r="2"/><circle cx="5" cy="20" r="2"/><circle cx="19" cy="20" r="2"/></svg>
---

Satellite entities — Comments, Threads, Tracked Changes — are secondary nodes that live under a primary content node. They share a consistent set of patterns for data modeling, handler implementation, access control, and reactive testing. This page is the canonical reference for all three.

> **Two satellite pages, two scopes:** this page covers the **data model, handler, access-control, and test patterns**. Its companion [Satellite Node Patterns](/Doc/Architecture/SatelliteNodePatterns) covers the **operational invariants** — hub ownership, persistence routing, and the table-routing rules. Read this one when building a satellite feature; read the other when debugging where a satellite lives and who owns it.
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 320" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <polygon points="0 0, 8 3.5, 0 7" fill="#90a4ae"/>
    </marker>
    <marker id="arr-blue" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <polygon points="0 0, 8 3.5, 0 7" fill="#1e88e5"/>
    </marker>
  </defs>
  <rect x="270" y="20" width="220" height="64" rx="10" fill="#1e88e5"/>
  <text x="380" y="46" font-family="sans-serif" font-size="14" font-weight="bold" fill="#fff" text-anchor="middle">Content Node</text>
  <text x="380" y="66" font-family="sans-serif" font-size="11" fill="#bbdefb" text-anchor="middle">Doc/MyDoc  ·  PartnerRe/AiConsulting</text>
  <rect x="20" y="168" width="180" height="72" rx="10" fill="#5c6bc0"/>
  <text x="110" y="192" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">Thread</text>
  <text x="110" y="210" font-family="sans-serif" font-size="10" fill="#c5cae9" text-anchor="middle">ns: Doc/MyDoc/_Thread</text>
  <text x="110" y="226" font-family="sans-serif" font-size="10" fill="#c5cae9" text-anchor="middle">Messages: ImmutableList&lt;string&gt;</text>
  <rect x="290" y="168" width="180" height="72" rx="10" fill="#43a047"/>
  <text x="380" y="192" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">Comment</text>
  <text x="380" y="210" font-family="sans-serif" font-size="10" fill="#c8e6c9" text-anchor="middle">ns: Doc/MyDoc/_Comment</text>
  <text x="380" y="226" font-family="sans-serif" font-size="10" fill="#c8e6c9" text-anchor="middle">Replies: ImmutableList&lt;string&gt;</text>
  <rect x="560" y="168" width="180" height="72" rx="10" fill="#f57c00"/>
  <text x="650" y="192" font-family="sans-serif" font-size="13" font-weight="bold" fill="#fff" text-anchor="middle">TrackedChange</text>
  <text x="650" y="210" font-family="sans-serif" font-size="10" fill="#ffe0b2" text-anchor="middle">ns: Doc/MyDoc/_Tracking</text>
  <text x="650" y="226" font-family="sans-serif" font-size="10" fill="#ffe0b2" text-anchor="middle">MainNode → content path</text>
  <line x1="380" y1="84" x2="180" y2="168" stroke="#90a4ae" stroke-width="1.5" stroke-opacity="0.6" marker-end="url(#arr)"/>
  <line x1="380" y1="84" x2="380" y2="168" stroke="#90a4ae" stroke-width="1.5" stroke-opacity="0.6" marker-end="url(#arr)"/>
  <line x1="380" y1="84" x2="580" y2="168" stroke="#90a4ae" stroke-width="1.5" stroke-opacity="0.6" marker-end="url(#arr)"/>
  <text x="230" y="138" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55" text-anchor="middle">owns</text>
  <text x="368" y="138" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55" text-anchor="middle">owns</text>
  <text x="520" y="138" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity="0.55" text-anchor="middle">owns</text>
  <line x1="110" y1="240" x2="340" y2="46" stroke="#1e88e5" stroke-width="1" stroke-dasharray="4 3" stroke-opacity="0.7" marker-end="url(#arr-blue)"/>
  <line x1="380" y1="240" x2="382" y2="84" stroke="#1e88e5" stroke-width="1" stroke-dasharray="4 3" stroke-opacity="0.7" marker-end="url(#arr-blue)"/>
  <line x1="650" y1="240" x2="420" y2="84" stroke="#1e88e5" stroke-width="1" stroke-dasharray="4 3" stroke-opacity="0.7" marker-end="url(#arr-blue)"/>
  <text x="190" y="290" font-family="sans-serif" font-size="10" fill="#1e88e5" fill-opacity="0.75" text-anchor="middle">MainNode = content path</text>
  <text x="380" y="302" font-family="sans-serif" font-size="10" fill="#1e88e5" fill-opacity="0.75" text-anchor="middle">MainNode = content path</text>
  <text x="570" y="290" font-family="sans-serif" font-size="10" fill="#1e88e5" fill-opacity="0.75" text-anchor="middle">MainNode = content path</text>
</svg>
*Satellite nodes live under a content node (solid arrows = ownership by namespace path); each satellite sets `MainNode` back to the content node (dashed arrows) so access control resolves correctly.*

## Data Model: Parent Tracks Children

A parent node holds an `ImmutableList<string>` of child IDs. The layout area reads this list from the workspace stream and renders a `LayoutAreaControl` for each entry — it never issues a query to discover children.

| Entity | Parent field | Child node type |
|--------|-------------|-----------------|
| Thread | `Messages: ImmutableList<string>` | `ThreadMessage` |
| Comment | `Replies: ImmutableList<string>` | Reply `Comment` |

When a child is created, the handler appends its ID to the parent list via `workspace.GetMeshNodeStream(parentPath).Update(...)`. The update flows immediately into every subscribed Blazor client through the sync stream — no polling, no page refresh.

### Top-level vs. reply detection

A comment is top-level when its namespace ends with `_Comment` (e.g. `Doc/MyDoc/_Comment`). A reply's namespace is the parent comment's path (e.g. `Doc/MyDoc/_Comment/c1`). No parent load is required — the shape of the path is the signal.

### Comments and changes are anchored, NOT injected into the document

A text-range comment or tracked change is **never** written into the document's markdown — the document stays clean. The satellite carries its own anchor: `Start`/`Length` (the captured character range in the document's clean text), the `Version` it was captured against, the `AnchorText` (the document text at that version), and the `HighlightedText`/`OriginalText`. At render time the **effective range** is recomputed against the current text: while the document is still at the captured `Version` the stored offsets are used verbatim; once it has moved ahead, `AnchorMath` diffs `AnchorText` against the current text and maps the offsets through that diff (`diff_xIndex`-style), exposed as `EffectiveStart`/`EffectiveEnd`. The comment highlight (`CommentRendering`) and the tracked-change diff (`ChangeRendering`) are overlaid for that one render by `CollaborativeRenderer` — never persisted. This decouples annotating from the document: a `Comment`-only user (no document `Update` permission) can comment, and edits above an annotation don't strand it. Tracked changes are satellites too (`_Tracking`); **accepting** one applies its `NewText` to the document, **rejecting** drops the satellite.

---

## Handler Pattern: Synchronous, Reactive, Error-Safe

Satellite entity handlers **must be fully synchronous**. Running `await` inside the hub execution pipeline causes deadlocks in Orleans distributed mode. The right shape is: start an `IObservable` chain, subscribe, and return immediately — the response is posted from inside the callback.

### Reference implementation

```csharp
// The submission watcher invokes ExecuteMessageAsync directly after writing
// PendingUserMessages via stream.Update on the thread node — no wire message,
// no handler dispatch. It returns IObservable<Unit>; the watcher subscribes and
// treats completion (gated on the terminal Status write) as "round done".
internal static IObservable<Unit> ExecuteMessageAsync(
    IMessageHub hub,
    RoundParams request,
    AccessContext? userAccessContext)
{
    var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
    var workspace = hub.GetWorkspace();  // Capture on the handler thread

    // 1) Start both node creates concurrently (IObservable — cold until Subscribe)
    var inputObs  = meshService.CreateNode(new MeshNode(...));
    var outputObs = meshService.CreateNode(new MeshNode(...));

    // 2) Zip waits for both; 3) COMPOSE the parent update into the chain —
    //    never mutate inside a Subscribe callback (wrong thread in Orleans).
    return inputObs.Zip(outputObs)
        .SelectMany(_ => workspace.GetMeshNodeStream().Update(node =>
        {
            var thread = node.ContentAs<Thread>(hub.JsonSerializerOptions, logger);
            if (node.Content is not null && thread is null) return node;  // never clobber
            thread ??= new Thread();
            return node with
            {
                Content = thread with
                {
                    Messages = thread.Messages.AddRange([userMsgId, responseMsgId])
                }
            };
        }))
        .Select(_ => Unit.Default);
    // The CALLER subscribes — errors flow to its OnError, completion signals done.
}
```

### Handler rules

1. **Synchronous signature, observable result** — return `IObservable<T>` (or `void` for fire-and-forget), never `async Task<T>`.
2. **No `await` anywhere in the message pipeline** — `await` deadlocks in Orleans. This applies to handlers, Blazor components, layout areas, and any code on the hub execution path. Request/response is `hub.Observe<TResponse>(request).Subscribe(onNext, onError)` — `RegisterCallback` and `AwaitResponse` are `[Obsolete]` and deadlock.
3. **No permission checks in handlers or layout areas** — access control is enforced by the delivery pipeline via partition access policies. If a user lacks the `Comment` or `Thread` permission, the request is rejected before reaching the handler. Handlers assume the caller is authorized.
4. **Never use `IMeshStorage` or persistence directly** — use `IMeshService` for CRUD and `workspace.GetMeshNodeStream(path).Update(...)` for node updates.
5. **Capture `workspace` on the handler thread** — `var workspace = hub.GetWorkspace()` must be called in the handler body, not inside a callback closure.
6. **Compose, don't nest** — chain dependent writes with `SelectMany` into one observable; mutating state inside a `Subscribe` callback runs on the wrong thread and deadlocks (see [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls)).
7. **Subscribe with an error handler** — `Update`/`CreateNode` are cold; the side effect only runs on Subscribe, and an unhandled OnError means a silent failure.

### Blazor component pattern

Blazor components follow the same rule — observe, never await:

```csharp
// CORRECT: reactive request/response
Hub.Observe<CreateCommentResponse>(new CreateCommentRequest { ... },
        o => o.WithTarget(new Address(hubAddress)))
    .Subscribe(
        response =>
        {
            if (!response.Message.Success)
                logger?.LogWarning("Failed: {Error}", response.Message.Error);
        },
        ex => logger?.LogWarning(ex, "CreateComment failed"));

// WRONG: AwaitResponse blocks the Blazor circuit if response never arrives
await Hub.AwaitResponse(request, o => o.WithTarget(address), default);  // DEADLOCK ([Obsolete])
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

// WRONG: posting response before the work completes
hub.Post(response, o => o.ResponseFor(request));  // too early
meshService.CreateNode(node).Subscribe(...);       // not done yet

// WRONG: mutating state inside the Subscribe callback — wrong thread, deadlocks
meshService.CreateNode(node).Subscribe(_ =>
{
    /* direct workspace mutation */            // ← composes into the chain instead
    hub.Post(response, o => o.ResponseFor(request));
});

// CORRECT: compose the dependent write, respond from the chain's terminal events
meshService.CreateNode(node)
    .SelectMany(_ => workspace.GetMeshNodeStream(parentPath).Update(n => ...))
    .Subscribe(
        _  => hub.Post(successResponse, o => o.ResponseFor(request)),
        ex => hub.Post(failureResponse, o => o.ResponseFor(request)));
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
    workspace.GetMeshNodeStream(parentPath).Update(node => { ... })
        .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));
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
        workspace.GetMeshNodeStream(parentPath).Update(node => { ... merge child progress ... })
            .Subscribe(_ => { }, ex => logger.LogWarning(ex, "mirror failed"));

        // On completion, resolve the TCS
        if (!childThread.IsExecuting)
            tcs.TrySetResult(new DelegationResult { ... });
    });

    // 3. Submit via stream.Update on the sub-thread. The sub-thread's
    //    submission watcher reacts to PendingUserMessages and invokes
    //    ExecuteMessageAsync directly.
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

Subscribe to the node's workspace stream **before** triggering the operation, then reactively wait for the expected state to appear. This applies to operations that mutate the document text — e.g. a **tracked change**, which embeds `<!--insert:…-->`/`<!--delete:…-->` markers:

```csharp
// 0) Subscribe BEFORE sending the request — never after
var markersAppeared = workspace.GetRemoteStream<MeshNode>(docAddress)
    .Select(nodes => nodes?.FirstOrDefault(n => n.Path == docPath))
    .Select(node => (node?.Content as MarkdownContent)?.Content ?? "")
    .Where(content => content.Contains($"<!--insert:{markerId}"))
    .FirstAsync()
    .ToTask(ct);

// 1) Send request
var response = await client.AwaitResponse(request, o => o.WithTarget(address), ct);

// 2) Wait for the stream to reflect the change
var updatedContent = await markersAppeared;
```

**Comments are different**: they do not change the document text, so don't wait on the doc stream. Verify the `Comment` satellite node instead — assert it carries the anchor (`HighlightedText`, `Start`/`Length`, `Version`) via `GetDataRequest` below.

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
4.  Send CreateCommentRequest (or create the Comment node directly via meshService.CreateNode)
5.  Assert CreateCommentResponse.Success == true
6.  GetDataRequest on comment path → verify Comment content AND its anchor
    (HighlightedText, Start/Length, Version)
7.  Assert the document text was NOT mutated (no `<!--comment:{markerId}` injected)
8.  (Optional) Subscribe to comment layout area to verify the highlight renders
9.  (Optional) Send reply CreateCommentRequest, verify the parent's Replies list grew
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
    hub.GetWorkspace().GetMeshNodeStream().Update(node =>
        node with { Content = /* updated content */ })
        .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));
    return delivery.Processed();
});

// 3. Post FROM the calling grain
hub.Post(new UpdateMyContent { Text = "hello" },
    o => o.WithTarget(new Address(targetPath)));
```

**Why this works:** `GetMeshNodeStream().Update(...)` on the own node updates the **local** data source stream on the target grain, which fires a `DataChangedEvent` on the sync stream. Blazor clients that subscribed via `GetRemoteStream` receive this event over SignalR and re-render without any polling. The update path is: grain workspace → sync stream → Orleans routing → Blazor SignalR → UI render.
