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
    private readonly PostgreSqlPartitionedStoreFactory _factory;
    private readonly PostgreSqlSqlGenerator _sqlGenerator = new();
    private readonly ILogger? _logger;

    public PostgreSqlCrossSchemaQueryProvider(
        NpgsqlDataSource dataSource,
        PostgreSqlPartitionedStoreFactory factory,
        ILogger<PostgreSqlCrossSchemaQueryProvider>? logger = null)
    {
        _dataSource = dataSource;
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Syncs the searchable_schemas table from discovered partitions.
    /// Called at startup after partition discovery.
    /// </summary>
    public async Task SyncSearchableSchemasAsync(CancellationToken ct = default)
    {
        var schemas = await _factory.DiscoverPartitionsAsync(ct);

        // Replace contents of searchable_schemas
        await using var cmd = _dataSource.CreateCommand(
            "DELETE FROM public.searchable_schemas; " +
            string.Join(" ", schemas.Select((s, i) =>
                $"INSERT INTO public.searchable_schemas (schema_name) VALUES ('{s.Replace("'", "''")}') ON CONFLICT DO NOTHING;")));
        await cmd.ExecuteNonQueryAsync(ct);
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

        // Inline parameter values into the SQL string
        foreach (var (name, value) in parameters.OrderByDescending(p => p.Key.Length))
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

    private static MeshNode ReadMeshNode(NpgsqlDataReader reader, JsonSerializerOptions options)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        var ns = reader.GetString(reader.GetOrdinal("namespace"));

        object? content = null;
        var contentOrd = reader.GetOrdinal("content");
        if (!reader.IsDBNull(contentOrd))
        {
            var json = reader.GetString(contentOrd);
            content = JsonSerializer.Deserialize<object>(json, options);
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

    public async IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string tableName,
        string? userId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (schemas.Count == 0)
            yield break;

        var generator = new PostgreSqlSqlGenerator();
        var (sql, parameters) = generator.GenerateCrossSchemaSelectQuery(query, schemas, userId, tableName);

        _logger?.LogInformation(
            "[CrossSchema] Satellite query: table={Table}, schemas={Count}, userId={User}",
            tableName, schemas.Count, userId);

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
            cmd.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadMeshNode(reader, options);
    }

    private static string EscapeSql(string input) =>
        input.Replace("'", "''");
}
