---
Name: Aggregating Providers
Description: "Canonical patterns for merging items from multiple independent providers using reactive observables — covering streaming-item and reactive-snapshot-set shapes, DI registration, and the async I/O boundary rule."
---

# Aggregating Provider Pattern

Many subsystems in MeshWeaver need to merge contributions from multiple independent providers: autocomplete suggestions, menu entries, search results, chat completions, and more. This page defines the **two correct shapes** for doing that so every provider-aggregator site in the codebase stays fast, deterministic, reactive, and cheap.

Both shapes are **`IObservable`-first**. Neither uses `IAsyncEnumerable` / `await foreach` at the provider or aggregator boundary — the only `await` permitted is the innermost storage bridge inside a streaming provider (`Observable.Create` + `await foreach`, sealed in one helper).

---

## Two shapes — pick by emission granularity

| Consumer shape | Provider contract | Aggregator |
|---|---|---|
| **Streaming items** — repaints as each individual item arrives (autocomplete suggest widget, live search) | `IObservable<TItem> GetItems(...)` | `Merge` + `ScanTopN` |
| **Reactive snapshot set** — each emission is the provider's *complete* current set; the consumer re-renders when inputs change (node menus, permission-gated panels) | `IObservable<IReadOnlyCollection<TItem>> GetItems(...)` | `CombineLatest` per provider → merged sorted set → re-render on every emission |

**Rule of thumb:** if the consumer wants to paint items one-by-one as they trickle in, the provider emits **one item per `OnNext`** (`IObservable<TItem>`). If the consumer renders a whole control from the full current set and must re-render whenever that set changes, the provider emits **the whole set per `OnNext`** (`IObservable<IReadOnlyCollection<TItem>>`).

> 🚨 **There is no `IAsyncEnumerable` "collect-then-render" shape anymore.** It took the *first* snapshot of its inputs and locked it in (`await foreach … yield break`). For a permission-gated menu, that meant baking in whatever permissions had propagated by first render — a runtime `AccessAssignment` that arrived later never reached the menu (the access race behind the old `Menu_Editor_ShowsCreateItems` flake). Reactive snapshot-set providers re-emit when their inputs change and the renderer re-renders. See `Doc/GUI/NodeMenu.md`.

> The autocomplete chain (`IAutocompleteProvider.GetItems`) is the canonical **streaming** example; the node-menu chain (`INodeMenuProvider.GetItems`) is the canonical **reactive-snapshot-set** example. Same DI registration shape (`TryAddEnumerable`), different emission granularity.

---

## The async boundary lives at the I/O edge

`async` / `await` / `IAsyncEnumerable` are **not** a style choice — they are the bridge across a *real* I/O wait (a Postgres round-trip, a file read, a network call). Everything above that wait stays synchronous-observable. This rule determines whether a provider, aggregator, or adapter is allowed to be async at all.

**In-memory sources are never async.** A provider, aggregator, or storage adapter that only touches in-process state — a registry, a dictionary, an already-loaded `ImmutableList`, a `DataContext`'s type sources — projects **synchronously** and lifts to the contract with `IEnumerable<T>.ToObservable()`. No `async`, no `await`, no `IAsyncEnumerable`, no `Task`. An `async IAsyncEnumerable` method that never actually awaits I/O is a bug: it pays the state-machine and allocation cost and lies about doing I/O. The in-memory `CommandAutocompleteProvider`, `ModelAutocompleteProvider`, `MeshCatalogAutocompleteProvider`, `DataAutocompleteProvider`, and `LayoutAreaAutocompleteProvider` are all pure `.ToObservable()` — that is the target shape for anything backed by memory.

**Only the leaf that performs the I/O crosses into async**, and it bridges back to the observable contract at exactly one sealed point (`Observable.Create` + `await foreach`, or the shared `FromAsyncEnumerable` helper). The Postgres / file-system / network adapters live here — e.g. `PostgreSqlMeshQuery.AutocompleteAsync`. **Pool at this edge:** a connection pool for the DB; for resources with no pool of their own (file system, blob, HTTP, compile, process) the shared mesh-scoped `IIoPool` governor caps concurrency and pushes the work onto the ThreadPool — so the wait is amortized and back-pressured, not a fresh allocation per call, and never runs on the hub scheduler. See [Controlled I/O Pooling](ControlledIoPooling).

**Push the boundary as deep as it will go.** If a query fans out across adapters and only one of them hits Postgres, only *that* adapter is async; the in-memory adapters in the same fan-out stay synchronous and the merge above them is pure Rx. The caller never sees async — it sees `IObservable<T>`.

