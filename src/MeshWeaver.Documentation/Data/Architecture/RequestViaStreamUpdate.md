---
Name: Request Via Stream Update
Description: "Canonical pattern for all mesh-node mutations: use workspace.GetMeshNodeStream(path).Update(...) instead of bespoke request/response types, with helpers, cross-hub patch semantics, and rationale."
---

# Requesting Work via `stream.Update()`

> **🚨 DEFAULT PATTERN — required for every mesh-node mutation.** Threads, thread messages, NodeType compile state, code editing, satellite annotations — all of them. If you are about to write `class XxxRequest : IRequest<XxxResponse>` to mutate a node's content, **stop and read this page first**.

The single rule: mutate the target node's state via `workspace.GetMeshNodeStream(path).Update(current => modified)`. A server-side watcher on the owning hub picks up the change and dispatches any side-effect work. Results are published by writing back to the same node, and the synchronization protocol propagates them cluster-wide automatically.

**Reads use the same stream.** Server-side, [`IMeshNodeStreamCache`](xref:Hosting/MeshNodeStreamCache) hands out a single shared stream per node. Client-side, the Blazor view holds an `ISynchronizationStream<MeshNode>` (see [GUI Data Binding](xref:GUI/DataBinding)). Read and write share one stream — there is no separate read API to keep in sync.

**Why this is mandatory, not merely preferred.** Every recent "hub becomes unresponsive after the second operation" CI failure — CodeEditRecompile, NodeTypeRelease, LinkedInPullActions, ThreadAgentIntegration in run 26036857424 — traced back to bespoke request/response handlers racing the watcher: two concurrent activities, leaked callbacks, wedged hub. The stream-based pattern is race-free by construction.
<svg viewBox="0 0 760 310" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
<defs>
<marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
</marker>
<marker id="arr-blue" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#1e88e5"/>
</marker>
<marker id="arr-green" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#43a047"/>
</marker>
<marker id="arr-orange" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#f57c00"/>
</marker>
</defs>
<rect x="10" y="30" width="130" height="54" rx="10" fill="#1e88e5"/>
<text x="75" y="53" text-anchor="middle" fill="#fff" font-weight="bold">Caller</text>
<text x="75" y="71" text-anchor="middle" fill="#fff" font-size="11">any hub / UI</text>
<rect x="10" y="220" width="130" height="54" rx="10" fill="#5c6bc0"/>
<text x="75" y="243" text-anchor="middle" fill="#fff" font-weight="bold">Subscriber</text>
<text x="75" y="261" text-anchor="middle" fill="#fff" font-size="11">other silos / clients</text>
<rect x="305" y="120" width="150" height="60" rx="10" fill="#37474f"/>
<text x="380" y="144" text-anchor="middle" fill="#fff" font-weight="bold">MeshNode</text>
<text x="380" y="163" text-anchor="middle" fill="#fff" font-size="11">stream (shared)</text>
<rect x="305" y="30" width="150" height="54" rx="10" fill="#26a69a"/>
<text x="380" y="53" text-anchor="middle" fill="#fff" font-weight="bold">IMeshNodeStreamCache</text>
<text x="380" y="71" text-anchor="middle" fill="#fff" font-size="11">one stream per node</text>
<rect x="570" y="100" width="140" height="54" rx="10" fill="#e53935"/>
<text x="640" y="122" text-anchor="middle" fill="#fff" font-weight="bold">Owning Hub</text>
<text x="640" y="140" text-anchor="middle" fill="#fff" font-size="11">WatchSubmission watcher</text>
<rect x="570" y="220" width="140" height="54" rx="10" fill="#f57c00"/>
<text x="640" y="243" text-anchor="middle" fill="#fff" font-weight="bold">Worker</text>
<text x="640" y="261" text-anchor="middle" fill="#fff" font-size="11">dispatch / side-effect</text>
<line x1="140" y1="57" x2="304" y2="139" stroke="#1e88e5" stroke-width="1.5" marker-end="url(#arr-blue)"/>
<text x="195" y="86" fill="#1e88e5" font-size="11" font-style="italic">stream.Update(…)</text>
<line x1="380" y1="84" x2="380" y2="119" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
<text x="386" y="108" fill="currentColor" fill-opacity=".6" font-size="11">emits change</text>
<line x1="455" y1="150" x2="569" y2="127" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr)"/>
<text x="483" y="131" fill="#e53935" font-size="11" font-style="italic">subscribe</text>
<line x1="640" y1="154" x2="640" y2="219" stroke="#f57c00" stroke-width="1.5" marker-end="url(#arr-orange)"/>
<text x="646" y="192" fill="#f57c00" font-size="11" font-style="italic">dispatch work</text>
<line x1="569" y1="247" x2="456" y2="165" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
<text x="489" y="222" fill="#43a047" font-size="11" font-style="italic">write-back result</text>
<line x1="305" y1="165" x2="141" y2="247" stroke="#5c6bc0" stroke-width="1.5" marker-end="url(#arr)"/>
<text x="160" y="210" fill="#5c6bc0" font-size="11" font-style="italic">propagate</text>
</svg>
*Stream-based mutation flow: caller writes via `stream.Update`, the owning hub's watcher dispatches work, the worker writes results back to the same node, and every subscriber (other silos, clients) receives the change automatically.*

