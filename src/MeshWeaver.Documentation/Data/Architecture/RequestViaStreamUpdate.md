# Requesting Work via `stream.Update()`

> **Pattern**: Instead of posting a request/response message, mutate the target node's
> state via `workspace.UpdateMeshNode(...)` and let a server-side watcher pick up the
> change and dispatch the work. The result is published by writing back to the same
> node — propagated cluster-wide automatically by the synchronization protocol.

## Use the canonical helpers

Don't roll your own watcher. Two helpers in `MeshWeaver.Mesh.Contract`
(`ActivityControlPlaneExtensions.cs`) implement this pattern as one-liners:

- **`hub.WatchControlPlane(onRequestedStatus, logger)`** — when the trigger is the
  single `ActivityLog.RequestedStatus` field. Drives Cancel / Start / Retry off a
  property patch.
- **`hub.WatchSubmission(fingerprint, needsDispatch, dispatch, logger)`** — when the
  trigger is an arbitrary "this state needs work now" predicate (e.g. a thread has
  unprocessed user messages and isn't already executing). Pure
  `GetMeshNodeStream → DistinctUntilChanged(fingerprint) → Where(needsDispatch) →
  SelectMany(dispatch)` composition.

The canonical reference for both is
[ActivityControlPlane.md](ActivityControlPlane) (see § "Generalising:
`WatchSubmission`" and § "Anti-patterns to remove on sight"). Everything below is
how those helpers are *used* for the request-via-stream-update pattern; if you find
yourself reaching for `Throttle(...)`, `Interlocked.CompareExchange`, or a manual
`Subject` + flag, that's the signal you should be calling one of the helpers above
instead.

## When to use this pattern

Use it when:

- The work needs to be **triggered** by a state change (caller mutates input fields).
- The work's **result** belongs on the same node (caller observes output fields on the same MeshNode it mutated).
- You want **automatic cluster-wide propagation** of the result (every silo that subscribes via `SyncedMeshNodeQuery` or per-node remote stream sees the update).
- You want **no cross-silo request/response round-trip during grain activation** (the watcher runs in-grain on whichever silo owns the node).

Do NOT use it for:

- One-shot transient queries that don't belong on the node (use `hub.Observe(request)` instead).
- Cases where the caller needs an *immediate* synchronous response (the watcher dispatches reactively off the node's stream — the round still has multi-step latency vs. a direct `hub.Observe` round-trip).

## The canonical example: thread execution request

`MeshWeaver.AI` already implements this pattern for thread execution dispatch.

### Caller (request side)

```csharp
// MeshWeaver.AI/ThreadInput.cs — AppendUserInput
workspace.UpdateMeshNode(node =>
{
    var thread = node.Content as MeshThread ?? new MeshThread();
    return node with
    {
        Content = thread with
        {
            UserMessageIds = thread.UserMessageIds.Add(msgId),
            PendingUserMessages = thread.PendingUserMessages.SetItem(msgId, message),
            // ... pending fields ...
        }
    };
});
```

The caller writes the request *into* the thread node's state — `PendingUserMessages` is
the request payload. No `IRequest<TResponse>` posted. No callback registered. The work
is requested by the very act of mutating the node.

### Server (dispatch side)

```csharp
// MeshWeaver.AI/ThreadSubmission.cs — ThreadSubmissionServer.InstallServerWatcher
return threadHub.WatchSubmission(
    fingerprint:   Fingerprint,     // (IsExecuting, Messages.Count, IngestedMessageIds.Count, PendingUserMessages.Count)
    needsDispatch: NeedsDispatch,   // !IsExecuting && (PendingUserMessages.Count > 0 || any UserMessageId not in IngestedMessageIds)
    dispatch:      node => DispatchRoundObs(threadHub, node, logger),
    logger:        logger);
```

The watcher subscribes once at hub init. `DistinctUntilChanged` on the fingerprint
guarantees the same actionable state cannot fire twice — there is no `dispatching`
flag, no `Throttle`, no reentrancy guard. `DispatchRoundObs` is an
`IObservable<Unit>` that creates satellite cells, commits the round to the thread
node, and posts to the `_Exec` hub; it composes via `SelectMany` into one round per
emission. Failures in `dispatch` are logged and swallowed — the next state change
retries naturally. See `ThreadSubmissionServer.InstallServerWatcher` in
`MeshWeaver.AI/ThreadSubmission.cs` for the full live example.

### Result publication

The dispatched work writes its progress + final result back onto the same node (or a
satellite node owned by it) via `workspace.UpdateMeshNode(...)` — fire-and-forget. The
result reaches every subscriber automatically:

- Local: the same hub's other subscribers see the next emission of the
  `MeshNodeReference` reducer.
- Cross-silo / clients: `SyncedMeshNodeQuery` subscribers and `GetRemoteStream<MeshNode,
  MeshNodeReference>` subscribers see the change via the synchronization protocol —
  no explicit broadcast needed.

## Applied: dynamic NodeType compilation

`INodeTypeService.EnrichWithNodeType`'s slow path now uses this pattern.
`GetCompilationPathRequest` is retained as a fallback (for hubs without a workspace /
remote-stream reducer); the primary path is stream-based.

### Caller — `NodeTypeService.ResolveViaStream`

> **Note**: The current production code in `NodeTypeService.cs` still uses an
> `Interlocked.CompareExchange(ref triggered, 1, 0)` flag to guard the one-shot
> `Pending` trigger. That is the imperative anti-pattern — it is being migrated
> to the shape below. The example shows the **target** shape: split the chain
> into a one-shot trigger pipeline and a terminal-status observation pipeline,
> both reactive.

```csharp
// Subscribe to the per-NodeType remote stream. The trigger pipeline takes the
// first emission whose CompilationStatus is null/Unknown and writes a single
// stream.Update flipping it to Pending — `.Take(1)` makes the trigger
// inherently one-shot; no `triggered` flag, no CompareExchange. The
// observation pipeline waits for the terminal status (Ok / Error) on the
// same stream.
var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(nodeType), new MeshNodeReference());

var trigger = stream
    .Select(change => change?.Value)
    .Where(node => node?.Content is NodeTypeDefinition def
        && (def.CompilationStatus is null || def.CompilationStatus == CompilationStatus.Unknown))
    .Take(1)
    .Do(_ => stream.Update(current =>
        current?.Content is NodeTypeDefinition d
            && (d.CompilationStatus is null || d.CompilationStatus == CompilationStatus.Unknown)
            ? new ChangeItem<MeshNode>(
                Value: current with { Content = d with { CompilationStatus = CompilationStatus.Pending } },
                ChangedBy: WellKnownUsers.System,
                StreamId: stream.StreamId,
                ChangeType: ChangeType.Full,
                Version: stream.Hub.Version,
                Updates: null)
            : null))
    .IgnoreElements()
    .Select(_ => default(MeshNode)!);

var terminal = stream
    .Select(change => change?.Value)
    .Where(node => node?.Content is NodeTypeDefinition def
        && (def.CompilationStatus == CompilationStatus.Ok
            || def.CompilationStatus == CompilationStatus.Error))
    .Take(1);

return trigger.Merge(terminal)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(30));
```

### Server — `MeshDataSourceExtensions.InstallCompileWatcher`

> **Note**: The previous `InstallCompileWatcher` (`Throttle(50ms)` +
> `SelectMany` + manual `Pending → Compiling` flip to dodge throttle's
> trailing-edge re-emission) was removed from `MeshDataSource.cs`. The
> shape below is the **target** if/when the per-NodeType compile watcher
> is re-introduced — it uses `WatchSubmission` with the
> `CompilationStatus` field as the fingerprint, which makes the
> "Pending → Compiling" guard unnecessary because `DistinctUntilChanged`
> on the fingerprint already prevents re-firing on our own write.

```csharp
hub.WatchSubmission(
    fingerprint:   node => (node.Content as NodeTypeDefinition)?.CompilationStatus,
    needsDispatch: node => node.Content is NodeTypeDefinition d
                         && d.CompilationStatus == CompilationStatus.Pending,
    dispatch:      node => Compile(workspace, compilationService, node)
                              .Select(_ => Unit.Default),
    logger:        logger);

// Compile is an IObservable<Unit> that flips Pending → Compiling, runs
// compilationService.CompileAndGetConfigurations, then writes the terminal
// (Ok / Error) status + AssemblyLocation back via workspace.UpdateMeshNode.
// Because the fingerprint is CompilationStatus, the watcher's own writes
// are filtered out by DistinctUntilChanged — no Throttle needed, no
// reentrancy guard needed.
```

### Cluster-wide cache propagation

Every silo's `NodeTypeService` uses `SyncedMeshNodeQuery` to subscribe to all `Code`
NodeType nodes. When the result is written back, the synced query emits the new node
state to every subscriber. Each silo's local `_hubConfigurations` cache picks up the
new `(AssemblyLocation, HubConfiguration)` automatically. The
`EnrichWithNodeType` fast-path then hits for that NodeType — `OnActivateAsync` becomes
synchronous.

## Error notification — caller must observe failure

The watcher dispatching the work MUST publish failure as well as success — a missing
or invalid NodeType, a compilation error, an unreachable persistence layer, all of
these need to flip a status field on the node. **Silent timeout is not acceptable**:
the caller is observing the same node's reducer; if the request never lands a
response, the caller has no way to distinguish "still working" from "broken".

Conventional shape:

```csharp
record CompilationStatus { Pending, Compiling, Ok, Failed, NodeNotFound, InvalidNodeType }
```

The caller's observation chain:

```csharp
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())
    .Where(c => c.Value?.Content is Code code
             && code.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Failed)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(60))
    .Select(c => /* unwrap */);
```

## Summary

| Aspect | Request/response (`hub.Observe`) | State-change-driven (`stream.Update`) |
|---|---|---|
| Trigger | Posted message | Node state mutation |
| Result delivery | Response message | Node state mutation (write-back) |
| Cluster propagation | Caller-targeted only | Every subscriber sees it |
| Activation-time cost | Cross-silo round-trip | Local cache lookup (after first warm-up) |
| Error model | DeliveryFailure / Timeout | Status field on node |
| Use when | Single-caller transient query | Work whose result belongs on the node |

Both patterns are valid; pick the one that matches the work's natural data shape.
For thread execution and dynamic NodeType compilation, the work's result IS the
node's content — `stream.Update()` is the natural fit.
