using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Renames the global <c>user</c> schema to <c>auth</c> and extends the
/// access-object mirror trigger to cover <c>ApiToken</c> nodes as well as
/// the existing User / Group / Role / VUser set.
///
/// <para><b>Why</b>: auth lookups (token validation, GetTokensForUser,
/// user-by-email) currently either fan out across every per-user partition
/// or hit a partial mirror (the <c>user</c> schema covers identities but
/// not tokens). Centralising every auth-related node in a single schema
/// makes each lookup a constant-cost single-schema query and removes the
/// last cross-partition fan-out from the security hot path. The schema
/// name <c>auth</c> reflects this broader scope (identities + credentials).</para>
///
/// <para><b>How</b>:
/// <list type="number">
///   <item><c>ALTER SCHEMA "user" RENAME TO auth</c> — atomic, all FK /
///   trigger references update in-place.</item>
///   <item><c>CREATE OR REPLACE FUNCTION public.mirror_access_object_to_auth_schema</c>
///   — same body as the previous <c>..._user_schema</c> function but writes to
///   <c>"auth".mesh_nodes</c> and includes <c>'ApiToken'</c> in the
///   <c>node_type</c> filter.</item>
///   <item>For each partition: drop the old trigger (pointing at the
///   <c>_user_schema</c> function) and recreate it pointing at the new
///   <c>_auth_schema</c> function. Idempotent
///   (<c>DROP TRIGGER IF EXISTS</c>).</item>
///   <item>Backfill ApiToken rows from every partition into <c>auth</c>
///   (existing User / Group / Role / VUser rows are already there from
///   V25 — the rename keeps them).</item>
///   <item>Drop the now-unreferenced <c>mirror_access_object_to_user_schema</c>
///   function.</item>
/// </list></para>
///
/// <para><b>Idempotent</b>: rename is no-op when <c>auth</c> already exists;
/// function + trigger creations use <c>CREATE OR REPLACE</c> /
/// <c>DROP IF EXISTS</c>; backfill uses <c>ON CONFLICT DO NOTHING</c>.
/// Safe to re-run.</para>
/// </summary>
public sealed class V27_RenameUserSchemaToAuthAndMirrorApiTokens : IMigration
{
    public int Version => 27;

    public string Description =>
        "Rename user schema to auth; add ApiToken to the access-object mirror trigger";

    private static readonly string[] AccessObjectNodeTypes =
        { "User", "Group", "Role", "VUser", "ApiToken" };

    public async Task RunAsync(MigrationContext ctx)
    {
        var hasUser = await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "user");
        var hasAuth = await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "auth");

        if (hasUser && hasAuth)
        {
            // Both exist — earlier partial run + fresh-DB init layered. Merge
            // the user rows into auth (ON CONFLICT DO NOTHING — auth wins for
            // any contended key, since auth is where new writes have been
            // landing), then drop user.
            ctx.Logger.LogInformation(
                "Repair v27: both 'user' and 'auth' schemas exist — merging user → auth and dropping user");
            await using (var cmd = ctx.DataSource.CreateCommand("""
                INSERT INTO "auth".mesh_nodes
                    (namespace, id, name, node_type, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node)
                SELECT namespace, id, name, node_type, category, icon, display_order,
                       last_modified, version, state, content, desired_id, main_node
                  FROM "user".mesh_nodes
                ON CONFLICT (namespace, id) DO NOTHING;
                """))
            {
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = ctx.DataSource.CreateCommand("""DROP SCHEMA "user" CASCADE;"""))
            {
                await cmd.ExecuteNonQueryAsync();
            }
        }
        else if (hasUser && !hasAuth)
        {
            ctx.Logger.LogInformation("Repair v27: renaming 'user' schema to 'auth'");
            await using var cmd = ctx.DataSource.CreateCommand("""ALTER SCHEMA "user" RENAME TO "auth";""");
            await cmd.ExecuteNonQueryAsync();
        }
        else if (!hasUser && !hasAuth)
        {
            // Fresh DB ordering quirk — schema init hasn't created either yet.
            // The migration runner reruns next time. Bail out softly.
            ctx.Logger.LogInformation(
                "Repair v27: neither 'user' nor 'auth' schema exists yet — skipping (will re-run next start)");
            return;
        }
        // else: hasAuth only → assume an earlier run finished; just refresh the
        // function + triggers + backfill below to make idempotent.

        // Trigger function: writes into auth schema, covers User/Group/Role/VUser/ApiToken.
        await using (var cmd = ctx.DataSource.CreateCommand("""
            CREATE OR REPLACE FUNCTION public.mirror_access_object_to_auth_schema()
            RETURNS TRIGGER AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    IF OLD.node_type IN ('User','Group','Role','VUser','ApiToken') THEN
                        DELETE FROM "auth".mesh_nodes
                         WHERE namespace = OLD.namespace AND id = OLD.id;
                    END IF;
                    RETURN OLD;
                END IF;

                IF NEW.node_type IN ('User','Group','Role','VUser','ApiToken') THEN
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
        }

        // Rewire every partition's mirror trigger to point at the new function.
        // The trigger NAME stays 'mesh_node_mirror_access_objects' so future
        // re-runs of V25 / schema init are no-ops (idempotent).
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);
        foreach (var schema in schemas)
        {
            // The 'auth' schema mirrors INTO itself by construction — no trigger needed there.
            if (string.Equals(schema, "auth", StringComparison.OrdinalIgnoreCase))
                continue;

            ctx.Logger.LogInformation("Repair v27: rewiring mirror trigger on {Schema} → auth", schema);

            await using (var cmd = ctx.DataSource.CreateCommand($"""
                DROP TRIGGER IF EXISTS mesh_node_mirror_access_objects ON "{schema}".mesh_nodes;
                CREATE TRIGGER mesh_node_mirror_access_objects
                    AFTER INSERT OR UPDATE OR DELETE ON "{schema}".mesh_nodes
                    FOR EACH ROW EXECUTE FUNCTION public.mirror_access_object_to_auth_schema();
                """))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            // Backfill ApiToken rows that weren't covered by V25.
            // ON CONFLICT DO NOTHING — the trigger already mirrors live writes;
            // backfill is just for rows that existed pre-V27.
            await using (var cmd = ctx.DataSource.CreateCommand($"""
                INSERT INTO "auth".mesh_nodes
                    (namespace, id, name, node_type, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node)
                SELECT namespace, id, name, node_type, category, icon, display_order,
                       last_modified, version, state, content, desired_id, main_node
                  FROM "{schema}".mesh_nodes
                 WHERE node_type = 'ApiToken'
                ON CONFLICT (namespace, id) DO NOTHING;
                """))
            {
                var n = await cmd.ExecuteNonQueryAsync();
                ctx.Logger.LogInformation(
                    "Repair v27: {Schema} backfilled {Count} ApiToken row(s)", schema, n);
            }
        }

        // Drop the old function — nothing references it after the trigger
        // rewire above. Tolerate absence (already dropped on rerun).
        await using (var cmd = ctx.DataSource.CreateCommand(
            "DROP FUNCTION IF EXISTS public.mirror_access_object_to_user_schema();"))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
