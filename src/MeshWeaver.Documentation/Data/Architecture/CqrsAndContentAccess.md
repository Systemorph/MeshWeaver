---
NodeType: Markdown
Name: "CQRS — Queries, Reads, Writes, Operations"
Abstract: "Query only for finding sets of elements. For a specific node's content use GetDataRequest for a one-shot, GetRemoteStream for a live subscription. Writes go through PatchDataChangeRequest. Operations like 'run this script' are named request types handled on the owning node's hub — the implementation (e.g. the kernel) stays private."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#c62828'/><path d='M12 5v5M9 8l3-3 3 3' stroke='white' stroke-width='2' fill='none' stroke-linecap='round' stroke-linejoin='round'/><path d='M5 19l4-4M19 19l-4-4' stroke='white' stroke-width='2' stroke-linecap='round'/><circle cx='6' cy='18' r='1.5' fill='white'/><circle cx='18' cy='18' r='1.5' fill='white'/><circle cx='12' cy='12' r='1.5' fill='white'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "CQRS"
  - "Queries"
  - "Streams"
  - "Consistency"
---

## The five primitives

| Intent | Primitive |
|---|---|
| **Bind a UI control to a node's content** | Declare a path-bound control (e.g. `new MeshNodeThumbnailControl { NodePath = path }`) or `JsonPointerReference`. The Blazor view subscribes via `GetRemoteStream<MeshNode, MeshNodeReference>` — **layout-area code never loads the node**. See [Data Binding](xref:GUI/DataBinding). |
| **Find a set of nodes** (existence, listing, search) | `mesh.ObserveQuery<T>(request)` — or `QueryAsync` for `IAsyncEnumerable` |
| **Read a known node's content (one-shot)** | `hub.Post(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(addr))` + `hub.Observe` |
| **Subscribe to a node's live updates** | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())` |
| **Write to a node** | `hub.Post(new PatchDataChangeRequest(...), o => o.WithTarget(addr))` (or `DataChangeRequest` for full updates) |
| **Perform an operation on a node** | Named request type handled on the owning hub — e.g. `ExecuteScriptRequest`, `MoveNodeRequest`, `ImportRequest` |

**Read this line twice:** *query only for sets*. A query that happens to return one row is still a query.

## Why queries are not for content

Queries route through a **read-side index** (a cached projection).  The index is eventually
consistent: there is a window — single-digit to tens of milliseconds in prod — where a
successful write is not yet reflected in query results. That's acceptable for browsing
and autocomplete but lethal for "read-your-writes" operations like Patch (read current
content → merge → write), auditing ("did my change take?"), or any decision flow.

> **Layout areas should bind, not fetch.** The whole lag problem disappears when the GUI
> subscribes directly to `GetRemoteStream<MeshNode, MeshNodeReference>` — the view shows
> the authoritative current state and re-renders on every change. See
> [Data Binding](xref:GUI/DataBinding) for the bind-by-path pattern.

`GetDataRequest(new MeshNodeReference())` goes to the **owning hub's workspace** — the
source of truth. No staleness. It also activates the hub if it was cold; you don't have
to pre-subscribe.

## 🚨 Query.Content is always stale — never read it

`mesh.ObserveQuery<MeshNode>` / `mesh.QueryAsync<MeshNode>` and the lower-level
`IStorageAdapter.Query(params string[] queries)` enumerate MeshNodes by reading the
read-side index. The returned objects technically include `.Content` — **but you
must never read it**. The catalog is eventually consistent and the `Content` column
lags every committed write by the index-refresh window.

**Rules — bright lines, no exceptions:**

