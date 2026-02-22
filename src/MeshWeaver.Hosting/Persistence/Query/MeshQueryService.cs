using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Scoped wrapper around IMeshQueryCore that automatically injects
/// JsonSerializerOptions from the current IMessageHub.
/// Merges static nodes from IStaticNodeProvider instances into query results.
/// </summary>
public class MeshQueryService(
    IMeshQueryCore core,
    IMessageHub hub,
    IEnumerable<IStaticNodeProvider> staticNodeProviders) : IMeshQuery
{
    private JsonSerializerOptions Options => hub.JsonSerializerOptions;

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var seen = new HashSet<string>();

        await foreach (var item in core.QueryAsync(request, Options, ct))
        {
            if (item is MeshNode node)
                seen.Add(node.Path);
            yield return item;
        }

        // Merge matching static nodes only when query has meaningful filters
        var parser = new QueryParser();
        var parsed = parser.Parse(request.Query);

        if (!HasNonTypeFilter(parsed))
            yield break;

        var evaluator = new QueryEvaluator();

        foreach (var provider in staticNodeProviders)
        {
            foreach (var staticNode in provider.GetStaticNodes())
            {
                if (seen.Contains(staticNode.Path))
                    continue;

                if (evaluator.Matches(staticNode, parsed))
                {
                    seen.Add(staticNode.Path);
                    yield return staticNode;
                }
            }
        }
    }

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        int limit = 10,
        CancellationToken ct = default)
        => core.AutocompleteAsync(basePath, prefix, Options, limit, ct);

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        CancellationToken ct = default)
        => core.AutocompleteAsync(basePath, prefix, Options, mode, limit, contextPath, ct);

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request)
    {
        var inner = core.ObserveQuery<T>(request, Options);

        return inner.Select(change =>
        {
            if (change.ChangeType is not (QueryChangeType.Initial or QueryChangeType.Reset))
                return change;

            var parser = new QueryParser();
            var parsed = parser.Parse(request.Query);

            if (!HasNonTypeFilter(parsed))
                return change;

            var evaluator = new QueryEvaluator();

            var existing = new HashSet<string>(
                change.Items.OfType<MeshNode>().Select(n => n.Path));

            var extras = new List<T>();
            foreach (var provider in staticNodeProviders)
            {
                foreach (var staticNode in provider.GetStaticNodes())
                {
                    if (existing.Contains(staticNode.Path))
                        continue;

                    if (evaluator.Matches(staticNode, parsed) && staticNode is T typed)
                    {
                        existing.Add(staticNode.Path);
                        extras.Add(typed);
                    }
                }
            }

            if (extras.Count == 0)
                return change;

            return change with { Items = [.. change.Items, .. extras] };
        });
    }

    public Task<T?> SelectAsync<T>(string path, string property, CancellationToken ct = default)
        => core.SelectAsync<T>(path, property, Options, ct);

    /// <summary>
    /// Returns true when the query has field-level filters beyond just $type,
    /// or has a text search term. Static nodes are only merged when meaningful
    /// filters exist — not for generic children/descendants queries.
    /// </summary>
    private static bool HasNonTypeFilter(ParsedQuery parsed)
    {
        if (!string.IsNullOrEmpty(parsed.TextSearch))
            return true;

        if (parsed.Filter == null)
            return false;

        return HasNonTypeCondition(parsed.Filter);
    }

    private static bool HasNonTypeCondition(QueryNode node)
    {
        return node switch
        {
            QueryComparison c => !c.Condition.Selector.Equals("$type", StringComparison.OrdinalIgnoreCase),
            QueryAnd a => a.Children.Any(HasNonTypeCondition),
            QueryOr o => o.Children.Any(HasNonTypeCondition),
            _ => false
        };
    }
}
