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

const int ExpectedVersion = 15; // bump in lock-step with DbVersionGate.ExpectedDbVersion

var mode = (Args.Count > 0 ? Args[0] : "prod").ToLowerInvariant();
var rg = mode switch
{
    "prod" => "prod-memex",
    "test" => "test-memex",
    _      => throw new ArgumentException($"unknown mode: {mode} (expected prod|test)"),
};
var dbName = mode == "test" ? "memex-test" : "memex";

// Discover the FQDN dynamically — we don't want a stale hard-coded host
// here drifting from what aspire deploy actually provisioned.
string host;
{
    var psi = new System.Diagnostics.ProcessStartInfo("az",
        $"postgres flexible-server list -g {rg} --query \"[0].fullyQualifiedDomainName\" -o tsv")
    {
        RedirectStandardOutput = true, UseShellExecute = false
    };
    using var p = System.Diagnostics.Process.Start(psi)!;
    host = (await p.StandardOutput.ReadToEndAsync()).Trim();
    await p.WaitForExitAsync();
    if (string.IsNullOrEmpty(host))
        throw new InvalidOperationException(
            $"Couldn't resolve postgres FQDN in resource group {rg}. Is `az login` current and the RG correct?");
}
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
var version = raw switch
{
    int v => v,
    long l => (int)l,
    _ => 0
};

if (version < ExpectedVersion)
{
    Console.Error.WriteLine($"db_version={version} < expected {ExpectedVersion}");
    Environment.Exit(1);
}

Console.WriteLine($"✅ db_version={version} (>= {ExpectedVersion})");
