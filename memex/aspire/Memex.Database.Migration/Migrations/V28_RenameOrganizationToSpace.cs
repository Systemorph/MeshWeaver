using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Renames the <c>Organization</c> NodeType to <c>Space</c> and removes the now-redundant
/// per-tenant <c>Partition</c> MeshNodes from <c>admin.mesh_nodes</c>.
///
/// <para><b>Why</b>: Spaces (formerly Organizations) and Users are the tenant roots —
/// each owns its own Postgres schema. The dedicated <c>Partition</c> MeshNode emitted by
/// the post-creation handlers was duplicate metadata: the routing layer already derives
/// the schema name from the first path segment via <c>PgPartitionCache.Probe</c> +
/// <c>PostgreSqlPathRoutingAdapter.AdapterForWriteState</c> (PendingCreate state →
/// lazy CREATE SCHEMA). Dropping the explicit records simplifies onboarding and removes
/// one source of partition-routing truth.</para>
///
/// <para><b>How</b>:
/// <list type="number">
///   <item>UPDATE <c>node_type='Organization'</c> → <c>'Space'</c> in every partition's
///         <c>mesh_nodes</c>.</item>
///   <item>DELETE the orphaned NodeType-registry row (<c>namespace=''</c>,
///         <c>id='Organization'</c>, <c>node_type='NodeType'</c>) left behind because
///         <c>StaticMeshNodeListProvider</c> upserts but never deletes.</item>
///   <item>DELETE per-tenant <c>Partition</c> rows from <c>admin.mesh_nodes</c>.
///         Keep the system-partition records (<c>Admin</c>, <c>Auth</c>, <c>Portal</c>,
///         <c>Kernel</c>, <c>_Access</c>, <c>_Activity</c>, <c>_UserActivity</c>,
///         <c>_Thread</c>) — those carry non-derivable routing config (<c>TableMappings</c>,
///         <c>Versioned</c>, special satellite-as-primary mapping).</item>
///   <item>CREATE OR REPLACE the V27 mirror function with <c>'Space'</c> added to the
///         filter. The trigger NAME on every partition is unchanged
///         (<c>mesh_node_mirror_access_objects</c>) so the existing trigger picks up the
///         new function body automatically; no per-partition re-iteration needed.</item>
///   <item>Backfill existing Space rows from every partition into <c>auth.mesh_nodes</c>
///         (matches V27's ApiToken backfill — the trigger only covers writes from V28
///         onward, so pre-V28 Organization-now-Space rows need an explicit copy).</item>
/// </list></para>
///
/// <para><b>Idempotent</b>: all UPDATE/DELETE statements are restartable; the function
/// is <c>CREATE OR REPLACE</c>; the backfill uses <c>ON CONFLICT DO NOTHING</c>. Safe to
/// re-run.</para>
/// </summary>
public sealed class V28_RenameOrganizationToSpace : IMigration
{
    public int Version => 28;
    public string Description => "Rename Organization → Space; drop per-tenant Partition rows; extend auth mirror to Space";

