// Quick diagnostic: connect to prod Postgres via Azure AD token, run a series
// of checks to validate the per-user-schema migration succeeded.
//   1. db_version (admin.mesh_nodes id='db_version').
//   2. Per-user schemas exist (one per User node).
//   3. Each per-user schema has the expected tables (mesh_nodes, access, threads, activities).
//   4. Access table populated (at least the user's self-Admin assignment per V05).
//   5. Threads migrated to per-user schema.
//
// Run with:  dotnet script tools/check-prod-db.csx
// Requires:  az login (uses oss-rdbms token), Npgsql installed.

#nullable enable
#r "nuget: Npgsql, 9.0.2"
#r "nuget: Azure.Identity, 1.13.1"

using Azure.Core;
using Azure.Identity;
using Npgsql;

const string Host = "memexpostgres-d272wxvys4nvo.postgres.database.azure.com";
const string Db = "memex";
const string User = "rbuergi@systemorph.com";

var cred = new DefaultAzureCredential();
var token = await cred.GetTokenAsync(new TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }));
var conn = new NpgsqlConnection($"Host={Host};Database={Db};Username={User};Password={token.Token};SSL Mode=Require");
await conn.OpenAsync();
Console.WriteLine($"Connected to {Host}/{Db} as {User}");
Console.WriteLine();

async Task<List<Dictionary<string, object?>>> Q(string sql)
{
    var rows = new List<Dictionary<string, object?>>();
    await using var cmd = new NpgsqlCommand(sql, conn);
    await using var rdr = await cmd.ExecuteReaderAsync();
    while (await rdr.ReadAsync())
    {
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < rdr.FieldCount; i++)
            row[rdr.GetName(i)] = await rdr.IsDBNullAsync(i) ? null : rdr.GetValue(i);
        rows.Add(row);
    }
    return rows;
}

void Show(string title, List<Dictionary<string, object?>> rows)
{
    Console.WriteLine($"=== {title} ({rows.Count}) ===");
    foreach (var r in rows.Take(20))
        Console.WriteLine("  " + string.Join("  ", r.Select(kv => $"{kv.Key}={kv.Value}")));
    if (rows.Count > 20) Console.WriteLine($"  ... and {rows.Count - 20} more");
    Console.WriteLine();
}

// 1. db_version
Show("DB version (admin.mesh_nodes id='db_version')",
    await Q("SELECT id, content::text AS content FROM admin.mesh_nodes WHERE id='db_version'"));

// 2. All schemas with mesh_nodes table
Show("Per-user schemas (have mesh_nodes table, excl public/admin/info_schema/etc)",
    await Q(@"
        SELECT schema_name
        FROM information_schema.schemata s
        WHERE EXISTS (SELECT 1 FROM information_schema.tables t
                      WHERE t.table_schema = s.schema_name AND t.table_name='mesh_nodes')
          AND s.schema_name NOT IN ('public','admin','information_schema','pg_catalog','pg_toast','user')
          AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
        ORDER BY schema_name"));

// 3. User nodes (one per user — should each have their own schema after V10)
Show("User nodes",
    await Q("SELECT id, name FROM \"user\".mesh_nodes WHERE node_type='User' ORDER BY id"));

// 4. Access assignments per schema (sample)
Show("Access assignments per partition (sampled)",
    await Q(@"
        WITH all_schemas AS (
            SELECT schema_name FROM information_schema.schemata s
            WHERE EXISTS (SELECT 1 FROM information_schema.tables t
                          WHERE t.table_schema = s.schema_name AND t.table_name='access')
              AND s.schema_name NOT IN ('public','admin','information_schema','pg_catalog','pg_toast')
              AND s.schema_name NOT LIKE '%\_versions' ESCAPE '\'
        )
        SELECT
            schema_name,
            (xpath('/row/c/text()', query_to_xml(format('SELECT count(*)::text AS c FROM %I.access', schema_name), false, true, '')))[1]::text AS access_rows
        FROM all_schemas
        ORDER BY schema_name"));

// 5. Thread migration check: where threads live
Show("Thread tables: row counts per schema",
    await Q(@"
        WITH all_schemas AS (
            SELECT schema_name FROM information_schema.schemata s
            WHERE EXISTS (SELECT 1 FROM information_schema.tables t
                          WHERE t.table_schema = s.schema_name AND t.table_name='threads')
              AND s.schema_name NOT IN ('information_schema','pg_catalog','pg_toast')
        )
        SELECT
            schema_name,
            (xpath('/row/c/text()', query_to_xml(format('SELECT count(*)::text AS c FROM %I.threads', schema_name), false, true, '')))[1]::text AS thread_rows
        FROM all_schemas
        ORDER BY schema_name"));

// 6. Specific check for rbuergi
// Diagnostic: figure out why per-user schemas don't exist
Show("Everything in admin schema (looking for db_version)",
    await Q("SELECT id, node_type, namespace FROM admin.mesh_nodes ORDER BY id LIMIT 30"));

Show("All schemas (sanity)",
    await Q(@"
        SELECT schema_name FROM information_schema.schemata
        WHERE schema_name NOT IN ('pg_catalog','pg_toast','information_schema','public')
        ORDER BY schema_name"));

Show("rbuergi-owned nodes (current location: user.mesh_nodes? or somewhere else?)",
    await Q(@"
        SELECT id, namespace, node_type, name
        FROM ""user"".mesh_nodes
        WHERE id IN ('loss-model','McpSmokeTest') OR namespace LIKE 'rbuergi%'
        LIMIT 20"));

Show("Where do loss-model + McpSmokeTest actually live (cross-schema search)",
    await Q(@"
        SELECT 'user' as schema, id, namespace, node_type, name FROM ""user"".mesh_nodes
        WHERE id IN ('loss-model','McpSmokeTest')
        UNION ALL
        SELECT 'partnerre' as schema, id, namespace, node_type, name FROM partnerre.mesh_nodes
        WHERE id IN ('loss-model','McpSmokeTest')
        UNION ALL
        SELECT 'systemorph' as schema, id, namespace, node_type, name FROM systemorph.mesh_nodes
        WHERE id IN ('loss-model','McpSmokeTest')"));

Show("Existing AccessAssignments for accessObject=rbuergi (cross-schema)",
    await Q(@"
        SELECT 'user' as schema, id, namespace, content::text FROM ""user"".access
        WHERE content->>'accessObject'='rbuergi'
        UNION ALL
        SELECT 'partnerre' as schema, id, namespace, content::text FROM partnerre.access
        WHERE content->>'accessObject'='rbuergi'
        UNION ALL
        SELECT 'systemorph' as schema, id, namespace, content::text FROM systemorph.access
        WHERE content->>'accessObject'='rbuergi'"));
