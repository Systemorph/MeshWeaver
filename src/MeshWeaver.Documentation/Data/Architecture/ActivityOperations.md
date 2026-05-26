---
Name: Activity Operations
Category: Documentation
Description: The canonical IMessageHub extension surface for driving activities — cancel, restart, request status transitions.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-6.219-8.56"/><path d="m9 11 3 3L22 4"/></svg>
---

# Activity Operations — the canonical `IMessageHub` surface

Every activity state-transition request in MeshWeaver — cancel, restart, request "now I want it Running" — goes through extension methods on `IMessageHub` defined in `src/MeshWeaver.Mesh.Contract/HubActivityExtensions.cs`. **Tests, GUI click handlers, MCP agents, and plugins all call these.** There is no other public entry point.

This page is the client-side counterpart to [Activity Control Plane](ActivityControlPlane), which covers the server side (the watcher that consumes the requested-status flip and runs the internal transition).

## Why

Same three reasons as the thread surface:

1. **Single source of truth.** Before the consolidation, every cancel button rolled its own five-line `GetMeshNodeStream(path).Update(curr => curr.Content is ActivityLog log ? curr with { Content = log with { RequestedStatus = ActivityStatus.Cancelled } } : curr).Subscribe(...)` — a different lambda per call site, half of them missing the no-op guard or the error logger. The IMessageHub extensions are the *only* shape; tests and the UI go through the same method.

2. **No verb-shaped messages.** All thread mutations write the activity node via `hub.GetWorkspace().GetMeshNodeStream(activityPath).Update(...)`. No `CancelActivityRequest`, no `RestartActivityRequest` — only `RequestedStatus` patches that the [activity control plane](ActivityControlPlane) translates into the internal transition.

3. **Discoverable.** `hub.` + IntelliSense lists the surface. No need to know `HubActivityExtensions` or `ActivityControlPlaneExtensions` exist.

## The surface

```csharp
using MeshWeaver.Mesh;

// 1. Cancel a running activity. Patches RequestedStatus = Cancelled. The
//    activity hub's WatchControlPlane handler reacts: trips the stored CTS,
//    transitions Status from Running to Cancelled.
hub.CancelActivity(activityPath);

// 2. Generic status flip — use for restart (Running), or any other transition
//    the activity hub's WatchControlPlane handler is set up to honour.
hub.RequestActivityStatus(activityPath, ActivityStatus.Running);
hub.RequestActivityStatus(activityPath, ActivityStatus.Cancelled);

// Both accept an optional onError callback for one-shot signalling:
hub.CancelActivity(activityPath, onError: msg => ShowToast(msg));
```

`hub` is any `IMessageHub` — the click context's `ctx.Host.Hub`, a test fixture's `Mesh`, an MCP plugin's captured hub, the activity hub itself patching its own status from a worker. The extension routes the write through `hub.GetWorkspace().GetMeshNodeStream(activityPath).Update(...)`, which auto-dispatches:

- If the writer is the activity hub itself, the write goes through its local data source.
- If the writer is anywhere else, the write routes as an RFC-7396 JSON-merge patch via the process-wide `IMeshNodeStreamCache`. The activity hub's single-threaded action block serialises every mirror's write — no races.

## Observing the result

The mutation methods are `void` / fire-and-forget. Callers observe state by **subscribing** to the activity node's remote stream — the same stream the running-activities UI strip already binds to. **The flow is 100% reactive end-to-end**: no `FirstAsync().ToTask(ct)`, no `await`, no `Task<T>` boundary. The UI re-renders when the stream ticks; a worker waiting for a terminal state chains via `SelectMany`. See [AsynchronousCalls](AsynchronousCalls) → "Why `await` Deadlocks in Hub Handlers".

```csharp
var sub = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(activityPath), new MeshNodeReference())
    .Select(c => c.Value?.Content as ActivityLog)
    .Where(log => log is { } l && l.Status != ActivityStatus.Running)
    .Take(1)
    .Subscribe(
        terminal => Logger.LogInformation(
            "Activity {Path} settled to {Status}", activityPath, terminal!.Status),
        ex => Logger.LogWarning(ex, "Activity stream errored for {Path}", activityPath));
// Caller owns `sub` and disposes when the wait is no longer relevant
// (component dispose, parent scope dispose, etc.).
```

Tests bridge to `Task` exactly once at the assertion edge — see [WritingTests](WritingTests). Application code stays observable.

## What the activity hub does

When `RequestedStatus` flips, the activity hub's `WatchControlPlane` subscription (registered via `MessageHubConfiguration.WithInitialization(...)` in the activity NodeType's `HubConfiguration`) fires on the value change. The handler runs on the hub's own action block:

```csharp
threadHub.WatchControlPlane(requested =>
{
    if (requested == ActivityStatus.Cancelled)
    {
        cts.Cancel();   // trips the stored CancellationToken
    }
});
```

The script throws `OperationCanceledException`, completion flows through the executor's normal terminal path, and the hub writes `Status = Cancelled` back to its own MeshNode. The same stream the cancel button is bound to ticks one more time with the terminal state; the UI re-renders without the cancel button.

See [Activity Control Plane](ActivityControlPlane) for the full server-side pattern, including how to wire your own NodeType to honour `RequestedStatus` transitions.

## What about `ActivityControlPlaneExtensions.WatchControlPlane`?

That's the **server-side** helper that the activity hub uses to install its own subscription. Application code never calls it — it lives in the `WithInitialization` callback of the activity NodeType's `HubConfiguration` (or any custom NodeType that follows the control-plane pattern).

If you're writing application code — a click action, a test, a plugin — use the `IMessageHub` extensions on this page. If you're writing a *new* NodeType's HubConfiguration, use `WatchControlPlane` to subscribe inside the hub.

## See also

- [Activity Control Plane](ActivityControlPlane) — the canonical Status / RequestedStatus pattern + how to wire your own NodeType to it
- [Thread Operations](ThreadOperations) — the matching `IMessageHub` surface for thread mutations (same shape)
- [RequestViaStreamUpdate](RequestViaStreamUpdate) — the underlying `stream.Update` mechanism every method here is built on
