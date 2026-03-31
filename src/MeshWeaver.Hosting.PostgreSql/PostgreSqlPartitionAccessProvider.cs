using MeshWeaver.Mesh.Services;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// PostgreSQL implementation of IPartitionAccessProvider.
/// Queries the public.partition_access table to determine which partitions
/// a user can access. This table is populated by the rebuild_user_effective_permissions()
/// trigger function in each partition schema.
/// </summary>
public class PostgreSqlPartitionAccessProvider(NpgsqlDataSource dataSource) : IPartitionAccessProvider
{
    public async Task<HashSet<string>?> GetAccessiblePartitionsAsync(string userId, CancellationToken ct = default)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = dataSource.CreateCommand(
            "SELECT partition FROM public.partition_access WHERE user_id = $1 OR user_id = 'Public'");
        cmd.Parameters.AddWithValue(userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }
}