> **Litmus test:** before you write `async` on a method, name the I/O it awaits. If you can't — because the data is already in memory — delete the `async` and return `IObservable<T>` built from the synchronous projection. The only methods that keep `async`/`IAsyncEnumerable` are the ones whose body literally opens a connection, reads a file, or calls the network.

---

## Streaming providers (autocomplete, live search)

The provider exposes `IObservable<TItem>` and emits one item per `OnNext`. Pure-in-memory providers wrap the synchronous projection via `IEnumerable<T>.ToObservable()`; providers that talk to an external system (database, file system, hub round-trip) bridge their inner `IAsyncEnumerable` via `Observable.Create` + `await foreach` — the **only** place `await` appears, sealed inside a single helper:

```csharp
// MeshWeaver.Data.Contract/Completion/IAutocompleteProvider.cs
public static class AutocompleteProviderObservable
{
    public static IObservable<AutocompleteItem> FromAsyncEnumerable(
        Func<CancellationToken, IAsyncEnumerable<AutocompleteItem>> factory) =>
        Observable.Create<AutocompleteItem>(async (observer, ct) =>
        {
            try
            {
                await foreach (var item in factory(ct).WithCancellation(ct))
                    observer.OnNext(item);
                observer.OnCompleted();
            }
            catch (OperationCanceledException) { observer.OnCompleted(); }
            catch (Exception ex) { observer.OnError(ex); }
        });
}
```

A provider that does no I/O — pure registry enumeration — just calls `ToObservable`:

```csharp
// CommandAutocompleteProvider, ModelAutocompleteProvider, MeshCatalogAutocompleteProvider, …
public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null) =>
    _registry.GetAllCommands()
        .Select(cmd => new AutocompleteItem(...))
        .ToObservable();
```

A provider that touches mesh state, file system, or any other async source uses the helper. The `await foreach` here is the **sanctioned boundary** — the storage layer (`meshQuery.AutocompleteAsync`, file enumeration) is genuinely `IAsyncEnumerable`, and `FromAsyncEnumerable` is the one place that bridges it into the observable contract:

```csharp
// UnifiedReferenceAutocompleteProvider, MeshNodeAutocompleteProvider,
// ContentAutocompleteProvider, AddressCatalogAutocompleteProvider
public IObservable<AutocompleteItem> GetItems(string query, string? contextPath = null) =>
    AutocompleteProviderObservable.FromAsyncEnumerable(ct => EnumerateAsync(query, contextPath, ct));

private async IAsyncEnumerable<AutocompleteItem> EnumerateAsync(
    string query, string? contextPath, [EnumeratorCancellation] CancellationToken ct)
{
    // ... await foreach over meshQuery.AutocompleteAsync, yield return ...
}
```

### Aggregating streaming providers

The aggregator merges the per-provider observables and folds them into a top-N snapshot via `ScanTopN`. Fast providers emit immediately; slow providers don't block fast ones; the snapshot keeps refining until every provider's `IObservable` completes:

```csharp
// AutocompleteStreamProvider.Stream / HandleAutocompleteRequest
providers
    .Select(p => p.GetItems(query, contextPath)
        .Catch(Observable.Empty<AutocompleteItem>()))   // one bad provider doesn't kill the rest
    .Merge()
    .ScanTopN(topN, ByPriorityDescending);
```

For a request/response consumer (cross-hub `AutocompleteRequest`), append `.LastOrDefaultAsync()` to post only the final snapshot. For a streaming UI consumer (Monaco completion widget), subscribe to the `ScanTopN` sequence directly — see `AutocompleteStreamProvider.Stream`.

### Testing streaming providers

Tests can `await` (the test edge is the only place observable→Task bridges are sanctioned), but **not** via `.ToTask()` on a hub-touching observable. The right shape for tests that just want a materialised list of items is to convert the observable to `IAsyncEnumerable` via `ToAsyncEnumerableSequence` and then use the standard `await ToArrayAsync` / `await foreach`:

```csharp
using MeshWeaver.Reactive;   // ToAsyncEnumerableSequence

var items = await provider.GetItems("@Sys", null)
    .ToAsyncEnumerableSequence(ct)
    .ToArrayAsync(ct);
```

---

## Reactive snapshot-set providers (node menus, permission-gated panels)

The provider returns `IObservable<IReadOnlyCollection<TItem>>` — **each emission is the provider's complete item set** for the current state. Compose the live input streams (node content, the viewer's effective permissions) and project the whole set; the provider re-emits whenever an input changes, so the consumer re-renders without a reload:

