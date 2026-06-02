---
NodeType: Markdown
Name: "Blazor Data Binding — How to Bind a View Without Awaiting"
Abstract: "The canonical pattern for Blazor views in MeshWeaver: subscribe in sync lifecycle methods, never await on hub-touching operations. Hold the stream as a field, render from local state populated by the Subscribe callback, and dispose on tear-down."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#6a1b9a'/><path d='M8 12a3 3 0 013-3h1v2h-1a1 1 0 100 2h1v2h-1a3 3 0 01-3-3z' fill='white'/><path d='M16 12a3 3 0 00-3-3h-1v2h1a1 1 0 110 2h-1v2h1a3 3 0 003-3z' fill='white'/><rect x='10' y='11' width='4' height='2' fill='white'/></svg>"
Tags:
  - "Architecture"
  - "Blazor"
  - "DataBinding"
  - "Reactive"
---

Blazor views in MeshWeaver follow one rule above all others: **subscribe, never await**. This page explains that rule, shows the canonical binding shapes, and lists the anti-patterns to delete on sight.

## The Rule

> **Blazor lifecycle methods are synchronous. Views subscribe to streams; they never await hub-touching operations.**

The Blazor circuit dispatcher and the mesh hub schedulers share the same message-pump infrastructure. When a lifecycle method awaits `pathResolver.ResolvePath(...)`, `hub.GetMeshNode(...)`, `meshService.QueryAsync(...)`, or any bridged `IObservable<T>.ToTask()`, the dispatcher blocks — waiting for a response that can only arrive through the same dispatcher it just blocked. Under any real load, this deadlocks deterministically.

There is no nuance, no "short helper" exception. The pattern is:

**subscribe → store the result in a field → call `StateHasChanged`**

For the full explanation of why `await` deadlocks hub-touching operations, see [Asynchronous Calls](AsynchronousCalls).

