using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Repopulates <c>public.searchable_schemas</c> from the current set of content partitions.
/// Idempotent and runs on every migration — schemas can be added or removed between runs.
/// </summary>
public static class SearchableSchemasUpdater
{
    private static readonly HashSet<string> ExcludedSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "portal", "kernel",
        "_access", "_address_", "_graph", "_settings", "_tracking", "_thread", "_source", "_test",
        "source", "test",
        "login", "markdown", "onboarding", "welcome", "settings", "storage",
        "p", "mesh", "thread", "agent", "partition", "organization", "vuser",
        "public", "information_schema", "pg_catalog", "pg_toast"
    };

    public static async Task RunAsync(NpgsqlDataSource dataSource, ILogger logger)
    {
        // Discover content schemas — same logic as
        // PostgreSqlPartitionedStoreFactory.DiscoverPartitionsAsync.
        var contentSchemas = new List<string>();
        await using (var discoverCmd = dataSource.CreateCommand("""
            SELECT schema_name FROM information_schema.schemata s
            WHERE EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes')
            AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
            AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
            ORDER BY s.schema_name
            """))
        {
            await using var rdr = await discoverCmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var schema = rdr.GetString(0);
                if (!ExcludedSchemas.Contains(schema))
                    contentSchemas.Add(schema);
            }
        }

        await using (var clearCmd = dataSource.CreateCommand("DELETE FROM public.searchable_schemas"))
            await clearCmd.ExecuteNonQueryAsync();

        foreach (var schema in contentSchemas)
        {
            await using var insertCmd = dataSource.CreateCommand(
                "INSERT INTO public.searchable_schemas (schema_name) VALUES ($1) ON CONFLICT DO NOTHING");
            insertCmd.Parameters.AddWithValue(schema);
            await insertCmd.ExecuteNonQueryAsync();
        }

        logger.LogInformation("Searchable schemas: [{Schemas}]", string.Join(", ", contentSchemas));
    }
}
