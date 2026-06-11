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
| Create node | Service | `meshService.CreateNode(node).Subscribe(...)` |
| Update node | Service | `meshService.UpdateNode(node).Subscribe(...)` (routes through `GetMeshNodeStream(path).Update`) |
| Create-or-update (upsert) | Request | `hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node)).Subscribe(...)` |
| Create transient | Service | `meshService.CreateTransient(node).Subscribe(...)` |
| Delete node | Service | `meshService.DeleteNode(path).Subscribe(...)` |
| Write as system / hub | AccessService | `using (accessService.ImpersonateAsSystem()) { … }` / `ImpersonateAsHub(hub)` |
| Move node | Message | `hub.Observe(new MoveNodeRequest(src, dst)).Subscribe(...)` |
| Update typed-entity data | Message | `DataChangeRequest` → hub (EntityStore collections — see [CRUD](/Doc/DataMesh/CRUD)) |

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
  <text x="380" y="32" font-family="sans-serif" font-size="13" font-weight="bold" text-anchor="middle" fill="#fff">IMeshService</text>
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

## 2. Creates, Updates, and Deletes — IMeshService (reactive)

Node lifecycle operations route through `IMeshService` and return **`IObservable<MeshNode>`** — cold observables that run on `Subscribe`. All operations travel through the message bus (`CreateNodeRequest` etc.) so that security validators (`INodeValidator`, RLS) are enforced for every write. The caller''s identity is captured automatically from `AccessService.Context` at call time.

```csharp
var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();

// Create — identity auto-captured from the current user
var node = MeshNode.FromPath("org/Acme/NewTeam") with
{
    Name = "New Team",
    NodeType = "Team"
};
meshService.CreateNode(node)
    .Subscribe(
        created => logger.LogInformation("Created {Path}", created.Path),
        ex => logger.LogWarning(ex, "Create failed"));

// Chained create → update: compose with SelectMany — never nest Subscribes
meshService.CreateNode(node)
    .SelectMany(created => meshService.UpdateNode(created with { Name = "Renamed Team" }))
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "create+update failed"));

// Transient node (UI edit-then-confirm flows)
meshService.CreateTransient(node).Subscribe(...);

// Delete — removes the node and all its descendants, bottom to top
meshService.DeleteNode("org/Acme/OldTeam").Subscribe(...);

// Create-or-update (upsert) — single verb when the caller has the full target shape
hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(node))
    .Subscribe(resp => { /* resp.Message.WasCreated */ }, ex => ...);
```

**Behaviour summary:**

- `CreateNode` — runs `INodeValidator`, sets state to Active.
- `UpdateNode` — validates; routes through the canonical `GetMeshNodeStream(path).Update` write path on the owning hub.
- `CreateTransient` — persists in Transient state; caller confirms or discards.
- `DeleteNode` — removes the node and all descendants (bottom to top).
- `CreateOrUpdateNodeRequest` — upsert; checks existence on the handler side and dispatches create or merge-patch update (see [CQRS](/Doc/Architecture/CqrsAndContentAccess)).
- Identity is auto-captured from `AccessService` and carried across `.Subscribe()` boundaries — see [AccessContextPropagation](/Doc/Architecture/AccessContextPropagation).

### Writing as system / hub — explicit impersonation

By default every write runs under the calling user''s identity. Infrastructure code with no human in the loop (cache hydration, seeds, sync heartbeats) opts in **explicitly**:

```csharp
// System identity — Permission.All, well-known "system-security" principal
using (accessService.ImpersonateAsSystem())
{
    meshService.CreateNode(systemNode).Subscribe(...);
}

// Hub identity — stamps the hub''s address as principal
using (accessService.ImpersonateAsHub(hub))
{
    meshService.CreateNode(hubOwnedNode).Subscribe(...);
}
```

`PostPipeline` fails closed when no context is set — a write with neither a user nor an explicit impersonation is rejected, never silently stamped. Full reference: [AccessContextPropagation](/Doc/Architecture/AccessContextPropagation).

### Reading as hub / system — the same explicit impersonation

Reads scope through the **same** `AccessService` impersonation: wrap the subscription, and every query / stream opened inside the scope runs under that identity for RLS filtering — useful when infrastructure code reads before a user context is established.

```csharp
// Query with hub identity — RLS filters against the hub's own permissions
using (accessService.ImpersonateAsHub(hub))
{
    meshService.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{nodePath}"))
        .Subscribe(result => { /* … */ }, ex => logger.LogWarning(ex, "query failed"));
}
```

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

**Step 2 — query with the hub's identity:**

```csharp
// The parent hub can now read descendants of each sub-hub
using (accessService.ImpersonateAsHub(hub))
{
    meshService.Query<MeshNode>(
            MeshQueryRequest.FromQuery("path:FutuRe/AsiaRe/Analysis scope:descendants"))
        .Subscribe(change => { /* aggregate sub-hub data */ },
                   ex => logger.LogWarning(ex, "aggregation failed"));
}
```

Without the impersonation scope, the query runs under the end user's identity — which may lack access to all sub-hubs. With it, the parent hub reads using its own permissions and can always aggregate across business units.

---

## 3. Moves and Data Changes — Message-Based

A small number of operations are driven by request messages posted directly to the hub rather than through `IMeshService`.

### Moving a node

```csharp
hub.Observe(new MoveNodeRequest("org/Acme/OldPath", "org/Acme/NewPath"))
    .Subscribe(
        response =>
        {
            if (response.Message is MoveNodeResponse { Node: not null } moveResult)
            {
                // Move succeeded
            }
        },
        ex => logger.LogWarning(ex, "Move failed"));
```

### Updating data collections (typed entities)

```csharp
// Replace a set of entities in an EntityStore collection — see /Doc/DataMesh/CRUD
hub.Post(new DataChangeRequest
{
    Updates = [updatedEntity]
});
```

### Updating a MeshNode's content

```csharp
// The ONE mutation API — cold observable, the trailing Subscribe runs the write.
workspace.GetMeshNodeStream(path).Update(node => node with { Content = updated })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed"));
```

(`PatchDataChangeRequest` is the internal stream-protocol message the framework ships for you — never post it from application code.)

---

## Defining Message Types

Request/response messages must implement `IRequest<TResponse>` so the messaging framework can route responses correctly and `hub.Observe` can infer the response type.

```csharp
// Request — implements IRequest<TResponse> for type-safe hub.Observe
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

**Sending and observing:**

```csharp
// TResponse is inferred from IRequest<MoveNodeResponse>
hub.Observe(new MoveNodeRequest("old/path", "new/path"),
        o => o.WithTarget(targetAddress))
    .Subscribe(
        response => { if (response.Message.Success) { ... } },
        ex => logger.LogWarning(ex, "Move failed"));

// Tests bridge to Task via MonolithMeshTestBase.AwaitResponseAsync(request, ...).
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
