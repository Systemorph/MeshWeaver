using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Query provider that routes queries to the appropriate partition based on parsed path/namespace.
/// When no path is specified, fans out to all partitions and merges results.
/// Queries already-known partitions immediately while discovering new ones in parallel.
/// </summary>
internal class RoutingMeshQueryProvider : IMeshQueryProvider
{
    private readonly RoutingPersistenceServiceCore _router;
    private readonly QueryParser _parser = new();

    public RoutingMeshQueryProvider(RoutingPersistenceServiceCore router)
    {
        _router = router;
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

        // Fan out: query known partitions immediately, discover+query new ones in parallel
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var channel = Channel.CreateUnbounded<object>();

        _ = ProduceAllResultsAsync();
        async Task ProduceAllResultsAsync()
        {
            try
            {
                var queryTasks = new ConcurrentBag<Task>();

                // Start querying already-known partitions immediately
                foreach (var p in _router.QueryProviders.Values)
                    queryTasks.Add(QueryOneAsync(p));

                // Discover new partitions and start querying each as it becomes available
                await foreach (var p in _router.DiscoverNewProvidersAsync(ct))
                    queryTasks.Add(QueryOneAsync(p));

                // All providers discovered — wait for ongoing queries to finish
                await Task.WhenAll(queryTasks);
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
                return;
            }
            channel.Writer.Complete();

            async Task QueryOneAsync(IMeshQueryProvider p)
            {
                await foreach (var item in p.QueryAsync(request, options, ct))
                {
                    if (item is MeshNode node && !seen.TryAdd(node.Path, 0))
                        continue;
                    await channel.Writer.WriteAsync(item, ct);
                }
            }
        }

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
            yield return item;
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

        foreach (var p in _router.QueryProviders.Values)
            tasks.Add(AutocompleteOneAsync(p));

        await foreach (var p in _router.DiscoverNewProvidersAsync(ct))
            tasks.Add(AutocompleteOneAsync(p));

        await Task.WhenAll(tasks);

        foreach (var s in all.OrderBy(s => s.Path.Length).ThenByDescending(s => s.Score).ThenBy(s => s.Name).Take(limit))
            yield return s;

        async Task AutocompleteOneAsync(IMeshQueryProvider p)
        {
            await foreach (var s in p.AutocompleteAsync(basePath, prefix, options, limit, ct))
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

        foreach (var p in _router.QueryProviders.Values)
            tasks.Add(AutocompleteOneAsync(p));

        await foreach (var p in _router.DiscoverNewProvidersAsync(ct))
            tasks.Add(AutocompleteOneAsync(p));

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

        async Task AutocompleteOneAsync(IMeshQueryProvider p)
        {
            await foreach (var s in p.AutocompleteAsync(basePath, prefix, options, mode, limit, contextPath, context, ct))
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

        // Fan out to all partitions (known + newly discovered), merge observables
        return Observable.Create<QueryResultChange<T>>(async (observer, ct) =>
        {
            // Collect all providers: already-known + newly discovered
            var allProviders = new List<IMeshQueryProvider>(_router.QueryProviders.Values);
            await foreach (var p in _router.DiscoverNewProvidersAsync(ct))
                allProviders.Add(p);

            if (allProviders.Count == 0)
            {
                observer.OnCompleted();
                return Disposable.Empty;
            }

            if (allProviders.Count == 1)
            {
                return allProviders[0].ObserveQuery<T>(request, options).Subscribe(observer);
            }

            var observables = allProviders
                .Select(p => p.ObserveQuery<T>(request, options))
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

        foreach (var p in _router.QueryProviders.Values)
            tasks.Add(p.SelectAsync<T>(path, property, options, ct));

        await foreach (var p in _router.DiscoverNewProvidersAsync(ct))
            tasks.Add(p.SelectAsync<T>(path, property, options, ct));

        var results = await Task.WhenAll(tasks);
        return results.FirstOrDefault(r => r != null);
    }
}