| What you have | What you do | What you must NOT do |
|---|---|---|
| A query you want to enumerate paths / names / nodeTypes / icons | `await foreach (var n in adapter.Query(queries)) yield return n.Path;` (or `n.Name`, etc.) | Read `n.Content` |
| A known **path** and you want the live MeshNode | `workspace.GetMeshNodeStream(path)` (returns `IObservable<MeshNode?>` subscribed to the owning hub) | `adapter.Query($"path:{path}")` and read `.Content` |
| A known path and you want a one-shot read | `hub.Post(GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))` + `hub.Observe(...)` | Anything that goes through the index |
| Recursive operation on a subtree (Copy, Move, Delete, …) | `hub.Post(CopyNodeRequest / MoveNodeRequest / DeleteNodeRequest, o => o.WithTarget(new Address(sourcePath)))` — the owning hub uses `GetMeshNodeStream` internally for live state | Load every node in the subtree from the query result, then write each one |

**Treat `MeshNode.Content` on a query row as if the column doesn't exist.** Project
to the metadata you need (`Path`, `Name`, `NodeType`, `Icon`, `LastModified`,
`Version`, `State`) and stop. If your call site needs `Content`, you're at the
wrong layer — either reshape it to use `GetMeshNodeStream`, or send the work to
the owning hub via a named request type.

### The "send the work to the owning hub" pattern (Copy / Move / Delete)

Recursive operations on a subtree look superficially like "query → load each →
do something" — that's the pattern that leaks `Content` reads and stale state.
The correct shape sends one request to each affected node's hub, where the
handler uses `GetMeshNodeStream` (or the workspace's `MeshNodeReference`
reducer) to obtain the **authoritative** state before acting.

```csharp
// Caller — fires one request per descendant, never touches Content from the query.
public IObservable<Unit> DeleteSubtree(string rootPath, IMessageHub hub, IMeshService mesh) =>
    Observable.Create<Unit>(async (observer, ct) =>
    {
        // 1. Enumerate descendant PATHS only — never read .Content from the iteration.
        var paths = new List<string>();
        await foreach (var shell in mesh.QueryAsync<MeshNode>(
            $"namespace:{rootPath} scope:subtree").WithCancellation(ct))
            paths.Add(shell.Path);                         // ← project to path; .Content untouched.
        paths.Add(rootPath);

        // 2. Fan out: one DeleteNodeRequest per address. Each owning hub
        //    handles its own delete — uses workspace.GetMeshNodeStream(self)
        //    if it needs current state, NOT the stale catalog row.
        Observable.Merge(paths.Select(p =>
                hub.Observe(new DeleteNodeRequest(p),
                    o => o.WithTarget(new Address(p)))))
            .Subscribe(_ => { },
                       ex => observer.OnError(ex),
                       () => { observer.OnNext(Unit.Default); observer.OnCompleted(); });
    });
```

```csharp
// Handler — registered on the owning per-node hub. Reads its OWN content via
// the workspace's MeshNodeReference reducer (the source of truth), not via
// any storage adapter or query.
private static IMessageDelivery HandleCopyNodeRequest(
    IMessageHub hub, IMessageDelivery<CopyNodeRequest> request)
{
    var targetPath = request.Message.TargetPath;
    hub.GetWorkspace().GetStream(new MeshNodeReference())!
        .Select(change => change.Value)
        .Where(node => node is not null)
        .Take(1)
        .Subscribe(self =>
        {
            // Use `self` to materialise the target — never query for it.
            hub.Post(new CreateNodeRequest(self! with { /* re-target */ }),
                o => o.WithTarget(new Address("mesh")));
            hub.Post(CopyNodeResponse.Ok(self!), o => o.ResponseFor(request));
        });
    return request.Processed();
}
```

The `DeleteNodeRequest` / `MoveNodeRequest` / `CopyNodeRequest` types are
defined in `src/MeshWeaver.Mesh.Contract/CreateNodeRequest.cs`. They route to
the source-node's address (or to the mesh hub which forwards). The handler
**never** reaches back through the index for content — it reads its own state
through the workspace's `MeshNodeReference` reducer, which is the only
non-stale view of the node.

> **Summary in one line:** `Query` gives you paths and shells; `GetMeshNodeStream`
> gives you live content. There is no third channel.

## One-shot reads (`GetDataRequest` + `Observe`)

The canonical pattern for "give me this node's current MeshNode":

