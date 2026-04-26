---
NodeType: "Doc/Article"
Title: "Blazor Data Binding — How to Bind a View Without Awaiting"
Abstract: "The canonical pattern for Blazor views in MeshWeaver: subscribe in sync lifecycle methods, never await on hub-touching operations. Hold the stream as a field, render from local state populated by the Subscribe callback, dispose on tear-down."
Icon: "Link"
Published: "2026-04-26"
Tags:
  - "Architecture"
  - "Blazor"
  - "DataBinding"
  - "Reactive"
---

## The rule

> **Blazor lifecycle methods are synchronous. The view subscribes to streams, never awaits.**
> Every `await` on a hub-touching operation in `OnInitializedAsync` / `OnParametersSetAsync` / `OnAfterRenderAsync` / async click handlers is a 100% deadlock surface (see [AsynchronousCalls](AsynchronousCalls)).

The Blazor circuit dispatcher and the mesh hub schedulers share message-pump infrastructure. Awaiting on `pathResolver.ResolvePath(...)`, `hub.GetMeshNode(...)`, `meshService.QueryAsync(...)`, or any bridged `IObservable<T>.ToTask()` blocks the dispatcher waiting for a response that has to flow back through the same dispatcher. Under load this deadlocks deterministically.

There is no nuance, no "small helper" exception, no "but my callback chain is short". The pattern is: **subscribe, store the result in a field, call `StateHasChanged`**.

## The canonical shape

```csharp
public partial class MyView : ComponentBase, IDisposable
{
    [Inject] private IPathResolver PathResolver { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;

    [Parameter] public string? Path { get; set; }

    // Local state — populated by the Subscribe callback, rendered by the view.
    private AddressResolution? _resolution;
    private MeshNode? _node;
    private string? _error;
    private bool _isLoading;

    // Disposable subscriptions held as fields — disposed on tear-down.
    private IDisposable? _resolveSub;
    private IDisposable? _nodeSub;

    // ✅ SYNCHRONOUS lifecycle method — no async, no await.
    protected override void OnParametersSet()
    {
        if (string.IsNullOrEmpty(Path))
        {
            _isLoading = false;
            return;
        }

        // Cancel previous in-flight subscription before starting a new one.
        _resolveSub?.Dispose();
        _nodeSub?.Dispose();
        _resolution = null;
        _node = null;
        _isLoading = true;

        // Subscribe — never await. The callback runs on whichever scheduler the
        // resolver completes; InvokeAsync(StateHasChanged) marshals back to Blazor.
        _resolveSub = PathResolver.ResolvePath(Path)
            .Catch<AddressResolution?, Exception>(ex =>
            {
                _error = ex.Message;
                return Observable.Return<AddressResolution?>(null);
            })
            .Subscribe(resolution =>
            {
                _resolution = resolution;
                if (resolution != null)
                {
                    // Chain the next reactive step — fetch the node.
                    _nodeSub = Hub.GetMeshNode(resolution.Prefix, TimeSpan.FromSeconds(10))
                        .Subscribe(node =>
                        {
                            _node = node;
                            _isLoading = false;
                            InvokeAsync(StateHasChanged);
                        });
                }
                else
                {
                    _error = "Path not found";
                    _isLoading = false;
                    InvokeAsync(StateHasChanged);
                }
            });
    }

    public void Dispose()
    {
        _resolveSub?.Dispose();
        _nodeSub?.Dispose();
    }
}
```

The view binds to `_resolution`, `_node`, `_error`, `_isLoading` directly — not to a `Task<T>` callback, not via `await`. The Subscribe callback updates the fields and re-renders.

## Lifecycle method shapes

| Lifecycle | Use | Don't use |
|---|---|---|
| Initial load (no parameters) | `OnInitialized` (sync) + Subscribe | `OnInitializedAsync` for hub work |
| Parameter changes | `OnParametersSet` (sync) + Subscribe | `OnParametersSetAsync` for hub work |
| After render (DOM access) | `OnAfterRenderAsync` (async) — but **no hub awaits** | `await` on hub round-trips here |
| Click action | sync handler returning `Task.CompletedTask` + Subscribe | `async ctx => await myService.DoX()` |
| Disposal | `Dispose` releases held subscriptions | leaving `_sub` un-disposed |

`OnAfterRenderAsync` is the only async lifecycle method that's safe to leave async — but only for **DOM-side** work (`JSRuntime.InvokeAsync`, `ElementReference.FocusAsync`, etc.). Never await a mesh-touching operation in it.

## Live data binding — `GetRemoteStream` for re-render on every change

Path resolution is one-shot — once the path resolves, the result doesn't change while the parameters stay the same. For **live** data (a node whose content updates while the view is rendered), use `GetRemoteStream<MeshNode, MeshNodeReference>` and stay subscribed:

