using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Rename <c>_Source</c>/<c>_Test</c> path segments to <c>Source</c>/<c>Test</c>.
///
/// Code nodes were renamed from satellite-style <c>_Source</c>/<c>_Test</c> sub-namespaces
/// to first-class <c>Source</c>/<c>Test</c> content folders (commit 0280084e7). Existing
/// DB rows still carry the old segment names in <c>namespace</c> and <c>main_node</c>;
/// the app now looks them up under the new names and finds nothing.
///
/// Fix: rewrite the path segment in place across every content partition's tables and
/// their <c>_versions</c> history. <c>path</c> is a GENERATED column and recomputes
/// itself. The routing target is unchanged (<c>code</c> table before and after), so rows
/// stay put.
/// </summary>
public sealed class V09_RenameSourceTestSegments : IMigration
{
    public int Version => 9;
    public string Description => "Rename _Source/_Test path segments to Source/Test";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Discover every schema (content partitions + their _versions mirrors) that has
        // at least one table with a `namespace` column.
        var schemas = new List<string>();
        await using (var listCmd = ctx.DataSource.CreateCommand("""
            SELECT DISTINCT s.schema_name
            FROM information_schema.schemata s
            JOIN information_schema.columns c
              ON c.table_schema = s.schema_name AND c.column_name = 'namespace'
            WHERE s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
            ORDER BY s.schema_name
            """))
        {
            await using var rdr = await listCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync()) schemas.Add(rdr.GetString(0));
        }

        var totalRowsUpdated = 0;
        foreach (var schema in schemas)
        {
            // Find all tables in this schema with both `namespace` and `main_node` columns
            // (mesh_nodes, code, access, threads, annotations, activities, ..., and
            // mesh_node_history in _versions schemas).
            var tables = new List<string>();
            await using (var tblCmd = ctx.DataSource.CreateCommand("""
                SELECT table_name
                FROM information_schema.columns
                WHERE table_schema = $1 AND column_name IN ('namespace', 'main_node')
                GROUP BY table_name
                HAVING COUNT(DISTINCT column_name) = 2
                ORDER BY table_name
                """))
            {
                tblCmd.Parameters.AddWithValue(schema);
                await using var rdr = await tblCmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync()) tables.Add(rdr.GetString(0));
            }

            foreach (var table in tables)
            {
                // Pre-delete legacy rows whose renamed counterpart already exists. After
                // commit 0280084e7 the app started writing the new `Source`/`Test` segment
                // names while old `_Source`/`_Test` rows were still in the DB; in some
                // partitions both versions coexist for the same (renamed_namespace, id).
                // The renamed row is canonical (that's what the app now reads), so the
                // legacy row is dead data — dropping it lets the UPDATE below succeed
                // without violating the (namespace, id) primary key.
                await using (var dedupCmd = ctx.DataSource.CreateCommand($"""
                    DELETE FROM "{schema}"."{table}" legacy
                    WHERE legacy.namespace ~ '(^|/)_(Source|Test)($|/)'
                      AND EXISTS (
                          SELECT 1 FROM "{schema}"."{table}" renamed
                          WHERE renamed.id = legacy.id
                            AND renamed.namespace = regexp_replace(
                                regexp_replace(legacy.namespace, '(^|/)_Source($|/)', '\1Source\2', 'g'),
                                '(^|/)_Test($|/)', '\1Test\2', 'g'
                            )
                      )
                    """))
                {
                    var deleted = await dedupCmd.ExecuteNonQueryAsync();
                    if (deleted > 0)
                        ctx.Logger.LogInformation(
                            "Repair v9: {Schema}.{Table} — pre-deleted {Count} legacy _Source/_Test row(s) whose renamed twin already existed",
                            schema, table, deleted);
                }

                // Rewrite `_Source` / `_Test` as whole path segments (anchored at string
                // start/end or bounded by '/'), preserving case and neighbours. Only
                // rewrite main_node when it is non-null — otherwise leave NULL alone.
                await using var fixCmd = ctx.DataSource.CreateCommand($"""
                    UPDATE "{schema}"."{table}" SET
                        namespace = regexp_replace(
                            regexp_replace(namespace, '(^|/)_Source($|/)', '\1Source\2', 'g'),
                            '(^|/)_Test($|/)', '\1Test\2', 'g'
                        ),
                        main_node = CASE
                            WHEN main_node IS NULL THEN NULL
                            ELSE regexp_replace(
                                regexp_replace(main_node, '(^|/)_Source($|/)', '\1Source\2', 'g'),
                                '(^|/)_Test($|/)', '\1Test\2', 'g'
                            )
                        END
                    WHERE namespace ~ '(^|/)_(Source|Test)($|/)'
                       OR main_node ~ '(^|/)_(Source|Test)($|/)'
                    """);
                var affected = await fixCmd.ExecuteNonQueryAsync();
                if (affected > 0)
                {
                    ctx.Logger.LogInformation(
                        "Repair v9: {Schema}.{Table} — renamed {Count} row(s)",
                        schema, table, affected);
                    totalRowsUpdated += affected;
                }
            }
        }

        ctx.Logger.LogInformation("Repair v9: updated {Total} row(s) across all schemas", totalRowsUpdated);
    }
}
