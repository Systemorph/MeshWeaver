---
NodeType: "Doc/Article"
Title: "CQRS — Queries, Reads, Writes, Operations"
Abstract: "Query only for finding sets of elements. For a specific node's content use GetDataRequest for a one-shot, GetRemoteStream for a live subscription. Writes go through PatchDataChangeRequest. Operations like 'run this script' are named request types handled on the owning node's hub — the implementation (e.g. the kernel) stays private."
Icon: "Split"
Published: "2026-04-23"
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
| **Read a known node's content (one-shot)** | `hub.Post(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(addr))` + `hub.RegisterCallback` |
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

## One-shot reads (`GetDataRequest` + `RegisterCallback`)

The canonical pattern for "give me this node's current MeshNode":

```csharp
var delivery = hub.Post(
    new GetDataRequest(new MeshNodeReference()),
    o => o.WithTarget(new Address(path)));

hub.RegisterCallback(delivery, (d, _) =>
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

hub.RegisterCallback(delivery, (d, _) =>
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
| `hub.RegisterCallback(delivery, callback)` — register; callback fires later | ✅ sync |
| `workspace.UpdateMeshNode(fn)` — apply an update | ✅ sync |
| `hub.GetWorkspace().GetStream(ref)?.Select(...).Where(...).Take(1).Subscribe(...)` — reactive read | ✅ |
| `hub.GetWorkspace().GetStream(ref)?.Current?.Value` — snapshot read | ❌ null on cold workspaces |
| `await anything` | ❌ never |
| `Observable.FromAsync(...)` | ❌ hides an await — same bug |

## Quick decision matrix

| Intent | Primitive |
|---|---|
| List nodes under X | `ObserveQuery` |
| Does node X exist? | `ObserveQuery` + check `Items.Count` |
| Give me node X's MeshNode (once) | `hub.Post(GetDataRequest(new MeshNodeReference()), WithTarget(X))` + `RegisterCallback` |
| Keep me updated on node X's MeshNode | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(X, new MeshNodeReference())` |
| Patch node X | `hub.Post(PatchDataChangeRequest(...), WithTarget(X))` |
| Replace node X wholesale | `hub.Post(DataChangeRequest{...}.WithUpdates(fullNode), WithTarget(X))` |
| Run the script on Code node X | `hub.Post(ExecuteScriptRequest(), WithTarget(X))` + `RegisterCallback<ExecuteScriptResponse>` |
| Wait until the run finishes | `workspace.GetRemoteStream` on X's output area until a terminal condition |
| Move/Copy node X | `hub.Post(MoveNodeRequest(...), WithTarget(X))` — same pattern, different request type |
| Stream content into node X during execution (AI streaming, long-running output) | Open `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(X, new MeshNodeReference())` once at start, push every delta via `.Update(node => node with { Content = ... })`, dispose at end. See [Thread Execution Streaming](xref:Architecture/ThreadExecutionStreaming) for the canonical writer + renderer pair. |

## Anti-patterns

```csharp
// ❌ Query to get content — stale read, lost-update risk.
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();
return JsonSerializer.Serialize(node);

// ❌ Same in reactive clothing — still a query, still stale.
return mesh.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
    .Take(1).Select(c => c.Items.FirstOrDefault());

// ❌ Wrapping QueryAsync in Observable.FromAsync does not fix consistency.
return Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct).AsTask());

// ❌ Caller addressing the implementation detail (kernel) directly.
hub.Post(new SubmitCodeRequest(...), o => o.WithTarget(kernelAddress));

// ❌ Async in a handler body.
.WithHandler<FooRequest>(async (hub, req) => { await something; return req.Processed(); })

// ✅ One-shot content read — authoritative.
var delivery = hub.Post(new GetDataRequest(new MeshNodeReference()),
    o => o.WithTarget(new Address(path)));
hub.RegisterCallback(delivery, (d, _) => { /* ... */ return Task.FromResult(d); });

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
