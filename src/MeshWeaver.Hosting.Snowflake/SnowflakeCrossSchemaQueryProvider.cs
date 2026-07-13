using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Snowflake implementation of <see cref="ICrossSchemaQueryProvider"/> — the port of
/// <c>PostgreSqlCrossSchemaQueryProvider</c>. The schema list is maintained in the central
/// <c>searchable_schemas</c> table (in <see cref="SnowflakeStorageOptions.Schema"/>, default
/// <c>public</c>), exactly like PG.
/// <para><b>No stored procedure.</b> PG's primary fan-out delegates to the
/// <c>public.search_across_schemas(...)</c> plpgsql function; Snowflake has no such routine, so
/// ALL THREE <c>QueryAcrossSchemasAsync</c> overloads generate the UNION in C# via
/// <see cref="SnowflakeSqlGenerator.GenerateCrossSchemaSelectQuery"/>. The stored proc's implicit
/// behaviors are reproduced explicitly by the primary overload (see its remarks): the
/// <c>main_node = path</c> branch filter, the <c>last_modified DESC</c> / <c>LIMIT 50</c>
/// defaults, the empty-registry short-circuit, and the per-schema <c>to_regclass</c>
/// access-control guard — the latter replaced by <see cref="GetSchemasWithAclTablesAsync"/>,
/// ONE information_schema probe feeding the generator's <c>aclSchemas</c> set.</para>
/// <para><b>Dialect</b>: every identifier is double-quoted lowercase via
/// <see cref="SnowflakeIdentifiers"/>, EXCEPT information_schema references, which are
/// deliberately emitted UNQUOTED — Snowflake uppercases unquoted identifiers, which is exactly
/// what resolves the uppercase catalog objects (quoting them lowercase would miss the catalog).
/// Parameters bind as <c>:name</c> via <see cref="SnowflakeConnectionSource.AddParam"/>;
/// timestamps are <c>TIMESTAMP_NTZ</c> storing UTC. Rows materialize through THE one reader,
/// <see cref="SnowflakeMeshNodeReader"/>.</para>
/// <para>All members are <see cref="Task"/>/<see cref="IAsyncEnumerable{T}"/>-shaped I/O leaves
/// per the contract — callers run them inside an <c>IIoPool</c> invoke, never on a hub
/// scheduler. The SQL generator is created per call (the generator carries per-generation
/// parameter state; PG's shared-instance field was a latent concurrency hazard its own
/// satellite overload already avoided).</para>
/// </summary>
public class SnowflakeCrossSchemaQueryProvider : ICrossSchemaQueryProvider
{
    private readonly SnowflakeConnectionSource _source;
    private readonly ILogger? _logger;

    /// <summary>Central schema holding <c>searchable_schemas</c> / <c>top_level_index</c> / <c>partition_access</c>.</summary>
    private readonly string _centralSchema;

    // SyncSearchableSchemasAsync throttle. The partitioned mesh query calls this once per
    // cross-schema fan-out, which under thread-render load is N times per page-load. Without
    // throttling, each call does a SELECT FROM information_schema + DELETE + INSERT on the
    // central searchable_schemas. In the PG backend (MaxPoolSize=1 on the public connection
    // pool) writes piled up, the DELETE-then-INSERT window briefly emptied the table, and
    // concurrent readers fell through to discover schemas from information_schema directly
    // (picking up empty schemas like 'welcome'/'login' that have no mesh_nodes) → undefined-
    // relation cascade → /authorize and thread-load deadlock. Prod incident 2026-05-20.
    // Ported verbatim: the failure shape is engine-independent.
    private long _lastSyncTicks;
    private int _syncInFlight;

    /// <summary>
    /// Minimum interval between actual <see cref="SyncSearchableSchemasAsync"/>
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
    /// <param name="source">The one place that opens Snowflake connections (the role <c>NpgsqlDataSource</c> plays for the PG provider).</param>
    /// <param name="logger">Optional logger for query and diagnostics output.</param>
    /// <param name="options">Storage options; <see cref="SnowflakeStorageOptions.Schema"/> locates the central tables (default <c>public</c>).</param>
    public SnowflakeCrossSchemaQueryProvider(
        SnowflakeConnectionSource source,
        ILogger<SnowflakeCrossSchemaQueryProvider>? logger = null,
        SnowflakeStorageOptions? options = null)
    {
        _source = source;
        _logger = logger;
        _centralSchema = options?.Schema ?? "public";
    }

