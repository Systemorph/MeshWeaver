---
Name: Blazor Async
Description: "Practical playbook for using IObservable<T> with Subscribe in Blazor components instead of await, covering lifecycle hooks, click handlers, and forbidden patterns."
---
# Blazor Async — `Subscribe`, not `await`

> **TL;DR** Every mesh / hub call returns `IObservable<T>`. You `Subscribe(...)` to it — never `await`, never `.ToTask()`, never `.FirstAsync().ToTask()`, never `.FirstOrDefaultAsync()`. State updates from your Subscribe callback always go through `InvokeAsync(...)` so they run on the Blazor circuit's dispatcher.

This is the companion article to [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls), which covers deadlock semantics in hub-handler code. This page is the practical playbook for Blazor components, layout-area views, and click-action handlers.
<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#888"/>
    </marker>
    <marker id="arr-red" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#e53935"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="#43a047"/>
    </marker>
  </defs>
  <text x="190" y="22" text-anchor="middle" fill="#e53935" font-weight="bold" font-size="14">❌ await (wrong)</text>
  <text x="570" y="22" text-anchor="middle" fill="#43a047" font-weight="bold" font-size="14">✅ Subscribe (correct)</text>
  <line x1="380" y1="10" x2="380" y2="310" stroke="currentColor" stroke-opacity=".25" stroke-dasharray="6,4"/>
  <rect x="30" y="35" width="150" height="36" rx="8" fill="#5c6bc0"/>
  <text x="105" y="58" text-anchor="middle" fill="#fff">Blazor Component</text>
  <rect x="230" y="35" width="130" height="36" rx="8" fill="#1e88e5"/>
  <text x="295" y="58" text-anchor="middle" fill="#fff">Mesh Hub</text>
  <line x1="180" y1="53" x2="230" y2="53" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <text x="205" y="47" text-anchor="middle" fill="#e53935" font-size="11">await</text>
  <rect x="30" y="100" width="150" height="36" rx="8" fill="#5c6bc0" opacity=".5"/>
  <text x="105" y="119" text-anchor="middle" fill="#fff" opacity=".8">Dispatcher</text>
  <text x="105" y="133" text-anchor="middle" fill="#fff" opacity=".8">blocked / waiting</text>
  <rect x="230" y="100" width="130" height="36" rx="8" fill="#1e88e5" opacity=".5"/>
  <text x="295" y="119" text-anchor="middle" fill="#fff" opacity=".8">Response needs</text>
  <text x="295" y="133" text-anchor="middle" fill="#fff" opacity=".8">dispatcher ←</text>
  <line x1="295" y1="71" x2="295" y2="100" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <line x1="230" y1="118" x2="180" y2="118" stroke="#e53935" stroke-width="1.5" stroke-dasharray="4,3" marker-end="url(#arr-red)"/>
  <rect x="80" y="165" width="230" height="36" rx="8" fill="#e53935" opacity=".85"/>
  <text x="195" y="188" text-anchor="middle" fill="#fff" font-weight="bold">DEADLOCK</text>
  <line x1="105" y1="136" x2="155" y2="165" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <line x1="295" y1="136" x2="245" y2="165" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="410" y="35" width="150" height="36" rx="8" fill="#5c6bc0"/>
  <text x="485" y="58" text-anchor="middle" fill="#fff">Blazor Component</text>
  <rect x="595" y="35" width="130" height="36" rx="8" fill="#1e88e5"/>
  <text x="660" y="58" text-anchor="middle" fill="#fff">Mesh Hub</text>
  <line x1="560" y1="53" x2="595" y2="53" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <text x="578" y="47" text-anchor="middle" fill="#43a047" font-size="11">Subscribe</text>
  <rect x="410" y="100" width="150" height="36" rx="8" fill="#5c6bc0"/>
  <text x="485" y="118" text-anchor="middle" fill="#fff">Dispatcher</text>
  <text x="485" y="132" text-anchor="middle" fill="#fff">free — renders</text>
  <rect x="595" y="100" width="130" height="36" rx="8" fill="#1e88e5"/>
  <text x="660" y="118" text-anchor="middle" fill="#fff">emits value</text>
  <text x="660" y="132" text-anchor="middle" fill="#fff">on change</text>
  <line x1="595" y1="118" x2="560" y2="118" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <text x="578" y="113" text-anchor="middle" fill="#43a047" font-size="11">callback</text>
  <rect x="430" y="165" width="210" height="36" rx="8" fill="#26a69a"/>
  <text x="535" y="183" text-anchor="middle" fill="#fff">InvokeAsync(StateHasChanged)</text>
  <text x="535" y="197" text-anchor="middle" fill="#fff" font-size="11">— re-renders on every emission</text>
  <line x1="485" y1="136" x2="500" y2="165" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <line x1="660" y1="136" x2="620" y2="165" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <rect x="430" y="230" width="210" height="36" rx="8" fill="#43a047"/>
  <text x="535" y="249" text-anchor="middle" fill="#fff" font-weight="bold">Live, reactive UI</text>
  <text x="535" y="263" text-anchor="middle" fill="#fff" font-size="11">updates on every change</text>
  <line x1="535" y1="201" x2="535" y2="230" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
