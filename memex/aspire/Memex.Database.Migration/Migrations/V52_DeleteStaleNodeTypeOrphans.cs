using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Delete stale NodeType definition orphans left behind by two renames.
///
/// <para><b>Background.</b> The course rename <c>KmuBasics</c> → <c>AgenticOffice</c> and the
/// plugin-module rename <c>Instances</c> → <c>Hosting</c> each migrated the <c>Source</c> code
/// nodes to the new home but left the OLD NodeType definition nodes behind. Those orphans have
/// no source to compile, so they park on a compile failure at every pod start — contained but
/// noisy, and each failed compile writes fresh <c>_Activity/compile-state</c> satellites.
/// Normal MCP deletion fails on the system-stamped satellites (e.g. "lacks Delete permission on
/// 'Instances/Deployment/_Activity/compile-state'"), so the sanctioned path is this Repair
/// migration running under the migration's database identity.</para>
///
/// <para><b>What is deleted</b> — the definition node plus its ENTIRE subtree (Source code
/// rows, <c>_Activity</c> compile-state, any other satellites) across every node table of the
/// owning partition schema, including the <c>mesh_node_history</c> snapshots:</para>
/// <list type="bullet">
///   <item><c>KmuBasics/Offerte</c>, <c>KmuBasics/Rechnung</c>, <c>KmuBasics/Buchungsjournal</c>,
///     <c>KmuBasics/Firmenprofil</c> — present on the memex-cloud database.</item>
///   <item><c>Instances/Deployment</c> — present on the systemorph / memex databases.</item>
/// </list>
///
/// <para><b>What is deliberately NOT deleted:</b> the <c>Instances</c> plugin root with its
/// <c>_Policy</c> / <c>_Access</c> shell (an operator uninstall decision, out of scope here) and
/// the <c>KmuBasics</c> partition root. Only the named subtrees go.</para>
///
/// <para><b>Per-portal scoping / idempotency:</b> each portal's database simply lacks the other
/// portal's schema, so the <c>SchemaExistsAsync</c> guard makes the whole target a no-op there
/// (same pattern as <see cref="V38_DropLegacyProviderSchema"/>); a path with no rows deletes
/// zero rows. Table discovery is column-shaped (every node table carries <c>namespace</c> +
/// <c>id</c> + the generated <c>path</c>), so satellite tables that don't exist on an older
/// schema layout are skipped rather than erroring.</para>
/// </summary>
public sealed class V52_DeleteStaleNodeTypeOrphans : IMigration
{
    public int Version => 52;

    public string Description =>
        "Delete stale NodeType definition orphans (KmuBasics/Offerte|Rechnung|Buchungsjournal|Firmenprofil, Instances/Deployment) incl. satellites and history";

    /// <summary>Schema → orphaned subtree roots to delete (node + everything under it).</summary>
    private static readonly (string Schema, string[] Roots)[] Targets =
    [
        ("kmubasics",
        [
            "KmuBasics/Offerte",
            "KmuBasics/Rechnung",
            "KmuBasics/Buchungsjournal",
            "KmuBasics/Firmenprofil",
        ]),
        ("instances",
        [
            "Instances/Deployment",
        ]),
    ];

    public async Task RunAsync(MigrationContext ctx)
    {
        if (ctx.IsFreshDb)
            return; // a fresh database never carried the pre-rename NodeType definitions

        var totalDeleted = 0;
        foreach (var (schema, roots) in Targets)
        {
            if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, schema))
            {
                ctx.Logger.LogInformation(
                    "Repair v52: schema \"{Schema}\" absent on this database — nothing to delete.", schema);
                continue;
            }

            // The legacy layout kept history in a "{schema}_versions" sidecar schema; the current
            // layout keeps mesh_node_history in the partition schema itself. Sweep both when present.
            var schemas = new List<string> { schema };
            if (await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, schema + "_versions"))
                schemas.Add(schema + "_versions");

            foreach (var sweepSchema in schemas)
                totalDeleted += await DeleteSubtreesAsync(ctx, sweepSchema, roots);
        }

        ctx.Logger.LogInformation(
            "Repair v52: deleted {Total} stale NodeType-orphan row(s) in total.", totalDeleted);
    }

    private static async Task<int> DeleteSubtreesAsync(MigrationContext ctx, string schema, string[] roots)
    {
        // Every node-row table (main + satellites + history) shares the (namespace, id) shape with
        // the generated `path` column — discover by columns so this works on any schema layout and
        // silently skips tables an older/newer layout doesn't have. Deliberately excludes
        // non-node tables (user_activity has namespace but no id; access_control has neither).
        var tables = new List<string>();
        await using (var tblCmd = ctx.DataSource.CreateCommand("""
            SELECT c1.table_name
            FROM information_schema.columns c1
            JOIN information_schema.columns c2
              ON c2.table_schema = c1.table_schema AND c2.table_name = c1.table_name AND c2.column_name = 'id'
            JOIN information_schema.columns c3
              ON c3.table_schema = c1.table_schema AND c3.table_name = c1.table_name AND c3.column_name = 'path'
            WHERE c1.table_schema = $1 AND c1.column_name = 'namespace'
            ORDER BY c1.table_name
            """))
        {
            tblCmd.Parameters.AddWithValue(schema);
            await using var rdr = await tblCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) tables.Add(rdr.GetString(0));
        }

        var quotedSchema = "\"" + schema.Replace("\"", "\"\"") + "\"";
        var deleted = 0;
        foreach (var table in tables)
        {
            var quotedTable = "\"" + table.Replace("\"", "\"\"") + "\"";
            foreach (var root in roots)
            {
                // The node itself plus its whole subtree (Source/…, _Activity/…, any satellite).
                // LOWER() matching tolerates casing drift; the roots contain no LIKE wildcards.
                await using var delCmd = ctx.DataSource.CreateCommand($"""
                    DELETE FROM {quotedSchema}.{quotedTable}
                    WHERE LOWER(path) = LOWER($1)
                       OR LOWER(path) LIKE LOWER($1) || '/%'
                    """);
                delCmd.Parameters.AddWithValue(root);
                var affected = await delCmd.ExecuteNonQueryAsync();
                if (affected > 0)
                {
                    ctx.Logger.LogInformation(
                        "Repair v52: {Schema}.{Table} — deleted {Count} row(s) under '{Root}'.",
                        schema, table, affected, root);
                    deleted += affected;
                }
            }
        }

        return deleted;
    }
}
