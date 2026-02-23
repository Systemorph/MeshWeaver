using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// An IMeshQueryProvider that serves static nodes (e.g., built-in roles)
/// from IStaticNodeProvider instances. Short-circuits when the query
/// cannot possibly match any static node.
/// </summary>
public class StaticNodeQueryProvider : IMeshQueryProvider
{
    private readonly MeshNode[] _staticNodes;
    private readonly HashSet<string> _nodeTypes;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();

    public StaticNodeQueryProvider(IEnumerable<IStaticNodeProvider> providers)
    {
        _staticNodes = providers.SelectMany(p => p.GetStaticNodes()).ToArray();
        _nodeTypes = new HashSet<string>(
            _staticNodes
                .Where(n => !string.IsNullOrEmpty(n.NodeType))
                .Select(n => n.NodeType!),
            StringComparer.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var parsed = _parser.Parse(request.Query);

        // Only merge static nodes when the query has meaningful filters
        if (!HasNonTypeFilter(parsed))
            yield break;

        // Short-circuit: if query has a nodeType filter that doesn't match any static node type
        var nodeTypeFilter = GetNodeTypeFilterValue(parsed.Filter);
        if (nodeTypeFilter != null && !_nodeTypes.Contains(nodeTypeFilter))
            yield break;

        foreach (var node in _staticNodes)
        {
            if (_evaluator.Matches(node, parsed))
                yield return node;
        }
    }

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath, string prefix, JsonSerializerOptions options,
        int limit = 10, CancellationToken ct = default)
        => AsyncEnumerable.Empty<QuerySuggestion>();

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode, int limit = 10, string? contextPath = null,
        CancellationToken ct = default)
        => AsyncEnumerable.Empty<QuerySuggestion>();

    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        return Observable.Create<QueryResultChange<T>>(async (observer, ct) =>
        {
            var items = new List<T>();
            await foreach (var item in QueryAsync(request, options, ct))
            {
                if (item is T typed)
                    items.Add(typed);
            }

            observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Items = items,
                Query = _parser.Parse(request.Query),
                Version = 0,
                Timestamp = DateTimeOffset.UtcNow
            });
            observer.OnCompleted();
            return System.Reactive.Disposables.Disposable.Empty;
        });
    }

    public Task<T?> SelectAsync<T>(string path, string property, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var node = _staticNodes.FirstOrDefault(n =>
            string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

        if (node == null)
            return Task.FromResult<T?>(default);

        var prop = typeof(MeshNode).GetProperty(property);
        if (prop == null)
            return Task.FromResult<T?>(default);

        var value = prop.GetValue(node);
        if (value is T typedValue)
            return Task.FromResult<T?>(typedValue);

        return Task.FromResult<T?>(default);
    }

    /// <summary>
    /// Returns true when the query has field-level filters beyond just $type,
    /// or has a text search term.
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
}
