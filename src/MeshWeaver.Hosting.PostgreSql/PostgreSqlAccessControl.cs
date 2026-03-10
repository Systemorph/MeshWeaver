using MeshWeaver.Mesh.Security;
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

    /// <summary>
    /// Grants or denies a permission for a subject at a node path.
    /// Stores in the access_control table and rebuilds the denormalized permissions.
    /// </summary>
    public async Task GrantAsync(string nodePath, string subject, string permission, bool isAllow, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            INSERT INTO access_control (node_path, subject, permission, is_allow)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (node_path, subject, permission) DO UPDATE SET is_allow = $4
            """);
        cmd.Parameters.AddWithValue(nodePath);
        cmd.Parameters.AddWithValue(subject);
        cmd.Parameters.AddWithValue(permission);
        cmd.Parameters.AddWithValue(isAllow);
        await cmd.ExecuteNonQueryAsync(ct);

        await RebuildDenormalizedTableAsync(ct);
    }

    /// <summary>
    /// Revokes a permission for a subject at a node path.
    /// </summary>
    public async Task RevokeAsync(string nodePath, string subject, string permission, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            DELETE FROM access_control
            WHERE node_path = $1 AND subject = $2 AND permission = $3
            """);
        cmd.Parameters.AddWithValue(nodePath);
        cmd.Parameters.AddWithValue(subject);
        cmd.Parameters.AddWithValue(permission);
        await cmd.ExecuteNonQueryAsync(ct);

        await RebuildDenormalizedTableAsync(ct);
    }

    /// <summary>
    /// Adds a member to a group.
    /// </summary>
    public async Task AddGroupMemberAsync(string groupName, string memberId, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            INSERT INTO group_members (group_name, member_id)
            VALUES ($1, $2)
            ON CONFLICT DO NOTHING
            """);
        cmd.Parameters.AddWithValue(groupName);
        cmd.Parameters.AddWithValue(memberId);
        await cmd.ExecuteNonQueryAsync(ct);

        await RebuildDenormalizedTableAsync(ct);
    }

    /// <summary>
    /// Removes a member from a group.
    /// </summary>
    public async Task RemoveGroupMemberAsync(string groupName, string memberId, CancellationToken ct = default)
    {
        await using var cmd = _dataSource.CreateCommand(
            """
            DELETE FROM group_members
            WHERE group_name = $1 AND member_id = $2
            """);
        cmd.Parameters.AddWithValue(groupName);
        cmd.Parameters.AddWithValue(memberId);
        await cmd.ExecuteNonQueryAsync(ct);

        await RebuildDenormalizedTableAsync(ct);
    }

    /// <summary>
    /// Sets a partition access policy at the specified namespace.
    /// Upserts a _Policy MeshNode with per-permission switches.
    /// Pass false for permissions to deny, null to inherit.
    /// </summary>
    public async Task SetPolicyAsync(string targetNamespace, bool? read = null, bool? create = null, bool? update = null, bool? delete = null, bool? comment = null, bool breaksInheritance = false, CancellationToken ct = default)
    {
        var ns = targetNamespace ?? "";
        // Build the JSON content with per-permission fields
        var contentParts = new List<string>();
        if (read.HasValue) contentParts.Add($"'read', {(read.Value ? "true" : "false")}");
        if (create.HasValue) contentParts.Add($"'create', {(create.Value ? "true" : "false")}");
        if (update.HasValue) contentParts.Add($"'update', {(update.Value ? "true" : "false")}");
        if (delete.HasValue) contentParts.Add($"'delete', {(delete.Value ? "true" : "false")}");
        if (comment.HasValue) contentParts.Add($"'comment', {(comment.Value ? "true" : "false")}");
        if (breaksInheritance) contentParts.Add("'breaksInheritance', true");

        var jsonBuild = contentParts.Count > 0
            ? $"jsonb_build_object({string.Join(", ", contentParts)})"
            : "'{}'::jsonb";

        await using var cmd = _dataSource.CreateCommand(
            $"""
            INSERT INTO mesh_nodes (namespace, id, name, node_type, content)
            VALUES ($1, '_Policy', 'Access Policy', 'PartitionAccessPolicy', {jsonBuild})
            ON CONFLICT (namespace, id) DO UPDATE
            SET content = {jsonBuild},
                node_type = 'PartitionAccessPolicy',
                name = 'Access Policy'
            """);
        cmd.Parameters.AddWithValue(ns);
        await cmd.ExecuteNonQueryAsync(ct);

        await RebuildDenormalizedTableAsync(ct);
    }

    /// <summary>
    /// Removes the partition access policy at the specified namespace.
    /// </summary>
    public async Task RemovePolicyAsync(string targetNamespace, CancellationToken ct = default)
    {
        var ns = targetNamespace ?? "";
        await using var cmd = _dataSource.CreateCommand(
            """
            DELETE FROM mesh_nodes
            WHERE namespace = $1 AND id = '_Policy' AND node_type = 'PartitionAccessPolicy'
            """);
        cmd.Parameters.AddWithValue(ns);
        await cmd.ExecuteNonQueryAsync(ct);

        await RebuildDenormalizedTableAsync(ct);
    }

    /// <summary>
    /// Syncs DI-registered NodeTypePermission records to the node_type_permissions table.
    /// Called at startup to populate the DB with module-declared permissions.
    /// Uses INSERT ON CONFLICT to be idempotent.
    /// </summary>
    public async Task SyncNodeTypePermissionsAsync(
        IEnumerable<NodeTypePermission> permissions,
        CancellationToken ct = default)
    {
        foreach (var p in permissions)
        {
            await using var cmd = _dataSource.CreateCommand(
                """
                INSERT INTO node_type_permissions (node_type, public_read)
                VALUES ($1, $2)
                ON CONFLICT (node_type) DO UPDATE SET public_read = EXCLUDED.public_read
                """);
            cmd.Parameters.AddWithValue(p.NodeType);
            cmd.Parameters.AddWithValue(p.PublicRead);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