```csharp
var delivery = hub.Post(
    new GetDataRequest(new MeshNodeReference()),
    o => o.WithTarget(new Address(path)));

hub.Observe(delivery, (d, _) =>
{
    if (d is IMessageDelivery<GetDataResponse> response
        && response.Message.Data is MeshNode node)
    {
        // Use node.Content, node.Version, etc. — authoritative, no lag.
    }
    return Task.FromResult(d);
}, cancellationToken);
```

No `ObserveQuery`, no `await`, no `FromAsync` bridge. The target hub activates on
receipt of the message, responds with a `GetDataResponse` wrapping the current
`MeshNode`, and your callback fires.

## Live updates (`GetRemoteStream`)

Use when you want to *react* to writes — render a view, wait for a job to finish,
watch progress roll in.

```csharp
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(jobPath), new MeshNodeReference())
    .Where(change =>
        change.Value?.Content is JobStatus { State: "Done" or "Failed" })
    .Take(1)
    .Subscribe(final =>
        logger.LogInformation("Job finished: {State}",
            ((JobStatus)final.Value!.Content!).State));
```

The first emission is the current state; subsequent emissions arrive as the hub
applies writes. `Where(...).Take(1)` waits until a condition is true and then
completes — no polling.

Use this for "wait for a job to finish" / "stream progress" — never a polling
loop against a query.

## Writes (`PatchDataChangeRequest`)

Writes flow to the owning hub as data changes, not as node CRUD:

```csharp
hub.Post(
    new PatchDataChangeRequest(
        StreamId: targetAddress.ToString(),
        Version: expectedVersion,
        Change: new RawJson(patchJson),
        ChangeType: ChangeType.Patch,
        ChangedBy: userId),
    o => o.WithTarget(targetAddress));
```

Never go through `mesh.QueryAsync` + merge in memory + `mesh.UpdateNode`. The index
read is stale; the merge loses concurrent writes; the full-node replace overwrites
anything you didn't explicitly read. Let the owning hub apply the patch on its
authoritative state.

For full-node updates use `DataChangeRequest.WithUpdates(fullNode)`.

## Operations — named request types per intent

When you want to **do** something on a node (not read or write content), define a
named request type and handle it on the owning hub. The caller never sees the
implementation detail.

Example — **run a script on a Code node**. The caller doesn't know (or need to know)
that the Code hub dispatches to an internal kernel:

```csharp
// In MeshWeaver.Mesh.Contract — no MeshWeaver.Kernel reference!
public record ExecuteScriptRequest : IRequest<ExecuteScriptResponse>
{
    public string? SubmissionId { get; init; }
}

public record ExecuteScriptResponse
{
    public bool Success { get; init; }
    public string? SubmissionId { get; init; }
    public string? OutputAreaReference { get; init; }
    public string? Error { get; init; }
}
```

The Code node's hub registers a **synchronous** handler:

```csharp
// In CodeNodeType.HubConfiguration
config.WithHandler<ExecuteScriptRequest>(HandleExecuteScript)

private static IMessageDelivery HandleExecuteScript(
    IMessageHub hub, IMessageDelivery<ExecuteScriptRequest> request)
{
    // Synchronous workspace read — .Current is the latest committed state.
    var node = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;
    if (node?.Content is not CodeConfiguration code || !code.IsExecutable)
    {
        hub.Post(new ExecuteScriptResponse { Success = false, Error = "..." },
            o => o.ResponseFor(request));
        return request.Processed();
    }

    var submissionId = request.Message.SubmissionId ?? Guid.NewGuid().ToString("N");
    var kernelAddress = /* private — derived from hub.Address */;

    // Fire-and-forget dispatch to the (private) kernel.
    hub.Post(new SubmitCodeRequest(code.Code ?? "") { Id = submissionId },
        o => o.WithTarget(kernelAddress));

    hub.Post(new ExecuteScriptResponse
        {
            Success = true,
            SubmissionId = submissionId,
            OutputAreaReference = submissionId
        },
        o => o.ResponseFor(request));
    return request.Processed();
}
```

