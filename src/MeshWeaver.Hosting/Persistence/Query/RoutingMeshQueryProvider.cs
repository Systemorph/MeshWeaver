using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Query provider that routes queries to the appropriate partition based on parsed path/namespace.
/// When no path is specified, fans out to all partitions the user can access and merges results.
/// Queries already-known partitions immediately while discovering new ones in parallel.
/// </summary>
internal class RoutingMeshQueryProvider : IMeshQueryProvider
{
    private readonly RoutingPersistenceServiceCore _router;
    private readonly MeshConfiguration? _meshConfig;
    private readonly ICrossSchemaQueryProvider? _crossSchemaProvider;
    private readonly AccessService? _accessService;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly ILogger? _logger;
    private readonly QueryParser _parser = new();

    /// <summary>
    /// Limits concurrent partition queries during fan-out to prevent pool exhaustion.
    /// With 23+ schemas, unrestricted parallelism exhausts the shared connection pool.
    /// </summary>
    private static readonly SemaphoreSlim FanOutThrottle = new(20, 20);

    /// <summary>
    /// Default autocomplete sort: shortest path first (top-level partitions
    /// beat deep descendants), highest score next, then by name. Matches the
    /// pre-streaming-fan-out behaviour — the OLD code did this in an
    /// <c>OrderBy(Path.Length).ThenByDescending(Score).ThenBy(Name)</c> at
    /// the end of <c>Task.WhenAll</c>; the new streaming chain pipes items
    /// through <c>CollectTopNAsync</c> with this comparer to maintain the
    /// same final ordering while letting fast partitions emit early.
    /// </summary>
    private static readonly IComparer<QuerySuggestion> AutocompleteByPathLengthThenScore =
        Comparer<QuerySuggestion>.Create((a, b) =>
        {
            var c = a.Path.Length.CompareTo(b.Path.Length);
            if (c != 0) return c;
            c = b.Score.CompareTo(a.Score); // higher score first
            if (c != 0) return c;
            return string.CompareOrdinal(a.Name, b.Name);
        });

