using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// In-memory implementation of IMeshService.
/// Extracts query functionality from AdapterPersistenceService for use as a standalone service.
/// </summary>
internal class MeshQueryEngine : IMeshQueryProvider, IMeshQueryCore
{
    private readonly IStorageAdapter persistence;
    private readonly AccessService? accessService;
    private readonly IDataChangeNotifier? changeNotifier;
    private readonly MeshConfiguration? meshConfiguration;
    // 🚨 Lazy<INodeValidator> — NOT bare INodeValidator. RlsNodeValidator
    // (the only non-test impl) takes ISecurityService at construction time.
    // SecurityService warms up by subscribing to a synced query during its
    // own ctor → reaches MeshQueryEngine → resolving INodeValidators eagerly
    // would re-enter SecurityService and cycle. Lazy<T> defers each
    // validator's construction to first ValidateReadAsync access; by then
    // SecurityService is fully built.
    private readonly IEnumerable<Lazy<INodeValidator>>? nodeValidators;
    private readonly ILogger<MeshQueryEngine>? logger;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();
    private long _version;

    /// <summary>
    /// Static-node providers folded into the query stream — built-in agents,
    /// well-known roles, type definitions. Indexed by path so
    /// <see cref="FindMatchingNodesAsync"/> can yield them alongside
    /// persistence-served nodes with path-keyed dedup against persistence
    /// (RoutingPersistenceServiceCore consumes IStaticNodeProvider directly,
    /// so the same node may come from both sources in prod).
    /// </summary>
    private readonly IReadOnlyDictionary<string, MeshNode> staticNodes;

    /// <summary>
    /// First-segment namespaces owned by static partitions (Agent, Model, …).
    /// The engine excludes these from its <see cref="Matches"/> predicate so the
    /// aggregator routes those queries to <see cref="StaticNodeQueryProvider"/>
    /// only — Postgres / in-memory persistence never round-trips for built-in
    /// content.
    /// </summary>
    private readonly HashSet<string> _excludedNamespaces;

