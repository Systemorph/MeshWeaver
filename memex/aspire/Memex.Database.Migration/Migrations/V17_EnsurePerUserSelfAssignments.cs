using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Ensures every per-user partition schema has a self-assignment (<c>Admin</c> role) in
/// its <c>access</c> satellite table.
///
/// <para>V05 created self-assignments in the old monolithic <c>user</c> schema. V10 moved
/// them into per-user schemas. V14 prepended the partition prefix to all namespaces. None
/// of these migrations covered users who were:</para>
/// <list type="bullet">
///   <item>Created after V10 ran and whose <c>UserScopeGrantHandler</c> call failed
///         silently (fire-and-forget Subscribe).</item>
///   <item>In the <c>user</c> schema when it was dropped — their access row could have
///         been missed if the V10 move ran before V05 populated it.</item>
///   <item>Migrated by V10 with the wrong namespace shape and then stripped again by V16.</item>
/// </list>
///
/// <para>The correct shape after V14 is:</para>
/// <list type="bullet">
///   <item><c>{userId}.access</c> — the satellite table in the user's own Postgres schema</item>
///   <item>namespace = <c>{userId}/_Access</c> (WITH partition prefix, per AGENTS.md convention)</item>
///   <item>id = <c>{userId}_Access</c></item>
///   <item>main_node = <c>{userId}</c></item>
///   <item>content = <c>{"accessObject":"{userId}","displayName":"{userId}","roles":[{"role":"Admin"}]}</c></item>
/// </list>
///
/// <para>Idempotent: the <c>ON CONFLICT DO NOTHING</c> guard skips users who already have
/// a conforming row. Ends with a full <c>rebuild_user_effective_permissions()</c> sweep
/// across every touched schema so permissions take effect immediately on next portal start.</para>
/// </summary>
public sealed class V17_EnsurePerUserSelfAssignments : IMigration
{
    public int Version => 17;
    public string Description => "Ensure per-user self-assignment (Admin) in every user partition schema";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Discover per-user schemas: schemas that have BOTH mesh_nodes AND access tables,
        // AND contain the user-identity row at (namespace='', id=<userId>, node_type='User').
        // This excludes org schemas (they have a Group identity, not a User identity) and
        // the admin schema. We collect (schemaName, userId) pairs.
        var userSchemas = new List<(string Schema, string UserId)>();

        await using (var discoverCmd = ctx.DataSource.CreateCommand("""
            SELECT t.table_schema
            FROM information_schema.tables t
            WHERE t.table_name = 'access'
              AND t.table_schema NOT IN ('information_schema','pg_catalog','pg_toast','public','admin')
              AND t.table_schema NOT LIKE '%_versions'
            ORDER BY t.table_schema
            """))
        await using (var rdr = await discoverCmd.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                userSchemas.Add((rdr.GetString(0), string.Empty));  // userId filled below
        }

        // For each schema, look up the User identity row to get the canonical userId.
        var confirmed = new List<(string Schema, string UserId)>();
        foreach (var (schema, _) in userSchemas)
        {
            // mesh_nodes may not exist (shouldn't happen but be defensive)
            bool hasMeshNodes;
            await using (var chkCmd = ctx.DataSource.CreateCommand("""
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = $1 AND table_name = 'mesh_nodes'
                LIMIT 1
                """))
            {
                chkCmd.Parameters.AddWithValue(schema);
                var result = await chkCmd.ExecuteScalarAsync();
                hasMeshNodes = result is not null;
            }
            if (!hasMeshNodes) continue;

            // User identity row: namespace='', node_type='User', id=<userId>
            // The special case documented in AGENTS.md and V14.
            string? userId = null;
            await using (var idCmd = ctx.DataSource.CreateCommand($"""
                SELECT id FROM "{schema.Replace("\"", "\"\"")}".mesh_nodes
                WHERE namespace = '' AND node_type = 'User'
                LIMIT 1
                """))
            await using (var idRdr = await idCmd.ExecuteReaderAsync())
            {
                if (await idRdr.ReadAsync())
                    userId = idRdr.IsDBNull(0) ? null : idRdr.GetString(0);
            }

            if (string.IsNullOrEmpty(userId))
                continue;  // org partition or not a user partition — skip

            confirmed.Add((schema, userId));
        }

