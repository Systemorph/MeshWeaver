---
Name: Permission API
Category: Documentation
Description: hub.CheckPermission / hub.GetEffectivePermissions — the canonical surface for reactive permission checks in MeshWeaver.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="16" r="1"/><rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Permission API

Two methods cover the vast majority of permission work in MeshWeaver. Both are defined on `IMessageHub` and live in the `MeshWeaver.Mesh` namespace.

```csharp
using MeshWeaver.Mesh;

// True / false for the current ambient user
IObservable<bool> canEdit = hub.CheckPermission(nodePath, Permission.Update);

// Full effective Permission set for the current user
IObservable<Permission> perms = hub.GetEffectivePermissions(nodePath);

// Explicit user — admin tooling, server-to-server
IObservable<bool> canTheyEdit = hub.CheckPermission(nodePath, "alice", Permission.Update);
IObservable<Permission> theirPerms = hub.GetEffectivePermissions(nodePath, "alice");
```

All overloads return `IObservable<T>`. Compose them with `CombineLatest`, `Select`, and `Where` as you would any other stream. In tests, bridge to `Task` at the outermost edge with `.FirstAsync().ToTask()`. Never use `await` inside `src/`.

## Enabling access control

```csharp
builder.AddRowLevelSecurity();
```

This single call activates the row-level security pipeline. Without it, `hub.CheckPermission` always emits `true`, so call sites work identically whether the mesh is gated or not — useful for lightweight dev setups where security is not yet configured.

## Composing permission streams with data streams

Permission observables are live. A revoked role propagates through the underlying `AccessAssignment` stream and re-emits automatically — no manual polling, no cache invalidation needed.

Combine a permission check with a data stream using `CombineLatest` to get a single, consistently-updated view:

```csharp
hub.CheckPermission(nodePath, Permission.Read)
    .CombineLatest(workspace.GetMeshNodeStream(nodePath),
        (canRead, node) => canRead ? RenderContent(node) : RenderAccessDenied(node));
```

Whenever the user's permissions change *or* the node content changes, the combined stream re-emits the correct view automatically.

## Quick reference

| Method | Returns | Use when |
|---|---|---|
| `hub.CheckPermission(path, permission)` | `IObservable<bool>` | Guard a single action for the ambient user |
| `hub.CheckPermission(path, userId, permission)` | `IObservable<bool>` | Admin tooling, server-to-server checks |
| `hub.GetEffectivePermissions(path)` | `IObservable<Permission>` | Render a permission summary for the ambient user |
| `hub.GetEffectivePermissions(path, userId)` | `IObservable<Permission>` | Inspect another user's effective rights |

## See also

- [AccessControl](AccessControl) — `AccessAssignment` node shape and recursive scope walk.
- [AccessContextPropagation](AccessContextPropagation) — how identity flows across hub boundaries.
