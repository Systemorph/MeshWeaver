using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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
    private readonly QueryParser _parser = new();
    private long _version;

    public PostgreSqlPartitionedMeshQuery(
        ICrossSchemaQueryProvider crossSchema,
        AccessService? accessService = null,
        ILogger<PostgreSqlPartitionedMeshQuery>? logger = null)
    {
        _crossSchema = crossSchema;
        _accessService = accessService;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// MeshQuery's fan-in passes every provider every query — this provider
    /// internally short-circuits scoped requests (see
    /// <see cref="NeedsFanOut"/>), so always claim a match. Returning
    /// <see langword="true"/> here is symmetric with
    /// <see cref="StorageAdapterMeshQueryProvider.Matches"/>: the routing
    /// decision lives in <see cref="ObserveQuery{T}"/> / <see cref="QueryAsync"/>.
    /// </remarks>
    public bool Matches(IReadOnlyList<string> queryNamespaces) => true;

    /// <inheritdoc/>
    public IObservable<QueryResultChange<T>> ObserveQuery<T>(
        MeshQueryRequest request, JsonSerializerOptions options)
    {
        var parsed = ParseFirst(request);
        _logger?.LogDebug(
            "[FanOut] ObserveQuery decision: NeedsFanOut={NeedsFanOut} Path='{Path}' Source={Source} Query='{Q}'",
            NeedsFanOut(parsed), parsed.Path ?? "(null)", parsed.Source, request.Query);

        // MergeProviderObservables in MeshQuery gates the merged Initial on
        // every provider emitting Initial. If we return Observable.Empty when
        // the query is scoped (NeedsFanOut=false) the source completes WITHOUT
        // emitting Initial, the merge counter never increments past our slot,
        // and the consumer hangs forever. Emit an empty Initial in the scoped
        // case so the merge can proceed (the per-schema StorageAdapterMeshQueryProvider
        // is the one that contributes the real rows for that path).
        var snapshot = new ReplaySubject<QueryResultChange<T>>(1);
        Observable.FromAsync(async ct =>
        {
            var items = new List<T>();
            if (NeedsFanOut(parsed))
            {
                await foreach (var node in EnumerateFanOutAsync(parsed, options, request, ct))
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
        if (!NeedsFanOut(parsed))
            yield break;

        var yielded = 0;
        var skip = request.Skip ?? 0;
        var limit = request.Limit ?? parsed.Limit;

        await foreach (var node in EnumerateFanOutAsync(parsed, options, request, ct))
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
    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        int limit = 10,
        CancellationToken ct = default)
        => System.Linq.AsyncEnumerable.Empty<QuerySuggestion>();

    /// <inheritdoc/>
    public IAsyncEnumerable<QuerySuggestion> AutocompleteAsync(
        string basePath,
        string prefix,
        JsonSerializerOptions options,
        AutocompleteMode mode,
        int limit,
        string? context,
        string? userId,
        CancellationToken ct = default)
        => System.Linq.AsyncEnumerable.Empty<QuerySuggestion>();

    /// <inheritdoc/>
    /// <remarks>Single-path SelectAsync is a scoped operation — leave it to
    /// the per-schema <see cref="StorageAdapterMeshQueryProvider"/>.</remarks>
    public Task<T?> SelectAsync<T>(
        string path, string property, JsonSerializerOptions options, CancellationToken ct = default)
        => Task.FromResult<T?>(default);

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
        var pinned = ResolvePinnedPartition(parsed);
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
            await _crossSchema.SyncSearchableSchemasAsync(ct);
            schemas = (await _crossSchema.GetSchemasWithTableAsync(tableName, ct)).ToList();
            if (joinTable is not null)
            {
                var withJoin = await _crossSchema.GetSchemasWithTableAsync(joinTable, ct);
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
                            activityUserId, ct))
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
        // Path-based mapping first — a concrete path with a satellite segment
        // (e.g. namespace:partition/doc/_Thread) pins the satellite table.
        if (!string.IsNullOrEmpty(parsed.Path))
        {
            foreach (var (suffix, table) in PartitionDefinition.StandardTableMappings
                         .OrderByDescending(kv => kv.Key.Length))
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
            foreach (var (suffix, table) in PartitionDefinition.StandardTableMappings
                         .OrderByDescending(kv => kv.Key.Length))
            {
                if (PathContainsSegment(sanitized, suffix))
                    return table;
            }
        }

        // nodeType-based mapping when neither path nor namespace LIKE
        // carries a satellite hint.
        var nodeType = parsed.ExtractNodeType();
        if (!string.IsNullOrEmpty(nodeType)
            && PartitionDefinition.NodeTypeToSuffix.TryGetValue(nodeType, out var nodeTypeSuffix)
            && PartitionDefinition.StandardTableMappings.TryGetValue(nodeTypeSuffix, out var nodeTypeTable))
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
    internal static string? ResolvePinnedPartition(ParsedQuery parsed)
    {
        // Concrete Path wins — e.g. `namespace:partition/doc/_Thread` lands here.
        if (!string.IsNullOrEmpty(parsed.Path))
        {
            var first = FirstSegment(parsed.Path);
            if (string.IsNullOrEmpty(first) || first == "*") return null;
            // 🚨 GLOBAL SATELLITE NAMESPACES (`_Access`, `_Activity`,
            // `_UserActivity`, `_Thread`) are registered with explicit Schema
            // names (`system_access`, `system_activity`, …) — the schema is
            // NOT the lowercased namespace. We don't have the partition cache
            // here to look up the actual schema, so fall through to the
            // GetSchemasWithTableAsync fan-out path which discovers schemas
            // via information_schema. Cost: one extra round-trip on these
            // satellite queries; correctness wins.
            if (first.StartsWith('_')) return null;
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
                // Same satellite-namespace guard as the path-based path.
                if (first.StartsWith('_')) return null;
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
