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
    /// <c>queryNamespaces</c> is in our segment set. Unscoped
    /// queries are pre-filtered by the aggregator (every provider
    /// participates) so this predicate is only consulted for scoped ones.</para>
    /// </summary>
    public static Func<IReadOnlyList<string>, bool> BuildDefaultMatches(
        IEnumerable<IStaticNodeProvider> providers,
        MeshConfiguration? meshConfiguration)
    {
        var firstSegments = providers
            .SelectMany(p => p.GetStaticNodes())
            .Concat((meshConfiguration?.AddMeshNodesList ?? Enumerable.Empty<MeshNode>()))
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

    /// <summary>
    /// Creates the static-node query provider over the supplied static node
    /// sources and seed configuration, pre-computing the provider/config node
    /// buckets and the node-type index used to short-circuit non-matching
    /// queries.
    /// </summary>
    /// <param name="providers">The static node providers (built-in roles, type definitions, etc.).</param>
    /// <param name="matches">Predicate deciding whether a scoped query's namespaces are owned by this provider.</param>
    /// <param name="meshConfiguration">Optional mesh configuration supplying seed (<c>AddMeshNodes</c>) nodes and context-exclusion rules; may be <see langword="null"/>.</param>
    /// <param name="loggerFactory">Optional logger factory; diagnostics are suppressed when <see langword="null"/>.</param>
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
            ((meshConfiguration?.AddMeshNodesList ?? Enumerable.Empty<MeshNode>()))
                .Select(n => n.Path)
                .Where(p => !string.IsNullOrEmpty(p)),
            StringComparer.OrdinalIgnoreCase);

        _providerNodes = providerList
            .SelectMany(p => p.GetStaticNodes())
            // Definition-only nodes (a DB-synced NodeType catalog's in-memory type-def) are NOT
            // queryable — Postgres owns the runtime node at their path. Excluding them keeps the
            // bare partition path (e.g. path:Harness) resolving to exactly the PG nodeType:NodeType
            // root, with no second claimant. See Doc/Architecture/NodeTypeCatalogs.md.
            .Where(n => !n.IsDefinitionOnly)
            .Where(n => !configPaths.Contains(n.Path))
            .ToArray();
        _logger?.LogDebug(
            "[StaticNodeQueryProvider] ctor: {Providers} provider(s) -> {Count} nodes; byType=[{ByType}]; byNamespace(top)=[{ByNs}]",
            providerList.Count,
            _providerNodes.Length,
            string.Join(", ", _providerNodes.GroupBy(n => n.NodeType ?? "(null)").Select(g => $"{g.Key}={g.Count()}")),
            string.Join(", ", _providerNodes.GroupBy(n => n.Namespace ?? "(null)").OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}={g.Count()}")));

        _configNodes = ((meshConfiguration?.AddMeshNodesList ?? Enumerable.Empty<MeshNode>()))
            // See _providerNodes: a definition-only catalog type-def is never a query result.
            .Where(n => !n.IsDefinitionOnly)
            .ToArray();

        _allNodes = _providerNodes.Concat(_configNodes).ToArray();

        _nodeTypes = new HashSet<string>(
            _allNodes
                .Where(n => !string.IsNullOrEmpty(n.NodeType))
                .Select(n => n.NodeType!),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reactive autocomplete — <b>fully synchronous</b>. The static catalog is purely in-memory and
    /// never performs I/O, so it builds the snapshot and <c>Observable.Return</c>s it (no
    /// <c>Task.Run</c>, no <c>await</c>, no async-enumerable). This is the "in-memory never async"
    /// rule in action — see <c>Doc/Architecture/AggregatingProviders.md</c> → "The async boundary
    /// lives at the I/O edge".
    /// </summary>
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
        string? contextPath = null, string? context = null)
    {
        var providerName = ((IMeshQueryProvider)this).Name;
        var rows = (IReadOnlyCollection<QueryResult>)ComputeSuggestions(basePath, prefix, mode, limit, contextPath, context)
            .Select(s => new QueryResult
            {
                Path = s.Path,
                Name = s.Name,
                NodeType = s.NodeType,
                Icon = s.Icon,
                Score = s.Score,
                ProviderName = providerName,
            })
            .ToList();
        return Observable.Return(rows);
    }

    /// <summary>
    /// Synchronous suggestion computation backing the reactive <see cref="Autocomplete"/> override.
    /// Pure in-memory scan + score + order — no I/O, so no async.
    /// </summary>
    private IReadOnlyList<QuerySuggestion> ComputeSuggestions(
        string basePath, string prefix, AutocompleteMode mode, int limit,
        string? contextPath, string? context)
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

        return ordered.Take(limit).ToList();
    }

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
    {
        // Static nodes are purely in-memory (no I/O) and never change.
        // Collect synchronously and return a completed Observable.Return — no async, no scheduler.
        var parsedQuery = _parser.Parse(request.Query);
        var items = CollectStaticResults<T>(parsedQuery, request.Context);
        _logger?.LogDebug(
            "[StaticNodeQueryProvider] Query query='{Query}' parsedPath='{Path}' parsedNodeType='{NodeType}' -> {Count} item(s)",
            request.Query, parsedQuery.Path ?? "(null)",
            (parsedQuery.Filter as MeshWeaver.Mesh.QueryComparison)?.Condition.Values is { } vs
                ? string.Join("|", vs) : "(complex)",
            items.Count);
        // Score the matched items when the query carries a text-search term.
        // For pure filter / namespace / nodeType queries we leave Scores=null so
        // the aggregator falls back to insertion order — there's no relevance
        // signal to surface ("give me all Threads in this namespace" is
        // unordered with respect to score).
        var scores = ComputeScores(items, parsedQuery);
        return Observable.Return(new QueryResultChange<T>
        {
            ChangeType = QueryChangeType.Initial,
            Items = items,
            Scores = scores,
            Query = parsedQuery,
            Version = 0,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Score the result set with <see cref="MeshWeaver.AI.Completion.FuzzyScorer"/> when the query has
    /// a <c>TextSearch</c> term — fzf-style ranking against the item's Name
    /// (with Path as a fallback for items without a Name). The aggregator
    /// (<c>MeshQuery.ClipMergedInitial</c>) sorts by these scores descending
    /// after applying any explicit <c>OrderBy</c>, so a typed-name match
    /// climbs above filter-only matches without the caller having to add
    /// <c>sort:</c>. Returns <see langword="null"/> for non-MeshNode T or
    /// non-text queries — aggregator preserves insertion order in those cases.
    /// </summary>
    private static IReadOnlyList<double>? ComputeScores<T>(IReadOnlyList<T> items, ParsedQuery parsed)
    {
        if (items.Count == 0) return null;
        if (string.IsNullOrEmpty(parsed.TextSearch)) return null;
        var scorer = new MeshWeaver.AI.Completion.FuzzyScorer();
        // FuzzyScorer.Score returns ScoredItem<T> filtered to MATCHES only —
        // it drops non-matches. For our purpose every item already matched the
        // server-side filter; we just want the relevance score on each. Re-run
        // scoring per-item via a one-element collection so we never lose rows.
        var scores = new double[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is not MeshNode node)
            {
                scores[i] = 0;
                continue;
            }
            var text = !string.IsNullOrEmpty(node.Name) ? node.Name : (node.Path ?? string.Empty);
            var scored = scorer.Score(new[] { node }, parsed.TextSearch, _ => text).FirstOrDefault();
            scores[i] = scored?.Score ?? 0;
        }
        return scores;
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
            or QueryScope.AncestorsAndSelf
            or QueryScope.NextLevel;
        var hasQualifier = HasFieldFilter(parsed) || !string.IsNullOrEmpty(parsed.Path) || scopeWalks;

        // NextLevel = the populated frontier. The suppressor universe is EVERY static node
        // (a real node occupies its level even if it doesn't match the outer filter), so build
        // the frontier path set once over _allNodes, then emit only nodes on it. Mirrors the
        // Postgres anti-join + the pedestrian walk's frontier filter.
        HashSet<string>? frontierPaths = parsed.Scope == QueryScope.NextLevel
            ? NamespaceFrontier.Frontier(parsed.Path ?? "", _allNodes.Select(n => n.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        bool MatchesScope(MeshNode node) => frontierPaths != null
            ? frontierPaths.Contains(node.Path)
            : string.IsNullOrEmpty(parsed.Path) || MatchesAnyPath(node, parsed);

        if (hasQualifier)
        {
            foreach (var node in _providerNodes)
            {
                if (!MatchesScope(node)) continue;
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
                if (!MatchesScope(node)) continue;
                if (!_evaluator.Matches(node, parsed)) continue;
                if (IsExcludedByContext(node, context)) continue;
                if (parsed.IsMain == true && node.MainNode != node.Path) continue;
                if (node is T typed) items.Add(typed);
            }
        }
        return items;
    }

    // namespace candidates a query targets = ExtractNamespaces + first segment
    // of Path. Mirrors MeshQuery.MergeQueryNamespaces (internal there) so the
    // self-filter in QueryAsync sees the same shape the aggregator's predicate
    // would have, were it to call Matches().
    private static IReadOnlyList<string> MergeNamespaceCandidates(ParsedQuery parsed)
    {
        var fromFilter = parsed.ExtractNamespaces();
        var firstSegment = string.IsNullOrEmpty(parsed.Path) ? null : parsed.Path.Split('/', 2)[0];
        if (string.IsNullOrEmpty(firstSegment))
            return fromFilter;
        if (fromFilter.Count == 0)
            return new[] { firstSegment };
        var combined = new List<string>(fromFilter.Count + 1);
        combined.AddRange(fromFilter);
        if (!combined.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            combined.Add(firstSegment);
        return combined;
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

    /// <inheritdoc />
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
    {
        // Pure in-memory — no I/O, so a single completed Observable.Return.
        T? result = default;
        var node = _allNodes.FirstOrDefault(n =>
            string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
        if (node != null && typeof(MeshNode).GetProperty(property)?.GetValue(node) is T typedValue)
            result = typedValue;
        return Observable.Return(result);
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
        if (node.IsExcludedFromContext(context))
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
