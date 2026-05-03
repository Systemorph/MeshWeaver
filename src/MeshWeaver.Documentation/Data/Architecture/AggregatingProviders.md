# Aggregating Provider Pattern

Many subsystems in MeshWeaver need to merge items contributed by many independent providers
(autocomplete suggestions, menu entries, search results, chat completions, …). This page captures
the **two correct shapes** for doing that so every provider-aggregator site in the codebase stays
fast, deterministic, and cheap.

## Two shapes — pick by consumer

| Consumer shape | Provider contract | Aggregator |
|---|---|---|
| **Streaming UI** that re-renders as items arrive (autocomplete suggest widget, live search) | `IObservable<TItem> GetItems(...)` | `Merge` + `ScanTopN` + `Subscribe` (no `await foreach` in the aggregator) |
| **Collect-then-render** that needs the full sorted set before doing anything (node menus, settings panels) | `IAsyncEnumerable<TItem> GetItemsAsync(...)` | `await foreach` into `ImmutableSortedSet<T>.Builder` per-bucket, return immutable snapshot |

**Rule of thumb:** if any downstream code re-renders as more items arrive, the provider returns
`IObservable<T>`. If the consumer needs a final list to do anything, the provider returns
`IAsyncEnumerable<T>` and is collected into a sorted set.

> The autocomplete chain (`IAutocompleteProvider.GetItems`) is the canonical observable example;
> the node-menu chain (`INodeMenuProvider.GetItemsAsync`) is the canonical collect-then-render
> example. Same DI registration shape (`TryAddEnumerable`), different return type.

## Observable-first providers (autocomplete, live search)

The provider exposes `IObservable<TItem>` directly. Pure-in-memory providers wrap the synchronous
projection via `IEnumerable<T>.ToObservable()`; providers that talk to an external system
(database, file system, hub round-trip) bridge their inner `IAsyncEnumerable` via
`Observable.Create` + `await foreach` — this is the **only** place `await` appears, sealed inside
a single helper:

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

A provider that touches mesh state, file system, or any other async source uses the helper:

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

The **aggregator** merges the per-provider observables and folds into a top-N snapshot via
`ScanTopN`. Fast providers emit immediately; slow providers don't block fast ones; the snapshot
keeps refining until every provider's `IObservable` completes:

```csharp
// AgentsApplicationExtensions.HandleAutocompleteRequest
providers
    .Select(p => p.GetItems(query, contextPath)
        .Catch(Observable.Empty<AutocompleteItem>()))   // one bad provider doesn't kill the rest
    .Merge()
    .ScanTopN(AutocompleteTopN, AutocompleteByPriority)
    .LastOrDefaultAsync()
    .Subscribe(snapshot => hub.Post(
        new AutocompleteResponse((snapshot ?? Array.Empty<AutocompleteItem>()).ToList()),
        o => o.ResponseFor(request)));
```

For consumers that want a *streaming* result (Blazor autocomplete widget that repaints as
suggestions arrive), drop the `LastOrDefaultAsync` and subscribe to the `ScanTopN` snapshot
sequence directly — see `AutocompleteStreamProvider.Stream`.

### Tests against observable providers

Tests can `await` (the test edge is the only place observable→Task bridges are sanctioned), but
**not** via `.ToTask()` on a hub-touching observable. The right shape for tests that just want
a materialised list of items is to convert the observable to `IAsyncEnumerable` via
`ToAsyncEnumerableSequence` and then use the standard `await ToArrayAsync` / `await foreach`:

```csharp
using MeshWeaver.Reactive;   // ToAsyncEnumerableSequence

var items = await provider.GetItems("@Sys", null)
    .ToAsyncEnumerableSequence(ct)
    .ToArrayAsync(ct);
```

`ToAsyncEnumerableSequence` is the reverse of `ToObservableSequence` — it bridges via an
unbounded channel and disposes the subscription when the iterator completes. Same primitive
the autocomplete pipeline uses internally; tests just consume it the same way.

