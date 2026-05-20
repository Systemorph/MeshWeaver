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
Console.WriteLine();

// Cross-check every searchable_schemas row against information_schema.tables for
// mesh_nodes / threads / access — these are what cross-schema UNION queries try to
// open. ANY false is a 42P01 waiting to happen.
Console.WriteLine("== searchable_schemas vs satellite tables ==");
await using (var cmd = new NpgsqlCommand(@"
    SELECT ss.schema_name,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=ss.schema_name AND t.table_name='mesh_nodes') AS has_mesh_nodes,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=ss.schema_name AND t.table_name='threads') AS has_threads,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=ss.schema_name AND t.table_name='access') AS has_access,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=ss.schema_name AND t.table_name='user_activities') AS has_user_activities,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=ss.schema_name AND t.table_name='activities') AS has_activities,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=ss.schema_name AND t.table_name='annotations') AS has_annotations,
           EXISTS(SELECT 1 FROM information_schema.tables t WHERE t.table_schema=ss.schema_name AND t.table_name='code') AS has_code
      FROM public.searchable_schemas ss
     ORDER BY ss.schema_name", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    Console.WriteLine($"{"schema",-26} {"mesh",5} {"thrds",5} {"acc",5} {"u_act",5} {"act",5} {"ann",5} {"code",5}");
    while (await rdr.ReadAsync())
    {
        Console.WriteLine($"{rdr.GetString(0),-26} {rdr.GetBoolean(1),5} {rdr.GetBoolean(2),5} {rdr.GetBoolean(3),5} {rdr.GetBoolean(4),5} {rdr.GetBoolean(5),5} {rdr.GetBoolean(6),5} {rdr.GetBoolean(7),5}");
    }
}
