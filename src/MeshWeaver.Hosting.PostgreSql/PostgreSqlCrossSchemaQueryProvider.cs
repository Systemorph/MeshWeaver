using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL implementation of ICrossSchemaQueryProvider.
/// Uses the search_across_schemas() stored procedure for single-query fan-out.
/// Schema list is maintained in public.searchable_schemas table.
/// </summary>
public class PostgreSqlCrossSchemaQueryProvider : ICrossSchemaQueryProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlSqlGenerator _sqlGenerator = new();
    private readonly ILogger? _logger;

    // SyncSearchableSchemasAsync throttle. PostgreSqlPartitionedMeshQuery
    // calls this once per cross-schema fan-out, which under thread-render
    // load is N times per page-load. Without throttling, each call does a
    // SELECT FROM information_schema + DELETE + N INSERTs on
    // public.searchable_schemas. Combined with MaxPoolSize=1 on the public
    // connection pool, writes pile up, the DELETE-then-INSERT window briefly
    // empties the table, and concurrent readers fall through to discover
    // schemas from information_schema directly (picking up empty schemas
    // like 'welcome'/'login' that have no mesh_nodes) → 42P01 cascade →
    // /authorize and thread-load deadlock. Prod incident 2026-05-20.
    private long _lastSyncTicks;
    private int _syncInFlight;

    /// <summary>
    /// Minimum interval between actual <see cref="SyncSearchableSchemasAsync(bool, CancellationToken)"/>
    /// runs. Calls within the window are no-ops. Internal setter for tests
    /// to force re-sync without waiting.
    /// </summary>
    internal TimeSpan SyncTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Test hook: number of times the actual sync work executed (vs returned
    /// early via the throttle). Used by the per-query-loop repro test.
    /// </summary>
    internal int ActualSyncCount;

    /// <summary>
    /// Initializes the cross-schema query provider.
    /// </summary>
    /// <param name="dataSource">The PostgreSQL data source used for schema discovery and fan-out queries.</param>
    /// <param name="logger">Optional logger for query and diagnostics output.</param>
    public PostgreSqlCrossSchemaQueryProvider(
        NpgsqlDataSource dataSource,
        ILogger<PostgreSqlCrossSchemaQueryProvider>? logger = null)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Schemas excluded from partition discovery — internal / system schemas
    /// that don't hold user content. Kept in sync with the now-deleted
    /// <c>PostgreSqlPartitionedStoreFactory.ExcludedSchemas</c>.
    /// </summary>
    private static readonly HashSet<string> ExcludedSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        // 'auth' is the central auth-lookup MIRROR (User/Group/Role/VUser/ApiToken replicated
        // there by the mesh_node_mirror_access_objects trigger). It must NOT participate in
        // cross-schema fan-out or every access object would surface twice — once from its
        // canonical partition and once from the auth mirror. Auth/onboarding middleware query
        // the 'auth' schema directly instead.
        "auth",
        "admin", "portal", "kernel",
        "_access", "_address_", "_graph", "_settings", "_tracking", "_thread", "_source", "_test",
        "source", "test",
        "login", "markdown", "onboarding", "welcome", "settings", "storage",
        // NOTE: 'agent' is NOT excluded. Since the per-partition agent-registry migration
        // (V36/V37) the `agent` schema is a REAL public catalog partition (publicRead, like
        // `skill`/`model`/`harness`) holding the platform agents. Excluding it kept the `agent`
        // schema out of `searchable_schemas`, so the multi-namespace registry fan-out
        // (`namespace:{user}/Agent|{space}/Agent|Agent`) never queried it → the chat agent
        // picker came back EMPTY for every user while models/skills worked (atioz 2026-06-20).
        // Single-namespace `namespace:Agent` masked it because that path is SCOPED (resolves the
        // schema directly, bypassing searchable_schemas).
        "p", "path", "mesh", "thread", "partition", "organization", "vuser",
        "public", "information_schema", "pg_catalog", "pg_toast"
    };

    /// <summary>
    /// Syncs the searchable_schemas table by querying information_schema for
    /// schemas that contain a <c>mesh_nodes</c> table. Same SQL the legacy
    /// factory's <c>DiscoverPartitionsAsync</c> ran; inlined here so this
    /// class is self-contained — no <c>PostgreSqlPartitionedStoreFactory</c>
    /// dependency.
    /// </summary>
    public Task SyncSearchableSchemasAsync(CancellationToken ct = default)
        => SyncSearchableSchemasAsync(force: false, ct);

    /// <inheritdoc />
    public async Task SyncSearchableSchemasAsync(bool force, CancellationToken ct = default)
    {
        // Fast path: another sync ran within SyncTtl — skip. New partitions
        // created in that window are invisible until the next sync, which is
        // an acceptable trade for not melting the connection pool. force=true
        // (the one-time boot self-heal) bypasses the TTL so a schema this boot's
        // import just provisioned is registered immediately, not up to SyncTtl later.
        var lastTicks = Interlocked.Read(ref _lastSyncTicks);
        if (!force && lastTicks != 0 && DateTime.UtcNow.Ticks - lastTicks < SyncTtl.Ticks)
            return;

        // Single-flight: only one sync runs at a time. Concurrent callers
        // (every cross-schema fan-out calls this) return immediately rather
        // than queuing on the public-schema connection. Honoured even under
        // force — force bypasses the TIME throttle, never the DELETE+INSERT
        // mutual-exclusion; a concurrent sync already rebuilds the registry.
        if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0)
            return;

        try
        {
            // Re-check under the flight gate: another caller may have just
            // finished while we were CAS-ing.
            lastTicks = Interlocked.Read(ref _lastSyncTicks);
            if (!force && lastTicks != 0 && DateTime.UtcNow.Ticks - lastTicks < SyncTtl.Ticks)
                return;

            var schemas = new List<string>();
            await using (var discoverCmd = _dataSource.CreateCommand("""
                SELECT schema_name
                FROM information_schema.schemata s
                WHERE EXISTS (
                    SELECT 1 FROM information_schema.tables t
                    WHERE t.table_schema = s.schema_name
                      AND t.table_name = 'mesh_nodes'
                )
                AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
                AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
                ORDER BY s.schema_name
                """))
            {
                await using var reader = await discoverCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var schema = reader.GetString(0);
                    if (!ExcludedSchemas.Contains(schema))
                        schemas.Add(schema);
                }
            }

            await using var cmd = _dataSource.CreateCommand(
                "DELETE FROM public.searchable_schemas; " +
                string.Join(" ", schemas.Select(s =>
                    $"INSERT INTO public.searchable_schemas (schema_name) VALUES ('{s.Replace("'", "''")}') ON CONFLICT DO NOTHING;")));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            // NOTE: public.top_level_index is NOT rebuilt here. Re-materializing a MATERIALIZED
            // VIEW (DROP+CREATE, ACCESS EXCLUSIVE) on the query hot path serializes every query
            // behind DDL and deadlocks under load. The matview is (re)built only on rare
            // partition-set changes — at schema-init/deploy and when a NEW partition schema is
            // first created (PostgreSqlPartitionStorageProvider) — never per query.

            Interlocked.Increment(ref ActualSyncCount);
            Interlocked.Exchange(ref _lastSyncTicks, DateTime.UtcNow.Ticks);
        }
        finally
        {
            Interlocked.Exchange(ref _syncInFlight, 0);
        }
    }

    /// <summary>
    /// Returns the subset of searchable schemas that actually contain
    /// <paramref name="tableName"/>. Use this before fanning out a UNION over
    /// a satellite table — older partitions / static-mesh schemas only have
    /// <c>mesh_nodes</c> (no <c>activities</c> / <c>threads</c> / <c>annotations</c>),
    /// and joining across them produces a <c>42P01 relation does not exist</c>.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSchemasWithTableAsync(
        string tableName, CancellationToken ct = default)
    {
        var schemas = await GetSearchableSchemasAsync(ct).ConfigureAwait(false);
        if (schemas.Count == 0 || string.IsNullOrEmpty(tableName))
            return schemas;

        var present = new List<string>(schemas.Count);
        await using var cmd = _dataSource.CreateCommand(
            $"""
            SELECT DISTINCT table_schema
            FROM information_schema.tables
            WHERE table_name = $1
              AND table_schema = ANY($2)
            """);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(schemas.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            present.Add(reader.GetString(0));
        return present;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetSearchableSchemasAsync(CancellationToken ct = default)
    {
        try
        {
            var schemas = new List<string>();
            await using var cmd = _dataSource.CreateCommand(
                "SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name");
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                schemas.Add(reader.GetString(0));

            // If empty (first run), sync from factory and retry
            if (schemas.Count == 0)
            {
                await SyncSearchableSchemasAsync(ct).ConfigureAwait(false);
                schemas.Clear();
                await using var cmd2 = _dataSource.CreateCommand(
                    "SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name");
                await using var reader2 = await cmd2.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader2.ReadAsync(ct).ConfigureAwait(false))
                    schemas.Add(reader2.GetString(0));
            }

            return schemas;
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load searchable schemas");
            return [];
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Build WHERE clause from parsed query (without access control or text search —
        // access control is handled by the stored proc, text search is added below with tsvector).
        // Strip TextSearch to avoid double ILIKE: GenerateWhereClause would add its own ILIKE,
        // and we add a tsvector-indexed search below.
        var queryForWhere = query with { TextSearch = null };
        var (whereClause, parameters) = _sqlGenerator.GenerateWhereClause(queryForWhere);

        // Strip "WHERE " prefix — the stored proc adds its own WHERE
        var filterClause = whereClause.StartsWith("WHERE ")
            ? whereClause[6..] : whereClause;

        // Add scope clause (e.g., namespace:'' → n.namespace = '' for root-level nodes)
        if (query.Path != null)
        {
            var (scopeClause, scopeParams) = _sqlGenerator.GenerateScopeClause(query.Path, query.Scope);
            if (!string.IsNullOrEmpty(scopeClause))
            {
                if (!string.IsNullOrEmpty(filterClause))
                    filterClause += " AND ";
                filterClause += scopeClause;
                foreach (var (k, v) in scopeParams)
                    parameters[k] = v;
            }
        }

        // Inline parameter values into the SQL string. Filter out null/empty keys
        // defensively — a malformed parameter (rare but observed in prod when an
        // unscoped query path adds a placeholder without a name) used to NRE here.
        foreach (var (name, value) in parameters
            .Where(p => !string.IsNullOrEmpty(p.Key))
            .OrderByDescending(p => p.Key.Length))
        {
            var sqlValue = value switch
            {
                string s => $"'{EscapeSql(s)}'",
                bool b => b ? "true" : "false",
                int i => i.ToString(),
                long l => l.ToString(),
                double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.ffffffzzz}'",
                _ => $"'{EscapeSql(value?.ToString() ?? "")}'"
            };
            filterClause = filterClause.Replace(name, sqlValue);
        }

        // Add text search if present — use tsvector for indexed word matching,
        // with ILIKE fallback for partial/path matches the tsvector misses.
        if (!string.IsNullOrEmpty(query.TextSearch))
        {
            var escaped = EscapeSql(query.TextSearch);
            // Split into words, each as prefix match (e.g., "Partner Re" → "Partner:* & Re:*")
            var words = escaped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var tsTerms = string.Join(" & ", words.Select(w => $"{w}:*"));

            var tsvectorExpr = "to_tsvector('english', COALESCE(n.name,'') || ' ' || COALESCE(n.description,'') || ' ' || COALESCE(n.node_type,''))";
            var ilikeFallback = $"COALESCE(n.name,'') || ' ' || n.path || ' ' || COALESCE(n.node_type,'')";

            if (!string.IsNullOrEmpty(filterClause))
                filterClause += " AND ";
            // tsvector uses the GIN index; ILIKE catches path/id matches
            filterClause += $"({tsvectorExpr} @@ to_tsquery('english', '{tsTerms}') OR {ilikeFallback} ILIKE '%{escaped}%')";
        }

        // Build ORDER BY
        var orderBy = query.OrderBy != null
            ? $"{PostgreSqlSqlGenerator.MapSelector(query.OrderBy.Property)} {(query.OrderBy.Descending ? "DESC" : "ASC")}"
            : "n.last_modified DESC";

        var limit = query.Limit ?? 50;

        _logger?.LogInformation(
            "[CrossSchema] search_across_schemas(where='{Where}', user='{User}', order='{Order}', limit={Limit})",
            filterClause, userId, orderBy, limit);

        // Call the stored procedure using named parameters
        await using var cmd = _dataSource.CreateCommand(
            "SELECT * FROM public.search_across_schemas(@p_where, @p_user, @p_order, @p_limit) " +
            "AS t(id TEXT, namespace TEXT, name TEXT, node_type TEXT, category TEXT, icon TEXT, " +
            "display_order INT, last_modified TIMESTAMPTZ, version BIGINT, state SMALLINT, " +
            "content JSONB, desired_id TEXT, main_node TEXT)");
        cmd.Parameters.Add(new NpgsqlParameter("@p_where", string.IsNullOrEmpty(filterClause) ? "" : filterClause));
        cmd.Parameters.Add(new NpgsqlParameter("@p_user", (object?)userId ?? DBNull.Value));
        cmd.Parameters.Add(new NpgsqlParameter("@p_order", orderBy));
        cmd.Parameters.Add(new NpgsqlParameter("@p_limit", limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return ReadMeshNode(reader, options);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<QueryResult>> AutocompleteTopLevelAsync(
        string prefix, string? userId, int limit, CancellationToken ct = default)
    {
        var results = new List<QueryResult>();
        try
        {
            // PG-side hybrid score: exact name > name-prefix > id-prefix > name-substring >
            // id-substring. ORDER BY score DESC (relevance, NOT alphabetical). Access-filtered
            // by partition_access (schema = lower(id)); @userId IS NULL = system (all). One
            // indexed matview read — no fan-out.
            await using var cmd = _dataSource.CreateCommand("""
                SELECT id, name, node_type, icon, path,
                  (CASE
                     WHEN @prefix = '' THEN 0
                     WHEN LOWER(COALESCE(name,'')) = LOWER(@prefix) THEN 1000
                     WHEN LOWER(COALESCE(name,'')) LIKE LOWER(@prefix) || '%' THEN 600
                     WHEN LOWER(id) LIKE LOWER(@prefix) || '%' THEN 500
                     WHEN LOWER(COALESCE(name,'')) LIKE '%' || LOWER(@prefix) || '%' THEN 300
                     WHEN LOWER(id) LIKE '%' || LOWER(@prefix) || '%' THEN 200
                     ELSE 0 END) AS score
                FROM public.top_level_index
                WHERE (@prefix = ''
                       OR LOWER(COALESCE(name,'')) LIKE '%' || LOWER(@prefix) || '%'
                       OR LOWER(id) LIKE '%' || LOWER(@prefix) || '%')
                  AND (@userId::text IS NULL
                       OR EXISTS (SELECT 1 FROM public.partition_access pa
                                  WHERE pa.user_id IN (@userId::text, 'Public') AND pa.partition = LOWER(id)))
                ORDER BY score DESC, name ASC NULLS LAST
                LIMIT @limit
                """);
            cmd.Parameters.Add(new NpgsqlParameter("@prefix", prefix ?? ""));
            cmd.Parameters.Add(new NpgsqlParameter("@userId", (object?)userId ?? DBNull.Value));
            cmd.Parameters.Add(new NpgsqlParameter("@limit", limit < 1 ? 10 : limit));

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var path = reader.GetString(reader.GetOrdinal("path"));
                results.Add(new QueryResult
                {
                    Path = path,
                    Name = reader.IsDBNull(reader.GetOrdinal("name")) ? path : reader.GetString(reader.GetOrdinal("name")),
                    NodeType = reader.IsDBNull(reader.GetOrdinal("node_type")) ? null : reader.GetString(reader.GetOrdinal("node_type")),
                    Icon = reader.IsDBNull(reader.GetOrdinal("icon")) ? null : reader.GetString(reader.GetOrdinal("icon")),
                    Score = reader.GetInt32(reader.GetOrdinal("score")),
                    ProviderName = nameof(PostgreSqlCrossSchemaQueryProvider),
                });
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // public.top_level_index not present yet (DB not migrated) — no top-level suggestions.
            _logger?.LogDebug("AutocompleteTopLevel: top_level_index unavailable ({Msg})", ex.Message);
        }
        return results;
    }

    private MeshNode ReadMeshNode(NpgsqlDataReader reader, JsonSerializerOptions options)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        var ns = reader.GetString(reader.GetOrdinal("namespace"));

        object? content = null;
        var contentOrd = reader.GetOrdinal("content");
        if (!reader.IsDBNull(contentOrd))
        {
            var json = reader.GetString(contentOrd);
            // A poisoned row (malformed polymorphic discriminator, an unknown
            // $type, etc.) must NOT take down the entire query. Skip the
            // content deserialization for THIS row only, leaving the MeshNode
            // skeleton intact so paths/names/timestamps still surface in the
            // cross-partition UNION result. Production repro: a Thread row
            // with `pendingUserMessages.{id}.$type` after the first property
            // → System.Text.Json polymorphic deserialiser throws "metadata
            // property must be first" → every Latest Threads fan-out hangs
            // in the Blazor loading spinner.
            try
            {
                content = JsonSerializer.Deserialize<object>(json, options);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[CrossSchema] Skipping content for poisoned row {Path}: {Error}",
                    string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}", ex.Message);
            }
        }

        return new MeshNode(id, string.IsNullOrEmpty(ns) ? null : ns)
        {
            Name = reader.IsDBNull(reader.GetOrdinal("name")) ? null : reader.GetString(reader.GetOrdinal("name")),
            NodeType = reader.IsDBNull(reader.GetOrdinal("node_type")) ? null : reader.GetString(reader.GetOrdinal("node_type")),
            Category = reader.IsDBNull(reader.GetOrdinal("category")) ? null : reader.GetString(reader.GetOrdinal("category")),
            Icon = reader.IsDBNull(reader.GetOrdinal("icon")) ? null : reader.GetString(reader.GetOrdinal("icon")),
            Order = reader.IsDBNull(reader.GetOrdinal("display_order")) ? null : reader.GetInt32(reader.GetOrdinal("display_order")),
            LastModified = new DateTimeOffset(reader.GetDateTime(reader.GetOrdinal("last_modified")), TimeSpan.Zero),
            Version = reader.GetInt64(reader.GetOrdinal("version")),
            State = (MeshNodeState)reader.GetInt16(reader.GetOrdinal("state")),
            SyncBehavior = PgMeshNodeReader.ReadSyncBehavior(reader),
            ExcludeFromContext = PgMeshNodeReader.ReadStringArray(reader, "exclude_from_context"),
            Content = content,
            DesiredId = reader.IsDBNull(reader.GetOrdinal("desired_id")) ? null : reader.GetString(reader.GetOrdinal("desired_id")),
            MainNode = reader.IsDBNull(reader.GetOrdinal("main_node"))
                ? (string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}")
                : reader.GetString(reader.GetOrdinal("main_node"))
        };
    }

    /// <inheritdoc />
    public IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string tableName,
        string? userId = null,
        CancellationToken ct = default)
        => QueryAcrossSchemasAsync(query, options, schemas, tableName, userId, activityUserId: null, ct);

    /// <summary>
    /// UNION-ALL fan-out across <paramref name="schemas"/> with optional
    /// <c>source:activity</c> / <c>source:accessed</c> JOIN support. When
    /// <paramref name="activityUserId"/> is non-null AND the query carries
    /// <see cref="QuerySource.Accessed"/>, each schema branch INNER JOINs the
    /// per-schema <c>user_activities</c> table by the user's
    /// <c>{user}/_UserActivity</c> namespace; when the query carries
    /// <see cref="QuerySource.Activity"/>, each branch INNER JOINs the
    /// per-schema <c>activities</c> table on <c>main_node</c>. The default
    /// sort becomes the joined satellite's <c>last_modified</c> DESC so the
    /// merged feed preserves "most recent activity first" across partitions.
    /// </summary>
    public async IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string tableName,
        string? userId,
        string? activityUserId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (schemas.Count == 0)
            yield break;

        // For a FREE-TEXT main-search omnibox query, fold indexed content into the SAME UNION: each
        // content-bearing schema contributes a content_chunks lexical branch projecting each file's best
        // chunk to its Document node (the cross-partition counterpart of the scoped vector UNION). Only
        // for the primary mesh_nodes projection (satellite/activity/accessed queries don't carry content)
        // and only when there's a term to match — a pure structured query adds nothing.
        IReadOnlyList<string>? contentSchemas = null;
        if (tableName == "mesh_nodes" && !string.IsNullOrEmpty(query.TextSearch))
            contentSchemas = await GetSchemasWithTableAsync("content_chunks", ct).ConfigureAwait(false);

        // source:accessed: the caller's access log lives in the CALLER's partition schema
        // ({user}/_UserActivity routes by its first segment — same seg.ToLowerInvariant() rule as
        // the path router), so every branch joins that ONE user_activities table. Joining each
        // branch's own table could never match a cross-partition access, which made the home's
        // "Last accessed" list empty outside the user's own partition. A caller without a
        // partition schema yields 42P01 → the existing missing-relation catch → empty (correct:
        // no access log, nothing accessed).
        var activityUserSchema = query.Source == QuerySource.Accessed && !string.IsNullOrEmpty(activityUserId)
            ? activityUserId!.ToLowerInvariant().Replace("\"", "\"\"")  // quoted-identifier escape
            : null;

        var generator = new PostgreSqlSqlGenerator();
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(
            query, schemas, userId, tableName, activityUserId, contentSchemas, activityUserSchema);

        _logger?.LogInformation(
            "[CrossSchema] Satellite query: table={Table}, schemas={Count}, contentSchemas={ContentCount}, userId={User}, source={Source}",
            tableName, schemas.Count, contentSchemas?.Count ?? 0, userId, query.Source);

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
            cmd.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));

        // "Relation does not exist" (42P01) — the satellite table hasn't been
        // created in one of the targeted schemas yet (typical for partition-
        // pinned satellite queries that race the lazy-create path, or for a
        // newly-discovered schema where CreateSatelliteTables hasn't run).
        // The error can surface at ExecuteReaderAsync (when PG eagerly plans)
        // or at the first ReadAsync (when PG defers). Catch at both seams and
        // treat as no rows; the next query will see the now-existing table
        // after the write commits.
        await foreach (var node in EnumerateReaderOrEmptyOnMissingRelationAsync(
            cmd, options, schemas, tableName, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return node;
        }
    }

    private async IAsyncEnumerable<MeshNode> EnumerateReaderOrEmptyOnMissingRelationAsync(
        Npgsql.NpgsqlCommand cmd,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string tableName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Npgsql.NpgsqlDataReader reader;
        try { reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false); }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger?.LogDebug(
                "[CrossSchema] Skipping satellite query — {Schemas} schemas missing {Table}: {Error}",
                schemas.Count, tableName, ex.Message);
            yield break;
        }

        await using var _disposeReader = reader;
        while (true)
        {
            bool hasNext;
            try { hasNext = await reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                _logger?.LogDebug(
                    "[CrossSchema] Skipping satellite query mid-stream — {Table} missing in some schema: {Error}",
                    tableName, ex.Message);
                yield break;
            }
            if (!hasNext) break;

            MeshNode? node;
            try { node = ReadMeshNode(reader, options); }
            catch (Exception ex)
            {
                // Per-row defence: a malformed reader value (corrupt vector,
                // unparseable timestamp, etc.) must not take down the entire
                // UNION. Log + skip.
                _logger?.LogWarning(ex,
                    "[CrossSchema] Skipping unreadable row in {Table}: {Error}",
                    tableName, ex.Message);
                continue;
            }
            yield return node;
        }
    }

    private static string EscapeSql(string input) =>
        input.Replace("'", "''");
}
