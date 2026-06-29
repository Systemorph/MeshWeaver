#nullable enable
// Read-only audit: scan every per-partition schema on prod for residual
// `User/...` paths, namespaces, accessObjects and main_node values that the
// V10..V15 migrations were supposed to rewrite. Reports counts per schema.
//
// Run with:  dotnet script tools/audit-user-prefix.csx -- prod <PG_HOST>

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

    // 1. Discover schemas with mesh_nodes.
    var schemas = new List<string>();
    await using (var cmd = new NpgsqlCommand("""
        SELECT s.schema_name
        FROM information_schema.schemata s
        WHERE EXISTS (
            SELECT 1 FROM information_schema.tables t
            WHERE t.table_schema = s.schema_name AND t.table_name = 'mesh_nodes')
          AND s.schema_name NOT IN ('information_schema', 'pg_catalog', 'pg_toast')
          AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
        ORDER BY s.schema_name
        """, conn))
    await using (var rdr = await cmd.ExecuteReaderAsync())
        while (await rdr.ReadAsync()) schemas.Add(rdr.GetString(0));

    Console.WriteLine($"Found {schemas.Count} schema(s) with mesh_nodes: {string.Join(", ", schemas)}");
    Console.WriteLine();

    // 2. Per-schema counts.
    foreach (var schema in schemas)
    {
        var qSchema = schema.Replace("\"", "\"\"");
        await using var cmd = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{qSchema}".mesh_nodes WHERE namespace LIKE 'User/%')                  AS ns_user,
                (SELECT count(*) FROM "{qSchema}".mesh_nodes WHERE namespace = 'User')                       AS ns_eq_user,
                (SELECT count(*) FROM "{qSchema}".mesh_nodes WHERE main_node LIKE 'User/%')                  AS mn_user,
                (SELECT count(*) FROM "{qSchema}".mesh_nodes WHERE node_type = 'AccessAssignment'
                    AND content->>'accessObject' LIKE 'User/%')                                              AS ao_user,
                (SELECT count(*) FROM "{qSchema}".mesh_nodes
                    WHERE node_type = 'AccessAssignment')                                                    AS aa_total,
                (SELECT count(*) FROM "{qSchema}".mesh_nodes)                                                AS total_nodes
            """, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        if (await rdr.ReadAsync())
        {
            var nsUser = rdr.GetInt64(0);
            var nsEq = rdr.GetInt64(1);
            var mnUser = rdr.GetInt64(2);
            var aoUser = rdr.GetInt64(3);
            var aaTotal = rdr.GetInt64(4);
            var total = rdr.GetInt64(5);
            var anyResidual = nsUser + nsEq + mnUser + aoUser > 0;
            var marker = anyResidual ? "!! " : "   ";
            Console.WriteLine(
                $"{marker}{schema,-20} total={total,5}  ns~User/%={nsUser,3}  ns=User={nsEq,3}  main~User/%={mnUser,3}  AA(ao~User/%)={aoUser,3}  AA-total={aaTotal,3}");
        }
    }

    // 3. List every AccessAssignment across all schemas (in the `access` satellite table).
    Console.WriteLine();
    Console.WriteLine("=== All AccessAssignment rows in access satellite tables ===");
    foreach (var schema in schemas)
    {
        var qSchema = schema.Replace("\"", "\"\"");
        // Only schemas that actually have an `access` table.
        bool hasAccess;
        await using (var probe = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM information_schema.tables
                          WHERE table_schema = $1 AND table_name = 'access')
            """, conn))
        {
            probe.Parameters.AddWithValue(schema);
            hasAccess = (bool)(await probe.ExecuteScalarAsync())!;
        }
        if (!hasAccess) continue;

        await using var cmd = new NpgsqlCommand($"""
            SELECT namespace, id, content->>'accessObject' AS access_object,
                   content->'roles'->0->>'role' AS first_role, main_node, last_modified
            FROM "{qSchema}".access
            ORDER BY namespace, id
            """, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var ns = rdr.GetString(0);
            var id = rdr.GetString(1);
            var ao = rdr.IsDBNull(2) ? "<null>" : rdr.GetString(2);
            var role = rdr.IsDBNull(3) ? "<null>" : rdr.GetString(3);
            var mn = rdr.IsDBNull(4) ? "<null>" : rdr.GetString(4);
            var lm = rdr.GetDateTime(5);
            var flag = ao.StartsWith("User/") ? "!!" : "  ";
            Console.WriteLine($"{flag} {schema,-12}  ns={ns,-32} id={id,-26} ao={ao,-22} role={role,-10} main={mn,-22} lm={lm:yyyy-MM-dd}");
        }
    }

    // 4. List every User-typed identity row.
    Console.WriteLine();
    Console.WriteLine("=== All User-typed nodes ===");
    foreach (var schema in schemas)
    {
        var qSchema = schema.Replace("\"", "\"\"");
        await using var cmd = new NpgsqlCommand($"""
            SELECT namespace, id, name, main_node
            FROM "{qSchema}".mesh_nodes
            WHERE node_type = 'User'
            ORDER BY namespace, id
            """, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var ns = rdr.GetString(0);
            var id = rdr.GetString(1);
            var name = rdr.IsDBNull(2) ? "<null>" : rdr.GetString(2);
            var mn = rdr.IsDBNull(3) ? "<null>" : rdr.GetString(3);
            var flag = (ns.StartsWith("User") || (mn?.StartsWith("User/") ?? false)) ? "!!" : "  ";
            Console.WriteLine($"{flag} {schema,-20}  ns={ns,-40} id={id,-30} main={mn,-40} name={name}");
        }
    }
}
