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
/// Pedestrian, per-adapter implementation of <see cref="IMeshQueryProvider"/> +
/// <see cref="IMeshQueryCore"/>. One instance per <see cref="IStorageAdapter"/>;
/// constructed by <c>RoutingPersistenceServiceCore</c> per partition.
/// <para>
/// Scope walks (<c>Children / Descendants / Subtree / Hierarchy / AncestorsAndSelf</c>)
/// compose against <see cref="IStorageAdapter.ListChildPaths"/> in IObservable form —
/// the right shape for in-memory, file-system, and embedded-resource adapters where
/// no native pushdown exists. SQL-backed backends register their own
/// <see cref="IMeshQueryProvider"/> (e.g. <c>PostgreSqlMeshQuery</c>,
/// <c>CosmosMeshQuery</c>) that pushes the scope clause to the database.
/// </para>
/// <para>
/// Strictly per-adapter: never walks partition keys, never holds a static-node
/// catalog, never coordinates across providers. Those concerns belong to
/// <c>RoutingMeshQueryProvider</c>, <c>StaticNodeQueryProvider</c>, and
/// <c>MeshQuery</c> respectively.
/// </para>
/// </summary>
internal class StorageAdapterMeshQueryProvider : IMeshQueryProvider, IMeshQueryCore
{
    private readonly IStorageAdapter persistence;
    private readonly AccessService? accessService;
    private readonly IDataChangeNotifier? changeNotifier;
    private readonly MeshConfiguration? meshConfiguration;
    // 🚨 Lazy<INodeValidator> — NOT bare INodeValidator. RlsNodeValidator
    // (the only non-test impl) takes ISecurityService at construction time.
    // SecurityService warms up by subscribing to a synced query during its
    // own ctor → reaches StorageAdapterMeshQueryProvider → resolving
    // INodeValidators eagerly would re-enter SecurityService and cycle.
    // Lazy<T> defers each validator's construction to first ValidateReadAsync
    // access; by then SecurityService is fully built.
    private readonly IEnumerable<Lazy<INodeValidator>>? nodeValidators;
    private readonly ILogger<StorageAdapterMeshQueryProvider>? logger;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();
    private long _version;

    /// <summary>
    /// First-segment namespaces owned by static partitions (Agent, Model, …).
    /// This provider excludes them from its <see cref="Matches"/> predicate so the
    /// aggregator routes those queries to <see cref="StaticNodeQueryProvider"/>
    /// only — Postgres / in-memory persistence never round-trips for built-in
    /// content.
    /// </summary>
    private readonly HashSet<string> _excludedNamespaces;

