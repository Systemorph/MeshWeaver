using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Repairs the <b>lost <c>'Space'</c> extension of the auth mirror</b> and backfills the
/// Space rows that went missing while it was broken.
///
/// <para><b>Background.</b> V28 extended <c>public.mirror_access_object_to_auth_schema()</c>
/// to mirror <c>node_type='Space'</c> rows into <c>auth.mesh_nodes</c> — the single-schema
/// lookup behind the Spaces catalog. But the always-run schema initializer
/// (<c>PostgreSqlSchemaInitializer.GetAuthMirrorFunctionScript</c>) re-creates that function
/// on every startup from its own constant, which did NOT include <c>'Space'</c> — so the very
/// next restart silently reverted V28. Every Space created after that revert (RolePlay, X,
/// Chess, … 2026-07) has a fully working partition, grants and content but no row in
/// <c>auth.mesh_nodes</c> — invisible in the Spaces catalog. The initializer's constant is
/// fixed in the same change set; this migration repairs already-deployed databases.</para>
///
/// <para>Two steps, both idempotent:</para>
/// <list type="number">
///   <item><b>Re-create the function WITH <c>'Space'</c></b> (both the upsert and the delete
///     branch). Frozen inline SQL — a migration must not depend on live code that keeps
///     evolving; this body matches the initializer's constant as of V42.</item>
///   <item><b>Backfill</b>: copy every <c>node_type='Space'</c> row from every content-partition
///     schema into <c>auth.mesh_nodes</c> (<c>ON CONFLICT … DO UPDATE</c>), mirroring V28's
///     backfill — needed because the trigger only covers writes from now on.</item>
/// </list>
///
/// <para><b>Belt and braces.</b> The same change set makes the mirror SELF-HEALING: the always-run
/// schema init (<c>PostgreSqlSchemaInitializer.GetAuthMirrorSelfHealScript</c>, step 5 of
/// <c>InitializeAsync</c>) re-installs missing partition triggers and reconciles missed/stale
/// mirrored rows on every boot. This migration remains as the recorded, ordered repair for
/// deployed databases (and heals DBs whose next boot predates the new initializer).</para>
/// </summary>
public sealed class V42_ReapplySpaceAuthMirrorAndBackfill : IMigration
{
    public int Version => 42;
    public string Description => "Re-apply 'Space' to the auth mirror function (lost to the schema-init constant) and backfill missing Space rows into auth.mesh_nodes";

    public async Task RunAsync(MigrationContext ctx)
    {
        // 1. Re-create the mirror function WITH 'Space' in both branches. CREATE OR REPLACE →
        //    safe against any prior state; the fail-safe guard keeps partitions writable even
        //    when auth isn't provisioned.
        await using (var fn = ctx.DataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION public.mirror_access_object_to_auth_schema()
            RETURNS TRIGGER AS $auth_mirror$
            BEGIN
                IF to_regclass('"auth".mesh_nodes') IS NULL THEN
                    RETURN COALESCE(NEW, OLD);
                END IF;

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
            $auth_mirror$ LANGUAGE plpgsql;
            """))
        {
            await fn.ExecuteNonQueryAsync();
            ctx.Logger.LogInformation("Repair v42: re-created mirror_access_object_to_auth_schema WITH 'Space'");
        }

        // 2. Backfill Space rows written while the function was Space-less. Same schema
        //    discovery as V28/V34: every schema owning a mesh_nodes table except the system
        //    schemas; auth mirrors INTO itself by construction, so it is excluded.
        var schemas = new List<string>();
        await using (var discover = ctx.DataSource.CreateCommand("""
            SELECT t.table_schema
            FROM information_schema.tables t
            WHERE t.table_name = 'mesh_nodes'
              AND t.table_schema NOT IN
                  ('information_schema','pg_catalog','pg_toast','public','admin','auth','doc')
              AND t.table_schema NOT LIKE '%\_versions'
            ORDER BY t.table_schema
            """))
        await using (var rdr = await discover.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        var backfilled = 0;
        foreach (var schema in schemas)
        {
            var quotedSchema = schema.Replace("\"", "\"\"");
            await using var copy = ctx.DataSource.CreateCommand($"""
                INSERT INTO "auth".mesh_nodes
                    (namespace, id, name, node_type, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node)
                SELECT namespace, id, name, node_type, category, icon, display_order,
                       last_modified, version, state, content, desired_id, main_node
                  FROM "{quotedSchema}".mesh_nodes
                 WHERE node_type = 'Space'
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
                    main_node = EXCLUDED.main_node
                """);
            var rows = await copy.ExecuteNonQueryAsync();
            if (rows > 0)
            {
                ctx.Logger.LogInformation(
                    "Repair v42: '{Schema}' backfilled {Rows} Space row(s) into auth.mesh_nodes", schema, rows);
                backfilled += rows;
            }
        }

        ctx.Logger.LogInformation("Repair v42: done — {Count} Space row(s) backfilled", backfilled);
    }
}
