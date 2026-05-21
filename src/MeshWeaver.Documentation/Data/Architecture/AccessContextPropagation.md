# AccessContext propagation

How user identity (`AccessContext`) flows from a Blazor circuit / API token / Orleans grain through the message bus, through reactive Subscribe chains, into AccessControl decisions on every write.

## Why this exists

Prod incident 2026-05-21: a dynamic NodeType (`Systemorph/EventCalendar`) auto-recompiled on every per-NodeType-grain activation. The compile activity's `CreateNode` was denied with `"Access denied: user 'sync/...' lacks Create permission"`. App Insights also showed `"Orleans: delivering RawJson to Systemorph/EventCalendar, accessContext=(null)"`.

**Root cause.** Three breaks chained together:

1. The compile-watcher kickoff fired on every grain activation, so background traffic (synced-query fan-out, NodeType enrichment, grain reactivation) auto-triggered a recompile that no user asked for.
2. The recompile's `meshService.CreateNode` ran from inside a Subscribe callback. The Subscribe callback executed on a thread where `AccessService.Context` (AsyncLocal) had been wiped — Rx cold pipelines don't promise ExecutionContext flow across their schedulers.
3. The PostPipeline's "stamp hub-self as principal when no context" fallback silently masked (2). When the activity hub posted `CreateNodeRequest` with no AsyncLocal, the pipeline stamped `activity/{guid}` as principal — that address matched no `AccessAssignment` → AccessControl denied.

User directive (verbatim, 2026-05-21 session): "we must always have access context set ⇒ access context must be transferred to activity and then be propagated there. we should NEVER write something as hub. we must always restore. can we do as cross cutting concern?"

## The model — "MessageHub sets, framework primitive preserves"

Two-step responsibility split. No per-callsite discipline; no `.PreserveAccessContext(...)` litter at the call site.

### Step 1 — MessageHub sets AsyncLocal on every handler invocation

Already in place; no change needed.

- `MessageHubGrain.DeliverMessage` (`src/MeshWeaver.Connection.Orleans/MessageHubGrain.cs:258-285`) reads Orleans `RequestContext.UserId/UserName` on each cross-grain delivery and restores `delivery.AccessContext`.
- `MessageService`'s delivery pipeline + `UserServicePostPipeline` (`src/MeshWeaver.Messaging.Hub/MessageHubConfiguration.cs:266-355`) read `delivery.AccessContext` and call `accessService.SetContext(...)` before the handler body runs.

The handler's thread therefore has the right AsyncLocal value during its synchronous body.

### Step 2 — framework primitives preserve that AsyncLocal across the `Subscribe()` boundary

When a handler runs:

```csharp
stream.Update(fn).Subscribe(_ => meshService.CreateNode(...));
```

three thread hops can occur:

1. `Update` returns a cold `IObservable<T>` synchronously on the handler's thread.
2. The handler calls `.Subscribe(...)`. Subscribe binds the subscriber to the cold pipeline; nothing has emitted yet.
3. Later (often on the workspace's emission scheduler, NOT the handler's thread), the upstream emits → Subscribe's callback runs.

Today AsyncLocal would be wiped between (2) and (3). The framework primitives fix this by capturing `AccessService.Context` at the moment they're invoked (step 1, on the handler's thread where AsyncLocal is correct) and re-stamping it on every emission of the returned cold pipeline.

The mechanism is one extension method: `IObservable<T>.CarryAccessContext(IServiceProvider)` (`src/MeshWeaver.Messaging.Hub/AccessContextCaptureExtensions.cs`). It captures into a closure, then wraps the source with `.Do(_ => accessService.SetContext(captured))`. Restore happens on whatever thread the emission lands on, just before the subscriber observes the value.

Applied INSIDE the framework primitives — not at callsites — so every caller gets the behaviour for free:

