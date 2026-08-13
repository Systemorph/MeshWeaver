---
Name: Activity Operations
Category: Documentation
Description: The canonical IMessageHub extension surface for driving activity state transitions — cancel, restart, and request status changes.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-6.219-8.56"/><path d="m9 11 3 3L22 4"/></svg>
---

# Activity Operations

Every activity state-transition in MeshWeaver — cancel, restart, or any `RequestedStatus` flip — goes through extension methods on `IMessageHub` defined in `src/MeshWeaver.Mesh.Contract/HubActivityExtensions.cs`. Tests, GUI click handlers, MCP agents, and plugins all call these methods. There is no other public entry point.

> This page covers the **client side**: how callers request a transition. For the server side — the watcher that consumes the flip and drives the internal transition — see [Activity Control Plane](/Doc/Architecture/ActivityControlPlane).

<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 760 260" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="20" y="90" width="140" height="56" rx="10" fill="#5c6bc0"/>
  <text x="90" y="114" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Caller</text>
  <text x="90" y="132" font-family="sans-serif" font-size="10" fill="#ddd" text-anchor="middle">hub.CancelActivity()</text>
  <rect x="220" y="90" width="160" height="56" rx="10" fill="#1e88e5"/>
  <text x="300" y="112" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">GetMeshNodeStream</text>
  <text x="300" y="128" font-family="sans-serif" font-size="10" fill="#ddd" text-anchor="middle">.Update(RequestedStatus</text>
  <text x="300" y="141" font-family="sans-serif" font-size="10" fill="#ddd" text-anchor="middle">= Cancelled)</text>
  <rect x="450" y="20" width="150" height="50" rx="10" fill="#8e24aa"/>
  <text x="525" y="41" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Activity Hub</text>
  <text x="525" y="57" font-family="sans-serif" font-size="10" fill="#ddd" text-anchor="middle">WatchControlPlane fires</text>
  <rect x="450" y="100" width="150" height="50" rx="10" fill="#e53935"/>
  <text x="525" y="121" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">CTS.Cancel()</text>
  <text x="525" y="137" font-family="sans-serif" font-size="10" fill="#ddd" text-anchor="middle">running script throws</text>
  <rect x="450" y="180" width="150" height="50" rx="10" fill="#43a047"/>
  <text x="525" y="201" font-family="sans-serif" font-size="12" fill="#fff" text-anchor="middle" font-weight="bold">Status = Cancelled</text>
  <text x="525" y="217" font-family="sans-serif" font-size="10" fill="#ddd" text-anchor="middle">stream ticks → UI updates</text>
  <line x1="160" y1="118" x2="218" y2="118" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="110" x2="448" y2="55" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="525" y1="70" x2="525" y2="98" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="525" y1="150" x2="525" y2="178" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="450" y1="205" x2="402" y2="205" stroke="currentColor" stroke-opacity=".4" stroke-width="1.2" stroke-dasharray="5,3" marker-end="url(#arr)"/>
  <rect x="230" y="178" width="160" height="50" rx="10" fill="none" stroke="#26a69a" stroke-opacity=".7" stroke-width="1.5"/>
  <text x="310" y="199" font-family="sans-serif" font-size="12" fill="#26a69a" text-anchor="middle" font-weight="bold">Observer / UI</text>
  <text x="310" y="215" font-family="sans-serif" font-size="10" fill="currentColor" fill-opacity=".6" text-anchor="middle">GetMeshNodeStream.Subscribe</text>
  <text x="390" y="204" font-family="sans-serif" font-size="9" fill="currentColor" fill-opacity=".5" text-anchor="middle">stream tick</text>
</svg>

*End-to-end activity cancellation: the caller writes `RequestedStatus`, the activity hub reacts, and the terminal state propagates back via the reactive stream.*

---

## Why a dedicated surface?

Before consolidation, every cancel button rolled its own five-line lambda — a different `GetMeshNodeStream(path).Update(...)` call per call site, with roughly half of them missing the no-op guard or the error logger. The `IMessageHub` extensions fix that in three ways:

| Reason | Detail |
|---|---|
| **Single source of truth** | Every cancel, restart, and status flip goes through one implementation. |
| **No verb-shaped messages** | There is no `CancelActivityRequest` or `RestartActivityRequest`. All mutations write `RequestedStatus` to the activity node, and the [activity control plane](/Doc/Architecture/ActivityControlPlane) reacts. |
| **Discoverable** | Type `hub.` and IntelliSense surfaces the full surface. No need to know `HubActivityExtensions` exists. |

---

## The extension surface

