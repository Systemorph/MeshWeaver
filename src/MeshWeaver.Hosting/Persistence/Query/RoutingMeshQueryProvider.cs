using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
        // Only for queries on mesh_nodes — satellite queries (source:activity, source:accessed)
        // and satellite node types (Thread, ThreadMessage) require per-partition fan-out
        // because the stored proc only searches mesh_nodes.
        var satelliteNodeType = parsed.ExtractNodeType() is { } nt
            && PartitionDefinition.NodeTypeToSuffix.ContainsKey(nt);
        var useCrossSchema = _crossSchemaProvider != null
            && parsed.Source == QuerySource.Default
            && !satelliteNodeType;
        if (useCrossSchema)
        {
            var schemas = await _crossSchemaProvider!.GetSearchableSchemasAsync(ct);
            var crossParsed = _parser.Parse(fanOutQuery);
            var userId = GetEffectiveUserId();

            _logger?.LogDebug("Cross-schema query: {Query}, schemas=[{Schemas}], userId={UserId}",
                fanOutQuery, string.Join(",", schemas), userId);

            var crossResults = new List<object>();
            await foreach (var node in _crossSchemaProvider.QueryAcrossSchemasAsync(
                crossParsed, options, schemas, userId, ct))
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
            yield break;
        }

        // Fallback: per-partition fan-out (non-PostgreSQL or when cross-schema not available)
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var allResults = new ConcurrentBag<object>();

        _logger?.LogDebug("Fan-out query: {Query}, providers={Count}, effectivePath={Path}",
            fanOutQuery, _router.QueryProviders.Count, effectivePath);

        var queryTasks = new List<Task>();
        foreach (var (key, p) in _router.QueryProviders)
        {
            _logger?.LogDebug("Fan-out: querying partition {Key} ({ProviderType})", key, p.GetType().Name);
            queryTasks.Add(QueryOneAsync(key, p));
        }

        // Discover new partitions and query each as it becomes available
        await foreach (var (key, p) in _router.DiscoverNewProvidersAsync(ct))
        {
            _logger?.LogDebug("Fan-out: discovered new partition {Key}", key);
            queryTasks.Add(QueryOneAsync(key, p));
        }

        await Task.WhenAll(queryTasks);

        _logger?.LogDebug("Fan-out complete: {ResultCount} results from {TaskCount} partitions",
            allResults.Count, queryTasks.Count);

        // Re-sort merged results and apply global limit
        var globalLimit = request.Limit ?? parsed.Limit;
        IEnumerable<object> sorted = allResults;
        if (parsed.OrderBy != null)
        {
            var evaluator = new QueryEvaluator();
            sorted = evaluator.OrderResults(allResults.OfType<MeshNode>(), parsed.OrderBy).Cast<object>();
        }
        if (globalLimit.HasValue)
            sorted = sorted.Take(globalLimit.Value);

        foreach (var item in sorted)
            yield return item;

        yield break;

        async Task QueryOneAsync(string partitionKey, IMeshQueryProvider p)
        {
            try
            {
                await FanOutThrottle.WaitAsync(ct);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                var scopedRequest = string.IsNullOrEmpty(effectivePath)
                    ? enrichedRequest with { DefaultPath = partitionKey, Query = fanOutQuery }
                    : enrichedRequest;

                var count = 0;
                await foreach (var item in p.QueryAsync(scopedRequest, options, ct))
                {
                    if (item is MeshNode node && !seen.TryAdd(node.Path, 0))
                        continue;
                    allResults.Add(item);
                    count++;
                }
                if (count > 0)
                    _logger?.LogDebug("Fan-out: partition {Key} returned {Count} results", partitionKey, count);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Fan-out: partition {Key} CANCELLED", partitionKey);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Fan-out query to partition {Partition} failed", partitionKey);
            }
            finally
            {
                FanOutThrottle.Release();
            }
        }
    }

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Note: no latest-wins cancellation here — let queries complete.
        // The SemaphoreSlim throttle limits concurrency sufficiently.

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
        // Use searchable_schemas from cross-schema provider if available.
        var searchableSchemas = _crossSchemaProvider != null
            ? (await _crossSchemaProvider.GetSearchableSchemasAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var all = new ConcurrentBag<QuerySuggestion>();
        var tasks = new ConcurrentBag<Task>();

        foreach (var (key, p) in _router.QueryProviders)
        {
            // Skip non-searchable partitions (Admin, Portal, Kernel)
            if (searchableSchemas != null && !searchableSchemas.Contains(key.ToLowerInvariant()))
                continue;
            tasks.Add(AutocompleteOneAsync(key, p));
        }

        await Task.WhenAll(tasks);

        foreach (var s in all.OrderBy(s => s.Path.Length).ThenByDescending(s => s.Score).ThenBy(s => s.Name).Take(limit))
            yield return s;

        async Task AutocompleteOneAsync(string partitionKey, IMeshQueryProvider p)
        {
            try { await FanOutThrottle.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                var effectiveBasePath = string.IsNullOrEmpty(basePath) ? partitionKey : basePath;
                await foreach (var s in p.AutocompleteAsync(effectiveBasePath, prefix, options, limit, ct))
                    all.Add(s);
            }
            catch (OperationCanceledException) { /* silent */ }
            catch (Exception) { /* don't kill other partitions */ }
            finally { FanOutThrottle.Release(); }
        }
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
        // Note: no latest-wins cancellation here — let queries complete.
        // The SemaphoreSlim throttle limits concurrency sufficiently.

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

        var all = new ConcurrentBag<QuerySuggestion>();
        var tasks = new ConcurrentBag<Task>();

        foreach (var (key, p) in _router.QueryProviders)
        {
            if (searchableSchemas != null && !searchableSchemas.Contains(key.ToLowerInvariant()))
                continue;
            tasks.Add(AutocompleteOneAsync(key, p));
        }

        await Task.WhenAll(tasks);

        IEnumerable<QuerySuggestion> ordered = mode switch
        {
            AutocompleteMode.RelevanceFirst => all
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Path.Length)
                .ThenBy(s => s.Name),
            _ => all
                .OrderBy(s => s.Path.Length)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name)
        };

        foreach (var s in ordered.Take(limit))
            yield return s;

        async Task AutocompleteOneAsync(string partitionKey, IMeshQueryProvider p)
        {
            try { await FanOutThrottle.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                var effectiveBasePath = string.IsNullOrEmpty(basePath) ? partitionKey : basePath;
                await foreach (var s in p.AutocompleteAsync(effectiveBasePath, prefix, options, mode, limit, contextPath, context, ct))
                    all.Add(s);
            }
            catch (OperationCanceledException) { /* silent */ }
            catch (Exception) { /* don't kill other partitions */ }
            finally { FanOutThrottle.Release(); }
        }
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
                var (key, prov) = allProviders[0];
                var scopedReq = string.IsNullOrEmpty(effectivePath)
                    ? request with { DefaultPath = key, Query = fanOutQuery }
                    : request;
                return prov.ObserveQuery<T>(scopedReq, options).Subscribe(observer);
            }

            var observables = allProviders
                .Select(entry =>
                {
                    var scopedReq = string.IsNullOrEmpty(effectivePath)
                        ? request with { DefaultPath = entry.Key, Query = fanOutQuery }
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