The caller just fires the request at the node and subscribes for progress:

```csharp
var delivery = hub.Post(
    new ExecuteScriptRequest(),
    o => o.WithTarget(new Address(codeNodePath)));

hub.Observe(delivery, (d, _) =>
{
    if (d is IMessageDelivery<ExecuteScriptResponse> resp && resp.Message.Success)
    {
        // Subscribe to the output area for progress — still no direct kernel reference.
        workspace.GetRemoteStream<UiControl, LayoutAreaReference>(
            new Address(codeNodePath),
            new LayoutAreaReference(resp.Message.OutputAreaReference!))
            .Subscribe(/* ... */);
    }
    return Task.FromResult(d);
});
```

**Rules for operation handlers:**
- Synchronous. No `.Subscribe` on a stream, no `await`, no `Observable.FromAsync`.
  Read `.Current?.Value` from the workspace stream (it's already populated at
  handler-invocation time).
- The target address is the **node** (`new Address(nodePath)`), never the
  implementation detail (kernel, persistence, etc.).
- The response is a *dispatch acknowledgement*, not a completion signal. For
  long-running work, expose an `OutputAreaReference` and let the caller subscribe
  via `GetRemoteStream`.

## Handlers: reactive chains, not `.Current`

Inside a `.WithHandler<TRequest>(...)` body the **handler body must not block**,
but reading state from a workspace stream is done **reactively** — compose with
`.Select(...)` / `.Where(...)` / `.Take(1)` / `.Subscribe(...)`. The Subscribe
callback fires once the stream emits; the handler returns `request.Processed()`
immediately and the callback later posts the actual response via
`hub.Post(response, o => o.ResponseFor(request))`.

**Never `.Current` / `.Current?.Value` on a stream.** `Current` is populated
after the stream has emitted its first value — inside a handler that just
triggered the hub's activation, the workspace hasn't loaded data yet and
`Current` is null. You will ship a wrong answer. The reactive chain avoids
this: Subscribe fires once the data is actually there.

```csharp
// ❌ NEVER
var node = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;

// ✅ ALWAYS
hub.GetWorkspace().GetStream(new MeshNodeReference())
    ?.Select(change => change.Value)
    .Where(node => node is not null)
    .Take(1)
    .Subscribe(node =>
    {
        // handler logic here — post the response inside this callback
        hub.Post(new MyResponse { /* ... */ }, o => o.ResponseFor(request));
    });
return request.Processed();   // handler returns immediately
```

| Inside a handler | OK? |
|---|---|
| `hub.Post(...)` — fire a message | ✅ sync |
| `hub.Observe(delivery, callback)` — register; callback fires later | ✅ sync |
| `workspace.UpdateMeshNode(fn)` — apply an update | ✅ sync |
| `hub.GetWorkspace().GetStream(ref)?.Select(...).Where(...).Take(1).Subscribe(...)` — reactive read | ✅ |
| `hub.GetWorkspace().GetStream(ref)?.Current?.Value` — snapshot read | ❌ null on cold workspaces |
| `await anything` | ❌ never |
| `Observable.FromAsync(...)` | ❌ hides an await — same bug |

## Quick decision matrix

