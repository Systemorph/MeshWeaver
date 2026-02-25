using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Scoped wrapper that automatically injects JsonSerializerOptions from the current IMessageHub
/// and aggregates results from all registered IMeshQueryProvider instances.
/// When source:activity is specified, results from the user's activity partition are returned first,
/// then completed with normal mesh query results.
/// </summary>
public class MeshQuery(
    IEnumerable<IMeshQueryProvider> providers,
    IMessageHub hub) : IMeshQuery
{
    private JsonSerializerOptions Options => hub.JsonSerializerOptions;
    private readonly QueryParser _parser = new();

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parsed = _parser.Parse(request.Query);

        if (parsed.Source == QuerySource.Activity)
        {
            await foreach (var item in QueryWithActivityFirstAsync(request, parsed, ct))
                yield return item;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            await foreach (var item in provider.QueryAsync(request, Options, ct))
            {
                if (item is MeshNode node)
                {
                    if (!seen.Add(node.Path))
                        continue; // deduplicate by path, first provider wins
                }
                yield return item;
            }
        }
    }

    /// <summary>
    /// Activity-first query: loads user's activity records, filters by query criteria,
    /// resolves to MeshNodes, then completes with normal mesh query results for remaining hits.
    /// </summary>
    private async IAsyncEnumerable<object> QueryWithActivityFirstAsync(
        MeshQueryRequest request,
        ParsedQuery parsed,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var limit = request.Limit ?? parsed.Limit ?? 50;

        // Get user's activity records
        var activityStore = hub.ServiceProvider.GetService(typeof(IActivityStore)) as IActivityStore;
        var accessService = hub.ServiceProvider.GetService(typeof(AccessService)) as AccessService;
        var persistence = hub.ServiceProvider.GetService(typeof(IPersistenceService)) as IPersistenceService;

        var userId = request.UserId ?? accessService?.Context?.ObjectId;

        if (activityStore != null && !string.IsNullOrEmpty(userId) && persistence != null)
        {
            var activities = await activityStore.GetActivitiesAsync(userId, ct);

            // Extract nodeType filter from parsed query
            var nodeTypeFilter = GetNodeTypeFilterValue(parsed.Filter);
            var namespaceFilter = parsed.Path; // namespace: maps to Path in ParsedQuery

            // Filter and sort activities
            var matchingActivities = activities
                .Where(a => nodeTypeFilter == null ||
                            string.Equals(a.NodeType, nodeTypeFilter, StringComparison.OrdinalIgnoreCase))
                .Where(a => string.IsNullOrEmpty(namespaceFilter) ||
                            string.Equals(a.Namespace, namespaceFilter, StringComparison.OrdinalIgnoreCase) ||
                            (a.NodePath?.StartsWith(namespaceFilter + "/", StringComparison.OrdinalIgnoreCase) == true))
                .OrderByDescending(a => a.LastAccessedAt)
                .Take(limit);

            // Resolve each activity to its MeshNode
            foreach (var activity in matchingActivities)
            {
                if (ct.IsCancellationRequested) yield break;

                var node = await persistence.GetNodeAsync(activity.NodePath);
                if (node != null && seen.Add(node.Path))
                {
                    yield return node;
                    if (seen.Count >= limit) yield break;
                }
            }
        }

        // Complete with normal mesh query for remaining hits (strip source:activity)
        var normalQuery = StripSourceFromQuery(request.Query);
        if (!string.IsNullOrWhiteSpace(normalQuery))
        {
            var normalRequest = request with { Query = normalQuery };
            foreach (var provider in providers)
            {
                await foreach (var item in provider.QueryAsync(normalRequest, Options, ct))
                {
                    if (item is MeshNode node)
                    {
                        if (!seen.Add(node.Path))
                            continue;
                    }
                    yield return item;
                    if (seen.Count >= limit) yield break;
                }
            }
        }
    }

    /// <summary>
    /// Removes the source:activity qualifier from a query string so it can be
    /// re-executed against normal providers.
    /// </summary>
    private static string StripSourceFromQuery(string query)
    {
        // Simple removal of source:activity token
        return System.Text.RegularExpressions.Regex.Replace(
            query, @"\bsource:activity\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
    }

    /// <summary>
    /// Extracts the nodeType filter value from the query AST, if present.
    /// </summary>
    private static string? GetNodeTypeFilterValue(QueryNode? node)
    {
        return node switch
        {
            QueryComparison c when c.Condition.Selector.Equals("nodeType", StringComparison.OrdinalIgnoreCase)
                => c.Condition.Values.FirstOrDefault(),
            QueryAnd a => a.Children.Select(GetNodeTypeFilterValue).FirstOrDefault(v => v != null),
            QueryOr o => o.Children.Select(GetNodeTypeFilterValue).FirstOrDefault(v => v != null),
            _ => null
        };
    }

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var all = new List<QuerySuggestion>();

        foreach (var provider in providers)
        {
            await foreach (var suggestion in provider.AutocompleteAsync(basePath, prefix, Options, limit, ct))
                all.Add(suggestion);
        }

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
        var all = new List<QuerySuggestion>();

        foreach (var provider in providers)
        {
            await foreach (var suggestion in provider.AutocompleteAsync(basePath, prefix, Options, mode, limit, contextPath, context, ct))
                all.Add(suggestion);
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
        foreach (var provider in providers)
        {
            var result = await provider.SelectAsync<T>(path, property, Options, ct);
            if (result != null)
                return result;
        }
        return default;
    }
}
