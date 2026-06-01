---
Name: Satellite Node Patterns
Category: Architecture
Description: Patterns for parent-child node hierarchies — hub ownership, persistence, content updates, and routing for Threads, Comments, and similar structures
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="8" y="2" width="8" height="5" rx="1"/><rect x="1" y="17" width="8" height="5" rx="1"/><rect x="15" y="17" width="8" height="5" rx="1"/><path d="M12 7v4M5 17v-2a4 4 0 0 1 4-4h6a4 4 0 0 1 4 4v2"/></svg>
---

# Satellite Node Patterns

A **satellite node** is any node whose `MainNode` points to a parent node. Threads and their messages, documents and their comments, approvals, activities — all follow this shape. The pattern gives each child its own hub, its own persistence, and a well-defined ownership boundary.

This page covers the invariants that every satellite type must respect, the pitfalls that are easy to hit, and reference examples from the two canonical implementations: Thread/ThreadMessage and Comment/Reply.

---

## Hub Ownership and Persistence

Every node in MeshWeaver has its own hub, created on demand when a message is routed to its address. The hub is the sole owner of the node's persistent state.

> **Rules every satellite type must follow**
> - Register `AddMeshDataSource()` in the node's `HubConfiguration`.
> - Persistence is managed by `MeshNodeTypeSource` with debounced saves — never write to storage directly.
> - No external code accesses a node's persistence via `IMeshService` or `IMeshQuery` while the hub is active.
> - The hub's workspace stream (`GetStream<MeshNode>()`) is the single source of truth for the node's content.

---

## Never Await in Hub Handlers

Hub message handlers run on the hub's serial execution block. Any `await` that waits for the same execution block to process another message will deadlock — the block is already occupied.

```csharp
// WRONG — deadlocks: CreateNodeAsync uses AwaitResponse internally,
// which needs the execution block that is currently busy running this handler.
private static async Task<IMessageDelivery> HandleRequest(IMessageHub hub, ...)
{
    await meshService.CreateNodeAsync(node);
}

// CORRECT — return immediately, continue work off the execution block
private static IMessageDelivery HandleRequest(IMessageHub hub, ...)
{
    meshService.CreateNodeAsync(node).ContinueWith(t =>
    {
        if (t.IsFaulted) logger.LogError(t.Exception, "...");
        else hub.Post(new SomeResponse { ... }, o => o.ResponseFor(delivery));
    });
    return delivery.Processed();
}
```

### Allowed Patterns

| Pattern | When to use |
|---|---|
| `hub.Post(message)` | Fire-and-forget to same or another hub |
| `hub.RegisterCallback(delivery, callback)` | Wait for a response without blocking |
| `.ContinueWith(t => ...)` | Continue after an async operation completes |
| `hub.InvokeAsync(async ct => { await foreach ... })` | Streaming loops — the **only** place `await` is acceptable |
| `stream.Subscribe(callback)` | React to workspace stream changes |

### Forbidden Patterns

| Pattern | Why |
|---|---|
| `await` in hub handlers | Deadlocks the execution block |
| `Task.Run(async () => ...)` | Breaks workspace stream propagation |
| `.GetAwaiter().GetResult()` | Blocks the execution thread |
| `JsonSerializer.SerializeToElement(...)` | Puts `$type` at the wrong position |

---

## Updating Node Content

To update a node's content — for example, appending a message ID to a Thread's list — subscribe to the workspace stream and post a `DataChangeRequest`:

```csharp
// Fires synchronously if the data is already loaded
workspace.GetStream<MeshNode>()?.Take(1).Subscribe(nodes =>
{
    var node = nodes.FirstOrDefault(n => n.Path == path);
    var updatedNode = node with { Content = newContent };
    // Post DataChangeRequest — the framework handles serialization
    hub.Post(new DataChangeRequest { Updates = [updatedNode] });
});
```

> **Never serialize manually.** The framework's `ObjectPolymorphicConverter` places `$type` discriminators at the correct position in the JSON. Manual `SerializeToElement` puts `$type` at the end, which STJ rejects on deserialization.

---

## Thread + ThreadMessage Pattern

Threads are satellite nodes stored under `User/{userId}/_Thread/`. Each Thread owns an ordered list of ThreadMessage children:

