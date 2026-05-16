using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Single top-level query fan-out. Aggregates every registered
/// <see cref="IMeshQueryProvider"/> for both the secured surface
/// (<see cref="ObserveQuery{T}(MeshQueryRequest)"/>) and the unsecured
/// <see cref="IMeshQueryCore"/> surface (used by SyncedQueryMeshNodes /
/// SecurityService to dodge the validator cycle). One boss for fan-out —
/// per-adapter providers stay leaves.
/// <para>
/// source:activity implies nodeType:Activity filter; source:accessed JOINs with UserActivity
/// nodes to order by last-access time. Providers that don't support these sources return normal results.
/// Identity is resolved from AccessService.Context. Use accessService.ImpersonateAsHub(hub)
/// to temporarily switch identity for hub-level operations.
/// </para>
/// </summary>
public class MeshQuery : IMeshQueryCore
{
    private readonly IReadOnlyList<IMeshQueryProvider> providers;
    private readonly IMessageHub hub;

    public MeshQuery(IEnumerable<IMeshQueryProvider> providers, IMessageHub hub)
    {
        // Distinct by Name — multiple AddSingleton<IMeshQueryProvider>(factory)
        // calls for the same provider class register duplicates that
        // TryAddEnumerable can't dedupe (factories have null ImplementationType).
        // Names default to the provider's type FullName, so the duplicate
        // StaticNodeQueryProvider registrations from AddPersistence vs
        // AddCoreAndWrapperServices fold into one execution per query.
        this.providers = providers
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        this.hub = hub;
    }

    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    /// <summary>
    /// Pre-extracts the partition candidates a query targets — union of
    /// <c>namespace:</c> condition values and the first segment of
    /// <see cref="ParsedQuery.Path"/>. Computed once per query and passed
    /// to every <see cref="IMeshQueryProvider.Matches"/> call.
    /// </summary>
    internal static IReadOnlyList<string> MergeQueryNamespaces(ParsedQuery parsed)
    {
        var fromFilter = parsed.ExtractNamespaces();
        if (string.IsNullOrEmpty(parsed.Path))
            return fromFilter;
        var firstSegment = parsed.Path.Split('/', 2)[0];
        if (fromFilter.Count == 0)
            return new[] { firstSegment };
        var combined = new List<string>(fromFilter.Count + 1);
        combined.AddRange(fromFilter);
        if (!combined.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            combined.Add(firstSegment);
        return combined;
    }

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        CancellationToken ct = default)
    {
        var matched = SelectMatchingProviders(NamespacesForBasePath(basePath));
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MeshQuery>();
        return MergeAutocompleteStreams(
            matched.Select(p => p.AutocompleteAsync(basePath, prefix, Options, limit, ct)),
            limit,
            applyBoost: null,
            logger,
            ct);
    }

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        CancellationToken ct = default)
    {
        var matched = SelectMatchingProviders(NamespacesForBasePath(basePath));
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MeshQuery>();
        Func<QuerySuggestion, QuerySuggestion>? boost = string.IsNullOrEmpty(contextPath)
            ? null
            : s => ApplyProximityBoost(s, contextPath, prefix);
        return MergeAutocompleteStreams(
            matched.Select(p => p.AutocompleteAsync(basePath, prefix, Options, mode, limit, contextPath, context, ct)),
            limit,
            applyBoost: boost,
            logger,
            ct);
    }

    /// <summary>
    /// Reactive autocomplete fan-out — every per-provider <see cref="IAsyncEnumerable{QuerySuggestion}"/>
    /// is bridged to <see cref="IObservable{QuerySuggestion}"/> via
    /// <see cref="ObservableTopNExtensions.ToObservableSequence{T}(IAsyncEnumerable{T})"/>,
    /// merged with <see cref="Observable.Merge{TSource}(IEnumerable{IObservable{TSource}})"/>,
    /// path-deduped + satellite-filtered, and finally folded into a single sorted
    /// snapshot via Subscribe-driven state. NO <c>await</c> in the body — the only
    /// await lives in <see cref="ObservableTopNExtensions.ToAsyncEnumerableSequence{T}(IObservable{T}, CancellationToken)"/>,
    /// the canonical bridge from observable to async-enumerable on the public surface.
    /// </summary>
    private static IAsyncEnumerable<QuerySuggestion> MergeAutocompleteStreams(
        IEnumerable<IAsyncEnumerable<QuerySuggestion>> sources,
        int limit,
        Func<QuerySuggestion, QuerySuggestion>? applyBoost,
        Microsoft.Extensions.Logging.ILogger? logger,
        CancellationToken ct)
    {
        var streams = sources.Select(s => s.ToObservableSequence()).ToList();
        var merged = streams.Count switch
        {
            0 => Observable.Empty<QuerySuggestion>(),
            1 => streams[0],
            _ => Observable.Merge(streams),
        };

        // Single fold: dedup by path + satellite filter + optional boost into a
        // ConcurrentBag/Dictionary (already thread-safe — Merge can OnNext from
        // any provider thread). On OnCompleted the bag is sorted + clipped to
        // limit; the snapshot is replayed as OnNext events to the downstream
        // observable, which then bridges back to IAsyncEnumerable.
        return Observable.Create<QuerySuggestion>((Func<IObserver<QuerySuggestion>, IDisposable>)(observer =>
        {
            var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var bag = new ConcurrentBag<QuerySuggestion>();
            return merged.Subscribe(
                s =>
                {
                    if (IsSatellitePath(s.Path)) return;
                    if (string.IsNullOrEmpty(s.Path) || !seen.TryAdd(s.Path, 0)) return;
                    bag.Add(applyBoost is null ? s : applyBoost(s));
                },
                ex => logger?.LogDebug(ex, "MeshQuery autocomplete merge faulted"),
                () =>
                {
                    foreach (var x in bag
                        .OrderByDescending(s => s.Score)
                        .ThenBy(s => s.Path.Length)
                        .ThenBy(s => s.Name)
                        .Take(limit))
                    {
                        observer.OnNext(x);
                    }
                    observer.OnCompleted();
                });
        })).ToAsyncEnumerableSequence(ct);
    }

    /// <summary>
    /// Applies proximity-based scoring boost to a suggestion based on its distance from contextPath.
    /// Closer items get higher scores. Shorter paths win when scores are tied.
    /// </summary>
    private static QuerySuggestion ApplyProximityBoost(QuerySuggestion suggestion, string? contextPath, string? prefix)
    {
        if (string.IsNullOrEmpty(contextPath))
            return suggestion;

        var boost = 0.0;
        var path = suggestion.Path;

        // Direct child of context: highest boost. Deeper descendants decay so they
        // don't outrank the context node itself (or its siblings) — see
        // LocalFirst_ChildrenOfContextScoreHigherThanDistant.
        if (path.StartsWith(contextPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = path[(contextPath.Length + 1)..];
            var relativeDepth = relative.Count(c => c == '/'); // 0 = direct child
            boost = relativeDepth switch
            {
                0 => 2000, // direct child
                1 => 900,  // grandchild — below sibling boost so sibling wins on ties
                _ => 600   // great-grandchild and deeper
            };
        }
        // Sibling: shares parent (also covers `path == contextPath`, since path starts with parent+"/")
        else if (!string.IsNullOrEmpty(contextPath))
        {
            var contextParent = contextPath.LastIndexOf('/');
            if (contextParent > 0)
            {
                var parent = contextPath[..contextParent];
                if (path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase))
                    boost = 1000; // sibling or cousin (or the context node itself)
            }
        }

        // Shared prefix segments bonus
        if (boost == 0)
        {
            var contextSegments = contextPath.Split('/');
            var pathSegments = path.Split('/');
            var shared = 0;
            for (var i = 0; i < Math.Min(contextSegments.Length, pathSegments.Length); i++)
            {
                if (contextSegments[i].Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase))
                    shared++;
                else
                    break;
            }
            if (shared >= 2)
                boost = 500;
        }

        // Path length penalty: prefer shorter paths (fewer segments)
        var segmentCount = path.Count(c => c == '/') + 1;
        boost -= segmentCount * 50;

        // Exact name match bonus
        if (!string.IsNullOrEmpty(prefix))
        {
            var name = suggestion.Name;
            if (name.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                boost += 1000;
            else if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                boost += 500;
        }

        return suggestion with { Score = suggestion.Score + boost };
    }

    /// <summary>
    /// Checks if a path is a satellite node path (contains /_Prefix/ segments).
    /// Satellite prefixes start with underscore: _Thread, _Comment, _Activity, _Access, etc.
    /// </summary>
    private static bool IsSatellitePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // Check for /_X segments where X starts with uppercase (satellite convention)
        var idx = 0;
        while ((idx = path.IndexOf("/_", idx, StringComparison.Ordinal)) >= 0)
        {
            idx += 2; // skip "/_"
            if (idx < path.Length && char.IsUpper(path[idx]))
                return true;
        }
        return false;
    }

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request)
    {
        var matched = SelectMatchingProviders(NamespacesForRequest(request));
        return MergeProviderObservables(
            matched.Select(p => p.ObserveQuery<T>(request, Options)).ToList(),
            request);
    }

    /// <summary>
    /// Unsecured fan-out — same merge as the secured surface, but each
    /// provider that implements <see cref="IMeshQueryCore"/> is invoked
    /// through that surface (skipping per-result validators). Providers
    /// that don't implement <c>IMeshQueryCore</c> (e.g. static-node
    /// catalogs) fall through to their regular surface because they have
    /// no security to bypass anyway.
    /// </summary>
    IObservable<QueryResultChange<T>> IMeshQueryCore.ObserveQuery<T>(
        MeshQueryRequest request,
        JsonSerializerOptions options)
    {
        var matched = SelectMatchingProviders(NamespacesForRequest(request));
        return MergeProviderObservables(
            matched.Select(p => p is IMeshQueryCore core
                ? core.ObserveQuery<T>(request, options)
                : p.ObserveQuery<T>(request, options)).ToList(),
            request);
    }

    /// <summary>
    /// Centralised provider gating — every fan-out in this class
    /// (<see cref="ObserveQuery{T}(MeshQueryRequest)"/>, the
    /// <see cref="IMeshQueryCore"/> surface, both <see cref="AutocompleteAsync(string, string, int, CancellationToken)"/>
    /// overloads, <see cref="SelectAsync{T}"/>) MUST go through this so a
    /// scoped query only subscribes / awaits providers that actually own
    /// (or claim) the partition. For a single-node-by-path lookup this
    /// typically resolves to ONE provider; the merge then waits on exactly
    /// that provider's Initial frame, so a stalled or irrelevant provider
    /// can't hold the merge hostage. Unscoped queries (no <c>namespace:</c>
    /// condition and no <c>path:</c> filter) fan to every provider — the
    /// <see cref="IMeshQueryProvider.Matches"/> contract documents this.
    /// </summary>
    private IReadOnlyList<IMeshQueryProvider> SelectMatchingProviders(IReadOnlyList<string> namespaces)
        => namespaces.Count == 0
            ? providers
            : providers.Where(p => p.Matches(namespaces)).ToList();

    /// <summary>
    /// Computes the namespace candidates for a <see cref="MeshQueryRequest"/>
    /// using its first effective query. Multi-query unions still subscribe
    /// every provider that matches ANY query's namespaces because the
    /// providers themselves handle each query independently — the catalog /
    /// per-instance fan-outs use a single query.
    /// </summary>
    private static IReadOnlyList<string> NamespacesForRequest(MeshQueryRequest request)
    {
        var firstQuery = request.EffectiveQueries.FirstOrDefault();
        if (string.IsNullOrEmpty(firstQuery))
            return Array.Empty<string>();
        var parsed = new QueryParser().Parse(firstQuery);
        return MergeQueryNamespaces(parsed);
    }

    /// <summary>
    /// Computes the namespace candidates for an autocomplete-style call
    /// (basePath + prefix). The aggregator extracts the first segment of
    /// basePath as the partition candidate; an empty basePath is unscoped
    /// (every provider participates).
    /// </summary>
    private static IReadOnlyList<string> NamespacesForBasePath(string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return Array.Empty<string>();
        var firstSegment = basePath.TrimStart('/').Split('/', 2)[0];
        return string.IsNullOrEmpty(firstSegment)
            ? Array.Empty<string>()
            : new[] { firstSegment };
    }

    private IObservable<QueryResultChange<T>> MergeProviderObservables<T>(
        List<IObservable<QueryResultChange<T>>> observables,
        MeshQueryRequest request)
    {
        // Collect Initial from all providers, merge into a single Initial emission,
        // then forward subsequent (non-Initial) changes from ongoing providers.
        if (observables.Count == 0)
            return Observable.Empty<QueryResultChange<T>>();

        if (observables.Count == 1)
        {
            // Single provider — still funnel Initial through ClipMergedInitial
            // so request.Skip / request.Limit are applied. The provider itself
            // yields up to (Skip + Limit) items as a load cap (see
            // StorageAdapterMeshQueryProvider.QueryAsync) and defers the
            // final paging to the merge layer; bypassing ClipMergedInitial
            // here returned all (Skip + Limit) items instead of the trimmed
            // Limit window (repro:
            // HierarchicalBrowsingTests.QueryAsync_Generic_WithPaging_ReturnsPagedResults
            // — 10 items created, Skip=3 Limit=3, got 6 = Skip+Limit instead
            // of the expected 3).
            return observables[0].Select(change => change.ChangeType == QueryChangeType.Initial
                ? ClipMergedInitial<T>(change.Items.ToList(), change, change.Query!, request)
                : change);
        }

        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            // Initial-merge dedupes by MeshNode.Path (mirroring QueryAsync's
            // ConcurrentDictionary<string, byte> dedup) so duplicate registrations
            // of the same provider — or two providers that both happen to surface
            // a static node — don't surface as duplicate rows in the GUI.
            // For non-MeshNode T, fall back to reference identity.
            //
            // Per-provider buckets — kept separate until the final emission so the
            // merge can order writable-persistence ahead of the static catalog
            // (otherwise a `scope:descendants limit:N` query risks the static
            // node-type entries crowding out the actual user content).
            var providerItems = new List<T>[observables.Count];
            for (var k = 0; k < providerItems.Length; k++) providerItems[k] = new List<T>();
            var initialPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var initialIdentities = new HashSet<T>();
            var initialCount = 0;
            var initialTarget = observables.Count;
            ParsedQuery? lastQuery = null;
            var gate = new object();
            var providerIsStatic = providers
                .Select(p => p is StaticNodeQueryProvider)
                .ToArray();

            // Live-stream dedup: track Path → ChangeType so a Removed for a path
            // we never Added is dropped, and an Added for a path that's already
            // in the live set is dropped (same provider re-emitted, or overlapping
            // providers both saw the change).
            var liveItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var subscriptions = new List<IDisposable>();

            for (var i = 0; i < observables.Count; i++)
            {
                var obs = observables[i];
                var idx = i;
                var sub = obs.Subscribe(
                    change =>
                    {
                        if (change.ChangeType == QueryChangeType.Initial)
                        {
                            lock (gate)
                            {
                                foreach (var item in change.Items)
                                {
                                    if (item is MeshNode node)
                                    {
                                        if (!string.IsNullOrEmpty(node.Path)
                                            && !initialPaths.Add(node.Path))
                                            continue;
                                        providerItems[idx].Add(item);
                                    }
                                    else if (initialIdentities.Add(item))
                                    {
                                        providerItems[idx].Add(item);
                                    }
                                }
                                lastQuery ??= change.Query;
                                initialCount++;

                                if (initialCount == initialTarget)
                                {
                                    foreach (var path in initialPaths)
                                        liveItems.Add(path);
                                    // Engine / writable persistence first, static catalog last,
                                    // so request-level Limit cuts off the static tail rather
                                    // than user content.
                                    var ordered = new List<T>();
                                    for (var p = 0; p < providerItems.Length; p++)
                                        if (!providerIsStatic[p])
                                            ordered.AddRange(providerItems[p]);
                                    for (var p = 0; p < providerItems.Length; p++)
                                        if (providerIsStatic[p])
                                            ordered.AddRange(providerItems[p]);
                                    var clipped = ClipMergedInitial<T>(
                                        ordered, change, lastQuery!, request);
                                    observer.OnNext(clipped);
                                }
                            }
                        }
                        else
                        {
                            lock (gate)
                            {
                                if (TryFilterDuplicateLiveChange(change, liveItems, out var filtered))
                                    observer.OnNext(filtered);
                            }
                        }
                    },
                    ex => observer.OnError(ex));

                subscriptions.Add(sub);
            }

            return new System.Reactive.Disposables.CompositeDisposable(subscriptions);
        });
    }

    /// <summary>
    /// Sort + skip + clip the merged initial set. Mirrors the post-collect
    /// pipeline that <see cref="StorageAdapterMeshQueryProvider.QueryAsync"/> runs per-provider.
    /// Also applies <c>select:</c> projection: static-node providers don't
    /// project to dictionaries on their own, so merging engine projections with
    /// raw static MeshNodes left mixed-shape results for callers.
    /// </summary>
    private static QueryResultChange<T> ClipMergedInitial<T>(
        List<T> items,
        QueryResultChange<T> change,
        ParsedQuery parsed,
        MeshQueryRequest request)
    {
        IEnumerable<T> merged = items;
        if (parsed.OrderBy is { } orderBy)
        {
            var evaluator = new QueryEvaluator();
            merged = evaluator.OrderResults(merged.OfType<MeshNode>(), orderBy).OfType<T>();
        }
        if (request.Skip is int skip && skip > 0)
            merged = merged.Skip(skip);
        var effectiveLimit = request.Limit ?? parsed.Limit;
        if (effectiveLimit is int limit && limit > 0)
            merged = merged.Take(limit);
        if (parsed.Select is { } select)
        {
            // Project each MeshNode through select; for non-MeshNode inputs
            // (already-projected dicts coming from the engine) leave alone.
            merged = merged.Select(item => item is MeshNode node
                ? (T)(object)ParsedQuery.ProjectToSelect(node, select)
                : item);
        }
        return change with { Items = merged.ToList() };
    }

    /// <summary>
    /// Strips items from a non-Initial change that are duplicates against the
    /// live dedup set (overlapping provider emitted the same change again, or
    /// a Removed arrived for a path we never Added). Returns false when the
    /// change has no usable items left, so the caller can drop the emission.
    /// </summary>
    private static bool TryFilterDuplicateLiveChange<T>(
        QueryResultChange<T> change,
        HashSet<string> liveItems,
        out QueryResultChange<T> filtered)
    {
        var kept = new List<T>(change.Items.Count);
        foreach (var item in change.Items)
        {
            if (item is not MeshNode node || string.IsNullOrEmpty(node.Path))
            {
                kept.Add(item);
                continue;
            }
            switch (change.ChangeType)
            {
                case QueryChangeType.Added or QueryChangeType.Updated:
                    if (liveItems.Add(node.Path))
                        kept.Add(item);
                    else if (change.ChangeType == QueryChangeType.Updated)
                        kept.Add(item); // updates flow through even if path already known
                    break;
                case QueryChangeType.Removed:
                    if (liveItems.Remove(node.Path))
                        kept.Add(item);
                    break;
                default:
                    kept.Add(item);
                    break;
            }
        }
        if (kept.Count == 0)
        {
            filtered = change;
            return false;
        }
        filtered = change with { Items = kept };
        return true;
    }

    public Task<T?> SelectAsync<T>(string path, string property, CancellationToken ct = default)
    {
        var matched = SelectMatchingProviders(NamespacesForBasePath(path));
        // Merge each provider's Task<T?> via Observable.FromAsync, take the first
        // non-null. No Task.WhenAll, no captured-scheduler awaits — the public
        // Task surface is built from the observable's first non-null emission via
        // FirstOrDefaultAsync (the framework primitive that bridges IObservable
        // → Task without forcing ConfigureAwait gymnastics on caller code).
        return matched
            .Select(p => Observable.FromAsync(token => p.SelectAsync<T>(path, property, Options, token)))
            .Merge()
            .Where(r => r is not null)
            .FirstOrDefaultAsync()
            .ToTask(ct);
    }

}
