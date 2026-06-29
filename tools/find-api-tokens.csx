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

Console.WriteLine("== ApiToken validation index (apitoken schema) ==");
await using (var cmd = new NpgsqlCommand(
    "SELECT namespace, id, name, last_modified, version, main_node FROM apitoken.mesh_nodes ORDER BY last_modified DESC LIMIT 20", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    Console.WriteLine($"{"namespace",-20} {"id",-50} {"main_node",-50} {"last_modified",-30}");
    while (await rdr.ReadAsync())
    {
        Console.WriteLine($"{rdr.GetString(0),-20} {rdr.GetString(1),-50} {(rdr.IsDBNull(5)?"":rdr.GetString(5)),-50} {rdr.GetDateTime(3):s}");
    }
}
Console.WriteLine();

Console.WriteLine("== _Api satellite per partition (User/{user}/_Api/{hash}) ==");
foreach (var schema in new[] { "user", "rbuergi", "rbuergi@systemorph.com", "systemorph" })
{
    Console.WriteLine($"  -- {schema} --");
    await using var cmd = new NpgsqlCommand(
        $"SELECT path, node_type, last_modified FROM \"{schema}\".mesh_nodes WHERE node_type='ApiToken' OR namespace LIKE '%/_Api' ORDER BY last_modified DESC LIMIT 10", conn);
    try
    {
        await using var rdr = await cmd.ExecuteReaderAsync();
        var any = false;
        while (await rdr.ReadAsync())
        {
            Console.WriteLine($"    {(rdr.IsDBNull(0)?"":rdr.GetString(0)),-60} {(rdr.IsDBNull(1)?"":rdr.GetString(1)),-15} {rdr.GetDateTime(2):s}");
            any = true;
        }
        if (!any) Console.WriteLine("    (none)");
    }
    catch (Exception ex) { Console.WriteLine($"    ERROR: {ex.Message.Split('\n')[0]}"); }
}
Console.WriteLine();

Console.WriteLine("== What's at 'welcome/...' paths? ==");
foreach (var schema in new[] { "doc", "user", "system_access" })
{
    await using var cmd = new NpgsqlCommand(
        $"SELECT path, node_type FROM \"{schema}\".mesh_nodes WHERE path LIKE 'welcome/%' OR path = 'welcome' OR namespace = 'welcome' LIMIT 5", conn);
    try
    {
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
            Console.WriteLine($"  {schema}: {(rdr.IsDBNull(0)?"":rdr.GetString(0))} (type={(rdr.IsDBNull(1)?"":rdr.GetString(1))})");
    }
    catch (Exception ex) { Console.WriteLine($"  {schema}: ERROR: {ex.Message.Split('\n')[0]}"); }
}