<svg viewBox="0 0 760 380" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
<defs>
<marker id="bdb-arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
</marker>
<marker id="bdb-arr-green" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#43a047"/>
</marker>
<marker id="bdb-arr-red" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#e53935"/>
</marker>
<marker id="bdb-arr-blue" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="#1e88e5"/>
</marker>
</defs>
<text x="200" y="22" text-anchor="middle" fill="#e53935" font-weight="bold" font-size="13">❌ async/await — Deadlock</text>
<text x="560" y="22" text-anchor="middle" fill="#43a047" font-weight="bold" font-size="13">✅ Subscribe — Safe</text>
<line x1="380" y1="10" x2="380" y2="370" stroke="currentColor" stroke-opacity="0.2" stroke-width="1" stroke-dasharray="4,4"/>
<rect x="60" y="38" width="280" height="36" rx="10" fill="#1e88e5"/>
<text x="200" y="56" text-anchor="middle" fill="#fff" font-weight="bold">Blazor Circuit Dispatcher</text>
<text x="200" y="70" text-anchor="middle" fill="#fff" font-size="11">(single logical thread per circuit)</text>
<rect x="60" y="108" width="280" height="36" rx="10" fill="#5c6bc0"/>
<text x="200" y="123" text-anchor="middle" fill="#fff">OnParametersSetAsync()</text>
<text x="200" y="139" text-anchor="middle" fill="#fff" font-size="11">await hub.GetMeshNode(path)</text>
<line x1="200" y1="74" x2="200" y2="105" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#bdb-arr)"/>
<rect x="60" y="168" width="280" height="36" rx="10" fill="#7b1fa2"/>
<text x="200" y="183" text-anchor="middle" fill="#fff">Dispatcher BLOCKED</text>
<text x="200" y="199" text-anchor="middle" fill="#fff" font-size="11">waiting for hub response</text>
<line x1="200" y1="144" x2="200" y2="165" stroke="#e53935" stroke-width="1.5" marker-end="url(#bdb-arr-red)"/>
<rect x="60" y="228" width="280" height="36" rx="10" fill="#546e7a"/>
<text x="200" y="243" text-anchor="middle" fill="#fff">Hub response arrives</text>
<text x="200" y="259" text-anchor="middle" fill="#fff" font-size="11">→ needs dispatcher to continue</text>
<line x1="200" y1="204" x2="200" y2="225" stroke="#e53935" stroke-width="1.5" marker-end="url(#bdb-arr-red)"/>
<rect x="90" y="288" width="220" height="28" rx="8" fill="#b71c1c"/>
<text x="200" y="307" text-anchor="middle" fill="#fff" font-weight="bold">🔴 DEADLOCK</text>
<line x1="200" y1="264" x2="200" y2="285" stroke="#e53935" stroke-width="1.5" marker-end="url(#bdb-arr-red)"/>
<rect x="410" y="38" width="300" height="36" rx="10" fill="#1e88e5"/>
<text x="560" y="56" text-anchor="middle" fill="#fff" font-weight="bold">Blazor Circuit Dispatcher</text>
<text x="560" y="70" text-anchor="middle" fill="#fff" font-size="11">(single logical thread per circuit)</text>
<rect x="410" y="108" width="300" height="36" rx="10" fill="#5c6bc0"/>
<text x="560" y="123" text-anchor="middle" fill="#fff">OnParametersSet() — sync</text>
<text x="560" y="139" text-anchor="middle" fill="#fff" font-size="11">stream.Subscribe(onNext)</text>
<line x1="560" y1="74" x2="560" y2="105" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#bdb-arr)"/>
<rect x="410" y="168" width="300" height="36" rx="10" fill="#26a69a"/>
<text x="560" y="183" text-anchor="middle" fill="#fff">Returns immediately</text>
<text x="560" y="199" text-anchor="middle" fill="#fff" font-size="11">Dispatcher FREE — circuit responsive</text>
<line x1="560" y1="144" x2="560" y2="165" stroke="#43a047" stroke-width="1.5" marker-end="url(#bdb-arr-green)"/>
<rect x="410" y="228" width="140" height="36" rx="10" fill="#f57c00"/>
<text x="480" y="243" text-anchor="middle" fill="#fff">Stream emits</text>
<text x="480" y="259" text-anchor="middle" fill="#fff" font-size="11">node / data value</text>
<line x1="480" y1="204" x2="480" y2="225" stroke="#43a047" stroke-width="1.5" marker-end="url(#bdb-arr-green)"/>
<rect x="570" y="228" width="140" height="36" rx="10" fill="#5c6bc0"/>
<text x="640" y="243" text-anchor="middle" fill="#fff">_field = value</text>
<text x="640" y="259" text-anchor="middle" fill="#fff" font-size="11">InvokeAsync(StateHasChanged)</text>
<line x1="552" y1="246" x2="573" y2="246" stroke="#43a047" stroke-width="1.5" marker-end="url(#bdb-arr-green)"/>
<rect x="460" y="292" width="200" height="36" rx="8" fill="#1b5e20"/>
<text x="560" y="307" text-anchor="middle" fill="#fff" font-weight="bold">✅ View re-renders</text>
<text x="560" y="321" text-anchor="middle" fill="#fff" font-size="11">with fresh data</text>
<line x1="560" y1="264" x2="560" y2="289" stroke="#43a047" stroke-width="1.5" marker-end="url(#bdb-arr-green)"/>
<line x1="640" y1="264" x2="640" y2="278" stroke="#43a047" stroke-width="1" stroke-dasharray="3,3"/>
<line x1="640" y1="278" x2="564" y2="278" stroke="#43a047" stroke-width="1" stroke-dasharray="3,3"/>
<line x1="564" y1="278" x2="564" y2="289" stroke="#43a047" stroke-width="1" stroke-dasharray="3,3" marker-end="url(#bdb-arr-green)"/>
<text x="380" y="365" text-anchor="middle" fill="currentColor" fill-opacity="0.45" font-size="11">Subscription stored as field; released in Dispose()</text>
</svg>

*Blazor circuit binding: `await` stalls the single-thread dispatcher so the hub response can never be delivered — subscribing synchronously keeps the circuit free and lets the stream push updates via `InvokeAsync(StateHasChanged)`.*

## The Canonical Shape

Every Blazor view that needs hub data follows this skeleton:

```csharp
public partial class MyView : ComponentBase, IDisposable
{
    [Inject] private IPathResolver PathResolver { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;

    [Parameter] public string? Path { get; set; }

    // Local state — populated by Subscribe callbacks, rendered directly by the view.
    private AddressResolution? _resolution;
    private MeshNode? _node;
    private string? _error;
    private bool _isLoading;

    // Disposable subscriptions stored as fields — released on tear-down.
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

        // Cancel any previous in-flight subscription before starting a new one.
        _resolveSub?.Dispose();
        _nodeSub?.Dispose();
        _resolution = null;
        _node = null;
        _isLoading = true;

        // Subscribe — never await. The callback runs on whichever scheduler the
        // resolver completes on; InvokeAsync(StateHasChanged) marshals back to Blazor.
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

The view binds directly to `_resolution`, `_node`, `_error`, and `_isLoading` — not to a `Task<T>` callback, not via `await`. The Subscribe callback updates the fields and triggers a re-render.

## Lifecycle Method Reference

| Lifecycle | Correct use | Avoid |
|---|---|---|
| Initial load (no parameters) | `OnInitialized` (sync) + Subscribe | `OnInitializedAsync` for hub work |
| Parameter changes | `OnParametersSet` (sync) + Subscribe | `OnParametersSetAsync` for hub work |
| After render (DOM access) | `OnAfterRenderAsync` — but **no hub awaits inside** | `await` on any hub round-trip |
| Click action | Sync handler returning `Task.CompletedTask` + Subscribe | `async ctx => await myService.DoX()` |
| Disposal | `Dispose()` releases all held subscriptions | Leaving `_sub` un-disposed |

`OnAfterRenderAsync` is the only lifecycle hook it is safe to leave `async` — but exclusively for **DOM-side** work (`JSRuntime.InvokeAsync`, `ElementReference.FocusAsync`, etc.). Never await a mesh-touching operation inside it.

## Live Data Binding — Staying Subscribed for Updates

Path resolution is a one-shot operation: once the path resolves it does not change while the parameters remain the same. For **live** data — a node whose content updates while the view is on screen — use `GetRemoteStream<MeshNode, MeshNodeReference>` and keep the subscription open:

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

The view re-renders on every change pushed to the node — ideal for editors, dashboards, and collaborative views where the user sees content live.

> **Never `.Take(1)` on a display stream.** That snapshots the first value and unsubscribes; the view stops reflecting updates from that point on.

## Multi-Source Streams with a Loading Indicator

When a view fans out across several producers and wants to show a spinner until **all** of them finish, the reactive shape is: each producer emits 0–N values and then completes; the view subscribes once, sets `_isInflight = true` on entry, and clears it in `Finally`. The chat-completion orchestrator ([ChatCompletionOrchestrator](../../../MeshWeaver.Hosting/Completion/ChatCompletionOrchestrator.cs)) is the canonical example.

```csharp
public partial class ChatInputView : ComponentBase, IDisposable
{
    [Inject] private IChatCompletionOrchestrator Completions { get; set; } = null!;

    private bool _isCompletionsInflight;          // Drives the spinner
    private IReadOnlyList<CompletionItem> _items = [];
    private IDisposable? _sub;
    private bool _isDisposed;

    private void RunCompletions(string query)
    {
        _sub?.Dispose();
        SetInflight(true);

        _sub = Completions
            // Returns IObservable<CompletionBatch> directly — no IAsyncEnumerable bridge.
            .GetCompletions(query, currentNamespace: _ns)
            .SelectMany(batch => batch.Items.Select(ToItem))
            .ScanTopN(50, ItemSortComparer)

            // ⬇ DistinctUntilChanged collapses redundant snapshots so a producer that
            //    completes WITHOUT changing the visible top-N doesn't trigger an extra
            //    StateHasChanged. Use a stable key — reference equality fails because
            //    every Scan emits a fresh list instance.
            .DistinctUntilChanged(SnapshotKey)

            // ⬇ Finally fires on normal completion, error, AND early disposal,
            //    so the spinner always clears no matter how the stream ends.
            .Finally(() => SetInflight(false))

            .Subscribe(
                snapshot =>
                {
                    _items = snapshot;
                    if (!_isDisposed) InvokeAsync(StateHasChanged);
                },
                ex => Logger.LogError(ex, "Completions failed"));
    }

    private void SetInflight(bool value)
    {
        if (_isCompletionsInflight == value) return;   // manual DistinctUntilChanged
        _isCompletionsInflight = value;
        if (!_isDisposed) InvokeAsync(StateHasChanged);
    }

    private static string SnapshotKey(IReadOnlyList<CompletionItem> items) =>
        string.Join('', items.Select(i => i.SortKey ?? i.Label ?? ""));