</svg>

*`await` blocks the Blazor dispatcher waiting for the hub — which needs the dispatcher to deliver the response — causing deadlock. `Subscribe` keeps the dispatcher free; the hub pushes values into the callback, which calls `InvokeAsync(StateHasChanged)` to re-render.*

---

## Why `await` is wrong in Blazor

There are two distinct failure modes — one is a deadlock, the other is a stale-data problem.

### 1. Deadlock — shared scheduling infrastructure

The Blazor circuit dispatcher and the mesh hub's `ActionBlock` both pump messages through scheduling primitives that, under load, can end up serialising work onto the same thread. Awaiting a hub round-trip inside a Blazor component method blocks the dispatcher waiting for a response that has to flow *back through that same dispatcher*. Under any concurrent load — multiple users, parallel queries from one page, even rapid keystrokes — this deadlocks deterministically.

This is the same root cause as the hub-handler deadlock described in [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls), with one extra layer: the awaiting context is the Blazor circuit rather than a hub `ActionBlock`. The deadlock surface is real even though the path is shorter.

### 2. Stale data — `Task<T>` is a snapshot, not a subscription

A `Task<T>` resolves once. If you bind your view to a `Task<T>` result, it shows the value captured at call time and never updates when the underlying mesh state changes. Even if the await *worked*, you'd see a stale snapshot the moment the data moves.

The correct primitive is `IObservable<T>` — it emits the initial value *and* every subsequent change. The view re-renders on each emission via `InvokeAsync(StateHasChanged)`.

---

## Canonical patterns

### Lifecycle: read live data, hold a subscription, dispose on tear-down

```csharp
public partial class MyView : ComponentBase, IDisposable
{
    private IDisposable? _sub;
    private MeshNode? _node;

    protected override void OnParametersSet()      // sync — never OnParametersSetAsync
    {
        _sub?.Dispose();
        // Shared per-path handle (IMeshNodeStreamCache) — same stream every
        // other reader and the writer use for this path.
        _sub = Hub.GetMeshNodeStream(BoundPath)
            .Subscribe(node =>
            {
                _node = node;
                InvokeAsync(StateHasChanged);
            });
    }

    public void Dispose() => _sub?.Dispose();
}
```

**Why `OnParametersSet` (sync) and not `OnParametersSetAsync`?** Async lifecycle hooks invite an `await` on the mesh observable inside the body, which triggers the deadlock from §1. The sync variant forces the `Subscribe` shape and removes the temptation entirely.

### Click handler: synchronous body, fire-and-forget Subscribe

```csharp
private void OnSaveClicked()
{
    MeshService.UpdateNode(_node!).Subscribe(
        updated => InvokeAsync(() =>
        {
            _saveResult = "Saved.";
            StateHasChanged();
        }),
        ex => InvokeAsync(() =>
        {
            _saveResult = $"Save failed: {ex.Message}";
            StateHasChanged();
        }));
}
```

Key rules:

- The handler returns `void` (or wraps in `Task.CompletedTask` for `EventCallback` signatures that require it) — never `async void`, never `async Task`.
- State mutation goes inside `InvokeAsync(...)` so it runs on the Blazor dispatcher.
- Both `onNext` and `onError` paths must update UI — never let `onError` fall on the floor.

