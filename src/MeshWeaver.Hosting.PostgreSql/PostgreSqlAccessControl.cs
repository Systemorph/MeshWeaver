using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Manages access control rules: grant/revoke permissions, group membership,
/// and hierarchical permission resolution.
/// </summary>
public class PostgreSqlAccessControl
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlAccessControl(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Grants or denies a permission for an access object (user or group) at a namespace.
    /// </summary>
    public async Task GrantAsync(string ns, string accessObject, string permission, bool isAllow = true, string? assignedBy = null, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            INSERT INTO access_control (namespace, access_object, permission, is_allow, assigned_by)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (namespace, access_object, permission) DO UPDATE SET
                is_allow = EXCLUDED.is_allow,
                assigned_by = EXCLUDED.assigned_by,
                assigned_at = NOW()
            """);
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(accessObject);
        cmd.Parameters.AddWithValue(permission);
        cmd.Parameters.AddWithValue(isAllow);
        cmd.Parameters.AddWithValue((object?)assignedBy ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Revokes a permission entry entirely.
    /// </summary>
    public async Task RevokeAsync(string ns, string accessObject, string permission, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            "DELETE FROM access_control WHERE namespace = $1 AND access_object = $2 AND permission = $3");
        cmd.Parameters.AddWithValue(ns);
        cmd.Parameters.AddWithValue(accessObject);
        cmd.Parameters.AddWithValue(permission);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Adds a member (user or group) to a group.
    /// </summary>
    public async Task AddGroupMemberAsync(string groupId, string accessObjectId, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            INSERT INTO group_members (group_id, access_object_id)
            VALUES ($1, $2)
            ON CONFLICT (group_id, access_object_id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue(groupId);
        cmd.Parameters.AddWithValue(accessObjectId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Removes a member (user or group) from a group.
    /// </summary>
    public async Task RemoveGroupMemberAsync(string groupId, string accessObjectId, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            "DELETE FROM group_members WHERE group_id = $1 AND access_object_id = $2");
        cmd.Parameters.AddWithValue(groupId);
        cmd.Parameters.AddWithValue(accessObjectId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Manually rebuilds the denormalized permissions table.
    /// The trigger does this automatically, but this is useful for bulk operations.
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
