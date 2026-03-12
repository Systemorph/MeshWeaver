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
using Microsoft.Extensions.Caching.Memory;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Query provider that routes queries to the appropriate partition based on parsed path/namespace.
/// When no path is specified, fans out to all partitions the user can access and merges results.
/// Queries already-known partitions immediately while discovering new ones in parallel.
/// </summary>
internal class RoutingMeshQueryProvider : IMeshQueryProvider
{
    private readonly RoutingPersistenceServiceCore _router;
    private readonly AccessService? _accessService;
    private readonly ISecurityService? _securityService;
    private readonly IDataChangeNotifier? _changeNotifier;
    private readonly QueryParser _parser = new();
    private readonly MemoryCache _accessCache = new(new MemoryCacheOptions());
    private static readonly MemoryCacheEntryOptions CacheOptions = new() { SlidingExpiration = TimeSpan.FromMinutes(5) };

    public RoutingMeshQueryProvider(
        RoutingPersistenceServiceCore router,
        AccessService? accessService = null,
        ISecurityService? securityService = null,
        IDataChangeNotifier? changeNotifier = null)
    {
        _router = router;
        _accessService = accessService;
        _securityService = securityService;
        _changeNotifier = changeNotifier;

        // Evict access cache when partition access changes
        _changeNotifier?.Subscribe(notification =>
        {
            if (notification.Path.StartsWith("Admin/Partition", StringComparison.OrdinalIgnoreCase) ||
                notification.Path.Contains("_Access", StringComparison.OrdinalIgnoreCase))
            {
                _accessCache.Compact(1.0); // Clear all entries
            }
        });
    }

