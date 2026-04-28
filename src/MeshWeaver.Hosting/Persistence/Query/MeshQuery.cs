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
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
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
                    await foreach (var item in provider.QueryAsync(request, Options, ct))
                    {
                        if (item is MeshNode node && !seen.TryAdd(node.Path, 0))
                            continue;

                        var result = parsedQuery.Select != null && item is MeshNode
                            ? ParsedQuery.ProjectToSelect(item, parsedQuery.Select)
                            : item;
                        await channel.Writer.WriteAsync(result, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    logger?.LogTrace("MeshQuery: {Provider} cancelled for '{Query}'",
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
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MeshQuery>();

        await Task.WhenAll(providers.Select(async provider =>
        {
            try
            {
                await foreach (var suggestion in provider.AutocompleteAsync(basePath, prefix, Options, limit, ct))
                {
                    // Skip satellite nodes — they have /_Prefix/ segments in their path
                    if (IsSatellitePath(suggestion.Path))
                        continue;
                    if (seen.TryAdd(suggestion.Path, 0))
                        all.Add(suggestion);
                }
            }
            catch (OperationCanceledException) { /* expected on cancel */ }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "{Provider} autocomplete failed", provider.GetType().Name);
            }
        }));

        foreach (var suggestion in all
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Path.Length)
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
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger<MeshQuery>();

        await Task.WhenAll(providers.Select(async provider =>
        {
            try
            {
                await foreach (var suggestion in provider.AutocompleteAsync(basePath, prefix, Options, mode, limit, contextPath, context, ct))
                {
                    // Skip satellite nodes — they have /_Prefix/ segments in their path
                    if (IsSatellitePath(suggestion.Path))
                        continue;
                    if (seen.TryAdd(suggestion.Path, 0))
                    {
                        // Apply proximity boost based on contextPath
                        var boosted = ApplyProximityBoost(suggestion, contextPath, prefix);
                        all.Add(boosted);
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on cancel */ }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "{Provider} autocomplete failed", provider.GetType().Name);
            }
        }));

        IEnumerable<QuerySuggestion> ordered = mode switch
        {
            AutocompleteMode.RelevanceFirst => all
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Path.Length)
                .ThenBy(s => s.Name),
            _ => all
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Path.Length)
                .ThenBy(s => s.Name)
        };

        foreach (var suggestion in ordered.Take(limit))
        {
            yield return suggestion;
        }
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
