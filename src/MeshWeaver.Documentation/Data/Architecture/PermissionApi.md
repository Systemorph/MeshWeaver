---
Name: Permission API
Category: Documentation
Description: hub.CheckPermission / hub.GetEffectivePermissions — the canonical surface for permission checks.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="16" r="1"/><rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Permission API

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

All methods return `IObservable<T>`. Compose with `CombineLatest` / `Select` / `Where`; tests bridge to `Task` at their edge with `.FirstAsync().ToTask()`. Never `await` in `src/`.

## Enabling access control on a mesh

```csharp
builder.AddRowLevelSecurity();
```

Without this call, `hub.CheckPermission` always emits `true` — same call sites work whether the mesh is gated or not.

## Composing with other streams

```csharp
hub.CheckPermission(nodePath, Permission.Read)
    .CombineLatest(workspace.GetMeshNodeStream(nodePath),
        (canRead, node) => canRead ? RenderContent(node) : RenderAccessDenied(node));
```

A revoked role propagates through the underlying AccessAssignment stream and re-emits — no manual polling, no cache invalidation.

## See also

- [AccessControl](AccessControl) — AccessAssignment node shape, recursive scope walk.
- [AccessContextPropagation](AccessContextPropagation) — how identity flows across hub boundaries.
