using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Final cleanup of the legacy <c>user</c> schema. V14 swept User-typed identity
/// rows but left two classes of residuals that surfaced on live deployments:
///
///   1. <b>Stale ApiToken duplicates</b> — rows like
///      <c>(ns='User/&lt;userid&gt;/ApiToken', id=&lt;hashPrefix&gt;)</c> written by the
///      pre-fix <see cref="Memex.Portal.Shared.Authentication.ApiTokenService"/>
///      whose post-fix replacements already exist at
///      <c>(ns='&lt;userid&gt;/ApiToken', id=&lt;hashPrefix&gt;)</c> in the user partition.
///      We move the row into the user partition under the correct namespace; if a
///      newer row already lives at the target, the upsert keeps the newer one
///      (last_modified comparison) and the stale row is dropped on DELETE.
///
///   2. <b>Stranded NodeType-meta records</b> — e.g.
///      <c>(ns='', id='User', node_type='User')</c>. The runtime re-registers
///      these via <c>IStaticNodeProvider</c> on every startup, so the persisted
///      copy is dead weight. Delete it.
///
/// Once both classes are gone, drop <c>user</c> + <c>user_versions</c> if every
/// user-schema satellite is empty.
/// </summary>
public sealed class V15_FinalUserSchemaCleanup : IMigration
{
    public int Version => 15;
    public string Description => "Sweep stale ApiToken/NodeType-meta residuals from legacy user schema and drop it";

    public async Task RunAsync(MigrationContext ctx)
    {
        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user"))
            return;

        // 1. Move stale ApiToken rows from user.mesh_nodes (User/<id>/ApiToken/...)
        //    into the per-user partition's mesh_nodes table, namespace=<id>/ApiToken.
        var apiTokenRows = new List<(string Namespace, string Id, string UserId)>();
        await using (var listCmd = ctx.DataSource.CreateCommand("""
            SELECT namespace, id, split_part(namespace, '/', 2) AS user_id
            FROM "user".mesh_nodes
            WHERE namespace LIKE 'User/%/ApiToken'
            """))
        {
            await using var rdr = await listCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                apiTokenRows.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)));
        }

        foreach (var (sourceNs, id, userId) in apiTokenRows)
        {
            if (string.IsNullOrEmpty(userId)) continue;
            var schemaName = SchemaHelpers.SanitizeSchemaName(userId);
            if (string.IsNullOrEmpty(schemaName)) continue;
            if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, schemaName))
            {
                ctx.Logger.LogWarning(
                    "Repair v15: ApiToken {Id} in user.mesh_nodes targets non-existent partition '{Schema}'. Leaving in place.",
                    id, schemaName);
                continue;
            }

            var targetNs = $"{userId}/ApiToken";
            await using var moveCmd = ctx.DataSource.CreateCommand($"""
                INSERT INTO "{schemaName}".mesh_nodes
                    (namespace, id, name, node_type, description, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node, embedding)
                SELECT $1, id, name, node_type, description, category, icon, display_order,
                       last_modified, version, state, content, desired_id, $2, embedding
                FROM "user".mesh_nodes
                WHERE namespace = $3 AND id = $4
                ON CONFLICT (namespace, id) DO UPDATE SET
                    -- Keep whichever copy is newer; the orphan in user.mesh_nodes
                    -- is usually a stale write that the user already replaced.
                    content = CASE
                        WHEN EXCLUDED.last_modified > "{schemaName}".mesh_nodes.last_modified
                        THEN EXCLUDED.content ELSE "{schemaName}".mesh_nodes.content END,
                    last_modified = GREATEST("{schemaName}".mesh_nodes.last_modified, EXCLUDED.last_modified)
                """);
            moveCmd.Parameters.AddWithValue(targetNs);
            moveCmd.Parameters.AddWithValue(userId);
            moveCmd.Parameters.AddWithValue(sourceNs);
            moveCmd.Parameters.AddWithValue(id);
            await moveCmd.ExecuteNonQueryAsync();

            await using var deleteCmd = ctx.DataSource.CreateCommand("""
                DELETE FROM "user".mesh_nodes WHERE namespace = $1 AND id = $2
                """);
            deleteCmd.Parameters.AddWithValue(sourceNs);
            deleteCmd.Parameters.AddWithValue(id);
            await deleteCmd.ExecuteNonQueryAsync();

            ctx.Logger.LogInformation(
                "Repair v15: moved ApiToken {Id} from \"user\".(ns={Source}) to \"{Schema}\".(ns={Target})",
                id, sourceNs, schemaName, targetNs);
        }

        // 2. Delete stranded NodeType meta rows. These are recreated on every
        //    startup by IStaticNodeProvider implementations (see UserNodeType.cs
        //    and similar), so the persisted copy is just legacy noise.
        await using (var delMeta = ctx.DataSource.CreateCommand("""
            DELETE FROM "user".mesh_nodes
            WHERE namespace = '' AND node_type = id
            """))
        {
            var n = await delMeta.ExecuteNonQueryAsync();
            if (n > 0) ctx.Logger.LogInformation(
                "Repair v15: dropped {Count} stranded NodeType-meta row(s) from user.mesh_nodes", n);
        }

        // 3. Drop user/user_versions if everything's empty.
        long residual;
        await using (var countCmd = ctx.DataSource.CreateCommand("""
            SELECT COALESCE(SUM(n), 0)::bigint FROM (
                SELECT count(*) AS n FROM "user".mesh_nodes
                UNION ALL SELECT count(*) FROM "user".access
                UNION ALL SELECT count(*) FROM "user".threads
                UNION ALL SELECT count(*) FROM "user".code
                UNION ALL SELECT count(*) FROM "user".annotations
                UNION ALL SELECT count(*) FROM "user".activities
            ) sub
            """))
        {
            try { residual = (long)(await countCmd.ExecuteScalarAsync())!; }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Repair v15: could not count user-schema residuals; not dropping.");
                return;
            }
        }

        if (residual > 0)
        {
            ctx.Logger.LogWarning(
                "Repair v15: legacy \"user\" schema still has {Residual} unrecognised row(s) — NOT dropping. Inspect manually.",
                residual);
            return;
        }

        await using (var drop = ctx.DataSource.CreateCommand("DROP SCHEMA \"user\" CASCADE"))
            await drop.ExecuteNonQueryAsync();
        await using (var dropV = ctx.DataSource.CreateCommand("DROP SCHEMA IF EXISTS \"user_versions\" CASCADE"))
            await dropV.ExecuteNonQueryAsync();
        ctx.Logger.LogInformation("Repair v15: dropped legacy \"user\" + \"user_versions\" schemas (empty)");
    }
}
