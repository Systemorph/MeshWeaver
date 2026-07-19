using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Adds the <c>exclude_from_context</c> column to every partition's <c>mesh_nodes</c>.
///
/// <para><b>Background.</b> <c>MeshNode.ExcludeFromContext</c> carries the instance-level
/// context opt-outs (<c>"header"</c> for chrome-less marketing pages, <c>"search"</c>,
/// <c>"create"</c> — see <c>MeshNodeVisibility</c>), but the Postgres <c>mesh_nodes</c> table
/// never had a column for it and the adapter never wrote/read it — so on every PG-backed mesh
/// the field silently round-tripped as NULL: imported brochures kept rendering the node header
/// and instance-level search/create exclusions never applied. Same defect class as the V45
/// authorship columns; the adapter (same change set) now persists and hydrates the column.</para>
///
/// <para><b>Steps per partition schema (idempotent):</b> <c>ALTER TABLE … ADD COLUMN IF NOT
/// EXISTS exclude_from_context TEXT[]</c>. History tables are untouched — the opt-out is
/// current-state metadata, not versioned content. No backfill: the field was never captured;
/// values populate on the first write (e.g. a forced GitSync re-import) after the fix.</para>
/// </summary>
public sealed class V46_AddExcludeFromContextColumn : IMigration
{
    public int Version => 46;
    public string Description => "Add exclude_from_context TEXT[] to mesh_nodes on every partition schema (PG dropped MeshNode.ExcludeFromContext on write)";

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
        await using (var rdr = await discover.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        foreach (var schema in schemas)
        {
            var quotedSchema = "\"" + schema.Replace("\"", "\"\"") + "\"";
            await using var alter = ctx.DataSource.CreateCommand($"""
                ALTER TABLE {quotedSchema}.mesh_nodes
                    ADD COLUMN IF NOT EXISTS exclude_from_context TEXT[]
                """);
            await alter.ExecuteNonQueryAsync();
        }

        ctx.Logger.LogInformation(
            "Repair v46: done — {Count} partition schema(s) now persist MeshNode.ExcludeFromContext", schemas.Count);
    }
}