    public MeshQueryEngine(
        IStorageAdapter persistence,
        // 🚨 NO ISecurityService here — that parameter created the Autofac
        // cycle SecurityService → SyncedQueryMeshNodes → IMeshQueryCore →
        // MeshQueryEngine → SecurityService. Per-node read filtering on the
        // secured IMeshQueryProvider surface goes through INodeValidator
        // instead, which has no back-reference into the synced-query path.
        // GetEffectivePermissions-style filtering for non-MeshNode results
        // is intentionally dropped — IMeshService.QueryAsync results are
        // MeshNodes today, and any future non-MeshNode projection should
        // declare an INodeValidator if it needs gating.
        AccessService? accessService = null,
        IDataChangeNotifier? changeNotifier = null,
        MeshConfiguration? meshConfiguration = null,
        IEnumerable<Lazy<INodeValidator>>? nodeValidators = null,
        IEnumerable<IStaticNodeProvider>? staticProviders = null,
        IEnumerable<IPartitionStorageProvider>? partitionProviders = null,
        ILogger<MeshQueryEngine>? logger = null)
    {
        this.persistence = persistence;
        this.accessService = accessService;
        this.changeNotifier = changeNotifier;
        this.meshConfiguration = meshConfiguration;
        this.nodeValidators = nodeValidators;
        this.logger = logger;
        staticNodes = (staticProviders ?? Enumerable.Empty<IStaticNodeProvider>())
            .SelectMany(p => p.GetStaticNodes())
            .Where(n => !string.IsNullOrEmpty(n.Path))
            .GroupBy(n => n.Path!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // Exclusion derived from IPartitionStorageProvider — the partition
        // registry. Only static-source partitions (DataSource="static") count
        // as "owned by someone else" for the Matches predicate. AddMeshNodes
        // seeds (writable runtime namespaces surfaced via
        // MeshConfigurationStaticNodeProvider) are NOT in this list — they
        // remain queryable through the engine.
        _excludedNamespaces = (partitionProviders ?? Enumerable.Empty<IPartitionStorageProvider>())
            .Where(p => string.Equals(p.PartitionDefinition?.DataSource, "static", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public bool Matches(IReadOnlyList<string> queryNamespaces)
    {
        // Engage when at least one of the query's namespaces is NOT static-owned —
        // i.e., we have writable persistence rows to contribute for it.
        for (var i = 0; i < queryNamespaces.Count; i++)
            if (!_excludedNamespaces.Contains(queryNamespaces[i]))
                return true;
        return false;
    }

    /// <summary>
    /// Default debounce interval for batching rapid changes.
    /// </summary>
    public static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets the effective user ID from the request or from the current access context.
    /// Returns WellKnownUsers.Anonymous for unauthenticated/virtual access.
    /// </summary>
    private string GetEffectiveUserId(MeshQueryRequest request)
    {
        // If request has explicit UserId set (including empty for anonymous), use it
        if (request.UserId != null)
            return string.IsNullOrEmpty(request.UserId) ? WellKnownUsers.Anonymous : request.UserId;

        // Get from access context, falling back to circuit context
        var userId = accessService?.Context?.ObjectId
                     ?? accessService?.CircuitContext?.ObjectId;
        return string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId;
    }

    /// <inheritdoc />
    /// <summary>
    /// Core query without access control — for infrastructure use (NodeTypeService, compilation).
    /// </summary>
    async IAsyncEnumerable<object> IMeshQueryCore.QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in QueryCoreAsync(request, options, ct))
            yield return item;
    }

    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (matched, parsedQuery, basePaths) = await CollectMatchedAsync(
            request, options, useSecurityFilter: true, ct);

        // For source:activity, filter to nodes that actually have _activity children.
        // Scan EVERY query's basePath for Activity satellite nodes and union their
        // MainNode paths so multi-query unions don't filter queries #2+ against
        // query #0's subtree only.
        // `source:activity` filtering: previously walked persistence descendants.
        // Now handled by the pedestrian query provider (SimpleMeshNodeStorageQueryProvider)
        // or by Postgres SQL-side JOIN — the engine knows nothing about how it's done.

        // Apply sort
        IEnumerable<object> sorted = matched;
        if (parsedQuery.OrderBy != null)
        {
            sorted = parsedQuery.OrderBy.Descending
                ? matched.OrderByDescending(n => GetSortableValue(n, parsedQuery.OrderBy.Property))
                : matched.OrderBy(n => GetSortableValue(n, parsedQuery.OrderBy.Property));
        }

        // Apply skip/limit and project. The effective limit is the FIRST query's
        // explicit `limit:N` override (if any) AND/OR request.Limit — whichever
        // is smaller wins, so callers can't escape a request-level cap.
        var effectiveLimit = MinLimit(request.Limit, parsedQuery.Limit);
        int skipped = 0;
        int yielded = 0;
        foreach (var node in sorted)
        {
            if (request.Skip.HasValue && request.Skip.Value > 0 && skipped < request.Skip.Value)
            {
                skipped++;
                continue;
            }

            yield return parsedQuery.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                : node;

            yielded++;
            if (effectiveLimit.HasValue && effectiveLimit.Value > 0
                && yielded >= effectiveLimit.Value)
                yield break;
        }
    }

    private static int? MinLimit(int? a, int? b) =>
        (a, b) switch
        {
            (null, null) => null,
            (null, var v) => v,
            (var v, null) => v,
            ({ } x, { } y) => Math.Min(x, y)
        };

    /// <summary>
    /// Core query without access control — used by IMeshQueryCore for infrastructure.
    /// Shares parsing/sorting/paging logic with QueryAsync but skips all AC checks.
    /// </summary>
    private async IAsyncEnumerable<object> QueryCoreAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (matched, parsedQuery, basePaths) = await CollectMatchedAsync(
            request, options, useSecurityFilter: false, ct);

        // `source:activity` activity-MainNode filter via persistence loop deleted —
        // see comment in QueryAsync above. Backend providers do this themselves
        // (Postgres satellite-table join; FS scans the _Activity satellite tree).

        IEnumerable<object> sorted = matched;
        if (parsedQuery.OrderBy != null)
        {
            sorted = parsedQuery.OrderBy.Descending
                ? matched.OrderByDescending(n => GetSortableValue(n, parsedQuery.OrderBy.Property))
                : matched.OrderBy(n => GetSortableValue(n, parsedQuery.OrderBy.Property));
        }

        var effectiveLimit = MinLimit(request.Limit, parsedQuery.Limit);
        int skipped = 0, yielded = 0;
        foreach (var node in sorted)
        {
            if (request.Skip.HasValue && request.Skip.Value > 0 && skipped < request.Skip.Value)
            { skipped++; continue; }

            yield return parsedQuery.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                : node;

            yielded++;
            if (effectiveLimit.HasValue && effectiveLimit.Value > 0
                && yielded >= effectiveLimit.Value)
                yield break;
        }
    }

