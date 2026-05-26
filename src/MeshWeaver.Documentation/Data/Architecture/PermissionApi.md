---
Name: Permission API
Category: Documentation
Description: Canonical extension surface for checking effective permissions — hub.CheckPermission / hub.GetEffectivePermissions. Mirrors the shape of hub.CancelActivity / hub.StartThread.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="16" r="1"/><rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Permission API — `hub.CheckPermission()` / `hub.GetEffectivePermissions()`

> **🚨 Default surface for every permission check.** Layout areas, click actions, MCP plugins, agent tools — all of them. If you find yourself resolving `ISecurityService` from DI or calling `PermissionHelper.GetEffectivePermissions`, stop and use the hub extension instead.

```csharp
using MeshWeaver.Mesh;

// True / false on the current ambient user
IObservable<bool> canEdit = hub.CheckPermission(nodePath, Permission.Update);

// Full effective Permission set on the current user
IObservable<Permission> perms = hub.GetEffectivePermissions(nodePath);

// Explicit user — for admin tooling / server-to-server checks
IObservable<bool> canTheyEdit = hub.CheckPermission(nodePath, "alice", Permission.Update);
IObservable<Permission> theirPerms = hub.GetEffectivePermissions(nodePath, "alice");
```

All four methods return `IObservable<T>` — no `Task<T>`, no `await`, no `.FirstAsync().ToTask()` bridge in `src/`. Compose with `CombineLatest`, `Select`, `Where`. Tests bridge to `Task` at their edge.

## What it does behind the scenes

`HubPermissionExtensions` resolves the process-wide `ISecurityService` from the hub's service provider and forwards. `ISecurityService` composes its effective-permission computation against the **process-wide `IMeshNodeStreamCache`** under `WellKnownUsers.System` identity:

- **AccessAssignment** subtree (`namespace:{scope}/_Access nodeType:AccessAssignment`) is cached once per scope, shared across every hub asking the same question.
- **PartitionAccessPolicy** chain (one node per scope) is also cached once per scope.
- The system-identity subscription is held for the cache's lifetime via `Observable.Using(accessService.ImpersonateAsSystem(), …)` — no per-call `using` scope that exits before the observable emits.

The result: zero per-hub synced-query subscriptions for access lookups, zero `hub-shaped principal set as AccessContext — must never happen` errors. Permission revocations propagate within the cache's TTL (currently 30 s).

## Why a single API surface

Three reasons:

1. **One place to fix.** The pre-extension world had `PermissionHelper.GetEffectivePermissions`, direct `_securityService.GetEffectivePermissions` calls in layout areas, ad-hoc `workspace.GetQuery("namespace:…/_Access …")` walks, and a few inlined "check `Permission.Read` via `_Access` query" patterns. When the cache identity / claim-first emission semantics changed, every site needed a separate edit. Now there's one extension; the cache lives in `MeshNodeStreamCache`, the policy chain lives in `SecurityService`, and the call sites all read `hub.CheckPermission`.

2. **Hub-scoped resolution.** The extension uses `hub.ServiceProvider`, so when a click handler is on the layout-area hub, the resolution finds the layout hub's `ISecurityService`; when MCP code is on the mesh hub, it finds the mesh hub's. Application code never needs to thread `IMessageHub` plus `ISecurityService` through DI — the hub already has both.

3. **Cancellable / composable.** Returning `IObservable<bool>` gives the caller a stream that re-emits when the underlying AccessAssignment set changes. The Blazor side panel and MCP autocomplete already wire to that — flipping a role propagates within ~30 s without manual reload.

## Don't do this

```csharp
// ❌ Direct ISecurityService resolution from application code
var sec = host.Hub.ServiceProvider.GetRequiredService<ISecurityService>();
var perms = await sec.GetEffectivePermissions(path).FirstAsync().ToTask();

// ❌ PermissionHelper static — legacy, prefer the extension
var perms = PermissionHelper.GetEffectivePermissions(host.Hub, path);

// ❌ Hand-rolled _Access query — bypasses the cache, leaks AccessContext
workspace.GetQuery("namespace:" + scope + "/_Access nodeType:AccessAssignment");
```

## Do this

```csharp
// ✅ Hub extension, reactive composition
var content = hub.CheckPermission(nodePath, Permission.Read)
    .CombineLatest(workspace.GetMeshNodeStream(nodePath), (canRead, node) =>
        canRead ? RenderContent(node) : RenderAccessDenied(node));
```

## Sanctioned exceptions

The new API replaces application-code callers of `ISecurityService`. The following framework-internal callers stay on the direct interface because they live inside the security infrastructure itself:

- `MeshWeaver.Hosting.Security.AccessControlPipeline` — the request-time validator that runs `HasPermission` synchronously on every inbound delivery; it owns the cache primitives the extension uses.
- `MeshWeaver.Hosting.Persistence.Query.StorageAdapterMeshQueryProvider` — the secured-query surface that applies per-node validators using the same cache.
- `MeshWeaver.Graph.Security.RlsNodeValidator` — the row-level-security node validator the storage adapter consumes.
- `MeshWeaver.Hosting.Security.SecurityService` itself.

Application code outside these four files calls `hub.CheckPermission` / `hub.GetEffectivePermissions`.

## See also

- [AccessControl.md](AccessControl) — full security architecture (claim-first, recursive scope chain, RLS validators).
- [AccessContextPropagation.md](AccessContextPropagation) — how the framework carries identity through hub boundaries.
- [HubActivityExtensions](xref:MeshWeaver.Mesh.HubActivityExtensions) — the parallel pattern for ActivityLog state transitions (`hub.CancelActivity`, `hub.RequestActivityStatus`).
