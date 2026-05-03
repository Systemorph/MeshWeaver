using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Per-user partitions + drop the <c>User/</c> namespace prefix.
///
/// Today every user's data lives in the shared <c>user</c> Postgres schema with paths
/// like <c>User/{id}/...</c>. This is two problems in one:
///   1. The <c>User</c> prefix is dead weight — it forces every layout area / agent /
///      reference to spell out <c>User/rbuergi/...</c> even though the partition structure
///      already encodes the user.
///   2. A single schema for ALL users mixes their content, complicates per-user access
///      control, and makes "is this row in the right place?" checks ambiguous. One
///      schema per user mirrors the org pattern (acme/cornerstone).
///
/// This migration:
///   - Creates one Postgres schema per user (e.g., <c>rbuergi</c>, <c>orwell2000</c>).
///   - Moves rows from <c>"user".T</c> to <c>"&lt;userid&gt;".T</c> for every satellite table,
///     stripping the <c>User/&lt;userid&gt;</c> namespace prefix in flight.
///   - Repopulates <c>partition_access</c> and <c>&lt;userid&gt;.user_effective_permissions</c>.
///   - Inserts a <c>MeshDataSource</c> discovery record per new partition.
///   - Drops <c>"user"</c> + <c>"user_versions"</c> once verified empty.
/// </summary>
public sealed class V10_PerUserPartitions : IMigration
{
    public int Version => 10;
    public string Description => "Per-user partitions + drop User/ namespace prefix";

    public async Task RunAsync(MigrationContext ctx)
    {
        // 0. Pre-flight: does the "user" schema even exist? (fresh DBs skip everything)
        var userSchemaExists = await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user");
        if (!userSchemaExists)
        {
            ctx.Logger.LogInformation("Repair v10: no \"user\" schema present — skipping (fresh DB).");
            return;
        }

        // 1. Discover users: union of explicit User-typed nodes + path-derived ids
        //    (covers users with content but no User node).
        var userIds = new List<string>();
        await using (var listCmd = ctx.DataSource.CreateCommand("""
            SELECT DISTINCT id FROM "user".mesh_nodes WHERE node_type = 'User'
            UNION
            SELECT DISTINCT split_part(namespace, '/', 2) AS id
            FROM "user".mesh_nodes
            WHERE namespace LIKE 'User/%' AND split_part(namespace, '/', 2) <> ''
            """))
        {
            await using var rdr = await listCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) userIds.Add(rdr.GetString(0));
        }

        ctx.Logger.LogInformation("Repair v10: discovered {Count} user(s): [{Users}]",
            userIds.Count, string.Join(", ", userIds));

        // Pre-flight: count cross-user content (rows in `user` schema NOT under any
        // User/{id}/...). These would orphan if we just dropped `user`.
        long orphanCount;
        await using (var orphanCmd = ctx.DataSource.CreateCommand(
            "SELECT count(*) FROM \"user\".mesh_nodes WHERE namespace NOT LIKE 'User/%' AND namespace <> 'User' AND namespace <> ''"))
        {
            orphanCount = (long)(await orphanCmd.ExecuteScalarAsync())!;
        }
        if (orphanCount > 0)
        {
            ctx.Logger.LogWarning(
                "Repair v10: {Count} row(s) in \"user\" schema are NOT under User/<id>/... — they will be left in `user` and the schema will NOT be dropped. Inspect manually.",
                orphanCount);
        }

