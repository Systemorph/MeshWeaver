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
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 280" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="perm-arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".65"/>
    </marker>
  </defs>
  <rect x="20" y="100" width="160" height="52" rx="10" fill="#5c6bc0"/>
  <text x="100" y="121" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">AccessAssignment</text>
  <text x="100" y="140" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">stream</text>
  <rect x="20" y="190" width="160" height="52" rx="10" fill="#26a69a"/>
  <text x="100" y="211" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">MeshNode</text>
  <text x="100" y="230" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">data stream</text>
  <rect x="260" y="80" width="170" height="52" rx="10" fill="#1e88e5"/>
  <text x="345" y="101" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">CheckPermission</text>
  <text x="345" y="120" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">IObservable&lt;bool&gt;</text>
  <rect x="260" y="170" width="170" height="52" rx="10" fill="#1e88e5"/>
  <text x="345" y="191" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">GetEffective</text>
  <text x="345" y="210" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">Permissions</text>
  <rect x="510" y="125" width="160" height="52" rx="10" fill="#f57c00"/>
  <text x="590" y="146" text-anchor="middle" font-family="sans-serif" font-size="13" fill="#fff" font-weight="bold">CombineLatest</text>
  <text x="590" y="165" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ccc">live, reactive view</text>
  <line x1="180" y1="126" x2="258" y2="106" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#perm-arr)"/>
  <line x1="180" y1="216" x2="258" y2="196" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#perm-arr)"/>
  <line x1="180" y1="216" x2="508" y2="170" stroke="currentColor" stroke-opacity=".35" stroke-width="1.5" stroke-dasharray="5,4" marker-end="url(#perm-arr)"/>
  <line x1="430" y1="106" x2="508" y2="142" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#perm-arr)"/>
  <line x1="430" y1="196" x2="508" y2="158" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#perm-arr)"/>
  <text x="380" y="40" text-anchor="middle" font-family="sans-serif" font-size="12" fill="currentColor" fill-opacity=".5">role revoked → AccessAssignment re-emits → permission stream re-emits → view updates</text>
</svg>

*Permission and data streams both feed `CombineLatest` — a role change anywhere propagates automatically.*

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

- [AccessControl](/Doc/Architecture/AccessControl) — `AccessAssignment` node shape and recursive scope walk.
- [AccessContextPropagation](/Doc/Architecture/AccessContextPropagation) — how identity flows across hub boundaries.