- `IMeshService.CreateNode / UpdateNode / DeleteNode / CopyNode` (`src/MeshWeaver.Hosting/MeshService.cs`) — wraps the return observable.
- `MeshNodeStreamHandle.Update` (`src/MeshWeaver.Mesh.Contract/MeshNodeStreamExtensions.cs`) — wraps the return observable.
- `IMeshNodeStreamCache.Update` (`src/MeshWeaver.Hosting/MeshNodeStreamCache.cs`) — delegates to `MeshNodeStreamHandle.Update`, inherits the wrap.

The cross-cutting hook covers **every** mesh-side write. Callers keep writing the natural shape and the framework guarantees the operation runs under the caller's identity.

```csharp
// Caller code — unchanged shape, no manual wrap needed:
meshService.CreateNode(node).Subscribe(_ => …);
streamCache.Update(path, fn).Subscribe(_ => …);
```

## `IMeshNodeStreamCache.GetStream` is access-checked

In addition to "writes preserve user identity", reads through the process-wide `IMeshNodeStreamCache` are gated by the caller's effective Read permission on the path. The cache asks the owning node hub via `GetPermissionRequest` (`src/MeshWeaver.Mesh.Contract/Security/GetPermissionRequest.cs`); the response is a `Permission` flags bag. Only when `Permissions.HasFlag(Permission.Read)` does the gated observable forward the upstream emissions; otherwise it terminates with `UnauthorizedAccessException`.

Per-`(path, userId)` validations are cached in-process for 30s (the `AccessTtl` constant in `MeshNodeStreamCache.cs`). Revocation surfaces within at most that window; we do not invalidate the cache reactively. The cached SHARED upstream is unchanged — only the returned subscriber-side observable is gated.

Why authoritative validation lives on the node hub: the hub already runs the validator chain when it answers `GetPermissionRequest` (see `AccessControlPipeline.HandleGetPermission`). Routing the check through that handler keeps the gate aligned with every other access decision in the system.

## What changed in PostPipeline

Before 2026-05-21, `UserServicePostPipeline` (`MessageHubConfiguration.cs:266-355`) had a two-tier fallback when no ambient `AccessService` context was set:

- For mesh hubs → fail closed (warn + null `AccessContext`).
- For every other hub kind (`sync/`, `portal/`, `apitoken/`, `activity/`, per-node hubs) → silently stamp the hub's own address as principal.

The fallback silently masked the prod bug. After the cleanup, ALL hub kinds fail closed by default. Framework-lifecycle messages (`InitializeHubRequest`, `HeartBeatEvent`, `ShutdownRequest`, `DisposeRequest`, `SubscribeRequest`) are still exempt from the warning since they carry no security-relevant payload.

Legitimate hub-internal writes MUST now opt in explicitly:

- **System bootstrap** (cache hydration, schema migration): wrap with `accessService.ImpersonateAsSystem()` — well-known `"system-security"` identity, granted `Permission.All` by `SecurityService` unconditionally.
- **SyncStream lifecycle** (heartbeat, `SetCurrentRequest`): wrap with `accessService.ImpersonateAsHub(hub)` or use `PostOptions.ImpersonateAsHub` at the post site. Stamps the hub's address as principal.

See `src/MeshWeaver.Data/Serialization/JsonSynchronizationStream.cs:218,292,327` for the canonical SyncStream pattern.

## `GetPermissionRequest` contract

```csharp
public record GetPermissionRequest : IRequest<GetPermissionResponse>;
public record GetPermissionResponse(Permission Permissions);
```

The request carries no path — the receiving hub answers for ITS OWN path. Callers route the request to the per-node hub at the path they care about; routing decides which hub responds. The handler (`AccessControlPipeline.HandleGetPermission`) resolves the per-hub-scoped `ISecurityService` and evaluates against the caller's `delivery.AccessContext`.

## Mental model

> *"If you call a framework write primitive, the operation runs as you.*  
> *If your code starts a background operation that needs to span users, use explicit `ImpersonateAsSystem` / `ImpersonateAsHub`."*

