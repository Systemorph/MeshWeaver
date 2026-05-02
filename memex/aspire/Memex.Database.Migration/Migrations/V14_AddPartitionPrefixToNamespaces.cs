using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Re-add the partition prefix to V10-migrated namespaces, and sweep any residuals
/// out of the legacy <c>user</c> schema.
///
/// V10 stripped the entire <c>User/&lt;userid&gt;/</c> prefix when moving rows into per-user
/// schemas. The current convention (see CLAUDE.md and how org partitions like
/// <c>partnerre</c>/<c>systemorph</c> store their rows) is that <c>namespace</c> KEEPS the
/// partition prefix — e.g., <c>(ns='rbuergi/Notes', id='foo')</c>, not <c>(ns='Notes')</c>.
///
/// V10-migrated content thus became invisible to every code path that queries via
/// <c>namespace:&lt;partition&gt;/...</c> or browses the user node's children. This migration:
///
/// 1. Sweeps any User-type rows still in the legacy <c>"user".mesh_nodes</c> schema into
///    their per-user partition root (namespace=<c>''</c>, id=<c>&lt;userid&gt;</c>).
/// 2. For every partition with a <c>MeshDataSource</c> record in <c>admin.mesh_nodes</c>:
///    rewrites <c>namespace</c> + <c>main_node</c> in every satellite table to include the
///    partition prefix, leaving the user-identity row at
///    <c>(ns='', id=&lt;userid&gt;, node_type='User')</c> as the documented special case.
/// 3. Drops <c>"user"</c> + <c>"user_versions"</c> schemas if empty after the sweep.
///
/// Idempotent: rows that already have the prefix are matched by the
/// <c>NOT LIKE '&lt;P&gt;/%'</c> guard and left untouched.
/// </summary>
public sealed class V14_AddPartitionPrefixToNamespaces : IMigration
{
    public int Version => 14;
    public string Description => "Restore partition prefix on V10-migrated namespaces; sweep user-schema residuals";

    public async Task RunAsync(MigrationContext ctx)
    {
        // 1. Sweep User-type residuals out of "user".mesh_nodes (if the schema is still around).
        await SweepLegacyUserSchemaAsync(ctx);

        // 2. Discover all partition prefixes from admin.mesh_nodes (Source records).
        var partitions = await DiscoverPartitionsAsync(ctx);
        ctx.Logger.LogInformation("Repair v14: discovered {Count} partition(s) to inspect: [{Partitions}]",
            partitions.Count, string.Join(", ", partitions.Select(p => $"{p.Partition}->{p.Schema}")));

        // 3. For each partition: prepend prefix where missing.
        foreach (var (partitionId, schemaName) in partitions)
        {
            await RewritePartitionAsync(ctx, partitionId, schemaName);
        }

        // 4. Drop user/user_versions if empty.
        await TryDropLegacyUserSchemaAsync(ctx);
    }

    private static async Task SweepLegacyUserSchemaAsync(MigrationContext ctx)
    {
        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user"))
            return;

        // Move any User-typed rows still in user.mesh_nodes into their per-user partition root.
        // These would have been written by code paths that still target the legacy User namespace
        // even after V10 moved everything else.
        var userRows = new List<(string Namespace, string Id)>();
        await using (var listCmd = ctx.DataSource.CreateCommand("""
            SELECT namespace, id FROM "user".mesh_nodes
            WHERE node_type = 'User' AND namespace = 'User'
            """))
        {
            await using var rdr = await listCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) userRows.Add((rdr.GetString(0), rdr.GetString(1)));
        }