```csharp
using MeshWeaver.Mesh;   // HubActivityExtensions
using MeshWeaver.Data;   // ActivityStatus, ActivityLog

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

`CancelActivity` is a thin alias — it calls `RequestActivityStatus(path, ActivityStatus.Cancelled, onError)`. The single implementation guards twice inside the `Update` lambda and **silently returns the node unchanged** in both cases:

- the node's `Content` does not materialise as a typed `ActivityLog`, or
- `RequestedStatus` already equals the requested value (the request is in flight).

A silent no-op is the correct outcome for a duplicate request, but it also means a cancel against a node whose content is not an `ActivityLog` does nothing and reports nothing — `onError` fires only when the `Update` itself faults, never for either guard. If a cancel appears to be ignored, check the node's content type first.

`hub` can be any `IMessageHub`: a click context's `ctx.Host.Hub`, a test fixture's `Mesh`, an MCP plugin's captured hub, or even the activity hub itself patching its own status from within a worker. The extension routes the write through `hub.GetWorkspace().GetMeshNodeStream(activityPath).Update(...)`, which auto-dispatches based on who is calling:

- **Writer is the activity hub itself** — write goes through its local data source.
- **Writer is anywhere else** — write routes as an RFC 7396 JSON-merge patch via the process-wide `IMeshNodeStreamCache`. The activity hub's single-threaded action block serialises every mirror's write, so there are no races.

---

## Observing the result

The mutation methods are fire-and-forget. To observe the outcome, subscribe to the activity node's stream — the same shared handle the running-activities UI strip already binds to.

> **The flow is 100% reactive end-to-end.** No `FirstAsync().ToTask(ct)`, no `await`, no `Task<T>` boundary. The UI re-renders when the stream ticks; a worker waiting for a terminal state chains via `SelectMany`. See [AsynchronousCalls](/Doc/Architecture/AsynchronousCalls) → "Why `await` Deadlocks in Hub Handlers".

```csharp
var sub = workspace.GetMeshNodeStream(activityPath)
    .Select(node => node?.Content as ActivityLog)
    .Where(log => log is { } l && l.Status != ActivityStatus.Running)
    .Take(1)
    .Subscribe(
        terminal => Logger.LogInformation(
            "Activity {Path} settled to {Status}", activityPath, terminal!.Status),
        ex => Logger.LogWarning(ex, "Activity stream errored for {Path}", activityPath));

// The caller owns `sub` and disposes it when the wait is no longer relevant
// (component dispose, parent scope dispose, etc.).
```

Tests bridge to `Task` exactly once at the assertion edge — see [WritingTests](/Doc/Architecture/WritingTests). Application code stays observable throughout.

---

## What the activity hub does in response

When `RequestedStatus` flips, the activity hub's `WatchControlPlane` subscription fires on the value change (it projects `RequestedStatus` off the hub's OWN node stream through `DistinctUntilChanged`, so the callback sees changes, not every emission). The subscription is installed from a `MessageHubConfiguration.WithInitialization(...)` callback in the activity NodeType's `HubConfiguration`:

```csharp
var subscription = hub.WatchControlPlane(requested =>
{
    // requested is ActivityStatus? — null means "no pending request"
    // (never set, or cleared after a transition).
    if (requested == ActivityStatus.Cancelled)
    {
        cts.Cancel();   // trips the stored CancellationToken
    }
});
hub.RegisterForDisposal(subscription);   // the watcher's lifetime IS the hub's
```

Two things the signature makes non-optional:

- **The callback parameter is `ActivityStatus?`.** `null` is a real value — it means no request is pending — so never treat the callback as "a transition was requested".
- **`WatchControlPlane` returns an `IDisposable` you must register with the hub's lifetime** (`hub.RegisterForDisposal(...)`). Drop it and the subscription outlives the hub.

The handler runs on whatever scheduler the upstream stream emits on — in practice the hub's own action block. Treat it as hub-reachable code: no `await`, compose follow-up work as `IObservable` chains.

The subscription is **self-healing but not infinitely retrying**. It is established through `SubscribeWithReEstablish`, which re-establishes after ~1 s on a transient fault but stops permanently on two classes: own-node content that cannot be deserialized (re-subscribing would replay the same poisoned emission at 1 Hz), and a routing `NotFound` on its own node (the node is gone — re-subscribing is the resubscribe storm that took prod down on 2026-06-10). Both terminal cases are logged loudly rather than retried.

The running script receives the cancellation, throws `OperationCanceledException`, and the executor's normal terminal path writes `Status = Cancelled` back to the activity's MeshNode. The same stream the cancel button is bound to ticks one final time with the terminal state, and the UI re-renders — the cancel button disappears without any additional coordination.

---

## `WatchControlPlane` — server side only

`ActivityControlPlaneExtensions.WatchControlPlane` is the **server-side** helper that an activity hub uses to install its own subscription inside `WithInitialization`. Application code never calls it directly.

| You are writing… | Use… |
|---|---|
| A click action, test, or plugin | `hub.CancelActivity(...)` / `hub.RequestActivityStatus(...)` (this page) |
| A new NodeType's `HubConfiguration` | `WatchControlPlane` inside the `WithInitialization` callback |

---

## Writing log messages: `ActivityLogAppender`

Every log line written onto a **persisted activity node** goes through `ActivityLogAppender.Append` (`src/MeshWeaver.Mesh.Contract/ActivityLogAppender.cs`). It is the append-side twin of the transition surface above: callers hand it messages plus an optional change to the log (terminal status, `End`, `ReturnValue`) and it performs **one** `stream.Update` carrying both — so a reader can never observe the terminal status before the lines that explain it.

```csharp
ActivityLogAppender.Append(hub, activityPath, [new LogMessage(text, LogLevel.Information)])
    .Subscribe(_ => { }, ex => logger.LogDebug(ex, "activity append failed"));