    /// <summary>
    /// Returns the set of partition keys the current user can access, or null if no filtering needed.
    /// </summary>
    private async Task<HashSet<string>?> GetAccessiblePartitionsAsync(CancellationToken ct)
    {
        var userId = _accessService?.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId) || _securityService == null)
            return null; // No user context or no security => no filtering

        var cacheKey = $"partition-access:{userId}";
        if (_accessCache.TryGetValue<HashSet<string>>(cacheKey, out var cached))
            return cached;

        var accessible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (partitionKey, _) in _router.QueryProviders)
        {
            // Check if user has read permission on the partition's namespace
            var ns = GetNamespaceForPartition(partitionKey);
            if (await _securityService.HasPermissionAsync(ns, userId, Permission.Read, ct))
                accessible.Add(partitionKey);
        }

        _accessCache.Set(cacheKey, accessible, CacheOptions);
        return accessible;
    }

    /// <summary>
    /// Gets the namespace for a partition. Uses Partition metadata if available,
    /// otherwise falls back to the partition key itself.
    /// </summary>
    private string GetNamespaceForPartition(string partitionKey)
    {
        // Check if any partition metadata maps to this store key
        foreach (var (_, ns) in _router.PartitionNamespaces)
        {
            if (_router.BasePathToPartition.TryGetValue(ns, out var storeKey) &&
                storeKey.Equals(partitionKey, StringComparison.OrdinalIgnoreCase))
            {
                return ns;
            }
        }

        // Default: the partition key itself is the namespace
        return partitionKey;
    }

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parsed = _parser.Parse(request.Query);
        var effectivePath = parsed.Path ?? request.DefaultPath;
        var segment = PathPartition.GetFirstSegment(effectivePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            // Route to specific partition
            await foreach (var item in provider.QueryAsync(request, options, ct))
                yield return item;
            yield break;
        }

        // When fanning out with default scope (Exact), use descendants to search
        // the full partition tree (not just direct children of each partition)
        var fanOutQuery = parsed.Scope == QueryScope.Exact
            ? (request.Query ?? "") + " scope:descendants"
            : request.Query;

        // Fan out: query accessible partitions in parallel, each scoped to its own namespace
        var accessiblePartitions = await GetAccessiblePartitionsAsync(ct);
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var channel = Channel.CreateUnbounded<object>();

        _ = ProduceAllResultsAsync();
        async Task ProduceAllResultsAsync()
        {
            try
            {
                var queryTasks = new ConcurrentBag<Task>();

                // Start querying already-known accessible partitions immediately
                foreach (var (key, p) in _router.QueryProviders)
                {
                    if (accessiblePartitions != null && !accessiblePartitions.Contains(key))
                        continue;
                    queryTasks.Add(QueryOneAsync(key, p));
                }

                // Discover new partitions and start querying each as it becomes available
                await foreach (var (key, p) in _router.DiscoverNewProvidersAsync(ct))
                    queryTasks.Add(QueryOneAsync(key, p));

                // All providers discovered — wait for ongoing queries to finish
                await Task.WhenAll(queryTasks);
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
                return;
            }
            channel.Writer.Complete();

            async Task QueryOneAsync(string partitionKey, IMeshQueryProvider p)
            {
                // Scope the request to the partition's namespace so each partition
                // only searches its own data (not all data via a shared adapter)
                var scopedRequest = string.IsNullOrEmpty(effectivePath)
                    ? request with { DefaultPath = partitionKey, Query = fanOutQuery }
                    : request;

                await foreach (var item in p.QueryAsync(scopedRequest, options, ct))
                {
                    if (item is MeshNode node && !seen.TryAdd(node.Path, 0))
                        continue;
                    await channel.Writer.WriteAsync(item, ct);
                }
            }
        }

        // Enforce global limit across all partitions
        var globalLimit = request.Limit ?? parsed.Limit;
        int count = 0;
        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
            if (globalLimit.HasValue && ++count >= globalLimit.Value)
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

        // Fan out: known partitions + newly discovered, all in parallel
        var all = new ConcurrentBag<QuerySuggestion>();
        var tasks = new ConcurrentBag<Task>();

        foreach (var (key, p) in _router.QueryProviders)
            tasks.Add(AutocompleteOneAsync(key, p));

        await foreach (var (key, p) in _router.DiscoverNewProvidersAsync(ct))
            tasks.Add(AutocompleteOneAsync(key, p));

        await Task.WhenAll(tasks);

        foreach (var s in all.OrderBy(s => s.Path.Length).ThenByDescending(s => s.Score).ThenBy(s => s.Name).Take(limit))
            yield return s;

        async Task AutocompleteOneAsync(string partitionKey, IMeshQueryProvider p)
        {
            var effectiveBasePath = string.IsNullOrEmpty(basePath) ? partitionKey : basePath;
            await foreach (var s in p.AutocompleteAsync(effectiveBasePath, prefix, options, limit, ct))
                all.Add(s);
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
        var segment = PathPartition.GetFirstSegment(basePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            await foreach (var s in provider.AutocompleteAsync(basePath, prefix, options, mode, limit, contextPath, context, ct))
                yield return s;
            yield break;
        }

        // Fan out: known partitions + newly discovered, all in parallel
        var all = new ConcurrentBag<QuerySuggestion>();
        var tasks = new ConcurrentBag<Task>();

        foreach (var (key, p) in _router.QueryProviders)
            tasks.Add(AutocompleteOneAsync(key, p));

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
            var effectiveBasePath = string.IsNullOrEmpty(basePath) ? partitionKey : basePath;
            await foreach (var s in p.AutocompleteAsync(effectiveBasePath, prefix, options, mode, limit, contextPath, context, ct))
                all.Add(s);
        }
    }

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        var parsed = _parser.Parse(request.Query);
        var effectivePath = parsed.Path ?? request.DefaultPath;
        var segment = PathPartition.GetFirstSegment(effectivePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            return provider.ObserveQuery<T>(request, options);
        }

        // When fanning out with default scope (Exact), use descendants for full partition search
        var fanOutQuery = parsed.Scope == QueryScope.Exact
            ? (request.Query ?? "") + " scope:descendants"
            : request.Query;

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