    /// <summary>
    /// Shared collector: iterates <see cref="MeshQueryRequest.EffectiveQueries"/>,
    /// runs <see cref="FindMatchingNodesAsync"/> per query, and unions hits by
    /// <see cref="MeshNode.Path"/>. Returns the matched nodes plus the FIRST
    /// query's parsed form + the union of every query's base path (used by
    /// the post-filter / sort blocks in the public callers).
    /// <para>
    /// <see cref="MeshQueryRequest.Limit"/> is intentionally NOT pushed into
    /// any per-query parse: doing so on query #0 only made the union
    /// iteration-order dependent (query #0 might hit its limit before yielding
    /// its most relevant rows, while queries #1+ contributed everything past
    /// it). The Limit is enforced post-union in the public callers instead.
    /// </para>
    /// </summary>
    private async Task<(List<object> Matched, ParsedQuery FirstParsed, IReadOnlyList<string> BasePaths)>
        CollectMatchedAsync(
            MeshQueryRequest request,
            JsonSerializerOptions options,
            bool useSecurityFilter,
            CancellationToken ct)
    {
        var effectiveQueries = request.EffectiveQueries;
        var userId = GetEffectiveUserId(request);

        var matchedByPath = new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
        var nonNodeMatched = new List<object>();
        var seenRefs = new HashSet<object>(ReferenceEqualityComparer.Instance);

        ParsedQuery? firstParsed = null;
        var basePaths = new List<string>(effectiveQueries.Count);

        for (var qi = 0; qi < effectiveQueries.Count; qi++)
        {
            var parsedQuery = _parser.Parse(effectiveQueries[qi]);
            if (parsedQuery.Source is QuerySource.Activity or QuerySource.Accessed)
                parsedQuery = parsedQuery with { IsMain = true };

            var (basePath, effectiveScope) = ResolvePathAndScope(parsedQuery, request);
            var context = request.Context ?? parsedQuery.Context;
            basePaths.Add(basePath);

            if (qi == 0)
            {
                firstParsed = parsedQuery;
            }

            await foreach (var node in FindMatchingNodesAsync(
                parsedQuery, effectiveScope, basePath, userId, context, request, options, ct))
            {
                if (node is MeshNode meshNode)
                {
                    if (!string.IsNullOrEmpty(meshNode.Path) && matchedByPath.ContainsKey(meshNode.Path))
                        continue;
                    if (useSecurityFilter && !await ValidateReadAsync(meshNode, userId, ct))
                        continue;
                    if (!string.IsNullOrEmpty(meshNode.Path))
                        matchedByPath[meshNode.Path] = meshNode;
                    else if (seenRefs.Add(meshNode))
                        nonNodeMatched.Add(meshNode);
                }
                else if (seenRefs.Add(node))
                {
                    nonNodeMatched.Add(node);
                }
            }
        }

        var matched = matchedByPath.Values.Cast<object>().Concat(nonNodeMatched).ToList();
        return (matched, firstParsed ?? _parser.Parse(""), basePaths);
    }

    /// <summary>
    /// Resolves effective base path (request.DefaultPath fallback) and scope
    /// (Children/Subtree fallback when query has no path + Exact scope) — same
    /// rules previously inlined at the top of <see cref="QueryAsync"/>.
    /// </summary>
    private (string BasePath, QueryScope Scope) ResolvePathAndScope(
        ParsedQuery parsedQuery, MeshQueryRequest request)
    {
        var effectivePath = parsedQuery.Path;
        var effectiveScope = parsedQuery.Scope;
        if (string.IsNullOrEmpty(effectivePath))
        {
            if (!string.IsNullOrEmpty(request.DefaultPath))
                effectivePath = request.DefaultPath;
            if (parsedQuery.Scope == QueryScope.Exact)
                effectiveScope = parsedQuery.HasConditions ? QueryScope.Subtree : QueryScope.Children;
        }
        return (NormalizePath(effectivePath), effectiveScope);
    }

