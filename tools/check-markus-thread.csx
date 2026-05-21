#nullable enable
#r "nuget: Npgsql, 9.0.2"
#r "nuget: Azure.Identity, 1.13.1"

using Azure.Core;
using Azure.Identity;
using Npgsql;

// Diagnostic for the prod 2026-05-21 thread page-not-found:
//   https://memex.meshweaver.cloud/Systemorph/_Thread/add-markus-kleiner-as-admin-to-systemorp-c578
//   → "Page not found: 'Systemorph/_Thread/add-markus-kleiner-as-admin-to-systemorp-c578'
//      does not match any registered address pattern."
//
// Three angles, in priority order:
//   1. Does the thread MeshNode exist in `Systemorph.threads`?
//   2. Does it exist in `Systemorph.mesh_nodes` (wrong satellite, would explain
//      a routing miss)?
//   3. Is there an AccessAssignment that grants the user Read on the partition?
//
// Auth via DefaultAzureCredential -> ossrdbms scope; same shape as
// tools/check-thread.csx.

const string Host = "memexpostgres-d272wxvys4nvo.postgres.database.azure.com";
const string Db = "memex";
const string ThreadId = "add-markus-kleiner-as-admin-to-systemorp-c578";
const string Namespace = "Systemorph/_Thread";
var user = Environment.GetEnvironmentVariable("AZURE_USER_PRINCIPAL_NAME") ?? "rbuergi@systemorph.com";

var cred = new DefaultAzureCredential();
var token = await cred.GetTokenAsync(new TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }));
var conn = new NpgsqlConnection($"Host={Host};Database={Db};Username={user};Password={token.Token};SSL Mode=Require");
await conn.OpenAsync();
Console.WriteLine($"Connected as {user}");

Console.WriteLine();
Console.WriteLine("== 1. Systemorph.threads (proper satellite table for _Thread) ==");
await using (var cmd = new NpgsqlCommand(
    @"SELECT namespace, id, node_type, version, state, main_node, last_modified
      FROM Systemorph.threads
      WHERE id = @id OR id LIKE '%markus-kleiner%' OR namespace = @ns", conn))
{
    cmd.Parameters.AddWithValue("id", ThreadId);
    cmd.Parameters.AddWithValue("ns", Namespace);
    await using var rdr = await cmd.ExecuteReaderAsync();
    var any = false;
    while (await rdr.ReadAsync())
    {
        Console.WriteLine($"  ns='{rdr.GetString(0)}' id='{rdr.GetString(1)}' type='{rdr.GetString(2)}' " +
            $"ver={rdr.GetInt64(3)} state={rdr.GetInt16(4)} " +
            $"main='{(rdr.IsDBNull(5) ? "" : rdr.GetString(5))}' lm={rdr.GetDateTime(6):s}");
        any = true;
    }
    if (!any) Console.WriteLine("  (no row)");
}

Console.WriteLine();
Console.WriteLine("== 2. Systemorph.mesh_nodes (wrong-table check) ==");
await using (var cmd = new NpgsqlCommand(
    @"SELECT namespace, id, node_type, version, state, last_modified
      FROM Systemorph.mesh_nodes
      WHERE id = @id OR id LIKE '%markus-kleiner%'", conn))
{
    cmd.Parameters.AddWithValue("id", ThreadId);
    await using var rdr = await cmd.ExecuteReaderAsync();
    var any = false;
    while (await rdr.ReadAsync())
    {
        Console.WriteLine($"  ns='{rdr.GetString(0)}' id='{rdr.GetString(1)}' type='{rdr.GetString(2)}' " +
            $"ver={rdr.GetInt64(3)} state={rdr.GetInt16(4)} lm={rdr.GetDateTime(5):s}");
        any = true;
    }
    if (!any) Console.WriteLine("  (no row — good, would indicate wrong-table routing if non-empty)");
}

Console.WriteLine();
Console.WriteLine("== 3. Systemorph.access (AccessAssignment) — who can Read this partition? ==");
await using (var cmd = new NpgsqlCommand(
    @"SELECT namespace, id, content::text
      FROM Systemorph.access
      ORDER BY last_modified DESC LIMIT 20", conn))
{
    await using var rdr = await cmd.ExecuteReaderAsync();
    var any = false;
    while (await rdr.ReadAsync())
    {
        var content = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
        if (content.Length > 240) content = content[..240] + "…";
        Console.WriteLine($"  ns='{rdr.GetString(0)}' id='{rdr.GetString(1)}'");
        Console.WriteLine($"     content={content}");
        any = true;
    }
    if (!any) Console.WriteLine("  (no AccessAssignments in Systemorph.access — partition is private to its owner only)");
}

Console.WriteLine();
Console.WriteLine("== 4. Any thread row in any schema mentioning 'markus' (broad sweep) ==");
await using (var cmd = new NpgsqlCommand(
    @"SELECT table_schema, table_name
      FROM information_schema.tables
      WHERE table_name = 'threads' AND table_schema NOT IN ('pg_catalog','information_schema')", conn))
{
    var schemas = new List<string>();
    await using (var rdr = await cmd.ExecuteReaderAsync())
    {
        while (await rdr.ReadAsync())
            schemas.Add(rdr.GetString(0));
    }
    foreach (var sch in schemas)
    {
        await using var q = new NpgsqlCommand(
            $"SELECT '{sch}' AS sch, namespace, id, last_modified FROM \"{sch}\".threads WHERE id LIKE '%markus%' LIMIT 5", conn);
        await using var rdr = await q.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
            Console.WriteLine($"  schema={rdr.GetString(0)} ns='{rdr.GetString(1)}' id='{rdr.GetString(2)}' lm={rdr.GetDateTime(3):s}");
    }
}
