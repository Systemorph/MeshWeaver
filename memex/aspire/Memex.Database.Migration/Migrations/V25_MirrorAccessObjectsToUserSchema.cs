using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Maintain a global index of every "access object" (User, Group, Role, VUser
/// nodes -- anything that can appear as <c>AccessObject</c> on an
/// AccessAssignment) inside the dedicated <c>user</c> schema.
///
/// <para>Problem this solves: per-user partitions hold their own User node
/// (at the root of each user's schema). Resolving <c>email -> user.Id</c>
/// previously fanned a <c>nodeType:User scope:subtree</c> synced query
/// across every partition's <c>mesh_nodes</c>. Under load and during the
/// 2026-05-20 thread-load incident this fan-out was one of the costs paying
/// the per-query searchable_schemas sync hit. A single-schema lookup is
/// constant cost and immune to per-partition pool starvation.</para>
///
/// <para>Approach: install an AFTER INSERT/UPDATE/DELETE trigger on each
/// partition's <c>mesh_nodes</c> that mirrors rows of the relevant node
/// types into <c>user.mesh_nodes</c>. Then backfill the existing rows.
/// The mirror is keyed on <c>(namespace, id)</c> exactly like the source,
/// and uses ON CONFLICT DO UPDATE so updates flow through.</para>
///
/// <para><b>Idempotent</b>: <c>CREATE OR REPLACE FUNCTION</c> +
/// <c>DROP TRIGGER IF EXISTS</c>. Safe to re-run.</para>
/// </summary>
public sealed class V25_MirrorAccessObjectsToUserSchema : IMigration
{
    public int Version => 25;

    public string Description =>
        "Mirror User/Group/Role/VUser nodes into user.mesh_nodes via per-partition trigger";

    private static readonly string[] AccessObjectNodeTypes =
        { "User", "Group", "Role", "VUser" };

    public async Task RunAsync(MigrationContext ctx)
    {
        // The 'user' schema must exist and have mesh_nodes. Both come from
        // schema initialisation; bail out softly if either is missing
        // (fresh-DB ordering quirk -- the migration runner reruns next time).
        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user"))
        {
            ctx.Logger.LogInformation("Repair v25: 'user' schema missing -- skipping");
            return;
        }

        // The mirror trigger function lives in the public schema so every
        // partition trigger can resolve it without a search_path dance. It
        // writes into "user".mesh_nodes via fully-qualified name.
        await using (var cmd = ctx.DataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION public.mirror_access_object_to_user_schema()
            RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    IF OLD.node_type IN ('User','Group','Role','VUser') THEN
                        DELETE FROM "user".mesh_nodes
                         WHERE namespace = OLD.namespace AND id = OLD.id;
                    END IF;
                    RETURN OLD;
                END IF;

                IF NEW.node_type IN ('User','Group','Role','VUser') THEN
                    INSERT INTO "user".mesh_nodes
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
        }

        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);

        foreach (var schema in schemas)
        {
            // The 'user' schema mirrors INTO itself by construction -- no trigger needed there.
            if (string.Equals(schema, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            ctx.Logger.LogInformation("Repair v25: installing mirror trigger on {Schema}", schema);

            await using (var cmd = ctx.DataSource.CreateCommand($"""
                DROP TRIGGER IF EXISTS mesh_node_mirror_access_objects ON "{schema}".mesh_nodes;
                CREATE TRIGGER mesh_node_mirror_access_objects
                    AFTER INSERT OR UPDATE OR DELETE ON "{schema}".mesh_nodes
                    FOR EACH ROW EXECUTE FUNCTION public.mirror_access_object_to_user_schema();
                """))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Backfill: copy every existing access-object row that isn't already in user.mesh_nodes.
            // We rely on the ON CONFLICT DO NOTHING here because the source partition is the
            // source of truth for content -- we don't want a backfill pass to clobber a freshly
            // updated row in the user index that the trigger already mirrored.
            await using (var cmd = ctx.DataSource.CreateCommand($"""
                INSERT INTO "user".mesh_nodes
                    (namespace, id, name, node_type, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node)
                SELECT namespace, id, name, node_type, category, icon, display_order,
                       last_modified, version, state, content, desired_id, main_node
                  FROM "{schema}".mesh_nodes
                 WHERE node_type IN ('User','Group','Role','VUser')
                ON CONFLICT (namespace, id) DO NOTHING;
                """))
            {
                var n = await cmd.ExecuteNonQueryAsync();
                ctx.Logger.LogInformation(
                    "Repair v25: {Schema} backfilled {Count} access-object row(s)", schema, n);
            }
        }
    }
}