### Multiple parallel queries: `CombineLatest` instead of `Task.WhenAll`

When a view needs N independent results before rendering, replace `Task.WhenAll(tasks)` with `CombineLatest`:

```csharp
private void LoadResults()
{
    var observables = queries.Select(q => MeshQuery
        .Query<MeshNode>(MeshQueryRequest.FromQuery(q))
        .Take(1)
        .Select(c => (IReadOnlyList<MeshNode>)c.Items)
        .Catch<IReadOnlyList<MeshNode>, Exception>(
            _ => Observable.Return<IReadOnlyList<MeshNode>>(new List<MeshNode>())));

    observables.CombineLatest()
        .Take(1)
        .Subscribe(allBatches => InvokeAsync(() =>
        {
            _results = allBatches.SelectMany(batch => batch).ToList();
            StateHasChanged();
        }));
}
```

`CombineLatest` waits for the first emission from every observable before firing once with the assembled tuple. Add `.Take(1)` to complete the chain after that single combined result arrives.

### Multi-step flow (e.g., export → ZIP → download)

When a click action does *hub work* followed by *non-hub work* (file I/O, JSInterop), there are two acceptable shapes.

**Shape A — split at the boundary.** The hub work is observable; the JS interop lives in a follow-on `async Task` invoked from the Subscribe callback:

```csharp
private void ExportClicked()
{
    ExportService.ExportToDirectory(SourcePath, tempDir, excluded)
        .Take(1)
        .Subscribe(
            result => _ = InvokeAsync(() => OnExportCompletedAsync(result, tempDir)),
            ex     => _ = InvokeAsync(() => OnExportFailed(ex.Message, tempDir)));
}

private async Task OnExportCompletedAsync(MeshExportResult result, string tempDir)
{
    // ZIP the directory, JSInterop download — no mesh awaits here.
    using var ms = new MemoryStream();
    ZipFile.CreateFromDirectory(tempDir, ms, CompressionLevel.Optimal, false);
    using var streamRef = new DotNetStreamReference(ms);
    await JSRuntime.InvokeVoidAsync("downloadFromStream", "export.zip", streamRef);
}
```

The `await JSRuntime.InvokeVoidAsync(...)` inside `OnExportCompletedAsync` is fine — JSInterop is not a hub round-trip.

**Shape B — chain inside the observable.** When follow-on work is synchronous or wrapped in `Observable.FromAsync` at a non-hub boundary:

```csharp
ExportService.ExportToDirectory(...)
    .SelectMany(result => result.Success
        ? Observable.FromAsync(ct => DoFileIo(result, ct))    // file I/O — sanctioned boundary
        : Observable.Throw<Unit>(new InvalidOperationException(result.Error)))
    .Subscribe(
        _  => InvokeAsync(StateHasChanged),
        ex => InvokeAsync(() => ShowError(ex.Message)));
```

### Form submit (`EditForm.OnSubmit`)

```csharp
private void Submit(EditContext context)
{
    Stream.SubmitModel(model!).Subscribe(log =>
    {
        InvokeAsync(() =>
        {
            if (log.Status == ActivityStatus.Succeeded)
                Reset();
            else
                ShowError(log);
            StateHasChanged();
        });
    });
}
```

`Submit` is `void`, not `async void`. The framework's `OnSubmit` `EventCallback` accepts both, so the void form is always preferred.

### Bridging to `IAsyncEnumerable` (autocomplete callbacks, etc.)

Some FluentUI / third-party APIs require `Func<string, Task<T[]>>` or `IAsyncEnumerable<T>`. Rather than `await observable.FirstAsync().ToTask()` (the deadlock pattern), use a `Channel`:

```csharp
private async IAsyncEnumerable<MyItem> StreamItems(string query,
    [EnumeratorCancellation] CancellationToken ct)
{
    var channel = Channel.CreateUnbounded<MyItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    using var sub = MeshService.Query<MyItem>(query)
        .SelectMany(c => c.Items)
        .Subscribe(
            item => channel.Writer.TryWrite(item),
            _    => channel.Writer.TryComplete(),
            ()   => channel.Writer.TryComplete());

    await foreach (var item in channel.Reader.ReadAllAsync(ct))
        yield return item;
}
```

