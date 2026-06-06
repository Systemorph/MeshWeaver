---
Name: Data Access Patterns
Category: Documentation
Description: The canonical APIs for reading, creating, updating, and deleting mesh nodes from application code — including security enforcement, node identity, and message-based operations
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M20 5v14c0 1.66-3.58 3-8 3s-8-1.34 8-3V5"/><path d="M4 12c0 1.66 3.58 3 8 3s8-1.34 8-3"/></svg>
---

MeshWeaver enforces strict data access patterns to ensure security (RLS validation), consistency, and traceability. Application code must **never** use `IMeshStorage` or `IMeshCatalog` directly — these are internal infrastructure interfaces.

There are three access patterns, each covering a distinct class of operation:

| Operation | Pattern | Interface / Type |
|---|---|---|
| Read nodes | Query | `IMeshQuery` |
| Read/write **one** node by path | Stream | `hub.GetMeshNodeStream(path)` / `workspace.GetMeshNodeStream(path)` |
| Create node | Factory | `IMeshNodePersistence.CreateNodeAsync` |
| Update node | Factory | `IMeshNodePersistence.UpdateNodeAsync` |
| Create transient | Factory | `IMeshNodePersistence.CreateTransientAsync` |
| Delete node | Factory | `IMeshNodePersistence.DeleteNodeAsync` |
| CRUD as node identity | Factory | `IMeshNodePersistence.ImpersonateAsNode()` |
| Read as node identity | Query | `IMeshQuery.ImpersonateAsNode()` |
| Move node | Message | `MoveNodeRequest` → hub |
| Update data | Message | `DataChangeRequest` → hub |

> ### 🚨 One mesh node by path → `GetMeshNodeStream`, never `GetRemoteStream<MeshNode>`
>
> The **single canonical API** for reading or writing one mesh node by path is
> `hub.GetMeshNodeStream(path)` / `workspace.GetMeshNodeStream(path)` (extension methods in
> `MeshWeaver.Mesh.Contract`). It routes every reader and writer through the shared
> `IMeshNodeStreamCache` — one process-wide upstream per path, so writes are visible to all
> readers. **Read** by subscribing to the handle (`IObservable<MeshNode>`, `Content` already
> typed); **write** via `.Update(current => current with { … }).Subscribe(...)` (cold
> observable — the write only runs on `Subscribe`).
>
> `workspace.GetRemoteStream<MeshNode, …>` / `GetRemoteStream<MeshNode>(addr)` is
> **discouraged** — the single-node remote reduce does not converge (divergent mirror
> streams, writes invisible to readers). Calling it **logs a warning** from the
> `MeshWeaver.Data` Workspace logger; grep that channel after a run to find stragglers to
> migrate. The only sanctioned callers are the cache's own upstream and the MeshNode
> reduce-callback plumbing, which use an internal `GetRemoteStreamUnchecked` overload to
> avoid the warning.

---
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 320" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="20" y="10" width="210" height="56" rx="10" fill="#1e88e5"/>
  <text x="125" y="32" font-family="sans-serif" font-size="13" font-weight="bold" text-anchor="middle" fill="#fff">IMeshQuery</text>
  <text x="125" y="50" font-family="sans-serif" font-size="11" text-anchor="middle" fill="#fff">Reads / Queries</text>
  <rect x="275" y="10" width="210" height="56" rx="10" fill="#43a047"/>
  <text x="380" y="32" font-family="sans-serif" font-size="13" font-weight="bold" text-anchor="middle" fill="#fff">IMeshNodePersistence</text>
  <text x="380" y="50" font-family="sans-serif" font-size="11" text-anchor="middle" fill="#fff">Create · Update · Delete</text>
  <rect x="530" y="10" width="210" height="56" rx="10" fill="#f57c00"/>
  <text x="635" y="32" font-family="sans-serif" font-size="13" font-weight="bold" text-anchor="middle" fill="#fff">Message Bus</text>
  <text x="635" y="50" font-family="sans-serif" font-size="11" text-anchor="middle" fill="#fff">Move · DataChange</text>
  <line x1="125" y1="66" x2="125" y2="118" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="66" x2="380" y2="118" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="635" y1="66" x2="635" y2="118" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="20" y="118" width="720" height="64" rx="10" fill="#8e24aa"/>
  <text x="380" y="143" font-family="sans-serif" font-size="13" font-weight="bold" text-anchor="middle" fill="#fff">Security &amp; Validation Layer</text>
  <text x="380" y="162" font-family="sans-serif" font-size="11" text-anchor="middle" fill="#fff">INodeValidator · RLS policies · AccessContext · audit trail</text>
  <line x1="380" y1="182" x2="380" y2="230" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="20" y="230" width="720" height="64" rx="10" fill="#26a69a"/>
  <text x="380" y="255" font-family="sans-serif" font-size="13" font-weight="bold" text-anchor="middle" fill="#fff">Storage Backend</text>
  <text x="380" y="274" font-family="sans-serif" font-size="11" text-anchor="middle" fill="#fff">IMeshStorage · IMeshCatalog  (internal — not accessible from app code)</text>
