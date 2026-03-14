using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL implementation of IStorageAdapter.
/// Stores MeshNodes in mesh_nodes table and partition objects in partition_objects table.
/// When a PartitionDefinition with TableMappings is provided, satellite nodes are routed
/// to their dedicated tables based on path pattern matching.
/// </summary>
public class PostgreSqlStorageAdapter : IStorageAdapter, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlSqlGenerator _sqlGenerator = new();
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly PartitionDefinition? _partitionDefinition;

    public NpgsqlDataSource DataSource => _dataSource;

    public PostgreSqlStorageAdapter(
        NpgsqlDataSource dataSource,
        IEmbeddingProvider? embeddingProvider = null,
        PartitionDefinition? partitionDefinition = null)
    {
        _dataSource = dataSource;
        _embeddingProvider = embeddingProvider ?? NullEmbeddingProvider.Instance;
        _partitionDefinition = partitionDefinition;
    }

    /// <summary>
    /// Resolves the table name for a given path.
    /// Returns a satellite table name if the path matches a TableMapping, otherwise "mesh_nodes".
    /// </summary>
    private string ResolveTable(string path)
        => _partitionDefinition?.ResolveTable(path) ?? "mesh_nodes";

    private static string NormalizePath(string? path) =>
        path?.Trim('/') ?? "";

    private static (string Namespace, string Id) SplitPath(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('/');
        var ns = lastSlash > 0 ? normalizedPath[..lastSlash] : "";
        var id = lastSlash > 0 ? normalizedPath[(lastSlash + 1)..] : normalizedPath;
        return (ns, id);
    }

    public async Task<MeshNode?> ReadAsync(string path, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return null;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT id, namespace, name, node_type, category, icon, display_order, " +
            $"last_modified, version, state, content, desired_id, main_node " +
            $"FROM \"{table}\" WHERE namespace = $1 AND id = $2");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadMeshNode(reader, options);
    }

    public async Task WriteAsync(MeshNode node, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var ns = node.Namespace ?? "";

        // Generate embedding
        var embeddingText = string.Join(" ",
            new[] { node.Name, node.NodeType }
                .Where(s => !string.IsNullOrEmpty(s)));
        var embeddingVector = await _embeddingProvider.GenerateEmbeddingAsync(embeddingText);

        var contentJson = node.Content != null
            ? JsonSerializer.Serialize(node.Content, node.Content.GetType(), options)
            : null;

        var table = ResolveTable(node.Path);
        await using var cmd = _dataSource.CreateCommand(
            $"""
            INSERT INTO "{table}" (namespace, id, name, node_type, category, icon, display_order,
                                    last_modified, version, state, content, desired_id, embedding, main_node)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11::jsonb, $12, $13, $14)
            ON CONFLICT (namespace, id) DO UPDATE SET
                name = EXCLUDED.name,
                node_type = EXCLUDED.node_type,
                category = EXCLUDED.category,
                icon = EXCLUDED.icon,
                display_order = EXCLUDED.display_order,
                last_modified = EXCLUDED.last_modified,
                version = EXCLUDED.version,
                state = EXCLUDED.state,
                content = EXCLUDED.content,
                desired_id = EXCLUDED.desired_id,
                embedding = EXCLUDED.embedding,
                main_node = EXCLUDED.main_node
            """);

        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(node.Id);
        cmd.Parameters.AddWithValue((object?)node.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.NodeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.Icon ?? DBNull.Value);
        cmd.Parameters.AddWithValue(node.Order.HasValue ? node.Order.Value : DBNull.Value);
        cmd.Parameters.AddWithValue(node.LastModified == default ? DateTimeOffset.UtcNow : node.LastModified);
        cmd.Parameters.AddWithValue(node.Version);
        cmd.Parameters.AddWithValue((short)node.State);
        cmd.Parameters.AddWithValue((object?)contentJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)node.DesiredId ?? DBNull.Value);

        if (embeddingVector != null)
            cmd.Parameters.AddWithValue(new Vector(embeddingVector));
        else
            cmd.Parameters.AddWithValue(DBNull.Value);

        cmd.Parameters.AddWithValue(node.MainNode);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        await using var cmd = _dataSource.CreateCommand(
            $"DELETE FROM \"{table}\" WHERE namespace = $1 AND id = $2");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths)> ListChildPathsAsync(
        string? parentPath,
        CancellationToken ct = default)
    {
        var normalizedParent = NormalizePath(parentPath);

        var table = ResolveTable(normalizedParent);
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT id, namespace FROM \"{table}\" WHERE namespace = $1");
        cmd.Parameters.AddWithValue(normalizedParent);

        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var ns = reader.GetString(1);
            var nodePath = string.IsNullOrEmpty(ns) ? id : $"{ns}/{id}";
            paths.Add(nodePath);
        }

        return (paths, Enumerable.Empty<string>());
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath))
            return false;

        var (ns, id) = SplitPath(normalizedPath);

        var table = ResolveTable(normalizedPath);
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT 1 FROM \"{table}\" WHERE namespace = $1 AND id = $2 LIMIT 1");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct);
    }

    public async Task<(MeshNode? Node, int MatchedSegments)> FindBestPrefixMatchAsync(
        string fullPath, JsonSerializerOptions options, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(fullPath);
        if (string.IsNullOrEmpty(normalizedPath))
            return (null, 0);

        // Single SQL query: find the node whose path is the longest prefix of the input.
        // Matches exact path or any ancestor (input starts with path + '/').
        // Ordered by path length descending to get the deepest (most specific) match first.
        var table = ResolveTable(normalizedPath);
        await using var cmd = _dataSource.CreateCommand(
            $"SELECT id, namespace, name, node_type, category, icon, display_order, " +
            $"last_modified, version, state, content, desired_id, main_node " +
            $"FROM \"{table}\" WHERE $1 = path OR $1 LIKE path || '/%' " +
            $"ORDER BY LENGTH(path) DESC LIMIT 1");
        cmd.Parameters.AddWithValue(normalizedPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (null, 0);

        var node = ReadMeshNode(reader, options);
        var matchedSegments = node.Path.Split('/').Length;
        return (node, matchedSegments);
    }

    #region Partition Storage

    public async IAsyncEnumerable<object> GetPartitionObjectsAsync(
        string nodePath,
        string? subPath,
        JsonSerializerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        await using var cmd = _dataSource.CreateCommand(
            "SELECT data, type_name FROM partition_objects WHERE partition_key = $1");
        cmd.Parameters.AddWithValue(partitionKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0);
            json = EnsureTypeDiscriminatorFirst(json);
            var typeName = reader.IsDBNull(1) ? null : reader.GetString(1);

            Type? type = null;
            if (typeName != null)
                type = Type.GetType(typeName);

            if (type != null)
            {
                var obj = JsonSerializer.Deserialize(json, type, options);
                if (obj != null)
                    yield return obj;
            }
            else
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(json, options);
                yield return doc;
            }
        }
    }

    public async Task SavePartitionObjectsAsync(
        string nodePath,
        string? subPath,
        IReadOnlyCollection<object> objects,
        JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        await DeletePartitionObjectsAsync(nodePath, subPath, ct);

        foreach (var obj in objects)
        {
            var id = GetObjectId(obj);
            var json = JsonSerializer.Serialize(obj, obj.GetType(), options);
            var typeName = obj.GetType().AssemblyQualifiedName;

            await using var cmd = _dataSource.CreateCommand(
                """
                INSERT INTO partition_objects (id, partition_key, type_name, data, last_modified)
                VALUES ($1, $2, $3, $4::jsonb, $5)
                ON CONFLICT (partition_key, id) DO UPDATE SET
                    type_name = EXCLUDED.type_name,
                    data = EXCLUDED.data,
                    last_modified = EXCLUDED.last_modified
                """);
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(partitionKey);
            cmd.Parameters.AddWithValue((object?)typeName ?? DBNull.Value);
            cmd.Parameters.AddWithValue(json);
            cmd.Parameters.AddWithValue(DateTimeOffset.UtcNow);

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DeletePartitionObjectsAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        await using var cmd = _dataSource.CreateCommand(
            "DELETE FROM partition_objects WHERE partition_key = $1");
        cmd.Parameters.AddWithValue(partitionKey);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTimeOffset?> GetPartitionMaxTimestampAsync(
        string nodePath,
        string? subPath = null,
        CancellationToken ct = default)
    {
        var partitionKey = GetPartitionStorageKey(nodePath, subPath);

        await using var cmd = _dataSource.CreateCommand(
            "SELECT MAX(last_modified) FROM partition_objects WHERE partition_key = $1");
        cmd.Parameters.AddWithValue(partitionKey);

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is DateTimeOffset dto)
            return dto;
        if (result is DateTime dt)
            return new DateTimeOffset(dt, TimeSpan.Zero);
        return null;
    }

    public async Task<IEnumerable<string>> ListPartitionSubPathsAsync(string nodePath, CancellationToken ct = default)
    {
        var prefix = NormalizePath(nodePath) + "/";

        await using var cmd = _dataSource.CreateCommand(
            """
            SELECT DISTINCT
                CASE WHEN position('/' in substring(partition_key from length($1) + 1)) > 0
                     THEN substring(partition_key from length($1) + 1 for position('/' in substring(partition_key from length($1) + 1)) - 1)
                     ELSE substring(partition_key from length($1) + 1)
                END AS sub_path
            FROM partition_objects
            WHERE partition_key LIKE $2
            """);
        cmd.Parameters.AddWithValue(prefix);
        cmd.Parameters.AddWithValue(prefix + "%");

        var subPaths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sub = reader.GetString(0);
            if (!string.IsNullOrEmpty(sub))
                subPaths.Add(sub);
        }

        return subPaths;
    }

    #endregion

    #region Query Support

    /// <summary>
    /// Queries nodes using parsed query, translated to PostgreSQL SQL.
    /// </summary>
    public async IAsyncEnumerable<MeshNode> QueryNodesAsync(
        ParsedQuery query,
        JsonSerializerOptions options,
        string? userId = null,
        string? basePath = null,
        string? activityUserId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Resolve the target table based on the query path or nodeType and partition definition.
        // For satellite paths like "User/alice/_Thread", this routes to the "threads" table.
        // For nodeType-only queries like "nodeType:Thread", resolves via nodeType-to-suffix mapping.
        var effectivePath = query.Path ?? basePath;
        var tableName = !string.IsNullOrEmpty(effectivePath)
            ? ResolveTable(effectivePath)
            : _partitionDefinition?.ResolveTableByNodeType(query.ExtractNodeType()) ?? "mesh_nodes";
        var (sql, parameters) = _sqlGenerator.GenerateSelectQuery(query, userId, activityUserId, tableName);
        if (!string.IsNullOrEmpty(effectivePath))
        {
            var (scopeClause, scopeParams) = _sqlGenerator.GenerateScopeClause(
                effectivePath, query.Scope);

            if (!string.IsNullOrEmpty(scopeClause))
            {
                foreach (var (k, v) in scopeParams)
                    parameters[k] = v;

                if (sql.Contains("WHERE"))
                    sql = sql.Replace("WHERE", $"WHERE {scopeClause} AND");
                else if (sql.Contains("ORDER BY"))
                    sql = sql.Replace("ORDER BY", $"WHERE {scopeClause} ORDER BY");
                else
                    sql += $" WHERE {scopeClause}";
            }
        }

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            var p = new NpgsqlParameter(name, value ?? DBNull.Value);
            cmd.Parameters.Add(p);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return ReadMeshNode(reader, options);
        }
    }

    /// <summary>
    /// Performs vector similarity search.
    /// </summary>
    public async IAsyncEnumerable<MeshNode> VectorSearchAsync(
        float[] queryVector,
        JsonSerializerOptions options,
        ParsedQuery? filter = null,
        string? userId = null,
        string? namespacePath = null,
        int topK = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (sql, parameters) = _sqlGenerator.GenerateVectorSearchQuery(
            filter, queryVector, userId, topK);

        if (!string.IsNullOrEmpty(namespacePath))
        {
            var normalizedPath = NormalizePath(namespacePath);
            parameters["@nsPrefix"] = $"{normalizedPath}/";

            if (sql.Contains("WHERE"))
                sql = sql.Replace("WHERE", "WHERE n.path LIKE @nsPrefix || '%' AND");
            else
                sql = sql.Replace("ORDER BY", "WHERE n.path LIKE @nsPrefix || '%' ORDER BY");
        }

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            var p = value is Vector v
                ? new NpgsqlParameter(name, v)
                : new NpgsqlParameter(name, value ?? DBNull.Value);
            cmd.Parameters.Add(p);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return ReadMeshNode(reader, options);
        }
    }

    #endregion

    private static MeshNode ReadMeshNode(NpgsqlDataReader reader, JsonSerializerOptions options)
    {
        var id = reader.GetString(reader.GetOrdinal("id"));
        var ns = reader.GetString(reader.GetOrdinal("namespace"));

        object? content = null;
        var contentOrd = reader.GetOrdinal("content");
        if (!reader.IsDBNull(contentOrd))
        {
            var json = reader.GetString(contentOrd);
            json = EnsureTypeDiscriminatorFirst(json);
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

    /// <summary>
    /// PostgreSQL jsonb reorders keys alphabetically at ALL nesting levels,
    /// which breaks System.Text.Json polymorphic deserialization (requires $type as the first property).
    /// This method recursively moves $type to the front in every object throughout the JSON tree.
    /// </summary>
    private static string EnsureTypeDiscriminatorFirst(string json)
    {
        if (!json.Contains("\"$type\"", StringComparison.Ordinal))
            return json; // No discriminator anywhere

        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            WriteElementWithTypeFirst(writer, doc.RootElement);
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Recursively writes a JsonElement, ensuring $type is the first property in every object.
    /// </summary>
    private static void WriteElementWithTypeFirst(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Write $type first if present
                if (element.TryGetProperty("$type", out var typeValue))
                {
                    writer.WritePropertyName("$type");
                    typeValue.WriteTo(writer);
                }
                // Write remaining properties (recursively)
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "$type")
                        continue;
                    writer.WritePropertyName(prop.Name);
                    WriteElementWithTypeFirst(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElementWithTypeFirst(writer, item);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string GetPartitionStorageKey(string nodePath, string? subPath)
    {
        var key = NormalizePath(nodePath);
        if (!string.IsNullOrEmpty(subPath))
            key = $"{key}/{NormalizePath(subPath)}";
        return key;
    }

    private static string GetObjectId(object obj)
    {
        var idProp = obj.GetType().GetProperty("Id") ?? obj.GetType().GetProperty("id");
        var id = idProp?.GetValue(obj)?.ToString();
        return id ?? Guid.NewGuid().ToString();
    }

    public ValueTask DisposeAsync()
    {
        // DataSource is typically shared and disposed elsewhere
        return ValueTask.CompletedTask;
    }
}