    /// <summary>
    /// RelevanceFirst variant: highest score first, then shortest path, then
    /// by name. Used by chat / search consumers that prioritise fuzzy-match
    /// quality over hierarchy depth.
    /// </summary>
    private static readonly IComparer<QuerySuggestion> AutocompleteByScoreThenPathLength =
        Comparer<QuerySuggestion>.Create((a, b) =>
        {
            var c = b.Score.CompareTo(a.Score); // higher score first
            if (c != 0) return c;
            c = a.Path.Length.CompareTo(b.Path.Length);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Name, b.Name);
        });



    public RoutingMeshQueryProvider(
        RoutingPersistenceServiceCore router,
        MeshConfiguration? meshConfig = null,
        ICrossSchemaQueryProvider? crossSchemaProvider = null,
        AccessService? accessService = null,
        IDataChangeNotifier? changeNotifier = null,
        ILogger<RoutingMeshQueryProvider>? logger = null)
    {
        _router = router;
        _meshConfig = meshConfig;
        _crossSchemaProvider = crossSchemaProvider;
        _accessService = accessService;
        _changeNotifier = changeNotifier;
        _logger = logger;
    }

    private string GetEffectiveUserId()
    {
        // Try thread-local context first, then circuit/session context
        var ctx = _accessService?.Context?.ObjectId;
        var circuit = _accessService?.CircuitContext?.ObjectId;
        var userId = ctx ?? circuit;
        _logger?.LogDebug("[UserId] Context={Context}, CircuitContext={Circuit}, Effective={Effective}",
            ctx ?? "(null)", circuit ?? "(null)", userId ?? "Anonymous");
        return string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId;
    }

    // Partition-level access control is enforced in SQL via public.partition_access JOIN
    // in PostgreSqlSqlGenerator.GenerateAccessControlClause. No in-memory filtering needed.

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parsed = _parser.Parse(request.Query);

        // Apply routing rules to resolve partition/table hints
        var hints = _meshConfig?.ResolveRoutingHints(parsed) ?? new QueryRoutingHints();
        var enrichedRequest = (hints.Partition != null || hints.Table != null)
            ? request with { PartitionHint = hints.Partition, TableHint = hints.Table }
            : request;

        var effectivePath = parsed.Path ?? request.DefaultPath;
        var segment = hints.Partition
            ?? PathPartition.GetFirstSegment(effectivePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            _logger?.LogDebug("[QueryRoute] Single partition: {Segment}, query={Query}", segment, request.Query);
            await foreach (var item in provider.QueryAsync(enrichedRequest, options, ct))
                yield return item;
            yield break;
        }

        _logger?.LogDebug("[QueryRoute] Global fan-out, crossSchema={HasCrossSchema}, query={Query}",
            _crossSchemaProvider != null, request.Query);

        // Build fan-out query: search full partition trees.
        // Exclude satellite nodes (is:main) unless the query has a specific filter
        // (e.g., nodeType:Thread) — filtered queries need to find satellites.
        var fanOutQuery = request.Query ?? "";
        if (parsed.Scope == QueryScope.Exact)
            fanOutQuery += " scope:subtree";
        if (parsed.IsMain != true && !parsed.HasConditions)
            fanOutQuery += " is:main";

        // Cross-schema query: single UNION ALL across all searchable schemas (PostgreSQL only).
        // Works for both mesh_nodes and satellite tables (threads, activities, etc.).
        var satelliteNodeType = parsed.ExtractNodeType() is { } nt
            && PartitionDefinition.NodeTypeToSuffix.ContainsKey(nt);
        var useCrossSchema = _crossSchemaProvider != null
            && parsed.Source == QuerySource.Default;
        if (useCrossSchema)
        {
            var schemas = await _crossSchemaProvider!.GetSearchableSchemasAsync(ct);
            var crossParsed = _parser.Parse(fanOutQuery);
            var userId = GetEffectiveUserId();

            // Resolve satellite table name if applicable
            string? satelliteTable = null;
            if (satelliteNodeType && parsed.ExtractNodeType() is { } nodeType)
            {
                var suffix = PartitionDefinition.NodeTypeToSuffix[nodeType];
                PartitionDefinition.StandardTableMappings.TryGetValue(suffix, out satelliteTable);
            }

            _logger?.LogDebug("Cross-schema query: {Query}, table={Table}, schemas=[{Schemas}], userId={UserId}",
                fanOutQuery, satelliteTable ?? "mesh_nodes", string.Join(",", schemas), userId);

            var crossResults = new List<object>();
            var crossStream = satelliteTable != null
                ? _crossSchemaProvider.QueryAcrossSchemasAsync(crossParsed, options, schemas, satelliteTable, userId, ct)
                : _crossSchemaProvider.QueryAcrossSchemasAsync(crossParsed, options, schemas, userId, ct);
            await foreach (var node in crossStream)
            {
                crossResults.Add(node);
            }

            var crossLimit = request.Limit ?? parsed.Limit;
            IEnumerable<object> crossSorted = crossResults;
            if (parsed.OrderBy != null)
            {
                var evaluator = new QueryEvaluator();
                crossSorted = evaluator.OrderResults(crossResults.OfType<MeshNode>(), parsed.OrderBy).Cast<object>();
            }
            if (crossLimit.HasValue)
                crossSorted = crossSorted.Take(crossLimit.Value);

            foreach (var item in crossSorted)
                yield return item;

            // Also query in-memory partitions not covered by cross-schema (e.g., static Doc partition)
            var searchableSchemas = new HashSet<string>(schemas, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, p) in _router.QueryProviders)
            {
                if (searchableSchemas.Contains(key)) continue; // Already queried via cross-schema
                await foreach (var item in p.QueryAsync(
                    request with { Query = fanOutQuery }, options, ct))
                {
                    yield return item;
                }
            }
            yield break;
        }

        // Fallback: per-partition fan-out (non-PostgreSQL or when cross-schema not available).
        // Streaming: each partition emits into a Channel; the iterator yields results as
        // they arrive. Fast partitions deliver first; slow ones don't block fast ones.
        // No per-partition timeout — consumer (e.g. ScanTopN snapshot, MeshSearchView)
        // decides when to stop reading. Cross-partition dedup-by-Path applies on the
        // consumer side. Global ordering used to be applied across the merged
        // ConcurrentBag after Task.WhenAll; with streaming each partition's own
        // ordering passes through and global re-sort is the consumer's job.
        _logger?.LogDebug("Fan-out query: {Query}, providers={Count}, effectivePath={Path}",
            fanOutQuery, _router.QueryProviders.Count, effectivePath);

        var globalLimit = request.Limit ?? parsed.Limit;

        // Snapshot the provider set + queue any newly-discovered ones into the same
        // streaming fan-out, so a partition activated mid-query still streams results
        // (matches the previous discover-then-WhenAll behaviour, just streaming).
        var initialProviders = _router.QueryProviders.ToList();
        var allProviders = new List<KeyValuePair<string, IMeshQueryProvider>>(initialProviders);
        await foreach (var (key, p) in _router.DiscoverNewProvidersAsync(ct))
        {
            _logger?.LogDebug("Fan-out: discovered new partition {Key}", key);
            allProviders.Add(new KeyValuePair<string, IMeshQueryProvider>(key, p));
        }

        var providerDict = allProviders.ToDictionary(kv => kv.Key, kv => kv.Value);

        // ORDER BY needs cross-partition sort — buffer all results, sort, then yield.
        // Without ORDER BY the natural emission is arrival order from the streaming fan-out
        // (the previous shape used a ConcurrentBag whose iteration was also arrival-ish).
        if (parsed.OrderBy != null)
        {
            var allResults = new List<object>();
            await foreach (var item in StreamFanOutAsync<object>(providerDict, searchableSchemas: null,
                (partitionKey, p, partitionCt) =>
                {
                    var scopedRequest = string.IsNullOrEmpty(effectivePath)
                        ? enrichedRequest with { Query = fanOutQuery }
                        : enrichedRequest;
                    return p.QueryAsync(scopedRequest, options, partitionCt);
                }, ct))
            {
                allResults.Add(item);
            }

            var evaluator = new QueryEvaluator();
            IEnumerable<object> sorted = evaluator.OrderResults(allResults.OfType<MeshNode>(), parsed.OrderBy).Cast<object>();
            if (globalLimit.HasValue)
                sorted = sorted.Take(globalLimit.Value);

            var seenOrdered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in sorted)
            {
                if (item is MeshNode node && !seenOrdered.Add(node.Path))
                    continue;
                yield return item;
            }
            yield break;
        }

        // No ORDER BY: stream as available. Cross-partition dedupe-by-Path applies.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = 0;
        await foreach (var item in StreamFanOutAsync<object>(providerDict, searchableSchemas: null,
            (partitionKey, p, partitionCt) =>
            {
                var scopedRequest = string.IsNullOrEmpty(effectivePath)
                    ? enrichedRequest with { Query = fanOutQuery }
                    : enrichedRequest;
                return p.QueryAsync(scopedRequest, options, partitionCt);
            }, ct))
        {
            if (item is MeshNode node && !seen.Add(node.Path))
                continue;
            yield return item;
            if (globalLimit.HasValue && ++emitted >= globalLimit.Value)
                yield break;
        }
    }

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(basePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            await foreach (var s in provider.AutocompleteAsync(basePath, prefix, options, limit, ct))
                yield return s;
            yield break;
        }

        // Discover and provision any new partitions (lazy init)
        await foreach (var _ in _router.DiscoverNewProvidersAsync(ct))
        { /* provisioning happens as a side effect */ }

        // Fan out: only to searchable partitions (excludes Admin, Portal, Kernel).
        var searchableSchemas = _crossSchemaProvider != null
            ? (await _crossSchemaProvider.GetSearchableSchemasAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        // Streaming fan-out + sorted top-N snapshot: partitions write into a
        // Channel as they produce; ScanTopN folds the merged stream into an
        // ImmutableList<T> kept sorted by the injected comparer
        // (path-length-first by default — matches the pre-streaming shape
        // `OrderBy(Path.Length).Then(Score).Then(Name).Take(limit)`). Fast
        // partitions don't wait for slow ones; the comparer guarantees deep
        // "loose match" candidates can't displace top-level partitions in
        // the final snapshot. We `LastOrDefaultAsync` to surface the final
        // snapshot at the IAsyncEnumerable boundary; consumers that want
        // streaming snapshots can subscribe to ScanTopN themselves at the
        // observable surface (see BlazorAutocompleteService for the live-
        // databinding shape).
        var snapshot = await StreamFanOutAsync(_router.QueryProviders, searchableSchemas,
                (partitionKey, p, partitionCt) =>
                {
                    var effectiveBasePath = string.IsNullOrEmpty(basePath) ? partitionKey : basePath;
                    return p.AutocompleteAsync(effectiveBasePath, prefix, options, limit, partitionCt);
                }, ct)
            .ScanTopN(limit, AutocompleteByPathLengthThenScore)
            .LastOrDefaultAsync();
        foreach (var s in snapshot ?? Array.Empty<QuerySuggestion>())
            yield return s;
    }

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(basePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            await foreach (var s in provider.AutocompleteAsync(basePath, prefix, options, mode, limit, contextPath, context, ct))
                yield return s;
            yield break;
        }

        // Discover and provision any new partitions (lazy init)
        await foreach (var _ in _router.DiscoverNewProvidersAsync(ct))
        { /* provisioning happens as a side effect */ }

        // Fan out: only to searchable partitions (excludes Admin, Portal, Kernel).
        var searchableSchemas = _crossSchemaProvider != null
            ? (await _crossSchemaProvider.GetSearchableSchemasAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        // Streaming fan-out + sorted top-N — see comment on the other
        // AutocompleteAsync overload. RelevanceFirst mode uses the
        // score-first comparer so high-score fuzzy matches win over
        // shallow paths with weak scores.
        var comparer = mode switch
        {
            AutocompleteMode.RelevanceFirst => AutocompleteByScoreThenPathLength,
            _ => AutocompleteByPathLengthThenScore,
        };
        var snapshot = await StreamFanOutAsync(_router.QueryProviders, searchableSchemas,
                (partitionKey, p, partitionCt) =>
                {
                    var effectiveBasePath = string.IsNullOrEmpty(basePath) ? partitionKey : basePath;
                    return p.AutocompleteAsync(effectiveBasePath, prefix, options, mode, limit, contextPath, context, partitionCt);
                }, ct)
            .ScanTopN(limit, comparer)
            .LastOrDefaultAsync();
        foreach (var s in snapshot ?? Array.Empty<QuerySuggestion>())
            yield return s;
    }

    /// <summary>
    /// Streaming fan-out helper. Each partition runs its iteration on a
    /// background continuation; results flow into a Channel and the iterator
    /// yields them as they arrive. Fast partitions emit immediately — slow ones
    /// don't block fast ones, and no per-partition timeout is needed because
    /// the consumer can stop reading at any point.
    /// </summary>
    private static async IAsyncEnumerable<T> StreamFanOutAsync<T>(
        IReadOnlyDictionary<string, IMeshQueryProvider> providers,
        HashSet<string>? searchableSchemas,
        Func<string, IMeshQueryProvider, CancellationToken, IAsyncEnumerable<T>> getPartitionResults,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Snapshot the matching providers FIRST so we know the exact count
        // before any task can complete. The previous shape did
        // `Increment(ref pending); StartTask()` per partition in the loop,
        // which races: the first task may run synchronously up to its first
        // `await` and decrement `pending` to 0 — closing the channel before
        // the next partition is spawned. Symptom: only the first partition's
        // results made it through (failing autocomplete tests with 1 result
        // instead of N).
        var matching = new List<KeyValuePair<string, IMeshQueryProvider>>();
        foreach (var kv in providers)
        {
            if (searchableSchemas != null && !searchableSchemas.Contains(kv.Key.ToLowerInvariant()))
                continue;
            matching.Add(kv);
        }

        var channel = Channel.CreateUnbounded<T>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        if (matching.Count == 0)
        {
            channel.Writer.TryComplete();
        }
        else
        {
            // pending starts at the FULL partition count — set before any
            // task runs, so a fast partition's decrement can't drop it to
            // zero prematurely. Each task decrements exactly once on
            // completion; the last one closes the channel.
            var pending = matching.Count;
            foreach (var (key, p) in matching)
                _ = StreamOneAsync(key, p);

            async Task StreamOneAsync(string partitionKey, IMeshQueryProvider p)
            {
                try { await FanOutThrottle.WaitAsync(ct); }
                catch (OperationCanceledException) { goto done; }

                try
                {
                    await foreach (var item in getPartitionResults(partitionKey, p, ct))
                        channel.Writer.TryWrite(item);
                }
                catch (OperationCanceledException) { /* caller cancelled */ }
                catch (Exception) { /* don't kill other partitions */ }
                finally { FanOutThrottle.Release(); }

            done:
                if (Interlocked.Decrement(ref pending) == 0)
                    channel.Writer.TryComplete();
            }
        }

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
            yield return item;
    }

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        var parsed = _parser.Parse(request.Query);

        // Apply routing rules to narrow partition
        var hints = _meshConfig?.ResolveRoutingHints(parsed) ?? new QueryRoutingHints();

        var effectivePath = parsed.Path ?? request.DefaultPath;
        var segment = hints.Partition
            ?? PathPartition.GetFirstSegment(effectivePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            return provider.ObserveQuery<T>(request, options);
        }

        // Build fan-out query: search full partition trees.
        // Exclude satellite nodes (is:main) unless the query has a specific filter
        // (e.g., nodeType:Thread) — filtered queries need to find satellites.
        var fanOutQuery = request.Query ?? "";
        if (parsed.Scope == QueryScope.Exact)
            fanOutQuery += " scope:subtree";
        if (parsed.IsMain != true && !parsed.HasConditions)
            fanOutQuery += " is:main";

        // Fan out to all partitions (known + newly discovered), merge observables
        return Observable.Create<QueryResultChange<T>>(async (observer, ct) =>
        {
            // Collect all providers with their keys: already-known + newly discovered
            var allProviders = new List<(string Key, IMeshQueryProvider Provider)>(
                _router.QueryProviders.Select(kvp => (kvp.Key, kvp.Value)));
            await foreach (var entry in _router.DiscoverNewProvidersAsync(ct))
                allProviders.Add(entry);

            if (allProviders.Count == 0)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            if (allProviders.Count == 1)
            {
                var (_, prov) = allProviders[0];
                // Don't inject DefaultPath — schema isolation already scopes data per partition,
                // and lowercase partition keys don't match proper-cased actual paths.
                var scopedReq = string.IsNullOrEmpty(effectivePath)
                    ? request with { Query = fanOutQuery }
                    : request;
                return prov.ObserveQuery<T>(scopedReq, options).Subscribe(observer);
            }

            var observables = allProviders
                .Select(entry =>
                {
                    var scopedReq = string.IsNullOrEmpty(effectivePath)
                        ? request with { Query = fanOutQuery }
                        : request;
                    return entry.Provider.ObserveQuery<T>(scopedReq, options);
                })
                .ToList();

            var initialItems = new List<T>();
            var initialCount = 0;
            var initialTarget = observables.Count;
            var gate = new object();
            var fanOutParsed = _parser.Parse(fanOutQuery);
            var globalLimit = request.Limit ?? fanOutParsed.Limit;

            var subscriptions = new List<IDisposable>();

            foreach (var obs in observables)
            {
                var sub = obs.Subscribe(
                    change =>
                    {
                        if (change.ChangeType == QueryChangeType.Initial)
                        {
                            lock (gate)
                            {
                                initialItems.AddRange(change.Items);
                                initialCount++;

                                if (initialCount == initialTarget)
                                {
                                    // Re-sort merged results across all partitions
                                    IEnumerable<T> merged = initialItems;
                                    if (fanOutParsed.OrderBy != null)
                                    {
                                        var evaluator = new QueryEvaluator();
                                        merged = evaluator.OrderResults(merged, fanOutParsed.OrderBy);
                                    }
                                    if (globalLimit.HasValue)
                                        merged = merged.Take(globalLimit.Value);

                                    observer.OnNext(change with { Items = merged.ToList() });
                                }
                            }
                        }
                        else
                        {
                            observer.OnNext(change);
                        }
                    },
                    ex => observer.OnError(ex));

                subscriptions.Add(sub);
            }

            return new CompositeDisposable(subscriptions);
        });
    }

    public async Task<T?> SelectAsync<T>(string path, string property, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var segment = PathPartition.GetFirstSegment(path);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            return await provider.SelectAsync<T>(path, property, options, ct);
        }

        // Fan out: known partitions + newly discovered, all in parallel
        var tasks = new ConcurrentBag<Task<T?>>();

        foreach (var (_, p) in _router.QueryProviders)
            tasks.Add(p.SelectAsync<T>(path, property, options, ct));

        await foreach (var (_, p) in _router.DiscoverNewProvidersAsync(ct))
            tasks.Add(p.SelectAsync<T>(path, property, options, ct));

        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(r => r != null);
    }
}
