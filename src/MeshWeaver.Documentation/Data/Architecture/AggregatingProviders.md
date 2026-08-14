---
Name: Aggregating Providers
Description: "Canonical patterns for merging items from multiple independent providers using reactive observables — covering the progressive-snapshot and reactive-snapshot-set shapes, DI registration, and the IIoPool boundary rule."
---

# Aggregating Provider Pattern

Many subsystems in MeshWeaver need to merge contributions from multiple independent providers: autocomplete suggestions, menu entries, search results, chat completions, and more. This page defines the **two correct shapes** for doing that so every provider-aggregator site in the codebase stays fast, deterministic, reactive, and cheap.

Both shapes are **`IObservable`-first**. Neither uses `IAsyncEnumerable` / `await foreach` at the provider or aggregator boundary — and neither uses it *inside* a provider either: an async or blocking leaf goes through **`IIoPool`** (`pool.Run` / `pool.RunStream` / `pool.InvokeBlocking`), never a bare `Observable.FromAsync` or `Observable.Create(async … await foreach)`. See [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

---

## Two shapes — pick by emission granularity

| Consumer shape | Provider contract | Aggregator |
|---|---|---|
| **Progressive snapshot** — each emission is the provider's *current best* list and the merged result refines as slower providers land (autocomplete suggest widget, live search) | `IObservable<IReadOnlyCollection<TItem>> GetItems(...)` | `CombineLatest` per provider → merged top-N snapshot |
| **Reactive snapshot set** — each emission is the provider's *complete* current set; the consumer re-renders when inputs change (node menus, permission-gated panels) | `IObservable<IReadOnlyCollection<TItem>> GetItems(...)` | `CombineLatest` per provider → merged sorted set → re-render on every emission |

**Both contracts are snapshot-shaped**: every `OnNext` carries the provider's whole current set, never a single item and never a delta. They differ only in what the aggregator does with the slices — a **priority-ordered top-N merge** for suggestions, an **`ImmutableSortedSet` union** for menus — and in what a later emission means (a *refinement* vs a *state change*).

> 🚨 **A per-item `IObservable<TItem>` contract is not one of the shapes.** Autocomplete used to be one (`Merge` + `ScanTopN` over item streams) and was migrated: a `CombineLatest` of snapshot streams gives the same "fast providers show first, slow ones merge in later" behaviour while letting each provider re-emit a *better* list, and it removes the per-item allocation churn. `ScanTopN` (`MeshWeaver.Reactive.ObservableTopNExtensions`) still exists for a genuinely item-at-a-time source — the chat composer's completion fold uses it — but it is not the provider contract.

> 🚨 **There is no `IAsyncEnumerable` "collect-then-render" shape at the provider/aggregator boundary anymore.** It took the *first* snapshot of its inputs and locked it in (`await foreach … yield break`). For a permission-gated menu, that meant baking in whatever permissions had propagated by first render — a runtime `AccessAssignment` that arrived later never reached the menu. Reactive snapshot-set providers re-emit when their inputs change and the renderer re-renders. (`IAsyncEnumerable` survives only as a storage-leaf shape, bridged through `IIoPool.RunStream`.) See [NodeMenu](/Doc/GUI/NodeMenu).

> The autocomplete chain (`IAutocompleteProvider.GetItems`) is the canonical **progressive-snapshot** example; the node-menu chain (`INodeMenuProvider.GetItems`) is the canonical **reactive-snapshot-set** example. Same DI registration shape (`TryAddEnumerable`), same `IObservable<IReadOnlyCollection<T>>` contract, different merge.

<svg viewBox="0 0 760 310" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <text x="190" y="22" text-anchor="middle" font-size="12" font-weight="bold" fill="currentColor" fill-opacity=".5">PROGRESSIVE SNAPSHOT</text>
  <text x="570" y="22" text-anchor="middle" font-size="12" font-weight="bold" fill="currentColor" fill-opacity=".5">REACTIVE SNAPSHOT-SET</text>
  <rect x="20" y="35" width="130" height="36" rx="8" fill="#1e88e5"/>
  <text x="85" y="49" text-anchor="middle" fill="#fff" font-size="11">Provider A</text>
  <text x="85" y="63" text-anchor="middle" fill="#fff" font-size="10">IObservable&lt;Collection&gt;</text>
  <rect x="20" y="85" width="130" height="36" rx="8" fill="#1e88e5"/>
  <text x="85" y="99" text-anchor="middle" fill="#fff" font-size="11">Provider B</text>
  <text x="85" y="113" text-anchor="middle" fill="#fff" font-size="10">IObservable&lt;Collection&gt;</text>
  <rect x="20" y="135" width="130" height="36" rx="8" fill="#1e88e5"/>
  <text x="85" y="149" text-anchor="middle" fill="#fff" font-size="11">Provider C</text>
  <text x="85" y="163" text-anchor="middle" fill="#fff" font-size="10">IObservable&lt;Collection&gt;</text>
  <line x1="150" y1="53" x2="195" y2="108" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="150" y1="103" x2="195" y2="108" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="150" y1="153" x2="195" y2="112" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="196" y="80" width="120" height="54" rx="10" fill="#5c6bc0"/>
  <text x="256" y="101" text-anchor="middle" fill="#fff" font-size="12" font-weight="bold">CombineLatest()</text>
  <text x="256" y="119" text-anchor="middle" fill="#fff" font-size="10">top-N merge</text>
  <line x1="316" y1="107" x2="355" y2="107" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="356" y="80" width="130" height="54" rx="10" fill="#43a047"/>
  <text x="421" y="101" text-anchor="middle" fill="#fff" font-size="12" font-weight="bold">Consumer</text>
  <text x="421" y="119" text-anchor="middle" fill="#fff" font-size="10">repaints as it refines</text>
  <text x="210" y="172" text-anchor="middle" font-size="10" fill="currentColor" fill-opacity=".5">merged list refines — fast providers</text>
  <text x="210" y="184" text-anchor="middle" font-size="10" fill="currentColor" fill-opacity=".5">don't wait for slow ones</text>
  <rect x="400" y="35" width="130" height="36" rx="8" fill="#f57c00"/>
  <text x="465" y="49" text-anchor="middle" fill="#fff" font-size="11">Provider A</text>
  <text x="465" y="63" text-anchor="middle" fill="#fff" font-size="10">IObservable&lt;Collection&gt;</text>
  <rect x="400" y="85" width="130" height="36" rx="8" fill="#f57c00"/>
  <text x="465" y="99" text-anchor="middle" fill="#fff" font-size="11">Provider B</text>
  <text x="465" y="113" text-anchor="middle" fill="#fff" font-size="10">IObservable&lt;Collection&gt;</text>
  <rect x="400" y="135" width="130" height="36" rx="8" fill="#f57c00"/>
  <text x="465" y="149" text-anchor="middle" fill="#fff" font-size="11">Provider C</text>
  <text x="465" y="163" text-anchor="middle" fill="#fff" font-size="10">IObservable&lt;Collection&gt;</text>
  <line x1="530" y1="53" x2="568" y2="108" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="530" y1="103" x2="568" y2="108" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="530" y1="153" x2="568" y2="112" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="568" y="80" width="126" height="54" rx="10" fill="#8e24aa"/>
  <text x="631" y="101" text-anchor="middle" fill="#fff" font-size="12" font-weight="bold">CombineLatest()</text>
  <text x="631" y="119" text-anchor="middle" fill="#fff" font-size="10">ImmutableSortedSet</text>
  <line x1="694" y1="107" x2="730" y2="107" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="731" y="80" width="10" height="54" rx="5" fill="#43a047"/>
  <text x="736" y="220" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="10" transform="rotate(-90 736 220)">re-renders</text>
  <text x="590" y="172" text-anchor="middle" font-size="10" fill="currentColor" fill-opacity=".5">each emission = full set;</text>
  <text x="590" y="184" text-anchor="middle" font-size="10" fill="currentColor" fill-opacity=".5">re-emits on any input change</text>
  <line x1="380" y1="35" x2="380" y2="200" stroke="currentColor" stroke-opacity=".2" stroke-width="1" stroke-dasharray="4,4"/>
  <rect x="400" y="210" width="130" height="30" rx="8" fill="#26a69a"/>
  <text x="465" y="230" text-anchor="middle" fill="#fff" font-size="11">TryAddEnumerable</text>
  <rect x="20" y="210" width="130" height="30" rx="8" fill="#26a69a"/>
  <text x="85" y="230" text-anchor="middle" fill="#fff" font-size="11">TryAddEnumerable</text>
  <text x="190" y="265" text-anchor="middle" font-size="11" fill="currentColor" fill-opacity=".55">Same DI registration shape</text>
  <text x="570" y="265" text-anchor="middle" font-size="11" fill="currentColor" fill-opacity=".55">Same DI registration shape</text>
  <text x="380" y="295" text-anchor="middle" font-size="11" fill="currentColor" fill-opacity=".4">Same snapshot contract; pick by what a later emission means: refinement vs state change</text>
</svg>

*Two aggregation shapes — same DI registration and the same snapshot contract, different merge.*

---

## The async boundary lives at the I/O edge

`async` / `await` / `IAsyncEnumerable` are **not** a style choice — they are the bridge across a *real* I/O wait (a Postgres round-trip, a file read, a network call). Everything above that wait stays synchronous-observable. This rule determines whether a provider, aggregator, or adapter is allowed to be async at all.

**In-memory sources are never async.** A provider, aggregator, or storage adapter that only touches in-process state — a registry, a dictionary, an already-loaded `ImmutableList`, a `DataContext`'s type sources — projects **synchronously** and lifts to the contract with a single `Observable.Return(snapshot)`. No `async`, no `await`, no `IAsyncEnumerable`, no `Task`. An `async IAsyncEnumerable` method that never actually awaits I/O is a bug: it pays the state-machine and allocation cost and lies about doing I/O. `DataAutocompleteProvider`, `LayoutAreaAutocompleteProvider`, and `MeshCatalogAutocompleteProvider` are pure in-memory projections — that is the target shape for anything backed by memory.

**Only the leaf that performs the I/O crosses into async**, and it bridges back to the observable contract through **`IIoPool`** — `pool.Run` (a `Task<T>` leaf), `pool.RunStream` (an `IAsyncEnumerable<T>` leaf), `pool.InvokeBlocking` (a sync-blocking/CPU leaf). The Postgres / file-system / network adapters live here. Never a bare `Observable.FromAsync` or `Observable.Create(async …)`: they run the prologue on the *subscribing* thread — the hub scheduler, when the subscribe happens mid-handler — with no concurrency bound. The pool caps concurrency, pushes the work onto the ThreadPool with `ConfigureAwait(false)`, and is drained on teardown. See [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

**Push the boundary as deep as it will go.** If a query fans out across adapters and only one of them hits Postgres, only *that* adapter is async; the in-memory adapters in the same fan-out stay synchronous and the merge above them is pure Rx. The caller never sees async — it sees `IObservable<T>`.

> **Litmus test:** before you write `async` on a method, name the I/O it awaits. If you can't — because the data is already in memory — delete the `async` and return `IObservable<T>` built from the synchronous projection. The only methods that keep `async`/`IAsyncEnumerable` are the ones whose body literally opens a connection, reads a file, or calls the network.

---

## Progressive-snapshot providers (autocomplete, live search)

`IAutocompleteProvider.GetItems` returns `IObservable<IReadOnlyCollection<AutocompleteItem>>` — each emission is the provider's **current best list**, sorted by `Priority` descending. Emit at least an empty snapshot (`AutocompleteSnapshots.Empty`) rather than `Observable.Empty`: the aggregator seeds each slice with `.StartWith(Empty)` so a silent provider cannot actually stall the `CombineLatest`, but "contributes nothing" and "still loading" must not look identical to a reader of the code.

A provider that does no I/O — pure registry enumeration — returns a single snapshot:

```csharp
// DataAutocompleteProvider, LayoutAreaAutocompleteProvider, …
public IObservable<IReadOnlyCollection<AutocompleteItem>> GetItems(
        string query, string? contextPath = null) =>
    Observable.Return<IReadOnlyCollection<AutocompleteItem>>(
        workspace.DataContext.TypeSources.Keys
            .Select(collectionName => new AutocompleteItem(...))
            .OrderByDescending(i => i.Priority)
            .ToArray());
```

A provider whose items arrive progressively builds its snapshot stream with `AutocompleteSnapshots.FromItems(items, topN)` — feed it an `IObservable<AutocompleteItem>` and it folds a growing, priority-ordered snapshot:

```csharp
// MeshNodeAutocompleteProvider — reactive end to end, no IAsyncEnumerable round-trip.
var items = meshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(queryString))
    .Take(1)
    .SelectMany(c => c.Items.Take(DefaultMaxResults).Select(ToAutocompleteItem));
return AutocompleteSnapshots.FromItems(items, 50);
```

**A genuinely async/blocking leaf goes through `IIoPool`** (`pool.Run` for a `Task<T>`, `pool.RunStream` for an `IAsyncEnumerable<T>`, `pool.InvokeBlocking` for a sync-blocking one) — never `Observable.FromAsync`, never `Observable.Create(async … await foreach)`. Both are forbidden outside `IoPool` itself: they run the prologue on the subscribing thread (the hub scheduler, mid-handler) with no concurrency bound. See [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

### Aggregating progressive-snapshot providers

The aggregator `CombineLatest`s the per-provider snapshot streams and merges them into a top-N snapshot. The merged snapshot appears as soon as the **first** provider returns and refines as the rest arrive — it never waits for the slowest:

```csharp
// AutocompleteStreamProvider.Stream
AutocompleteSnapshots.Combine(
    providers.Select(p => p.GetItems(query, contextPath)
        .Catch(Observable.Return(AutocompleteSnapshots.Empty))),  // one bad provider doesn't kill the rest
    topN);
```

For a request/response consumer (cross-hub `AutocompleteRequest`), take the settled snapshot (`LastOrDefaultAsync`) and post that. For a streaming UI consumer (the completion widget), subscribe to `IAutocompleteStreamProvider.Stream` directly.

### Testing progressive-snapshot providers

Tests may bridge to `await` at the test edge — but bound it, and wait for the *shape* you expect rather than the first emission (the first is the empty seed):

```csharp
var items = await provider.GetItems("@Sys", null)
    .Where(snapshot => snapshot.Count > 0)
    .Timeout(10.Seconds())
    .FirstAsync()
    .ToTask(ct);
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

1. **Always emit at least an empty collection — never `Observable.Empty`.** The aggregator `CombineLatest`s every provider in the context, seeding each slice with `.StartWith([])`, so silence does not literally wedge the combine — but it makes "contributes nothing for this node" indistinguishable from "has not loaded yet", and it breaks the moment a caller composes the provider without that seed. Emit `[]`, not silence.
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

// ❌ Observable.Empty for "contributes nothing" — indistinguishable from "still loading",
//    and it only survives because the aggregator happens to seed each slice with StartWith([]).
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
        ServiceDescriptor.Scoped<INodeMenuProvider, ExportMenuProvider>());
    return services;
});
```

The node-menu chain also supports **delegate** providers registered via `config.AddNodeMenuItems(context, NodeMenuItemProvider)` for menu items that live with a node type's configuration rather than a standalone class — same reactive `IObservable<IReadOnlyCollection<…>>` contract, resolved alongside the DI providers per context.

---

## Sites that follow these patterns

**Progressive snapshot** (provider returns `IObservable<IReadOnlyCollection<TItem>>`, aggregator uses `AutocompleteSnapshots.Combine` — a `CombineLatest` + top-N merge):

- `IAutocompleteProvider` + `AutocompleteStreamProvider` / the `AutocompleteRequest` handler (`AgentsApplicationExtensions.cs`) — autocomplete suggestions.

**Reactive snapshot set** (provider returns `IObservable<IReadOnlyCollection<TItem>>`, aggregator uses `CombineLatest` + per-emission re-render):

- `INodeMenuProvider` + `NodeMenuItemsExtensions.CollectMenuItemStreamsByContext` / `RenderMenus` (`NodeMenuItemsExtensions.cs`) — node / mesh menu aggregator. Implementers: `DefaultNodeMenuProvider`, `DefaultMeshMenuProvider`, `ExportMenuProvider`, `LinkedInCredentialMenuProvider`, `ApprovalMenuProvider`, the AI thread menu providers.

Any new aggregator that gathers items from multiple providers should look like one of these and nothing else. Pick **progressive snapshot** when the consumer repaints as the merged best-list refines (any suggest widget); pick **reactive-snapshot-set** when the consumer renders a whole control from the current set and must re-render when that set changes (a permission-gated menu). If it is tempting to reach for `Where` / `OrderBy` / `Distinct` at the aggregation boundary, stop — put the comparer into the merge (`AutocompleteSnapshots.ByPriorityDescending`) or into the `ImmutableSortedSet` and let it do the work.

---

## Reviewer checklist

**Progressive-snapshot contracts:**

- [ ] Provider returns `IObservable<IReadOnlyCollection<T>>` (current best list per `OnNext`, priority-ordered); no `Task<…>`.
- [ ] Provider emits at least `AutocompleteSnapshots.Empty` — never `Observable.Empty` ("nothing" must be distinguishable from "not loaded").
- [ ] No `await` anywhere; an async/blocking leaf goes through `IIoPool` (`Run` / `RunStream` / `InvokeBlocking`), never `Observable.FromAsync` / `Observable.Create(async …)`.
- [ ] Aggregator uses `AutocompleteSnapshots.Combine` so providers run in parallel and the merged snapshot refines incrementally.
- [ ] Per-provider `Catch(Observable.Return(AutocompleteSnapshots.Empty))` so one bad provider doesn't kill the merge.

**Reactive snapshot-set contracts:**

- [ ] Provider returns `IObservable<IReadOnlyCollection<T>>`; each emission is the full set.
- [ ] Provider **always emits** at least `[]` — never `Observable.Empty` ("nothing" must be distinguishable from "not loaded").
- [ ] Provider composes live input streams (`GetMeshNodeStream`, `GetEffectivePermissions`) with `Select` / `CombineLatest` — it does **not** `await foreach … yield break` or otherwise snapshot the first input.
- [ ] Aggregator uses `CombineLatest` (each `StartWith([])`) into an `ImmutableSortedSet` with a comparer that defines both order and equality — no `OrderBy` / `Sort` after.
- [ ] Renderer subscribes and pushes per emission via `host.UpdateArea`, with `RegisterForDisposal`.

**Both:**

- [ ] Providers are resolved via `hub.ServiceProvider.GetServices<T>()` after `TryAddEnumerable` (or, for menu delegate providers, registered via `AddNodeMenuItems`).
- [ ] Tests bridge back to `await` via `.Where(predicate).Timeout(...).FirstAsync().ToTask(ct)` — waiting for the expected shape, never the first emission, and never `.ToTask()` on a raw hub-touching observable without a bounding `Timeout`.