```csharp
// NodeMenuItemsExtensions.DefaultNodeMenuProvider
private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> DefaultNodeMenuProvider(
    LayoutAreaHost host, RenderingContext ctx)
    => GetMenuContext(host)   // CombineLatest(live node stream, GetEffectivePermissions)
        .Select(menuCtx =>
        {
            var (menuPath, _, _, perms) = menuCtx;
            var items = ImmutableList.CreateBuilder<NodeMenuItemDefinition>();
            var edit = MeshNodeLayoutAreas.GetEditMenuItem(menuPath, perms);
            if (edit != null) items.Add(edit);
            // … more permission-gated items …
            return (IReadOnlyCollection<NodeMenuItemDefinition>)items.ToImmutable();
        });
```

Three rules every snapshot-set provider must follow:

1. **Always emit at least an empty collection — never `Observable.Empty`.** The aggregator `CombineLatest`s every provider in the context; a provider that never emits stalls the whole context. "Contributes nothing for this node" means emit `[]`, not silence.
2. **Each emission is the full set, not a delta.** The aggregator replaces the provider's slice on every emission and re-merges.
3. **Compose live streams, never snapshot.** `GetEffectivePermissions` emits `seed.Concat(enriched)` — the static/claim seed first, then the synced-AccessAssignment-backed enrichment. Project off it with `.Select` so the menu self-corrects the instant a runtime grant propagates. Snapshotting the first emission is the exact access race this pattern exists to kill.

### Aggregating snapshot-set providers

The aggregator combines providers for a context with `CombineLatest` — each `StartWith([])` so the combine fires immediately instead of stalling on a slow provider — folding into an `ImmutableSortedSet` keyed on a comparer that encodes the total sort order (sorted + deduped on every insert, no post-hoc `OrderBy`):

```csharp
// NodeMenuItemsExtensions.CombineProviderStreams
providerStreams
    .Select(s => s.StartWith(EmptyItems))
    .CombineLatest(slices =>
    {
        var builder = ImmutableSortedSet.CreateBuilder(MenuItemComparer);
        foreach (var slice in slices)
            foreach (var item in slice)
                builder.Add(item);
        return (IReadOnlyCollection<NodeMenuItemDefinition>)builder.ToImmutable();
    });
```

The **renderer** is a predicate renderer (`WithRenderer(_ => true, …)`) that runs once per area render. For each registered context it subscribes to the merged stream and pushes the result into `$Menu:{context}` via `host.UpdateArea` on every emission, tying the subscription to the area's lifecycle with `RegisterForDisposal` — the same shape the framework's own reactive `RenderArea` overloads use:

```csharp
// NodeMenuItemsExtensions.RenderMenus
host.RegisterForDisposal(
    MenuControl.MenuArea,
    items
        .DistinctUntilChanged(MenuItemsSequenceComparer.Instance)   // suppress identical re-renders
        .Subscribe(slice => host.UpdateArea(areaContext, new MenuControl([.. slice]))));
```

### Testing snapshot-set providers

Because the menu re-emits as permissions enrich, a test must **not** grab the first non-null snapshot (that is the empty / pre-propagation render). Subscribe to the layout stream and `.Where(predicate)` until the set reaches the expected state, with a `Timeout` as the failure signal:

```csharp
var items = await MenuStream(client, nodeAddress, NodeMenuContext)
    .CombineLatest(MenuStream(client, nodeAddress, MeshMenuContext), Merge)
    .Where(set => set.Select(i => i.Label).ToHashSet().SetEquals(expectedLabels))
    .Timeout(20.Seconds())
    .FirstAsync()
    .ToTask(ct);
```

`SetEquals` waiting catches both *missing* items (role not yet propagated) and *extra* items (wrong gating) — either way the menu never reaches the expected set and the `Timeout` fails the test. See `MenuAccessControlTest`.

---

## Anti-patterns

```csharp
// ❌ await foreach + yield break in a provider — takes the FIRST input snapshot and locks it in.
//    The menu never updates when a runtime AccessAssignment propagates → access race.
await foreach (var perms in host.Hub.GetEffectivePermissions(path).ToAsyncEnumerableSequence())
{
    if (perms.HasFlag(Permission.Update)) yield return item;
    yield break;   // ← first-snapshot-wins
}

// ❌ Observable.Empty for "contributes nothing" — stalls the aggregator's CombineLatest forever.
return applicable ? Observable.Return(items) : Observable.Empty<IReadOnlyCollection<T>>();
//                                              ^ must be Observable.Return((IReadOnlyCollection<T>)[])

// ❌ Post-hoc sort — collects into a mutable List then sorts at the end (Collections-Policy
//    violation + O(n log n) every render instead of amortized inserts).
var items = new List<X>();
foreach (var it in slice) items.Add(it);
items.Sort((a, b) => a.Order.CompareTo(b.Order));

// ❌ Grabbing the first menu render in a test — that's the empty StartWith snapshot.
var menu = await menuStream.FirstAsync(x => x != null);   // races permission propagation
```

