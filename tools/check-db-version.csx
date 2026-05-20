#nullable enable
// Verify admin.mesh_nodes.db_version is at or above the expected version for
// the deployed environment. Exits 0 on success, non-zero on missing/old.
//
// Run with:  dotnet script tools/check-db-version.csx -- {prod|test}
// Used by tools/deploy.sh as the post-deploy migration gate.

#r "nuget: Npgsql, 9.0.2"
#r "nuget: Azure.Identity, 1.13.1"

using Azure.Core;
using Azure.Identity;
using Npgsql;

const int ExpectedVersion = 24; // bump in lock-step with DbVersionGate.ExpectedDbVersion

var mode = (Args.Count > 0 ? Args[0] : "prod").ToLowerInvariant();
var rg = mode switch
{
    "prod" => "prod-memex",
    "test" => "test-memex",
    _      => throw new ArgumentException($"unknown mode: {mode} (expected prod|test)"),
};
var dbName = mode == "test" ? "memex-test" : "memex";

// FQDN of the deployed Postgres flexible-server. Pass it as the 2nd arg so
// we don't hard-code an environment-specific hostname (the random suffix
// changes whenever the resource group is reprovisioned). tools/deploy.sh
// resolves it via `az postgres flexible-server list -g $RG ...` and forwards
// it here. Falls back to PG_HOST env var for ad-hoc invocation.
var host = Args.Count > 1
    ? Args[1]
    : Environment.GetEnvironmentVariable("PG_HOST")
      ?? throw new InvalidOperationException(
          "Postgres host not provided. Pass it as the 2nd arg "
          + "(`dotnet script tools/check-db-version.csx -- prod <fqdn>`) "
          + "or set PG_HOST. tools/deploy.sh discovers it via `az` and forwards.");
var db = dbName;

// AAD identity from `az login`. The signed-in user must be a Postgres AAD admin
// (or a member of an AAD group that is).
var cred = new DefaultAzureCredential();
var token = await cred.GetTokenAsync(
    new TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }));

// User UPN comes from the same `az` context — same identity that minted the token.
var user = Environment.GetEnvironmentVariable("AZURE_USER_PRINCIPAL_NAME")
           ?? throw new InvalidOperationException(
               "Set AZURE_USER_PRINCIPAL_NAME to your AAD UPN (e.g. rbuergi@systemorph.com).");

// dotnet-script REPL doesn't support `using var` at the script root, so wrap
// the connection lifetime in an async lambda and let it dispose at scope exit.
int version = await GetDbVersionAsync();
async Task<int> GetDbVersionAsync()
{
    await using var conn = new NpgsqlConnection(
        $"Host={host};Database={db};Username={user};Password={token.Token};SSL Mode=Require");
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand("""
        SELECT (content->>'Version')::int AS v
          FROM admin.mesh_nodes
         WHERE id = 'db_version' AND namespace = ''
         LIMIT 1
        """, conn);
    var raw = await cmd.ExecuteScalarAsync();
    return raw switch
    {
        int v => v,
        long l => (int)l,
        _ => 0
    };
}

if (version < ExpectedVersion)
{
    Console.Error.WriteLine($"db_version={version} < expected {ExpectedVersion}");
    Environment.Exit(1);
}

Console.WriteLine($"✅ db_version={version} (>= {ExpectedVersion})");
