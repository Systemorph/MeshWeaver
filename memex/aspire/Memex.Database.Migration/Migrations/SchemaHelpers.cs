using System.Text;
using MeshWeaver.Hosting.PostgreSql;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Shared helpers used by multiple migrations: partition-schema discovery, name sanitisation,
/// and per-schema data-source bootstrapping.
/// </summary>
internal static class SchemaHelpers
{
    /// <summary>Discover schemas that look like content partitions (have a <c>mesh_nodes</c> table).</summary>
    public static async Task<List<string>> DiscoverPartitionSchemasAsync(NpgsqlDataSource dataSource)
        => await DiscoverSchemasAsync(dataSource, requireTable: "mesh_nodes");

    /// <summary>Discover schemas that have an <c>access</c> table — used by access-related repairs.</summary>
    public static async Task<List<string>> DiscoverAccessSchemasAsync(NpgsqlDataSource dataSource)
        => await DiscoverSchemasAsync(dataSource, requireTable: "access");

    private static async Task<List<string>> DiscoverSchemasAsync(NpgsqlDataSource dataSource, string requireTable)
    {
        var schemas = new List<string>();
        await using var listCmd = dataSource.CreateCommand($"""
            SELECT schema_name FROM information_schema.schemata s
            WHERE EXISTS (SELECT 1 FROM information_schema.tables t WHERE t.table_schema = s.schema_name AND t.table_name = '{requireTable}')
            AND s.schema_name NOT IN ('public', 'information_schema', 'pg_catalog', 'pg_toast')
            AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
            ORDER BY s.schema_name
            """);
        await using var rdr = await listCmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync()) schemas.Add(rdr.GetString(0));
        return schemas;
    }

    /// <summary>
    /// Sanitises an arbitrary identifier (e.g., a userId) to a Postgres schema name —
    /// must match <c>PostgreSqlPartitionedStoreFactory.SanitizeSchemaName</c>: lowercase,
    /// non-alphanumeric → '_', leading digit prefixed with '_'.
    /// </summary>
    public static string SanitizeSchemaName(string s)
    {
        var lower = s.ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in lower)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        var result = sb.ToString();
        if (result.Length > 0 && char.IsDigit(result[0])) result = "_" + result;
        return result;
    }

    /// <summary>
    /// Build a per-schema NpgsqlDataSource with <c>SearchPath = "{schema},public"</c> and pgvector
    /// enabled — used for migrations that need to (re)create stored procedures inside a partition.
    /// </summary>
    public static NpgsqlDataSource BuildSchemaDataSource(string baseConnectionString, string schema, bool useVector = true)
    {
        var csb = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = $"{schema},public" };
        var dsb = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        if (useVector) dsb.UseVector();
        return dsb.Build();
    }

    /// <summary>Build the per-schema PostgreSqlStorageOptions for a partition migration.</summary>
    public static PostgreSqlStorageOptions BuildSchemaOptions(string baseConnectionString, string schema, int vectorDimensions)
    {
        var csb = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = $"{schema},public" };
        return new PostgreSqlStorageOptions
        {
            ConnectionString = csb.ConnectionString,
            VectorDimensions = vectorDimensions,
            Schema = schema
        };
    }

    /// <summary>Does a Postgres schema with this name exist?</summary>
    public static async Task<bool> SchemaExistsAsync(NpgsqlDataSource dataSource, string schemaName)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = $1)");
        cmd.Parameters.AddWithValue(schemaName);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