        // Discover satellite tables that have both `namespace` and `id` columns — those
        // are the ones whose rows are addressable by mesh path. Tables like
        // `change_logs` or `user_activity` use a different keying scheme and are
        // partition-scoped (not user-scoped), so they're left in place.
        var satelliteTables = new List<string>();
        await using (var tblCmd = ctx.DataSource.CreateCommand("""
            SELECT a.table_name
            FROM information_schema.columns a
            JOIN information_schema.columns b
              ON a.table_schema = b.table_schema AND a.table_name = b.table_name
             AND b.column_name = 'id'
            WHERE a.table_schema = 'user' AND a.column_name = 'namespace'
              AND a.table_name IN ('mesh_nodes', 'access', 'threads', 'code', 'annotations', 'activities', 'user_activities', 'partition_objects')
            GROUP BY a.table_name
            ORDER BY a.table_name
            """))
        {
            await using var rdr = await tblCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) satelliteTables.Add(rdr.GetString(0));
        }

        // 2. Per-user: bootstrap target schema (idempotent), move rows, rebuild perms.
        foreach (var userId in userIds)
        {
            await MigrateUserAsync(ctx, userId, satelliteTables);
        }

        // 7. Drop the legacy `Source/user` MeshDataSource record (if present).
        await using (var dropOldDs = ctx.DataSource.CreateCommand(
            "DELETE FROM admin.mesh_nodes WHERE namespace = 'Source' AND id = 'user'"))
        {
            await dropOldDs.ExecuteNonQueryAsync();
        }

        // 8. Wipe partition_access partition='user' rows — per-schema rebuild repopulated
        //    them under the new partition names.
        await using (var wipePa = ctx.DataSource.CreateCommand(
            "DELETE FROM public.partition_access WHERE partition = 'user'"))
        {
            await wipePa.ExecuteNonQueryAsync();
        }

        // 9. Verify and drop. Only drop if EVERY user-table is empty (orphans abort the drop).
        long residual = 0;
        foreach (var t in satelliteTables)
        {
            await using var countCmd = ctx.DataSource.CreateCommand($"SELECT count(*) FROM \"user\".\"{t}\"");
            residual += (long)(await countCmd.ExecuteScalarAsync())!;
        }
        if (residual == 0)
        {
            await using (var dropCmd = ctx.DataSource.CreateCommand("DROP SCHEMA \"user\" CASCADE"))
                await dropCmd.ExecuteNonQueryAsync();
            await using (var dropVCmd = ctx.DataSource.CreateCommand("DROP SCHEMA IF EXISTS \"user_versions\" CASCADE"))
                await dropVCmd.ExecuteNonQueryAsync();
            ctx.Logger.LogInformation("Repair v10: dropped \"user\" and \"user_versions\" schemas (empty)");
        }
        else
        {
            ctx.Logger.LogWarning(
                "Repair v10: \"user\" schema has {Residual} residual row(s) — NOT dropping. Inspect manually before next run.",
                residual);
        }
    }

    private static async Task MigrateUserAsync(MigrationContext ctx, string userId, List<string> satelliteTables)
    {
        var schemaName = SchemaHelpers.SanitizeSchemaName(userId);
        if (string.IsNullOrEmpty(schemaName)) return;

        var targetExists = await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, schemaName);
        if (targetExists)
        {
            ctx.Logger.LogInformation("Repair v10: schema \"{Schema}\" already exists — re-using (idempotent re-init)", schemaName);
        }

        // Bootstrap the target schema (mesh_nodes + satellites + _versions)
        await using var schemaDs = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, schemaName);

        await using (var createSchemaCmd = ctx.DataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{schemaName}\""))
            await createSchemaCmd.ExecuteNonQueryAsync();

        var versionsSchemaName = schemaName + "_versions";
        await using (var createVersionsCmd = ctx.DataSource.CreateCommand($"CREATE SCHEMA IF NOT EXISTS \"{versionsSchemaName}\""))
            await createVersionsCmd.ExecuteNonQueryAsync();

        // BuildSchemaDataSource wires SSL + AAD password provider for Azure — a raw
        // NpgsqlDataSourceBuilder skips both and dies with `28000: no pg_hba.conf entry`.
        await using var versionsDs = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, versionsSchemaName, useVector: false);

        var schemaOpts = SchemaHelpers.BuildSchemaOptions(ctx.ConnectionString, schemaName, ctx.Options.VectorDimensions);

        await PostgreSqlSchemaInitializer.InitializeWithVersionsSchemaAsync(
            ctx.DataSource, schemaDs, versionsDs, schemaOpts, versionsSchemaName);
        await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
            schemaDs, schemaOpts, MeshWeaver.Mesh.PartitionDefinition.StandardTableMappings.Values);

        // 3. Move rows for every satellite table. `path` is GENERATED — recomputes from
        //    the rewritten namespace. Use `information_schema.columns` to compute the
        //    column list dynamically per table, since satellites have slightly different
        //    shapes (no embedding, no description, etc.).
        foreach (var table in satelliteTables)
        {
            await MoveTableAsync(ctx, userId, schemaName, table);
        }

        // 4. Move history (mesh_node_history lives in the _versions schemas)
        var hasUserVersions = await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user_versions");
        if (hasUserVersions)
        {
            await MoveHistoryAsync(ctx, userId, versionsSchemaName);
        }

        // 5. Rebuild permissions for the new schema (re-syncs partition_access).
        try
        {
            await using var rebuildCmd = ctx.DataSource.CreateCommand(
                $"SELECT \"{schemaName}\".rebuild_user_effective_permissions()");
            await rebuildCmd.ExecuteNonQueryAsync();
            ctx.Logger.LogInformation("Repair v10: \"{Schema}\".rebuild_user_effective_permissions() OK", schemaName);
        }
        catch (Exception ex)
        {
            ctx.Logger.LogWarning(ex, "Repair v10: rebuild_user_effective_permissions failed for \"{Schema}\"", schemaName);
        }

        // 6. MeshDataSource discovery record — mirror what AddMeshDataSource writes
        //    for orgs (namespace="Source", nodeType="MeshDataSource").
        await using (var meshDsCmd = ctx.DataSource.CreateCommand("""
            INSERT INTO admin.mesh_nodes (namespace, id, name, node_type, state, content, last_modified, main_node)
            VALUES ('Source', $1, $1, 'MeshDataSource', 2,
                    jsonb_build_object('Partition', $1, 'StorageType', 'Postgres', 'ProviderType', 'PostgreSql'),
                    now(), $1)
            ON CONFLICT (namespace, id) DO UPDATE SET
                content = EXCLUDED.content,
                last_modified = now()
            """))
        {
            meshDsCmd.Parameters.AddWithValue(userId);
            await meshDsCmd.ExecuteNonQueryAsync();
        }
    }

    private static async Task MoveTableAsync(MigrationContext ctx, string userId, string schemaName, string table)
    {
        // Pull column names that exist on BOTH the source and the destination, excluding
        // the generated `path` column (must not be inserted).
        var commonCols = new List<string>();
        await using (var colCmd = ctx.DataSource.CreateCommand("""
            SELECT a.column_name
            FROM information_schema.columns a
            JOIN information_schema.columns b
              ON a.column_name = b.column_name
             AND b.table_schema = $2 AND b.table_name = $3
            WHERE a.table_schema = 'user' AND a.table_name = $1
              AND a.column_name <> 'path'
            ORDER BY a.ordinal_position
            """))
        {
            colCmd.Parameters.AddWithValue(table);
            colCmd.Parameters.AddWithValue(schemaName);
            colCmd.Parameters.AddWithValue(table);
            await using var rdr = await colCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) commonCols.Add(rdr.GetString(0));
        }

        if (commonCols.Count == 0)
        {
            ctx.Logger.LogDebug("Repair v10: skipping {Table} — no common columns between user and {Schema}", table, schemaName);
            return;
        }

        // SELECT projection: rewrite namespace + main_node, pass through the rest.
        var selectExprs = string.Join(", ", commonCols.Select(c => c switch
        {
            "namespace" =>
                // 'User/<userid>' → '', 'User/<userid>/x/y' → 'x/y'
                "CASE WHEN namespace = 'User/' || $1 THEN '' " +
                "ELSE regexp_replace(namespace, '^User/' || $1 || '/', '') END AS namespace",
            "main_node" =>
                "CASE WHEN main_node IS NULL THEN NULL " +
                "WHEN main_node = 'User/' || $1 THEN '' " +
                "ELSE regexp_replace(main_node, '^User/' || $1 || '/', '') END AS main_node",
            _ => $"\"{c}\""
        }));
        var insertCols = string.Join(", ", commonCols.Select(c => $"\"{c}\""));

        var moveSql = $"""
            WITH moved AS (
                DELETE FROM "user"."{table}"
                WHERE namespace = 'User/' || $1
                   OR namespace LIKE 'User/' || $1 || '/%'
                RETURNING {string.Join(", ", commonCols.Select(c => $"\"{c}\""))}
            )
            INSERT INTO "{schemaName}"."{table}" ({insertCols})
            SELECT {selectExprs} FROM moved
            ON CONFLICT (namespace, id) DO NOTHING
            """;

        await using var moveCmd = ctx.DataSource.CreateCommand(moveSql);
        moveCmd.Parameters.AddWithValue(userId);
        var moved = await moveCmd.ExecuteNonQueryAsync();
        if (moved > 0)
            ctx.Logger.LogInformation("Repair v10: \"{Schema}\".{Table} ← \"user\".{Table} — moved {Count} row(s)",
                schemaName, table, table, moved);
    }

    private static async Task MoveHistoryAsync(MigrationContext ctx, string userId, string versionsSchemaName)
    {
        // Same dynamic column-discovery pattern — `mesh_node_history` may be missing
        // `embedding` and other optional columns depending on when the schema was
        // first created.
        var histCols = new List<string>();
        await using (var histColCmd = ctx.DataSource.CreateCommand("""
            SELECT a.column_name
            FROM information_schema.columns a
            JOIN information_schema.columns b
              ON a.column_name = b.column_name
             AND b.table_schema = $1 AND b.table_name = 'mesh_node_history'
            WHERE a.table_schema = 'user_versions' AND a.table_name = 'mesh_node_history'
              AND a.column_name <> 'path'
            ORDER BY a.ordinal_position
            """))
        {
            histColCmd.Parameters.AddWithValue(versionsSchemaName);
            await using var rdr = await histColCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) histCols.Add(rdr.GetString(0));
        }

        if (histCols.Count == 0 || !histCols.Contains("namespace") || !histCols.Contains("id") || !histCols.Contains("version"))
            return;

        var histSelect = string.Join(", ", histCols.Select(c => c switch
        {
            "namespace" =>
                "CASE WHEN namespace = 'User/' || $1 THEN '' " +
                "ELSE regexp_replace(namespace, '^User/' || $1 || '/', '') END AS namespace",
            "main_node" =>
                "CASE WHEN main_node IS NULL THEN NULL " +
                "WHEN main_node = 'User/' || $1 THEN '' " +
                "ELSE regexp_replace(main_node, '^User/' || $1 || '/', '') END AS main_node",
            _ => $"\"{c}\""
        }));
        var histReturning = string.Join(", ", histCols.Select(c => $"\"{c}\""));
        var histInsertCols = string.Join(", ", histCols.Select(c => $"\"{c}\""));

        var histSql = $"""
            WITH moved AS (
                DELETE FROM "user_versions".mesh_node_history
                WHERE namespace = 'User/' || $1
                   OR namespace LIKE 'User/' || $1 || '/%'
                RETURNING {histReturning}
            )
            INSERT INTO "{versionsSchemaName}".mesh_node_history ({histInsertCols})
            SELECT {histSelect} FROM moved
            ON CONFLICT (namespace, id, version) DO NOTHING
            """;
        await using var moveHistCmd = ctx.DataSource.CreateCommand(histSql);
        moveHistCmd.Parameters.AddWithValue(userId);
        var movedHist = await moveHistCmd.ExecuteNonQueryAsync();
        if (movedHist > 0)
            ctx.Logger.LogInformation("Repair v10: \"{V}\".mesh_node_history ← \"user_versions\" — moved {Count} row(s)",
                versionsSchemaName, movedHist);
    }
}