    private static readonly string[] SystemPartitionIds =
        ["Admin", "Auth", "Portal", "Kernel", "_Access", "_Activity", "_UserActivity", "_Thread"];

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);
        if (schemas.Count == 0)
        {
            ctx.Logger.LogInformation("Repair v28: no content-partition schemas found — skipping rename phase");
        }

        // 1 + 2. Rename Organization → Space and remove the orphaned NodeType-registry row.
        foreach (var schema in schemas)
        {
            int renamed;
            await using (var cmd = ctx.DataSource.CreateCommand($"""
                UPDATE "{schema}".mesh_nodes
                   SET node_type = 'Space'
                 WHERE node_type = 'Organization'
                """))
            {
                renamed = await cmd.ExecuteNonQueryAsync();
            }

            int deleted;
            await using (var cmd = ctx.DataSource.CreateCommand($"""
                DELETE FROM "{schema}".mesh_nodes
                 WHERE namespace = ''
                   AND id = 'Organization'
                   AND node_type = 'NodeType'
                """))
            {
                deleted = await cmd.ExecuteNonQueryAsync();
            }

            if (renamed > 0 || deleted > 0)
                ctx.Logger.LogInformation(
                    "Repair v28: \"{Schema}\" — renamed {Renamed} Organization → Space row(s); dropped {Deleted} orphaned NodeType-registry row(s)",
                    schema, renamed, deleted);
        }

        // 3. Drop per-tenant Partition MeshNodes from admin (keep system partitions).
        await using (var cmd = ctx.DataSource.CreateCommand($"""
            DELETE FROM admin.mesh_nodes
             WHERE node_type = 'Partition'
               AND namespace = 'Admin/Partition'
               AND id <> ALL($1)
            """))
        {
            cmd.Parameters.AddWithValue(SystemPartitionIds);
            var partitionRowsDeleted = await cmd.ExecuteNonQueryAsync();
            ctx.Logger.LogInformation(
                "Repair v28: dropped {Count} per-tenant Partition row(s) from admin.mesh_nodes (system partitions retained)",
                partitionRowsDeleted);
        }

        // 4. Extend V27 mirror function to include 'Space'. Function name unchanged →
        //    the per-partition trigger picks up the new body without re-creating
        //    the trigger. If V27 hasn't installed the function yet (running on a
        //    pre-V27 DB), this CREATE OR REPLACE installs the function fresh; V27's
        //    own RunAsync will then re-create it identically (idempotent).
        await using (var cmd = ctx.DataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION public.mirror_access_object_to_auth_schema()
            RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    IF OLD.node_type IN ('User','Group','Role','VUser','ApiToken','Space') THEN
                        DELETE FROM "auth".mesh_nodes
                         WHERE namespace = OLD.namespace AND id = OLD.id;
                    END IF;
                    RETURN OLD;
                END IF;

                IF NEW.node_type IN ('User','Group','Role','VUser','ApiToken','Space') THEN
                    INSERT INTO "auth".mesh_nodes
                        (namespace, id, name, node_type, category, icon, display_order,
                         last_modified, version, state, content, desired_id, main_node)
                    VALUES (NEW.namespace, NEW.id, NEW.name, NEW.node_type, NEW.category, NEW.icon, NEW.display_order,
                            NEW.last_modified, NEW.version, NEW.state, NEW.content, NEW.desired_id, NEW.main_node)
                    ON CONFLICT (namespace, id) DO UPDATE SET
                        name = EXCLUDED.name,
                        node_type = EXCLUDED.node_type,
                        category = EXCLUDED.category,
                        icon = EXCLUDED.icon,
                        display_order = EXCLUDED.display_order,
                        last_modified = EXCLUDED.last_modified,
                        version = EXCLUDED.version,
                        state = EXCLUDED.state,
                        content = EXCLUDED.content,
                        desired_id = EXCLUDED.desired_id,
                        main_node = EXCLUDED.main_node;
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            """))
        {
            await cmd.ExecuteNonQueryAsync();
            ctx.Logger.LogInformation("Repair v28: extended mirror_access_object_to_auth_schema to include 'Space'");
        }

        // 5. Backfill Space rows that existed before V28 ran (the trigger only covers
        //    writes from now on; existing rows need a one-shot copy). The auth schema
        //    mirrors INTO itself by construction — skip the auth schema in the loop.
        foreach (var schema in schemas)
        {
            if (string.Equals(schema, "auth", StringComparison.OrdinalIgnoreCase))
                continue;

            await using var cmd = ctx.DataSource.CreateCommand($"""
                INSERT INTO "auth".mesh_nodes
                    (namespace, id, name, node_type, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node)
                SELECT namespace, id, name, node_type, category, icon, display_order,
                       last_modified, version, state, content, desired_id, main_node
                  FROM "{schema}".mesh_nodes
                 WHERE node_type = 'Space'
                ON CONFLICT (namespace, id) DO NOTHING
                """);
            var backfilled = await cmd.ExecuteNonQueryAsync();
            if (backfilled > 0)
                ctx.Logger.LogInformation(
                    "Repair v28: {Schema} backfilled {Count} Space row(s) into auth.mesh_nodes",
                    schema, backfilled);
        }
    }
}