```csharp
private ISynchronizationStream<MeshNode>? _nodeStream;
private string? _renderedHtml;

protected override void OnParametersSet()
{
    if (string.IsNullOrEmpty(BoundNodePath)) return;

    _nodeStream?.Dispose();
    _nodeStream = Hub.GetWorkspace().GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(BoundNodePath), new MeshNodeReference());

    // Held subscription — disposed in Dispose().
    _renderSub = _nodeStream
        .Where(change => change.Value != null)
        .Select(change => change.Value!.PreRenderedHtml)
        .DistinctUntilChanged()
        .Subscribe(html =>
        {
            _renderedHtml = html;
            InvokeAsync(StateHasChanged);
        });
}
```

The view re-renders on every change to the node. This is the right primitive for editors, dashboards, collaborative views, anywhere the user sees the content live. **Never `.Take(1)` on a long-standing display stream** — that snapshots and unsubscribes; the view stops updating.

## Click handlers — sync + Subscribe, never `async ctx => await ...`

```csharp
// ✅ RIGHT — sync click action, Subscribe inside.
.WithClickAction(ctx =>
{
    // Optimistic immediate feedback.
    ctx.Host.UpdateData("status", "<p>Working…</p>");

    // Read form data via Subscribe — never `await stream.FirstAsync()`.
    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>("form")
        .Take(1)
        .Subscribe(data =>
        {
            // Composable mesh ops — Subscribe, never bridge to Task.
            myService.DoWork(data!).Subscribe(
                result => ctx.Host.UpdateData("status", $"<p>Done: {result}</p>"),
                ex     => ctx.Host.UpdateData("status", $"<p>Error: {ex.Message}</p>"));
        });

    return Task.CompletedTask;  // ← click handler signature is sync
})

// ❌ WRONG — async click action, awaits hub work, deadlocks.
.WithClickAction(async ctx =>
{
    var data = await ctx.Host.Stream.GetDataStream<...>("form").FirstAsync();
    var result = await myService.DoWorkAsync(data);
    ctx.Host.UpdateData("status", result);
})
```

## Why `await` deadlocks the Blazor circuit

The circuit dispatcher runs UI events sequentially on a single logical thread (the SignalR connection's pump). When the lifecycle method awaits a Task, the dispatcher is blocked. If the awaited work needs the dispatcher to make progress — even indirectly, via a hub round-trip whose response is delivered through the same dispatcher — the system deadlocks.

Symptoms:
- The page hangs forever on a "Loading…" spinner.
- The user navigates, then can't navigate again.
- Production load tests pass; the first concurrent user crashes everything.
- The trace shows the awaited Task is still pending while the response messages pile up in the hub's ActionBlock.

The cure is structural, not tactical: **eliminate the await**. Don't wrap `Task.Run` around it (loses identity, hides exceptions). Don't add a timeout (just exposes the bug as a different symptom). Don't move it to `OnAfterRenderAsync` ("it's later, surely it's fine") — same dispatcher, same deadlock. **Delete the await; subscribe instead.**

## Anti-patterns to delete on sight

```csharp
// ❌ WRONG — Task bridge in lifecycle.
protected override async Task OnInitializedAsync()
{
    var resolution = await PathResolver.ResolvePath(Path).FirstAsync().ToTask();
    // ...
}

// ❌ WRONG — TaskCompletionSource fakes the bridge.
var tcs = new TaskCompletionSource<X>();
PathResolver.ResolvePath(Path).Subscribe(r => tcs.TrySetResult(r));
var resolution = await tcs.Task;     // same deadlock, more typing

// ❌ WRONG — async click handler.
.WithClickAction(async ctx => { await something; })

// ❌ WRONG — Task.Run "fix".
.WithClickAction(ctx =>
{
    _ = Task.Run(async () => { await myService.DoX(); });
    return Task.CompletedTask;
})

// ❌ WRONG — `.Take(1)` on a display stream.
//    Snapshots the first value and unsubscribes; view freezes.
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())
    .Take(1).Subscribe(node => { ... });
```

## Checklist before merging a Blazor view

1. **No `async Task OnInitializedAsync` / `OnParametersSetAsync` for hub work.** Sync `OnInitialized` / `OnParametersSet` only.
2. **No `await` anywhere in the file** (other than the body of `OnAfterRenderAsync` for pure DOM-side work).
3. **No `.ToTask()` / `.FirstOrDefaultAsync()` / `.AsTask()` on any mesh observable.**
4. **No `Task.Run` / `_ = SomeAsync()` fire-and-forget.** Subscribe instead — exceptions and cancellation flow through the observable.
5. **All held `IDisposable` subscriptions are stored as fields and disposed in `Dispose()`.** Otherwise stale subscriptions leak across navigations.
6. **`StateHasChanged` is wrapped in `InvokeAsync(...)`** when called from a Subscribe callback (which may be on a non-UI thread).
7. **Display streams stay subscribed** (no `.Take(1)`). One-shot reads use `Hub.GetMeshNode(path)` (also without `.Take(1)`).

When in doubt: write the code with **zero awaits and zero Task returns**, then check that the view still updates correctly. If it doesn't, the missing piece is a Subscribe callback + StateHasChanged — never an `await`.

## Related reading

- [Asynchronous Calls](AsynchronousCalls) — why `await` deadlocks the hub.
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — choose the right read primitive.
- [Workspace References](WorkspaceReferences) — what each `WorkspaceReference<T>` shape emits.