    /// <summary>
    /// Yields matching nodes as they are discovered across all applicable scopes.
    /// No buffering or sorting — results stream immediately.
    /// </summary>
    private async IAsyncEnumerable<object> FindMatchingNodesAsync(
        ParsedQuery parsedQuery,
        QueryScope effectiveScope,
        string basePath,
        string userId,
        string? context,
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pathsToSearch = GetPathsForScope(basePath, effectiveScope);
        var emittedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Exact path matches — bridge IObservable<MeshNode?> back to a one-shot Task
        // here at the IMeshQuery boundary (sanctioned per AsynchronousCalls.md).
        foreach (var searchPath in pathsToSearch)
        {
            var node = await persistence.Read(searchPath, options).FirstAsync().ToTask(ct);
            if (node != null && _evaluator.Matches(node, parsedQuery)
                && !IsExcludedByContext(node, context)
                && !IsExcludedByIsMain(node, parsedQuery))
            {
                if (!string.IsNullOrEmpty(node.Path)) emittedPaths.Add(node.Path);
                yield return node;
            }
        }

        // Children / Descendants / Hierarchy / Subtree scopes are NOT handled here.
        // 🚨 The engine itself knows nothing about descendant walks — that's the
        // pedestrian-backend's job (in-memory / file-system / embedded resources)
        // via `SimpleMeshNodeStorage` + its dedicated `IMeshQueryProvider`.
        // Postgres handles these scopes via SQL pushdown in `PostgreSqlMeshQuery`.
        // What this engine still serves: static-node fold-in (below) + Exact-path
        // probes (above).

        // Static nodes — same path/scope/context filtering as persistence
        // results, with path-keyed dedup so backends that include static
        // nodes directly (RoutingPersistenceServiceCore) don't double-count.
        //
        // 🚨 Skip when context == "search": StaticNodeQueryProvider is the
        // canonical search-context source for static partitions and applies
        // its own context filter (type definitions excluded from search).
        // Iterating staticNodes here too leaks config-node type definitions
        // into search results (regression caught by
        // StaticNodeQueryContextTests.SearchContext_ExcludesStaticNodes).
        if (!string.Equals(context, "search", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (_, node) in staticNodes)
            {
                if (string.IsNullOrEmpty(node.Path)) continue;
                if (emittedPaths.Contains(node.Path)) continue;
                if (!StaticNodeMatchesScope(node, basePath, effectiveScope)) continue;
                if (IsExcludedByContext(node, context)) continue;
                if (IsExcludedByIsMain(node, parsedQuery)) continue;
                if (!_evaluator.Matches(node, parsedQuery)) continue;
                yield return node;
            }
        }
    }

    private static bool StaticNodeMatchesScope(MeshNode node, string basePath, QueryScope scope)
    {
        var path = node.Path ?? "";
        if (string.IsNullOrEmpty(basePath))
            return true;
        return scope switch
        {
            QueryScope.Exact => string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase),
            QueryScope.Children =>
                string.Equals(node.Namespace ?? "", basePath, StringComparison.OrdinalIgnoreCase),
            QueryScope.Subtree =>
                string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.Descendants =>
                path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.Ancestors => basePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.AncestorsAndSelf =>
                string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase) ||
                basePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            QueryScope.Hierarchy =>
                string.Equals(path, basePath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase) ||
                basePath.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    /// <summary>
    /// Gets the path for an item (MeshNode or object with Path property).
    /// </summary>
    private static string? GetItemPath(object item)
    {
        if (item is MeshNode node)
            return node.Path;

        // Try to get Path property via reflection for partition objects
        var pathProp = item.GetType().GetProperty("Path");
        if (pathProp != null)
            return pathProp.GetValue(item) as string;

        return null;
    }

    /// <summary>
    /// Gets a sortable value from a node for the given property name.
    /// Maps common property names to MeshNode fields.
    /// </summary>
    private static object? GetSortableValue(object item, string property)
    {
        if (item is not MeshNode node) return null;
        return property.ToLowerInvariant() switch
        {
            "lastmodified" or "last_modified" => node.LastModified,
            "name" => node.Name,
            "order" or "display_order" => node.Order,
            "path" => node.Path,
            "nodetype" or "node_type" => node.NodeType,
            "category" => node.Category,
            "state" => node.State,
            "version" => node.Version,
            _ => node.Name
        };
    }

    /// <summary>
    /// Validates a node read operation using INodeValidator instances from DI.
    /// Mirrors MeshCatalog.ValidateReadAsync logic.
    /// </summary>
    private async Task<bool> ValidateReadAsync(MeshNode node, string userId, CancellationToken ct = default)
    {
        if (nodeValidators == null)
            return true;

        // Always use the effective userId for the validation context.
        // The query's explicit UserId takes precedence over session AccessContext
        // to prevent admin context from leaking into public queries.
        var accessContext = !string.IsNullOrEmpty(userId) && userId != WellKnownUsers.Anonymous
            ? new AccessContext { ObjectId = userId }
            : accessService?.Context ?? accessService?.CircuitContext;

        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Read,
            Node = node,
            AccessContext = accessContext
        };

