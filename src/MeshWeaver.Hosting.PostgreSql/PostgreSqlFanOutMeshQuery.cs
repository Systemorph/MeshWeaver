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
/// PostgreSQL fan-out query provider — the missing piece behind the prod
/// symptom where <c>UserActivityLayoutAreas</c>' Activity Feed and Latest
/// Threads come back empty for users with content in multiple partitions.
///
/// <para>The pedestrian <see cref="StorageAdapterMeshQueryProvider"/> talks
/// to a single <see cref="IStorageAdapter"/> (the path-routing facade) and
/// walks via <c>ListChildPaths</c>; with no base path the routing adapter
/// throws (root-level walks are a query concern, not a storage concern), so
/// every unscoped or wildcard-namespaced query degrades to "nothing in the
/// caller's own partition." This provider plugs that gap by deciding, at
/// the start of every <c>ObserveQuery</c> / <c>QueryAsync</c> /
/// <c>AutocompleteAsync</c> call:</para>
/// <list type="number">
///   <item><b>Scoped</b> (single concrete first segment, no wildcard) → return
///     empty. <see cref="StorageAdapterMeshQueryProvider"/> handles those
///     unchanged via the per-schema path-routing adapter — there is no value
///     in doubling the result set.</item>
///   <item><b>Fan-out</b> (no path, empty path, or a path whose first segment
///     is <c>*</c>) → consult every searchable partition via
///     <see cref="ICrossSchemaQueryProvider.QueryAcrossSchemasAsync(ParsedQuery,JsonSerializerOptions,IReadOnlyList{string},string,string?,string?,CancellationToken)"/>.
///     The provider picks the right satellite table from
///     <see cref="PartitionDefinition.NodeTypeToSuffix"/> / <see cref="PartitionDefinition.StandardTableMappings"/>
///     when the query carries a <c>nodeType:</c> filter, and forwards the
///     <c>source:activity</c> / <c>source:accessed</c> JOIN hint so the
///     UNION preserves activity-recency ordering across schemas.</item>
/// </list>
///
/// <para>The fan-out result emits as a single
/// <see cref="QueryChangeType.Initial"/> emission — there is no per-partition
/// live notify hookup here. The pedestrian provider's live deltas keep
/// flowing on scoped queries; for cross-partition dashboards an explicit
/// re-query (or a future cross-partition change-notify subscription) is the
/// expected re-poll strategy.</para>
/// </summary>
public sealed class PostgreSqlFanOutMeshQuery : IMeshQueryProvider
{
    private readonly ICrossSchemaQueryProvider _crossSchema;
    private readonly AccessService? _accessService;
    private readonly ILogger<PostgreSqlFanOutMeshQuery>? _logger;
    private readonly QueryParser _parser = new();
    private long _version;

    public PostgreSqlFanOutMeshQuery(
        ICrossSchemaQueryProvider crossSchema,
        AccessService? accessService = null,
        ILogger<PostgreSqlFanOutMeshQuery>? logger = null)
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
        // Sync the partition-schema list each time. GetSearchableSchemasAsync's
        // cached read of public.searchable_schemas only syncs from
        // information_schema when its own table is empty — a new partition
        // created mid-session is invisible to the fan-out until the next
        // sync. SyncSearchableSchemasAsync is one cheap SELECT + DELETE +
        // INSERTs, so paying it per fan-out is the simplest correctness fix
        // (the alternative — pg_notify-on-CREATE-SCHEMA — needs DDL trigger
        // wiring this provider doesn't own).
        await _crossSchema.SyncSearchableSchemasAsync(ct);

        // For source:activity, the satellite to JOIN is "activities" — the
        // primary projection still comes from mesh_nodes (the main content
        // node). For source:accessed, the same shape uses user_activities.
        // Otherwise: a nodeType filter routes the UNION to its satellite
        // table when one is known; an unconstrained query goes to mesh_nodes.
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
            tableName = ResolveTableForNodeTypeFilter(parsed) ?? "mesh_nodes";
        }

        // Filter to schemas that contain BOTH the projection table and the
        // join table (when present). Older partitions / static-mesh schemas
        // (Doc, etc.) only ship mesh_nodes — including them in a satellite
        // UNION raises "relation does not exist".
        var schemas = await _crossSchema.GetSchemasWithTableAsync(tableName, ct);
        if (joinTable is not null)
        {
            var withJoin = await _crossSchema.GetSchemasWithTableAsync(joinTable, ct);
            var joinSet = new HashSet<string>(withJoin, StringComparer.OrdinalIgnoreCase);
            schemas = schemas.Where(s => joinSet.Contains(s)).ToList();
        }

        // If the query is namespace-scoped (single concrete first segment),
        // narrow the fan-out to that one partition. Postgres-schema names
        // are lowercased relative to MeshNode namespaces, mirroring the
        // PartitionDefinition.Schema convention.
        if (!string.IsNullOrEmpty(parsed.Path) && FirstSegment(parsed.Path) != "*")
        {
            var pinned = FirstSegment(parsed.Path).ToLowerInvariant();
            schemas = schemas.Where(s => string.Equals(s, pinned, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        _logger?.LogDebug(
            "[FanOut] schemas: count={Count} table={Table} joinTable={JoinTable} list=[{Schemas}]",
            schemas.Count, tableName, joinTable ?? "(none)", string.Join(", ", schemas));
        if (schemas.Count == 0)
            yield break;

        // Strip the wildcard from Path so the SQL where-clause doesn't try
        // to filter on `n.path LIKE '*/...'`. The fan-out itself is what
        // honours the wildcard semantic.
        var queryForSql = parsed;
        if (!string.IsNullOrEmpty(queryForSql.Path) && FirstSegment(queryForSql.Path) == "*")
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
    /// If the parsed query has a <c>nodeType:</c> filter whose type maps to
    /// a known satellite path suffix, return the satellite table name so the
    /// UNION targets the right table per schema. <see langword="null"/>
    /// means "use <c>mesh_nodes</c>".
    /// </summary>
    private static string? ResolveTableForNodeTypeFilter(ParsedQuery parsed)
    {
        var nodeType = parsed.ExtractNodeType();
        if (string.IsNullOrEmpty(nodeType)) return null;
        if (!PartitionDefinition.NodeTypeToSuffix.TryGetValue(nodeType, out var suffix))
            return null;
        return PartitionDefinition.StandardTableMappings.TryGetValue(suffix, out var table)
            ? table
            : null;
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
