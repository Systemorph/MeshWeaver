using MeshWeaver.Mesh.Activity;
using Npgsql;
using NpgsqlTypes;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL implementation of IActivityStore using the user_activity table.
/// </summary>
public class PostgreSqlActivityStore : IActivityStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlActivityStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<UserActivityRecord>> GetActivitiesAsync(string userId, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            SELECT node_path, activity_type, first_accessed, last_accessed,
                   access_count, node_name, node_type, namespace
            FROM user_activity
            WHERE user_id = $1
            ORDER BY last_accessed DESC
            """);
        cmd.Parameters.AddWithValue(userId);

        var results = new List<UserActivityRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var nodePath = reader.GetString(0);
            results.Add(new UserActivityRecord
            {
                Id = nodePath.Replace("/", "_"),
                NodePath = nodePath,
                UserId = userId,
                ActivityType = (ActivityType)reader.GetInt16(1),
                FirstAccessedAt = reader.GetFieldValue<DateTimeOffset>(2),
                LastAccessedAt = reader.GetFieldValue<DateTimeOffset>(3),
                AccessCount = reader.GetInt32(4),
                NodeName = reader.IsDBNull(5) ? null : reader.GetString(5),
                NodeType = reader.IsDBNull(6) ? null : reader.GetString(6),
                Namespace = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return results;
    }

    public async Task SaveActivitiesAsync(string userId, IReadOnlyCollection<UserActivityRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
            return;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var batch = new NpgsqlBatch(conn);

        foreach (var record in records)
        {
            var cmd = new NpgsqlBatchCommand(
                """
                INSERT INTO user_activity (user_id, node_path, activity_type, first_accessed, last_accessed, access_count, node_name, node_type, namespace)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                ON CONFLICT (user_id, node_path) DO UPDATE SET
                    activity_type = EXCLUDED.activity_type,
                    last_accessed = EXCLUDED.last_accessed,
                    access_count = user_activity.access_count + EXCLUDED.access_count,
                    node_name = COALESCE(EXCLUDED.node_name, user_activity.node_name),
                    node_type = COALESCE(EXCLUDED.node_type, user_activity.node_type),
                    namespace = COALESCE(EXCLUDED.namespace, user_activity.namespace)
                """);
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(record.NodePath);
            cmd.Parameters.AddWithValue((short)record.ActivityType);
            cmd.Parameters.AddWithValue(record.FirstAccessedAt);
            cmd.Parameters.AddWithValue(record.LastAccessedAt);
            cmd.Parameters.AddWithValue(record.AccessCount);
            cmd.Parameters.AddWithValue((object?)record.NodeName ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)record.NodeType ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)record.Namespace ?? DBNull.Value);
            batch.BatchCommands.Add(cmd);
        }

        await batch.ExecuteNonQueryAsync(ct);
    }
}
