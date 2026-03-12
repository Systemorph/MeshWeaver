using System.Runtime.CompilerServices;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL implementation of IVersionQuery.
/// Queries the mesh_node_history table (schema-local via SearchPath).
/// </summary>
public class PostgreSqlVersionQuery : IVersionQuery
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlVersionQuery(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    private static (string Namespace, string Id) SplitPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var ns = lastSlash > 0 ? path[..lastSlash] : "";
        var id = lastSlash > 0 ? path[(lastSlash + 1)..] : path;
        return (ns, id);
    }

    public async IAsyncEnumerable<MeshNodeVersion> GetVersionsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (ns, id) = SplitPath(path);
        await using var cmd = _dataSource.CreateCommand(
            "SELECT version, last_modified, changed_by, name, node_type " +
            "FROM mesh_node_history WHERE namespace = $1 AND id = $2 " +
            "ORDER BY version DESC");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return new MeshNodeVersion(
                path,
                reader.GetInt64(0),
                new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)
            );
        }
    }

    public async Task<MeshNode?> GetVersionAsync(
        string path, long version, JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var (ns, id) = SplitPath(path);
        await using var cmd = _dataSource.CreateCommand(
            "SELECT id, namespace, name, node_type, category, icon, display_order, " +
            "last_modified, version, state, content, desired_id, main_node " +
            "FROM mesh_node_history WHERE namespace = $1 AND id = $2 AND version = $3");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(version);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadMeshNode(reader, options);
    }

    public async Task<MeshNode?> GetVersionBeforeAsync(
        string path, long beforeVersion, JsonSerializerOptions options,
        CancellationToken ct = default)
    {
        var (ns, id) = SplitPath(path);
        await using var cmd = _dataSource.CreateCommand(
            "SELECT id, namespace, name, node_type, category, icon, display_order, " +
            "last_modified, version, state, content, desired_id, main_node " +
            "FROM mesh_node_history WHERE namespace = $1 AND id = $2 AND version < $3 " +
            "ORDER BY version DESC LIMIT 1");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(beforeVersion);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadMeshNode(reader, options);
    }

    private static MeshNode ReadMeshNode(NpgsqlDataReader reader, JsonSerializerOptions options)
    {
        var nodeId = reader.GetString(reader.GetOrdinal("id"));
        var ns = reader.GetString(reader.GetOrdinal("namespace"));

        object? content = null;
        var contentOrd = reader.GetOrdinal("content");
        if (!reader.IsDBNull(contentOrd))
        {
            var json = reader.GetString(contentOrd);
            content = JsonSerializer.Deserialize<object>(json, options);
        }

        return new MeshNode(nodeId, string.IsNullOrEmpty(ns) ? null : ns)
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
                ? (string.IsNullOrEmpty(ns) ? nodeId : $"{ns}/{nodeId}")
                : reader.GetString(reader.GetOrdinal("main_node"))
        };
    }
}