| Intent | Primitive |
|---|---|
| List nodes under X (paths / metadata only) | `mesh.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(...))` — project to `Path` / `Name` / etc. **never read `.Content`** |
| Does node X exist? | `ObserveQuery` + check `Items.Count` |
| Give me node X's MeshNode (live) | `workspace.GetMeshNodeStream(X)` — the **only** non-stale read path |
| Give me node X's MeshNode (once) | `hub.Post(GetDataRequest(new MeshNodeReference()), WithTarget(X))` + `Observe` |
| Keep me updated on node X's MeshNode | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(X, new MeshNodeReference())` |
| Patch node X | `hub.Post(PatchDataChangeRequest(...), WithTarget(X))` |
| Replace node X wholesale | `hub.Post(DataChangeRequest{...}.WithUpdates(fullNode), WithTarget(X))` |
| Run the script on Code node X | `hub.Post(ExecuteScriptRequest(), WithTarget(X))` + `Observe<ExecuteScriptResponse>` |
| Wait until the run finishes | `workspace.GetRemoteStream` on X's output area until a terminal condition |
| Move/Copy node X (incl. subtree) | `hub.Post(MoveNodeRequest / CopyNodeRequest, WithTarget(X))` — owning hub reads its own state via `GetMeshNodeStream`, fans out per-child requests, never queries for content |
| Delete node X (incl. subtree) | `hub.Post(DeleteNodeRequest, WithTarget(X))` — recursive variant queries for **paths only** then fires one `DeleteNodeRequest` per descendant address |
| Stream content into node X during execution (AI streaming, long-running output) | Open `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(X, new MeshNodeReference())` once at start, push every delta via `.Update(node => node with { Content = ... })`, dispose at end. See [Thread Execution Streaming](xref:Architecture/ThreadExecutionStreaming) for the canonical writer + renderer pair. |

## Anti-patterns

```csharp
// ❌ Query to get content — stale read, lost-update risk.
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();
return JsonSerializer.Serialize(node);

// ❌ Same in reactive clothing — still a query, still stale.
return mesh.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
    .Take(1).Select(c => c.Items.FirstOrDefault());

// ❌ Reading Content while enumerating a query result — Content is stale.
await foreach (var n in mesh.QueryAsync<MeshNode>($"namespace:{parent} scope:subtree"))
{
    if (n.Content is JobStatus { State: "Done" }) { … }   // ← stale Content
}

// ❌ Wrapping QueryAsync in Observable.FromAsync does not fix consistency.
return Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct).AsTask());

// ❌ "Recursive operation" by loading every subtree node from a query.
//    Stale Content + N+1 + memory blow-up + bypasses per-node hub validators.
await foreach (var n in mesh.QueryAsync<MeshNode>(
    $"namespace:{root} scope:subtree"))
{
    storage.DeleteAsync(n.Path);            // ← uses stale n; bypasses hub
}

// ❌ Caller addressing the implementation detail (kernel) directly.
hub.Post(new SubmitCodeRequest(...), o => o.WithTarget(kernelAddress));

// ❌ Async in a handler body.
.WithHandler<FooRequest>(async (hub, req) => { await something; return req.Processed(); })

// ✅ Project to metadata only — `.Path` / `.Name` / `.NodeType`, never `.Content`.
await foreach (var shell in mesh.QueryAsync<MeshNode>(
    $"namespace:{parent} scope:subtree"))
    paths.Add(shell.Path);                  // never read shell.Content

// ✅ Need content for a known path? Subscribe to the owning hub.
workspace.GetMeshNodeStream(path)
    .Take(1)
    .Subscribe(node => { /* node.Content is live, no lag */ });

// ✅ Recursive operation — fan out one request per descendant address;
//    each owning hub does the work with its own live state.
Observable.Merge(paths.Select(p =>
        hub.Observe(new DeleteNodeRequest(p), o => o.WithTarget(new Address(p)))))
    .Subscribe(_ => { }, err => logger.LogError(err, "delete fan-out failed"));

// ✅ One-shot content read — authoritative.
var delivery = hub.Post(new GetDataRequest(new MeshNodeReference()),
    o => o.WithTarget(new Address(path)));
hub.Observe(delivery, (d, _) => { /* ... */ return Task.FromResult(d); });

// ✅ Live updates.
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(path), new MeshNodeReference());

// ✅ Named operation — caller never references the kernel.
hub.Post(new ExecuteScriptRequest(), o => o.WithTarget(new Address(codeNodePath)));
```

Related reading:
- [Asynchronous Calls](AsynchronousCalls) — the hub's single-threaded scheduler and
  why `await` deadlocks it.
- [Workspace references](WorkspaceReferences) — catalogue of `WorkspaceReference<T>`
  shapes and what each one emits.
- [Data access patterns](DataAccessPatterns) — which DI service to use for what.
