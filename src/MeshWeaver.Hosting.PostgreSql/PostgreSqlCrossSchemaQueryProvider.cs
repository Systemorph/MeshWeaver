using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
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

    public PostgreSqlCrossSchemaQueryProvider(
        NpgsqlDataSource dataSource,
        PostgreSqlPartitionedStoreFactory factory)
    {
        _dataSource = dataSource;
        _factory = factory;
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

    public async IAsyncEnumerable<MeshNode> QueryAcrossSchemasAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        IReadOnlyList<string> schemas,
        string? userId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Build WHERE clause from parsed query (without access control — the stored proc handles that).
        // Since the stored proc receives the WHERE clause as a TEXT string and executes it dynamically,
        // we must inline parameter values — parameterized @p0 refs won't work inside EXECUTE.
        var (whereClause, parameters) = _sqlGenerator.GenerateWhereClause(query);

        // Strip "WHERE " prefix — the stored proc adds its own WHERE
        var filterClause = whereClause.StartsWith("WHERE ")
            ? whereClause[6..] : whereClause;

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

        // Add text search if present
        if (!string.IsNullOrEmpty(query.TextSearch))
        {
            var textExpr = "COALESCE(n.name,'') || ' ' || COALESCE(n.namespace || '/' || n.id,'') || ' ' || COALESCE(n.node_type,'')";
            if (!string.IsNullOrEmpty(filterClause))
                filterClause += " AND ";
            filterClause += $"{textExpr} ILIKE '%{EscapeSql(query.TextSearch)}%'";
        }

        // Build ORDER BY
        var orderBy = query.OrderBy != null
            ? $"{PostgreSqlSqlGenerator.MapSelector(query.OrderBy.Property)} {(query.OrderBy.Descending ? "DESC" : "ASC")}"
            : "n.last_modified DESC";

        var limit = query.Limit ?? 50;

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

    private static string EscapeSql(string input) =>
        input.Replace("'", "''").Replace("%", "\\%").Replace("_", "\\_");
}
