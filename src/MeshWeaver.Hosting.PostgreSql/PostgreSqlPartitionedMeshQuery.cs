using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// The PostgreSQL <see cref="IMeshQueryProvider"/> for partitioned setups —
/// the one Postgres-aware brain in the <see cref="MeshQuery"/> fan-in chain.
///
/// <para><b>The architectural contract.</b> <see cref="MeshQuery"/> delegates
/// every query to every registered provider and merges the returned
/// observables. Each provider is responsible for the WHOLE shape of its own
/// data domain — the aggregator never tells a provider "you handle only X."
/// For Postgres, that means this provider alone reacts to a missing
/// namespace (unscoped query) or a wildcard first segment by fanning out
/// across every searchable partition. Fan-out is an implementation detail
/// of the Postgres provider, NOT a separate provider type, NOT a concern
/// of <c>MeshSearch</c> or any GUI control.</para>
///
/// <para><b>Two query shapes, one provider:</b></para>
/// <list type="number">
///   <item><b>Scoped</b> (single concrete first segment, no wildcard) →
///     short-circuit to an empty Initial emission. The pedestrian
///     <see cref="StorageAdapterMeshQueryProvider"/> backed by the
///     <see cref="IStorageAdapter"/> path-routing facade still runs in the
///     fan-in chain; for a scoped Postgres path it routes directly to the
///     per-schema adapter and contributes the actual rows. We deliberately
///     don't duplicate that work here — the result-merge in
///     <see cref="MeshQuery"/> dedupes by Path so returning the row twice
///     would be wasted load, not wrong.</item>
///   <item><b>Missing namespace / wildcard first segment</b> → fan out
///     across every searchable partition via
///     <see cref="ICrossSchemaQueryProvider.QueryAcrossSchemasAsync(ParsedQuery,JsonSerializerOptions,IReadOnlyList{string},string,string?,string?,CancellationToken)"/>.
///     Satellite-aware: a <c>nodeType:</c> filter routes the UNION to the
///     matching satellite table (Thread → <c>threads</c>, Activity →
///     <c>activities</c>, …); <c>source:activity</c> / <c>source:accessed</c>
///     turn into a per-schema INNER JOIN that projects the satellite's
///     <c>last_modified</c> into the result row so cross-partition
///     sort:LastModified-desc ranks by activity recency. Schema selection
///     filters to partitions that actually contain both the projection and
///     join tables — older partitions / static-mesh schemas (Doc, etc.)
///     only ship <c>mesh_nodes</c>.</item>
/// </list>
///
/// <para>The Initial emission is a one-shot snapshot. Live deltas across
/// partitions are out of scope; a cross-partition feed (Activity, Latest
/// Threads, Recently Viewed) is an explicit re-query, not a live cursor.</para>
/// </summary>
public sealed class PostgreSqlPartitionedMeshQuery : IMeshQueryProvider
{
    private readonly ICrossSchemaQueryProvider _crossSchema;
    private readonly AccessService? _accessService;
    private readonly ILogger<PostgreSqlPartitionedMeshQuery>? _logger;
    private readonly PostgreSqlPartitionStorageProvider? _partitionProvider;
    private readonly IoPoolRegistry? _ioPoolRegistry;
    // Cross-schema fan-out + autocomplete run on the bounded I/O pool (Invoke), never a bare
    // Observable.FromAsync. The per-schema leaf reads are pooled inside their own adapters.
    private readonly IIoPool _ioPool;
    // Routing rules (nodeType:User → Auth, nodeType:Invitation → Admin) live here. A path-less
    // query that matches a rule must pin to the rule's schema instead of fanning out cross-schema
    // (which EXCLUDES auth/admin) — see the hint fallback in EnumerateFanOutAsync.
    private readonly MeshConfiguration? _meshConfiguration;
    private readonly QueryParser _parser = new();
    private long _version;