        foreach (var (_, userId) in userRows)
        {
            var schemaName = SchemaHelpers.SanitizeSchemaName(userId);
            if (string.IsNullOrEmpty(schemaName)) continue;
            if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, schemaName))
            {
                ctx.Logger.LogWarning(
                    "Repair v14: User '{UserId}' has rows in legacy user schema but no per-user schema '{Schema}' exists. Leaving in place.",
                    userId, schemaName);
                continue;
            }

            // INSERT (selecting empty namespace) then DELETE. Use ON CONFLICT to make idempotent
            // — if the per-user partition already has a User row at (ns='', id=<userId>), the
            // newer last_modified wins.
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
                "Repair v14: moved User identity '{UserId}' from legacy user schema to '{Schema}' (rows affected: {Count})",
                userId, schemaName, moved);
        }
    }

    private static async Task<List<(string Partition, string Schema)>> DiscoverPartitionsAsync(MigrationContext ctx)
    {
        var partitions = new List<(string, string)>();
        // MeshDataSource records live at (ns='Source', id=<partitionId>) in admin.mesh_nodes.
        await using var cmd = ctx.DataSource.CreateCommand("""
            SELECT id FROM admin.mesh_nodes
            WHERE namespace = 'Source' AND node_type = 'MeshDataSource'
            ORDER BY id
            """);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var partitionId = rdr.GetString(0);
            var schemaName = SchemaHelpers.SanitizeSchemaName(partitionId);
            if (string.IsNullOrEmpty(schemaName)) continue;
            if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, schemaName)) continue;
            partitions.Add((partitionId, schemaName));
        }
        return partitions;
    }

    private static async Task RewritePartitionAsync(MigrationContext ctx, string partitionId, string schemaName)
    {
        // Discover tables in this schema that have BOTH namespace and main_node — these are
        // the satellite-shaped tables (mesh_nodes, access, threads, code, annotations, activities, ...).
        var tables = new List<string>();
        await using (var tblCmd = ctx.DataSource.CreateCommand("""
            SELECT table_name
            FROM information_schema.columns
            WHERE table_schema = $1 AND column_name IN ('namespace', 'main_node')
            GROUP BY table_name
            HAVING COUNT(DISTINCT column_name) = 2
            ORDER BY table_name
            """))
        {
            tblCmd.Parameters.AddWithValue(schemaName);
            await using var rdr = await tblCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) tables.Add(rdr.GetString(0));
        }

        var totalRows = 0;
        foreach (var table in tables)
        {
            // Two-step rewrite per table:
            //   (a) rows where namespace = '' AND not the user-identity row
            //       → namespace = partitionId
            //   (b) rows where namespace doesn't already start with partitionId/ AND not equal to partitionId
            //       → namespace = partitionId || '/' || namespace
            // main_node mirrors the same rule (NULL preserved).
            //
            // The user-identity special case: (node_type='User' AND id=partitionId AND namespace='')
            // — leave untouched. CLAUDE.md documents this as the documented exception.
            //
            // partitionId is interpolated (validated source: admin.mesh_nodes ids that we control)
            // because Postgres LIKE patterns with parameters are awkward when we also need
            // concatenation in the SET clause.
            var quotedPid = partitionId.Replace("'", "''");
            await using var rewriteCmd = ctx.DataSource.CreateCommand($"""
                UPDATE "{schemaName}"."{table}" SET
                    namespace = CASE
                        WHEN namespace = '' THEN '{quotedPid}'
                        ELSE '{quotedPid}/' || namespace
                    END,
                    main_node = CASE
                        WHEN main_node IS NULL THEN NULL
                        WHEN main_node = '' THEN '{quotedPid}'
                        WHEN main_node = '{quotedPid}' THEN main_node
                        WHEN main_node LIKE '{quotedPid}/%' THEN main_node
                        ELSE '{quotedPid}/' || main_node
                    END
                WHERE
                    -- Skip rows that already conform to the convention.
                    namespace <> '{quotedPid}'
                    AND namespace NOT LIKE '{quotedPid}/%'
                    -- Skip the user-identity special case.
                    AND NOT (namespace = '' AND id = '{quotedPid}' AND node_type = 'User')
                """);
            var affected = await rewriteCmd.ExecuteNonQueryAsync();
            if (affected > 0)
            {
                ctx.Logger.LogInformation(
                    "Repair v14: \"{Schema}\".{Table} — prepended prefix to {Count} row(s)",
                    schemaName, table, affected);
                totalRows += affected;
            }
        }

        if (totalRows > 0)
            ctx.Logger.LogInformation("Repair v14: \"{Schema}\" total rows updated: {Total}", schemaName, totalRows);
    }

    private static async Task TryDropLegacyUserSchemaAsync(MigrationContext ctx)
    {
        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user"))
            return;

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
                // One of the satellite tables may not exist on older deployments — be lenient.
                ctx.Logger.LogWarning(ex, "Repair v14: could not count user-schema residuals; not dropping.");
                return;
            }
        }

        if (residual > 0)
        {
            ctx.Logger.LogWarning(
                "Repair v14: legacy \"user\" schema still has {Residual} row(s) — NOT dropping. Inspect manually.",
                residual);
            return;
        }

        await using (var drop = ctx.DataSource.CreateCommand("DROP SCHEMA \"user\" CASCADE"))
            await drop.ExecuteNonQueryAsync();
        await using (var dropV = ctx.DataSource.CreateCommand("DROP SCHEMA IF EXISTS \"user_versions\" CASCADE"))
            await dropV.ExecuteNonQueryAsync();
        ctx.Logger.LogInformation("Repair v14: dropped legacy \"user\" + \"user_versions\" schemas (empty)");
    }
}
