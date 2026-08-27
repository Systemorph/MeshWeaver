using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL implementation of ICrossSchemaQueryProvider — one UNION ALL across every partition
/// schema, generated in C# by <see cref="PostgreSqlSqlGenerator.GenerateCrossSchemaSelectQuery"/>.
/// The schema list is maintained in <c>public.searchable_schemas</c>.
///
/// <para>🚨 The <c>public.search_across_schemas(...)</c> stored function is NOT used here any more.
/// It backed a second fan-out shape that clipped an unlimited query at 50 rows, and no runtime
/// caller ever reached it — <c>PostgreSqlPartitionedMeshQuery.EnumerateFanOutAsync</c>, the path
/// for EVERY unpinned query, has only ever taken the table-name overload. It is deleted rather
/// than kept "in case paging is wanted": a silent default clip is the defect #1216/#1326/#1960
/// were filed against, and a SECOND access-control implementation that nothing exercises is a
/// place for a security fix to land on the wrong copy (#2048). The SQL function itself stays in
/// the schema — an older portal replica mid-rollout still calls it.</para>
/// </summary>
public class PostgreSqlCrossSchemaQueryProvider : ICrossSchemaQueryProvider
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger? _logger;

    /// <summary>
    /// Fan-out width of the LAST schema list read — how many partitions a cross-schema UNION spans.
    /// Only ever used to annotate the timing log, so a stale-by-one value is harmless; keeping it a
    /// plain int avoids putting a lock on the query path to serve a diagnostic.
    /// </summary>
    internal int _cachedSchemaCount;

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
        // picker came back EMPTY for every user while models/skills worked (prod 2026-06-20).
        // Single-namespace `namespace:Agent` masked it because that path is SCOPED (resolves the
        // schema directly, bypassing searchable_schemas).
        "p", "path", "mesh", "thread", "partition", "organization", "vuser",
        "public", "information_schema", "pg_catalog", "pg_toast"
    };

    /// <summary>
    /// True when <paramref name="schema"/> belongs in <c>public.searchable_schemas</c> — the SAME
    /// predicate the discovery sync applies, exposed so the ONE place that provisions a partition
    /// (<see cref="PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned"/>) can register
    /// the new schema in the registry immediately instead of waiting for the throttled poll.
    ///
    /// <para>🚨 It must stay the same predicate, not a second copy: registering an EXCLUDED schema
    /// (notably <c>auth</c>, the access-object mirror) would surface every access object twice in
    /// the cross-schema UNION until the next sync deleted it again.</para>
    /// </summary>
    internal static bool IsSearchableSchema(string? schema)
        => !string.IsNullOrEmpty(schema)
           && !ExcludedSchemas.Contains(schema)
           && !schema.EndsWith("_versions", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// The subset of <paramref name="candidates"/> that actually own <paramref name="tableName"/>.
    ///
    /// <para>Deliberately NOT <see cref="GetSchemasWithTableAsync"/>: that one starts from
    /// <c>public.searchable_schemas</c> and so can only ever return REGISTERED partitions. The
    /// fan-out's partition-pinned fast path builds a one-element schema list without consulting
    /// that registry, so intersecting with it silently classifies a perfectly good partition as
    /// "not access-controlled" and drops it from the union — granted cross-partition nodes vanish.
    /// Ask about exactly the schemas in play.</para>
    /// </summary>
    internal async Task<IReadOnlyList<string>> GetSchemasHavingTableAsync(
        IReadOnlyList<string> candidates, string tableName, CancellationToken ct = default)
    {
        if (candidates.Count == 0 || string.IsNullOrEmpty(tableName))
            return [];
        var present = new List<string>(candidates.Count);
        await using var cmd = _dataSource.CreateCommand(
            """
            SELECT DISTINCT table_schema
            FROM information_schema.tables
            WHERE table_name = $1
              AND table_schema = ANY($2)
            """);
        cmd.Parameters.AddWithValue(tableName);
        cmd.Parameters.AddWithValue(candidates.ToArray());
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

            _cachedSchemaCount = schemas.Count;
            return schemas;
        }
        catch (OperationCanceledException) { return []; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load searchable schemas");
            return [];
        }
    }


    /// <summary>Elapsed above which a cross-schema fan-out is logged as a Warning, not Debug.</summary>
    internal const long SlowFanOutMs = 1000;

    /// <summary>
    /// One line per cross-schema fan-out: total elapsed, time to the FIRST row, rows returned, the
    /// limit, and how many schemas the UNION spanned. Time-to-first-row separates a slow QUERY (the
    /// scan) from slow STREAMING (the caller's own consumption) — without it a slow log line is
    /// ambiguous and the usual next step is to blame the database.
    /// </summary>
    internal void LogFanOutTiming(string what, long totalMs, long firstRowMs, int rows, int limit)
    {
        var schemas = _cachedSchemaCount;
        if (totalMs >= SlowFanOutMs)
            _logger?.LogWarning(
                "[CrossSchema] SLOW {What}: {TotalMs}ms (first row {FirstMs}ms) — {Rows}/{Limit} rows across {Schemas} schema(s). " +
                "An unanchored query UNIONs every partition; add a path:/namespace: first segment to pin it to one schema.",
                what, totalMs, firstRowMs, rows, limit, schemas);
        else
            _logger?.LogDebug(
                "[CrossSchema] {What}: {TotalMs}ms (first row {FirstMs}ms) — {Rows}/{Limit} rows across {Schemas} schema(s).",
                what, totalMs, firstRowMs, rows, limit, schemas);
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
        => QueryAcrossSchemasAsync(query, options, schemas, tableName, userId,
            activityUserId: null, excludedNodeTypes: null, ct);

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
        IReadOnlyCollection<string>? excludedNodeTypes,
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

        // Which of THESE schemas actually carry `user_effective_permissions` — the only per-schema
        // relation the access clause names. A schema without it (a partition provisioned by an
        // older/foreign path, a static content import) turns the clause into a reference to a
        // missing relation, Postgres fails to PLAN the whole UNION with 42P01, and the reader below
        // absorbs 42P01 as "no rows" — so ONE such partition returned an empty result for every
        // authenticated user's unscoped query. Resolved directly against the schemas in play (NOT
        // via GetSchemasWithTableAsync, which intersects `searchable_schemas` and so would drop the
        // pinned fast path, whose schema is never registered there).
        var accessControlledSchemas = string.IsNullOrEmpty(userId)
            ? null
            : await GetSchemasHavingTableAsync(schemas, "user_effective_permissions", ct)
                .ConfigureAwait(false);

        var generator = new PostgreSqlSqlGenerator();
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(
            query, schemas, userId, tableName, activityUserId, contentSchemas, activityUserSchema,
            accessControlledSchemas, excludedNodeTypes);

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
        // ⏱️ TIMED, because this IS the expensive shape: an unanchored query UNIONs every row of
        // public.searchable_schemas, so its cost grows with the number of partitions on the mesh
        // and gets quietly slower over months with no code change. The fan-out width is logged
        // beside the elapsed time so a slow query says WHY it was slow — a WHERE-clause dump alone
        // cannot distinguish "bad filter" from "300 schemas".
        //
        // 🚨 It is measured HERE, on the overload every runtime fan-out takes
        // (PostgreSqlPartitionedMeshQuery.EnumerateFanOutAsync → this method). It used to sit on
        // the paging overload instead — which no request could reach — so the instrumentation for
        // the expensive shape produced not one line in production (#2048).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rows = 0;
        var firstRowMs = -1L;
        // 🚨 try/FINALLY, not a call after the loop. A caller that stops enumerating early — a
        // `.Take(n)`, a `break`, a cancellation, or a throw — disposes the iterator without ever
        // reaching code placed after the `await foreach`, and the slow fan-out would go UNLOGGED:
        // the one case this instrumentation exists to catch.
        try
        {
            await foreach (var node in EnumerateReaderOrEmptyOnMissingRelationAsync(
                cmd, options, schemas, tableName, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (rows++ == 0)
                    firstRowMs = sw.ElapsedMilliseconds;
                yield return node;
            }
        }
        finally
        {
            // `limit` is what the caller ASKED for, not a default this method applied — there is
            // none. `0` reads as "unbounded", which is the honest answer for an unpinned query
            // that stated no limit: every match comes back (#2048).
            LogFanOutTiming(
                tableName, sw.ElapsedMilliseconds, firstRowMs, rows,
                query.Limit is > 0 ? query.Limit.Value : 0);
        }
    }

    /// <summary>
    /// Bounded retry budget for a TRANSIENT fan-out fault — matches the storage adapter's read
    /// retry (<c>PostgreSqlStorageAdapter.MaxTransientReadRetries</c>), and for the same reason:
    /// short enough that four attempts stay well inside a render's budget.
    /// </summary>
    private const int MaxTransientQueryRetries = 3;

    /// <summary>
    /// Runs the command and streams its rows, distinguishing THREE outcomes that used to be two
    /// (issue #2132).
    ///
    /// <list type="number">
    ///   <item><b><c>42P01</c> undefined_table → empty, and that is AUTHORITATIVE.</b> The
    ///     satellite table genuinely does not exist in one of the targeted schemas, so there are
    ///     genuinely no rows. Unchanged.</item>
    ///   <item><b>A TRANSIENT fault → retry, bounded.</b> An <c>NpgsqlException</c> wrapping
    ///     <c>EndOfStreamException</c> ("Attempted to read past the end of the stream" — the
    ///     connection dropped mid-read) or <c>TimeoutException</c>, and <c>PostgresException</c>
    ///     <c>40P01</c>/<c>40001</c>, are momentary conditions that a re-run resolves. These were
    ///     the 171 occurrences across every memex-portal replica that put an error panel on
    ///     Timeline / Comments / Preview / Catalog.</item>
    ///   <item><b>Anything else, and an exhausted transient → PROPAGATE.</b></item>
    /// </list>
    ///
    /// <para>🚨 Widening the <c>42P01</c> catch to swallow a transient as "no rows" — the obvious
    /// way to stop the area failing — is the ONE thing this must not do. It would hand the view a
    /// well-formed EMPTY result that is indistinguishable from an authoritative one, so a
    /// momentary connection blip would silently render "no comments", "no timeline entries", "the
    /// catalog is empty" as fact. A failure the user can see (LayoutAreaHost already renders an
    /// error control into the failed area) is strictly better than a lie; the cure for the blip is
    /// the retry above, not the swallow.</para>
    ///
    /// <para>The retry is only ever taken BEFORE the first row is yielded. Once a row has left this
    /// method a re-execution would duplicate it, so a mid-stream fault past that point propagates
    /// even when it is transient.</para>
    ///
    /// <para>🚨 ONE <c>attempts</c> counter spans BOTH legs (open and stream), rather than a budget
    /// per leg. Nested budgets MULTIPLY — 4 opens × 4 reads — which is how a "bounded" retry quietly
    /// becomes a 16-attempt, multi-second stall inside a render. The bound here is exactly
    /// <see cref="MaxTransientQueryRetries"/> retries in total, however they are distributed.</para>
    /// </summary>
    private async IAsyncEnumerable<MeshNode> EnumerateReaderOrEmptyOnMissingRelationAsync(
        Npgsql.NpgsqlCommand cmd,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string tableName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var yieldedAny = false;
        var attempts = 0;
        while (true)
        {
            Npgsql.NpgsqlDataReader? reader = null;
            var retryAfter = TimeSpan.Zero;
            try { reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false); }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
            {
                _logger?.LogDebug(
                    "[CrossSchema] Skipping satellite query — {Schemas} schemas missing {Table}: {Error}",
                    schemas.Count, tableName, ex.Message);
                yield break;
            }
            catch (Exception ex) when (PostgreSqlStorageAdapter.IsTransientConnectionFault(ex)
                                       && attempts < MaxTransientQueryRetries)
            {
                retryAfter = PostgreSqlStorageAdapter.TransientReadBackoff(attempts);
                LogTransientRetry(ex, tableName, attempts++, retryAfter);
            }

            if (reader is null)
            {
                await Task.Delay(retryAfter, ct).ConfigureAwait(false);
                continue;
            }

            // Streaming leg. `restart` is set only by a transient fault that arrived before any row
            // was yielded — the one case a re-execution cannot duplicate anything.
            var restart = false;
            await using (reader)
            {
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
                    catch (Exception ex) when (!yieldedAny
                                               && PostgreSqlStorageAdapter.IsTransientConnectionFault(ex)
                                               && attempts < MaxTransientQueryRetries)
                    {
                        retryAfter = PostgreSqlStorageAdapter.TransientReadBackoff(attempts);
                        LogTransientRetry(ex, tableName, attempts++, retryAfter);
                        restart = true;
                        break;
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
                    yieldedAny = true;
                    yield return node;
                }
            }

            if (!restart) yield break;
            await Task.Delay(retryAfter, ct).ConfigureAwait(false);
        }
    }

    private void LogTransientRetry(Exception ex, string what, int attempt, TimeSpan delay) =>
        _logger?.LogWarning(ex,
            "[CrossSchema] transient DB fault on {What} fan-out, attempt {Attempt}/{Max}, retrying in {Delay}ms",
            what, attempt + 1, MaxTransientQueryRetries, delay.TotalMilliseconds);

    /// <summary>
    /// Opens the command's reader, retrying ONLY on
    /// <see cref="PostgreSqlStorageAdapter.IsTransientConnectionFault"/> (dropped connection,
    /// read timeout, <c>40P01</c>/<c>40001</c>) up to <see cref="MaxTransientQueryRetries"/> times
    /// with the same jittered backoff the storage adapter's reads use. Every other error —
    /// <c>42P01</c> included — propagates untouched to the caller's own handling, so this can
    /// never turn a real fault into an empty result. Safe by construction: no row has been read
    /// yet, so a re-execution cannot duplicate anything.
    /// </summary>
    private async Task<Npgsql.NpgsqlDataReader> ExecuteReaderWithTransientRetryAsync(
        Npgsql.NpgsqlCommand cmd, string what, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan retryAfter;
            try { return await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (PostgreSqlStorageAdapter.IsTransientConnectionFault(ex)
                                       && attempt < MaxTransientQueryRetries)
            {
                retryAfter = PostgreSqlStorageAdapter.TransientReadBackoff(attempt);
                LogTransientRetry(ex, what, attempt, retryAfter);
            }
            await Task.Delay(retryAfter, ct).ConfigureAwait(false);
        }
    }
}
