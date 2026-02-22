using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Manages hierarchical permission resolution using the denormalized user_effective_permissions table.
/// Permissions are now populated from AccessAssignment and GroupMembership MeshNodes via triggers.
/// </summary>
public class PostgreSqlAccessControl
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlAccessControl(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Manually rebuilds the denormalized permissions table.
    /// The trigger does this automatically when AccessAssignment/GroupMembership MeshNodes change,
    /// but this is useful for bulk operations or initial setup.
    /// </summary>
    public async Task RebuildDenormalizedTableAsync(CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT rebuild_user_effective_permissions()");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Checks if a user has a specific permission at a given node path.
    /// Uses the denormalized table with most-specific-prefix-wins logic.
    /// </summary>
    public async Task<bool> HasPermissionAsync(string userId, string nodePath, string permission, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            SELECT uep.is_allow
            FROM user_effective_permissions uep
            WHERE uep.user_id = $1
              AND uep.permission = $2
              AND $3 LIKE uep.node_path_prefix || '%'
            ORDER BY LENGTH(uep.node_path_prefix) DESC
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(permission);
        cmd.Parameters.AddWithValue(nodePath);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is true;
    }

    /// <summary>
    /// Gets all effective permissions for a user at a given node path.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(string userId, string nodePath, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            SELECT DISTINCT permission
            FROM (
                SELECT permission, is_allow,
                       ROW_NUMBER() OVER (PARTITION BY permission ORDER BY LENGTH(node_path_prefix) DESC) AS rn
                FROM user_effective_permissions
                WHERE user_id = $1
                  AND $2 LIKE node_path_prefix || '%'
            ) sub
            WHERE sub.rn = 1 AND sub.is_allow = true
            """);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(nodePath);

        var permissions = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            permissions.Add(reader.GetString(0));
        }

        return permissions;
    }
}
