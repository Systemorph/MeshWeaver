# Asynchronous Calls in MeshWeaver

MeshWeaver uses a **truly asynchronous** message-passing model. This is fundamentally different from C#'s `async/await` pattern, which is better described as "fake async" — you still block the calling context waiting for a result.

## 🚨 The absolute rules (no exceptions outside tests)

1. **No `Task<T>` / `async` / `await` in mesh-reachable code.** Public methods on services, handlers, layout areas, and click actions return `IObservable<T>` (or `void`). Return types matter — an `async Task` method that `await`s a hub operation deadlocks the hub ActionBlock. No exceptions for "just a wrapper" or "small helper".
2. **No `*Async` extension shims on `IMeshService`.** Use `meshService.CreateNode(node)` / `UpdateNode(node)` / `DeleteNode(path)` / `CreateTransient(node)` — these return `IObservable<MeshNode>`. **Never** use `.CreateNodeAsync(...)` / `.UpdateNodeAsync(...)` / `.DeleteNodeAsync(...)` / `.CreateTransientAsync(...)` — those extensions are being removed. They bridge the Observable to Task via `.ToTask()` and make the caller `await`, which deadlocks every time they are reached from a hub handler.
3. **Never `.QueryAsync<MeshNode>($"path:X").FirstOrDefaultAsync()` to read a known node.** Queries go through a lagged read-side index. For a known path use `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(new Address(path), new MeshNodeReference()).Take(1).Select(change => change.Value)`. Also the primitive for **wait-for-completion** — subscribe until a completion condition emits.
4. **Never wrap a Task-returning query in `Observable.FromAsync(() => query.QueryAsync(...).FirstOrDefaultAsync().AsTask())`.** This is fake-reactive — runs through the lagged index and returns stale content. Use `GetRemoteStream<MeshNode, MeshNodeReference>` for the authoritative live view.
5. **`ISynchronizationStream<T>.Update` callbacks must be synchronous.** Don't use the `Func<T?, CancellationToken, Task<ChangeItem<T>?>>` overload from hub-reachable code — it hides an `await` inside the stream update. Use the sync `Func<T?, ChangeItem<T>?>` form and compose any async I/O outside the callback.

## 🚨🚨🚨 NEVER USE `QueryAsync` TO OBTAIN A `MeshNode` 🚨🚨🚨

**Queries are not a node lookup. Queries are not a node lookup. Queries are not a node lookup.**

`IMeshService.QueryAsync` is for searching and listing — it runs through a **lagged, eventually-consistent read-side index** that can return stale content right after a write. It is **never** the right tool for reading the current committed state of a specific node.

### ❌ WRONG — every line below is a bug

```csharp
// ❌ Lagged index — returns stale content after a write.
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();

// ❌ Same bug, wrapped in Observable.FromAsync to look reactive.
return Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct).AsTask());

// ❌ Even with a path: filter, this is still a query. Still lagged. Still wrong.
await foreach (var n in mesh.QueryAsync<MeshNode>($"path:{path}")) { node = n; break; }

// ❌ Calling .Current on a stream — snapshot may be null before first emission.
var node = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;
```

### ✅ RIGHT — the ONE way to obtain a known MeshNode

```csharp
// Direct subscription to the owning hub's workspace reference.
// Authoritative, live, no staleness, no query index involved.
var workspace = hub.GetWorkspace();
return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(path), new MeshNodeReference())
    .Take(1)                                    // one emission then complete
    .Timeout(TimeSpan.FromSeconds(10))          // bound the wait
    .Select(change => change.Value);            // unwrap ChangeItem<MeshNode>
```

This is also how you **wait for work to finish** — subscribe until a field in the node's content flips to a completion state, then `.Take(1)`. No polling loop. No repeated queries.

### Sets / listings — **prefer `ObserveQuery`**, not `QueryAsync`

Even for the cases where a query is the right idea (listings, filters, existence across the mesh), **do not `await` the `IAsyncEnumerable<T>`** version — use the reactive `IMeshService.ObserveQuery<T>` overload. It returns `IObservable<QueryResultChange<T>>` with an initial full set and then incremental deltas, and it composes with `Select` / `Where` / `Subscribe` exactly like every other mesh observable.

