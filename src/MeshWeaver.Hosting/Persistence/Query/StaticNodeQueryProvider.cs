using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// An IMeshQueryProvider that serves static nodes (e.g., built-in roles, type definitions)
/// from IStaticNodeProvider instances and MeshConfiguration.Nodes.
/// Provider nodes (roles) bypass path/scope checks (they are global).
/// Configuration nodes (type definitions) respect path/scope and context filtering.
/// </summary>
public class StaticNodeQueryProvider : IMeshQueryProvider
{
    /// <summary>
    /// Default <see cref="Matches"/> predicate: union of the providers' static
    /// node first-segments + the seed nodes' first-segments. Computed once at
    /// registration time so the resulting lambda is closed over the
    /// partition set (data-driven, no hard-coded namespace list).
    ///
    /// <para>Accepts the query when any of the pre-extracted
    /// <paramref name="queryNamespaces"/> is in our segment set. Unscoped
    /// queries are pre-filtered by the aggregator (every provider
    /// participates) so this predicate is only consulted for scoped ones.</para>
    /// </summary>
    public static Func<IReadOnlyList<string>, bool> BuildDefaultMatches(
        IEnumerable<IStaticNodeProvider> providers,
        MeshConfiguration? meshConfiguration)
    {
        var firstSegments = providers
            .SelectMany(p => p.GetStaticNodes())
            .Concat(meshConfiguration?.Nodes.Values ?? Enumerable.Empty<MeshNode>())
            .Select(n => n.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!.Split('/', 2)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return queryNamespaces =>
        {
            for (var i = 0; i < queryNamespaces.Count; i++)
                if (firstSegments.Contains(queryNamespaces[i]))
                    return true;
            return false;
        };
    }

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
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    // Caller-supplied predicate — keeps partition routing data-driven (the
    // registration site declares which namespaces this provider owns, via a
    // lambda or the partition registry) rather than hard-coded here.
    private readonly Func<IReadOnlyList<string>, bool> _matches;

    /// <inheritdoc/>
    public bool Matches(IReadOnlyList<string> queryNamespaces) => _matches(queryNamespaces);

    public StaticNodeQueryProvider(
        IEnumerable<IStaticNodeProvider> providers,
        Func<IReadOnlyList<string>, bool> matches,
        MeshConfiguration? meshConfiguration = null,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
    {
        _meshConfiguration = meshConfiguration;
        _logger = loggerFactory?.CreateLogger<StaticNodeQueryProvider>();
        _matches = matches;

        var providerList = providers as IList<IStaticNodeProvider> ?? providers.ToList();

        // MeshConfiguration.Nodes (AddMeshNodes seed) wins the "config" bucket —
        // bridge providers like MeshConfigurationStaticNodeProvider re-emit those
        // same nodes via IStaticNodeProvider, but they retain config-node
        // semantics (search-context excluded). Without this priority, the
        // bridged config nodes leak through QueryAsync's _providerNodes path
        // which has no isSearch guard (caught by
        // StaticNodeQueryContextTests.SearchContext_ExcludesStaticNodes).
        var configPaths = new HashSet<string>(
            (meshConfiguration?.Nodes.Values ?? Enumerable.Empty<MeshNode>())
                .Select(n => n.Path)
                .Where(p => !string.IsNullOrEmpty(p)),
            StringComparer.OrdinalIgnoreCase);

        _providerNodes = providerList
            .SelectMany(p => p.GetStaticNodes())
            .Where(n => !configPaths.Contains(n.Path))
            .ToArray();
        _logger?.LogInformation(
            "[StaticNodeQueryProvider] ctor: {Providers} provider(s) -> {Count} nodes; byType=[{ByType}]; byNamespace(top)=[{ByNs}]",
            providerList.Count,
            _providerNodes.Length,
            string.Join(", ", _providerNodes.GroupBy(n => n.NodeType ?? "(null)").Select(g => $"{g.Key}={g.Count()}")),
            string.Join(", ", _providerNodes.GroupBy(n => n.Namespace ?? "(null)").OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}={g.Count()}")));

        _configNodes = (meshConfiguration?.Nodes.Values ?? Enumerable.Empty<MeshNode>()).ToArray();

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
        var context = request.Context ?? parsed.Context;

        // source:activity / source:accessed never match static catalog entries —
        // they have no activity satellites and no UserActivity rows.
        if (parsed.Source is QuerySource.Activity or QuerySource.Accessed)
            yield break;

        // Short-circuit: if query has a nodeType filter that doesn't match any static node type
        var nodeTypeFilter = GetNodeTypeFilterValue(parsed.Filter);
        if (nodeTypeFilter != null && !_nodeTypes.Contains(nodeTypeFilter))
            yield break;

        // Provider nodes (roles, agents, etc.) — included when:
        // 1. There's an explicit field filter (e.g., nodeType:Role) — global match, no path check
        // 2. There's a path constraint (e.g., namespace:Agent) — path-scoped match
        if (HasFieldFilter(parsed) || !string.IsNullOrEmpty(parsed.Path))
        {
            foreach (var node in _providerNodes)
            {
                if (!string.IsNullOrEmpty(parsed.Path) && !MatchesPath(node, parsed))
                    continue;
                if (!_evaluator.Matches(node, parsed))
                    continue;
                if (IsExcludedByContext(node, context))
                    continue;
                if (parsed.IsMain == true && node.MainNode != node.Path)
                    continue;
                yield return node;
            }
        }

        // Config nodes are type definitions (meta-infrastructure), not user content.
        // They are excluded from search context — user content comes from persistence providers.
        var isSearch = string.Equals(context, "search", StringComparison.OrdinalIgnoreCase);
        if (!isSearch && (HasFieldFilter(parsed) || !string.IsNullOrEmpty(parsed.Path)))
        {
            foreach (var node in _configNodes)
            {
                if (!MatchesPath(node, parsed))
                    continue;
                if (!_evaluator.Matches(node, parsed))
                    continue;
                if (IsExcludedByContext(node, context))
                    continue;
                if (parsed.IsMain == true && node.MainNode != node.Path)
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
        // Static nodes are purely in-memory (no I/O) and never change.
        // Collect synchronously and return a completed Observable.Return — no async, no scheduler.
        var parsedQuery = _parser.Parse(request.Query);
        var items = CollectStaticResults<T>(parsedQuery, request.Context);
        _logger?.LogInformation(
            "[StaticNodeQueryProvider] ObserveQuery query='{Query}' parsedPath='{Path}' parsedNodeType='{NodeType}' -> {Count} item(s)",
            request.Query, parsedQuery.Path ?? "(null)",
            (parsedQuery.Filter as MeshWeaver.Mesh.QueryComparison)?.Condition.Values is { } vs
                ? string.Join("|", vs) : "(complex)",
            items.Count);
        return Observable.Return(new QueryResultChange<T>
        {
            ChangeType = QueryChangeType.Initial,
            Items = items,
            Query = parsedQuery,
            Version = 0,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    private List<T> CollectStaticResults<T>(ParsedQuery parsed, string? requestContext)
    {
        var context = requestContext ?? parsed.Context;
        var items = new List<T>();

        // source:activity / source:accessed are activity-satellite / UserActivity
        // joins — static catalog entries have neither, so always empty here.
        if (parsed.Source is QuerySource.Activity or QuerySource.Accessed)
            return items;

        var nodeTypeFilter = GetNodeTypeFilterValue(parsed.Filter);
        if (nodeTypeFilter != null && !_nodeTypes.Contains(nodeTypeFilter))
            return items;

        // Emit static nodes when:
        //  - a field filter is set (nodeType:, name:, etc.),
        //  - a path is set,
        //  - or an explicit scope walks the static set (Children/Descendants/
        //    Subtree/Hierarchy/AncestorsAndSelf). The last branch is what makes
        //    a bare "scope:descendants" against an empty namespace return the
        //    full static catalog instead of nothing.
        var scopeWalks = parsed.Scope is QueryScope.Children
            or QueryScope.Descendants
            or QueryScope.Subtree
            or QueryScope.Hierarchy
            or QueryScope.AncestorsAndSelf;
        var hasQualifier = HasFieldFilter(parsed) || !string.IsNullOrEmpty(parsed.Path) || scopeWalks;

        if (hasQualifier)
        {
            foreach (var node in _providerNodes)
            {
                if (!string.IsNullOrEmpty(parsed.Path) && !MatchesAnyPath(node, parsed)) continue;
                if (!_evaluator.Matches(node, parsed)) continue;
                if (IsExcludedByContext(node, context)) continue;
                if (parsed.IsMain == true && node.MainNode != node.Path) continue;
                if (node is T typed) items.Add(typed);
            }
        }

        var isSearch = string.Equals(context, "search", StringComparison.OrdinalIgnoreCase);
        if (!isSearch && hasQualifier)
        {
            foreach (var node in _configNodes)
            {
                if (!MatchesAnyPath(node, parsed)) continue;
                if (!_evaluator.Matches(node, parsed)) continue;
                if (IsExcludedByContext(node, context)) continue;
                if (parsed.IsMain == true && node.MainNode != node.Path) continue;
                if (node is T typed) items.Add(typed);
            }
        }
        return items;
    }

    /// <summary>
    /// Multi-value path support: <c>path:a|b|c</c> parses to
    /// <see cref="ParsedQuery.Paths"/> with three entries. <see cref="MatchesPath"/>
    /// only considers <see cref="ParsedQuery.Path"/> (the first value) — which
    /// silently drops ancestor matches for path-resolution queries
    /// (<see cref="MeshWeaver.Hosting.PathResolutionService"/> emits
    /// <c>path:a/b/c/d|a/b/c|a/b|a</c>). This helper iterates every path value
    /// and returns true on any match, mirroring the loop in
    /// <c>StorageAdapterMeshQueryProvider</c>.
    /// </summary>
    private static bool MatchesAnyPath(MeshNode node, ParsedQuery parsed)
    {
        if (parsed.Paths is { Count: > 1 } multi)
        {
            foreach (var p in multi)
            {
                if (MatchesPathValue(node, p, parsed.Scope))
                    return true;
            }
            return false;
        }
        return MatchesPath(node, parsed);
    }

    private static bool MatchesPathValue(MeshNode node, string path, QueryScope scope)
    {
        var nodePath = node.Path;
        var nodeNamespace = node.Namespace ?? "";
        return scope switch
        {
            QueryScope.Exact =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),
            QueryScope.Children =>
                string.Equals(nodeNamespace, path, StringComparison.OrdinalIgnoreCase),
            QueryScope.Descendants =>
                nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.Subtree =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.Hierarchy =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || nodePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.AncestorsAndSelf =>
                string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(nodePath + "/", StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(nodePath, path, StringComparison.OrdinalIgnoreCase),
        };
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
