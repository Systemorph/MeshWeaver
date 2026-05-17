using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Final sweep of legacy User-typed rows still landing in
/// <c>user.mesh_nodes</c> with <c>namespace='User'</c>.
///
/// <para><see cref="V14_AddPartitionPrefixToNamespaces"/> already does the
/// move, but a post-V14 onboarding bug in <c>Onboarding.razor</c> kept
/// writing <c>new MeshNode(username, "User")</c> for every new signup,
/// re-creating the legacy row on every onboarding. The bug is fixed in the
/// same commit as this migration (onboarding now writes
/// <c>new MeshNode(username)</c> with empty namespace). This migration
/// cleans up the strays so <c>/&lt;username&gt;</c> finds the User node in
/// the user's own partition root via standard path routing — no synth, no
/// cross-partition lookup.</para>
///
/// <para>Same shape as V14's user-move: INSERT into <c>{username}.mesh_nodes</c>
/// at <c>(namespace='', id=username)</c> on conflict update with the newer
/// timestamp; then DELETE the legacy row from <c>user.mesh_nodes</c>.</para>
/// </summary>
public sealed class V20_RemoveStrayLegacyUserRows : IMigration
{
    public int Version => 20;
    public string Description =>
        "Move stray User-typed rows from user.mesh_nodes (namespace=User) to {username}.mesh_nodes (namespace='')";

    public async Task RunAsync(MigrationContext ctx)
    {
        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user"))
            return;

        var userRows = new List<string>();
        await using (var listCmd = ctx.DataSource.CreateCommand("""
            SELECT id FROM "user".mesh_nodes
            WHERE node_type = 'User' AND namespace = 'User'
            """))
        {
            await using var rdr = await listCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) userRows.Add(rdr.GetString(0));
        }

        if (userRows.Count == 0)
        {
            ctx.Logger.LogInformation(
                "Repair v20: no stray User rows in user.mesh_nodes (namespace=User) — nothing to do");
            return;
        }

        var movedCount = 0;
        var skippedCount = 0;
        foreach (var userId in userRows)
        {
            var schemaName = SchemaHelpers.SanitizeSchemaName(userId);
            if (string.IsNullOrEmpty(schemaName))
            {
                skippedCount++;
                continue;
            }
            if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, schemaName))
            {
                ctx.Logger.LogWarning(
                    "Repair v20: User '{UserId}' has a stray legacy row but no per-user schema '{Schema}' exists — leaving in place",
                    userId, schemaName);
                skippedCount++;
                continue;
            }

            // INSERT (selecting empty namespace) then DELETE. ON CONFLICT update with
            // the newer last_modified — if the per-user partition already has a User
            // row at (ns='', id=<userId>), keep the freshest content.
            await using var moveCmd = ctx.DataSource.CreateCommand($"""
                INSERT INTO "{schemaName}".mesh_nodes
                    (namespace, id, name, node_type, description, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node, embedding)
                SELECT '', id, name, node_type, description, category, icon, display_order,
                       last_modified, version, state, content, desired_id, NULL, embedding
                FROM "user".mesh_nodes
                WHERE node_type = 'User' AND namespace = 'User' AND id = $1
                ON CONFLICT (namespace, id) DO UPDATE SET
                    content = EXCLUDED.content,
                    name = COALESCE(EXCLUDED.name, "{schemaName}".mesh_nodes.name),
                    icon = COALESCE(EXCLUDED.icon, "{schemaName}".mesh_nodes.icon),
                    last_modified = GREATEST("{schemaName}".mesh_nodes.last_modified, EXCLUDED.last_modified),
                    version = "{schemaName}".mesh_nodes.version + 1
                """);
            moveCmd.Parameters.AddWithValue(userId);
            var moved = await moveCmd.ExecuteNonQueryAsync();

            await using var deleteCmd = ctx.DataSource.CreateCommand("""
                DELETE FROM "user".mesh_nodes WHERE node_type = 'User' AND namespace = 'User' AND id = $1
                """);
            deleteCmd.Parameters.AddWithValue(userId);
            await deleteCmd.ExecuteNonQueryAsync();

            ctx.Logger.LogInformation(
                "Repair v20: moved User '{UserId}' from legacy user.mesh_nodes to {Schema}.mesh_nodes (rows affected: {Count})",
                userId, schemaName, moved);
            movedCount++;
        }

        ctx.Logger.LogInformation(
            "Repair v20: moved {Moved} User identity row(s); skipped {Skipped}",
            movedCount, skippedCount);
    }
}
