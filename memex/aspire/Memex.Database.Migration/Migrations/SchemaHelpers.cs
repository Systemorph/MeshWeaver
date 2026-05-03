using System.Text;
using Azure.Core;
using Azure.Identity;
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
    /// Build a per-schema NpgsqlDataSource with <c>SearchPath = "{schema},public"</c>,
    /// pgvector enabled, and Azure AD password-provider wired when the connection
    /// string targets an Azure-managed Postgres (host ends in
    /// <c>.postgres.database.azure.com</c> AND password is empty).
    ///
    /// <para>Without the AAD provider, per-schema migrations on prod/test fail
    /// with <c>28000: no pg_hba.conf entry for host … user "app"</c> because the
    /// raw <c>NpgsqlDataSourceBuilder.Build()</c> attempts password auth with an
    /// empty password against an SSL-required, AAD-only server. Aspire's
    /// <c>AddAzureNpgsqlDataSource</c> wires this token provider on the *main*
    /// runner connection — but every per-schema datasource we spin up here
    /// needs the same hook or it falls back to anonymous password auth and dies.
    /// Local Docker postgres (mode=local) uses username+password and is left
    /// untouched.</para>
    /// </summary>
    public static NpgsqlDataSource BuildSchemaDataSource(string baseConnectionString, string schema, bool useVector = true)
    {
        var csb = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = $"{schema},public" };

        var isAzure = csb.Host?.EndsWith(".postgres.database.azure.com", StringComparison.OrdinalIgnoreCase) == true;
        var hasPassword = !string.IsNullOrEmpty(csb.Password);

        // Aspire's AddAzureNpgsqlDataSource enforces SSL via the *builder*, not the conn string.
        // When we clone the conn string here, that enforcement is lost — the server then rejects
        // us with `28000: no pg_hba.conf entry for host …, no encryption`. Azure Flexible Server
        // requires SSL unconditionally, so force it on every per-schema datasource we build.
        if (isAzure)
            csb.SslMode = SslMode.Require;

        var dsb = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        if (useVector) dsb.UseVector();

        if (isAzure && !hasPassword)
        {
            dsb.UsePeriodicPasswordProvider(
                async (_, ct) =>
                {
                    var token = await AzureCredential.GetTokenAsync(
                        new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), ct);
                    return token.Token;
                },
                successRefreshInterval: TimeSpan.FromMinutes(45),
                failureRefreshInterval: TimeSpan.FromSeconds(30));
        }

        return dsb.Build();
    }

    /// <summary>
    /// Shared <see cref="DefaultAzureCredential"/> reused across per-schema
    /// password providers — avoids re-authenticating for every schema while a
    /// migration walks dozens of partitions.
    /// </summary>
    private static readonly DefaultAzureCredential AzureCredential = new();

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
