# Requesting Work via `stream.Update()`

> **Pattern**: Instead of posting a request/response message, mutate the target node's
> state via `workspace.UpdateMeshNode(...)` and let a server-side watcher pick up the
> change and dispatch the work. The result is published by writing back to the same
> node — propagated cluster-wide automatically by the synchronization protocol.

## When to use this pattern

Use it when:

- The work needs to be **triggered** by a state change (caller mutates input fields).
- The work's **result** belongs on the same node (caller observes output fields on the same MeshNode it mutated).
- You want **automatic cluster-wide propagation** of the result (every silo that subscribes via `SyncedMeshNodeQuery` or per-node remote stream sees the update).
- You want **no cross-silo request/response round-trip during grain activation** (the watcher runs in-grain on whichever silo owns the node).

Do NOT use it for:

- One-shot transient queries that don't belong on the node (use `hub.Observe(request)` instead).
- Cases where the caller needs an *immediate* synchronous response (the watcher's Throttle window introduces latency).

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
var sub = workspace.GetStream(new MeshNodeReference())
    ?.Where(change => change.Value?.Content is MeshThread)
    ?.Throttle(TimeSpan.FromMilliseconds(50))
    ?.Subscribe(change =>
    {
        var thread = (MeshThread)change.Value!.Content!;
        if (thread.IsExecuting) return;          // reentrancy guard
        var dispatch = ThreadSubmission.PlanNextRound(thread);
        if (dispatch is null) return;            // nothing to do

        DispatchRound(threadHub, change.Value, dispatch, ...);
    });
```

The watcher subscribes once at hub init. Each tick of the `MeshNodeReference` reducer
that surfaces a thread node with pending input triggers a dispatch. `Throttle(50ms)`
coalesces rapid mutations into a single dispatch.

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

```csharp
// Subscribe to the per-NodeType remote stream and trigger compile only on the
// initial null/Unknown emission; further emissions are observed for the terminal
// status (Ok / Error). The trigger is a single stream.Update flipping
// CompilationStatus to Pending — the synchronization protocol routes the patch
// to the owning per-NodeType hub.
var stream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(nodeType), new MeshNodeReference());

return stream
    .Where(change => change?.Value != null)
    .Select(change => change.Value!)
    .Do(typeNode =>
    {
        if (typeNode.Content is not NodeTypeDefinition def) return;
        if (def.CompilationStatus is not null
            && def.CompilationStatus != CompilationStatus.Unknown) return;
        if (Interlocked.CompareExchange(ref triggered, 1, 0) != 0) return;
        stream.Update(current =>
            current?.Content is NodeTypeDefinition d
                && (d.CompilationStatus is null || d.CompilationStatus == CompilationStatus.Unknown)
                ? new ChangeItem<MeshNode>(
                    Value: current with { Content = d with { CompilationStatus = CompilationStatus.Pending } },
                    ChangedBy: WellKnownUsers.System,
                    StreamId: stream.StreamId,
                    ChangeType: ChangeType.Full,
                    Version: stream.Hub.Version,
                    Updates: null)
                : null);
    })
    .Where(typeNode => typeNode.Content is NodeTypeDefinition def
        && (def.CompilationStatus == CompilationStatus.Ok
            || def.CompilationStatus == CompilationStatus.Error))
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(30));
```

### Server — `MeshDataSourceExtensions.InstallCompileWatcher`

```csharp
workspace.GetMeshNodeStream()
    .Where(n => n?.Content is NodeTypeDefinition d
        && d.CompilationStatus == CompilationStatus.Pending)
    .Throttle(TimeSpan.FromMilliseconds(50))
    .SelectMany(node =>
    {
        // Pending → Compiling so the filter no longer matches; throttle would
        // otherwise let one extra Pending sneak through.
        workspace.UpdateMeshNode(curr =>
            curr.Content is NodeTypeDefinition def
                ? curr with { Content = def with {
                    CompilationStatus = CompilationStatus.Compiling,
                    LastCompileStartedAt = DateTimeOffset.UtcNow } }
                : curr);

        return compilationService.CompileAndGetConfigurations(node!).Take(1);
    })
    .Subscribe(result =>
    {
        workspace.UpdateMeshNode(curr =>
        {
            if (curr.Content is not NodeTypeDefinition def) return curr;
            return !string.IsNullOrEmpty(result?.AssemblyLocation)
                ? curr with {
                    Content = def with {
                        CompilationStatus = CompilationStatus.Ok,
                        CompilationError = null,
                        LastCompileSucceededAt = DateTimeOffset.UtcNow,
                        LastCompiledVersion = curr.Version },
                    AssemblyLocation = result.AssemblyLocation }
                : curr with {
                    Content = def with {
                        CompilationStatus = CompilationStatus.Error,
                        CompilationError = result?.Log?.Errors().FirstOrDefault()?.Message
                            ?? "Compilation produced no assembly" } };
        });
    });
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
