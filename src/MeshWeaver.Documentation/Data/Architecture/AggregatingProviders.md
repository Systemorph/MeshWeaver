# Aggregating Provider Pattern

Many subsystems in MeshWeaver need to merge items contributed by many independent providers
(autocomplete suggestions, menu entries, search results, chat completions, …). This page captures
the **one correct shape** for doing that so every provider-aggregator site in the codebase stays
fast, deterministic, and cheap.

## The rule

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

## Sites that follow this pattern

- `IAutocompleteProvider` + `HandleAutocompleteRequest` (`DataExtensions.cs`) — the original.
- `INodeMenuProvider` + `CollectMenuItemsByContextAsync` (`NodeMenuItemsExtensions.cs`) —
  the node/mesh menu aggregator.

Any new aggregator that gathers async-yielded items from multiple providers should look like
these two and nothing else. If it's tempting to reach for `Where`/`OrderBy`/`Distinct` at the
aggregation boundary, stop — put the comparer into the sorted set and let it do the work.

## Checklist for reviewers

- [ ] Each provider's `IAsyncEnumerable` appears in exactly one `await foreach` per request.
- [ ] Items land in `ImmutableSortedSet<T>.Builder` (or another insert-sorted container) with
      a comparer that defines both order and equality.
- [ ] No `OrderBy` / `Sort` call after the `await foreach` loop.
- [ ] Providers are resolved via `hub.ServiceProvider.GetServices<T>()` after being registered
      with `TryAddEnumerable`.
