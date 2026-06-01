---
Name: Blazor Async
Description: "Practical playbook for using IObservable<T> with Subscribe in Blazor components instead of await, covering lifecycle hooks, click handlers, and forbidden patterns."
---
# Blazor Async — `Subscribe`, not `await`

> **TL;DR** Every mesh / hub call returns `IObservable<T>`. You `Subscribe(...)` to it — never `await`, never `.ToTask()`, never `.FirstAsync().ToTask()`, never `.FirstOrDefaultAsync()`. State updates from your Subscribe callback always go through `InvokeAsync(...)` so they run on the Blazor circuit's dispatcher.

This is the companion article to [Asynchronous Calls](AsynchronousCalls), which covers deadlock semantics in hub-handler code. This page is the practical playbook for Blazor components, layout-area views, and click-action handlers.

---

## Why `await` is wrong in Blazor

There are two distinct failure modes — one is a deadlock, the other is a stale-data problem.

### 1. Deadlock — shared scheduling infrastructure

The Blazor circuit dispatcher and the mesh hub's `ActionBlock` both pump messages through scheduling primitives that, under load, can end up serialising work onto the same thread. Awaiting a hub round-trip inside a Blazor component method blocks the dispatcher waiting for a response that has to flow *back through that same dispatcher*. Under any concurrent load — multiple users, parallel queries from one page, even rapid keystrokes — this deadlocks deterministically.

This is the same root cause as the hub-handler deadlock described in [Asynchronous Calls](AsynchronousCalls), with one extra layer: the awaiting context is the Blazor circuit rather than a hub `ActionBlock`. The deadlock surface is real even though the path is shorter.

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
        _sub = Workspace
            .GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(BoundPath), new MeshNodeReference())
            .Subscribe(change =>
            {
                _node = change.Value;
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
        .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(q))
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

    using var sub = MeshService.ObserveQuery<MyItem>(query)
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
| `var x = await mesh.QueryAsync<T>("path:X").FirstOrDefaultAsync()` | `mesh.GetMeshNode(path).Subscribe(n => UpdateState(n))` for a known path; `mesh.ObserveQuery<T>(req).Subscribe(...)` for set queries |
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

- [Asynchronous Calls](AsynchronousCalls) — the master doc on hub-handler deadlock semantics; this article specialises that to the Blazor surface.
- [Blazor Data Binding](BlazorDataBinding) — how layout-area paths bind to Razor view fields, and the canonical view-side stream patterns.
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — when to use `QueryAsync` / `ObserveQuery` vs. `GetMeshNode` / `GetRemoteStream`.
