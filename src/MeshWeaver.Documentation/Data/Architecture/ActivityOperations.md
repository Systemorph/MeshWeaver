---
Name: Activity Operations
Category: Documentation
Description: The canonical IMessageHub extension surface for driving activity state transitions — cancel, restart, and request status changes.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-6.219-8.56"/><path d="m9 11 3 3L22 4"/></svg>
---

# Activity Operations

Every activity state-transition in MeshWeaver — cancel, restart, or any `RequestedStatus` flip — goes through extension methods on `IMessageHub` defined in `src/MeshWeaver.Mesh.Contract/HubActivityExtensions.cs`. Tests, GUI click handlers, MCP agents, and plugins all call these methods. There is no other public entry point.

> This page covers the **client side**: how callers request a transition. For the server side — the watcher that consumes the flip and drives the internal transition — see [Activity Control Plane](ActivityControlPlane).

---

## Why a dedicated surface?

Before consolidation, every cancel button rolled its own five-line lambda — a different `GetMeshNodeStream(path).Update(...)` call per call site, with roughly half of them missing the no-op guard or the error logger. The `IMessageHub` extensions fix that in three ways:

| Reason | Detail |
|---|---|
| **Single source of truth** | Every cancel, restart, and status flip goes through one implementation. |
| **No verb-shaped messages** | There is no `CancelActivityRequest` or `RestartActivityRequest`. All mutations write `RequestedStatus` to the activity node, and the [activity control plane](ActivityControlPlane) reacts. |
| **Discoverable** | Type `hub.` and IntelliSense surfaces the full surface. No need to know `HubActivityExtensions` exists. |

---

## The extension surface

```csharp
using MeshWeaver.Mesh;

// Cancel a running activity.
// Patches RequestedStatus = Cancelled. The activity hub's WatchControlPlane
// handler trips the stored CTS and transitions Status → Cancelled.
hub.CancelActivity(activityPath);

// Generic status flip — use for restart (Running) or any other transition
// the activity hub's WatchControlPlane handler is wired to honour.
hub.RequestActivityStatus(activityPath, ActivityStatus.Running);
hub.RequestActivityStatus(activityPath, ActivityStatus.Cancelled);

// Both accept an optional onError callback for one-shot error signalling:
hub.CancelActivity(activityPath, onError: msg => ShowToast(msg));
```

`hub` can be any `IMessageHub`: a click context's `ctx.Host.Hub`, a test fixture's `Mesh`, an MCP plugin's captured hub, or even the activity hub itself patching its own status from within a worker. The extension routes the write through `hub.GetWorkspace().GetMeshNodeStream(activityPath).Update(...)`, which auto-dispatches based on who is calling:

- **Writer is the activity hub itself** — write goes through its local data source.
- **Writer is anywhere else** — write routes as an RFC 7396 JSON-merge patch via the process-wide `IMeshNodeStreamCache`. The activity hub's single-threaded action block serialises every mirror's write, so there are no races.

---

## Observing the result

The mutation methods are fire-and-forget. To observe the outcome, subscribe to the activity node's remote stream — the same stream the running-activities UI strip already binds to.

> **The flow is 100% reactive end-to-end.** No `FirstAsync().ToTask(ct)`, no `await`, no `Task<T>` boundary. The UI re-renders when the stream ticks; a worker waiting for a terminal state chains via `SelectMany`. See [AsynchronousCalls](AsynchronousCalls) → "Why `await` Deadlocks in Hub Handlers".

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

// The caller owns `sub` and disposes it when the wait is no longer relevant
// (component dispose, parent scope dispose, etc.).
```

Tests bridge to `Task` exactly once at the assertion edge — see [WritingTests](WritingTests). Application code stays observable throughout.

---

## What the activity hub does in response

When `RequestedStatus` flips, the activity hub's `WatchControlPlane` subscription fires on the value change. This subscription is registered via `MessageHubConfiguration.WithInitialization(...)` in the activity NodeType's `HubConfiguration`, and it runs on the hub's own action block:

```csharp
threadHub.WatchControlPlane(requested =>
{
    if (requested == ActivityStatus.Cancelled)
    {
        cts.Cancel();   // trips the stored CancellationToken
    }
});
```

The running script receives the cancellation, throws `OperationCanceledException`, and the executor's normal terminal path writes `Status = Cancelled` back to the activity's MeshNode. The same stream the cancel button is bound to ticks one final time with the terminal state, and the UI re-renders — the cancel button disappears without any additional coordination.

---

## `WatchControlPlane` — server side only

`ActivityControlPlaneExtensions.WatchControlPlane` is the **server-side** helper that an activity hub uses to install its own subscription inside `WithInitialization`. Application code never calls it directly.

| You are writing… | Use… |
|---|---|
| A click action, test, or plugin | `hub.CancelActivity(...)` / `hub.RequestActivityStatus(...)` (this page) |
| A new NodeType's `HubConfiguration` | `WatchControlPlane` inside the `WithInitialization` callback |

---

## See also

- [Activity Control Plane](ActivityControlPlane) — the `Status` / `RequestedStatus` pattern and how to wire your own NodeType to it
- [Thread Operations](ThreadOperations) — the matching `IMessageHub` surface for thread mutations (same shape)
- [RequestViaStreamUpdate](RequestViaStreamUpdate) — the underlying `stream.Update` mechanism every method here is built on