</svg>

*Three access patterns funnel through the security layer before reaching storage — direct storage access bypasses RLS and auditing.*

## 1. Reads — IMeshQuery

All read operations go through `IMeshQuery`, which uses a GitHub-style query syntax to filter, search, and enumerate nodes.

```csharp
// Resolve from the hub's service provider
var query = hub.ServiceProvider.GetRequiredService<IMeshQuery>();

// Get a single node by exact path
var node = await query.QueryAsync("path:org/Acme", maxResults: 1)
    .FirstOrDefaultAsync(ct);

// List direct children of a path
await foreach (var child in query.QueryAsync("parent:org/Acme"))
{
    // process child nodes
}

// Search by name within a namespace
await foreach (var match in query.QueryAsync("name:Report parent:org", maxResults: 10))
{
    // process matches
}
```

**Supported query syntax:**

| Token | Meaning |
|---|---|
| `path:<exact-path>` | Exact path match |
| `parent:<path>` | Direct children of a path |
| `name:<text>` | Name contains text |
| `type:<node-type>` | Filter by NodeType |

Tokens compose freely: `"parent:org type:Team name:Alpha"` matches all Team nodes under `org` whose name contains "Alpha".

---

## 2. Creates, Updates, and Deletes — IMeshNodePersistence

Node lifecycle operations route through `IMeshNodePersistence`. All operations travel through the message bus so that security validators (`INodeValidator`, RLS) are enforced for every write.

`IMeshNodePersistence` is registered as a **scoped service** — each hub gets its own instance with the correct `IMessageHub` injected. The caller's identity (`createdBy`, `updatedBy`, `deletedBy`) is automatically resolved from `AccessService.Context.ObjectId`; you do not pass it explicitly.

```csharp
// Resolve from the hub's service provider (scoped — correct hub per level)
var factory = hub.ServiceProvider.GetRequiredService<IMeshNodePersistence>();

// Create — identity auto-resolved from the current user
var node = MeshNode.FromPath("org/Acme/NewTeam") with
{
    Name = "New Team",
    NodeType = "Team"
};
var created = await factory.CreateNodeAsync(node, ct: ct);

// Update
var updated = await factory.UpdateNodeAsync(
    created with { Name = "Renamed Team" }, ct: ct);

// Create a transient node (for UI edit-then-confirm flows)
var transient = await factory.CreateTransientAsync(node, ct);

// Delete — removes the node and all its descendants, bottom to top
await factory.DeleteNodeAsync("org/Acme/OldTeam", ct: ct);
```

**Behaviour summary:**

- `CreateNodeAsync` — runs `INodeValidator`, sets state to Active.
- `UpdateNodeAsync` — validates and updates an existing node.
- `CreateTransientAsync` — persists in Transient state; caller confirms or discards.
- `DeleteNodeAsync` — removes the node and all descendants (bottom to top).
- Identity is auto-resolved from `AccessService` — explicit `createdBy`/`updatedBy`/`deletedBy` is optional.

### Node identity for writes — ImpersonateAsNode

By default, `IMeshNodePersistence` operations use the current user's identity. Call `ImpersonateAsNode()` to switch to the hub's own address as the acting principal — useful for infrastructure code that runs without a human in the loop.

```csharp
var factory = hub.ServiceProvider.GetRequiredService<IMeshNodePersistence>();

// All subsequent operations use the hub's address as identity
var impersonated = factory.ImpersonateAsNode();

var node = MeshNode.FromPath("system/AutoGenerated") with
{
    Name = "System Node",
    NodeType = "Markdown"
};
var created = await impersonated.CreateNodeAsync(node, ct: ct);
await impersonated.UpdateNodeAsync(created with { Name = "Updated" }, ct: ct);
await impersonated.DeleteNodeAsync("system/AutoGenerated", ct: ct);
```

Key properties:

- `ImpersonateAsNode()` sets a flag on the same instance — no separate wrapper class.
- `createdBy`/`updatedBy`/`deletedBy` auto-resolve to `hub.Address.ToFullString()`.
- `PostOptions.ImpersonateAsHub()` is set, so `AccessContext.ObjectId` becomes the hub's address.
- The hub must have the appropriate roles/permissions assigned to its address.
- Calling `ImpersonateAsNode()` on an already-impersonated instance returns itself.

### Node identity for reads — ImpersonateAsNode on IMeshQuery

`IMeshQuery` supports the same `ImpersonateAsNode()` pattern for reads. When impersonated, all queries use the hub's address as `UserId` for RLS filtering — useful when infrastructure code checks node existence before a user context is established.