        ctx.Logger.LogInformation(
            "Repair v17: found {Count} per-user schema(s): [{Schemas}]",
            confirmed.Count, string.Join(", ", confirmed.Select(t => t.Schema)));

        var inserted = 0;
        var rebuilt = 0;
        foreach (var (schema, userId) in confirmed)
        {
            var quotedSchema = schema.Replace("\"", "\"\"");
            var quotedUserId = userId.Replace("'", "''");

            // Expected namespace for the self-assignment in the post-V14 convention:
            // namespace = '{userId}/_Access'  (WITH partition prefix)
            var ns = $"{userId}/_Access";
            var id = $"{userId}_Access";
            var mainNode = userId;

            // Check existence first to keep the log noise low.
            bool exists;
            await using (var chkCmd = ctx.DataSource.CreateCommand($"""
                SELECT 1 FROM "{quotedSchema}".access
                WHERE namespace = $1
                  AND content->>'accessObject' = $2
                LIMIT 1
                """))
            {
                chkCmd.Parameters.AddWithValue(ns);
                chkCmd.Parameters.AddWithValue(userId);
                exists = await chkCmd.ExecuteScalarAsync() is not null;
            }

            if (exists)
            {
                ctx.Logger.LogDebug("Repair v17: '{Schema}' — self-assignment already present, skipping", schema);
                continue;
            }

            // Insert the missing self-assignment. ON CONFLICT DO NOTHING makes this
            // idempotent even under concurrent runs.
            await using (var insertCmd = ctx.DataSource.CreateCommand($"""
                INSERT INTO "{quotedSchema}".access
                    (namespace, id, name, node_type, state, content, main_node, last_modified, version)
                VALUES ($1, $2, $3, 'AccessAssignment', 2,
                    jsonb_build_object(
                        'accessObject', $4,
                        'displayName', $4,
                        'roles', jsonb_build_array(jsonb_build_object('role', 'Admin'))
                    ),
                    $5, now(), 1)
                ON CONFLICT (namespace, id) DO NOTHING
                """))
            {
                insertCmd.Parameters.AddWithValue(ns);
                insertCmd.Parameters.AddWithValue(id);
                insertCmd.Parameters.AddWithValue($"{userId} Access");
                insertCmd.Parameters.AddWithValue(userId);
                insertCmd.Parameters.AddWithValue(mainNode);
                var rows = await insertCmd.ExecuteNonQueryAsync();
                if (rows > 0)
                {
                    ctx.Logger.LogInformation(
                        "Repair v17: '{Schema}' — inserted self-assignment for user '{UserId}'",
                        schema, userId);
                    inserted++;
                }
            }

            // Rebuild permissions for this schema so the new assignment is reflected
            // immediately in user_effective_permissions without requiring a portal restart.
            try
            {
                await using var rebuildCmd = ctx.DataSource.CreateCommand(
                    $"SELECT \"{quotedSchema}\".rebuild_user_effective_permissions()");
                await rebuildCmd.ExecuteNonQueryAsync();
                rebuilt++;
                ctx.Logger.LogDebug("Repair v17: '{Schema}'.rebuild_user_effective_permissions() OK", schema);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex,
                    "Repair v17: rebuild_user_effective_permissions failed for '{Schema}'", schema);
            }
        }

        ctx.Logger.LogInformation(
            "Repair v17: done — {Inserted} self-assignment(s) inserted, {Rebuilt} schema(s) rebuilt",
            inserted, rebuilt);
    }
}
