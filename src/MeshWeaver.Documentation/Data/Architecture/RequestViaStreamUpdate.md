---
Name: Request Via Stream Update
Description: "Canonical pattern for all mesh-node mutations: use workspace.GetMeshNodeStream(path).Update(...) instead of bespoke request/response types, with helpers, cross-hub patch semantics, and rationale."
---

# Requesting Work via `stream.Update()`

> **🚨 DEFAULT PATTERN for every mesh-node mutation.** Threads, thread messages, NodeType compile state, Code editing, satellite annotations — all of them. If you are about to write `class XxxRequest : IRequest<XxxResponse>` to mutate a node's content, **stop and read this page**.
>
> Mutate the target node's state via `workspace.GetMeshNodeStream(path).Update(current => modified)`. A server-side watcher on the owning hub picks up the change and dispatches any side-effect work. The result is published by writing back to the same node — propagated cluster-wide automatically by the synchronization protocol.
>
> **Reads use the same stream.** Server-side, [`IMeshNodeStreamCache`](xref:Hosting/MeshNodeStreamCache) hands out a single shared stream per node; client-side, the Blazor view holds an `ISynchronizationStream<MeshNode>` (see [GUI Data Binding](xref:GUI/DataBinding)). Same stream serves read + write — no parallel read API to keep coherent.
>
> **Why this is mandatory** (not just preferred): every recent "hub becomes unresponsive after the second operation" CI failure (CodeEditRecompile, NodeTypeRelease, LinkedInPullActions, ThreadAgentIntegration in run 26036857424) traced back to bespoke request/response handlers racing the watcher → two concurrent activities → leaked callbacks → wedged hub. The stream-based pattern is race-free by construction.

## Sanctioned exceptions

Use `hub.Observe(request)` ONLY for:

- **Node lifecycle on the mesh hub** — `CreateNodeRequest`, `DeleteNodeRequest`, `MoveNodeRequest`. These create / destroy / re-key the node itself; they don't mutate its content.
- **Transient queries that don't belong on any node** — e.g. autocomplete completions, one-shot diagnostic probes that aren't part of the node's state.

Everything else — including every state machine, every "trigger work and observe progress" flow, every UI button that mutates a node — uses `stream.Update()`.

## Staleness-safe cross-hub patches

`workspace.GetMeshNodeStream(otherPath).Update(...)` from a non-owner hub
(`UpdateRemote`) currently re-runs the lambda against the caller's
snapshot, NOT the owner's current state. The framework then ships an
RFC 7396 JSON-merge patch of the diff to the owner.

That works correctly only when the patch is **idempotent under
merge** — i.e. applying it twice produces the same result as applying
it once. Two cases are merge-safe:

1. **Single-field assignment**: `{ Foo: value }`. Merging twice = same.
2. **Dict SetItem by key**: `{ Bag: { key: value } }`. Merging twice = same key+value.

Two cases are NOT merge-safe:

1. **List append / prepend**: `node with { Messages = Messages.Add(x) }`.
   The diff becomes the WHOLE list (with `x` at the end). Two concurrent
   appends from different hubs each compute a list ending in `x`; the
   owner merges them in order and the last write wins, dropping the first.
2. **List rewrite based on read-modify-write**: same root cause —
   the patch is the new list, which doesn't account for concurrent
   writers.

**Design rule**: cross-hub mutations are a single `stream.Update(...)` on the
target node. The action-block serialisation on the owning hub guarantees
race-free merge; RFC-7396 patch semantics let the lambda touch only the
fields it intends to change.

The thread refactor is the canonical example. The whole resubmit/delete-from/
record-failure flow used to post bespoke `ResubmitTrigger` / `DeleteFromTrigger` /
`RecordFailureTrigger` messages, then briefly used intent-field payloads
(`RequestedResubmit`, `RequestedDeleteFromMessageId`, `PendingFailures`)
consumed by per-operation watchers, and now does the **full mutation inline**
inside the hub extension method's `stream.Update` lambda — truncate `Messages`,
re-queue `PendingUserMessages`, etc. all in one patch. See
`Doc/Architecture/ThreadOperations.md` for the public API
(`hub.ResubmitMessage`, `hub.DeleteFromMessage`, `hub.RecordSubmissionFailure`).

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
| Use when | Node lifecycle + transient queries (see "Sanctioned exceptions" above) | **Every other mutation** — default pattern |

Request/response is the *exception*, not a peer pattern. The work's result almost always belongs on a node (thread, message, NodeType, satellite); making the node's own content the contract eliminates an entire class of race conditions, leaked callbacks, and hub wedges that bespoke handlers reintroduce every time.