    // Per-schema scoped-query delegates, keyed by the CACHED adapter instance (so each shares
    // that adapter's live in-process Changes feed). A SCOPED query — a concrete first path
    // segment, including _-prefix globals like _Access→system_access — is served by a per-schema
    // PostgreSqlMeshQuery (full live-delta + satellite serving). That is how THIS provider OWNS
    // scoped serving, letting the pedestrian StorageAdapterMeshQueryProvider be retired for
    // Postgres. See Doc/Architecture/PartitionStorageRouting.md.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<PostgreSqlStorageAdapter, PostgreSqlMeshQuery> _scopedDelegates = new();

    public PostgreSqlPartitionedMeshQuery(
        ICrossSchemaQueryProvider crossSchema,
        AccessService? accessService = null,
        ILogger<PostgreSqlPartitionedMeshQuery>? logger = null,
        PostgreSqlPartitionStorageProvider? partitionProvider = null,
        IoPoolRegistry? ioPoolRegistry = null,
        MeshConfiguration? meshConfiguration = null)
    {
        _crossSchema = crossSchema;
        _accessService = accessService;
        _logger = logger;
        _partitionProvider = partitionProvider;
        _ioPoolRegistry = ioPoolRegistry;
        _ioPool = ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;
        _meshConfiguration = meshConfiguration;
    }

    /// <summary>
    /// The per-schema <see cref="PostgreSqlMeshQuery"/> that serves a query SCOPED to the
    /// partition holding <paramref name="path"/>'s first segment — resolved (incl. <c>_</c>-prefix
    /// globals like <c>_Access</c>→<c>system_access</c>) to its CACHED adapter, one delegate per
    /// adapter so it observes that adapter's live <c>Changes</c> feed (post-write re-emits work).
    /// Null for unscoped / wildcard paths → those fan out across schemas.
    /// </summary>
    private PostgreSqlMeshQuery? GetDelegateForPath(string? path)
    {
        if (_partitionProvider is null || string.IsNullOrEmpty(path)) return null;
        var first = FirstSegment(path);
        if (string.IsNullOrEmpty(first) || first == "*") return null;
        var adapter = _partitionProvider.GetSchemaAdapter(path);
        if (adapter is null) return null;
        return _scopedDelegates.GetOrAdd(adapter, a =>
            new PostgreSqlMeshQuery(a, _accessService, meshConfiguration: null,
                excludedNamespaces: null, embeddingProvider: _partitionProvider.EmbeddingProvider,
                ioPoolRegistry: _ioPoolRegistry));
    }

    /// <summary>
    /// The scoped delegate for a parsed query, or null when the query must fan out across
    /// partitions. Routes to the per-schema <see cref="PostgreSqlMeshQuery"/> — which serves
    /// primary content AND satellites with LIVE deltas under the same access filter — for any
    /// query pinned to a single concrete partition, INCLUDING scoped satellite reads
    /// (<c>namespace:Systemorph/_Thread</c>, <c>nodeType:Thread namespace:Systemorph</c>,
    /// <c>_Access</c>-style globals resolved via the registered-partition cache). This retires
    /// the pedestrian <see cref="StorageAdapterMeshQueryProvider"/> for scoped satellites — it
    /// could only ever walk <c>mesh_nodes</c>, never the satellite tables. Verified by
    /// <c>SatelliteSyncedInitialTests</c>: a permitted user's pre-existing satellite rows land
    /// in the delegate's Initial (the historical "Initial under-returns satellite rows" claim
    /// was the access filter correctly dropping rows for an unpermitted Anonymous caller).
    ///
    /// <para>Two cases still fan out (→ null here): <c>source:activity</c> /
    /// <c>source:accessed</c> are cross-partition satellite JOINs (the dashboard feeds span
    /// every partition — a scoped one still runs on the fan-out's single-element schema list);
    /// and unscoped / wildcard-first paths (<see cref="GetDelegateForPath"/> returns null), the
    /// genuine cross-partition broadcasts.</para>
    /// </summary>
    private PostgreSqlMeshQuery? GetScopedDelegate(ParsedQuery parsed)
        => parsed.Source is QuerySource.Activity or QuerySource.Accessed
            ? null
            : GetDelegateForPath(parsed.Path);

