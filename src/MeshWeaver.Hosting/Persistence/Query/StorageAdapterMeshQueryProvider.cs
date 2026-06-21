using System.Reactive.Disposables;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
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
    private readonly MeshConfiguration? meshConfiguration;
    // 🚨 Lazy<INodeValidator> — NOT bare INodeValidator. RlsNodeValidator
    // (the only non-test impl) takes SecurityService at construction time.
    // SecurityService warms up by subscribing to a synced query during its
    // own ctor → reaches StorageAdapterMeshQueryProvider → resolving
    // INodeValidators eagerly would re-enter SecurityService and cycle.
    // Lazy<T> defers each validator's construction to first ValidateReadAsync
    // access; by then SecurityService is fully built.
    private readonly IEnumerable<Lazy<INodeValidator>>? nodeValidators;
    private readonly ILogger<StorageAdapterMeshQueryProvider>? logger;
    private readonly QueryParser _parser = new();
    private readonly QueryEvaluator _evaluator = new();
    // #20: when set (partitioned Postgres), defer UNSCOPED queries to the native
    // fan-out provider (PostgreSqlPartitionedMeshQuery), removing the pedestrian's
    // slow cross-partition walk from those merges. See StorageAdapterQueryProviderOptions.
    private readonly bool _deferToNative;
    // The query scope-walk leaf (ListChildPaths / Read over the storage adapter)
    // is bridged to IObservable through this pool — never via a bare
    // Observable.FromAsync, which deadlocks under a blocking subscriber. See
    // IoPoolExtensions and Doc/Architecture/AsynchronousCalls.md.
    private readonly IIoPool _ioPool;
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
        // 🚨 NO SecurityService here — that parameter created the Autofac
        // cycle SecurityService → SyncedQueryMeshNodes → IMeshQueryCore →
        // StorageAdapterMeshQueryProvider → SecurityService. Per-node read
        // filtering on the secured IMeshQueryProvider surface goes through
        // INodeValidator instead, which has no back-reference into the
        // synced-query path. GetEffectivePermissions-style filtering for
        // non-MeshNode results is intentionally dropped — IMeshService.QueryAsync
        // results are MeshNodes today, and any future non-MeshNode projection
        // should declare an INodeValidator if it needs gating.
        AccessService? accessService = null,
        MeshConfiguration? meshConfiguration = null,
        IEnumerable<Lazy<INodeValidator>>? nodeValidators = null,
        IEnumerable<IPartitionStorageProvider>? partitionProviders = null,
        ILogger<StorageAdapterMeshQueryProvider>? logger = null,
        IoPoolRegistry? ioPoolRegistry = null,
        StorageAdapterQueryProviderOptions? options = null)
    {
        this.persistence = persistence;
        this.accessService = accessService;
        this.meshConfiguration = meshConfiguration;
        this.nodeValidators = nodeValidators;
        this.logger = logger;
        _deferToNative = options?.DeferToNativeProvider ?? false;
        // FileSystem pool cap governs the scope-walk concurrency; Unbounded is the
        // DI-less fallback (still offloads to the ThreadPool with ConfigureAwait).
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
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
    /// #20: in <see cref="StorageAdapterQueryProviderOptions.DeferToNativeProvider"/> mode,
    /// true when the native <c>PostgreSqlPartitionedMeshQuery</c> owns this query and the
    /// pedestrian should contribute nothing — removing its <c>ListChildPaths</c> scope-walk
    /// (the storm fix) for those shapes: <b>unscoped / wildcard-first-segment</b> (native
    /// cross-schema fan-out) and <b>scoped primary (<c>mesh_nodes</c>)</b> reads (native
    /// per-schema delegate, live). Returns <see langword="false"/> ONLY for scoped SATELLITE
    /// reads (a <c>_</c>-prefixed path segment, a satellite nodeType, or
    /// <c>source:activity</c>/<c>accessed</c>) — the pedestrian stays their live server until
    /// the delegate's satellite Query Initial is fixed.
    /// </summary>
    private bool DefersToNativeProvider(MeshQueryRequest request)
    {
        var parsed = _parser.Parse(request.EffectiveQueries.FirstOrDefault());
        var path = parsed.Path;
        // Unscoped / wildcard-first-segment → the native PostgreSqlPartitionedMeshQuery fans
        // out across partitions; defer.
        if (string.IsNullOrEmpty(path)) return true;
        var slash = path.IndexOf('/');
        var first = slash < 0 ? path : path[..slash];
        if (first.Length == 0 || first == "*") return true;
        // Scoped. Keep serving scoped SATELLITE reads here — the native provider's per-schema
        // delegate doesn't yet serve satellite Query Initial correctly (it under-returns
        // pre-existing rows), and source:activity/accessed are cross-partition JOINs. A
        // satellite query is one whose path carries a `_`-prefixed segment, whose nodeType maps
        // to a satellite table, or which is an activity/accessed source.
        if (parsed.Source is QuerySource.Activity or QuerySource.Accessed) return false;
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (seg.StartsWith('_')) return false;
        var nodeType = parsed.ExtractNodeType();
        if (!string.IsNullOrEmpty(nodeType) && PartitionDefinition.IsSatelliteNodeType(nodeType))
            return false;
        // Scoped PRIMARY (mesh_nodes) read → the per-schema delegate now serves it live; defer
        // so the pedestrian's ListChildPaths scope-walk is removed for this shape (the storm fix).
        return true;
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

    /// <summary>
    /// Pure-IObservable query: composes <see cref="FindMatchingNodes"/> across every
    /// effective query, unions hits by path, sorts, projects, and caps — emitting the
    /// full matched set as a single <see cref="IReadOnlyList{T}"/>. No <c>async</c>, no
    /// <c>IAsyncEnumerable</c>, no <c>await foreach</c>: the I/O leaves
    /// (<see cref="IStorageAdapter.ListChildPaths"/> / <c>Read</c>) are already pooled
    /// IObservables at their own boundary; this layer only composes them. Replaces the
    /// former <c>QueryAsync</c>/<c>QueryCoreAsync</c> async-enumerable pair —
    /// <paramref name="useSecurityFilter"/> selects RLS-filtered (IMeshQueryProvider)
    /// vs raw (IMeshQueryCore) reads.
    /// <para>
    /// The <c>#20</c> defer-to-native gate lives in the public callers
    /// (<see cref="ObserveQueryInternal{T}"/>); <see cref="Autocomplete"/> intentionally
    /// never deferred (a scoped storage adapter short-circuits the walk itself), so this
    /// layer applies no defer of its own — behaviour identical to the old pair.
    /// </para>
    /// </summary>
    private IObservable<IReadOnlyList<object>> RunQueryNodes(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        bool useSecurityFilter)
        => CollectMatched(request, options, useSecurityFilter)
            .Select(collected =>
            {
                var (matched, parsedQuery, _) = collected;

                // Match PG's table separation: a NON-satellite content query must not return
                // satellite-path nodes (_Access grants, _Thread, _Comment, …). On PG these live in
                // separate per-prefix tables so a mesh_nodes content query never sees them; the
                // in-memory adapter keeps everything in one store, so e.g. an auto-created
                // {partition}/_Access/{creator}_Access grant leaked into `scope:descendants`.
                // Explicit satellite queries (a _-segment path, a satellite nodeType, or
                // source:activity/accessed) are unaffected — they still return their satellites.
                IEnumerable<object> matchedNodes = matched;
                if (!IsSatelliteTargetedQuery(parsedQuery))
                    matchedNodes = matchedNodes.Where(n => n is not MeshNode mn || !IsSatellitePath(mn.Path));

                // Partition roots (the auto-provisioned Space node at namespace="") are STRUCTURAL
                // partition containers, not content — and abac5dec2 stamps them with a current
                // LastModified, so they would otherwise dominate broad recency/listing sweeps
                // (is:main scope:descendants sort:LastModified-desc). Drop them from BROAD (non-Exact)
                // scope queries. An EXACT path read (path:Globex) still returns the node at that path
                // — a Space you asked for by exact path is yours to read — as does an explicit
                // nodeType:Space query.
                if (parsedQuery.Scope != QueryScope.Exact && !QueryTargetsPartitionRoot(parsedQuery))
                    matchedNodes = matchedNodes.Where(n => n is not MeshNode mn || !IsPartitionRoot(mn));

                IEnumerable<object> sorted = matchedNodes;
                if (parsedQuery.OrderBy != null)
                {
                    sorted = parsedQuery.OrderBy.Descending
                        ? matchedNodes.OrderByDescending(n => GetSortableValue(n, parsedQuery.OrderBy.Property))
                        : matchedNodes.OrderBy(n => GetSortableValue(n, parsedQuery.OrderBy.Property));
                }

                IEnumerable<object> projected = sorted.Select(node =>
                    parsedQuery.Select != null
                        ? ParsedQuery.ProjectToSelect(node, parsedQuery.Select)
                        : node);

                // Load cap = (Skip ?? 0) + Limit. request.Skip itself is applied
                // POST-MERGE in ClipMergedInitial (applying it here too would
                // double-skip); we only bound the load so a deep walk over a
                // 10 000-row subtree doesn't materialise everything for a 3-item page.
                var loadCap = LoadCap(request, parsedQuery);
                if (loadCap is int cap && cap > 0)
                    projected = projected.Take(cap);

                return (IReadOnlyList<object>)projected.ToList();
            });

    /// <summary>
    /// True when the query explicitly TARGETS satellite nodes — a <c>_</c>-prefixed path segment,
    /// a satellite nodeType, or <c>source:activity</c>/<c>accessed</c>. Such queries keep their
    /// satellites; every other (content) query has satellite-path rows filtered out so the
    /// in-memory adapter matches PG's separate-table behaviour. Mirrors the satellite detection in
    /// <see cref="DefersToNativeProvider"/>.
    /// </summary>
    private static bool IsSatelliteTargetedQuery(ParsedQuery parsed)
    {
        if (parsed.Source is QuerySource.Activity or QuerySource.Accessed) return true;
        var path = parsed.Path;
        if (!string.IsNullOrEmpty(path))
            foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (seg.StartsWith('_')) return true;
        var nodeType = parsed.ExtractNodeType();
        return !string.IsNullOrEmpty(nodeType) && PartitionDefinition.IsSatelliteNodeType(nodeType);
    }

    /// <summary>
    /// True when <paramref name="path"/> contains a satellite segment (<c>/_X</c> where X is
    /// upper-case: <c>_Access</c>, <c>_Thread</c>, <c>_Comment</c>, …). Same rule as
    /// <c>MeshQuery.IsSatellitePath</c>.
    /// </summary>
    private static bool IsSatellitePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var idx = 0;
        while ((idx = path.IndexOf("/_", idx, StringComparison.Ordinal)) >= 0)
        {
            idx += 2;
            if (idx < path.Length && char.IsUpper(path[idx])) return true;
        }
        return false;
    }

    /// <summary>The partition-root (Space) NodeType — matches <c>MeshExtensions.PartitionRootNodeTypeName</c>.</summary>
    private const string PartitionRootNodeType = "Space";

    /// <summary>
    /// True when <paramref name="node"/> is a partition root: a <see cref="PartitionRootNodeType"/>
    /// node at the partition path itself (empty <see cref="MeshNode.Namespace"/>).
    /// </summary>
    private static bool IsPartitionRoot(MeshNode node)
        => string.IsNullOrEmpty(node.Namespace)
            && string.Equals(node.NodeType, PartitionRootNodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the query explicitly targets the partition-root type (<c>nodeType:Space</c>).</summary>
    private static bool QueryTargetsPartitionRoot(ParsedQuery parsed)
        => string.Equals(parsed.ExtractNodeType(), PartitionRootNodeType, StringComparison.OrdinalIgnoreCase);

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
    /// Shared collector: iterates <see cref="MeshQueryRequest.EffectiveQueries"/>,
    /// runs <see cref="FindMatchingNodes"/> per query (already <see cref="IObservable{T}"/>),
    /// and unions hits by <see cref="MeshNode.Path"/>. Emits — as a single reactive value —
    /// the matched nodes plus the FIRST query's parsed form and every query's base path.
    /// Pure-IObservable: no <c>async</c>, no <c>await</c>, no <c>IAsyncEnumerable</c>.
    /// Replaces the old <c>CollectMatchedAsync</c> whose per-query <c>ForEachAsync</c>
    /// Task-bridge re-entered the single-threaded hub pump under bulk load (the
    /// autocomplete-fan-out deadlock).
    /// <para>
    /// <see cref="MeshQueryRequest.Limit"/> is intentionally NOT pushed into any
    /// per-query parse: doing so on query #0 only made the union iteration-order
    /// dependent. The Limit is enforced post-union in <see cref="RunQueryNodes"/>.
    /// </para>
    /// </summary>
    private IObservable<(IReadOnlyList<object> Matched, ParsedQuery FirstParsed, IReadOnlyList<string> BasePaths)>
        CollectMatched(
            MeshQueryRequest request,
            JsonSerializerOptions options,
            bool useSecurityFilter)
    {
        var effectiveQueries = request.EffectiveQueries;
        var userId = GetEffectiveUserId(request);

        ParsedQuery? firstParsed = null;
        var basePaths = new List<string>(effectiveQueries.Count);
        var perQuery = new List<IObservable<object>>(effectiveQueries.Count);

        for (var qi = 0; qi < effectiveQueries.Count; qi++)
        {
            var parsedQuery = _parser.Parse(effectiveQueries[qi]);
            if (parsedQuery.Source is QuerySource.Activity or QuerySource.Accessed)
                parsedQuery = parsedQuery with { IsMain = true };

            var (basePath, effectiveScope) = ResolvePathAndScope(parsedQuery, request);
            var context = request.Context ?? parsedQuery.Context;
            basePaths.Add(basePath);
            if (qi == 0)
                firstParsed = parsedQuery;

            // FindMatchingNodes emits objects reactively; apply the RLS validator
            // (when useSecurityFilter) inline. Dedup runs once on the materialised
            // set below — cheaper to express, identical union result.
            perQuery.Add(
                FindMatchingNodes(parsedQuery, effectiveScope, basePath, userId, context, request, options)
                    .SelectMany(node =>
                    {
                        if (node is MeshNode meshNode)
                            return useSecurityFilter
                                ? ValidateRead(meshNode, userId).Where(valid => valid).Select(_ => (object)meshNode)
                                : Observable.Return<object>(meshNode);
                        return Observable.Return(node);
                    }));
        }

        // Concat preserves query order (query #0's hits before #1's) — same ordering
        // as the old sequential per-query ForEachAsync fold. ToList materialises the
        // union once; the dedup fold below is in-memory, off the I/O path.
        return perQuery.Concat()
            .ToList()
            .Select(all =>
            {
                var matchedByPath = new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
                var nonNodeMatched = new List<object>();
                var seenRefs = new HashSet<object>(ReferenceEqualityComparer.Instance);

                foreach (var n in all)
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
                }

                var matched = matchedByPath.Values.Cast<object>().Concat(nonNodeMatched).ToList();
                return ((IReadOnlyList<object>)matched, firstParsed ?? _parser.Parse(""), (IReadOnlyList<string>)basePaths);
            });
    }

    /// <summary>
    /// Resolves effective base path (request.DefaultPath fallback) and scope
    /// (Children/Subtree fallback when query has no path + Exact scope) — same
    /// rules previously inlined at the top of the former <c>QueryAsync</c>.
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
    /// observable; the caller (<see cref="CollectMatched"/>) consumes
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

        // NextLevel = the populated frontier (the in-memory mirror of the Postgres anti-join).
        // Gather ALL descendant paths first — the suppressor universe. Empty namespace segments
        // have no node, so they never suppress; a real node DOES, which is what skips the empties
        // (e.g. a/b/node surfaces at the root). Then read + filter only the frontier nodes.
        // Scoped adapters (PostgreSqlMeshQuery / Cosmos) compute the frontier in one query — the
        // pedestrian walk would be duplicate work, so skip it.
        if (effectiveScope == QueryScope.NextLevel)
        {
            if (persistence is IScopedQueryStorageAdapter)
                return Observable.Empty<object>();

            var emittedFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return WalkAdapter(basePath, QueryScope.Descendants)
                .Where(path => !string.IsNullOrEmpty(path))
                .ToList()
                .SelectMany(allPaths => NamespaceFrontier.Frontier(basePath, allPaths).ToObservable())
                .Where(path => emittedFrontier.Add(path))
                .SelectMany(path => persistence.Read(path, options)
                    .Take(1)
                    .Catch<MeshNode?, Exception>(ex =>
                    {
                        logger?.LogWarning(ex,
                            "[NextLevel.Read] swallowed for path={Path}; returning null", path);
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
                        "[StorageAdapterMeshQueryProvider.NextLevel] pipeline threw query=[{Query}] basePath={BasePath}",
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

    /// <summary>
    /// Native reactive autocomplete. A thin scoring layer over <see cref="RunQueryNodes"/>: the
    /// per-query scope walk (over <see cref="IStorageAdapter.ListChildPaths"/> / <c>Read</c>, all
    /// already <see cref="IObservable{T}"/>) is the I/O leaf, pooled inside the adapter. The query
    /// itself stays pure-IObservable here — no async-enumerable, no <c>_ioPool.Run</c>/await-foreach
    /// bridge — so the calling hub's action
    /// block is never blocked. No <c>Task.Run</c> bridge (that was the deadlock), no async-enumerable
    /// on the public surface. Emits a single snapshot then completes.
    /// </summary>
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst,
        int limit = 10,
        string? contextPath = null,
        string? context = null)
    {
        var providerName = ((IMeshQueryProvider)this).Name;
        var normalizedPath = NormalizePath(basePath);
        var normalizedPrefix = prefix ?? "";

        // Per-adapter autocomplete is a thin scoring layer over the QUERY
        // stream — autocomplete never reads nodes by path itself, never
        // re-walks the adapter. The query path (QueryCoreAsync) handles the
        // scope walk inside this adapter and returns fully-populated MeshNodes;
        // here we just score them against the prefix.
        //
        // Empty basePath: walk this adapter's full subtree from the root —
        // the per-adapter is its own boss for "find anything matching prefix"
        // inside its data. In routed setups, RoutingMeshQueryProvider has
        // already narrowed basePath to a partition key before calling here,
        // so the per-adapter never sees a truly empty basePath in routed mode.
        var queryString = string.IsNullOrEmpty(normalizedPath)
            ? "scope:subtree"
            : $"path:{normalizedPath} scope:subtree";

        var queryRequest = new MeshQueryRequest
        {
            Query = queryString,
            Context = context,
            ContextPath = contextPath,
            UserId = null,
            // Over-fetch so the scorer can pick the best matches; the request
            // limit is enforced post-scoring below. Empty basePath means "match
            // anywhere across this adapter" — contains/substring search needs the
            // FULL subtree because the matcher can't push the prefix into the scan.
            Limit = string.IsNullOrEmpty(normalizedPath)
                ? Math.Max(limit * 50, 1000)
                : Math.Max(limit * 5, 100),
        };

        // Pure-IObservable scoring layer over the query stream — no _ioPool.Run /
        // await foreach bridge (the bulk-fan-out deadlock). RunQueryNodes already
        // composes the (pooled) adapter reads reactively; we just score the snapshot.
        return RunQueryNodes(queryRequest, options, useSecurityFilter: false)
            .Select(nodes =>
            {
                var suggestions = new List<QuerySuggestion>();
                foreach (var obj in nodes)
                {
                    if (obj is not MeshNode node) continue;
                    // Skip node types excluded from autocomplete (AddAutocompleteExcludedTypes)
                    if (meshConfiguration?.AutocompleteExcludedNodeTypes.Contains(node.NodeType ?? "") == true)
                        continue;
                    if (context != null && IsExcludedByContext(node, context))
                        continue;

                    var name = node.Name ?? node.Id ?? node.Path ?? "";
                    var path = node.Path ?? "";

                    // Match score: every comparison flows through OrdinalIgnoreCase —
                    // no lowercased copies.
                    double score = 0;

                    if (string.IsNullOrEmpty(normalizedPrefix))
                    {
                        // Empty prefix = list-all-children at basePath. Rank purely by
                        // depth so parents are always visited before descendants (repro:
                        // AutocompleteMultiSourceTest.ShorterPathsWin_ParentBeforeGrandchild).
                        score = 100 - (path.Count(c => c == '/') * 10);
                    }
                    else if (name.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        score = 100 - (name.Length - normalizedPrefix.Length); // shorter prefix match wins
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

                    // Path-depth penalty for non-empty prefixes too: a parent's name
                    // match beats a deeper descendant's name match of equal strength.
                    if (!string.IsNullOrEmpty(normalizedPrefix))
                        score -= path.Count(c => c == '/');

                    score += PathProximity.ComputeBoost(contextPath, node.Path);

                    if (score > 0)
                        suggestions.Add(new QuerySuggestion(node.Path ?? "", name, node.NodeType, score, node.Icon));
                }

                IEnumerable<QuerySuggestion> ordered = mode switch
                {
                    // PathFirst: path length first, then score, then name (path-based @refs)
                    AutocompleteMode.PathFirst => suggestions
                        .OrderBy(s => s.Path.Length)
                        .ThenByDescending(s => s.Score)
                        .ThenBy(s => s.Name),
                    // RelevanceFirst: score first, then path length, then name (node selection)
                    AutocompleteMode.RelevanceFirst => suggestions
                        .OrderByDescending(s => s.Score)
                        .ThenBy(s => s.Path.Length)
                        .ThenBy(s => s.Name),
                    _ => suggestions
                        .OrderBy(s => s.Path.Length)
                        .ThenByDescending(s => s.Score)
                        .ThenBy(s => s.Name)
                };

                return (IReadOnlyCollection<QueryResult>)ordered
                    .Take(limit)
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
            });
    }

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
        // NextLevel is discovered via a frontier walk (no exact-path probes).
        if (scope == QueryScope.Children || scope == QueryScope.Descendants || scope == QueryScope.NextLevel)
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
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
        // persistence.Read is already IObservable — stay reactive end-to-end, no ToTask.
        => persistence.Read(path, options)
            .Take(1)
            .Select(node =>
            {
                if (node != null && typeof(MeshNode).GetProperty(property)?.GetValue(node) is T typedValue)
                    return typedValue;
                return default;
            });

    /// <inheritdoc cref="IMeshQueryCore.Query{T}"/>
    IObservable<QueryResultChange<T>> IMeshQueryCore.Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        => ObserveQueryInternal<T>(request, options, useSecurityFilter: false);

    /// <inheritdoc />
    public IObservable<QueryResultChange<T>> Query<T>(MeshQueryRequest request, JsonSerializerOptions options)
        => ObserveQueryInternal<T>(request, options, useSecurityFilter: true);

    /// <summary>
    /// Shared Query body. <paramref name="useSecurityFilter"/> selects between the
    /// security-filtered (IMeshQueryProvider surface) and the raw (IMeshQueryCore
    /// surface) <see cref="RunQueryNodes"/> read. The latter is what
    /// SecurityService consumes via SyncedQueryMeshNodes — it must NOT re-enter
    /// SecurityService for filtering, otherwise the DI container detects a cycle.
    /// </summary>
    private IObservable<QueryResultChange<T>> ObserveQueryInternal<T>(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        bool useSecurityFilter)
    {
        // #20: defer to the native fan-out provider for unscoped + satellite queries
        // (still emit an empty Initial so MeshQuery's merge counter advances — an
        // Observable.Empty here would never increment our slot and hang the merge).
        if (_deferToNative && DefersToNativeProvider(request))
            return Observable.Return(new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Version = Interlocked.Increment(ref _version),
                Query = _parser.Parse(request.EffectiveQueries.FirstOrDefault()),
                Items = Array.Empty<T>(),
                Timestamp = DateTimeOffset.UtcNow,
            });

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

            // Pure-IObservable query: RunQueryNodes composes the (pooled) adapter
            // reads reactively and emits the full snapshot once — no IAsyncEnumerable,
            // no _ioPool.Run / await-foreach bridge that re-entered the single-threaded
            // hub pump and deadlocked under bulk fan-out. useSecurityFilter selects the
            // RLS-filtered (IMeshQueryProvider) vs raw (IMeshQueryCore) read.
            IObservable<List<(string? Path, T Item)>> RunQuery() =>
                RunQueryNodes(request, options, useSecurityFilter)
                    .Select(nodes =>
                    {
                        var results = new List<(string?, T)>();
                        foreach (var item in nodes)
                            if (item is T typed)
                                results.Add((GetItemPath(item), typed));
                        return results;
                    });

            // Subscribe to persistence.Changes BEFORE running the initial query so
            // notifications during the I/O window are captured. After Initial,
            // swap to a live Buffer pipeline that re-queries on each batch.
            // persistence.Changes is the adapter-level Subject (in-process);
            // cross-process change visibility relies on the backend's own change
            // feed (PG LISTEN/NOTIFY, Cosmos change feed) feeding per-process
            // Subjects — same shape as prod.
            var earlyBacklog = new List<DataChangeNotification>();
            var earlyLock = new object();
            var initialDone = false;
            var earlySubscription = persistence.Changes
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

            disposables.Add(
                RunQuery().Subscribe(
                    initialResults =>
                    {
                        var initialItems = new List<T>();
                        foreach (var (path, item) in initialResults)
                        {
                            initialItems.Add(item);
                            if (!string.IsNullOrEmpty(path))
                                currentItems[path] = item;
                        }

                        DataChangeNotification[] backlog;
                        var changeBuffer = new Subject<DataChangeNotification>();
                        disposables.Add(changeBuffer);
                        disposables.Add(
                            persistence.Changes
                                .Where(n => scopeFilters.Any(sf =>
                                    PathMatcher.ShouldNotify(n.Path, sf.BasePath, sf.Scope)))
                                .Subscribe(changeBuffer));
                        // 🚨 Strict unit-of-work + zero debounce: every change
                        // triggers its own RunQuery, serialised via Concat so
                        // the shared currentItems dictionary is never raced.
                        // Buffer(DefaultDebounceInterval) was a 100 ms debouncer
                        // that batched changes; that window was the race that
                        // caused order-dependent permission-check flakes —
                        // subscribers attaching during the debounce gap saw the
                        // pre-write Replay(1) snapshot. Trade throughput (one
                        // RunQuery per change vs batched) for correctness.
                        disposables.Add(
                            changeBuffer
                                .Select(n => RunQuery()
                                    .Select(newResults => (batch: (IList<DataChangeNotification>)new[] { n }, newResults)))
                                .Concat()
                                .Subscribe(
                                    t => ProcessBatch(t.batch, t.newResults, currentItems, parsedQuery, observer),
                                    ex => observer.OnError(ex)));

                        lock (earlyLock)
                        {
                            backlog = earlyBacklog.ToArray();
                            earlyBacklog.Clear();
                            initialDone = true;
                        }
                        earlySubscription.Dispose();

                        observer.OnNext(new QueryResultChange<T>
                        {
                            ChangeType = QueryChangeType.Initial,
                            Items = initialItems,
                            Query = parsedQuery,
                            Version = Interlocked.Increment(ref _version),
                            Timestamp = DateTimeOffset.UtcNow,
                        });

                        // Push backlog through the same Concat-serialized
                        // pipeline rather than running a parallel RunQuery
                        // that would race the first live batch.
                        if (backlog.Length > 0)
                        {
                            Scheduler.Default.Schedule(() =>
                            {
                                foreach (var n in backlog)
                                {
                                    if (disposables.IsDisposed) return;
                                    try { changeBuffer.OnNext(n); }
                                    catch (ObjectDisposedException) { return; }
                                }
                            });
                        }
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
        if (node.IsExcludedFromContext(context))
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