The `await foreach` iterates a `ChannelReader` — that is not a hub round-trip Task bridge, so there is no deadlock surface. `using var sub` disposes the subscription when the consumer stops reading.

---

## Forbidden patterns and their fixes

| ❌ Wrong | ✅ Right |
|---|---|
| `var x = await mesh.QueryAsync<T>("path:X").FirstOrDefaultAsync()` | `mesh.GetMeshNode(path).Subscribe(n => UpdateState(n))` for a known path; `mesh.Query<T>(req).Subscribe(...)` for set queries |
| `var r = await Hub.AwaitResponse<R>(req)` | `Hub.Observe(req).Subscribe(r => …, ex => …)` |
| `Hub.RegisterCallback(d, r => { … })` | `Hub.Observe(d).Subscribe(r => …, ex => …)` |
| `var x = await stream.FirstAsync().ToTask()` | `stream.Take(1).Subscribe(x => UpdateState(x))` |
| `var x = await meshService.UpdateNode(node).FirstAsync()` | `meshService.UpdateNode(node).Subscribe(n => …, ex => …)` |
| `return Task.FromResult(_items.ToArray())` (callback returning a snapshot) | Bind directly to `_items`; Subscribe pushes updates and `StateHasChanged` re-renders |
| `_ = LoadAsync(); await ...` (fire-and-forget Task swallowing errors) | Sync method that fires `Subscribe(onNext, onError)` — both paths handled |
| `private async void Submit(...) => await svc.DoAsync()` | `private void Submit(...) => svc.Do().Subscribe(...)` |
| `private async Task RunCell(int i) { ... await ExecuteAsync ... }` | `private void RunCell(int i) { ... ExecuteCode(...).Subscribe(...) }` |
| `await Task.WhenAll(observables.Select(o => o.ToTask()))` | `Observable.Merge(observables)` or `Observable.CombineLatest(observables)` + single Subscribe |

---

## Lifecycle reference

| Hook | When to use it | Mesh I/O? |
|---|---|---|
| `OnInitialized` (sync) | One-time setup that doesn't read mesh state | Yes — `Subscribe` to streams here |
| `OnInitializedAsync` | **Avoid for mesh reads.** Sanctioned only for non-mesh async work (e.g. JSInterop ready check) | No mesh awaits |
| `OnParametersSet` (sync) | Re-subscribe when bound parameters change | Yes — dispose old `IDisposable`, subscribe to new |
| `OnParametersSetAsync` | **Avoid for mesh reads.** Same rationale as `OnInitializedAsync` | No mesh awaits |
| `OnAfterRenderAsync` | JSInterop after first render (`firstRender` check) | JSInterop only |
| Click / event handlers | Call services, post messages, update state | `Subscribe`, never `await` mesh observables |
| `Dispose` | Tear down subscriptions | Dispose all `IDisposable`s held by the component |

---

## When `await` IS allowed in Blazor code

There is a narrow set of awaits that are safe because they don't bridge a hub round-trip:

- `await JSRuntime.InvokeVoidAsync(...)` / `InvokeAsync<T>(...)` — JSInterop is not a hub call.
- `await InvokeAsync(...)` to marshal a state mutation onto the Blazor dispatcher — this is the dispatcher itself, not a mesh round-trip.
- `await Task.Delay(...)` for debouncing — pure timer.
- `await streamRef.WriteToStreamAsync(...)` / file I/O — sanctioned boundary.
- `await foreach (var x in channelReader.ReadAllAsync(ct))` — channel iteration, not a `.ToTask()` bridge.

Everything else — every `await` whose right-hand side touches `IMessageHub`, `IMeshService`, `IWorkspace`, `ISynchronizationStream`, or any extension of those — must become a `Subscribe`.

---

## Cross-references

- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — the master doc on hub-handler deadlock semantics; this article specialises that to the Blazor surface.
- [Blazor Data Binding](/Doc/Architecture/BlazorDataBinding) — how layout-area paths bind to Razor view fields, and the canonical view-side stream patterns.
- [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess) — when to use `QueryAsync` / `Query` vs. `GetMeshNode` / `GetRemoteStream`.
