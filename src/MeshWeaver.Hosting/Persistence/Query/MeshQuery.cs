using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Scoped wrapper that automatically injects JsonSerializerOptions from the current IMessageHub
/// and aggregates results from all registered IMeshQueryProvider instances.
/// source:activity implies nodeType:Activity filter; source:accessed JOINs with UserActivity
/// nodes to order by last-access time. Providers that don't support these sources return normal results.
/// Identity is resolved from AccessService.Context. Use accessService.ImpersonateAsHub(hub)
/// to temporarily switch identity for hub-level operations.
/// </summary>
public class MeshQuery(
    IEnumerable<IMeshQueryProvider> providers,
    IMessageHub hub)
{
    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {

        // Always delegate to providers. source:activity implies nodeType:Activity;
        // source:accessed JOINs with UserActivity MeshNodes for last-access ordering.
        // Providers that don't support these sources return normal results.
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Parse query once to extract select: projection (applied uniformly after dedup)
        var parsedQuery = new QueryParser().Parse(request.Query);

        var channel = Channel.CreateUnbounded<object>();

        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MeshQuery>();

        _ = FanOutProvidersAsync();
        async Task FanOutProvidersAsync()
        {
            await Task.WhenAll(providers.Select(async provider =>
            {
                try
                {
                    var count = 0;
                    await foreach (var item in provider.QueryAsync(request, Options, ct))
                    {
                        if (item is MeshNode node && !seen.TryAdd(node.Path, 0))
                            continue; // deduplicate by path

                        count++;
                        var result = parsedQuery.Select != null && item is MeshNode
                            ? ParsedQuery.ProjectToSelect(item, parsedQuery.Select)
                            : item;
                        await channel.Writer.WriteAsync(result, ct);
                    }
                    if (count > 0 || logger?.IsEnabled(LogLevel.Debug) == true)
                        logger?.LogDebug("MeshQuery: {Provider} returned {Count} items for '{Query}'",
                            provider.GetType().Name, count, request.Query);
                }
                catch (OperationCanceledException)
                {
                    logger?.LogDebug("MeshQuery: {Provider} cancelled for '{Query}'",
                        provider.GetType().Name, request.Query);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "MeshQuery: {Provider} failed for '{Query}'",
                        provider.GetType().Name, request.Query);
                }
            }));
            channel.Writer.Complete();
        }

        // Use CancellationToken.None — the channel completes when all producers finish.
        // Passing `ct` here would throw OperationCanceledException immediately if the
        // request was cancelled (e.g., user typing fast in search bar), even though
        // partial results may already be in the channel.
        await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return item;
    }

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var all = new ConcurrentBag<QuerySuggestion>();
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(providers.Select(async provider =>
        {
            await foreach (var suggestion in provider.AutocompleteAsync(basePath, prefix, Options, limit, ct))
            {
                if (seen.TryAdd(suggestion.Path, 0))
                    all.Add(suggestion);
            }
        }));

        foreach (var suggestion in all
            .OrderBy(s => s.Path.Length)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Name)
            .Take(limit))
        {
            yield return suggestion;
        }
    }

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var all = new ConcurrentBag<QuerySuggestion>();
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(providers.Select(async provider =>
        {
            await foreach (var suggestion in provider.AutocompleteAsync(basePath, prefix, Options, mode, limit, contextPath, context, ct))
            {
                if (seen.TryAdd(suggestion.Path, 0))
                    all.Add(suggestion);
            }
        }));

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

        foreach (var suggestion in ordered.Take(limit))
        {
            yield return suggestion;
        }
    }

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request)
    {
        // Collect Initial from all providers, merge into a single Initial emission,
        // then forward subsequent (non-Initial) changes from ongoing providers.
        var observables = providers
            .Select(p => p.ObserveQuery<T>(request, Options))
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
            ParsedQuery? lastQuery = null;
            var gate = new object();

            var subscriptions = new List<IDisposable>();

            for (var i = 0; i < observables.Count; i++)
            {
                var obs = observables[i];
                var sub = obs.Subscribe(
                    change =>
                    {
                        if (change.ChangeType == QueryChangeType.Initial)
                        {
                            lock (gate)
                            {
                                initialItems.AddRange(change.Items);
                                lastQuery ??= change.Query;
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

            return new System.Reactive.Disposables.CompositeDisposable(subscriptions);
        });
    }

    public async Task<T?> SelectAsync<T>(string path, string property, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(
            providers.Select(p => p.SelectAsync<T>(path, property, Options, ct)));
        return results.FirstOrDefault(r => r != null);
    }

}
