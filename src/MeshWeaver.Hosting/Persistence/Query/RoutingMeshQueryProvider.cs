using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MeshWeaver.Mesh;
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
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly ILogger? _logger;
    private readonly QueryParser _parser = new();

    /// <summary>
    /// Limits concurrent partition queries during fan-out to prevent pool exhaustion.
    /// With 23+ schemas, unrestricted parallelism exhausts the shared connection pool.
    /// </summary>
    private static readonly SemaphoreSlim FanOutThrottle = new(5, 5);

    /// <summary>
    /// Latest-wins context for autocomplete — cancels previous in-flight fan-out
    /// when a new autocomplete request arrives (user types faster than queries complete).
    /// </summary>
    private readonly QueryContext _autocompleteContext = new();

    /// <summary>
    /// Latest-wins context for search queries — cancels previous fan-out
    /// when a new search request arrives.
    /// </summary>
    private readonly QueryContext _queryContext = new();

    public RoutingMeshQueryProvider(
        RoutingPersistenceServiceCore router,
        MeshConfiguration? meshConfig = null,
        IDataChangeNotifier? changeNotifier = null,
        ILogger<RoutingMeshQueryProvider>? logger = null)
    {
        _router = router;
        _meshConfig = meshConfig;
        _changeNotifier = changeNotifier;
        _logger = logger;

    }

    // Partition-level access control is enforced in SQL via public.partition_access JOIN
    // in PostgreSqlSqlGenerator.GenerateAccessControlClause. No in-memory filtering needed.

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Cancel any previous in-flight fan-out query
        ct = _queryContext.StartNew(ct);

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
            // Route to specific partition (from path or routing rules)
            await foreach (var item in provider.QueryAsync(enrichedRequest, options, ct))
                yield return item;
            yield break;
        }

        // Build fan-out query: search full partition trees.
        // Exclude satellite nodes (is:main) unless the query has a specific filter
        // (e.g., nodeType:Thread) — filtered queries need to find satellites.
        var fanOutQuery = request.Query ?? "";
        if (parsed.Scope == QueryScope.Exact)
            fanOutQuery += " scope:subtree";
        if (parsed.IsMain != true && !parsed.HasConditions)
            fanOutQuery += " is:main";

        // Fan out: query all partitions in parallel, collect all results,
        // then re-sort and apply the global limit. Each partition applies its own
        // ORDER BY + LIMIT, but the merge must re-sort to produce correct global ordering.
        // Partition-level access control is enforced in SQL (public.partition_access JOIN).
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var allResults = new ConcurrentBag<object>();

        var queryTasks = new List<Task>();
        foreach (var (key, p) in _router.QueryProviders)
        {
            queryTasks.Add(QueryOneAsync(key, p));
        }

        // Discover new partitions and query each as it becomes available
        await foreach (var (key, p) in _router.DiscoverNewProvidersAsync(ct))
            queryTasks.Add(QueryOneAsync(key, p));

        await Task.WhenAll(queryTasks);

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
            await FanOutThrottle.WaitAsync(ct);
            try
            {
                var scopedRequest = string.IsNullOrEmpty(effectivePath)
                    ? enrichedRequest with { DefaultPath = partitionKey, Query = fanOutQuery }
                    : enrichedRequest;

                await foreach (var item in p.QueryAsync(scopedRequest, options, ct))
                {
                    if (item is MeshNode node && !seen.TryAdd(node.Path, 0))
                        continue;
                    allResults.Add(item);
                }
            }
            catch (OperationCanceledException) { /* silent */ }
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
        // Cancel any previous in-flight autocomplete fan-out
        ct = _autocompleteContext.StartNew(ct);

        var segment = PathPartition.GetFirstSegment(basePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            await foreach (var s in provider.AutocompleteAsync(basePath, prefix, options, limit, ct))
                yield return s;
            yield break;
        }

        // Fan out: query all partitions. Access control enforced in SQL.
        // Limit concurrency to avoid pool exhaustion on keystroke-driven autocomplete.
        var all = new ConcurrentBag<QuerySuggestion>();
        var tasks = new ConcurrentBag<Task>();

        foreach (var (key, p) in _router.QueryProviders)
        {
            tasks.Add(AutocompleteOneAsync(key, p));
        }

        await foreach (var (key, p) in _router.DiscoverNewProvidersAsync(ct))
            tasks.Add(AutocompleteOneAsync(key, p));

        await Task.WhenAll(tasks);

        foreach (var s in all.OrderBy(s => s.Path.Length).ThenByDescending(s => s.Score).ThenBy(s => s.Name).Take(limit))
            yield return s;

        async Task AutocompleteOneAsync(string partitionKey, IMeshQueryProvider p)
        {
            await FanOutThrottle.WaitAsync(ct);
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
        // Cancel any previous in-flight autocomplete fan-out
        ct = _autocompleteContext.StartNew(ct);

        var segment = PathPartition.GetFirstSegment(basePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            await foreach (var s in provider.AutocompleteAsync(basePath, prefix, options, mode, limit, contextPath, context, ct))
                yield return s;
            yield break;
        }

        // Fan out: query all partitions. Access control enforced in SQL.
        var all = new ConcurrentBag<QuerySuggestion>();
        var tasks = new ConcurrentBag<Task>();

        foreach (var (key, p) in _router.QueryProviders)
        {
            tasks.Add(AutocompleteOneAsync(key, p));
        }

        await foreach (var (key, p) in _router.DiscoverNewProvidersAsync(ct))
            tasks.Add(AutocompleteOneAsync(key, p));

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
            await FanOutThrottle.WaitAsync(ct);
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
                                    observer.OnNext(change with { Items = initialItems.ToList() });
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