---

## Sanctioned Exceptions

`hub.Observe(request)` is valid only for:

| Use case | Why it's different |
|---|---|
| **Node lifecycle** — `CreateNodeRequest`, `DeleteNodeRequest`, `MoveNodeRequest` | Creates, destroys, or re-keys the node itself; does not mutate its content |
| **Transient queries** — autocomplete completions, one-shot diagnostic probes | Result does not belong on any node's persistent state |

Everything else — state machines, "trigger work and observe progress" flows, every UI button that mutates a node — uses `stream.Update()`.

---

## Cross-Hub Patch Semantics

When you call `workspace.GetMeshNodeStream(otherPath).Update(...)` from a non-owner hub (`UpdateRemote`), the framework re-runs your lambda against the caller's snapshot and ships an RFC 7396 JSON-merge patch of the diff to the owner.

This is safe only when the patch is **idempotent under merge** — applying it twice yields the same result as applying it once.

**Merge-safe operations:**

- **Single-field assignment** — `{ Foo: value }`. Merging twice gives the same result.
- **Dict `SetItem` by key** — `{ Bag: { key: value } }`. Merging twice leaves the same key-value pair.

**Not merge-safe:**

- **List append / prepend** — `node with { Messages = Messages.Add(x) }`. The patch becomes the whole list with `x` at the end. Two concurrent appends each compute a list ending in `x`; the owner merges them in order and the last write wins, silently dropping the first.
- **Read-modify-write on a list** — same root cause: the patch is the new list, unaware of concurrent writers.

> **Design rule:** Cross-hub mutations should be a single `stream.Update(...)` on the target node. The owning hub's action-block serialisation guarantees race-free merge; RFC 7396 patch semantics ensure you touch only the fields you intend to change.

**The thread refactor as the canonical example.** The full resubmit/delete-from/record-failure flow once posted bespoke trigger messages, then briefly used intent-field payloads (`RequestedResubmit`, `RequestedDeleteFromMessageId`, `PendingFailures`) consumed by per-operation watchers. Today, the **full mutation is inline** inside the hub extension method's `stream.Update` lambda — truncate `Messages`, re-queue `PendingUserMessages`, etc., all in one patch. See [ThreadOperations.md](ThreadOperations) for the public API (`hub.ResubmitMessage`, `hub.DeleteFromMessage`, `hub.RecordSubmissionFailure`).

---

## Canonical Helpers

Don't roll your own watcher. Two helpers in `MeshWeaver.Mesh.Contract` (`ActivityControlPlaneExtensions.cs`) implement this pattern as one-liners:

**`hub.WatchControlPlane(onRequestedStatus, logger)`**
Use when the trigger is the single `ActivityLog.RequestedStatus` field. Drives Cancel / Start / Retry off a property patch.

**`hub.WatchSubmission(fingerprint, needsDispatch, dispatch, logger)`**
Use when the trigger is an arbitrary "this state needs work now" predicate — for example, a thread has unprocessed user messages and isn't already executing. Internally it composes:

```
GetMeshNodeStream → DistinctUntilChanged(fingerprint) → Where(needsDispatch) → SelectMany(dispatch)
```

The canonical reference for both helpers is [ActivityControlPlane.md](ActivityControlPlane) (see § "Generalising: `WatchSubmission`" and § "Anti-patterns to remove on sight"). If you find yourself reaching for `Throttle(...)`, `Interlocked.CompareExchange`, or a manual `Subject` + flag, that's the signal you should be calling one of these helpers instead.

---

## When to Use This Pattern

**Use it when:**

- The work is **triggered by a state change** (caller mutates input fields).
- The result **belongs on the same node** (caller observes output fields on the same `MeshNode` it mutated).
- You want **automatic cluster-wide propagation** — every silo that subscribes via `SyncedMeshNodeQuery` or a per-node remote stream sees the update without any explicit broadcast.
- You want **no cross-silo round-trip during grain activation** — the watcher runs in-grain on whichever silo owns the node.

**Do not use it for:**

- One-shot transient queries whose result doesn't belong on a node (use `hub.Observe(request)` instead).
- Cases requiring an immediate synchronous response — the watcher dispatches reactively off the node stream, so the round has multi-step latency compared to a direct `hub.Observe` round-trip.

---

## The Canonical Example: Thread Execution

`MeshWeaver.AI` already implements this pattern end-to-end for thread execution dispatch.

### Caller — writing the request into node state

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
            // ... other pending fields ...
        }
    };
});
```

The caller writes the request *into* the thread node's state. `PendingUserMessages` is the request payload. No `IRequest<TResponse>` is posted. No callback is registered. The work is requested by the very act of mutating the node.

### Server — the dispatch watcher

```csharp
// MeshWeaver.AI/ThreadSubmission.cs — ThreadSubmissionServer.InstallServerWatcher
return threadHub.WatchSubmission(
    fingerprint:   Fingerprint,     // (IsExecuting, Messages.Count, IngestedMessageIds.Count, PendingUserMessages.Count)
    needsDispatch: NeedsDispatch,   // !IsExecuting && (PendingUserMessages.Count > 0 || any UserMessageId not in IngestedMessageIds)
    dispatch:      node => DispatchRoundObs(threadHub, node, logger),
    logger:        logger);