---

## Provider registration — one instance per hub

DI-registered providers (`INodeMenuProvider`, `IAutocompleteProvider`) are added via `TryAddEnumerable(ServiceDescriptor.Scoped<IFoo, MyFoo>())` so each implementation type is registered at most once per hub, and the aggregator resolves them with `hub.ServiceProvider.GetServices<IFoo>()`:

```csharp
hub.WithServices(services =>
{
    services.TryAddEnumerable(
        ServiceDescriptor.Scoped<INodeMenuProvider, MarkdownExportMenuProvider>());
    return services;
});
```

The node-menu chain also supports **delegate** providers registered via `config.AddNodeMenuItems(context, NodeMenuItemProvider)` for menu items that live with a node type's configuration rather than a standalone class — same reactive `IObservable<IReadOnlyCollection<…>>` contract, resolved alongside the DI providers per context.

---

## Sites that follow these patterns

**Streaming** (provider returns `IObservable<TItem>`, aggregator uses `Merge` + `ScanTopN`):

- `IAutocompleteProvider` + `AutocompleteStreamProvider` / `HandleAutocompleteRequest` (`DataExtensions.cs`, `AgentsApplicationExtensions.cs`) — autocomplete suggestions.

**Reactive snapshot set** (provider returns `IObservable<IReadOnlyCollection<TItem>>`, aggregator uses `CombineLatest` + per-emission re-render):

- `INodeMenuProvider` + `NodeMenuItemsExtensions.CollectMenuItemStreamsByContext` / `RenderMenus` (`NodeMenuItemsExtensions.cs`) — node / mesh menu aggregator. Implementers: `DefaultNodeMenuProvider`, `DefaultMeshMenuProvider`, `MarkdownExportMenuProvider`, `LinkedInCredentialMenuProvider`, `ApprovalMenuProvider`, the AI thread menu providers.

Any new aggregator that gathers items from multiple providers should look like one of these and nothing else. Pick **streaming** when the consumer repaints as items arrive (any suggest widget); pick **reactive-snapshot-set** when the consumer renders a whole control from the current set and must re-render when that set changes (a permission-gated menu). If it is tempting to reach for `Where` / `OrderBy` / `Distinct` at the aggregation boundary, stop — put the comparer into `ScanTopN` (streaming) or the `ImmutableSortedSet` (snapshot-set) and let it do the work.

---

## Reviewer checklist

**Streaming contracts:**

- [ ] Provider returns `IObservable<T>` (one item per `OnNext`); no `Task<…>`.
- [ ] No `await` outside the innermost `Observable.Create + await foreach` bridge (or the shared `*ProviderObservable.FromAsyncEnumerable` helper).
- [ ] Aggregator uses `Merge` + `ScanTopN` so providers run in parallel and the snapshot refines incrementally.
- [ ] Per-provider `Catch(Observable.Empty<T>())` so one bad provider doesn't kill the merge.

**Reactive snapshot-set contracts:**

- [ ] Provider returns `IObservable<IReadOnlyCollection<T>>`; each emission is the full set.
- [ ] Provider **always emits** at least `[]` — never `Observable.Empty` (would stall `CombineLatest`).
- [ ] Provider composes live input streams (`GetMeshNodeStream`, `GetEffectivePermissions`) with `Select` / `CombineLatest` — it does **not** `await foreach … yield break` or otherwise snapshot the first input.
- [ ] Aggregator uses `CombineLatest` (each `StartWith([])`) into an `ImmutableSortedSet` with a comparer that defines both order and equality — no `OrderBy` / `Sort` after.
- [ ] Renderer subscribes and pushes per emission via `host.UpdateArea`, with `RegisterForDisposal`.

**Both:**

- [ ] Providers are resolved via `hub.ServiceProvider.GetServices<T>()` after `TryAddEnumerable` (or, for menu delegate providers, registered via `AddNodeMenuItems`).
- [ ] Tests bridge back to `await` via `.ToAsyncEnumerableSequence(ct)` (streaming) or `.Where(predicate).Timeout(...).FirstAsync().ToTask(ct)` (snapshot-set) — never `.ToTask()` on a raw hub-touching observable without a bounding `Timeout`.