If the test wants the streaming snapshot semantics (verifying intermediate snapshots, not just
the final list), iterate the `ScanTopN` output instead:

```csharp
await foreach (var snap in provider.GetItems(query, contextPath)
    .ScanTopN(50, ByPriorityDescending)
    .ToAsyncEnumerableSequence(ct))
{
    // assert on each snapshot as it arrives
}
```

## Collect-then-render providers (node menus, settings)

For an aggregator that gathers items from N `IAsyncEnumerable`-style providers:

1. **Enumerate each provider's `IAsyncEnumerable` exactly once per request** — no repeated
   `foreach` over the same provider call, no calling the provider again per-filter or per-group.
   The async sequence is the provider's side effect budget; double-enumeration doubles the work
   (DB round-trips, permission checks, etc.) and can surface the same item twice.
2. **Insert each yielded item directly into an `ImmutableSortedSet<T>.Builder`** (or equivalent
   sorted collection) using a comparer that encodes the total sort order. The set keeps items
   in order on every `Add` — no post-hoc `OrderBy`, no `List.Sort` at the end.
3. **Do not use `Where(...).OrderBy(...)` on the intermediate collection.** Those LINQ chains
   re-enumerate their source every time the outer result is iterated; if the source is an
   `IAsyncEnumerable` or is wrapped through `Select`, the provider runs again. Materialize
   into the sorted set first, then iterate the set.
4. **Dedupe through the comparer, not with a parallel `HashSet`.** Define the comparer so
   items that are "the same" compare equal — the sorted set drops the later duplicate in one
   step, no extra allocation.

## Anti-patterns

```csharp
// ❌ Enumerates providers once per filter context — O(providers × contexts) async calls
foreach (var ctx in contexts)
{
    await foreach (var item in provider.GetItemsAsync(host, ctx))
        ...
}

// ❌ Post-hoc sort — collects into a List then sorts at the end. The List is already mutable
//    (a Collections-Policy violation) and the O(n log n) sort runs every time instead of
//    being amortized across inserts.
var items = new List<X>();
await foreach (var it in provider.GetItemsAsync()) items.Add(it);
items.Sort((a, b) => a.Order.CompareTo(b.Order));

// ❌ LINQ filter + order on an already-materialized set — re-runs the projection every time
//    the caller iterates (e.g. once to count, again to render).
return bucket.Where(x => x.Order > 0).OrderBy(x => x.Order);
```

## Canonical shape

```csharp
// Comparer first: primary key (sort order) then tiebreakers that preserve identity so items
// with equal primary keys but different payloads don't collapse.
private static readonly IComparer<NodeMenuItemDefinition> ItemComparer =
    Comparer<NodeMenuItemDefinition>.Create((a, b) =>
    {
        var c = a.Order.CompareTo(b.Order);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Label, b.Label);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Area, b.Area);
    });

internal static async Task<ImmutableDictionary<string, ImmutableSortedSet<NodeMenuItemDefinition>>>
    CollectAsync(LayoutAreaHost host, RenderingContext ctx)
{
    var buckets = new Dictionary<string, ImmutableSortedSet<NodeMenuItemDefinition>.Builder>();

    ImmutableSortedSet<NodeMenuItemDefinition>.Builder GetBucket(string key)
        => buckets.TryGetValue(key, out var b)
            ? b
            : buckets[key] = ImmutableSortedSet.CreateBuilder(ItemComparer);

    async Task ConsumeAsync(string key, IAsyncEnumerable<NodeMenuItemDefinition> items)
    {
        var bucket = GetBucket(key);
        await foreach (var item in items)
            bucket.Add(item);  // sorted-set Add inserts in position, dedupes via comparer
    }

    foreach (var provider in host.Hub.ServiceProvider.GetServices<INodeMenuProvider>())
        await ConsumeAsync(provider.Context ?? "", provider.GetItemsAsync(host, ctx));

    var result = ImmutableDictionary.CreateBuilder<string, ImmutableSortedSet<NodeMenuItemDefinition>>();
    foreach (var kvp in buckets)
        result[kvp.Key] = kvp.Value.ToImmutable();
    return result.ToImmutable();
}
```

