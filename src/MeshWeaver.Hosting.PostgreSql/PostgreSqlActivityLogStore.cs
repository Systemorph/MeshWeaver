using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Data;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL implementation of IActivityLogStore using the change_logs table.
/// </summary>
public class PostgreSqlActivityLogStore : IActivityLogStore
{
    private readonly NpgsqlDataSource _dataSource;
    private static readonly JsonSerializerOptions JsonOptions = new();

    public PostgreSqlActivityLogStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task SaveActivityLogAsync(string hubPath, ActivityLog log, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            INSERT INTO change_logs (id, hub_path, changed_by, category, start_time, end_time, change_count, status, messages)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue(log.Id);
        cmd.Parameters.AddWithValue(hubPath);
        cmd.Parameters.AddWithValue((object?)log.User?.Email ?? DBNull.Value);
        cmd.Parameters.AddWithValue(log.Category);
        cmd.Parameters.AddWithValue(log.Start);
        cmd.Parameters.AddWithValue((object?)log.End ?? DBNull.Value);
        cmd.Parameters.AddWithValue(log.Messages.Count);
        cmd.Parameters.AddWithValue((short)log.Status);
        cmd.Parameters.AddWithValue(log.Messages.Count > 0
            ? JsonSerializer.Serialize(log.Messages, JsonOptions)
            : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ActivityLog>> GetActivityLogsAsync(
        string hubPath,
        string? user = null,
        DateTime? from = null,
        DateTime? to = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var sql = "SELECT id, hub_path, changed_by, category, start_time, end_time, change_count, status, messages FROM change_logs WHERE hub_path = $1";
        var paramIndex = 2;
        var conditions = new List<string>();
        var parameters = new List<NpgsqlParameter> { new() { Value = hubPath } };

        if (user != null)
        {
            conditions.Add($"changed_by = ${paramIndex++}");
            parameters.Add(new NpgsqlParameter { Value = user });
        }
        if (from.HasValue)
        {
            conditions.Add($"start_time >= ${paramIndex++}");
            parameters.Add(new NpgsqlParameter { Value = from.Value });
        }
        if (to.HasValue)
        {
            conditions.Add($"end_time <= ${paramIndex++}");
            parameters.Add(new NpgsqlParameter { Value = to.Value });
        }

        if (conditions.Count > 0)
            sql += " AND " + string.Join(" AND ", conditions);

        sql += $" ORDER BY start_time DESC LIMIT ${paramIndex}";
        parameters.Add(new NpgsqlParameter { Value = limit });

        await using var cmd = _dataSource.CreateCommand(sql);
        foreach (var param in parameters)
            cmd.Parameters.Add(param);

        var results = new List<ActivityLog>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var messages = reader.IsDBNull(8)
                ? ImmutableList<LogMessage>.Empty
                : JsonSerializer.Deserialize<ImmutableList<LogMessage>>(reader.GetString(8), JsonOptions)
                    ?? ImmutableList<LogMessage>.Empty;

            results.Add(new ActivityLog(reader.GetString(3))
            {
                Id = reader.GetString(0),
                User = reader.IsDBNull(2) ? null : new UserInfo(reader.GetString(2), reader.GetString(2)),
                Start = reader.GetDateTime(4),
                End = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                Status = (ActivityStatus)reader.GetInt16(7),
                Messages = messages
            });
        }

        return results;
    }
}
