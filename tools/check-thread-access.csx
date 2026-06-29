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

Console.WriteLine("== rbuergi.user_effective_permissions ==");
await using (var cmd = new NpgsqlCommand(
    "SELECT user_id, node_path_prefix, permission, is_allow FROM rbuergi.user_effective_permissions ORDER BY user_id, node_path_prefix LIMIT 30", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync())
        Console.WriteLine($"  user='{rdr.GetString(0)}' prefix='{rdr.GetString(1)}' perm='{rdr.GetString(2)}' allow={rdr.GetBoolean(3)}");
}

Console.WriteLine();
Console.WriteLine("== public.partition_access ==");
await using (var cmd = new NpgsqlCommand(
    "SELECT user_id, partition FROM public.partition_access WHERE user_id LIKE '%rbuergi%' OR partition = 'rbuergi' ORDER BY user_id", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync())
        Console.WriteLine($"  user='{rdr.GetString(0)}' partition='{rdr.GetString(1)}'");
}

Console.WriteLine();
Console.WriteLine("== rbuergi.access raw rows ==");
await using (var cmd = new NpgsqlCommand(
    "SELECT namespace, id, content::text FROM rbuergi.access ORDER BY last_modified", conn))
await using (var rdr = await cmd.ExecuteReaderAsync())
{
    while (await rdr.ReadAsync())
        Console.WriteLine($"  ns='{rdr.GetString(0)}' id='{rdr.GetString(1)}'\n     content={rdr.GetString(2)}");
}
