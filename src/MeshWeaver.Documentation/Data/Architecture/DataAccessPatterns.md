---
Name: Data Access Patterns
Category: Documentation
Description: How to correctly read, create, update, and delete mesh nodes and data from application code
---

MeshWeaver enforces strict data access patterns to ensure security (RLS validation), consistency, and traceability. Application code must **never** use `IPersistenceService` or `IMeshCatalog` directly — these are internal infrastructure interfaces.

# The Three Patterns

## 1. Reads — IMeshQuery

All read operations use the `IMeshQuery` interface with GitHub-style query syntax.

```csharp
// Inject IMeshQuery
var query = hub.ServiceProvider.GetRequiredService<IMeshQuery>();

// Get a single node by path
var node = await query.QueryAsync("path:org/Acme", maxResults: 1)
    .FirstOrDefaultAsync(ct);

// Query children of a path
await foreach (var child in query.QueryAsync("parent:org/Acme"))
{
    // process child nodes
}

// Search by name
await foreach (var match in query.QueryAsync("name:Report parent:org", maxResults: 10))
{
    // process matches
}
```

**Query syntax supports:**
- `path:<exact-path>` — exact path match
- `parent:<path>` — direct children of a path
- `name:<text>` — name contains text
- `type:<node-type>` — filter by NodeType
- Combine filters: `"parent:org type:Team name:Alpha"`

## 2. Creates and Deletes — IMeshNodeFactory

Node lifecycle operations (create, delete) use the public `IMeshNodeFactory` interface.

```csharp
// Inject IMeshNodeFactory
var factory = hub.ServiceProvider.GetRequiredService<IMeshNodeFactory>();

// Create a confirmed node
var node = MeshNode.FromPath("org/Acme/NewTeam") with
{
    Name = "New Team",
    NodeType = "Team"
};
var created = await factory.CreateNodeAsync(node, createdBy: "user@example.com", ct);

// Create a transient node (for UI creation flows)
var transient = await factory.CreateTransientAsync(node, ct);

// Delete a node
await factory.DeleteNodeAsync("org/Acme/OldTeam", recursive: true, ct);
```

**Key behaviors:**
- `CreateNodeAsync` validates via `INodeValidator`, sets state to Confirmed
- `CreateTransientAsync` persists in Transient state for UI edit-then-confirm flows
- `DeleteNodeAsync` removes a node (and optionally all descendants)

## 3. Updates and Moves — Message-Based

Data modifications use request messages posted to the hub.

### Updating a node

```csharp
// Update node properties via UpdateNodeRequest
var response = await hub.AwaitResponse(
    new UpdateNodeRequest(updatedNode),
    ct);

// Or fire-and-forget for non-critical updates
hub.Post(new UpdateNodeRequest(node with { Name = "Updated Name" }));
```

### Moving a node

```csharp
// Move a node to a new path
var response = await hub.AwaitResponse(
    new MoveNodeRequest("org/Acme/OldPath", "org/Acme/NewPath"),
    ct);

if (response.Message is MoveNodeResponse { Node: not null } moveResult)
{
    // Move succeeded
}
```

### Updating data collections

```csharp
// Update data via DataChangeRequest
hub.Post(new DataChangeRequest
{
    Updates = [updatedEntity]
});

// Or use PatchDataChangeRequest for partial updates
hub.Post(new PatchDataChangeRequest
{
    Updates = [partialEntity]
});
```

# Defining Message Types

Request/response messages must implement `IRequest<TResponse>` so the messaging framework can route responses correctly and `AwaitResponse` can infer the response type.

```csharp
// Request message — must implement IRequest<TResponse>
public record MoveNodeRequest(string SourcePath, string TargetPath)
    : IRequest<MoveNodeResponse>;

// Response message
public record MoveNodeResponse
{
    public bool Success { get; init; }
    public MeshNode? Node { get; init; }
    public string? Error { get; init; }
}
```

**Sending and awaiting:**

```csharp
// Type-safe: TResponse is inferred from IRequest<MoveNodeResponse>
var response = await hub.AwaitResponse(
    new MoveNodeRequest("old/path", "new/path"),
    o => o.WithTarget(targetAddress),
    ct);

// response.Message is MoveNodeResponse
if (response.Message.Success) { ... }
```

**Registering the handler:**

```csharp
// In hub configuration
config.WithHandler<MoveNodeRequest>(async (hub, delivery, ct) =>
{
    // process the request...
    return delivery.Processed(new MoveNodeResponse { Success = true, Node = moved });
});
```

**Registering types for serialization:**

```csharp
// In AddMeshTypes() or equivalent setup
config.TypeRegistry.WithType(typeof(MoveNodeRequest), nameof(MoveNodeRequest));
config.TypeRegistry.WithType(typeof(MoveNodeResponse), nameof(MoveNodeResponse));
```

# Why Internal Interfaces?

`IPersistenceService` and `IMeshCatalog` are **internal** to infrastructure assemblies. Direct use from application code:

1. **Bypasses RLS validation** — security policies are enforced at the message handler layer
2. **Breaks traceability** — message-based operations are logged and auditable
3. **Couples to storage details** — persistence backends (Cosmos, PostgreSQL, file system) are an implementation detail
4. **Skips business rules** — `INodeValidator` runs on create/update/move/delete operations at the handler level

Only infrastructure assemblies (`MeshWeaver.Hosting`, `MeshWeaver.Hosting.Orleans`, etc.) have `InternalsVisibleTo` access to these interfaces.

# Summary

| Operation | Pattern | Interface / Type |
|-----------|---------|-----------------|
| Read nodes | Query | `IMeshQuery` |
| Create node | Factory | `IMeshNodeFactory.CreateNodeAsync` |
| Create transient | Factory | `IMeshNodeFactory.CreateTransientAsync` |
| Delete node | Factory | `IMeshNodeFactory.DeleteNodeAsync` |
| Update node | Message | `UpdateNodeRequest` → hub |
| Move node | Message | `MoveNodeRequest` → hub |
| Update data | Message | `DataChangeRequest` → hub |
