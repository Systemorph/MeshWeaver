using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Backfills the <c>user</c> schema with a thin <c>User</c> registry entry for every
/// existing per-user partition.
///
/// <para>Two-tier User design:</para>
/// <list type="bullet">
///   <item><b>Per-user partition</b> (<c>{userId}.mesh_nodes</c>): the FULL User node
///         (path = <c>{userId}</c>, namespace = <c>""</c>, full bio/role/pinnedPaths/etc.).
///         Visiting <c>/{userId}</c> resolves here via first-segment partition routing.</item>
///   <item><b>User partition</b> (<c>user.mesh_nodes</c>): a THIN registry entry per user
///         (path = <c>User/{userId}</c>, namespace = <c>"User"</c>, content = {email, $type}).
///         <c>OnboardingMiddleware</c>'s <c>nodeType:User content.email:X</c> lookup is
///         pinned to the <c>User</c> partition by routing rule, so without these entries
///         every signed-in user gets bounced back to <c>/onboarding</c> on every request.</item>
/// </list>
///
/// <para>The post-V10 onboarding moved User nodes from <c>user</c> schema (namespace=User)
/// to per-user schemas (namespace=""), but the User-partition registry index was never
/// repopulated — so existing users couldn't be found by email. This migration walks every
/// per-user schema and inserts the thin entry. Idempotent: <c>ON CONFLICT DO NOTHING</c>
/// guards re-runs.</para>
///
/// <para>Going forward, <c>Onboarding.razor</c> creates BOTH entries on every new
/// onboarding (full in per-user partition + thin in User partition).</para>
/// </summary>
public sealed class V18_BackfillUserPartitionRegistry : IMigration
{
    public int Version => 18;
    public string Description => "Backfill user.mesh_nodes with thin User registry entries (path=User/{id}) for existing per-user partitions";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Discover candidate schemas: have a mesh_nodes table AND contain a User identity row
        // (namespace='', node_type='User'). Excludes the user/admin/public schemas explicitly,
        // and any *_versions sidecar.
        var candidates = new List<(string Schema, string UserId, string? Name, string? Icon, string Content)>();

        await using (var discoverCmd = ctx.DataSource.CreateCommand("""
            SELECT t.table_schema
            FROM information_schema.tables t
            WHERE t.table_name = 'mesh_nodes'
              AND t.table_schema NOT IN ('information_schema','pg_catalog','pg_toast','public','admin','user')
              AND t.table_schema NOT LIKE '%_versions'
            ORDER BY t.table_schema
            """))
        await using (var rdr = await discoverCmd.ExecuteReaderAsync())
        {
            var schemas = new List<string>();
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));

            await rdr.CloseAsync();

            foreach (var schema in schemas)
            {
                var quotedSchema = schema.Replace("\"", "\"\"");
                await using var idCmd = ctx.DataSource.CreateCommand($"""
                    SELECT id, name, icon, content::text
                    FROM "{quotedSchema}".mesh_nodes
                    WHERE namespace = '' AND node_type = 'User'
                    LIMIT 1
                    """);
                await using var idRdr = await idCmd.ExecuteReaderAsync();
                if (await idRdr.ReadAsync())
                {
                    var userId = idRdr.IsDBNull(0) ? null : idRdr.GetString(0);
                    if (string.IsNullOrEmpty(userId)) continue;
                    var name = idRdr.IsDBNull(1) ? null : idRdr.GetString(1);
                    var icon = idRdr.IsDBNull(2) ? null : idRdr.GetString(2);
                    var content = idRdr.IsDBNull(3) ? "{}" : idRdr.GetString(3);
                    candidates.Add((schema, userId, name, icon, content));
                }
            }
        }

        ctx.Logger.LogInformation(
            "Repair v18: found {Count} per-user schema(s) with a User identity: [{Schemas}]",
            candidates.Count, string.Join(", ", candidates.Select(c => c.Schema)));

        // Ensure the destination user.mesh_nodes table exists. SchemaInitialization is the
        // canonical creator; we only insert here. If the schema is missing entirely, log
        // and skip — that's a setup bug, not something this migration fixes.
        bool userMeshExists;
        await using (var chkCmd = ctx.DataSource.CreateCommand("""
            SELECT 1 FROM information_schema.tables
            WHERE table_schema = 'user' AND table_name = 'mesh_nodes'
            LIMIT 1
            """))
        {
            userMeshExists = await chkCmd.ExecuteScalarAsync() is not null;
        }
        if (!userMeshExists)
        {
            ctx.Logger.LogWarning("Repair v18: user.mesh_nodes does not exist — User partition not initialised, skipping backfill");
            return;
        }

        var inserted = 0;
        foreach (var (schema, userId, name, icon, sourceContent) in candidates)
        {
            // Build a minimal content payload: just $type + email so the
            // `nodeType:User content.email:X` lookup in OnboardingMiddleware lands
            // on this row. Everything else (bio, role, pinnedPaths, fullName) stays
            // exclusively on the per-user partition's full node.
            string thinContent;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(sourceContent);
                var email = doc.RootElement.TryGetProperty("email", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String
                    ? e.GetString() ?? ""
                    : "";
                var type = doc.RootElement.TryGetProperty("$type", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                    ? t.GetString() ?? "MeshWeaver.Mesh.Security.User"
                    : "MeshWeaver.Mesh.Security.User";
                thinContent = System.Text.Json.JsonSerializer.Serialize(new { email, type }, new System.Text.Json.JsonSerializerOptions())
                    .Replace("\"type\"", "\"$type\""); // anonymous-type field rename
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex,
                    "Repair v18: failed to parse content for '{Schema}'.{UserId} — skipping",
                    schema, userId);
                continue;
            }

            await using var insertCmd = ctx.DataSource.CreateCommand("""
                INSERT INTO "user".mesh_nodes
                    (namespace, id, name, icon, node_type, state, content, main_node, last_modified, version)
                VALUES ('User', $1, $2, $3, 'User', 2, $4::jsonb, $5, now(), 1)
                ON CONFLICT (namespace, id) DO NOTHING
                """);
            insertCmd.Parameters.AddWithValue(userId);
            insertCmd.Parameters.AddWithValue((object?)name ?? userId);
            insertCmd.Parameters.AddWithValue((object?)icon ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue(thinContent);
            insertCmd.Parameters.AddWithValue($"User/{userId}");
            var rows = await insertCmd.ExecuteNonQueryAsync();
            if (rows > 0)
            {
                ctx.Logger.LogInformation(
                    "Repair v18: 'user' partition — inserted thin User entry for '{UserId}' (source schema '{Schema}')",
                    userId, schema);
                inserted++;
            }
        }

        ctx.Logger.LogInformation("Repair v18: done — {Inserted} thin User entries inserted into 'user' partition", inserted);
    }
}