**`QueryAsync` breaks the update flow.** It is a one-shot snapshot: you get the rows that existed at query time and nothing else. The view is frozen — if a row is added, removed, or mutated on the mesh, your list doesn't change. Any reactive chain downstream (a layout area, a dashboard, a dependent query) that re-renders when data changes is now silently broken because this particular upstream doesn't emit on updates. `ObserveQuery` emits the initial set plus a delta for every subsequent change, so the downstream chain stays live.

```csharp
// ❌ WRONG — IAsyncEnumerable + await — hub ActionBlock blocks on query pump.
var items = await meshService.QueryAsync<MeshNode>("nodeType:Post").ToListAsync();

// ✅ RIGHT — reactive, live, auto-updates on mesh changes.
meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("nodeType:Post"))
    .Select(change => change.Result)           // full result set (or delta payload)
    .Subscribe(nodes => { /* render, react */ });
```

Valid uses of `ObserveQuery`:

- Listing children of a namespace (`path/*`)
- Filtering by predicate across the mesh (`nodeType:X`, `name:*sales*`)
- Checking "does any node match this predicate?" for existence tests
- Autocomplete / browsing / search UIs
- Any layout area that renders a list and wants live updates when the underlying set changes

Known-path single-node reads: **`GetRemoteStream<MeshNode, MeshNodeReference>`** (see above), never a query. No exceptions.

### The ONE case where `QueryAsync` is correct: one-shot lookups that exit the process

`QueryAsync` is a one-time snapshot — the result is frozen at query time and does not reflect subsequent mutations. That is exactly the shape needed for request/response call sites that return **once** and then the caller is gone:

- **MCP tool handlers** — an agent calls a tool, the tool returns a payload, the session ends. No reactive subscriber downstream, no update flow to break.
- **Export / import CLI services** — pull-and-leave jobs that dump to disk.
- **HTTP endpoints that render once and close** — e.g. a CSV download endpoint.

Anywhere else — layout areas, dashboards, chained reactive consumers, hub handlers, click actions, background orchestration that waits for state to flip — `QueryAsync` is wrong because the view won't update. Use `ObserveQuery` for those.

Rule of thumb: **if any downstream code re-renders or re-computes when data changes, you need `ObserveQuery`.** `QueryAsync` is only safe when the caller serialises the snapshot and walks away.

## 🚨 Hard rule — never read `.Current` off a stream

Streams (`ISynchronizationStream<T>`, any `workspace.GetStream(...)` /
`GetRemoteStream(...)` result) **must be consumed reactively**. That means
`.Select(...)` / `.Where(...)` / `.Take(1)` / `.Subscribe(...)` — never
`.Current` / `.Current?.Value`.

`Current` is a snapshot that is only populated *after* the stream has emitted its
first value. Inside a handler that has just caused the hub to activate, the
workspace may not have loaded its data yet — `Current` will be null and you will
ship a wrong answer. The reactive chain handles this correctly: Subscribe fires
once the stream actually emits.

```csharp
// ❌ NEVER — looks sync, returns wrong answer on cold workspaces.
var node = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;

// ✅ ALWAYS — reactive chain, fires when the stream emits.
hub.GetWorkspace().GetStream(new MeshNodeReference())
    ?.Select(change => change.Value)
    .Where(node => node is not null)
    .Take(1)
    .Subscribe(node => { /* handler body */ });
```

The handler method itself still returns `request.Processed()` immediately —
the Subscribe callback fires later, posts the response via
`hub.Post(response, o => o.ResponseFor(request))`. The caller blocks on
`RegisterCallback`, not on the handler method.

## 🚨 Related rule, read this first

**Queries are for sets and existence — never for reading a specific node's content.**
Queries go through a read-side index that lags behind writes; they are eventually
consistent. To read the current committed state of a known node, use
`workspace.GetRemoteStream<MeshNode, MeshNodeReference>(address, new MeshNodeReference())`.
That stream is also how you **wait for a job to finish** (subscribe until a completion
condition emits) — no polling, no `await` on a long-running task.

Full treatment: *[CQRS — Queries vs. Content Access](CqrsAndContentAccess)*.

The anti-patterns below (`Observable.FromAsync(() => query.FirstOrDefaultAsync(...).AsTask())`)
are fake-reactive wrappers over the lagged read path. They don't deadlock the hub —
they return stale content. Same bug class, different symptom.

## The T-Shirt Analogy

When you order a t-shirt online, you don't stand next to the mailbox until it arrives. Your life continues. The t-shirt shows up later, and you deal with it then.

