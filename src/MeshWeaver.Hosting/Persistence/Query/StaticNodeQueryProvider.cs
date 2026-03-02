using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// An IMeshQueryProvider that serves static nodes (e.g., built-in roles, type definitions)
/// from IStaticNodeProvider instances and MeshConfiguration.Nodes.
/// Provider nodes (roles) bypass path/scope checks (they are global).
/// Configuration nodes (type definitions) respect path/scope and context filtering.
/// </summary>
public class StaticNodeQueryProvider : IMeshQueryProvider
{
    // Provider nodes (from IStaticNodeProvider) — global, no path/scope check
    private readonly MeshNode[] _providerNodes;
    // Config nodes (from MeshConfiguration.Nodes) — respect path/scope/context
    private readonly MeshNode[] _configNodes;
    // All nodes combined for SelectAsync/nodeType index
    private readonly MeshNode[] _allNodes;
    private readonly HashSet<string> _nodeTypes;
    private readonly MeshConfiguration? _meshConfiguration;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();

    public StaticNodeQueryProvider(
        IEnumerable<IStaticNodeProvider> providers,
        MeshConfiguration? meshConfiguration = null)
    {
        _meshConfiguration = meshConfiguration;

        _providerNodes = providers.SelectMany(p => p.GetStaticNodes()).ToArray();

        var providerPaths = new HashSet<string>(
            _providerNodes.Select(n => n.Path),
            StringComparer.OrdinalIgnoreCase);

        // Config nodes that aren't already in provider nodes (deduplicate)
        _configNodes = (meshConfiguration?.Nodes.Values ?? Enumerable.Empty<MeshNode>())
            .Where(n => !providerPaths.Contains(n.Path))
            .ToArray();

        _allNodes = _providerNodes.Concat(_configNodes).ToArray();

        _nodeTypes = new HashSet<string>(
            _allNodes
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

        // Short-circuit: if query has a nodeType filter that doesn't match any static node type
        var nodeTypeFilter = GetNodeTypeFilterValue(parsed.Filter);
        if (nodeTypeFilter != null && !_nodeTypes.Contains(nodeTypeFilter))
            yield break;

        var context = request.Context ?? parsed.Context;

        // Provider nodes (roles, agents, etc.) — global, bypass path/scope checks.
        // Only included when there's an explicit field filter (e.g., nodeType:Role).
        // Without a field filter, path-only queries (like scope:children) won't leak provider nodes.
        if (HasFieldFilter(parsed))
        {
            foreach (var node in _providerNodes)
            {
                if (!_evaluator.Matches(node, parsed))
                    continue;
                if (IsExcludedByContext(node, context))
                    continue;
                yield return node;
            }
        }

        // Config nodes (type definitions) — require field filter or path, apply path/scope/context
        if (HasFieldFilter(parsed) || !string.IsNullOrEmpty(parsed.Path))
        {
            foreach (var node in _configNodes)
            {
                if (!MatchesPath(node, parsed))
                    continue;
                if (!_evaluator.Matches(node, parsed))
                    continue;
                if (IsExcludedByContext(node, context))
                    continue;
                yield return node;
            }
        }
    }

    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath, string prefix, JsonSerializerOptions options,
        int limit = 10, CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, AutocompleteMode.PathFirst, limit, null, null, ct);

    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode, int limit = 10, string? contextPath = null,
        string? context = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedPrefix = (prefix ?? "").ToLowerInvariant();
        var suggestions = new List<QuerySuggestion>();

        foreach (var node in _allNodes)
        {
            if (_meshConfiguration?.AutocompleteExcludedNodeTypes.Contains(node.NodeType ?? "") == true)
                continue;
            if (IsExcludedByContext(node, context))
                continue;

            // Check path scope: node must be a descendant of basePath (or basePath is empty)
            if (!string.IsNullOrEmpty(basePath))
            {
                var nodePath = node.Path ?? "";
                if (!nodePath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(nodePath, basePath, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var name = node.Name ?? node.Id ?? node.Path ?? "";
            double score = 0;

            if (string.IsNullOrEmpty(normalizedPrefix))
            {
                score = 1;
            }
            else if (name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 100 - (name.Length - normalizedPrefix.Length);
            }
            else if (name.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 50;
            }
            else if ((node.Path ?? "").Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 30;
            }
            else
            {
                continue;
            }

            score += PathProximity.ComputeBoost(contextPath, node.Path);
            suggestions.Add(new QuerySuggestion(node.Path ?? "", name, node.NodeType, score, node.Icon));
        }

        IEnumerable<QuerySuggestion> ordered = mode switch
        {
            AutocompleteMode.RelevanceFirst => suggestions
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Path.Length)
                .ThenBy(s => s.Name),
            _ => suggestions
                .OrderBy(s => s.Path.Length)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name)
        };

        foreach (var suggestion in ordered.Take(limit))
            yield return suggestion;

        await Task.CompletedTask; // Satisfy async requirement
    }

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
        var node = _allNodes.FirstOrDefault(n =>
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
    /// Checks whether a node matches the query's path and scope constraints.
    /// Only applied to configuration nodes, not provider nodes.
    /// </summary>
    private static bool MatchesPath(MeshNode node, ParsedQuery parsed)
    {
        if (string.IsNullOrEmpty(parsed.Path))
            return true;

        var path = parsed.Path;
        var nodePath = node.Path;
        var nodeNamespace = node.Namespace ?? "";

        return parsed.Scope switch
        {
            QueryScope.Exact =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),

            QueryScope.Children =>
                string.Equals(nodeNamespace, path, StringComparison.OrdinalIgnoreCase),

            QueryScope.Descendants =>
                nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),

            QueryScope.Subtree =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),

            QueryScope.Ancestors =>
                path.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase),

            QueryScope.AncestorsAndSelf =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase),

            QueryScope.Hierarchy =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase),

            _ => true
        };
    }

    /// <summary>
    /// Checks if a node is excluded from the specified context.
    /// </summary>
    private bool IsExcludedByContext(MeshNode node, string? context)
    {
        if (context == null) return false;
        if (_meshConfiguration?.IsExcludedFromContext(node.NodeType, context) == true)
            return true;
        if (node.ExcludeFromContext?.Contains(context) == true)
            return true;
        return false;
    }

    /// <summary>
    /// Returns true when the query has field-level filters beyond just $type,
    /// or has a text search term.
    /// </summary>
    private static bool HasFieldFilter(ParsedQuery parsed)
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
