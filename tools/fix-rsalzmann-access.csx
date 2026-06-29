#nullable enable
// One-shot fix for the residual stale AccessAssignment row on prod:
//   systemorph.access  ns=Systemorph/_Access  id=rsalzmann_Access
//   ao=User/rsalzmann (legacy)  role=Role/Admin (legacy)
// Rewrite accessObject → 'rsalzmann' and roles[0].role → 'Admin'.
// Bump version + last_modified so the workspace cache picks up the change.
//
// Idempotent: matches the legacy shape only.
//
// Run with:  dotnet script tools/fix-rsalzmann-access.csx -- prod <PG_HOST>

#r "nuget: Npgsql, 9.0.2"
#r "nuget: Azure.Identity, 1.13.1"

using Azure.Core;
using Azure.Identity;
using Npgsql;

var mode = (Args.Count > 0 ? Args[0] : "prod").ToLowerInvariant();
var dbName = mode == "test" ? "memex-test" : "memex";
var host = Args.Count > 1 ? Args[1]
    : Environment.GetEnvironmentVariable("PG_HOST")
      ?? throw new InvalidOperationException("Pass PG_HOST as 2nd arg.");
var user = Environment.GetEnvironmentVariable("AZURE_USER_PRINCIPAL_NAME")
           ?? throw new InvalidOperationException("Set AZURE_USER_PRINCIPAL_NAME.");

var cred = new DefaultAzureCredential();
var token = await cred.GetTokenAsync(
    new TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }));
var connStr = $"Host={host};Database={dbName};Username={user};Password={token.Token};SSL Mode=Require";

await RunAsync();

async Task RunAsync()
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    // Show before
    Console.WriteLine("=== BEFORE ===");
    await ShowRow(conn);

    // Patch in a single statement: rewrite accessObject + roles[0].role,
    // bump version + last_modified. Match the legacy shape so re-runs no-op.
    await using var cmd = new NpgsqlCommand("""
        UPDATE systemorph.access SET
            content = jsonb_set(
                jsonb_set(content, '{accessObject}', '"rsalzmann"'::jsonb),
                '{roles,0,role}', '"Admin"'::jsonb),
            version = COALESCE(version, 0) + 1,
            last_modified = now()
        WHERE namespace = 'Systemorph/_Access'
          AND id = 'rsalzmann_Access'
          AND content->>'accessObject' = 'User/rsalzmann'
        """, conn);
    var affected = await cmd.ExecuteNonQueryAsync();
    Console.WriteLine();
    Console.WriteLine($"Rows updated: {affected}");
    Console.WriteLine();

    // Show after
    Console.WriteLine("=== AFTER ===");
    await ShowRow(conn);
}

async Task ShowRow(NpgsqlConnection conn)
{
    await using var cmd = new NpgsqlCommand("""
        SELECT namespace, id, content->>'accessObject' AS access_object,
               content->'roles'->0->>'role' AS first_role,
               version, last_modified
        FROM systemorph.access
        WHERE namespace = 'Systemorph/_Access' AND id = 'rsalzmann_Access'
        """, conn);
    await using var rdr = await cmd.ExecuteReaderAsync();
    if (await rdr.ReadAsync())
    {
        Console.WriteLine($"  ns={rdr.GetString(0)}  id={rdr.GetString(1)}  ao={rdr.GetString(2)}  role={rdr.GetString(3)}  v={rdr.GetValue(4)}  lm={rdr.GetDateTime(5):o}");
    }
    else
    {
        Console.WriteLine("  (no row)");
    }
}
