#nullable enable
// Audit (and optionally fix) every per-partition `access` table for legacy
// AccessAssignment shapes:
//   - content.accessObject like 'User/...' (should be the bare user id)
//   - content.roles[].role like 'Role/...' (should be the bare role name)
//
// Both classes are residuals of the V10..V15 migrations + a writer that the
// migrations missed. Idempotent — only matches the legacy shape.
//
// Usage:
//   dotnet script tools/audit-and-fix-access.csx -- audit  prod        # AAD auth, prod
//   dotnet script tools/audit-and-fix-access.csx -- fix    prod        # apply fix on prod
//   dotnet script tools/audit-and-fix-access.csx -- audit  local       # local Aspire pg
//   dotnet script tools/audit-and-fix-access.csx -- fix    local
//
// For local mode the script reads the Aspire-generated password by inspecting
// the docker container env of any running pgvector container.

#r "nuget: Npgsql, 9.0.2"
#r "nuget: Azure.Identity, 1.13.1"

using System.Diagnostics;
using Azure.Core;
using Azure.Identity;
using Npgsql;

var op = (Args.Count > 0 ? Args[0] : "audit").ToLowerInvariant();
var env = (Args.Count > 1 ? Args[1] : "prod").ToLowerInvariant();
if (op != "audit" && op != "fix") throw new ArgumentException("op must be 'audit' or 'fix'");
if (env != "prod" && env != "local" && env != "test") throw new ArgumentException("env must be prod|local|test");

var connStr = await BuildConnStrAsync(env);

await RunAsync();

async Task RunAsync()
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    var schemas = await DiscoverSchemasWithAccessAsync(conn);
    Console.WriteLine($"[{env}] schemas with `access` table: {string.Join(", ", schemas)}");

    int totalLegacyAo = 0, totalLegacyRole = 0;
    foreach (var schema in schemas)
    {
        var qSchema = schema.Replace("\"", "\"\"");
        await using var cmd = new NpgsqlCommand($"""
            SELECT namespace, id, content->>'accessObject' AS ao,
                   content->'roles'->0->>'role' AS first_role,
                   last_modified
            FROM "{qSchema}".access
            WHERE content->>'accessObject' LIKE 'User/%'
               OR content->'roles'->0->>'role' LIKE 'Role/%'
            ORDER BY namespace, id
            """, conn);
        await using var rdr = await cmd.ExecuteReaderAsync();
        var rows = new List<(string ns, string id, string ao, string role)>();
        while (await rdr.ReadAsync())
        {
            rows.Add((rdr.GetString(0), rdr.GetString(1),
                      rdr.IsDBNull(2) ? "" : rdr.GetString(2),
                      rdr.IsDBNull(3) ? "" : rdr.GetString(3)));
        }
        foreach (var r in rows)
        {
            if (r.ao.StartsWith("User/")) totalLegacyAo++;
            if (r.role.StartsWith("Role/")) totalLegacyRole++;
            Console.WriteLine($"   {schema,-12}  ns={r.ns,-32} id={r.id,-26} ao={r.ao,-22} role={r.role}");
        }
    }
    Console.WriteLine();
    Console.WriteLine($"Legacy `User/...` accessObjects: {totalLegacyAo}");
    Console.WriteLine($"Legacy `Role/...` roles:        {totalLegacyRole}");

    if (op == "fix" && (totalLegacyAo + totalLegacyRole) > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Applying fix…");
        var totalFixed = 0;
        foreach (var schema in schemas)
        {
            var qSchema = schema.Replace("\"", "\"\"");
            // Rewrite both fields in one statement:
            //   accessObject: strip leading 'User/' if present
            //   roles[0].role: strip leading 'Role/' if present
            // Match WHERE so the update only touches legacy rows (idempotent).
            var sql = "UPDATE \"" + qSchema + "\".access SET " +
                "content = jsonb_set(" +
                    "jsonb_set(content, '{accessObject}', " +
                        "CASE WHEN content->>'accessObject' LIKE 'User/%' " +
                             "THEN to_jsonb(substring(content->>'accessObject' FROM 6)) " +
                             "ELSE content->'accessObject' END), " +
                    "'{roles,0,role}', " +
                    "CASE WHEN content->'roles'->0->>'role' LIKE 'Role/%' " +
                         "THEN to_jsonb(substring(content->'roles'->0->>'role' FROM 6)) " +
                         "ELSE content->'roles'->0->'role' END), " +
                "version = COALESCE(version, 0) + 1, " +
                "last_modified = now() " +
                "WHERE content->>'accessObject' LIKE 'User/%' " +
                   "OR content->'roles'->0->>'role' LIKE 'Role/%'";
            await using var cmd = new NpgsqlCommand(sql, conn);
            var n = await cmd.ExecuteNonQueryAsync();
            if (n > 0)
            {
                Console.WriteLine($"   {schema}: {n} row(s) rewritten");
                totalFixed += n;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"Total fixed: {totalFixed}");
    }
}