## Provider registration — one instance per hub

Providers are DI-registered via `TryAddEnumerable(ServiceDescriptor.Scoped<IFoo, MyFoo>())` so
each implementation type is added at most once per hub, and the aggregator resolves them with
`hub.ServiceProvider.GetServices<IFoo>()`. Same pattern as `IAutocompleteProvider`:

```csharp
hub.WithServices(services =>
{
    services.TryAddEnumerable(
        ServiceDescriptor.Scoped<INodeMenuProvider, MarkdownExportMenuProvider>());
    return services;
});
```

Do **not** accumulate providers into a config-level collection (e.g. `config.Set(...)` + a
custom `Collection` record) — that path has no dedup, serializes at config time, and can't
be resolved via standard DI idioms.

## Sites that follow these patterns

**Observable-first** (provider returns `IObservable<T>`, aggregator uses `Merge` + `ScanTopN`):

- `IAutocompleteProvider` + `HandleAutocompleteRequest` (`DataExtensions.cs`,
  `AgentsApplicationExtensions.cs`) — autocomplete suggestions.
- `IAutocompleteStreamProvider.Stream` (`AutocompleteStreamProvider`) — streaming snapshot
  variant of the same chain for live UI consumers (Monaco completion widget, etc.).

**Collect-then-render** (provider returns `IAsyncEnumerable<T>`, aggregator uses
`ImmutableSortedSet` + `await foreach`):

- `INodeMenuProvider` + `CollectMenuItemsByContextAsync` (`NodeMenuItemsExtensions.cs`) —
  node/mesh menu aggregator.

Any new aggregator that gathers items from multiple providers should look like one of these and
nothing else. Pick observable-first when the consumer re-renders as items arrive (any UI suggest
widget); pick collect-then-render when the consumer needs the final sorted snapshot before doing
anything (rendering a static menu). If it's tempting to reach for `Where` / `OrderBy` / `Distinct`
at the aggregation boundary, stop — put the comparer into the sorted set (collect-then-render) or
into `ScanTopN` (observable-first) and let it do the work.

## Checklist for reviewers

**Observable-first contracts:**

- [ ] Provider returns `IObservable<T>` (not `IAsyncEnumerable<T>`, not `Task<…>`).
- [ ] No `await` outside the innermost `Observable.Create + await foreach` bridge (or the
      shared `*ProviderObservable.FromAsyncEnumerable` helper).
- [ ] Aggregator uses `Merge` (not `await foreach` over each provider in a `foreach`) so
      providers run in parallel and emit as they produce.
- [ ] Aggregator uses `ScanTopN` (or `ScanSorted`) so the snapshot updates incrementally as
      providers emit — no `ToList` / `LastAsync` on the merged stream unless the consumer
      genuinely wants only the final snapshot.
- [ ] Per-provider `Catch(Observable.Empty<T>())` so one bad provider doesn't kill the merge.
- [ ] Tests bridge back to `await` via `provider.GetItems(...).ToAsyncEnumerableSequence(ct)`
      then `await foreach` or `await ToArrayAsync(ct)` — never `.ToTask()` on a hub-touching
      observable.

**Collect-then-render contracts:**

- [ ] Each provider's `IAsyncEnumerable` appears in exactly one `await foreach` per request.
- [ ] Items land in `ImmutableSortedSet<T>.Builder` (or another insert-sorted container) with
      a comparer that defines both order and equality.
- [ ] No `OrderBy` / `Sort` call after the `await foreach` loop.

**Both:**

- [ ] Providers are resolved via `hub.ServiceProvider.GetServices<T>()` after being registered
      with `TryAddEnumerable`.
