using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Query provider that routes queries to the appropriate partition based on parsed path/namespace.
/// When no path is specified, fans out to all partitions and merges results.
/// </summary>
public class RoutingMeshQueryProvider : IMeshQueryProvider
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

        // Fan out to all partitions, deduplicate by path
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _router.QueryProviders.Values)
        {
            await foreach (var item in p.QueryAsync(request, options, ct))
            {
                if (item is MeshNode node)
                {
                    if (!seen.Add(node.Path))
                        continue;
                }
                yield return item;
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
        var segment = PathPartition.GetFirstSegment(basePath);

        if (segment != null && _router.QueryProviders.TryGetValue(segment, out var provider))
        {
            await foreach (var s in provider.AutocompleteAsync(basePath, prefix, options, limit, ct))
                yield return s;
            yield break;
        }

        // Fan out to all partitions
        var all = new List<QuerySuggestion>();
        foreach (var p in _router.QueryProviders.Values)
        {
            await foreach (var s in p.AutocompleteAsync(basePath, prefix, options, limit, ct))
                all.Add(s);
        }

        foreach (var s in all.OrderBy(s => s.Path.Length).ThenByDescending(s => s.Score).ThenBy(s => s.Name).Take(limit))
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

        // Fan out to all partitions
        var all = new List<QuerySuggestion>();
        foreach (var p in _router.QueryProviders.Values)
        {
            await foreach (var s in p.AutocompleteAsync(basePath, prefix, options, mode, limit, contextPath, context, ct))
                all.Add(s);
        }

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

        // Fan out to all partitions, merge observables
        var observables = _router.QueryProviders.Values
            .Select(p => p.ObserveQuery<T>(request, options))
            .ToList();

        if (observables.Count == 0)
            return Observable.Empty<QueryResultChange<T>>();

        if (observables.Count == 1)
            return observables[0];

        return Observable.Create<QueryResultChange<T>>(observer =>
        {
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

        // Fan out
        foreach (var p in _router.QueryProviders.Values)
        {
            var result = await p.SelectAsync<T>(path, property, options, ct);
            if (result != null)
                return result;
        }
        return default;
    }
}