async Task<List<string>> DiscoverSchemasWithAccessAsync(NpgsqlConnection conn)
{
    var list = new List<string>();
    await using var cmd = new NpgsqlCommand("""
        SELECT t.table_schema FROM information_schema.tables t
        WHERE t.table_name = 'access'
          AND t.table_schema NOT IN ('information_schema', 'pg_catalog', 'pg_toast')
        ORDER BY t.table_schema
        """, conn);
    await using var rdr = await cmd.ExecuteReaderAsync();
    while (await rdr.ReadAsync()) list.Add(rdr.GetString(0));
    return list;
}

async Task<string> BuildConnStrAsync(string env)
{
    if (env == "local")
    {
        // Read host:port + password from the running Aspire pgvector container.
        var (port, pw) = ReadLocalDocker();
        return $"Host=127.0.0.1;Port={port};Database=memex;Username=postgres;Password={pw};SSL Mode=Disable";
    }

    var rg = env == "test" ? "test-memex" : "prod-memex";
    var dbName = env == "test" ? "memex-test" : "memex";
    var fqdn = await ShellAsync($"az postgres flexible-server list -g {rg} --query \"[0].fullyQualifiedDomainName\" -o tsv");
    var user = Environment.GetEnvironmentVariable("AZURE_USER_PRINCIPAL_NAME")
               ?? throw new InvalidOperationException("Set AZURE_USER_PRINCIPAL_NAME (your AAD UPN).");

    var cred = new DefaultAzureCredential();
    var token = await cred.GetTokenAsync(
        new TokenRequestContext(new[] { "https://ossrdbms-aad.database.windows.net/.default" }));
    return $"Host={fqdn.Trim()};Database={dbName};Username={user};Password={token.Token};SSL Mode=Require";
}

(int port, string password) ReadLocalDocker()
{
    var nameLine = ShellAsync("docker ps --format \"{{.Names}}\" --filter ancestor=pgvector/pgvector:pg17").GetAwaiter().GetResult().Trim();
    if (string.IsNullOrEmpty(nameLine))
        throw new InvalidOperationException("No running pgvector/pgvector:pg17 container found. Is Aspire running?");
    var name = nameLine.Split('\n')[0].Trim();
    var portMap = ShellAsync($"docker port {name} 5432").GetAwaiter().GetResult().Trim();
    // e.g. "0.0.0.0:53063" or "127.0.0.1:53063" or both lines
    var portStr = portMap.Split('\n').First(l => l.Contains(":")).Split(':').Last().Trim();
    var port = int.Parse(portStr);
    var env = ShellAsync($"docker inspect {name} --format \"{{{{range .Config.Env}}}}{{{{println .}}}}{{{{end}}}}\"").GetAwaiter().GetResult();
    var pwLine = env.Split('\n').First(l => l.StartsWith("POSTGRES_PASSWORD="));
    var pw = pwLine.Substring("POSTGRES_PASSWORD=".Length).TrimEnd('\r');
    return (port, pw);
}

async Task<string> ShellAsync(string cmd)
{
    var psi = new ProcessStartInfo
    {
        FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "bash",
        Arguments = OperatingSystem.IsWindows() ? $"/c {cmd}" : $"-c \"{cmd}\"",
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
    };
    var p = Process.Start(psi)!;
    var output = await p.StandardOutput.ReadToEndAsync();
    await p.WaitForExitAsync();
    return output;
}
