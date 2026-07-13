using System.Data;
using System.Data.Common;
using System.Globalization;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Snowflake;

/// <summary>
/// Manages hierarchical permission resolution using the denormalized
/// <c>user_effective_permissions</c> table — the member-for-member Snowflake port of
/// <c>PostgreSqlAccessControl</c>. Permissions are populated from AccessAssignment and
/// GroupMembership MeshNodes; on PG that happens via DB triggers, here via
/// <see cref="SnowflakeAccessProjection"/> (invoked by the storage adapter's write path and by
/// this class's mutating convenience methods). When <c>schemaName</c> is set, all table
/// references are schema-qualified for cross-schema queries.
///
/// <para><b>Dialect deltas vs PG</b>: positional <c>$n</c> parameters → named <c>:name</c> via
/// <see cref="SnowflakeConnectionSource.AddParam"/>; <c>jsonb_build_object(...)</c> →
/// <c>OBJECT_CONSTRUCT(...)</c>; <c>ON CONFLICT ... DO UPDATE / DO NOTHING</c> → <c>MERGE</c>
/// when the endpoint supports it (read from <see cref="SnowflakeCapabilityHolder.Current"/>
/// lazily per operation), else DELETE-by-key + INSERT (for upserts) / <c>WHERE NOT EXISTS</c>
/// guarded INSERT (for do-nothing inserts); the longest-prefix permission lookup uses the
/// <c>MAX_BY</c> shape when supported, else PG's <c>ORDER BY LENGTH(...) DESC LIMIT 1</c> form
/// which ports unchanged; <c>rebuild_user_effective_permissions()</c> (a plpgsql function on
/// PG) delegates to <see cref="SnowflakeAccessProjection.RebuildOnConnectionAsync"/>.</para>
///
/// <para>Every method is an async I/O leaf — like <see cref="SnowflakeSchemaInitializer"/> and
/// the capability probe, callers run these inside an <c>IIoPool</c> invoke, never on a hub
/// scheduler.</para>
/// </summary>
public class SnowflakeAccessControl
{
    private readonly SnowflakeConnectionSource _source;
    private readonly string? _schemaName;
    private readonly string _centralSchema;
    private readonly SnowflakeCapabilityHolder _capabilities;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes the access-control helper over a connection source, optionally scoped to a schema.
    /// </summary>
    /// <param name="source">The Snowflake connection source used for all permission queries and rebuilds.</param>
    /// <param name="schemaName">Optional schema name; when set, all table references are schema-qualified for cross-schema queries.</param>
    /// <param name="centralSchema">The central schema holding <c>partition_access</c> (default <c>public</c>) — the projection sync target.</param>
    /// <param name="capabilities">Probed endpoint capabilities; a fresh all-on holder when null. Read lazily per operation.</param>
    /// <param name="logger">Optional diagnostics logger.</param>
    public SnowflakeAccessControl(
        SnowflakeConnectionSource source,
        string? schemaName = null,
        string centralSchema = "public",
        SnowflakeCapabilityHolder? capabilities = null,
        ILogger<SnowflakeAccessControl>? logger = null)
    {
        _source = source;
        _schemaName = schemaName;
        _centralSchema = centralSchema;
        _capabilities = capabilities ?? new SnowflakeCapabilityHolder();
        _logger = logger;
    }

    /// <summary>Schema-qualifies (and double-quotes) a table reference — the PG <c>Q</c> helper.</summary>
    private string Q(string table)
        => string.IsNullOrEmpty(_schemaName)
            ? SnowflakeIdentifiers.Quote(table)
            : SnowflakeIdentifiers.Qualify(_schemaName, table);