        foreach (var lazyValidator in nodeValidators)
        {
            // Resolve lazily — see ctor comment on `nodeValidators` for the
            // SecurityService cycle this defers.
            var validator = lazyValidator.Value;
            if (validator.SupportedOperations.Count > 0 &&
                !validator.SupportedOperations.Contains(NodeOperation.Read))
                continue;

            var result = await validator.Validate(context).FirstAsync().ToTask(ct);
            if (!result.IsValid)
            {
                logger?.LogDebug("Validator {Validator} rejected read on node {Path}: {Error}",
                    validator.GetType().Name, node.Path, result.ErrorMessage);
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, null, AutocompleteMode.PathFirst, limit, null, null, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        CancellationToken ct = default)
        => AutocompleteAsync(basePath, prefix, options, null, mode, limit, contextPath, context, ct);

    /// <summary>
    /// Autocomplete with user ID for access control filtering.
    /// </summary>
    public async IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        string? userId,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(basePath);
        var normalizedPrefix = prefix ?? "";

        var suggestions = new List<QuerySuggestion>();

        // Autocomplete on this engine: root-node exact match only. Descendant
        // walks happen in the per-backend IMeshQueryProvider (pedestrian via
        // SimpleMeshNodeStorageQueryProvider; Postgres via its own pushdown).
        async IAsyncEnumerable<MeshNode> GetNodesForAutocomplete()
        {
            if (!string.IsNullOrEmpty(normalizedPath))
            {
                var rootNode = await persistence.Read(normalizedPath, options).FirstAsync().ToTask(ct);
                if (rootNode != null)
                    yield return rootNode;
            }
            await Task.CompletedTask;
        }

        await foreach (var node in GetNodesForAutocomplete())
        {
            // Skip node types excluded from autocomplete (configured via AddAutocompleteExcludedTypes)
            if (meshConfiguration?.AutocompleteExcludedNodeTypes.Contains(node.NodeType ?? "") == true)
                continue;

            // Context-based exclusion for autocomplete
            if (context != null && IsExcludedByContext(node, context))
                continue;

            var name = node.Name ?? node.Id ?? node.Path ?? "";
            var nameLower = name;
            var pathLower = node.Path ?? "";

            // Calculate match score based on prefix match (check both name and path)
            double score = 0;

            // Name matches (higher priority)
            if (nameLower.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 100 - (nameLower.Length - normalizedPrefix.Length); // Exact prefix match, shorter is better
            }
            else if (nameLower.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 50 - (nameLower.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase)); // Contains match
            }
            // Path matches (lower priority than name)
            else if (pathLower.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score = 30 - (pathLower.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase) * 0.1); // Path contains match
            }
            else if (FuzzyMatch(nameLower, normalizedPrefix))
            {
                score = 25; // Fuzzy match on name
            }

            score += PathProximity.ComputeBoost(contextPath, node.Path);