**Truly async (MeshWeaver pattern):**
```csharp
// Post the request — fire and forget
hub.Post(new MyRequest(), o => o.WithTarget(address));

// Register a callback — triggered when the answer returns
hub.RegisterCallback(delivery, response =>
{
    // Handle response here — your "mailbox notification"
    return response;
});

// Your code continues immediately — no blocking
return delivery.Processed();
```

**Fake async (C# async/await):**
```csharp
// You ARE standing at the mailbox
var response = await hub.AwaitResponse<MyResponse>(request);
// Nothing else happens until the response arrives
```

## Why `await` Deadlocks in Hub Handlers

The message hub processes messages sequentially through a single-threaded `ActionBlock`. When a handler calls `await`, it blocks the action block waiting for a response. But that response is itself a message that needs to be processed by the same action block — which is blocked. **Deadlock.**

```
Handler runs on ActionBlock
    → await AwaitResponse(request)
        → ActionBlock is blocked waiting
            → Response message arrives
                → Cannot be processed — ActionBlock is busy
                    → DEADLOCK
```

This applies to:
- `await hub.AwaitResponse(...)` — blocks the hub
- `await someTask` — blocks the hub scheduler
- `hub.InvokeAsync(...)` — schedules work on the blocked scheduler
- `workspace.GetStream().Subscribe(...)` — if the stream observes on the hub scheduler, the emission is queued behind the blocked handler

## The Observable Pattern

Use `IMeshService` to get into reactive/observable contexts. Observables are inherently truly async — you subscribe and get notified when data is available.

### Creating Nodes (Non-Blocking)

Fire-and-forget node creation. State updates go in the **handler body** (runs on the grain scheduler), not in the Subscribe callback:

```csharp
// Fire-and-forget — no callback needed for state updates
meshService.CreateNode(new MeshNode(id, namespace)
{
    NodeType = "MyType",
    Content = new MyContent { ... }
}).Subscribe(
    _ => logger.LogInformation("Node created"),
    error => logger.LogError(error, "Node creation failed"));

// State update in the handler body (grain scheduler) — safe
hub.GetWorkspace().UpdateMeshNode(node => node with
{
    Content = content with { Messages = content.Messages.Add(id) }
});

// Handler returns immediately
return delivery.Processed();
```

### CRITICAL: Never Do State Updates in Subscribe Callbacks

Subscribe callbacks run on **arbitrary threads**. State updates (`workspace.UpdateMeshNode`) require the hub's scheduler. Mixing these causes deadlocks — this is not framework-specific, it's a fundamental consequence of truly async programming: you don't control which thread a callback runs on.

```csharp
// WRONG — callback runs on unknown thread, state update needs hub scheduler:
meshService.CreateNode(node).Subscribe(_ =>
{
    workspace.UpdateMeshNode(n => ...); // ← deadlock: wrong thread
});

// CORRECT — separate concerns: fire-and-forget for I/O, state update in handler body:
meshService.CreateNode(node).Subscribe();  // fire-and-forget
hub.GetWorkspace().UpdateMeshNode(n => ...);  // handler body = hub scheduler
```

The principle: **I/O is fire-and-forget, state changes happen where you control the thread.** This is true for any actor-based or message-passing system.

## UI Click Actions — Same Rules Apply

Blazor button clicks configured via `WithClickAction` flow through the layout area host, which is backed by a message hub. **The same rule applies: no `await` on mesh-backed operations inside the click handler.** The hub pump is shared with all the layout's other reactive updates — blocking it freezes the UI.

### The canonical reactive click handler

```csharp
.WithClickAction(ctx =>
{
    // 1. Immediate optimistic feedback so the user sees the click registered.
    ctx.Host.UpdateData(resultId, "<p>Working…</p>");

    // 2. Read form data via Subscribe (NOT await FirstAsync).
    //    The data stream emits its current value synchronously on subscribe.
    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
        .Take(1)
        .Subscribe(data =>
        {
            var label = data?.GetValueOrDefault("label")?.ToString() ?? "";
            if (string.IsNullOrEmpty(label))
            {
                ctx.Host.UpdateData(resultId, "<p>Please enter a label.</p>");
                return;
            }

            // 3. Call reactive services — IObservable<T>, not Task<T>.
            //    These compose hub.Post + RegisterCallback under the hood.
            myService.DoWork(label).Subscribe(
                result => ctx.Host.UpdateData(resultId, $"<p>Done: {result}</p>"),
                ex     => ctx.Host.UpdateData(resultId, $"<p>Error: {ex.Message}</p>"));
        });

    return Task.CompletedTask;  // click handler itself is synchronous
})
```

### Writing reactive services

Expose `IObservable<T>` (not `Task<T>`) from any service that will be called from a click handler or hub handler. Compose with `SelectMany`, `Select`, `FirstOrDefaultAsync` (the Rx operator, not the `IAsyncEnumerable` extension — do not `await` it).

```csharp
public IObservable<TokenResult> CreateToken(string label)
{
    var userNode = new MeshNode(...);
    return nodeFactory.CreateNode(userNode)                  // IObservable<MeshNode>
        .SelectMany(created =>
        {
            var indexNode = new MeshNode(...);
            return nodeFactory.CreateNode(indexNode)         // second reactive write
                .Select(_ => new TokenResult(rawToken, created));
        });
    // No await. Consumer calls .Subscribe(onNext, onError).
}

// ❌ WRONG — `Observable.FromAsync(() => query.FirstOrDefaultAsync().AsTask())`
//    is the fake-reactive pattern. It runs through the lagged read-side index,
//    can return stale content just after a write, and provides no live updates.
//
// ✅ CORRECT — read the current committed content directly from the owning hub:
public IObservable<bool> DeleteToken(string path)
{
    var workspace = hub.GetWorkspace();
    return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference())
        .Take(1)
        .Timeout(TimeSpan.FromSeconds(10))
        .SelectMany(change => change.Value is null
            ? Observable.Return(false)
            : nodeFactory.DeleteNode(path));
}
```

See *[CQRS — Queries vs. Content Access](CqrsAndContentAccess)* for the full rule.

### Anti-patterns in click handlers

```csharp
// ❌ async click handler with await — deadlocks under load.
.WithClickAction(async ctx =>
{
    var data = await ctx.Host.Stream.GetDataStream<T>(id).FirstAsync();
    var result = await myService.DoWorkAsync(data);  // hub-backed service
    ctx.Host.UpdateData(resultId, result);
})

// ❌ Task.Run as a "fix" — hides the problem: AccessContext doesn't flow,
// exceptions vanish into the thread pool, and you can't compose with other streams.
.WithClickAction(ctx =>
{
    _ = Task.Run(async () => { await myService.DoWorkAsync(); });
    return Task.CompletedTask;
})
```

## Post + RegisterCallback Pattern

For request-response flows where you need the result but can't block:

```csharp
// 1. Post the request (returns immediately)
var delivery = hub.Post(new CreateNodeRequest(node), o => o.WithTarget(address));

// 2. Register a callback for the response (non-blocking)
hub.RegisterCallback((IMessageDelivery)delivery, response =>
{
    if (response is IMessageDelivery<CreateNodeResponse> cnr)
    {
        // Handle success
        tcs.TrySetResult(cnr.Message);
    }
    return response;
});

// 3. Return immediately — callback fires later
return delivery.Processed();
```

## 🚨 Node-mutating requests must run on the owning hub — forward, don't process locally

`UpdateNodeRequest`, `MoveNodeRequest`, and any future per-node mutation request **must** be processed on the owning per-node hub. The owning hub holds the authoritative MeshNode in its workspace (loaded by `MeshDataSource` at init via `MeshNodeReference`); the mesh hub does not.

When a request arrives at a hub that is not the owning hub (typically the mesh hub, where `IMeshService.UpdateNode` posts by default), **forward** it to the owning hub and relay the response — same shape as `UpdateThreadMessageContent` (which posts to the message's per-node address and is handled there by `HandleUpdateContent`).

```csharp
// Mesh hub's UpdateNodeRequest handler — forward to owning hub.
private static IMessageDelivery HandleUpdateNodeRequest(
    IMessageHub hub, IMessageDelivery<UpdateNodeRequest> request)
{
    var updatedNode = request.Message.Node;

    if (!hub.Address.ToString().Equals(updatedNode.Path, StringComparison.Ordinal))
    {
        var fwd = hub.Post(request.Message, o => o.WithTarget(new Address(updatedNode.Path)));
        hub.RegisterCallback((IMessageDelivery)fwd, response =>
        {
            if (response is IMessageDelivery<UpdateNodeResponse> ur)
                hub.Post(ur.Message, o => o.ResponseFor(request));
            else if (response.Message is DeliveryFailure df)
                hub.Post(UpdateNodeResponse.Fail(df.Message ?? "Forwarding failed"),
                    o => o.ResponseFor(request));
            return response.Processed();
        });
        return request.Processed();
    }

    // Own hub: read existing via the local MeshNodeReference stream and validate locally.
    var existingNodeObs = hub.GetWorkspace().GetMeshNodeStream()
        .Take(1).Timeout(TimeSpan.FromSeconds(15));
    // ... validate, persist, respond inside the Subscribe callback ...
}
```

**Why:** the mesh hub workspace doesn't carry a MeshNode collection — it has no `MeshNodeReference` reducer, no per-node validation context, no version tracking for the target. Trying to read existing state via `workspace.GetMeshNodeStream()` on the mesh hub throws `Failed to create stream`. Trying via `GetMeshNodeStream(path)` (remote subscription) hangs because no per-node hub has been activated yet for nodes the caller hasn't touched. Forwarding the request lets routing activate the owning hub on demand; that hub's `MeshDataSource` init loads the node from persistence, the gate opens, and the handler runs locally with `GetMeshNodeStream()` (own).

The 2026-04-24 PlanStorage / MeshNodeAuditing test failures all traced to the same bug: the mesh hub's UpdateNodeRequest handler was trying to read existing state locally. The fix is to forward to the owning hub. Same pattern applies to `MoveNodeRequest` (forward to source hub).

## 🚨 Blazor / GUI rule — *no `await` ever, no `Task.FromResult`, stay in observables*

**Never** `await` a mesh operation in a Blazor component lifecycle method, click handler, autocomplete callback, or anywhere else. This is non-negotiable: every `await meshService.QueryAsync(...)`, `await meshService.UpdateNode(...)`, `await Hub.AwaitResponse(...)` in GUI code is a deadlock waiting to happen, and `Task.FromResult(snapshot)` is no better — it freezes the snapshot at the call moment and ignores live updates.

The pattern is: **maintain a state list (or scalar) outside the observable; subscribe to the mesh observable; when the observable emits, fold the new items into your state list (sorted/dedup as required) and call `StateHasChanged`**.

```csharp
public partial class MyView : ComponentBase, IDisposable
{
    // Maintained list — kept sorted by score / Path / whatever the view requires.
    private readonly List<Suggestion> _suggestions = new();
    private IDisposable? _sub;
    private string _query = "";

    private void RefreshSuggestions(string query)
    {
        if (query == _query) return;
        _query = query;
        _sub?.Dispose();
        _suggestions.Clear();

        // ObserveQuery — the live stream of result-set changes for this query.
        // Initial / Reset → replace; Added / Updated → upsert; Removed → drop.
        _sub = MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Subscribe(change =>
            {
                ApplyChange(change);                  // updates _suggestions IN PLACE
                _suggestions.Sort(BestMatchComparer); // keep the list sorted (e.g. by score)
                InvokeAsync(StateHasChanged);
            });
    }

    public void Dispose() => _sub?.Dispose();
}
```

The view binds to `_suggestions` directly — no callback that returns `Task<T[]>`. Where a third-party control (FluentUI Autocomplete, etc.) requires a `Task<T[]>` callback, bind to a property/list instead of using the callback; the observable subscription pushes updates into the bound list and `StateHasChanged` triggers re-render.

Specifically forbidden in GUI code (and the substitution to use):

| ❌ Wrong | ✅ Right |
|---|---|
| `var x = await mesh.QueryAsync(...).ToListAsync()` | `mesh.ObserveQuery<T>(req).Subscribe(c => ApplyChange(c))` |
| `await Hub.AwaitResponse<R>(req, ...)` | `Hub.Post(req, ...); Hub.RegisterCallback(delivery, r => { ... })` |
| `var n = await mesh.GetMeshNodeStream(p).Take(1).ToTask()` | `mesh.GetMeshNodeStream(p).Subscribe(n => UpdateState(n))` |
| `return Task.FromResult(_suggestions.ToArray())` | bind directly to `_suggestions`; let the `Subscribe` push updates |
| `_ = LoadAsync(); await ...` | sync method that fires `Subscribe` |

Lifecycle wiring:
- `OnParametersSet` (sync) — kick off `Refresh*()`; never `OnParametersSetAsync` for mesh reads.
- Click handlers — `() => { svc.Op().Subscribe(r => UpdateState(r)); }`; never `async ctx => await svc.Op()`.
- `Dispose` — clean up all `IDisposable` subscriptions to stop emissions after the component unmounts.

## 🚨 Copy / recursive subtree operations — `ObserveQuery` + `.Select(CreateNode)`

Recursive node operations (Copy, and Move which is Copy + Delete) MUST stay in the observable world end to end. **Never** read source content via `GetRemoteStream<MeshNode, MeshNodeReference>` for this — the remote stream subscribes to the owning per-node hub, which may not be activated yet for newly-created nodes, and the subscription waits indefinitely. **Never** use `await meshService.QueryAsync(...)` either — drop into the observable world via `ObserveQuery`.

The canonical shape for Copy:

```csharp
meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
        $"path:{sourcePath} scope:subtree"))
    .Take(1)                                       // initial result set: source + descendants + satellites
    .Timeout(TimeSpan.FromSeconds(15))
    .SelectMany(change =>
    {
        var nodes = change.Items;
        var sourceNode = nodes.FirstOrDefault(n => n.Path == sourcePath);
        if (sourceNode is null)
        {
            hub.Post(CopyNodeResponse.Fail("Source not found", NodeCopyRejectionReason.SourceNotFound),
                o => o.ResponseFor(request));
            return Observable.Empty<MeshNode>();
        }

        var others = nodes.Where(/* IncludeDescendants / IncludeSatellites filter */);

        return meshService.CreateNode(RetargetNode(sourceNode, sourcePath, targetPath))
            .SelectMany(rootCreated => others.ToObservable()
                .Select(n => RetargetNode(n, sourcePath, targetPath))
                .SelectMany(retargeted => meshService.CreateNode(retargeted))   // ← Select(create)
                .ToList()
                .Select(_ => rootCreated));
    })
    .Subscribe(
        rootCreated => hub.Post(CopyNodeResponse.Ok(rootCreated), o => o.ResponseFor(request)),
        ex          => hub.Post(CopyNodeResponse.Fail(ex.Message),  o => o.ResponseFor(request)));
```

Move uses Copy then Delete — the Delete only fires after Copy completes (SelectMany short-circuits on Copy error):

```csharp
meshService.CopyNode(source, target, includeDescendants: true, includeSatellites: true)
    .SelectMany(copied => meshService.DeleteNode(source).Select(_ => copied))
    .Subscribe(
        movedNode => hub.Post(MoveNodeResponse.Ok(movedNode), o => o.ResponseFor(request)),
        ex        => hub.Post(MoveNodeResponse.Fail(ex.Message), o => o.ResponseFor(request)));
```

**Why:**
- `ObserveQuery` is observable from the start — no `await`, no `Observable.FromAsync` bridge over `QueryAsync`.
- The initial emission contains the full subtree snapshot we need to copy.
- `meshService.CreateNode(...).SelectMany(...)` chain ensures each child create completes before we move on. `.ToList()` aggregates all child completions before propagating success.
- `Move = Copy + Delete` keeps Move's handler trivial (no per-node logic, no subtree iteration); the Copy handler owns the recursion.
- `IncludeSatellites`/`IncludeDescendants` flags on `CopyNodeRequest` let callers opt in. By default Copy includes descendants but not satellites — Move sets both `true` to hard-move activity logs, comments, etc.

## 🚨 Updating a remote MeshNode — `GetRemoteStream` + `stream.Update`

For mutations to a `MeshNode` that lives at **another hub's address**, the only correct primitive is the remote stream's `.Update(...)`:

```csharp
// ✅ Right — push the patch to the owning hub via the synchronization stream.
var remote = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(targetPath), new MeshNodeReference());
remote.Update(current => current with { Name = "Renamed", LastModifiedBy = me });
```

`stream.Update` ships the patch through the synchronization protocol; the owning hub validates it, runs its node validators, writes to persistence, and republishes to all subscribers. The caller never touches `IMeshStorage`, never calls `GetNodeAsync`, never calls `SaveNodeAsync`.

### Persistence belongs in MeshDataSource init — nowhere else

`IMeshStorage` is loaded **once**, during `MeshDataSource` initialization, to populate the workspace. After init, the workspace is the source of truth. Every read/write goes through reactive streams. **No handler ever calls `persistence.GetNodeAsync` or `persistence.SaveNode`** — not even as a fallback, not even as a one-liner.

The wrong patterns (every line is a deadlock or a stale-content bug):

```csharp
// ❌ Reading existing state via persistence in a handler.
var existing = await persistence.GetNodeAsync(path, ct);

// ❌ "Fallback" to persistence when the workspace stream is empty.
//    The workspace being empty means the data isn't loaded — the fix is to
//    fix MeshDataSource init, not to bypass the workspace.
var obs = workspace.GetStream<MeshNode>() != null
    ? workspace.GetMeshNodeStream(path)
    : Observable.FromAsync(ct => persistence.GetNodeAsync(path, ct));

// ❌ Writing to a remote node by reaching into persistence directly.
//    Bypasses the owning hub's validators, version tracking, change feed,
//    and post-write hooks.
await persistence.SaveNode(node);
```

The right patterns:

| Operation | Primitive |
|---|---|
| Read own MeshNode | `workspace.GetMeshNodeStream()` |
| Read MeshNode at any path | `workspace.GetMeshNodeStream(path)` (auto-dispatches own / local collection / remote) |
| Update remote MeshNode | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference()).Update(current => updated)` |
| Update own MeshNode (in handler) | `workspace.UpdateMeshNode(node => updated)` |
| Create node | `meshService.CreateNode(node).Subscribe(...)` |
| Delete node | `meshService.DeleteNode(path).Subscribe(...)` |

Symptom of getting this wrong: persistence reads from a non-owning hub return stale content (workspace had a fresh value the persistence layer hadn't flushed yet); persistence writes from a non-owning hub silently skip validators, fail to update version history, and let other subscribers read stale workspace state.

## Workspace Updates (Non-Blocking)

`workspace.UpdateMeshNode` applies an update function to the current node state. It posts the update to the data stream — no blocking, no subscription:

```csharp
// Read current state and update atomically — no stream subscription needed
workspace.UpdateMeshNode(node =>
{
    var content = node.Content as MyContent ?? new MyContent();
    return node with
    {
        Content = content with { Status = "updated" }
    };
});
```

## Rules Summary

| Pattern | Safe in Handlers? | Notes |
|---------|-------------------|-------|
| `hub.Post(...)` | Yes | Fire-and-forget, safe from any thread |
| `hub.RegisterCallback(...)` | Yes | Non-blocking callback registration |
| `meshService.CreateNode(...).Subscribe()` | Yes | Fire-and-forget, no callback logic |
| `workspace.UpdateMeshNode(...)` in handler body | Yes | Runs on grain scheduler |
| `workspace.UpdateMeshNode(...)` in Subscribe callback | **NO** | Wrong thread in Orleans, deadlocks |
| `meshService.QueryAsync(...)` | **NO** | Blocks waiting for response |
| `await hub.AwaitResponse(...)` | **NO** | Deadlocks the hub scheduler |
| `await someTask` | **NO** | Blocks the hub scheduler |
| `hub.InvokeAsync(...)` | **NO** | Schedules on potentially blocked scheduler |
| `stream.Subscribe(...)` | **Risky** | May deadlock if stream observes on hub scheduler |

## When async/await IS Safe

`async/await` is safe in contexts that don't run on the hub's scheduler:
- Blazor component event handlers (`OnClick`, `OnInitializedAsync`)
- HTTP middleware and API controllers
- Background services and hosted services
- Test code

The rule is simple: **if your code runs inside a hub message handler (registered via `.WithHandler<T>()`), never await.**

## Blocking Execution (AI Streaming)

Sometimes you genuinely need long-running I/O — for example, streaming an AI response. This uses a **hosted hub** (`_Exec`) that runs the blocking work on its own thread via `hub.InvokeAsync`. But even here:

- All **state updates** (workspace, thread content) go through the **parent hub** (`parentHub.GetWorkspace().UpdateMeshNode(...)`, `parentHub.Post(...)`)
- All **messages** go through the parent hub — never post to the execution hub
- The execution hub is purely for hosting the blocking I/O — it should never own state

```csharp
// In HandleSubmitMessage (runs on thread hub):
var executionHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), ...);
executionHub.Post(request);  // Only message to execution hub: start the work

// In ExecuteMessageAsync (runs on _Exec hub):
var parentHub = hub.Configuration.ParentHub!;
parentHub.Post(new UpdateThreadMessageContent { ... });  // State via parent hub
parentHub.GetWorkspace().UpdateMeshNode(...);              // Workspace via parent hub
```

The parent hub's scheduler is free (the handler returned `delivery.Processed()` immediately). State updates and callbacks process normally on it.