    /// <summary>
    /// Manually rebuilds the denormalized permissions table. The storage adapter's write path
    /// does this automatically when AccessAssignment/GroupMembership nodes change (the trigger
    /// replacement), but this is useful for bulk operations or initial setup. PG calls the
    /// schema's <c>rebuild_user_effective_permissions()</c> plpgsql function; here the same
    /// computation runs in C# via <see cref="SnowflakeAccessProjection"/>. A schemaless helper
    /// (null schema name) rebuilds the central schema — the twin of PG's unqualified call
    /// resolving through the search_path.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RebuildDenormalizedTableAsync(CancellationToken ct = default)
    {
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await SnowflakeAccessProjection.RebuildOnConnectionAsync(
            connection, _schemaName ?? _centralSchema, _centralSchema, _logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks if a user has a specific permission at a given node path, using the denormalized
    /// table with most-specific-prefix-wins logic. Uses Snowflake's <c>MAX_BY</c> aggregate when
    /// supported (the ranking term prefers longer prefixes, with the exact-user tie-break kept
    /// for shape-compatibility with the documented Snowflake form); otherwise PG's
    /// <c>ORDER BY LENGTH(...) DESC LIMIT 1</c> form, which ports unchanged.
    /// </summary>
    /// <param name="userId">The user to check.</param>
    /// <param name="nodePath">The node path the permission applies to.</param>
    /// <param name="permission">The permission name (e.g. <c>Read</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the most specific matching rule allows.</returns>
    public async Task<bool> HasPermissionAsync(string userId, string nodePath, string permission, CancellationToken ct = default)
    {
        var uepTable = Q("user_effective_permissions");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = _capabilities.Current.SupportsMaxBy
            ? $"""
              SELECT MAX_BY("is_allow", LENGTH("node_path_prefix") * 2 + IFF("user_id" = :user_id, 1, 0))
              FROM {uepTable}
              WHERE "user_id" = :user_id
                AND "permission" = :permission
                AND :node_path LIKE "node_path_prefix" || '%'
              """
            : $"""
              SELECT "is_allow"
              FROM {uepTable}
              WHERE "user_id" = :user_id
                AND "permission" = :permission
                AND :node_path LIKE "node_path_prefix" || '%'
              ORDER BY LENGTH("node_path_prefix") DESC
              LIMIT 1
              """;
        SnowflakeConnectionSource.AddParam(cmd, "user_id", userId, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "permission", permission, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "node_path", nodePath, DbType.String);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        // Defensive coercion: real Snowflake surfaces BOOLEAN as bool; the emulator may
        // transpile it to a numeric.
        return result switch
        {
            bool b => b,
            null or DBNull => false,
            _ => Convert.ToBoolean(result, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Gets all effective permissions for a user at a given node path — PG's
    /// ROW_NUMBER-in-derived-table form, which is fully supported by Snowflake and ports
    /// unchanged (most specific prefix wins per permission, allowed only).
    /// </summary>
    /// <param name="userId">The user to resolve.</param>
    /// <param name="nodePath">The node path the permissions apply to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The distinct allowed permission names.</returns>
    public async Task<IReadOnlyList<string>> GetEffectivePermissionsAsync(string userId, string nodePath, CancellationToken ct = default)
    {
        var uepTable = Q("user_effective_permissions");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT "permission"
            FROM (
                SELECT "permission", "is_allow",
                       ROW_NUMBER() OVER (PARTITION BY "permission" ORDER BY LENGTH("node_path_prefix") DESC) AS "rn"
                FROM {uepTable}
                WHERE "user_id" = :user_id
                  AND :node_path LIKE "node_path_prefix" || '%'
            ) sub
            WHERE sub."rn" = 1 AND sub."is_allow" = TRUE
            """;
        SnowflakeConnectionSource.AddParam(cmd, "user_id", userId, DbType.String);
        SnowflakeConnectionSource.AddParam(cmd, "node_path", nodePath, DbType.String);

        var permissions = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            permissions.Add(reader.GetString(0));
        }

        return permissions;
    }

    /// <summary>
    /// Grants or denies a permission for a subject at a node path. Stores in the
    /// <c>access_control</c> table (PG's <c>ON CONFLICT DO UPDATE</c> → MERGE, or DELETE+INSERT
    /// on the emulator) and rebuilds the denormalized permissions.
    /// </summary>
    /// <param name="nodePath">The node path the rule applies to (a path prefix).</param>
    /// <param name="subject">The user or group the rule applies to.</param>
    /// <param name="permission">The permission name.</param>
    /// <param name="isAllow"><c>true</c> to allow, <c>false</c> to deny.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task GrantAsync(string nodePath, string subject, string permission, bool isAllow, CancellationToken ct = default)
    {
        var acTable = Q("access_control");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

        void BindKey(DbCommand cmd)
        {
            SnowflakeConnectionSource.AddParam(cmd, "node_path", nodePath, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "subject", subject, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "permission", permission, DbType.String);
        }

        void BindAll(DbCommand cmd)
        {
            BindKey(cmd);
            SnowflakeConnectionSource.AddParam(cmd, "is_allow", isAllow, DbType.Boolean);
        }

        if (_capabilities.Current.SupportsMerge)
        {
            await using var merge = connection.CreateCommand();
            merge.CommandText = $"""
                MERGE INTO {acTable} AS t
                USING (SELECT :node_path AS "node_path", :subject AS "subject",
                              :permission AS "permission", :is_allow AS "is_allow") AS s
                ON t."node_path" = s."node_path" AND t."subject" = s."subject" AND t."permission" = s."permission"
                WHEN MATCHED THEN UPDATE SET "is_allow" = s."is_allow"
                WHEN NOT MATCHED THEN INSERT ("node_path", "subject", "permission", "is_allow")
                VALUES (s."node_path", s."subject", s."permission", s."is_allow")
                """;
            BindAll(merge);
            await merge.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText =
                    $"DELETE FROM {acTable} WHERE \"node_path\" = :node_path " +
                    "AND \"subject\" = :subject AND \"permission\" = :permission";
                BindKey(delete);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                $"INSERT INTO {acTable} (\"node_path\", \"subject\", \"permission\", \"is_allow\") " +
                "SELECT :node_path, :subject, :permission, :is_allow";
            BindAll(insert);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SnowflakeAccessProjection.RebuildOnConnectionAsync(
            connection, _schemaName ?? _centralSchema, _centralSchema, _logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes a permission for a subject at a node path and rebuilds the denormalized permissions.
    /// </summary>
    /// <param name="nodePath">The node path the rule applied to.</param>
    /// <param name="subject">The user or group the rule applied to.</param>
    /// <param name="permission">The permission name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RevokeAsync(string nodePath, string subject, string permission, CancellationToken ct = default)
    {
        var acTable = Q("access_control");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                DELETE FROM {acTable}
                WHERE "node_path" = :node_path AND "subject" = :subject AND "permission" = :permission
                """;
            SnowflakeConnectionSource.AddParam(cmd, "node_path", nodePath, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "subject", subject, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "permission", permission, DbType.String);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SnowflakeAccessProjection.RebuildOnConnectionAsync(
            connection, _schemaName ?? _centralSchema, _centralSchema, _logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a member to a group (PG's <c>ON CONFLICT DO NOTHING</c> → an insert-only MERGE, or a
    /// <c>WHERE NOT EXISTS</c> guarded INSERT on the emulator) and rebuilds the denormalized
    /// permissions.
    /// </summary>
    /// <param name="groupName">The group.</param>
    /// <param name="memberId">The member (user id, or another group for nesting).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AddGroupMemberAsync(string groupName, string memberId, CancellationToken ct = default)
    {
        var gmTable = Q("group_members");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = _capabilities.Current.SupportsMerge
                ? $"""
                  MERGE INTO {gmTable} AS t
                  USING (SELECT :group_name AS "group_name", :member_id AS "member_id") AS s
                  ON t."group_name" = s."group_name" AND t."member_id" = s."member_id"
                  WHEN NOT MATCHED THEN INSERT ("group_name", "member_id")
                  VALUES (s."group_name", s."member_id")
                  """
                : $"""
                  INSERT INTO {gmTable} ("group_name", "member_id")
                  SELECT :group_name, :member_id
                  FROM (SELECT 1 AS "x")
                  WHERE NOT EXISTS (
                      SELECT 1 FROM {gmTable}
                      WHERE "group_name" = :group_name AND "member_id" = :member_id)
                  """;
            SnowflakeConnectionSource.AddParam(cmd, "group_name", groupName, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "member_id", memberId, DbType.String);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SnowflakeAccessProjection.RebuildOnConnectionAsync(
            connection, _schemaName ?? _centralSchema, _centralSchema, _logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a member from a group and rebuilds the denormalized permissions.
    /// </summary>
    /// <param name="groupName">The group.</param>
    /// <param name="memberId">The member to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RemoveGroupMemberAsync(string groupName, string memberId, CancellationToken ct = default)
    {
        var gmTable = Q("group_members");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                DELETE FROM {gmTable}
                WHERE "group_name" = :group_name AND "member_id" = :member_id
                """;
            SnowflakeConnectionSource.AddParam(cmd, "group_name", groupName, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "member_id", memberId, DbType.String);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SnowflakeAccessProjection.RebuildOnConnectionAsync(
            connection, _schemaName ?? _centralSchema, _centralSchema, _logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a partition access policy at the specified namespace: upserts a <c>_Policy</c>
    /// MeshNode with per-permission switches (pass <c>false</c> for permissions to deny,
    /// <c>null</c> to inherit). PG's <c>jsonb_build_object</c> → <c>OBJECT_CONSTRUCT</c> (kept
    /// in the MERGE source select so the emulator's expression restrictions on plain VALUES
    /// don't apply); PG's generated <c>path</c> column is a REAL column here and therefore
    /// written explicitly. Rebuilds the denormalized permissions afterwards.
    /// </summary>
    /// <param name="targetNamespace">The namespace the policy caps (null/empty → schema root).</param>
    /// <param name="read">Read switch (false denies, null inherits).</param>
    /// <param name="create">Create switch.</param>
    /// <param name="update">Update switch.</param>
    /// <param name="delete">Delete switch.</param>
    /// <param name="comment">Comment switch.</param>
    /// <param name="breaksInheritance">When true, stamps the policy as inheritance-breaking.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetPolicyAsync(string targetNamespace, bool? read = null, bool? create = null, bool? update = null, bool? delete = null, bool? comment = null, bool breaksInheritance = false, CancellationToken ct = default)
    {
        var ns = targetNamespace ?? "";
        // Build the VARIANT content with per-permission fields — literal keys/booleans like PG.
        var contentParts = new List<string>();
        if (read.HasValue) contentParts.Add($"'read', {(read.Value ? "true" : "false")}");
        if (create.HasValue) contentParts.Add($"'create', {(create.Value ? "true" : "false")}");
        if (update.HasValue) contentParts.Add($"'update', {(update.Value ? "true" : "false")}");
        if (delete.HasValue) contentParts.Add($"'delete', {(delete.Value ? "true" : "false")}");
        if (comment.HasValue) contentParts.Add($"'comment', {(comment.Value ? "true" : "false")}");
        if (breaksInheritance) contentParts.Add("'breaksInheritance', true");

        var jsonBuild = $"OBJECT_CONSTRUCT({string.Join(", ", contentParts)})";

        var mnTable = Q("mesh_nodes");
        var mainNode = string.IsNullOrEmpty(ns) ? "_Policy" : $"{ns}/_Policy";
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

        void BindAll(DbCommand cmd)
        {
            SnowflakeConnectionSource.AddParam(cmd, "namespace", ns, DbType.String);
            SnowflakeConnectionSource.AddParam(cmd, "main_node", mainNode, DbType.String);
        }

        if (_capabilities.Current.SupportsMerge)
        {
            await using var merge = connection.CreateCommand();
            merge.CommandText = $"""
                MERGE INTO {mnTable} AS t
                USING (SELECT :namespace AS "namespace", '_Policy' AS "id", {jsonBuild} AS "content") AS s
                ON t."namespace" = s."namespace" AND t."id" = s."id"
                WHEN MATCHED THEN UPDATE SET
                    "content" = s."content",
                    "node_type" = 'PartitionAccessPolicy',
                    "name" = 'Access Policy',
                    "main_node" = :main_node,
                    "state" = 2
                WHEN NOT MATCHED THEN INSERT
                    ("namespace", "id", "path", "name", "node_type", "content", "main_node", "state")
                VALUES (s."namespace", s."id", :main_node, 'Access Policy', 'PartitionAccessPolicy',
                        s."content", :main_node, 2)
                """;
            BindAll(merge);
            await merge.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await using (var deleteCmd = connection.CreateCommand())
            {
                deleteCmd.CommandText =
                    $"DELETE FROM {mnTable} WHERE \"namespace\" = :namespace AND \"id\" = '_Policy'";
                SnowflakeConnectionSource.AddParam(deleteCmd, "namespace", ns, DbType.String);
                await deleteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                $"INSERT INTO {mnTable} " +
                "(\"namespace\", \"id\", \"path\", \"name\", \"node_type\", \"content\", \"main_node\", \"state\") " +
                $"SELECT :namespace, '_Policy', :main_node, 'Access Policy', 'PartitionAccessPolicy', {jsonBuild}, :main_node, 2";
            BindAll(insert);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SnowflakeAccessProjection.RebuildOnConnectionAsync(
            connection, _schemaName ?? _centralSchema, _centralSchema, _logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the partition access policy at the specified namespace and rebuilds the
    /// denormalized permissions.
    /// </summary>
    /// <param name="targetNamespace">The namespace whose policy is removed (null/empty → schema root).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RemovePolicyAsync(string targetNamespace, CancellationToken ct = default)
    {
        var ns = targetNamespace ?? "";
        var mnTable = Q("mesh_nodes");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                DELETE FROM {mnTable}
                WHERE "namespace" = :namespace AND "id" = '_Policy' AND "node_type" = 'PartitionAccessPolicy'
                """;
            SnowflakeConnectionSource.AddParam(cmd, "namespace", ns, DbType.String);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await SnowflakeAccessProjection.RebuildOnConnectionAsync(
            connection, _schemaName ?? _centralSchema, _centralSchema, _logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Syncs DI-registered <see cref="NodeTypePermission"/> records to the
    /// <c>node_type_permissions</c> table. Called at startup to populate the DB with
    /// module-declared permissions; idempotent. Where the PG code loops one
    /// <c>INSERT ... ON CONFLICT</c> per record, Snowflake round-trips are expensive, so the
    /// records batch into chunked multi-row MERGEs (UNION-ALL source); the emulator fallback is
    /// per-row DELETE + INSERT on one connection.
    /// </summary>
    /// <param name="permissions">The node-type permission flags to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SyncNodeTypePermissionsAsync(
        IEnumerable<NodeTypePermission> permissions,
        CancellationToken ct = default)
    {
        var rows = permissions.ToList();
        if (rows.Count == 0)
            return;

        var ntpTable = Q("node_type_permissions");
        await using var connection = await _source.OpenAsync(ct).ConfigureAwait(false);

        if (_capabilities.Current.SupportsMerge)
        {
            // 2 binds per row; chunk to keep statements well under driver/emulator limits.
            const int chunkSize = 200;
            foreach (var chunk in rows.Chunk(chunkSize))
            {
                await using var merge = connection.CreateCommand();
                var sourceRows = new List<string>(chunk.Length);
                for (var i = 0; i < chunk.Length; i++)
                {
                    sourceRows.Add(i == 0
                        ? $"SELECT :nt{i} AS \"node_type\", :pr{i} AS \"public_read\""
                        : $"SELECT :nt{i}, :pr{i}");
                    SnowflakeConnectionSource.AddParam(merge, $"nt{i}", chunk[i].NodeType, DbType.String);
                    SnowflakeConnectionSource.AddParam(merge, $"pr{i}", chunk[i].PublicRead, DbType.Boolean);
                }
                merge.CommandText = $"""
                    MERGE INTO {ntpTable} AS t
                    USING ({string.Join(" UNION ALL ", sourceRows)}) AS s
                    ON t."node_type" = s."node_type"
                    WHEN MATCHED THEN UPDATE SET "public_read" = s."public_read"
                    WHEN NOT MATCHED THEN INSERT ("node_type", "public_read")
                    VALUES (s."node_type", s."public_read")
                    """;
                await merge.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return;
        }

        foreach (var p in rows)
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = $"DELETE FROM {ntpTable} WHERE \"node_type\" = :node_type";
                SnowflakeConnectionSource.AddParam(delete, "node_type", p.NodeType, DbType.String);
                await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                $"INSERT INTO {ntpTable} (\"node_type\", \"public_read\") SELECT :node_type, :public_read";
            SnowflakeConnectionSource.AddParam(insert, "node_type", p.NodeType, DbType.String);
            SnowflakeConnectionSource.AddParam(insert, "public_read", p.PublicRead, DbType.Boolean);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