// terminal status + its explanation, atomically:
ActivityLogAppender.Append(hub, activityPath, [new LogMessage(error, LogLevel.Error)],
        log => log with { Status = ActivityStatus.Failed, End = DateTime.UtcNow })
    .Subscribe(_ => { }, ex => logger.LogDebug(ex, "activity complete failed"));
```

### `Messages` is a bounded window

`ActivityLog.Messages` holds at most `ActivityLog.MessageWindowLimit` (500) entries. Older lines are sealed into `ActivityLogSegment` satellites at `{activityPath}/_Log/{index:D6}` and drop off the head, leaving `MessageWindowKeep` (100) behind.

**Why.** Every `stream.Update` re-serialises the whole `MeshNode.Content` to compute its patch, so appending N lines to one growing list costs **O(N²)** — measured at ~719 MB of serialisation for a single memex-cloud import activity (5,239 writes over a 141 kB node), and the dominant term in that pod's CFS throttling. A delta field does not escape it: the cross-hub path ships an RFC 7396 merge patch, which clones a changed array whole, and the three-way merge's base extraction clones the previous array too — so one append to an N-element collection ships ~2N elements. Bounding the head is the only lever that changes the asymptotics; with the window fixed, each write is O(1) and the activity is O(N).

**Below the window nothing changes.** An activity that never reaches 500 messages takes exactly the single write per append it always did, with byte-identical content. Only long activities — the ones that actually hurt — take the new path.

### Consequences for readers

| You want… | Read… |
|---|---|
| How many lines the activity produced | `log.TotalMessageCount` — **never** `log.Messages.Count` |
| Whether it errored / its terminal status | `log.HasErrors()`, `log.Finish(...)` — both answer from the `MaxSeverity` counter |
| The latest line, or the last few | `log.Messages` — the window keeps the **most recent** entries, so `[^1]` and `TakeLast(n)` are unaffected |
| The full transcript | the window plus the `_Log` segments, ordered by `ActivityLogSegment.FirstOrdinal` |

🚨 **Enumerate segments with a children query on `{activityPath}/_Log`, never a point-read of a segment path.** A point-read of an absent satellite opens the shared stream cache's storm breaker on a path a concurrent write is about to use, and the breaker fast-fails writes too.

🚨 **Never derive progress from `Messages.Count`.** It stops growing once an activity passes the window, so anything keyed on it — a `DistinctUntilChanged`, a change detector — silently freezes for exactly the long-running activities it exists to follow. `TotalMessageCount` is the monotonic signal.

### How the flush stays safe without a lock

A seal is **claimed inside the head's update lambda** (`ActivityLog.ClaimSeal`), which the owning hub serialises — so exactly one appender claims each slice and no two claims overlap. The claimed messages **stay on the head** until the segment write succeeds; only then are they trimmed (`ActivityLog.CompleteSeal`). A crash or a failed segment write therefore loses nothing and needs no watchdog: the claim is still standing and the messages are still there, so the next append retries the identical slice against the same (deterministic) segment index.

`ActivityLogLogger` (the kernel's script logger) is the one writer that does **not** use the appender: it re-asserts whole content on every 100 ms flush rather than patching, so it is the single writer of its node and seals its own overflow directly under the lock it already holds for the terminal settle. Same window, same segment shape, no claim protocol needed because there is no second appender to race.

---

## See also

- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — the `Status` / `RequestedStatus` pattern and how to wire your own NodeType to it
- [Thread Operations](/Doc/Architecture/ThreadOperations) — the matching `IMessageHub` surface for thread mutations (same shape)
- [RequestViaStreamUpdate](/Doc/Architecture/RequestViaStreamUpdate) — the underlying `stream.Update` mechanism every method here is built on
- [Owner Injection](/Doc/Architecture/OwnerInjection) — the activity **owner** is the standing access context on the activity hub, injected everywhere and carried forward via `CircuitContext`