    public StorageAdapterMeshQueryProvider(
        IStorageAdapter persistence,
        // 🚨 NO ISecurityService here — that parameter created the Autofac
        // cycle SecurityService → SyncedQueryMeshNodes → IMeshQueryCore →
        // StorageAdapterMeshQueryProvider → SecurityService. Per-node read
        // filtering on the secured IMeshQueryProvider surface goes through
        // INodeValidator instead, which has no back-reference into the
        // synced-query path. GetEffectivePermissions-style filtering for
        // non-MeshNode results is intentionally dropped — IMeshService.QueryAsync
        // results are MeshNodes today, and any future non-MeshNode projection
        // should declare an INodeValidator if it needs gating.
        AccessService? accessService = null,
        IDataChangeNotifier? changeNotifier = null,
        MeshConfiguration? meshConfiguration = null,
        IEnumerable<Lazy<INodeValidator>>? nodeValidators = null,
        IEnumerable<IPartitionStorageProvider>? partitionProviders = null,
        ILogger<StorageAdapterMeshQueryProvider>? logger = null)
    {
        this.persistence = persistence;
        this.accessService = accessService;
        this.changeNotifier = changeNotifier;
        this.meshConfiguration = meshConfiguration;
        this.nodeValidators = nodeValidators;
        this.logger = logger;
        // Exclusion derived from IPartitionStorageProvider — the partition
        // registry. Only static-source partitions (DataSource="static") count
        // as "owned by someone else" for the Matches predicate. AddMeshNodes
        // seeds (writable runtime namespaces surfaced via
        // MeshConfigurationStaticNodeProvider) are NOT in this list — they
        // remain queryable through this provider.
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

    private async IAsyncEnumerable<object> QueryAsync(
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

        // Yield with a "Skip + Limit" load buffer. The engine is one bucket in
        // the MeshQuery.MergeProviderObservables fan-out — request.Skip /
        // request.Limit are applied POST-MERGE in ClipMergedInitial.
        // Applying request.Skip here too would double-skip (engine skips N,
        // then merge skips another N → empty page 2). What we DO cap here is
        // the load: yield at most (Skip + Limit) items so a deep walk over a
        // 10 000-row subtree doesn't materialise everything when the caller
        // only wants 3 items at offset 0. parsedQuery.Limit (the explicit
        // `limit:N` in the query string) is still honoured per-query — it's
        // a hint to each provider, not the cross-provider cap.
        var loadCap = LoadCap(request, parsedQuery);
        int yielded = 0;
        foreach (var node in sorted)
        {
            yield return parsedQuery.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                : node;

            yielded++;
            if (loadCap.HasValue && loadCap.Value > 0 && yielded >= loadCap.Value)
                yield break;
        }
    }

    private static int? LoadCap(MeshQueryRequest request, ParsedQuery parsedQuery)
    {
        // Engine load cap = (Skip ?? 0) + (request.Limit ?? parsedQuery.Limit).
        // parsedQuery.Limit is the per-query limit:N hint and is still honoured
        // separately if no request-level limit was set.
        var skip = request.Skip ?? 0;
        int? limit = request.Limit ?? parsedQuery.Limit;
        return limit is int l ? skip + l : null;
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

        // See QueryAsync for the rationale: request.Skip is applied post-merge
        // in ClipMergedInitial; the engine loads up to (Skip + Limit) items
        // and yields without an in-engine skip to avoid double-skipping.
        var loadCap = LoadCap(request, parsedQuery);
        int yielded = 0;
        foreach (var node in sorted)
        {
            yield return parsedQuery.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                : node;

            yielded++;
            if (loadCap.HasValue && loadCap.Value > 0 && yielded >= loadCap.Value)
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

            // Per-query reactive composition: FindMatchingNodes emits objects;
            // we filter by validator (when useSecurityFilter), dedup, and fold
            // into the shared closures. ForEachAsync awaits source completion
            // and is the single Task bridge per query — everything inside the
            // observable chain stays IObservable.
            await FindMatchingNodes(
                    parsedQuery, effectiveScope, basePath, userId, context, request, options)
                .SelectMany(node =>
                {
                    if (node is MeshNode meshNode)
                    {
                        if (!string.IsNullOrEmpty(meshNode.Path) && matchedByPath.ContainsKey(meshNode.Path))
                            return Observable.Empty<object>();
                        return useSecurityFilter
                            ? ValidateRead(meshNode, userId)
                                .Where(valid => valid)
                                .Select(_ => (object)meshNode)
                            : Observable.Return<object>(meshNode);
                    }
                    return Observable.Return(node);
                })
                .ForEachAsync(n =>
                {
                    if (n is MeshNode mn)
                    {
                        if (!string.IsNullOrEmpty(mn.Path))
                            matchedByPath[mn.Path] = mn;
                        else if (seenRefs.Add(mn))
                            nonNodeMatched.Add(mn);
                    }
                    else if (seenRefs.Add(n))
                    {
                        nonNodeMatched.Add(n);
                    }
                }, ct);
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
    /// <summary>
    /// Pure-IObservable per-query node finder — no <c>async</c>, no <c>await</c>,
    /// no <c>IAsyncEnumerable</c> bridge inside. The three sub-pipelines
    /// (activity-source / exact-path probes / scope-walk) compose into a single
    /// observable; the caller (<see cref="CollectMatchedAsync"/>) consumes
    /// reactively via <c>SelectMany</c>.
    /// </summary>
    private IObservable<object> FindMatchingNodes(
        ParsedQuery parsedQuery,
        QueryScope effectiveScope,
        string basePath,
        string userId,
        string? context,
        MeshQueryRequest request,
        JsonSerializerOptions options)
    {
        // source:activity is a join with the `_activity` satellites — pushed
        // down to SQL by PostgreSqlSqlGenerator (INNER JOIN activities ON
        // act.main_node = n.path). For pedestrian adapters the equivalent
        // "join" is in the path itself: any activity satellite lives at
        // `{mainPath}/_activity/{actId}`, so we derive the MainNode by string
        // trim and skip the satellite read entirely. 1 walk + 1 read per
        // distinct main — same cost shape as Postgres' JOIN.
        //
        // source:activity is exclusive — bypass the normal walk/exact-probe.
        if (parsedQuery.Source == QuerySource.Activity)
        {
            // Skip the walk-derive-MainNode-then-Read pattern when the adapter's
            // own provider already handles source:activity via a satellite JOIN
            // (PostgreSqlMeshQuery does this in one round-trip). Otherwise the
            // pedestrian walk is N+1 duplicate work running in parallel.
            if (persistence is IScopedQueryStorageAdapter)
                return Observable.Empty<object>();

            const string activitySegment = "/_activity/";
            var seenMains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return WalkAdapter(basePath, QueryScope.Subtree)
                .Select(path =>
                {
                    if (string.IsNullOrEmpty(path)) return null;
                    var idx = path.IndexOf(activitySegment, StringComparison.OrdinalIgnoreCase);
                    return idx > 0 ? path.Substring(0, idx) : null;
                })
                .Where(mainPath => mainPath != null && seenMains.Add(mainPath))
                .SelectMany(mainPath => persistence.Read(mainPath!, options)
                    .Take(1)
                    .Catch<MeshNode?, Exception>(ex =>
                    {
                        // Surface as warning so silent swallow-and-return-null
                        // doesn't make TimeoutException disappear into "node
                        // dropped by Where(!= null)" — visible cause when a
                        // query mysteriously returns fewer rows than expected.
                        logger?.LogWarning(ex,
                            "[SourceActivity.ReadMain] swallowed for path={Path}; returning null",
                            mainPath);
                        return Observable.Return<MeshNode?>(null);
                    }))
                .Where(node => node != null)
                .Select(node => node!)
                .Where(node => _evaluator.Matches(node, parsedQuery)
                    && !IsExcludedByContext(node, context)
                    && !IsExcludedByIsMain(node, parsedQuery))
                .Catch<MeshNode, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "[StorageAdapterMeshQueryProvider.SourceActivity] pipeline threw query=[{Query}] basePath={BasePath}",
                        string.Join(" | ", request.EffectiveQueries), basePath);
                    return Observable.Empty<MeshNode>();
                })
                .Cast<object>();
        }

        // Multi-value `path:a|b|c` — parsedQuery.Paths carries the full IN list.
        // For the path-resolution idiom (`path:a|b|c sort:length(path)-desc limit:1`)
        // this is "probe every candidate ancestor and take the deepest hit", so
        // exact probes across the whole list are exactly what we want — no scope
        // walk on top.
        var pathsToSearch = parsedQuery.Paths is { Count: > 1 } multi
            ? multi.ToList()
            : GetPathsForScope(basePath, effectiveScope);
        var emittedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Exact-path probes — batched via IStorageAdapter.ReadMany so the
        // multi-value `path:a|b|c` URL-resolver query (and any other multi-path
        // probe) collapses to ONE round-trip on backends that support it
        // (Postgres: WHERE namespace = $1 AND id IN (…)). FileSystem / InMemory
        // fall back to the default Merge of N Reads — fine, no per-call
        // latency to amortise.
        var nonEmptyPaths = pathsToSearch
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        var exactPathNodes = (nonEmptyPaths.Count == 0
                ? Observable.Empty<MeshNode>()
                : Observable.Defer(() =>
                {
                    try { return persistence.ReadMany(nonEmptyPaths, options); }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex,
                            "[StorageAdapterMeshQueryProvider.ExactRead] ReadMany threw synchronously paths=[{Paths}]",
                            string.Join(",", nonEmptyPaths));
                        return Observable.Empty<MeshNode>();
                    }
                }))
            .Where(node => _evaluator.Matches(node, parsedQuery)
                && !IsExcludedByContext(node, context)
                && !IsExcludedByIsMain(node, parsedQuery))
            .Do(node => { if (!string.IsNullOrEmpty(node.Path)) emittedPaths.Add(node.Path); })
            .Catch<MeshNode, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[StorageAdapterMeshQueryProvider.ExactScope] pipeline threw query=[{Query}] basePath={BasePath}",
                    string.Join(" | ", request.EffectiveQueries), basePath);
                return Observable.Empty<MeshNode>();
            });

        // Children / Descendants / Hierarchy / Subtree / AncestorsAndSelf scopes —
        // walk via the adapter. Each scope expands to a list of (root, scope)
        // pairs the walker iterates over.
        //  AncestorsAndSelf → Children of each ancestor (one level each).
        //  Hierarchy       → Subtree from self + Children of each ancestor
        //                    (siblings of self and uncles, but not entire
        //                    sibling subtrees — that's the "hierarchy" semantic).
        //  Children / Descendants / Subtree → just basePath, with the scope as-is.
        if (effectiveScope is not (QueryScope.Children
            or QueryScope.Descendants
            or QueryScope.Hierarchy
            or QueryScope.Subtree
            or QueryScope.AncestorsAndSelf))
        {
            return exactPathNodes.Cast<object>();
        }

        // 🚨 When the adapter answers scoped queries with one round-trip
        // (PostgreSqlMeshQuery / CosmosMeshQuery), our ListChildPaths-walk +
        // per-path Read is pure N+1 duplicate work running in parallel with the
        // optimized provider. Skip the walk entirely — the dedicated
        // IMeshQueryProvider for that backend already contributes the rows.
        // The exact-path probes (above) still run for Ancestors/AncestorsAndSelf/
        // multi-path cases that GetPathsForScope populated.
        if (persistence is IScopedQueryStorageAdapter)
        {
            return exactPathNodes.Cast<object>();
        }

        var walkPairs = new List<(string Root, QueryScope Scope)>();
        if (effectiveScope == QueryScope.AncestorsAndSelf)
        {
            foreach (var ancestor in GetPathsForScope(basePath, QueryScope.AncestorsAndSelf))
                walkPairs.Add((ancestor, QueryScope.Children));
        }
        else if (effectiveScope == QueryScope.Hierarchy)
        {
            // descendants of self
            walkPairs.Add((basePath, QueryScope.Subtree));
            // children of each strict ancestor (skip self — already covered)
            foreach (var ancestor in GetPathsForScope(basePath, QueryScope.Ancestors))
                walkPairs.Add((ancestor, QueryScope.Children));
        }
        else
        {
            walkPairs.Add((basePath, effectiveScope));
        }

        var matchedScopeNodes = walkPairs
            .ToObservable()
            .SelectMany(pair => WalkAdapter(pair.Root, pair.Scope))
            .Where(path => !string.IsNullOrEmpty(path))
            .Where(path => emittedPaths.Add(path))
            .SelectMany(path =>
                persistence.Read(path, options)
                    .Take(1)
                    .Catch<MeshNode?, Exception>(ex =>
                    {
                        // Surface swallowed read errors at warning level so a
                        // timeout / RLS-deny / corrupt-row doesn't silently
                        // drop the node out of the result set.
                        logger?.LogWarning(ex,
                            "[MatchScope.Read] swallowed for path={Path}; returning null",
                            path);
                        return Observable.Return<MeshNode?>(null);
                    }))
            .Where(node => node != null)
            .Select(node => node!)
            .Where(node => _evaluator.Matches(node, parsedQuery)
                && !IsExcludedByContext(node, context)
                && !IsExcludedByIsMain(node, parsedQuery))
            .Catch<MeshNode, Exception>(ex =>
            {
                logger?.LogWarning(ex,
                    "[StorageAdapterMeshQueryProvider.MatchScope] pipeline threw query=[{Query}] basePath={BasePath}",
                    string.Join(" | ", request.EffectiveQueries), basePath);
                return Observable.Empty<MeshNode>();
            });

        // Sequential composition: exact-path probes complete first (populating
        // emittedPaths), then scope-walk filters via the shared HashSet. The
        // Concat operator guarantees the second observable doesn't subscribe
        // until the first completes — same ordering as the original two
        // sequential await-foreach loops.
        return Observable.Concat(exactPathNodes, matchedScopeNodes).Cast<object>();
    }

    /// <summary>
    /// Pure-IObservable BFS walk through <see cref="IStorageAdapter.ListChildPaths"/>.
    /// Children scope = one level; Descendants/Subtree/Hierarchy = recursive.
    /// Composes via <c>SelectMany</c>; no <c>await</c>, no <c>.ToTask()</c> —
    /// runs end-to-end reactively (per AsynchronousCalls.md).
    /// </summary>
    private IObservable<string> WalkAdapter(string basePath, QueryScope scope)
    {
        var recursive = scope != QueryScope.Children;
        return WalkLevel(string.IsNullOrEmpty(basePath) ? null : basePath, recursive);
    }

    private IObservable<string> WalkLevel(string? parent, bool recursive)
        => Observable.Defer(() =>
            {
                try
                {
                    return persistence.ListChildPaths(parent);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex,
                        "[StorageAdapterMeshQueryProvider.WalkLevel] ListChildPaths threw parent={Parent}", parent);
                    return Observable.Empty<(IEnumerable<string>, IEnumerable<string>)>();
                }
            })
            .Take(1)
            .Do(level => logger?.LogDebug(
                "[StorageAdapterMeshQueryProvider.WalkLevel] parent={Parent} recursive={Recursive} nodes=[{Nodes}] dirs=[{Dirs}]",
                parent ?? "(null)", recursive,
                string.Join(",", level.Item1 ?? Enumerable.Empty<string>()),
                string.Join(",", level.Item2 ?? Enumerable.Empty<string>())))
            .SelectMany(level =>
            {
                var nodePaths = (level.Item1 ?? Enumerable.Empty<string>()).ToObservable();
                if (!recursive)
                    return nodePaths;
                var nodesAndDeeper = nodePaths.SelectMany(p =>
                    Observable.Return(p).Concat(WalkLevel(p, recursive: true)));
                var dirs = (level.Item2 ?? Enumerable.Empty<string>()).ToObservable()
                    .SelectMany(d => WalkLevel(d, recursive: true));
                return nodesAndDeeper.Concat(dirs);
            })
            .Catch<string, Exception>(ex =>
            {
                logger?.LogWarning(ex, "[StorageAdapterMeshQueryProvider.WalkLevel] parent={Parent} failed", parent);
                return Observable.Empty<string>();
            });

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
    /// Pure-reactive — no await, no FirstAsync().ToTask() bridge. Each validator's
    /// <see cref="INodeValidator.Validate"/> returns IObservable&lt;NodeValidationResult&gt;
    /// already; we sequence them via Concat and short-circuit via .All, which
    /// disposes the upstream subscription on the first false (so subsequent
    /// validators in the Concat queue never subscribe — same semantics as the
    /// old loop's early <c>return false</c>).
    /// </summary>
    private IObservable<bool> ValidateRead(MeshNode node, string userId)
    {
        if (nodeValidators == null)
            return Observable.Return(true);

        // Always use the resolved userId as the validation context. The
        // upstream GetEffectiveUserId has already produced the right value
        // (request.UserId if explicit — including "" → Anonymous —, otherwise
        // ambient AccessContext, defaulting to Anonymous). Building the
        // AccessContext from userId means an explicit anonymous probe
        // (request.UserId = "") gets validated as Anonymous, NOT silently
        // upgraded to the test/circuit's admin context.
        //
        // BUG fixed 2026-05-22: the previous branch fell back to the
        // ambient AccessService.Context when userId == Anonymous, which
        // caused MeshQuery_AnonymousUser_FiltersRestrictedNodes to validate
        // against Roland (the test's DevLogin admin) and see Private nodes
        // an actual anonymous caller could not.
        var accessContext = new AccessContext { ObjectId = userId };

        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Read,
            Node = node,
            AccessContext = accessContext
        };

        // Resolve lazily — see ctor comment on `nodeValidators` for the
        // SecurityService cycle this defers. Materialise the relevant set
        // synchronously (cheap — typically 0–3 validators per partition);
        // each .Validate(...) call stays observable.
        var relevant = nodeValidators
            .Select(lv => lv.Value)
            .Where(v => v.SupportedOperations.Count == 0
                     || v.SupportedOperations.Contains(NodeOperation.Read))
            .ToList();

        if (relevant.Count == 0)
            return Observable.Return(true);

        return relevant
            .Select(v => v.Validate(context).Take(1).Do(r =>
            {
                if (!r.IsValid)
                    logger?.LogDebug("Validator {Validator} rejected read on node {Path}: {Error}",
                        v.GetType().Name, node.Path, r.ErrorMessage);
            }))
            .ToObservable()
            .Concat()
            .All(r => r.IsValid);
    }

    /// <inheritdoc />
    public IObservable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10)
        => AutocompleteImpl(basePath, prefix, options, null, AutocompleteMode.PathFirst, limit, null, null);

    /// <inheritdoc />
    public IObservable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
        => AutocompleteImpl(basePath, prefix, options, null, mode, limit, contextPath, context);

    /// <summary>
    /// Autocomplete with user ID for access control filtering. Observable-first.
    /// </summary>
    public IObservable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        string? userId,
        AutocompleteMode mode,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
        => AutocompleteImpl(basePath, prefix, options, userId, mode, limit, contextPath, context);

    private IObservable<QuerySuggestion> AutocompleteImpl(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        string? userId,
        AutocompleteMode mode,
        int limit,
        string? contextPath,
        string? context)
    {
        var normalizedPath = NormalizePath(basePath);
        var normalizedPrefix = prefix ?? "";

        var queryString = string.IsNullOrEmpty(normalizedPath)
            ? "scope:subtree"
            : $"path:{normalizedPath} scope:subtree";

        var queryRequest = new MeshQueryRequest
        {
            Query = queryString,
            Context = context,
            ContextPath = contextPath,
            UserId = userId,
            Limit = Math.Max(limit * 5, 100),
        };

        // 🚨 Pure-IObservable pipeline. QueryCoreAsync is still IAsyncEnumerable
        // internally; bridge it via ToObservableSequence at this boundary (the
        // existing helper used elsewhere in the codebase). Each emitted MeshNode
        // is filtered + scored, accumulated into a list, sorted, clipped, then
        // re-emitted as OnNext events. No await, no Task.Run.
        return QueryCoreAsync(queryRequest, options).ToObservableSequence()
            .Select(obj => obj as MeshNode)
            .Where(node => node is not null
                && meshConfiguration?.AutocompleteExcludedNodeTypes.Contains(node.NodeType ?? "") != true
                && (context == null || !IsExcludedByContext(node, context)))
            .Select(node => ScoreOne(node!, normalizedPrefix, contextPath))
            .Where(s => s.Score > 0)
            .ToList()
            .SelectMany(list => OrderForMode(list, mode).Take(limit));
    }

    private static QuerySuggestion ScoreOne(MeshNode node, string normalizedPrefix, string? contextPath)
    {
        var name = node.Name ?? node.Id ?? node.Path ?? "";
        var path = node.Path ?? "";
        double score = 0;

        if (string.IsNullOrEmpty(normalizedPrefix))
        {
            // Empty prefix: rank purely by depth so parents come before descendants.
            score = 100 - (path.Count(c => c == '/') * 10);
        }
        else if (name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            score = 100 - (name.Length - normalizedPrefix.Length);
        }
        else if (name.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            score = 50 - (name.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase));
        }
        else if (path.Contains(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            score = 30 - (path.IndexOf(normalizedPrefix, StringComparison.OrdinalIgnoreCase) * 0.1);
        }
        else if (FuzzyMatch(name, normalizedPrefix))
        {
            score = 25;
        }

        if (!string.IsNullOrEmpty(normalizedPrefix))
            score -= path.Count(c => c == '/');

        score += PathProximity.ComputeBoost(contextPath, node.Path);

        return new QuerySuggestion(node.Path ?? "", name, node.NodeType, score, node.Icon);
    }

    private static IEnumerable<QuerySuggestion> OrderForMode(IList<QuerySuggestion> suggestions, AutocompleteMode mode) => mode switch
    {
        AutocompleteMode.PathFirst => suggestions
            .OrderBy(s => s.Path.Length)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Name),
        AutocompleteMode.RelevanceFirst => suggestions
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Path.Length)
            .ThenBy(s => s.Name),
        _ => suggestions
            .OrderBy(s => s.Path.Length)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Name),
    };

    /// <summary>
    /// Loose-but-bounded fuzzy match: every character of <paramref name="prefix"/>
    /// must appear in <paramref name="text"/> in order AND within a window of
    /// <c>max(prefix.Length + 2, prefix.Length * 2)</c> characters of the first
    /// matched position. Bounding the window prevents "Sys" from matching
    /// "AsiaRe Profitability Analysis" (which legitimately contains s-y-s in
    /// order across 30 characters spanning unrelated words — caller's filter
    /// then can't reject it, repro:
    /// <c>UnifiedReferenceAutocompleteProviderTest.Provider_AtPartialPrefix_OnlyReturnsMatchingNodes</c>).
    /// The window keeps deliberate intra-word typo-tolerance ("Systmorph" → "Systemorph",
    /// "AmricansIns" → "AmericasIns") while rejecting scattered-letter coincidences.
    /// </summary>
    private static bool FuzzyMatch(string text, string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return true;

        // Minimum 3-char prefix to enable fuzzy — single-char "fuzzy" matches
        // anything and is just noise.
        if (prefix.Length < 3)
            return false;

        var window = Math.Max(prefix.Length + 2, prefix.Length * 2);
        int prefixIndex = 0;
        int firstMatchIndex = -1;
        for (int i = 0; i < text.Length; i++)
        {
            // Substring compare with explicit OrdinalIgnoreCase instead of
            // lowercasing both sides — canonical case-insensitive comparison
            // on 1-char windows, no string allocations.
            if (string.Compare(text, i, prefix, prefixIndex, 1,
                    StringComparison.OrdinalIgnoreCase) != 0)
                continue;
            if (firstMatchIndex < 0)
                firstMatchIndex = i;
            else if (i - firstMatchIndex >= window)
                return false; // window blown — not a real match
            prefixIndex++;
            if (prefixIndex >= prefix.Length)
                return true;
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

            // Pure IObservable shape — no Task.Factory.StartNew. The async
            // IAsyncEnumerable is wrapped via Observable.FromAsync, then pushed
            // to TaskPoolScheduler.Default via SubscribeOn so no inherited
            // TaskScheduler (Orleans grain, ASP.NET) is captured by the async
            // state machine on Subscribe.
            IObservable<List<(string? Path, T Item)>> RunQuery(CancellationToken ct) =>
                Observable.FromAsync(async cancel =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancel);
                        var results = new List<(string?, T)>();
                        await foreach (var item in QueryStream(linked.Token))
                        {
                            if (item is T typed)
                                results.Add((GetItemPath(item), typed));
                        }
                        return results;
                    })
                    .SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default);

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

}
