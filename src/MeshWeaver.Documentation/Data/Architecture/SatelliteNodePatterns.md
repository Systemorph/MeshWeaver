# Satellite Node Patterns

Satellite nodes are nodes whose `MainNode` points to a parent node. They form hierarchical structures like Threads with Messages, or Documents with Comments. This document describes the architecture patterns for implementing satellite nodes with children.

## Hub Ownership and Persistence

Each node has its own hub, created on demand when messages are routed to its address. The hub manages persistence exclusively through `AddMeshDataSource()` and `MeshNodeTypeSource`.

**Rules:**
- Every satellite node type MUST register `AddMeshDataSource()` in its `HubConfiguration`
- Persistence is managed by `MeshNodeTypeSource` with debounced saves
- No external code should access a node's persistence directly via `IMeshService` or `IMeshQuery` while the hub is active
- The hub's workspace stream (`GetStream<MeshNode>()`) is the single source of truth

## Never Await in Hub Handlers

Hub message handlers run on the execution block. Any `await` that depends on the execution block processing another message will deadlock.

```csharp
// WRONG — deadlocks
private static async Task<IMessageDelivery> HandleRequest(IMessageHub hub, ...)
{
    await meshService.CreateNodeAsync(node); // Uses AwaitResponse internally — DEADLOCK
}

// CORRECT — fire-and-forget with ContinueWith
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

| Pattern | When to Use |
|---------|------------|
| `hub.Post(message)` | Fire-and-forget to same or other hub |
| `hub.RegisterCallback(delivery, callback)` | Wait for response without blocking |
| `.ContinueWith(t => ...)` | Continue after async operation completes |
| `hub.InvokeAsync(async ct => { await foreach ... })` | Streaming loops (the ONLY place await is OK) |
| `stream.Subscribe(callback)` | React to workspace stream changes |

### Forbidden Patterns

| Pattern | Why |
|---------|-----|
| `await` in hub handlers | Deadlocks the execution block |
| `Task.Run(async () => ...)` | Breaks workspace stream propagation |
| `.GetAwaiter().GetResult()` | Blocks the execution thread |
| `JsonSerializer.SerializeToElement(...)` | Puts `$type` at wrong position |

## Updating Node Content

To update a node's content (e.g., adding message IDs to a Thread), use `DataChangeRequest` with typed objects:

```csharp
// Subscribe to workspace stream (fires synchronously if data exists)
workspace.GetStream<MeshNode>()?.Take(1).Subscribe(nodes =>
{
    var node = nodes.FirstOrDefault(n => n.Path == path);
    var updatedNode = node with { Content = newContent };
    // Post DataChangeRequest — framework handles serialization
    hub.Post(new DataChangeRequest { Updates = [updatedNode] });
});
```

**Never serialize manually** — the framework's `ObjectPolymorphicConverter` handles `$type` discriminators. Manual `SerializeToElement` puts `$type` at the end, which STJ rejects during deserialization.

## Thread + ThreadMessage Pattern

Threads are satellite nodes under `User/{userId}/_Thread/`. Each Thread has ThreadMessage children:

```
User/Roland/_Thread/hello-world-4651        (Thread node)
User/Roland/_Thread/hello-world-4651/msg1   (ThreadMessage node)
User/Roland/_Thread/hello-world-4651/msg2   (ThreadMessage node)
```

### Data Flow

1. **Thread.ThreadMessages** stores an ordered `IReadOnlyList<string>` of child message IDs
2. **HandleSubmitMessage** (sync handler):
   - Subscribes to workspace stream, appends new IDs, posts `DataChangeRequest`
   - Fire-and-forget `CreateNodeAsync` for user + response message nodes
   - `ContinueWith` starts streaming in `_Exec` sub-hub after nodes exist
   - Returns `delivery.Processed()` immediately
3. **_Exec sub-hub** owns the streaming loop:
   - `EnsureInitializedAsync` runs via `ContinueWith` (off execution block)
   - Streaming loop via `hub.InvokeAsync` (only awaits chat stream enumeration)
   - Response text updates posted as `DataChangeRequest` to ThreadMessage hub address
4. **Blazor view** data-binds `ThreadViewModel` (wraps Messages list)

### ThreadViewModel and Data Binding

Raw arrays (`IReadOnlyList<string>`) cannot be deserialized by `GetStream<object>`. Wrap in a view model:

```csharp
public record ThreadViewModel
{
    public IReadOnlyList<string> Messages { get; init; } = [];
    // Custom Equals with SequenceEqual to prevent redundant UI updates
}
```

Push via `host.UpdateData()` with `DistinctUntilChanged()`. The Blazor view binds via `JsonPointerReference` and a converter that extracts the typed object.

## Comment + Reply Pattern

Comments are satellite nodes under `{docPath}/_Comment/`. Replies are children of comments:

```
Doc/MyDoc/_Comment/abc123           (Comment node)
Doc/MyDoc/_Comment/abc123/reply1    (Reply comment node)
```

### Key Differences from Threads

- **No message handlers** — comments use click actions in layout areas
- **No indexed child list** — children discovered via reactive query (`ObserveQuery`)
- **Direct UpdateNodeRequest** for text edits (fire-and-forget `hub.Post`)
- **Simpler creation** — `CreateTransientAsync` then confirm via `UpdateNodeRequest`

## PostgreSQL Table Routing

Both Thread/ThreadMessage and Comment/Reply nodes route to satellite tables via `PartitionDefinition.TableMappings`:

```json
{ "_Thread": "threads", "_Comment": "comments" }
```

The path-based mapping means children inherit the parent's table:
- `User/alice/_Thread/chat-1` → `threads` table
- `User/alice/_Thread/chat-1/msg1` → `threads` table (path contains `_Thread`)

## ConfigureDefaultNodeHub

`MeshBuilder.ConfigureDefaultNodeHub()` registers configuration applied to ALL node hubs. Both Monolith and Orleans routing MUST compose this with the node's own `HubConfiguration`:

```csharp
// Correct composition (both routing services)
var hubConfig = defaultConfig != null
    ? config => nodeConfig(defaultConfig(config))
    : nodeConfig;
```

This ensures handlers like `AddThreadSupport()` (for `CreateThreadRequest`) are available on every node hub.

## Type Registry

AI types must be registered on both mesh hub and client hub TypeRegistries:
- **Mesh hub**: via `ConfigureHub(config => config.TypeRegistry.AddAITypes())`
- **Client hub**: via `configuration.TypeRegistry.AddAITypes()` in `AddChatViews()`
- **Node hubs**: via `ConfigureDefaultNodeHub` composition

Without this, messages crossing hub boundaries arrive as `JsonElement` (RawJson) and fail to deserialize.
