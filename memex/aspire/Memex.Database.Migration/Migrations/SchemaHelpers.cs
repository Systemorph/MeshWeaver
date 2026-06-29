using System.Text;
using System.Text.Json;
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

        if (isAzure)
        {
            // Mirror Aspire's exact AAD wiring (release/13.2 src/Components/Common/
            // ManagedIdentityTokenCredentialHelpers.cs::ConfigureEntraIdAuthentication).
            // Two pieces are required, and the per-schema datasource has historically
            // missed both:
            //
            //   1. Username derivation. The conn string Aspire injects has NO Username —
            //      Aspire fills it at runtime from the access token's `xms_mirid` claim
            //      (last segment after `userAssignedIdentities/` → e.g.
            //      "db_migration_identity"). Without this, Npgsql falls back to
            //      Environment.UserName which is "app" in mcr.microsoft.com/dotnet/aspnet:10.0,
            //      and Postgres rejects with 28P01: password authentication failed for user "app".
            //   2. Per-connection UsePasswordProvider with the ossrdbms scope. Azure.Identity
            //      caches tokens internally, so no need for UsePeriodicPasswordProvider.
            if (string.IsNullOrEmpty(csb.Username))
            {
                // Management scope first — its tokens carry user-name claims for both UAMIs
                // and SPs (Aspire does the same).
                var mgmtToken = AzureCredential.GetToken(s_managementTokenRequestContext, default);
                if (TryGetUsernameFromToken(mgmtToken.Token, out var username) ||
                    TryGetUsernameFromToken(
                        AzureCredential.GetToken(s_databaseForPostgresSqlTokenRequestContext, default).Token,
                        out username))
                {
                    csb.Username = username;
                }
                // If neither token carries a username, leave Username unset and let Npgsql
                // surface the misconfiguration on connect (Aspire does the same).
            }

            if (!hasPassword)
            {
                dsb = new NpgsqlDataSourceBuilder(csb.ConnectionString);
                if (useVector) dsb.UseVector();

                dsb.UsePasswordProvider(
                    _ => AzureCredential.GetToken(s_databaseForPostgresSqlTokenRequestContext, default).Token,
                    async (_, ct) => (await AzureCredential.GetTokenAsync(s_databaseForPostgresSqlTokenRequestContext, ct).ConfigureAwait(false)).Token);
            }
        }

        return dsb.Build();
    }

    private static readonly TokenRequestContext s_databaseForPostgresSqlTokenRequestContext =
        new(["https://ossrdbms-aad.database.windows.net/.default"]);
    private static readonly TokenRequestContext s_managementTokenRequestContext =
        new(["https://management.azure.com/.default"]);

    // Verbatim port of Aspire's TryGetUsernameFromToken /
    // ParsePrincipalName / AddBase64Padding from release/13.2
    // src/Components/Common/ManagedIdentityTokenCredentialHelpers.cs.
    private static bool TryGetUsernameFromToken(string jwtToken, out string? username)
    {
        username = null;
        try
        {
            var tokenParts = jwtToken.Split('.');
            if (tokenParts.Length != 3) return false;

            var payload = AddBase64Padding(tokenParts[1]);
            var decodedBytes = Convert.FromBase64String(payload);
            var reader = new Utf8JsonReader(decodedBytes);
            var payloadJson = JsonElement.ParseValue(ref reader);

            if (payloadJson.TryGetProperty("xms_mirid", out var mirid) &&
                mirid.GetString() is string miridString &&
                ParsePrincipalName(miridString) is string principalName)
            {
                username = principalName;
            }
            else if (payloadJson.TryGetProperty("upn", out var upn))
                username = upn.GetString();
            else if (payloadJson.TryGetProperty("preferred_username", out var preferred))
                username = preferred.GetString();
            else if (payloadJson.TryGetProperty("unique_name", out var unique))
                username = unique.GetString();

            return username != null;
        }
        catch
        {
            return false;
        }
    }

    private static string? ParsePrincipalName(string xmsMirid)
    {
        var lastSlash = xmsMirid.LastIndexOf('/');
        if (lastSlash == -1) return null;

        var beginning = xmsMirid.AsSpan(0, lastSlash);
        var principalName = xmsMirid.AsSpan(lastSlash + 1);

        if (principalName.IsEmpty ||
            !beginning.EndsWith("providers/Microsoft.ManagedIdentity/userAssignedIdentities", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return principalName.ToString();
    }

    private static string AddBase64Padding(string base64) => (base64.Length % 4) switch
    {
        2 => base64 + "==",
        3 => base64 + "=",
        _ => base64,
    };

    /// <summary>
    /// Shared <see cref="ManagedIdentityCredential"/> reused across per-schema
    /// datasources — avoids re-authenticating for every schema while a migration
    /// walks dozens of partitions (Azure.Identity caches the access token internally).
    ///
    /// <para>This is the *exact* construction Aspire uses for AAD-on-Postgres
    /// (see <c>src/Shared/AzureCredentialHelper.cs</c> in <c>dotnet/aspire</c>):
    /// a <c>ManagedIdentityCredential</c> pinned to the UAMI whose client id is
    /// in <c>AZURE_CLIENT_ID</c>. Bare <c>DefaultAzureCredential</c> is wrong in
    /// a multi-UAMI Container App because the chain runs EnvironmentCredential /
    /// WorkloadIdentityCredential first and IMDS returns 400
    /// <c>multiple_matching_tokens</c> when more than one UAMI is attached and
    /// no client id is specified — typical symptom is
    /// <c>28P01: password authentication failed for user "app"</c>.</para>
    /// </summary>
    private static readonly ManagedIdentityCredential AzureCredential =
        new(new ManagedIdentityCredentialOptions(
            ManagedIdentityId.FromUserAssignedClientId(
                Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
                ?? throw new InvalidOperationException(
                    "AZURE_CLIENT_ID env var must be set on the Container App so the per-schema "
                    + "datasource can pin to the intended User-Assigned Managed Identity. "
                    + "Aspire AppHost should set this automatically when WithRoleAssignments is wired."))));

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