```
User/Roland/_Thread/hello-world-4651          (Thread node)
User/Roland/_Thread/hello-world-4651/msg1     (ThreadMessage node)
User/Roland/_Thread/hello-world-4651/msg2     (ThreadMessage node)
```

### Data Flow

1. **`Thread.ThreadMessages`** stores an ordered `IReadOnlyList<string>` of child message IDs.
2. **`HandleSubmitMessage`** (sync handler):
   - Subscribes to the workspace stream, appends new IDs, posts `DataChangeRequest`.
   - Fire-and-forgets `CreateNodeAsync` for the user and response message nodes.
   - Uses `ContinueWith` to start streaming in the `_Exec` sub-hub once the nodes exist.
   - Returns `delivery.Processed()` immediately — the execution block is never held.
3. **`_Exec` sub-hub** owns the streaming loop:
   - `EnsureInitializedAsync` runs via `ContinueWith` (off the execution block).
   - The streaming loop runs via `hub.InvokeAsync` — the only place an `await foreach` is permitted.
   - Response text updates are posted as `DataChangeRequest` to the ThreadMessage hub address.
4. **Blazor view** data-binds a `ThreadViewModel` that wraps the messages list.

### ThreadViewModel and Data Binding

Raw arrays (`IReadOnlyList<string>`) cannot be deserialized by `GetStream<object>`. Wrap them in a view model with value-equality:

```csharp
public record ThreadViewModel
{
    public IReadOnlyList<string> Messages { get; init; } = [];
    // Custom Equals uses SequenceEqual to suppress redundant UI updates
}
```

Push via `host.UpdateData()` with `DistinctUntilChanged()`. The Blazor view binds via `JsonPointerReference` and a converter that extracts the typed object.

---

## Comment + Reply Pattern

Comments are satellite nodes stored under `{docPath}/_Comment/`. Replies are children of the Comment node:

```
Doc/MyDoc/_Comment/abc123              (Comment node)
Doc/MyDoc/_Comment/abc123/reply1       (Reply node)
```

### Key Differences from Threads

| Aspect | Thread/Message | Comment/Reply |
|---|---|---|
| Mutation entry point | Hub message handlers | Click actions in layout areas |
| Child list | Indexed `ThreadMessages` on the parent | Discovered via `ObserveQuery` |
| Text edits | `DataChangeRequest` via `_Exec` sub-hub | Direct `UpdateNodeRequest` (fire-and-forget) |
| Node creation | `CreateNodeAsync` → confirm in handler | `CreateTransientAsync` → confirm via `UpdateNodeRequest` |

---

## PostgreSQL Table Routing

Both Thread/ThreadMessage and Comment/Reply nodes are stored in satellite tables. The mapping is configured in `PartitionDefinition.TableMappings`:

```json
{ "_Thread": "threads", "_Comment": "comments" }
```

The routing is path-based, so children automatically inherit the parent's table:

| Path | Table |
|---|---|
| `User/alice/_Thread/chat-1` | `threads` |
| `User/alice/_Thread/chat-1/msg1` | `threads` (path contains `_Thread`) |
| `Doc/MyDoc/_Comment/abc123` | `comments` |
| `Doc/MyDoc/_Comment/abc123/reply1` | `comments` (path contains `_Comment`) |

---

## ConfigureDefaultNodeHub

`MeshBuilder.ConfigureDefaultNodeHub()` registers configuration that applies to **all** node hubs. Both Monolith and Orleans routing must compose this overlay with the node's own `HubConfiguration` — not replace it:

```csharp
// Correct: compose default config with the node's own config
var hubConfig = defaultConfig != null
    ? config => nodeConfig(defaultConfig(config))
    : nodeConfig;
```

Skipping this composition means handlers such as `AddThreadSupport()` (which registers `CreateThreadRequest`) will be absent from the node hub, causing silent failures when thread creation is attempted.

---

## Type Registry

AI types must be registered on all three hub boundaries or cross-hub messages will arrive as raw `JsonElement` and fail to deserialize:

| Hub | Registration call |
|---|---|
| Mesh hub | `ConfigureHub(config => config.TypeRegistry.AddAITypes())` |
| Client hub | `configuration.TypeRegistry.AddAITypes()` in `AddChatViews()` |
| Node hubs | Inherited via `ConfigureDefaultNodeHub` composition (see above) |