    /// <summary>
    /// Schemas excluded from partition discovery — internal / system schemas
    /// that don't hold user content. Kept in sync with the PG provider's set
    /// (contents identical; the <c>pg_*</c> entries are retained for byte-parity —
    /// they never appear in a Snowflake catalog, so they are harmless).
    /// Immutable constant lookup, not a cache.
    /// </summary>
    private static readonly FrozenSet<string> ExcludedSchemas = new[]
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
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quoted two-part reference to a central table (in <see cref="_centralSchema"/>).</summary>
    private string CentralTable(string table) => SnowflakeIdentifiers.Qualify(_centralSchema, table);

    /// <summary>
    /// Syncs the searchable_schemas table by querying information_schema for
    /// schemas that contain a <c>mesh_nodes</c> table — the same discovery the PG
    /// provider inlined from the legacy factory's <c>DiscoverPartitionsAsync</c>,
    /// so this class is self-contained. Throttled by <see cref="SyncTtl"/> with an
    /// <see cref="Interlocked"/> single-flight gate (no semaphore — see the field
    /// comment for the incident this prevents).
    /// </summary>
    public async Task SyncSearchableSchemasAsync(CancellationToken ct = default)
    {
        // Fast path: another sync ran within SyncTtl — skip. New partitions
        // created in that window are invisible until the next sync, which is
        // an acceptable trade for not melting the connection pool.
        var lastTicks = Interlocked.Read(ref _lastSyncTicks);
        if (lastTicks != 0 && DateTime.UtcNow.Ticks - lastTicks < SyncTtl.Ticks)
            return;

        // Single-flight: only one sync runs at a time. Concurrent callers
        // (every cross-schema fan-out calls this) return immediately rather
        // than queuing on the central-schema connection.
        if (Interlocked.CompareExchange(ref _syncInFlight, 1, 0) != 0)
            return;

        try
        {
            // Re-check under the flight gate: another caller may have just
            // finished while we were CAS-ing.
            lastTicks = Interlocked.Read(ref _lastSyncTicks);
            if (lastTicks != 0 && DateTime.UtcNow.Ticks - lastTicks < SyncTtl.Ticks)
                return;

            await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

            var discovered = ImmutableList.CreateBuilder<string>();
            await using (var discoverCmd = connection.CreateCommand())
            {
                // information_schema identifiers are deliberately emitted UNQUOTED — the ONE
                // sanctioned exception to this backend's always-quote-lowercase rule: Snowflake
                // uppercases unquoted identifiers, which is exactly what resolves the uppercase
                // catalog objects (INFORMATION_SCHEMA.SCHEMATA / TABLES); quoting them lowercase
                // would MISS the catalog. The compared VALUES stay case-exact ('mesh_nodes' and
                // lowercase schema names) because partition schemas/tables are created
                // quoted-lowercase. Predicates mirror the PG discovery: has a mesh_nodes table,
                // not a system/central schema, not a *_versions history schema. Two dialect
                // adaptations: the NOT IN is LOWER()ed (Snowflake's built-in PUBLIC /
                // INFORMATION_SCHEMA names surface UPPERCASE, unlike PG's lowercase catalog),
                // and the LIKE escape is declared explicitly ('\\' in the SQL text = one
                // backslash — Snowflake has no default escape character; PG defaults to it).
                discoverCmd.CommandText = """
                    SELECT schema_name
                    FROM information_schema.schemata s
                    WHERE EXISTS (
                        SELECT 1 FROM information_schema.tables t
                        WHERE t.table_schema = s.schema_name
                          AND t.table_name = 'mesh_nodes'
                    )
                    AND LOWER(s.schema_name) NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
                    AND s.schema_name NOT LIKE '%\\_versions' ESCAPE '\\'
                    ORDER BY s.schema_name
                    """;
                await using var reader = await discoverCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var schema = reader.GetString(0);
                    if (!ExcludedSchemas.Contains(schema))
                        discovered.Add(schema);
                }
            }

            // Dedupe in C# — replaces PG's per-row ON CONFLICT DO NOTHING (schema_name is the
            // PK; Ordinal matches the PK's case-sensitive TEXT semantics). Catalog rows are
            // already distinct, so this is belt-and-braces.
            var schemas = discovered.Distinct(StringComparer.Ordinal).ToImmutableList();

            // DELETE + one multi-row INSERT, wrapped in an explicit transaction: PG batched
            // both into a single command (one implicit transaction); Snowflake auto-commits
            // per statement, so without the transaction a concurrent reader could observe the
            // empty table mid-swap — exactly the fall-through window of the 2026-05-20
            // incident described on the throttle fields.
            await using (var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                await using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = $"DELETE FROM {CentralTable("searchable_schemas")}";
                    await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (schemas.Count > 0)
                {
                    await using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = tx;
                    // Inlined quote-escaped literals, like PG's INSERT batch — the values come
                    // straight from the catalog and are additionally escaped here.
                    insertCmd.CommandText =
                        $"INSERT INTO {CentralTable("searchable_schemas")} (\"schema_name\") VALUES " +
                        string.Join(", ", schemas.Select(s => $"('{s.Replace("'", "''")}')"));
                    await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            // NOTE: top_level_index is NOT rebuilt here. Swapping the index (CREATE OR REPLACE
            // TABLE) on the query hot path would serialize every query behind DDL — the same
            // reason PG never re-materializes its matview per query. The index is (re)built only
            // on rare partition-set changes, by SnowflakeSearchInfrastructure (schema init /
            // partition provisioning) — never per query.

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
    /// and joining across them produces Snowflake's "object does not exist" error
    /// (the twin of PG's <c>42P01</c>).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSchemasWithTableAsync(
        string tableName, CancellationToken ct = default)
    {
        var schemas = await GetSearchableSchemasAsync(ct).ConfigureAwait(false);
        if (schemas.Count == 0 || string.IsNullOrEmpty(tableName))
            return schemas;

        return await FilterSchemasContainingTableAsync(schemas, tableName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the subset of <paramref name="schemas"/> that carry a
    /// <c>user_effective_permissions</c> table — the schemas the SQL generator may emit
    /// access-control predicates for (its <c>aclSchemas</c> argument). Schemas outside the
    /// set are PUBLIC content (e.g. the mirrored documentation) that ship <c>mesh_nodes</c>
    /// WITHOUT the per-partition permission tables; referencing those missing relations would
    /// fail the whole UNION. Replaces the PG stored proc's per-schema <c>to_regclass</c>
    /// existence guard with ONE information_schema query. Deliberately NOT cached: the PG
    /// proc re-evaluated <c>to_regclass</c> on every call too, so a freshly provisioned
    /// partition's permission tables take effect on the very next query — a positive cache
    /// would be a behavior change, not a port.
    /// </summary>
    /// <param name="schemas">The candidate fan-out schemas.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async Task<IReadOnlyList<string>> GetSchemasWithAclTablesAsync(
        IReadOnlyList<string> schemas, CancellationToken ct)
    {
        if (schemas.Count == 0)
            return ImmutableList<string>.Empty;

        return await FilterSchemasContainingTableAsync(schemas, "user_effective_permissions", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared catalog probe behind <see cref="GetSchemasWithTableAsync"/> and
    /// <see cref="GetSchemasWithAclTablesAsync"/>: ONE information_schema query for the subset
    /// of <paramref name="schemas"/> containing <paramref name="tableName"/>. PG binds the
    /// schema list as <c>= ANY($2)</c>; this dialect has no array binds, so the list expands
    /// to an IN-list of individual <c>:sN</c> markers. information_schema identifiers are
    /// deliberately UNQUOTED (see <see cref="SyncSearchableSchemasAsync"/> for why).
    /// </summary>
    private async Task<IReadOnlyList<string>> FilterSchemasContainingTableAsync(
        IReadOnlyList<string> schemas, string tableName, CancellationToken ct)
    {
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        var markers = new string[schemas.Count];
        for (var i = 0; i < schemas.Count; i++)
            markers[i] = ":s" + i;

        cmd.CommandText =
            "SELECT DISTINCT table_schema FROM information_schema.tables " +
            $"WHERE table_name = :tableName AND table_schema IN ({string.Join(", ", markers)})";
        SnowflakeConnectionSource.AddParam(cmd, "tableName", tableName, DbType.String);
        for (var i = 0; i < schemas.Count; i++)
            SnowflakeConnectionSource.AddParam(cmd, "s" + i, schemas[i], DbType.String);

        var present = ImmutableList.CreateBuilder<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            present.Add(reader.GetString(0));
        return present.ToImmutable();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetSearchableSchemasAsync(CancellationToken ct = default)
    {
        try
        {
            var schemas = await ReadSearchableSchemasOnceAsync(ct).ConfigureAwait(false);

            // If empty (first run), sync from information_schema and retry.
            if (schemas.Count == 0)
            {
                await SyncSearchableSchemasAsync(ct).ConfigureAwait(false);
                schemas = await ReadSearchableSchemasOnceAsync(ct).ConfigureAwait(false);
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

    /// <summary>One read of the central <c>searchable_schemas</c> registry, ordered by name.</summary>
    private async Task<IReadOnlyList<string>> ReadSearchableSchemasOnceAsync(CancellationToken ct)
    {
        var schemas = ImmutableList.CreateBuilder<string>();
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"SELECT \"schema_name\" FROM {CentralTable("searchable_schemas")} ORDER BY \"schema_name\"";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            schemas.Add(reader.GetString(0));
        return schemas.ToImmutable();
    }

    /// <inheritdoc />
    /// <remarks>
    /// PG delegates this overload to the <c>public.search_across_schemas(...)</c> stored
    /// procedure; here the SAME UNION is generated in C# via
    /// <see cref="SnowflakeSqlGenerator.GenerateCrossSchemaSelectQuery"/>, reproducing the
    /// proc's implicit behaviors explicitly:
    /// <list type="bullet">
    ///   <item><b><c>WHERE n.main_node = n.path</c> on every branch</b> — reproduced by passing
    ///     <c>IsMain = true</c> on the <see cref="ParsedQuery"/> (the generator emits exactly
    ///     that predicate).</item>
    ///   <item><b><c>ORDER BY last_modified DESC</c> default</b> — reproduced via an
    ///     <see cref="OrderByClause"/> fallback when the query has no explicit sort. Deliberate
    ///     deviation: a FREE-TEXT query without an explicit sort keeps the generator's
    ///     relevance-score ordering (which ends in the same <c>last_modified DESC</c>
    ///     tiebreaker) instead of the proc's flat recency sort — the tsvector ranking PG's
    ///     provider layered on top does not exist here, and the ILIKE relevance ladder is its
    ///     designated replacement (see the generator's full-text dialect note).</item>
    ///   <item><b><c>LIMIT 50</c> default</b> — reproduced via a <c>Limit</c> fallback.</item>
    ///   <item><b>Empty registry → no rows</b> — the proc returned nothing when
    ///     <c>searchable_schemas</c> was empty; reproduced by the empty-<paramref name="schemas"/>
    ///     short-circuit. (The proc re-read the registry itself and ignored the caller's list;
    ///     here the caller-supplied list — sourced from <see cref="GetSearchableSchemasAsync"/> —
    ///     drives the UNION.)</item>
    ///   <item><b>Per-schema <c>to_regclass</c> ACL guard</b> — replaced by
    ///     <see cref="GetSchemasWithAclTablesAsync"/> feeding the generator's
    ///     <c>aclSchemas</c> set (skipped entirely for system access, where the proc emitted no
    ///     ACL SQL either).</item>
    /// </list>
    /// The filter/scope/text predicates the PG provider inlined into <c>p_where_clause</c> are
    /// the generator's own <c>GenerateWhereClause</c>/scope push-down here — parameters stay
    /// BOUND instead of PG's string-inlined values. Content-chunk omnibox branches are NOT
    /// added on this overload (the proc had none; parity with PG, which folds content only in
    /// the table-name overload).
    /// </remarks>
    public async IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (schemas.Count == 0)
            yield break;

        var effectiveQuery = query with
        {
            IsMain = true,
            Limit = query.Limit ?? 50,
            OrderBy = query.OrderBy ?? (string.IsNullOrEmpty(query.TextSearch)
                ? new OrderByClause("last_modified", Descending: true)
                : null)
        };

        var aclSchemas = await GetAclSchemasOrEmptyAsync(schemas, userId, ct).ConfigureAwait(false);

        var generator = new SnowflakeSqlGenerator();
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(
            effectiveQuery, schemas, aclSchemas, userId);

        _logger?.LogInformation(
            "[CrossSchema] Cross-schema query: schemas={Count}, aclSchemas={AclCount}, userId={User}, order={Order}, limit={Limit}",
            schemas.Count, aclSchemas.Count, userId, effectiveQuery.OrderBy?.Property, effectiveQuery.Limit);

        await foreach (var node in EnumerateReaderOrEmptyOnMissingObjectAsync(
            sql, parameters, options, schemas, "mesh_nodes", ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return node;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<QueryResult>> AutocompleteTopLevelAsync(
        string prefix, string? userId, int limit, CancellationToken ct = default)
    {
        var results = ImmutableList.CreateBuilder<QueryResult>();
        try
        {
            await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();

            // Server-side hybrid score, identical ladder to PG: exact name > name-prefix >
            // id-prefix > name-substring > id-substring. ORDER BY score DESC (relevance, NOT
            // alphabetical). Access-filtered by the central partition_access (schema =
            // lower(id)); :userId IS NULL = system (sees all). One indexed-lookup-sized read of
            // top_level_index — a PLAIN TABLE here, rebuilt by SnowflakeSearchInfrastructure
            // (PG: a materialized view) — never a cross-schema fan-out, so it can't drain the
            // connection pool. Dialect notes: PG's `@userId::text` casts vanish (parameters are
            // typed by the bind), and LIMIT is inlined (int-typed, no injection surface) —
            // driver-side LIMIT binding is unverified and the SQL generator inlines LIMIT for
            // the same reason.
            var effectiveLimit = limit < 1 ? 10 : limit;
            cmd.CommandText = $"""
                SELECT "id", "name", "node_type", "icon", "path",
                  (CASE
                     WHEN :prefix = '' THEN 0
                     WHEN LOWER(COALESCE("name",'')) = LOWER(:prefix) THEN 1000
                     WHEN LOWER(COALESCE("name",'')) LIKE LOWER(:prefix) || '%' THEN 600
                     WHEN LOWER("id") LIKE LOWER(:prefix) || '%' THEN 500
                     WHEN LOWER(COALESCE("name",'')) LIKE '%' || LOWER(:prefix) || '%' THEN 300
                     WHEN LOWER("id") LIKE '%' || LOWER(:prefix) || '%' THEN 200
                     ELSE 0 END) AS "score"
                FROM {CentralTable("top_level_index")}
                WHERE (:prefix = ''
                       OR LOWER(COALESCE("name",'')) LIKE '%' || LOWER(:prefix) || '%'
                       OR LOWER("id") LIKE '%' || LOWER(:prefix) || '%')
                  AND (:userId IS NULL
                       OR EXISTS (SELECT 1 FROM {CentralTable("partition_access")} pa
                                  WHERE pa."user_id" IN (:userId, 'Public') AND pa."partition" = LOWER("id")))
                ORDER BY "score" DESC, "name" ASC NULLS LAST
                LIMIT {effectiveLimit}
                """;
            SnowflakeConnectionSource.AddParam(cmd, "prefix", prefix ?? "", DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "userId", userId, DbType.String);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var pathOrd = GetOrdinalIgnoreCase(reader, "path");
            var nameOrd = GetOrdinalIgnoreCase(reader, "name");
            var typeOrd = GetOrdinalIgnoreCase(reader, "node_type");
            var iconOrd = GetOrdinalIgnoreCase(reader, "icon");
            var scoreOrd = GetOrdinalIgnoreCase(reader, "score");
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var path = reader.GetString(pathOrd);
                results.Add(new QueryResult
                {
                    Path = path,
                    Name = reader.IsDBNull(nameOrd) ? path : reader.GetString(nameOrd),
                    NodeType = reader.IsDBNull(typeOrd) ? null : reader.GetString(typeOrd),
                    Icon = reader.IsDBNull(iconOrd) ? null : reader.GetString(iconOrd),
                    // Snowflake NUMBER may surface as long or decimal depending on the
                    // driver's precision metadata — coerce instead of GetInt32.
                    Score = Convert.ToDouble(reader.GetValue(scoreOrd), CultureInfo.InvariantCulture),
                    ProviderName = nameof(SnowflakeCrossSchemaQueryProvider),
                });
            }
        }
        catch (Exception ex) when (SnowflakeStorageAdapter.IsUndefinedObject(ex))
        {
            // top_level_index not present yet (backend not initialized) — no top-level
            // suggestions. Error 2003 is the twin of PG's 42P01 catch here.
            _logger?.LogDebug("AutocompleteTopLevel: top_level_index unavailable ({Msg})", ex.Message);
        }
        return results.ToImmutable();
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
    /// Unlike PG (whose generator emits access-control SQL for every schema), the
    /// Snowflake generator requires the provisioned ACL-schema subset explicitly —
    /// supplied here by <see cref="GetSchemasWithAclTablesAsync"/>.
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

        var aclSchemas = await GetAclSchemasOrEmptyAsync(schemas, userId, ct).ConfigureAwait(false);

        var generator = new SnowflakeSqlGenerator();
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(
            query, schemas, aclSchemas, userId, tableName, activityUserId, contentSchemas);

        _logger?.LogInformation(
            "[CrossSchema] Satellite query: table={Table}, schemas={Count}, contentSchemas={ContentCount}, userId={User}, source={Source}",
            tableName, schemas.Count, contentSchemas?.Count ?? 0, userId, query.Source);

        // "Object does not exist" — the satellite table hasn't been created in one of the
        // targeted schemas yet (typical for partition-pinned satellite queries that race the
        // lazy-create path, or for a newly-discovered schema where the satellite DDL hasn't
        // run). The error can surface at ExecuteReaderAsync (eager planning) or at the first
        // ReadAsync (deferred). Caught at both seams inside the enumerator and treated as no
        // rows; the next query will see the now-existing table after the write commits.
        await foreach (var node in EnumerateReaderOrEmptyOnMissingObjectAsync(
            sql, parameters, options, schemas, tableName, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            yield return node;
        }
    }

    /// <summary>
    /// The ACL-schema set for the generator: empty for system access (no
    /// <paramref name="userId"/> → the generator emits no access-control SQL, exactly like the
    /// PG proc's <c>user_list IS NULL</c> branch — no catalog round-trip wasted), else the
    /// <see cref="GetSchemasWithAclTablesAsync"/> probe over <paramref name="schemas"/>.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> GetAclSchemasOrEmptyAsync(
        IReadOnlyList<string> schemas, string? userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userId))
            return ImmutableList<string>.Empty;
        return await GetSchemasWithAclTablesAsync(schemas, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the generated UNION and streams materialized nodes, tolerating Snowflake's
    /// "object does not exist" (<see cref="SnowflakeStorageAdapter.IsUndefinedObject"/> — the
    /// twin of PG's <c>42P01</c>) at BOTH seams where it can surface — <c>ExecuteReaderAsync</c>
    /// and <c>ReadAsync</c> — by ending the stream empty, mirroring PG's
    /// <c>EnumerateReaderOrEmptyOnMissingRelationAsync</c>. Also applies the per-row defence:
    /// one malformed row (corrupt value, unparseable timestamp) is logged and skipped instead
    /// of faulting the whole UNION. Owns the connection for the lifetime of the enumeration.
    /// </summary>
    /// <param name="sql">The generated UNION statement.</param>
    /// <param name="parameters">Generator parameter map (bare-name keys, SQL references <c>:name</c>).</param>
    /// <param name="options">Serializer options for content deserialization.</param>
    /// <param name="schemas">The fanned-out schemas (diagnostics only).</param>
    /// <param name="tableName">The per-schema table queried (diagnostics only).</param>
    /// <param name="ct">Cancellation token.</param>
    private async IAsyncEnumerable<MeshNode> EnumerateReaderOrEmptyOnMissingObjectAsync(
        string sql,
        Dictionary<string, object> parameters,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string tableName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            BindGeneratedParameter(cmd, name, value);

        DbDataReader reader;
        try { reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (SnowflakeStorageAdapter.IsUndefinedObject(ex))
        {
            _logger?.LogDebug(
                "[CrossSchema] Skipping cross-schema query — {Schemas} schemas missing {Table}: {Error}",
                schemas.Count, tableName, ex.Message);
            yield break;
        }

        await using var _disposeReader = reader;
        while (true)
        {
            bool hasNext;
            try { hasNext = await reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (SnowflakeStorageAdapter.IsUndefinedObject(ex))
            {
                _logger?.LogDebug(
                    "[CrossSchema] Skipping cross-schema query mid-stream — {Table} missing in some schema: {Error}",
                    tableName, ex.Message);
                yield break;
            }
            if (!hasNext) break;

            MeshNode? node;
            try { node = SnowflakeMeshNodeReader.ReadMeshNode(reader, options, _logger); }
            catch (Exception ex)
            {
                // Per-row defence: a malformed reader value must not take down the entire
                // UNION. Log + skip. (Poisoned CONTENT is already degraded to null inside
                // ReadMeshNode; this catches defects in the non-content columns.)
                _logger?.LogWarning(ex,
                    "[CrossSchema] Skipping unreadable row in {Table}: {Error}",
                    tableName, ex.Message);
                continue;
            }
            yield return node;
        }
    }

    /// <summary>
    /// Binds one generator-produced parameter onto <paramref name="command"/>, mirroring the
    /// storage adapter's convention: the key's sigil (if any) is stripped to the bare name the
    /// driver registers (SQL references <c>:name</c>), and the <see cref="DbType"/> is inferred
    /// from the CLR value. <see cref="DateTimeOffset"/> binds its
    /// <see cref="DateTimeOffset.UtcDateTime"/> — <c>TIMESTAMP_NTZ</c> stores UTC by this
    /// backend's contract. (No <c>float[]</c> case: the cross-schema generator inlines vector
    /// literals rather than binding them.)
    /// </summary>
    private static void BindGeneratedParameter(DbCommand command, string name, object? value)
    {
        var bare = name.TrimStart('@', ':');
        switch (value)
        {
            case null or DBNull:
                SnowflakeConnectionSource.AddParam(command, bare, DBNull.Value, DbType.String);
                break;
            case string s:
                SnowflakeConnectionSource.AddParam(command, bare, s, DbType.String);
                break;
            case bool b:
                SnowflakeConnectionSource.AddParam(command, bare, b, DbType.Boolean);
                break;
            case short i16:
                SnowflakeConnectionSource.AddParam(command, bare, i16, DbType.Int16);
                break;
            case int i32:
                SnowflakeConnectionSource.AddParam(command, bare, i32, DbType.Int32);
                break;
            case long i64:
                SnowflakeConnectionSource.AddParam(command, bare, i64, DbType.Int64);
                break;
            case float f:
                SnowflakeConnectionSource.AddParam(command, bare, (double)f, DbType.Double);
                break;
            case double d:
                SnowflakeConnectionSource.AddParam(command, bare, d, DbType.Double);
                break;
            case decimal m:
                SnowflakeConnectionSource.AddParam(command, bare, m, DbType.Decimal);
                break;
            case DateTimeOffset dto:
                SnowflakeConnectionSource.AddParam(command, bare, dto.UtcDateTime, DbType.DateTime);
                break;
            case DateTime dt:
                SnowflakeConnectionSource.AddParam(command, bare, dt, DbType.DateTime);
                break;
            case Guid g:
                SnowflakeConnectionSource.AddParam(command, bare, g.ToString(), DbType.String);
                break;
            default:
                SnowflakeConnectionSource.AddParam(
                    command, bare, Convert.ToString(value, CultureInfo.InvariantCulture), DbType.String);
                break;
        }
    }

    /// <summary>
    /// Finds a column ordinal by name, case-insensitively (unquoted Snowflake identifiers come
    /// back uppercased; quoted ones lowercase — accept either), throwing when the projection
    /// lacks the column — a missing column here is a defective SELECT, not a readable row.
    /// </summary>
    private static int GetOrdinalIgnoreCase(DbDataReader reader, string name)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new InvalidOperationException(
            $"Required column '{name}' is missing from the autocomplete projection.");
    }
}
