using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Delete legacy <c>_Release</c> MeshNodes across every partition.
///
/// <para>NodeType-compile Release MeshNodes used to live at
/// <c>{nodeTypePath}/_Release/{version}</c>. The leading underscore made them look like
/// a satellite-routed entity (alongside <c>_Access</c>, <c>_Thread</c>, etc.), but
/// <c>PartitionDefinition.StandardTableMappings</c> never routed <c>_Release</c>, so
/// they lived in <c>mesh_nodes</c> all along. The path-segment underscore was cosmetic
/// dead-weight that confused readers, so the namespace was renamed to <c>Release</c>
/// (no underscore) alongside the cross-silo assembly-reference refactor.</para>
///
/// <para>Strategy: <b>delete in place rather than rename</b>. Release nodes are
/// regenerated on every successful compile (<c>MeshDataSource.TryCreateReleaseNode</c>),
/// and the next portal cold-start will trigger a recompile on every NodeType whose
/// <c>HasUsableBuildMetadata</c> predicate returns false (i.e. any whose
/// <c>NodeTypeDefinition.LatestAssemblyCollection</c>/<c>LatestAssemblyPath</c> are
/// not yet populated — every NodeType, post-deploy). The deleted history is
/// observability + UI listings; no live activation depends on it.</para>
///
/// <para>Only deletes from <c>mesh_nodes</c> in each schema; the rest of the
/// partition's tables (<c>code</c>, <c>access</c>, etc.) don't carry release rows.
/// Versions sidecars (<c>mesh_node_history</c> in <c>*_versions</c> schemas) are
/// scrubbed too so the rewrite history stays consistent.</para>
/// </summary>
public sealed class V19_DeleteLegacyReleaseNodes : IMigration
{
    public int Version => 19;
    public string Description => "Delete legacy _Release/* MeshNodes — regenerated on next successful compile";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Discover every schema (content partitions + their _versions mirrors) that has
        // a mesh_nodes table.
        var schemas = new List<string>();
        await using (var listCmd = ctx.DataSource.CreateCommand("""
            SELECT DISTINCT t.table_schema
            FROM information_schema.tables t
            WHERE t.table_name IN ('mesh_nodes', 'mesh_node_history')
              AND t.table_schema NOT IN ('information_schema', 'pg_catalog', 'pg_toast')
            ORDER BY t.table_schema
            """))
        {
            await using var rdr = await listCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) schemas.Add(rdr.GetString(0));
        }

        var totalDeleted = 0;
        foreach (var schema in schemas)
        {
            // Find the actual table name(s) in this schema. mesh_nodes for content
            // partitions; mesh_node_history for _versions sidecars.
            var tables = new List<string>();
            await using (var tblCmd = ctx.DataSource.CreateCommand("""
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = $1
                  AND table_name IN ('mesh_nodes', 'mesh_node_history')
                ORDER BY table_name
                """))
            {
                tblCmd.Parameters.AddWithValue(schema);
                await using var rdr = await tblCmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync()) tables.Add(rdr.GetString(0));
            }

            foreach (var table in tables)
            {
                // Match _Release as a whole path segment anywhere in `namespace`
                // (so we catch e.g. `acme/Underwriting/_Release/v20260101...`) and
                // also bare `_Release` as the namespace (defensive — should never
                // happen in practice).
                await using var delCmd = ctx.DataSource.CreateCommand($"""
                    DELETE FROM "{schema}"."{table}"
                    WHERE namespace ~ '(^|/)_Release($|/)'
                       OR namespace = '_Release'
                    """);
                var affected = await delCmd.ExecuteNonQueryAsync();
                if (affected > 0)
                {
                    ctx.Logger.LogInformation(
                        "Repair v19: {Schema}.{Table} — deleted {Count} legacy _Release node(s)",
                        schema, table, affected);
                    totalDeleted += affected;
                }
            }
        }

        ctx.Logger.LogInformation(
            "Repair v19: deleted {Total} legacy _Release node(s) across all schemas — they will be regenerated on next compile",
            totalDeleted);
    }
}