    public void Dispose() { _isDisposed = true; _sub?.Dispose(); }
}
```

```razor
@if (_isCompletionsInflight)
{
    <FluentProgressRing Title="Loading suggestions…" />
}
@foreach (var item in _items) { ... }
```

### Why Each Primitive Was Chosen

| Concern | Reactive primitive | Rationale |
|---|---|---|
| Stream lifecycle | `IObservable<T>` end-to-end | `OnCompleted` is the natural "all sources done" signal — no `ProducerTracker`, no `ChannelWriter`, no `TaskCompletionSource`. |
| In-flight indicator | Subscribe sets flag + `Finally(() => SetInflight(false))` | Symmetric: subscription flips it on; any terminal notification flips it off. Catches normal completion, error, and early dispose. |
| Redundant updates | `DistinctUntilChanged(KeySelector)` | Each `Scan` emits a fresh list, so reference comparison is meaningless. A stable string key over the item content makes the comparator effective. |
| Cross-producer merge | `Observable.Merge(a, b).Concat(Defer(maybeC))` | Merge fans out A and B; Concat starts C only after Merge completes; Defer captures accumulated state at C-start time. No locks, no Subjects. |

### Avoid Bridging `IObservable` to `IAsyncEnumerable`

If the orchestrator already returns an `IObservable<T>`, subscribe to it directly — do not round-trip through `IAsyncEnumerable`:

```csharp
// ❌ WRONG — converts to IAsyncEnumerable and back, loses OnCompleted timing,
//    forces an extra Channel hop, and doesn't compose with DistinctUntilChanged.
return Completions.GetCompletionsAsync(query, _ns)
    .ToObservableSequence()
    .ScanTopN(...)
    .Subscribe(...);

// ✅ RIGHT — the orchestrator IS an IObservable; subscribe directly.
return Completions.GetCompletions(query, _ns)
    .ScanTopN(...)
    .DistinctUntilChanged(SnapshotKey)
    .Subscribe(...);
```

## Click Handlers — Sync + Subscribe

Click handlers must return `Task.CompletedTask` synchronously and compose their work as an observable chain. The `async ctx => await ...` form awaits hub work and deadlocks for the same reason lifecycle methods do.

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

## Why `await` Deadlocks the Blazor Circuit

The circuit dispatcher processes UI events sequentially on a single logical thread — the SignalR connection's pump. When a lifecycle method awaits a Task, the dispatcher stalls. If the awaited work needs the dispatcher to make further progress (even indirectly, via a hub round-trip whose response must flow back through the same dispatcher), the system deadlocks.

The symptoms are easy to recognise and hard to pin down without knowing the cause:

- The page hangs forever on a "Loading…" spinner.
- The user navigates away and then cannot navigate again.
- Production load tests pass; the first concurrent user breaks everything.
- The trace shows the awaited Task is still pending while response messages pile up in the hub's ActionBlock.

The cure is structural: **eliminate the await**. Tactical workarounds all fail:

- `Task.Run` — loses identity, hides exceptions, still races the dispatcher.
- A timeout — exposes the bug as a different symptom, does not fix it.
- Moving the await to `OnAfterRenderAsync` — same dispatcher, same deadlock.

Delete the `await`. Subscribe instead.

## Anti-Patterns to Delete on Sight

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

// ❌ WRONG — .Take(1) on a display stream.
//    Snapshots the first value and unsubscribes; the view freezes on first data.
workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())
    .Take(1).Subscribe(node => { ... });
```

## Pre-Merge Checklist

Before merging any Blazor view, verify each point:

1. **No `async Task OnInitializedAsync` / `OnParametersSetAsync` for hub work.** Use sync `OnInitialized` / `OnParametersSet` only.
2. **No `await` anywhere in the file** except inside `OnAfterRenderAsync` for pure DOM-side calls.
3. **No `.ToTask()`, `.FirstOrDefaultAsync()`, or `.AsTask()` on any mesh observable.**
4. **No `Task.Run` or fire-and-forget `_ = SomeAsync()`.** Subscribe instead — exceptions and cancellation flow through the observable chain.
5. **All held `IDisposable` subscriptions are stored as fields and disposed in `Dispose()`.** Un-disposed subscriptions leak across navigations.
6. **`StateHasChanged` is wrapped in `InvokeAsync(...)`** when called from a Subscribe callback, which may run on a non-UI thread.
7. **Display streams stay subscribed** (no `.Take(1)`). One-shot reads use `Hub.GetMeshNode(path)` — also without `.Take(1)`.

When in doubt: write the code with zero awaits and zero Task returns, then verify the view still updates correctly. If it does not, the missing piece is a Subscribe callback + `StateHasChanged` — never an `await`.

## Related Reading

- [Asynchronous Calls](AsynchronousCalls) — the full explanation of why `await` deadlocks hub-touching operations.
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — choosing the right read primitive for each situation.
- [Workspace References](WorkspaceReferences) — what each `WorkspaceReference<T>` shape emits and when to use each.