```

The watcher subscribes once at hub init. `DistinctUntilChanged` on the fingerprint guarantees the same actionable state cannot fire twice — there is no `dispatching` flag, no `Throttle`, no reentrancy guard. `DispatchRoundObs` is an `IObservable<Unit>` that creates satellite cells, commits the round to the thread node, and posts to the `_Exec` hub; it composes via `SelectMany` into one round per emission. Failures in `dispatch` are logged and swallowed — the next state change retries naturally. See `ThreadSubmissionServer.InstallServerWatcher` in `MeshWeaver.AI/ThreadSubmission.cs` for the full live source.

### Result publication

The dispatched work writes its progress and final result back onto the same node (or a satellite node it owns) via `workspace.UpdateMeshNode(...)`. The result reaches every subscriber automatically:

- **Local:** other subscribers on the same hub see the next emission of the `MeshNodeReference` reducer.
- **Cross-silo / clients:** `SyncedMeshNodeQuery` and `GetRemoteStream<MeshNode, MeshNodeReference>` subscribers see the change via the synchronization protocol — no explicit broadcast needed.

---

## Applied: Dynamic NodeType Compilation

`INodeTypeService.EnrichWithNodeType`'s slow path now uses this pattern. `GetCompilationPathRequest` is retained as a fallback for hubs without a workspace / remote-stream reducer; the primary path is stream-based.

### Caller — `NodeTypeService.ResolveViaStream`

> **Note:** The current production code in `NodeTypeService.cs` still uses an `Interlocked.CompareExchange(ref triggered, 1, 0)` flag to guard the one-shot `Pending` trigger. That is the imperative anti-pattern and is being migrated to the shape below. The example shows the **target** form: split the chain into a one-shot trigger pipeline and a terminal-status observation pipeline, both reactive.

```csharp
// Subscribe to the per-NodeType remote stream.
// Trigger pipeline: takes the first emission whose CompilationStatus is null/Unknown
// and writes a single stream.Update flipping it to Pending.
// .Take(1) makes the trigger inherently one-shot — no `triggered` flag, no CompareExchange.
// Observation pipeline: waits for terminal status (Ok / Error) on the same stream.
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

> **Note:** The previous `InstallCompileWatcher` (`Throttle(50ms)` + `SelectMany` + a manual `Pending → Compiling` flip to dodge throttle's trailing-edge re-emission) was removed from `MeshDataSource.cs`. The shape below is the **target** if/when the per-NodeType compile watcher is re-introduced — `WatchSubmission` with `CompilationStatus` as the fingerprint makes the `Pending → Compiling` guard unnecessary, because `DistinctUntilChanged` already prevents re-firing on our own write.

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
// are filtered out by DistinctUntilChanged — no Throttle needed, no reentrancy guard.
```

### Cluster-wide cache propagation

Every silo's `NodeTypeService` uses `SyncedMeshNodeQuery` to subscribe to all `Code` NodeType nodes. When the compiled result is written back, the synced query emits the new node state to every subscriber. Each silo's local `_hubConfigurations` cache picks up the new `(AssemblyLocation, HubConfiguration)` automatically — `EnrichWithNodeType`'s fast path then hits for that NodeType, and `OnActivateAsync` becomes synchronous.

---

## Error Notification — Callers Must Observe Failure

The watcher dispatching the work **must** publish failure as well as success. A missing or invalid NodeType, a compilation error, an unreachable persistence layer — all must flip a status field on the node.

> **Silent timeout is not acceptable.** The caller observes the same node's reducer. If the request never lands a response, the caller cannot distinguish "still working" from "broken".

Conventional status shape:

```csharp
record CompilationStatus { Pending, Compiling, Ok, Failed, NodeNotFound, InvalidNodeType }
```

Caller observation chain:

```csharp
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())
    .Where(c => c.Value?.Content is Code code
             && code.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Failed)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(60))
    .Select(c => /* unwrap */);
```

---

## Pattern Comparison

| Aspect | Request/response (`hub.Observe`) | State-change-driven (`stream.Update`) |
|---|---|---|
| Trigger | Posted message | Node state mutation |
| Result delivery | Response message | Node state write-back |
| Cluster propagation | Targeted at caller only | Every subscriber sees it automatically |
| Activation-time cost | Cross-silo round-trip | Local cache lookup (after first warm-up) |
| Error model | `DeliveryFailure` / `Timeout` | Status field on node |
| **Use when** | Node lifecycle + transient queries (see exceptions above) | **Every other mutation — the default** |

Request/response is the *exception*, not a peer pattern. The work's result almost always belongs on a node (thread, message, NodeType, satellite). Making the node's own content the contract eliminates an entire class of race conditions, leaked callbacks, and hub wedges that bespoke handlers reintroduce every time.
