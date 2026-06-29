#nullable enable
#r "nuget: Npgsql, 9.0.2"
#r "nuget: Azure.Identity, 1.13.1"

using Azure.Core;
using Azure.Identity;
using Npgsql;

const string Host = "memexpostgres-d272wxvys4nvo.postgres.database.azure.com";
const string Db = "memex";
var user = Environment.GetEnvironmentVariable("AZURE_USER_PRINCIPAL_NAME") ?? "rbuergi@systemorph.com";

var cred = new DefaultAzureCredential();
var token = await cred.GetTokenAsync(new TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }));
var conn = new NpgsqlConnection($"Host={Host};Database={Db};Username={user};Password={token.Token};SSL Mode=Require");
await conn.OpenAsync();

Console.WriteLine($"Connected as {user}");
Console.WriteLine();

// Find every entry in searchable_schemas whose schema does NOT exist in PG.
// `apitoken` is the known offender; this query catches it AND any other
// orphan that might have crept in.
await using (var diag = new NpgsqlCommand(@"
    SELECT ss.schema_name
      FROM public.searchable_schemas ss
     WHERE NOT EXISTS (
        SELECT 1 FROM information_schema.schemata s
         WHERE s.schema_name = ss.schema_name
     )
     ORDER BY ss.schema_name", conn))
await using (var rdr = await diag.ExecuteReaderAsync())
{
    Console.WriteLine("Orphans in public.searchable_schemas (no matching PG schema):");
    var any = false;
    while (await rdr.ReadAsync()) { Console.WriteLine($"  - {rdr.GetString(0)}"); any = true; }
    if (!any) Console.WriteLine("  (none)");
}
Console.WriteLine();

await using (var del = new NpgsqlCommand(@"
    DELETE FROM public.searchable_schemas
     WHERE schema_name NOT IN (
        SELECT s.schema_name FROM information_schema.schemata s
     )", conn))
{
    var n = await del.ExecuteNonQueryAsync();
    Console.WriteLine($"DELETE removed {n} orphan row(s).");
}
Console.WriteLine();

Console.WriteLine("public.searchable_schemas after fix:");
await using (var cmd = new NpgsqlCommand("SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync())
        Console.WriteLine($"  {rdr.GetString(0)}");
}
