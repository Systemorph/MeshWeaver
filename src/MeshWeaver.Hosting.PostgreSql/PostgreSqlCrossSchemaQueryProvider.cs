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
        "admin", "portal", "kernel",
        "_access", "_address_", "_graph", "_settings", "_tracking", "_thread", "_source", "_test",
        "source", "test",
        "login", "markdown", "onboarding", "welcome", "settings", "storage",
        "p", "path", "mesh", "thread", "agent", "partition", "organization", "vuser",
        "public", "information_schema", "pg_catalog", "pg_toast"
    };

    /// <summary>
    /// Syncs the searchable_schemas table by querying information_schema for
    /// schemas that contain a <c>mesh_nodes</c> table. Same SQL the legacy
    /// factory's <c>DiscoverPartitionsAsync</c> ran; inlined here so this
    /// class is self-contained — no <see cref="PostgreSqlPartitionedStoreFactory"/>
    /// dependency.
    /// </summary>
    public async Task SyncSearchableSchemasAsync(CancellationToken ct = default)
    {
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
            await using var reader = await discoverCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
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
        await cmd.ExecuteNonQueryAsync(ct);
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
        var schemas = await GetSearchableSchemasAsync(ct);
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
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            present.Add(reader.GetString(0));
        return present;
    }

    public async Task<IReadOnlyList<string>> GetSearchableSchemasAsync(CancellationToken ct = default)
    {
        try
        {
            var schemas = new List<string>();
            await using var cmd = _dataSource.CreateCommand(
                "SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                schemas.Add(reader.GetString(0));

            // If empty (first run), sync from factory and retry
            if (schemas.Count == 0)
            {
                await SyncSearchableSchemasAsync(ct);
                schemas.Clear();
                await using var cmd2 = _dataSource.CreateCommand(
                    "SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name");
                await using var reader2 = await cmd2.ExecuteReaderAsync(ct);
                while (await reader2.ReadAsync(ct))
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

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return ReadMeshNode(reader, options);
        }
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
            Content = content,
            DesiredId = reader.IsDBNull(reader.GetOrdinal("desired_id")) ? null : reader.GetString(reader.GetOrdinal("desired_id")),
            MainNode = reader.IsDBNull(reader.GetOrdinal("main_node"))
                ? (string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}")
                : reader.GetString(reader.GetOrdinal("main_node"))
        };
    }

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

        var generator = new PostgreSqlSqlGenerator();
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(
            query, schemas, userId, tableName, activityUserId);

        _logger?.LogInformation(
            "[CrossSchema] Satellite query: table={Table}, schemas={Count}, userId={User}, source={Source}",
            tableName, schemas.Count, userId, query.Source);

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
            cmd.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
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