            if (score > 0)
            {
                suggestions.Add(new QuerySuggestion(node.Path ?? "", name, node.NodeType, score, node.Icon));
            }
        }

        // Order based on mode
        IEnumerable<QuerySuggestion> ordered = mode switch
        {
            // PathFirst: path length first, then score, then name (for path-based autocomplete like @references)
            AutocompleteMode.PathFirst => suggestions
                .OrderBy(s => s.Path.Length)
                .ThenByDescending(s => s.Score)
                .ThenBy(s => s.Name),

            // RelevanceFirst: score first (name match > path match > other), then path length, then name (for node selection)
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
        {
            yield return suggestion;
        }
    }

    private static bool FuzzyMatch(string text, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return true;

        int prefixIndex = 0;
        foreach (var c in text)
        {
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(prefix[prefixIndex]))
            {
                prefixIndex++;
                if (prefixIndex >= prefix.Length)
                    return true;
            }
        }
        return false;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return "";

        // Remove leading/trailing slashes and normalize
        return path.Trim('/').Replace('\\', '/');
    }

    private static List<string> GetPathsForScope(string basePath, QueryScope scope)
    {
        var paths = new List<string>();

        // Children and Descendants scopes do NOT include self
        // Children are fetched separately by GetChildrenAsync
        // Descendants are fetched separately by GetDescendantsAsync
        if (scope == QueryScope.Children || scope == QueryScope.Descendants)
        {
            return paths;
        }

        // Include self for: Exact, Hierarchy, Subtree, AncestorsAndSelf
        // Ancestors does NOT include self
        if (scope != QueryScope.Ancestors)
        {
            paths.Add(basePath);
        }

        // Add ancestor paths for: Ancestors, AncestorsAndSelf, Hierarchy
        if (scope == QueryScope.Ancestors || scope == QueryScope.AncestorsAndSelf || scope == QueryScope.Hierarchy)
        {
            var segments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                var ancestorPath = string.Join("/", segments.Take(i));
                if (!paths.Contains(ancestorPath, StringComparer.OrdinalIgnoreCase))
                    paths.Add(ancestorPath);
            }
        }

        return paths;
    }

    /// <inheritdoc />
    public async Task<T?> SelectAsync<T>(string path, string property, JsonSerializerOptions options, CancellationToken ct = default)
    {
        // SelectAsync<T> contract is Task by design; bridge once at the call site.
        var node = await persistence.Read(path, options).FirstAsync().ToTask(ct);
        if (node == null)
            return default;

        var prop = typeof(MeshNode).GetProperty(property);
        if (prop == null)
            return default;

        var value = prop.GetValue(node);
        if (value is T typedValue)
            return typedValue;

        return default;
    }

    /// <inheritdoc cref="IMeshQueryCore.ObserveQuery{T}"/>
    IObservable<QueryResultChange<T>> IMeshQueryCore.ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options)
        => ObserveQueryInternal<T>(request, options, useSecurityFilter: false);

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> ObserveQuery<T>(MeshQueryRequest request, JsonSerializerOptions options)
        => ObserveQueryInternal<T>(request, options, useSecurityFilter: true);

    /// <summary>
    /// Shared ObserveQuery body. <paramref name="useSecurityFilter"/> selects between the
    /// security-filtered <see cref="QueryAsync"/> (IMeshQueryProvider surface) and the raw
    /// <see cref="QueryCoreAsync"/> (IMeshQueryCore surface). The latter is what
    /// SecurityService consumes via SyncedQueryMeshNodes — it must NOT re-enter
    /// ISecurityService for filtering, otherwise the DI container detects a cycle.
    /// </summary>
    private IObservable<QueryResultChange<T>> ObserveQueryInternal<T>(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        bool useSecurityFilter)
    {
        // Use the synchronous Observable.Create overload so no TaskScheduler is
        // captured at subscribe-time. Observable.Create(async ...) captures the
        // caller's scheduler; when that caller is an Orleans grain handler the
        // continuation deadlocks against the grain's single-threaded scheduler.
        return Observable.Create<QueryResultChange<T>>(observer =>
        {
            // For change-feed scoping, accept a notification if it matches ANY
            // of the request's queries' resolved (basePath, scope) pairs. The
            // first query supplies the parsedQuery emitted on QueryResultChange
            // for back-compat with single-query consumers.
            var effectiveQueries = request.EffectiveQueries;
            var scopeFilters = effectiveQueries
                .Select(q =>
                {
                    var parsed = _parser.Parse(q);
                    var (bp, sc) = ResolvePathAndScope(parsed, request);
                    return (BasePath: bp, Scope: sc);
                })
                .ToList();
            var parsedQuery = _parser.Parse(effectiveQueries[0]);
            var (firstBasePath, firstScope) = scopeFilters[0];
            var effectivePath = firstBasePath;
            var effectiveScope = firstScope;
            var normalizedBasePath = effectivePath;
            var currentItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            var disposables = new CompositeDisposable();
            var cts = new CancellationTokenSource();
            disposables.Add(cts);

            IAsyncEnumerable<object> QueryStream(CancellationToken ct) =>
                useSecurityFilter ? QueryAsync(request, options, ct) : QueryCoreAsync(request, options, ct);

            // Enumerates the IAsyncEnumerable on a fresh thread-pool thread so that
            // no custom TaskScheduler (Orleans, ASP.NET) is captured by the async
            // state machine. Task.Factory.StartNew with TaskScheduler.Default is the
            // explicit form of "run on thread pool, no inherited scheduler".
            IObservable<List<(string? Path, T Item)>> RunQuery(CancellationToken ct) =>
                Observable.Create<List<(string?, T)>>(inner =>
                {
                    Task.Factory.StartNew(async () =>
                    {
                        var results = new List<(string?, T)>();
                        try
                        {
                            await foreach (var item in QueryStream(ct))
                            {
                                if (item is T typed)
                                    results.Add((GetItemPath(item), typed));
                            }
                            inner.OnNext(results);
                            inner.OnCompleted();
                        }
                        catch (OperationCanceledException) { inner.OnCompleted(); }
                        catch (Exception ex) { inner.OnError(ex); }
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                    return Disposable.Empty;
                });

            // Race-fix: subscribe to changeNotifier BEFORE running the initial query so
            // that any NotifyChange events fired during the initial query's I/O window
            // are captured. Otherwise the events fire before the subscription is set
            // up and are silently lost (the DataChangeNotifier is a plain Subject<> with
            // no buffering). Symptom of the bug: synced query consumers (GetTokensForUser,
            // WaitForPermissionAsync) never see writes that complete during their first
            // Initial query — first emission has the stale snapshot and the live change
            // stream never replays the missed event.
            //
            // Approach: accumulate early notifications in a synchronized List until the
            // initial query completes. Inside the initialResults callback, swap to the
            // live Buffer pipeline AND drain the backlog as one synthetic batch.
            var earlyBacklog = new List<DataChangeNotification>();
            var earlyLock = new object();
            var initialDone = false;

            IDisposable? earlySubscription = null;
            if (changeNotifier != null)
            {
                earlySubscription = changeNotifier
                    .Where(n => scopeFilters.Any(sf =>
                        PathMatcher.ShouldNotify(n.Path, sf.BasePath, sf.Scope)))
                    .Subscribe(n =>
                    {
                        lock (earlyLock)
                        {
                            if (!initialDone)
                                earlyBacklog.Add(n);
                        }
                    });
                disposables.Add(earlySubscription);
            }

            disposables.Add(
                RunQuery(cts.Token).Subscribe(
                    initialResults =>
                    {
                        var initialItems = new List<T>();
                        foreach (var (path, item) in initialResults)
                        {
                            initialItems.Add(item);
                            if (!string.IsNullOrEmpty(path))
                                currentItems[path] = item;
                        }

                        // Wire up the LIVE change pipeline before emitting Initial so that
                        // any node mutation triggered by a subscriber reacting to Initial
                        // is guaranteed to be captured by the changeBuffer.
                        //
                        // Ordering matters: set up the LIVE subscription FIRST so no event
                        // can fire after "initialDone = true" but before there's any
                        // downstream subscriber. Events fired between live-set-up and
                        // backlog-swap may be captured BOTH by the live Buffer pipeline AND
                        // by the early subscription — ProcessBatch is idempotent against
                        // currentItems, so duplicate-processing is wasted CPU but correct.
                        DataChangeNotification[] backlog = Array.Empty<DataChangeNotification>();
                        if (changeNotifier != null)
                        {
                            // 1) Set up live subscription first — starts buffering immediately.
                            var changeBuffer = new Subject<DataChangeNotification>();
                            disposables.Add(changeBuffer);
                            disposables.Add(
                                changeNotifier
                                    .Where(n => scopeFilters.Any(sf =>
                                        PathMatcher.ShouldNotify(n.Path, sf.BasePath, sf.Scope)))
                                    .Subscribe(changeBuffer));
                            disposables.Add(
                                changeBuffer
                                    .Buffer(DefaultDebounceInterval)
                                    .Where(batch => batch.Count > 0)
                                    .Subscribe(batch =>
                                        disposables.Add(
                                            RunQuery(cts.Token).Subscribe(
                                                newResults => ProcessBatch(batch, newResults, currentItems, parsedQuery, observer),
                                                ex => observer.OnError(ex)))));

                            // 2) Snapshot + clear early backlog under lock; gate further early-capture.
                            lock (earlyLock)
                            {
                                backlog = earlyBacklog.ToArray();
                                earlyBacklog.Clear();
                                initialDone = true;
                            }

                            // 3) Early subscription is now redundant — live pipeline carries
                            //    all subsequent events. Dispose to free the upstream sub.
                            earlySubscription?.Dispose();
                        }

                        observer.OnNext(new QueryResultChange<T>
                        {
                            ChangeType = QueryChangeType.Initial,
                            Items = initialItems,
                            Query = parsedQuery,
                            Version = Interlocked.Increment(ref _version),
                            Timestamp = DateTimeOffset.UtcNow,
                        });

                        // Drain the early backlog as one immediate batch — these events
                        // fired DURING the initial query window, so we need to re-query
                        // and apply diffs against the just-populated currentItems.
                        if (backlog.Length > 0)
                        {
                            disposables.Add(
                                RunQuery(cts.Token).Subscribe(
                                    newResults => ProcessBatch(backlog.ToList(), newResults, currentItems, parsedQuery, observer),
                                    ex => observer.OnError(ex)));
                        }

                        if (changeNotifier == null)
                            observer.OnCompleted();
                    },
                    ex => observer.OnError(ex)));

            return disposables;
        });
    }

    private void ProcessBatch<T>(
        IList<DataChangeNotification> batch,
        List<(string? Path, T Item)> newResults,
        Dictionary<string, T> currentItems,
        ParsedQuery parsedQuery,
        IObserver<QueryResultChange<T>> observer)
    {
        var newItems = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, item) in newResults)
            if (!string.IsNullOrEmpty(path))
                newItems[path] = item;

        var changesByPath = batch
            .GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        // Supplement re-query results with entities from notifications
        // that haven't appeared in the store yet (write lag).
        foreach (var (path, change) in changesByPath)
        {
            if (change.Entity is T directMatch && _evaluator.Matches(directMatch, parsedQuery))
            {
                if (change.Kind == DataChangeKind.Deleted)
                    newItems.Remove(path);
                else
                    newItems[path] = directMatch;
            }
        }

        var addedItems = new List<T>();
        var updatedItems = new List<T>();
        var removedItems = new List<T>();

        foreach (var (path, item) in newItems)
        {
            if (currentItems.TryGetValue(path, out var existing))
            {
                if (!ItemEquals(existing, item))
                    updatedItems.Add(item);
            }
            else
            {
                addedItems.Add(item);
            }
        }
        foreach (var (path, item) in currentItems)
        {
            if (!newItems.ContainsKey(path))
                removedItems.Add(item);
        }

        currentItems.Clear();
        foreach (var (p, v) in newItems) currentItems[p] = v;

        void Emit(QueryChangeType type, IReadOnlyList<T> items)
        {
            if (items.Count == 0) return;
            observer.OnNext(new QueryResultChange<T>
            {
                ChangeType = type,
                Items = items,
                Query = parsedQuery,
                Version = Interlocked.Increment(ref _version),
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
        Emit(QueryChangeType.Added, addedItems);
        Emit(QueryChangeType.Updated, updatedItems);
        Emit(QueryChangeType.Removed, removedItems);
    }

    /// <summary>
    /// Compares two items for equality in the context of DistinctUntilChanged.
    /// For MeshNode, strips non-serializable fields (HubConfiguration, GlobalServiceConfigurations)
    /// before comparison to avoid false negatives from Func&lt;&gt; reference inequality.
    /// </summary>
    private static bool ItemEquals<T>(T a, T b)
    {
        if (a is MeshNode nodeA && b is MeshNode nodeB)
        {
            // Compare content-relevant fields only, stripping volatile/non-serializable fields
            return nodeA with { HubConfiguration = null, GlobalServiceConfigurations = [], LastModified = default, Version = 0 }
                == nodeB with { HubConfiguration = null, GlobalServiceConfigurations = [], LastModified = default, Version = 0 };
        }
        return Equals(a, b);
    }

    /// <summary>
    /// Checks whether a node should be excluded based on context.
    /// Checks both type-level exclusion (from MeshConfiguration) and node-level exclusion.
    /// </summary>
    private bool IsExcludedByContext(MeshNode node, string? context)
    {
        if (context == null) return false;
        if (meshConfiguration?.IsExcludedFromContext(node.NodeType, context) == true)
            return true;
        if (node.ExcludeFromContext?.Contains(context) == true)
            return true;
        return false;
    }

    /// <summary>
    /// Checks whether a node should be excluded by the is:main filter.
    /// Excludes satellite nodes (MainNode != null and MainNode != Path).
    /// </summary>
    private static bool IsExcludedByIsMain(MeshNode node, ParsedQuery query)
    {
        if (query.IsMain != true) return false;
        return node.MainNode != node.Path;
    }

    /// <summary>
    /// Checks if a path is within a satellite partition (contains /_X/ segments
    /// where X starts with uppercase, e.g., /_Comment/, /_Thread/).
    /// When the base path is within a satellite partition, descendant queries
    /// should include satellite nodes (use GetAllDescendantsAsync).
    /// </summary>
    private static bool IsSatellitePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var idx = 0;
        while ((idx = path.IndexOf("/_", idx, StringComparison.Ordinal)) >= 0)
        {
            idx += 2; // skip "/_"
            if (idx < path.Length && char.IsUpper(path[idx]))
                return true;
        }
        return false;
    }
}