The first rule covers ~99% of code. The second rule is the security smell: every callsite of `ImpersonateAsSystem` / `ImpersonateAsHub` is a deliberate choice to bypass the user's identity. Grep for them; review each one.

## Anti-patterns

| Anti-pattern | Why it's wrong | Fix |
|---|---|---|
| Per-callsite `.PreserveAccessContext(...)` / `.Do(_ => SetContext(...))` litter | The framework primitives already do this. Adding it at the callsite is redundant and drifts. | Remove. |
| `meshService.CreateNode(node, ...)` followed by `o.WithAccessContext(captured)` | Implicit via the framework wrap. | Remove the explicit `WithAccessContext` (unless you have a non-AsyncLocal source for the context). |
| `accessService.ImpersonateAsHub(...)` in application code | The "writing as hub" anti-pattern the user explicitly rejected — application writes should ride the user's identity. | Either propagate the user's identity correctly (default path) or use `ImpersonateAsSystem` if it's truly infrastructure. |
| Catching `UnauthorizedAccessException` from a write and falling through silently | Hides denials; the prod EventCalendar bug. | Surface the denial. Empty-state the UI, navigate to AccessDenied, or rethrow. |
| Reading `MeshNode.Content` from a query row (stale) | Reads are not gated by the user's permission at the row level once the query has returned. | Use `workspace.GetMeshNodeStream(path)` or `IMeshNodeStreamCache.GetStream(path)` for content. |

## Worked example — the compile-activity chain

Before 2026-05-21 the chain auto-fired on activation and lost the user's identity at every Subscribe boundary. After:

1. User clicks **Compile** in the Overview's `BuildCompileStatusPanel` (`src/MeshWeaver.Graph/NodeTypeLayoutAreas.cs:1416`).
2. The button's `WithClickAction` runs `streamCache.Update(typePath, ...)` to flip `RequestedReleaseAt` on the NodeType's own MeshNode. The Update returns a cold `IObservable<MeshNode>` already wrapped with `CarryAccessContext` — the user's `AccessContext` is captured into the closure on the click thread.
3. The Subscribe callback fires when the partition write lands. AsyncLocal restored to the user → no-op for the click (it's a single-shot).
4. The per-NodeType hub's `InstallReleaseRequestWatcher` (`NodeTypeCompilationHelpers.cs:531-613`) observes `RequestedReleaseAt` change and promotes it to `CompilationStatus = Pending`. Its Subscribe ran under the user's AccessContext (preserved by the wrap on the own-stream).
5. The main compile watcher (`InstallCompileWatcher`'s `watcherSub`) observes `Pending` and calls `NodeTypeCompilationActivity.Start(hub, hubPath, logger)`. `Start` builds an Activity MeshNode and calls `meshService.CreateNode(node)`. The CreateNode runs through PostPipeline, which reads the (now restored) user's AsyncLocal, stamps it on the outbound `CreateNodeRequest`.
6. The activity hub (newly created at `{typePath}/_Activity/compile-{id}`) receives `RunCompileRequest` with the user's identity on `delivery.AccessContext`. Roslyn runs; the result is written back to the NodeType's MeshNode (status, latest release path, etc.) — all writes attributed to the user.

Result: every row touched by the compile cycle carries `CreatedBy = <user>` / `LastModifiedBy = <user>`. AccessControl validated Edit-on-NodeType once (at the user's click); every downstream write inherits the same identity.

## Related docs

- `Doc/Architecture/AsynchronousCalls.md` — reactive end-to-end patterns; AccessContext rides for free through framework primitives.
- `Doc/Architecture/CqrsAndContentAccess.md` — `GetStream` is access-checked; details the TTL cache + `GetPermissionRequest` handshake.
- `Doc/GUI/DataBinding.md` — Blazor views can receive `OnError(UnauthorizedAccessException)` from `IMeshNodeStreamCache.GetStream` if the user's access is revoked.
- `Doc/Architecture/ActivityControlPlane.md` — operations as scripts: `RequestedX` triggers, status-machine semantics; cross-references this doc for the security model.
