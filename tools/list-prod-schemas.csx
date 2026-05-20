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

Console.WriteLine("== All non-system schemas + mesh_nodes presence ==");
await using (var cmd = new NpgsqlCommand(@"
    SELECT s.schema_name,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=s.schema_name AND t.table_name='mesh_nodes') AS has_mesh_nodes,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=s.schema_name AND t.table_name='threads') AS has_threads,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=s.schema_name AND t.table_name='access') AS has_access
      FROM information_schema.schemata s
     WHERE s.schema_name NOT LIKE 'pg_%'
       AND s.schema_name NOT IN ('public','information_schema')
     ORDER BY s.schema_name", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    Console.WriteLine($"{"schema",-30} {"mesh",6} {"threads",8} {"access",7}");
    while (await rdr.ReadAsync())
        Console.WriteLine($"{rdr.GetString(0),-30} {rdr.GetBoolean(1),6} {rdr.GetBoolean(2),8} {rdr.GetBoolean(3),7}");
}

Console.WriteLine();
Console.WriteLine("== public.searchable_schemas ==");
await using (var cmd = new NpgsqlCommand("SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync())
        Console.WriteLine($"  {rdr.GetString(0)}");
}
