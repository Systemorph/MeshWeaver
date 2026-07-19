using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Re-applies <c>rebuild_user_effective_permissions()</c> on every schema that has it so the
/// function takes the global transaction-scoped advisory lock
/// (<c>pg_advisory_xact_lock(hashtext('meshweaver_uep_rebuild'))</c>) as its FIRST statement.
///
/// <para><b>The bug (memex-cloud 2026-07-19, twice).</b> Concurrent <c>_Access</c> writes from
/// DIFFERENT partitions deadlocked with <c>40P01 deadlock detected</c>: two plugin hubs warming
/// concurrently each ran their access-seeding pass; each grant write fired that schema's
/// <c>access_changed</c> trigger, which runs the FULL <c>rebuild_user_effective_permissions()</c>
/// inside the originating write's transaction. Each rebuild takes ACCESS EXCLUSIVE on its OWN
/// schema's <c>user_effective_permissions</c> (the atomic-swap <c>ALTER TABLE … RENAME</c>) AND
/// writes the SHARED <c>public.partition_access</c> rows — two transactions touching different
/// schema <c>access</c> tables but the same shared rows interleave those locks in opposite orders
/// → lock cycle → PG kills one → the whole seeding pass of that hub aborts
/// (<c>MeshNode Unknown at 'AgenticEngineering/Start/_Access/Public_Access': 40P01</c>).</para>
///
/// <para><b>The fix.</b> <c>PostgreSqlSchemaInitializer.GetUepRebuildFunctionScript</c> (the
/// single-sourced body all installers now embed) opens with the advisory lock, so rebuild
/// transactions QUEUE instead of interleaving into a cycle; the xact-scoped lock releases
/// automatically at commit/rollback. No retries, no app-side locking.</para>
///
/// <para><b>Why a migration?</b> The schema script only runs for the boot schema
/// (<c>InitializeAsync</c> → <c>GetSchemaScript</c>) and for partitions that go through
/// <c>public.ensure_partition_schema</c> again (new provisioning) — EXISTING partition schemas
/// keep their deployed function body until something re-CREATEs it. This migration is that
/// something: one <c>CREATE OR REPLACE</c> per schema that already has the function (probed via
/// <c>pg_proc</c> — unversioned schemas never had it and are skipped). Fresh DBs skip this repair
/// and get the locked body straight from the initializer templates. Idempotent; the function's
/// projection semantics are unchanged, so no data backfill / re-rebuild is needed.</para>
/// </summary>
public sealed class V47_SerializeUepRebuildAdvisoryLock : IMigration
{
    public int Version => 47;
    public string Description =>
        "Serialize rebuild_user_effective_permissions() with a global advisory xact lock (40P01 cross-partition deadlock)";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Every schema that carries the function — public + versioned partitions. Probing
        // pg_proc (not table existence) keeps this surgical: schemas without the function
        // (unversioned admin/portal/kernel shapes) are untouched.
        var schemas = new List<string>();
        await using (var discover = ctx.DataSource.CreateCommand("""
            SELECT n.nspname
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.proname = 'rebuild_user_effective_permissions'
            ORDER BY n.nspname
            """))
        await using (var rdr = await discover.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        foreach (var schema in schemas)
        {
            // Per-schema data source (search_path = "{schema},public") so the unqualified
            // CREATE OR REPLACE FUNCTION lands in that schema — same OID, so the existing
            // access_changed trigger picks the locked body up immediately.
            await using var schemaDs = SchemaHelpers.BuildSchemaDataSource(
                ctx.ConnectionString, schema, useVector: false);
            var schemaRef = "'" + schema.Replace("'", "''") + "'";
            try
            {
                await using var cmd = schemaDs.CreateCommand(
                    PostgreSqlSchemaInitializer.GetUepRebuildFunctionScript(schemaRef));
                await cmd.ExecuteNonQueryAsync();
                ctx.Logger.LogInformation(
                    "Repair v47: \"{Schema}\".rebuild_user_effective_permissions now serializes via the global advisory lock",
                    schema);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex,
                    "Repair v47: \"{Schema}\" — CREATE OR REPLACE rebuild_user_effective_permissions failed", schema);
            }
        }

        ctx.Logger.LogInformation("Repair v47: done — {Count} schema(s) updated", schemas.Count);
    }
}
