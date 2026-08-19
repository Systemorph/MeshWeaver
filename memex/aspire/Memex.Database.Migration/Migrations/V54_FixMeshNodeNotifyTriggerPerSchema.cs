using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Installs the <c>mesh_node_notify</c> trigger on <b>every</b> partition schema's
/// <c>mesh_nodes</c>, so a write to a partition actually emits <c>pg_notify</c>.
///
/// <para><b>Background — the same defect V44 repaired for this trigger's sibling.</b> The schema
/// scripts created the trigger under a GLOBALLY-scoped guard:</para>
/// <code>IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'mesh_node_notify')</code>
/// <para>Trigger names are unique <b>per table</b>, not per database, so the first schema to be
/// provisioned satisfied the predicate for the whole database and <b>every later partition schema
/// silently skipped its own trigger</b>. <see cref="V44_FixMeshNodeHistoryTriggerPerSchema"/>
/// documents the identical bug, in the identical form, for
/// <c>mesh_node_copy_to_history</c> — and one of the three affected sites here is
/// <c>GetVersionedPartitionDdl</c>, the very method V44 fixed. The sibling was repaired; this one
/// was left. Every other trigger in <c>PostgreSqlSchemaInitializer</c> (~20 of them) already uses
/// the correct per-table <c>DROP TRIGGER IF EXISTS … ON &lt;table&gt;; CREATE TRIGGER …</c> form;
/// <c>mesh_node_notify</c> was the last one on the broken guard.</para>
///
/// <para><b>MEASURED on a live 33-partition database before this repair</b> (rather than argued
/// from the code):</para>
/// <list type="bullet">
///   <item><c>mesh_node_notify</c> on <c>mesh_nodes</c>: present in <b>1</b> schema, missing in
///     <b>32</b>. The one schema that had it is <c>public</c> — which is <b>empty by design</b>
///     (see AGENTS.md → "One Schema Per Partition"), so in practice <b>no node write in that
///     database had ever emitted <c>pg_notify</c></b>.</item>
///   <item>Satellite tables: <b>231</b> correctly carrying their <c>*_notify</c> trigger.</item>
///   <item><c>mesh_node_copy_to_history</c>: <b>33 of 33</b> — V44's repair already covered every
///     existing schema, which is why this migration does not touch it. Its source-side guard in
///     <c>GetMeshSchemaScript</c> is fixed alongside so a NEW schema cannot regress.</item>
/// </list>
///
/// <para><b>Why it survived.</b> Satellite tables were unaffected — <c>GetSatelliteTableScript</c>
/// always used the correct DROP/CREATE form — so a partition's <c>_Access</c> / <c>_Thread</c> /
/// <c>_Activity</c> writes notified while writes to that same partition's <c>mesh_nodes</c> did
/// not. And the in-process <c>IStorageAdapter.Changes</c> feed still carries same-process writes,
/// so only a notification that had to cross a process boundary was lost — silently.</para>
///
/// <para><b>Idempotent.</b> DROP-then-CREATE per schema; safe to re-run, and it doubles as the
/// re-apply path if the function body changes. Fresh partitions get the fixed form from the updated
/// schema scripts and the <c>ensure_partition_schema</c> proc.</para>
///
/// <para><b>Scope.</b> Every schema owning a <c>mesh_nodes</c> table, excluding the catalogs and the
/// cross-schema <c>{schema}_versions</c> history schemas (which have no <c>mesh_nodes</c> of their
/// own). The trigger function <c>notify_mesh_node_changes()</c> is resolved unqualified, exactly as
/// the schema scripts create it — V24 already replaced that function in every partition schema.</para>
/// </summary>
public sealed class V54_FixMeshNodeNotifyTriggerPerSchema : IMigration
{
    public int Version => 54;

    public string Description =>
        "Install mesh_node_notify on every partition schema's mesh_nodes (the global trigger-name "
        + "guard installed it only on the first schema, so partition writes emitted no pg_notify)";

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = new List<string>();
        await using (var discover = ctx.DataSource.CreateCommand("""
            SELECT table_schema
            FROM information_schema.tables
            WHERE table_name = 'mesh_nodes'
              AND table_schema NOT IN ('information_schema','pg_catalog','pg_toast')
              AND table_schema NOT LIKE '%\_versions'
            ORDER BY table_schema
            """))
        {
            await using var rdr = await discover.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        var installed = 0;
        var alreadyPresent = 0;
        foreach (var schema in schemas)
        {
            var quotedSchema = "\"" + schema.Replace("\"", "\"\"") + "\"";
            var literalSchema = schema.Replace("'", "''");

            // Report what was actually missing rather than just "done": this migration's whole
            // point is that a silent skip went unnoticed for a long time, so the repair says how
            // many schemas were genuinely lacking the trigger. A run reporting 0 missing on a
            // multi-partition database would itself be evidence the diagnosis was wrong.
            bool present;
            await using (var probe = ctx.DataSource.CreateCommand($"""
                SELECT EXISTS (
                    SELECT 1 FROM pg_trigger tg
                    JOIN pg_class c ON c.oid = tg.tgrelid
                    JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE tg.tgname = 'mesh_node_notify'
                      AND c.relname = 'mesh_nodes'
                      AND n.nspname = '{literalSchema}')
                """))
            {
                present = (bool)(await probe.ExecuteScalarAsync())!;
            }

            if (present)
            {
                // 🚨 Deliberately do NOT drop-and-recreate a trigger that is already correct.
                // CREATE/DROP TRIGGER takes an ACCESS EXCLUSIVE lock on the table, and this loop
                // runs over EVERY partition schema on the server; re-applying an identical
                // definition would buy nothing and take a (brief) exclusive lock per partition.
                // The trigger DEFINITION is stable — what changes over time is the function body,
                // and V24 already re-applies that separately via CREATE OR REPLACE FUNCTION.
                alreadyPresent++;
            }
            else
            {
                await using (var cmd = ctx.DataSource.CreateCommand($"""
                    CREATE TRIGGER mesh_node_notify
                        AFTER INSERT OR UPDATE OR DELETE ON {quotedSchema}.mesh_nodes
                        FOR EACH ROW EXECUTE FUNCTION notify_mesh_node_changes();
                    """))
                {
                    await cmd.ExecuteNonQueryAsync();
                }

                installed++;
                ctx.Logger.LogInformation(
                    "Repair v54: '{Schema}' — mesh_nodes had NO mesh_node_notify trigger (the global "
                    + "guard skipped it); installed. Writes to this partition now emit pg_notify.",
                    schema);
            }
        }

        ctx.Logger.LogInformation(
            "Repair v54: done — {Installed} schema(s) were missing mesh_node_notify and now have it; "
            + "{Present} already had it ({Total} schema(s) with mesh_nodes inspected).",
            installed, alreadyPresent, schemas.Count);
    }
}