    /// <inheritdoc/>
    /// <remarks>
    /// MeshQuery's fan-in passes every provider every query — this provider
    /// internally short-circuits scoped requests (see
    /// <see cref="NeedsFanOut"/>), so always claim a match. Returning
    /// <see langword="true"/> here is symmetric with
    /// <see cref="StorageAdapterMeshQueryProvider.Matches"/>: the routing
    /// decision lives in <see cref="Query{T}"/> / <see cref="QueryAsync"/>.
    /// </remarks>
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <inheritdoc/>
    public IObservable<QueryResultChange<T>> Query<T>(
        MeshQueryRequest request, JsonSerializerOptions options)
    {
        var parsed = ParseFirst(request);

        // SCOPED → delegate to the per-schema PostgreSqlMeshQuery (live deltas + satellite
        // serving over the cached adapter). This is the scoped serving that retires the
        // pedestrian for Postgres; only unscoped / wildcard / activity-join queries fan out.
        if (GetScopedDelegate(parsed) is { } scoped)
            return scoped.Query<T>(request, options);

        _logger?.LogDebug(
            "[FanOut] Query decision: NeedsFanOut={NeedsFanOut} Path='{Path}' Source={Source} Query='{Q}'",
            NeedsFanOut(parsed), parsed.Path ?? "(null)", parsed.Source, request.Query);

        // MergeProviderObservables in MeshQuery gates the merged Initial on
        // every provider emitting Initial. If we return Observable.Empty when
        // the query is scoped (NeedsFanOut=false) the source completes WITHOUT
        // emitting Initial, the merge counter never increments past our slot,
        // and the consumer hangs forever. Emit an empty Initial in the scoped
        // case so the merge can proceed (the per-schema StorageAdapterMeshQueryProvider
        // is the one that contributes the real rows for that path).
        var snapshot = new ReplaySubject<QueryResultChange<T>>(1);
        _ioPool.Invoke(async ct =>
        {
            var items = new List<T>();
            if (NeedsFanOut(parsed))
            {
                await foreach (var node in EnumerateFanOutAsync(parsed, options, request, ct).ConfigureAwait(false))
                {
                    if (node is T typed)
                        items.Add(typed);
                }
            }
            return new QueryResultChange<T>
            {
                ChangeType = QueryChangeType.Initial,
                Version = Interlocked.Increment(ref _version),
                Query = parsed,
                Items = items,
                Timestamp = DateTimeOffset.UtcNow,
            };
        }).Subscribe(snapshot);
        return snapshot.AsObservable();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<object> QueryAsync(
        MeshQueryRequest request,
        JsonSerializerOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        var parsed = ParseFirst(request);

        // SCOPED → delegate to the per-schema provider (it applies skip/limit/select itself).
        if (GetScopedDelegate(parsed) is { } scoped)
        {
            await foreach (var item in scoped.QueryAsync(request, options, ct).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        if (!NeedsFanOut(parsed))
            yield break;

        var yielded = 0;
        var skip = request.Skip ?? 0;
        var limit = request.Limit ?? parsed.Limit;

        await foreach (var node in EnumerateFanOutAsync(parsed, options, request, ct).ConfigureAwait(false))
        {
            if (skip > 0) { skip--; continue; }
            yield return parsed.Select != null
                ? ParsedQuery.ProjectToSelect(node, parsed.Select)
                : (object)node;
            yielded++;
            if (limit.HasValue && yielded >= limit.Value)
                yield break;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Autocomplete NEVER fans out across partitions. Two shapes:
    /// <list type="bullet">
    ///   <item><b>TOP-LEVEL</b> — empty base (root) or an explicit <c>/name</c> prefix → the
    ///     partition roots from the <c>public.top_level_index</c> matview (one small indexed
    ///     relation, PG-scored).</item>
    ///   <item><b>WITHIN-PARTITION</b> — a concrete <paramref name="basePath"/> → owned by the
    ///     per-schema provider (a scoped <c>namespace:&lt;basePath&gt; scope:descendants</c>
    ///     query against the single context schema); this provider contributes nothing.</item>
    /// </list>
    /// </remarks>
    public IObservable<IReadOnlyCollection<QueryResult>> Autocomplete(
        string basePath, string prefix, JsonSerializerOptions options,
        AutocompleteMode mode = AutocompleteMode.RelevanceFirst, int limit = 10,
        string? contextPath = null, string? context = null)
    {
        var isTopLevel = string.IsNullOrEmpty(basePath) || (prefix?.StartsWith('/') ?? false);
        if (!isTopLevel)
            // WITHIN-PARTITION → delegate to the per-schema provider for the context partition
            // (scoped, never fans out). This is the within-partition half the pedestrian served.
            return GetDelegateForPath(basePath)?.Autocomplete(basePath, prefix, options, mode, limit, contextPath, context)
                ?? Observable.Return((IReadOnlyCollection<QueryResult>)Array.Empty<QueryResult>());

        var effectivePrefix = (prefix ?? "").TrimStart('/');
        var ctxUser = _accessService?.Context?.ObjectId ?? _accessService?.CircuitContext?.ObjectId;
        // System → null (sees all); no context → Anonymous (Public only); else the caller.
        var effectiveUserId = ctxUser == WellKnownUsers.System ? null
            : string.IsNullOrEmpty(ctxUser) ? WellKnownUsers.Anonymous : ctxUser;

        // _ioPool.Invoke already offloads to the ThreadPool behind its gate — no extra SubscribeOn.
        return _ioPool.Invoke(ct =>
            _crossSchema.AutocompleteTopLevelAsync(effectivePrefix, effectiveUserId, limit, ct));
    }

    /// <inheritdoc/>
    /// <remarks>Single-path Select is scoped → delegate to the per-schema
    /// <see cref="PostgreSqlMeshQuery"/> for the path's partition (cached adapter).</remarks>
    public IObservable<T?> Select<T>(string path, string property, JsonSerializerOptions options)
        => GetDelegateForPath(path)?.Select<T>(path, property, options)
            ?? Observable.Return<T?>(default);

    private ParsedQuery ParseFirst(MeshQueryRequest request)
    {
        var first = request.EffectiveQueries.FirstOrDefault();
        return string.IsNullOrEmpty(first) ? ParsedQuery.Empty : _parser.Parse(first);
    }

    /// <summary>
    /// True when the query is genuinely unscoped (no partition-narrowing
    /// constraint), so the result must come from every searchable partition
    /// rather than the caller's own.
    ///
    /// <para>Scoped = a single concrete first segment in <see cref="ParsedQuery.Path"/>
    /// (no <c>*</c>, no empty, no comma/pipe-list across partitions). Wildcards
    /// (<c>*/_Thread</c>) are treated as fan-out because the user is explicitly
    /// asking for results across partitions.</para>
    /// </summary>
    internal static bool NeedsFanOut(ParsedQuery parsed)
    {
        // source:activity / source:accessed are satellite-join queries that the
        // pedestrian StorageAdapterMeshQueryProvider can't handle on Postgres:
        // ListChildPaths only sees mesh_nodes rows, never satellite-table rows,
        // so its subtree walk misses every `_Activity` / `_UserActivity` path.
        // Route them through THIS provider regardless of scope — when the query
        // is namespace-scoped the cross-schema UNION still emits the right
        // rows; it just runs against a one-element schema list.
        if (parsed.Source is QuerySource.Activity or QuerySource.Accessed)
            return true;
        // Any query that resolves to a SATELLITE table (Thread, Activity,
        // Comment, …) — whether via path segment or nodeType filter — must
        // go through this provider, because the pedestrian StorageAdapterMeshQueryProvider's
        // path walk never visits satellite tables. Without this, even a
        // single-partition `namespace:partition/*/_Thread` query degrades to
        // empty.
        if (ResolveTable(parsed) != "mesh_nodes")
            return true;
        // No Path → unscoped → fan out.
        if (string.IsNullOrEmpty(parsed.Path))
            return true;
        var firstSegment = FirstSegment(parsed.Path);
        // Wildcard first segment → explicit cross-partition request.
        if (firstSegment == "*")
            return true;
        // Single concrete partition → leave to the per-schema provider.
        return false;
    }

    private static string FirstSegment(string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0) return "";
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    private async IAsyncEnumerable<MeshNode> EnumerateFanOutAsync(
        ParsedQuery parsed,
        JsonSerializerOptions options,
        MeshQueryRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct)
    {
        // For source:activity, the satellite to JOIN is "activities" — the
        // primary projection still comes from mesh_nodes (the main content
        // node). For source:accessed, the same shape uses user_activities.
        // Otherwise: ResolveTable picks between path-based satellite mapping
        // (namespace:*/_Thread → threads) and nodeType-based mapping
        // (nodeType:Thread → threads, nodeType:ThreadMessage → threads, …).
        string tableName;
        string? joinTable = null;
        if (parsed.Source is QuerySource.Activity)
        {
            tableName = "mesh_nodes";
            joinTable = "activities";
        }
        else if (parsed.Source is QuerySource.Accessed)
        {
            tableName = "mesh_nodes";
            joinTable = "user_activities";
        }
        else
        {
            tableName = ResolveTable(parsed);
        }

        // 🚨 Partition-pinned fast path. When the parsed query carries a
        // concrete first segment (e.g. `nodeType:Thread namespace:Systemorph`,
        // `path:Systemorph/_Thread/foo`, `namespace:Systemorph/_Thread`), we
        // KNOW which schema to hit — skip the fan-out machinery entirely:
        //   * no `SyncSearchableSchemasAsync` PG round-trip
        //   * no `GetSchemasWithTableAsync` PG round-trip
        //   * single-element schema list straight to QueryAcrossSchemasAsync
        // The two skipped round-trips alone were costing 200-700 ms on cold
        // page loads (visible in prod App Insights as serial `SELECT … FROM
        // public.searchable_schemas` and `information_schema.tables` lookups
        // right before the actual UNION query). Trusts that satellite tables
        // exist in every partition that has primary `mesh_nodes` — if a row
        // lookup misses it returns no rows; UNION over zero rows is still
        // correct.
        var pinned = ResolvePinnedPartition(parsed, ResolveGlobalSchema);
        // Routing-rule fallback: a path-less query (no concrete first segment) that matches a
        // registered QueryRoutingRule pins to that rule's partition — nodeType:User → auth,
        // nodeType:Invitation → admin. These schemas are DELIBERATELY excluded from cross-schema
        // search (PostgreSqlCrossSchemaQueryProvider.ExcludedSchemas: auth is the central user/auth
        // mirror, admin is platform-only), so without this a path-less nodeType:User query fans out,
        // misses auth, and never finds the user → the onboarding redirect loop. This realises the
        // hint UserNodeType/InvitationNodeType register (previously inert — no consumer).
        if (pinned is null && _meshConfiguration is not null)
        {
            var hintPartition = _meshConfiguration.ResolveRoutingHints(parsed).Partition;
            if (!string.IsNullOrEmpty(hintPartition))
                pinned = hintPartition.ToLowerInvariant();
        }
        List<string> schemas;
        if (pinned is not null)
        {
            schemas = [pinned];
            _logger?.LogDebug(
                "[FanOut] pinned fast-path: partition={Partition} table={Table} joinTable={JoinTable}",
                pinned, tableName, joinTable ?? "(none)");
        }
        else
        {
            // Unpinned (wildcard or missing first segment) → genuine cross-schema
            // fan-out. Sync the schema list + filter to schemas that contain
            // BOTH the projection table and the join table. Older partitions /
            // static-mesh schemas (Doc, etc.) only ship mesh_nodes — including
            // them in a satellite UNION raises "relation does not exist".
            await _crossSchema.SyncSearchableSchemasAsync(ct).ConfigureAwait(false);
            schemas = (await _crossSchema.GetSchemasWithTableAsync(tableName, ct).ConfigureAwait(false)).ToList();
            if (joinTable is not null)
            {
                var withJoin = await _crossSchema.GetSchemasWithTableAsync(joinTable, ct).ConfigureAwait(false);
                var joinSet = new HashSet<string>(withJoin, StringComparer.OrdinalIgnoreCase);
                schemas = schemas.Where(s => joinSet.Contains(s)).ToList();
            }
        }
        _logger?.LogDebug(
            "[FanOut] schemas: count={Count} table={Table} joinTable={JoinTable} pinned={Pinned} list=[{Schemas}]",
            schemas.Count, tableName, joinTable ?? "(none)", pinned ?? "(none)", string.Join(", ", schemas));
        if (schemas.Count == 0)
            yield break;

        // Strip the path when it carries a wildcard segment ("*"). The schema
        // selection + satellite table selection above already encoded the
        // routing intent — keeping `*` in the SQL WHERE clause would force
        // `n.path LIKE '*/...'` which matches nothing. For partition-pinned
        // wildcards (e.g. `namespace:p/*/_Thread`) the partition schema is
        // already bound so every row in `p.threads` matches the pattern.
        var queryForSql = parsed;
        if (!string.IsNullOrEmpty(queryForSql.Path) && queryForSql.Path.Contains('*'))
            queryForSql = queryForSql with { Path = null, Scope = QueryScope.Exact };

        var userId = GetEffectiveUserId(request);
        // activityUserId is only meaningful for source:accessed today (joins
        // user_activities); source:activity reads the activity satellite,
        // not user-scoped.
        var activityUserId = parsed.Source == QuerySource.Accessed ? userId : null;

        _logger?.LogInformation(
            "[FanOut] schemas={Count} table={Table} source={Source} userId={User} query={Q}",
            schemas.Count, tableName, parsed.Source, userId, request.Query);

        await foreach (var node in _crossSchema.QueryAcrossSchemasAsync(
                            queryForSql, options, schemas, tableName,
                            userId == WellKnownUsers.System ? null : userId,
                            activityUserId, ct).ConfigureAwait(false))
        {
            yield return node;
        }
    }

    /// <summary>
    /// Resolves the satellite table the fan-out should UNION across, given a
    /// parsed query. Consulted in priority order:
    /// <list type="number">
    ///   <item><b>Path segment</b> — <c>namespace:*/_Thread</c>,
    ///     <c>namespace:partition/_Thread</c>, <c>namespace:partition/*/_Thread</c>,
    ///     and any other path that carries a satellite segment
    ///     (<c>_Thread</c>, <c>_ThreadMessage</c>, <c>_Activity</c>,
    ///     <c>_Access</c>, …) resolve via <see cref="PartitionDefinition.StandardTableMappings"/>.
    ///     Longest-suffix-wins ordering inside <see cref="PartitionDefinition.ResolveTable"/>
    ///     means <c>_ThreadMessage</c> beats <c>_Thread</c> when both could match.</item>
    ///   <item><b>nodeType filter</b> — when the path is missing or doesn't
    ///     contain a satellite segment, fall back to the nodeType filter:
    ///     <c>nodeType:Thread</c> → <c>threads</c>, <c>nodeType:ThreadMessage</c> → <c>threads</c>,
    ///     <c>nodeType:Activity</c> → <c>activities</c>, <c>nodeType:Comment</c> → <c>annotations</c>,
    ///     etc. — via <see cref="PartitionDefinition.NodeTypeToSuffix"/> chained
    ///     into <see cref="PartitionDefinition.StandardTableMappings"/>.</item>
    ///   <item><b>Fallback</b> — <c>mesh_nodes</c> for primary content.</item>
    /// </list>
    /// </summary>
    internal static string ResolveTable(ParsedQuery parsed)
    {
        // Satellite table layout — the configurable default (PostgreSqlStorageOptions.SatelliteTables);
        // table names are uniform across a fan-out's schemas, so the default maps apply. No static dict.
        var segmentTables = PartitionDefinition.DefaultSegmentTableMappings();

        // Path-based mapping first — a concrete path with a satellite segment
        // (e.g. namespace:partition/doc/_Thread) pins the satellite table.
        if (!string.IsNullOrEmpty(parsed.Path))
        {
            foreach (var (suffix, table) in segmentTables.OrderByDescending(kv => kv.Key.Length))
            {
                if (PathContainsSegment(parsed.Path, suffix))
                    return table;
            }
        }

        // Wildcard namespace mapping — `namespace:*/_Thread` is parsed as a
        // `namespace LIKE '%/_Thread'` filter (NOT as a Path), so the
        // path-based check above doesn't see it. Walk the parsed filter for
        // a namespace LIKE node and inspect its value for a satellite segment.
        var nsLikeValue = ExtractNamespaceLikeValue(parsed.Filter);
        if (!string.IsNullOrEmpty(nsLikeValue))
        {
            // Strip SQL wildcards so PathContainsSegment can do its
            // boundary check ("partition/%/_Thread" → "partition//_Thread"
            // → still has '_Thread' bounded by '/').
            var sanitized = nsLikeValue.Replace("%", "");
            foreach (var (suffix, table) in segmentTables.OrderByDescending(kv => kv.Key.Length))
            {
                if (PathContainsSegment(sanitized, suffix))
                    return table;
            }
        }

        // nodeType-based mapping when neither path nor namespace LIKE
        // carries a satellite hint.
        var nodeType = parsed.ExtractNodeType();
        if (!string.IsNullOrEmpty(nodeType)
            && PartitionDefinition.DefaultNodeTypeTableMappings().TryGetValue(nodeType, out var nodeTypeTable))
        {
            return nodeTypeTable;
        }

        return "mesh_nodes";
    }

    /// <summary>
    /// Walks <paramref name="node"/> for a <c>QueryComparison</c> whose
    /// selector is <c>namespace</c> and operator is <c>Like</c>. The
    /// <see cref="QueryParser"/> emits exactly this shape for
    /// <c>namespace:VALUE_WITH_*</c> (e.g. <c>namespace:*/_Thread</c>) —
    /// stashing the matched pattern as the LIKE argument. Returns the raw
    /// pattern (with <c>%</c> still in place) for the caller to sanitise.
    /// <see langword="null"/> if no matching node.
    /// </summary>
    private static string? ExtractNamespaceLikeValue(QueryNode? node)
    {
        if (node is null) return null;
        if (node is QueryComparison c
            && c.Condition.Selector.Equals("namespace", StringComparison.OrdinalIgnoreCase)
            && c.Condition.Operator == QueryOperator.Like
            && c.Condition.Values is { Length: > 0 } values)
        {
            return values[0];
        }
        if (node is QueryAnd andNode)
        {
            foreach (var child in andNode.Children)
            {
                var v = ExtractNamespaceLikeValue(child);
                if (v is not null) return v;
            }
        }
        if (node is QueryOr orNode)
        {
            foreach (var child in orNode.Children)
            {
                var v = ExtractNamespaceLikeValue(child);
                if (v is not null) return v;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the partition scope of <paramref name="parsed"/>:
    /// <list type="bullet">
    ///   <item>A concrete first segment (e.g. <c>namespace:partition/...</c>,
    ///     <c>namespace:partition/*/_Thread</c>) pins the query to ONE
    ///     partition; the fan-out narrows to that single schema.</item>
    ///   <item><c>*</c> as first segment or an empty/missing path → fan out
    ///     across every searchable partition.</item>
    /// </list>
    /// </summary>
    internal static string? ResolvePinnedPartition(
        ParsedQuery parsed, Func<string, string?>? resolveGlobalSchema = null)
    {
        // Concrete Path wins — e.g. `namespace:partition/doc/_Thread` lands here.
        if (!string.IsNullOrEmpty(parsed.Path))
        {
            var first = FirstSegment(parsed.Path);
            if (string.IsNullOrEmpty(first) || first == "*") return null;
            // 🚨 GLOBAL SATELLITE NAMESPACES (`_Access`, `_Activity`,
            // `_UserActivity`, `_Thread`) are registered with explicit Schema
            // names (`system_access`, `system_activity`, …) — the schema is NOT
            // the lowercased namespace. Resolve them through the registered-
            // partition lookup (the SAME source PostgreSqlPathRoutingAdapter.ResolveSchema
            // uses for scoped reads) so the fan-out pins to the one real schema
            // instead of a SyncSearchableSchemas + GetSchemasWithTableAsync discovery
            // round-trip. Unregistered (or no resolver) → null → fan-out (correctness floor).
            if (first.StartsWith('_')) return resolveGlobalSchema?.Invoke(first);
            return first.ToLowerInvariant();
        }

        // Wildcard namespace path went into a LIKE filter — e.g.
        // `namespace:partition/*/_Thread` parses as `namespace LIKE 'partition/%/_Thread'`.
        // If the FIRST segment of the LIKE pattern is concrete (no '*' / '%'),
        // pin to that partition.
        var nsLike = ExtractNamespaceLikeValue(parsed.Filter);
        if (!string.IsNullOrEmpty(nsLike))
        {
            // Find the first segment of the pattern (everything before the
            // first '/' or '%'). If it has no wildcard, it's the partition.
            var trimmed = nsLike.TrimStart('/');
            var stopIdx = trimmed.IndexOfAny(new[] { '/', '%', '*' });
            var first = stopIdx < 0 ? trimmed : trimmed[..stopIdx];
            if (!string.IsNullOrEmpty(first) && !first.Contains('*') && !first.Contains('%'))
            {
                // Same global-satellite resolution as the path-based branch.
                if (first.StartsWith('_')) return resolveGlobalSchema?.Invoke(first);
                return first.ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary>
    /// Mirrors <see cref="PartitionDefinition"/>'s private path-segment match:
    /// the suffix must appear at a path boundary (either start-of-string or
    /// preceded by '/' AND followed by '/' or end-of-string).
    /// </summary>
    private static bool PathContainsSegment(string path, string segment)
    {
        var idx = 0;
        while (idx < path.Length)
        {
            var pos = path.IndexOf(segment, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return false;
            var atStart = pos == 0 || path[pos - 1] == '/';
            var atEnd = pos + segment.Length == path.Length || path[pos + segment.Length] == '/';
            if (atStart && atEnd) return true;
            idx = pos + 1;
        }
        return false;
    }

    /// <summary>
    /// Resolves a <c>_</c>-prefixed global-satellite namespace (<c>_Access</c>,
    /// <c>_Activity</c>, <c>_Thread</c>, <c>_UserActivity</c>) to its real partition
    /// schema (<c>system_access</c>, …) via the process-wide registered-partition cache —
    /// the same lookup <see cref="PostgreSqlPathRoutingAdapter"/> uses for scoped reads.
    /// Lets the fan-out pin these to the one real schema instead of an information_schema
    /// discovery round-trip. <see langword="null"/> when no provider is wired or the
    /// namespace isn't registered (→ caller falls back to the discovery fan-out).
    /// </summary>
    private string? ResolveGlobalSchema(string segment)
        => _partitionProvider is not null
           && _partitionProvider.TryGetRegisteredPartition(segment, out var def)
           && !string.IsNullOrEmpty(def.Schema)
            ? def.Schema
            : null;

    private string GetEffectiveUserId(MeshQueryRequest request)
    {
        if (request.UserId == WellKnownUsers.System)
            return WellKnownUsers.System;
        if (!string.IsNullOrEmpty(request.UserId))
            return request.UserId;
        var userId = _accessService?.Context?.ObjectId
                     ?? _accessService?.CircuitContext?.ObjectId;
        return string.IsNullOrEmpty(userId) ? WellKnownUsers.Anonymous : userId;
    }
}
