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

Console.WriteLine("== rbuergi.threads (looking for hello-2a76) ==");
await using (var cmd = new NpgsqlCommand(
    "SELECT namespace, id, name, node_type, version, state, main_node, last_modified FROM rbuergi.threads WHERE id LIKE '%hello-2a76%' OR namespace LIKE '%hello-2a76%' OR main_node LIKE '%hello-2a76%' LIMIT 10", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    var any = false;
    while (await rdr.ReadAsync())
    {
        Console.WriteLine($"  ns='{rdr.GetString(0)}' id='{rdr.GetString(1)}' type={rdr.GetString(3)} ver={rdr.GetInt64(4)} state={rdr.GetInt16(5)} main='{(rdr.IsDBNull(6)?"":rdr.GetString(6))}' lm={rdr.GetDateTime(7):s}");
        any = true;
    }
    if (!any) Console.WriteLine("  (none) -- maybe in a different partition?");
}

Console.WriteLine();
Console.WriteLine("== thread search across schemas ==");
await using (var cmd = new NpgsqlCommand(@"
    SELECT 'rbuergi/_Thread' as src, namespace, id, version, state FROM rbuergi.threads WHERE id = 'hello-2a76'
    UNION ALL
    SELECT 'rbuergi.mesh_nodes' as src, namespace, id, version, state FROM rbuergi.mesh_nodes WHERE namespace = 'rbuergi/_Thread' AND id = 'hello-2a76'", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync())
        Console.WriteLine($"  {rdr.GetString(0)}: ns='{rdr.GetString(1)}' id='{rdr.GetString(2)}' ver={rdr.GetInt64(3)} state={rdr.GetInt16(4)}");
}

Console.WriteLine();
Console.WriteLine("== rbuergi access assignments ==");
await using (var cmd = new NpgsqlCommand(
    "SELECT namespace, id, content FROM rbuergi.access ORDER BY last_modified DESC LIMIT 10", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync())
        Console.WriteLine($"  ns='{rdr.GetString(0)}' id='{rdr.GetString(1)}' content={(rdr.IsDBNull(2)?"":rdr.GetString(2)[..Math.Min(140, rdr.GetString(2).Length)])}");
}