```csharp
var meshQuery = hub.ServiceProvider.GetRequiredService<IMeshQuery>();

// Query with node identity — bypasses user-level RLS, uses node's own permissions
var existing = await meshQuery.ImpersonateAsNode()
    .QueryAsync<MeshNode>($"path:{nodePath}")
    .FirstOrDefaultAsync(ct);

// Without impersonation — uses the current user's identity from AccessService
var result = await meshQuery
    .QueryAsync<MeshNode>($"path:{nodePath}")
    .FirstOrDefaultAsync(ct);
```

Key properties:

- Sets `MeshQueryRequest.UserId` to `hub.Address.ToFullString()` on all queries.
- The node must have Read permission on the target namespace.
- Calling `ImpersonateAsNode()` on an already-impersonated instance returns itself.

### Example: aggregating across business-unit sub-hubs (FutuRe)

The `FutuRe/Analysis` group hub needs to read data from two business-unit sub-hubs: `FutuRe/AsiaRe/Analysis` and `FutuRe/EuropeRe/Analysis`. Each sub-hub has its own RLS scope, so the parent hub must be granted explicit read access via `AccessAssignment` nodes, then query using its own identity.

**Step 1 — grant read access in each sub-hub** (see `samples/Graph/Data/FutuRe/AsiaRe/Analysis/FutuRe_Analysis_Access.json`):

```json
{
  "id": "FutuRe_Analysis_Access",
  "namespace": "FutuRe/AsiaRe/Analysis",
  "name": "FutuRe/Analysis Node Access",
  "nodeType": "AccessAssignment",
  "content": {
    "$type": "AccessAssignment",
    "accessObject": "FutuRe/Analysis",
    "displayName": "Group Analysis Hub",
    "roles": [
      { "role": "Reader" }
    ]
  }
}
```

Apply the same pattern to `FutuRe/EuropeRe/Analysis/FutuRe_Analysis_Access.json`.

**Step 2 — query with node identity:**

```csharp
var meshQuery = hub.ServiceProvider.GetRequiredService<IMeshQuery>();
var impersonatedQuery = meshQuery.ImpersonateAsNode();

// The parent hub can now read descendants of each sub-hub
await foreach (var node in impersonatedQuery.QueryAsync<MeshNode>(
    "path:FutuRe/AsiaRe/Analysis scope:descendants"))
{
    // aggregate sub-hub data
}
```

Without `ImpersonateAsNode()`, the query runs under the end user's identity — which may lack access to all sub-hubs. With it, the parent hub reads using its own permissions and can always aggregate across business units.

---

## 3. Moves and Data Changes — Message-Based

A small number of operations are driven by request messages posted directly to the hub rather than through `IMeshNodePersistence`.

### Moving a node

```csharp
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
// Replace a set of entities
hub.Post(new DataChangeRequest
{
    Updates = [updatedEntity]
});

// Partial update (JSON-merge patch semantics)
hub.Post(new PatchDataChangeRequest
{
    Updates = [partialEntity]
});
```

---

## Defining Message Types

Request/response messages must implement `IRequest<TResponse>` so the messaging framework can route responses correctly and `AwaitResponse` can infer the response type.

```csharp
// Request — implements IRequest<TResponse> for type-safe AwaitResponse
public record MoveNodeRequest(string SourcePath, string TargetPath)
    : IRequest<MoveNodeResponse>;

// Response
public record MoveNodeResponse
{
    public bool Success { get; init; }
    public MeshNode? Node { get; init; }
    public string? Error { get; init; }
}
```

**Sending and awaiting:**

```csharp
// TResponse is inferred from IRequest<MoveNodeResponse>
var response = await hub.AwaitResponse(
    new MoveNodeRequest("old/path", "new/path"),
    o => o.WithTarget(targetAddress),
    ct);

if (response.Message.Success) { ... }
```

**Registering the handler:**

```csharp
config.WithHandler<MoveNodeRequest>(async (hub, delivery, ct) =>
{
    // process the request...
    return delivery.Processed(new MoveNodeResponse { Success = true, Node = moved });
});
```

**Registering types for serialization:**

```csharp
config.TypeRegistry.WithType(typeof(MoveNodeRequest), nameof(MoveNodeRequest));
config.TypeRegistry.WithType(typeof(MoveNodeResponse), nameof(MoveNodeResponse));
```

---

## Why Are the Internal Interfaces Off-Limits?

`IMeshStorage` and `IMeshCatalog` are **internal** to infrastructure assemblies. Using them directly from application code:

1. **Bypasses RLS validation** — security policies are enforced at the message handler layer, not in storage.
2. **Breaks traceability** — message-based operations are logged and auditable; direct storage calls are not.
3. **Couples to storage details** — backends (Cosmos, PostgreSQL, file system) are an implementation detail and may change.
4. **Skips business rules** — `INodeValidator` runs at the handler level on every create, update, move, and delete.

Only infrastructure assemblies (`MeshWeaver.Hosting`, `MeshWeaver.Hosting.Orleans`, etc.) have `InternalsVisibleTo` access to these interfaces.
