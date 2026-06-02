using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Repairs the auth-lookup mirror on databases that were provisioned FRESH and thus
/// fast-forwarded past the V25 / V27 repairs that originally installed the access-object
/// mirror trigger.
///
/// <para><b>The bug.</b> The per-partition DDL (<c>PostgreSqlSchemaInitializer.GetVersionedPartitionDdl</c>)
/// installs the <c>mesh_node_mirror_access_objects</c> trigger ONLY IF the function
/// <c>public.mirror_access_object_to_auth_schema()</c> already exists. That function was
/// created only by the V27 *repair* migration — and <c>MigrationRunner</c> SKIPS all repairs
/// on a fresh DB. So fresh deployments ended up at <c>db_version=31</c> with no mirror
/// function, no triggers on any partition, and an empty <c>auth</c> schema — every auth
/// lookup silently fell back to a cross-partition fan-out. (Confirmed on
/// <c>memex.systemorph.com</c> 2026-06-02: <c>FUNC=0</c>, triggers <c>NONE</c>, <c>auth=0</c>.)
///
/// <para><b>The repair (idempotent).</b>
/// <list type="number">
///   <item>Create the (fail-safe) mirror function — single-sourced from
///     <see cref="PostgreSqlSchemaInitializer.GetAuthMirrorFunctionScript"/>. (Schema-init now
///     creates it too, so fresh DBs install triggers via <c>ensure_partition_schema</c>; this
///     migration covers partitions that were provisioned BEFORE the function existed.)</item>
///   <item>Ensure the <c>auth</c> mirror partition exists.</item>
///   <item>Install the trigger on every existing partition schema (skipping <c>auth</c> itself).</item>
///   <item>Backfill existing User / Group / Role / VUser / ApiToken rows into <c>auth</c>
///     (<c>ON CONFLICT DO NOTHING</c> — the trigger owns live writes).</item>
/// </list>
/// Skipped on fresh DBs (no legacy partitions to retrofit / nothing to backfill); the
/// always-run schema-init function + <c>ensure_partition_schema</c> trigger install cover those.</para>
/// </summary>
public sealed class V32_RepairAuthMirrorTriggerAndBackfill : IMigration
{
    public int Version => 32;

    public string Description =>
        "Install auth-mirror function+trigger on existing partitions and backfill auth (fresh-DB repair)";

    public async Task RunAsync(MigrationContext ctx)
    {
        // 1. (Re)create the mirror function — fail-safe, single-sourced with schema-init.
        await using (var cmd = ctx.DataSource.CreateCommand(
            PostgreSqlSchemaInitializer.GetAuthMirrorFunctionScript()))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Ensure the auth mirror partition exists (idempotent — no-op when present).
        await using (var cmd = ctx.DataSource.CreateCommand(
            "SELECT public.ensure_partition_schema('auth')"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // 3 + 4. Install the trigger on every partition and backfill its access objects.
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);
        foreach (var schema in schemas)
        {
            // The 'auth' schema is the mirror target — it doesn't mirror into itself.
            if (string.Equals(schema, "auth", StringComparison.OrdinalIgnoreCase))
                continue;

            await using (var cmd = ctx.DataSource.CreateCommand($"""
                DROP TRIGGER IF EXISTS mesh_node_mirror_access_objects ON "{schema}".mesh_nodes;
                CREATE TRIGGER mesh_node_mirror_access_objects
                    AFTER INSERT OR UPDATE OR DELETE ON "{schema}".mesh_nodes
                    FOR EACH ROW EXECUTE FUNCTION public.mirror_access_object_to_auth_schema();
                """))
            {
                await cmd.ExecuteNonQueryAsync();
            }

            await using (var cmd = ctx.DataSource.CreateCommand($"""
                INSERT INTO "auth".mesh_nodes
                    (namespace, id, name, node_type, category, icon, display_order,
                     last_modified, version, state, content, desired_id, main_node)
                SELECT namespace, id, name, node_type, category, icon, display_order,
                       last_modified, version, state, content, desired_id, main_node
                  FROM "{schema}".mesh_nodes
                 WHERE node_type IN ('User','Group','Role','VUser','ApiToken')
                ON CONFLICT (namespace, id) DO NOTHING;
                """))
            {
                var n = await cmd.ExecuteNonQueryAsync();
                if (n > 0)
                    ctx.Logger.LogInformation(
                        "Repair v32: backfilled {Count} access-object row(s) from {Schema} into auth", n, schema);
            }
        }
    }
}
