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

## When to apply: dynamic NodeType compilation

Today `INodeTypeService.EnrichWithNodeType` handles dynamic `Code` NodeTypes via a
cross-silo request/response:

```csharp
// SLOW PATH — see NodeTypeService.cs:524-555
return hub.Observe(
    new GetCompilationPathRequest(/* HEAD */),
    o => o.WithTarget(new Address(nodeType)))
    .Select(d => d.Message)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(30))
    ...;
```

That round-trip happens during `MessageHubGrain.OnActivateAsync` and serialises into
long chains under cluster-cold-start. **It should be replaced with this pattern**:

### Caller — `EnrichWithNodeType` slow path becomes:

```csharp
// Mutate the NodeType node's state to request compilation
workspace.UpdateMeshNode(node =>
{
    var code = node.Content as Code ?? new Code();
    return node with
    {
        Content = code with
        {
            CompileRequestedAt = DateTime.UtcNow,
            // any other request payload (target version, options, ...)
        }
    };
}, nodePath: nodeTypePath);

// Then OBSERVE the same node's MeshNodeReference reducer for the result
// (AssemblyLocation appearing on Content, or a CompilationStatus field flipping
// to Compiled / Failed).
```

### Server — per-NodeType hub installs at init:

```csharp
workspace.GetStream(new MeshNodeReference())
    .Where(change => change.Value?.Content is Code code
                  && code.CompileRequestedAt > code.LastCompiledAt)
    .Throttle(TimeSpan.FromMilliseconds(50))
    .Subscribe(change => Compile(change.Value!).Subscribe(
        result => workspace.UpdateMeshNode(n => n with
        {
            Content = (Code)n.Content! with
            {
                AssemblyLocation = result.AssemblyLocation,
                LastCompiledAt = DateTime.UtcNow,
                CompilationStatus = CompilationStatus.Ok
            }
        }),
        ex => workspace.UpdateMeshNode(n => n with
        {
            Content = (Code)n.Content! with
            {
                CompilationStatus = CompilationStatus.Failed,
                CompilationError = ex.Message,
                LastCompiledAt = DateTime.UtcNow
            }
        })));
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
