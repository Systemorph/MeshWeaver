#nullable enable
#r "nuget: Npgsql, 9.0.2"
#r "nuget: Azure.Identity, 1.13.1"

using Azure.Core;
using Azure.Identity;
using Npgsql;

const string Host = "memexpostgres-d272wxvys4nvo.postgres.database.azure.com";
const string Db = "memex";
var caller = Environment.GetEnvironmentVariable("AZURE_USER_PRINCIPAL_NAME") ?? "rbuergi@systemorph.com";

var cred = new DefaultAzureCredential();
var token = await cred.GetTokenAsync(new TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }));
var conn = new NpgsqlConnection($"Host={Host};Database={Db};Username={caller};Password={token.Token};SSL Mode=Require");
await conn.OpenAsync();

Console.WriteLine("== Every nodeType=User across all schemas with mesh_nodes ==");
await using (var cmd = new NpgsqlCommand(@"
    DO $$
    DECLARE
        rec RECORD;
    BEGIN
        FOR rec IN SELECT schema_name FROM public.searchable_schemas ORDER BY schema_name LOOP
            EXECUTE format('SELECT %L AS src, namespace, id, name, last_modified, content->>''email'' AS email FROM %I.mesh_nodes WHERE node_type = ''User''', rec.schema_name, rec.schema_name);
        END LOOP;
    END$$;", conn))
    try { await cmd.ExecuteNonQueryAsync(); } catch (Exception ex) { Console.WriteLine($"  (DO loop output isn't captured this way: {ex.Message.Split('\n')[0]})"); }

// Manual fan-out via UNION ALL with the partitions we know about.
var schemas = new[] {"apitoken", "carson", "doc", "globalsettings", "meshweaver", "mkleiner", "partnerre", "rbuergi", "release", "rsalzmann", "sglauser", "system_access", "systemorph", "thomager12", "user"};
var union = string.Join("\nUNION ALL\n", schemas.Select(s =>
    $"SELECT '{s}' as src, namespace, id, name, last_modified, content->>'email' AS email FROM \"{s}\".mesh_nodes WHERE node_type = 'User'"));
await using (var cmd = new NpgsqlCommand(union + "\nORDER BY src, id", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    Console.WriteLine($"{"schema",-15} {"namespace",-20} {"id",-30} {"email",-30} {"last_modified",-25}");
    while (await rdr.ReadAsync())
        Console.WriteLine($"{rdr.GetString(0),-15} {(rdr.IsDBNull(1)?"":rdr.GetString(1)),-20} {rdr.GetString(2),-30} {(rdr.IsDBNull(5)?"":rdr.GetString(5)),-30} {rdr.GetDateTime(4):s}");
}
