using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Repairs <b>typeless partition-root nodes</b> by setting <c>node_type='Space'</c>.
///
/// <para>Symptom: a partition root (e.g. <c>AgenticPensions</c>) exists with
/// <c>namespace='' </c>, <c>state=Active</c> but <b>no <c>node_type</c></b>. Because the
/// per-node hub resolves its config from the node type
/// (<c>NodeTypeEnrichmentHelpers</c>: a null type falls back to the default hub config, NOT
/// the Space config), a typeless root gets no <c>AddContentCollections()</c> — so its
/// <c>/Files</c> view has no backing and spins — and it is invisible in the Spaces catalog
/// (which queries <c>nodeType:Space</c>). These roots predate proper Space creation
/// (<c>CreateLayoutArea</c> always sets <c>NodeType</c> now) and V28's Organization→Space
/// rename couldn't catch them (it matched <c>node_type='Organization'</c>).</para>
///
/// <para>Fix: for every content-partition schema, set <c>node_type='Space'</c> on the root
/// row whose <c>node_type IS NULL</c>. This is safe and precise:</para>
/// <list type="bullet">
///   <item>User-partition roots are <c>node_type='User'</c> (not NULL) → untouched.</item>
///   <item>Already-typed Space roots are <c>node_type='Space'</c> (not NULL) → untouched.</item>
///   <item>System schemas (public/admin/auth/doc/*_versions) are excluded by name.</item>
/// </list>
/// Idempotent (the NULL guard means a re-run is a no-op). Casing-safe: operates on the
/// actual schema names + stored row, never deriving paths from lower-cased names.
/// </summary>
public sealed class V34_TypeOrphanPartitionRootsAsSpace : IMigration
{
    public int Version => 34;
    public string Description => "Set node_type='Space' on typeless partition-root nodes (partitions that lost their type)";

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = new List<string>();
        await using (var discoverCmd = ctx.DataSource.CreateCommand("""
            SELECT t.table_schema
            FROM information_schema.tables t
            WHERE t.table_name = 'mesh_nodes'
              AND t.table_schema NOT IN
                  ('information_schema','pg_catalog','pg_toast','public','admin','auth','doc')
              AND t.table_schema NOT LIKE '%\_versions'
            ORDER BY t.table_schema
            """))
        await using (var rdr = await discoverCmd.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        var fixedCount = 0;
        foreach (var schema in schemas)
        {
            var quotedSchema = schema.Replace("\"", "\"\"");
            await using var upd = ctx.DataSource.CreateCommand($"""
                UPDATE "{quotedSchema}".mesh_nodes
                   SET node_type = 'Space', last_modified = now()
                 WHERE namespace = '' AND node_type IS NULL
                """);
            var rows = await upd.ExecuteNonQueryAsync();
            if (rows > 0)
            {
                ctx.Logger.LogInformation(
                    "Repair v34: '{Schema}' — typed {Rows} orphan partition root(s) as Space", schema, rows);
                fixedCount += rows;
            }
        }

        ctx.Logger.LogInformation("Repair v34: done — {Count} orphan partition root(s) typed as Space", fixedCount);
    }
}
