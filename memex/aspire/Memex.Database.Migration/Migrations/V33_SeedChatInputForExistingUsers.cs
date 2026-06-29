using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Seeds the per-user <b>ThreadComposer</b> singleton at <c>{userId}/_Memex/ThreadComposer</c> for every
/// existing user partition, so the chat composer's read RESOLVES instead of emitting a routing
/// <c>NotFound</c> that the GUI re-issues on a loop (the 2026-06-08 ThreadComposer event-storm class).
///
/// <para>New users get this seeded at onboarding (<c>ThreadComposerSeedHandler</c>); this repair
/// backfills users created before that handler existed. The node is a <b>MAIN</b> node under the
/// hidden <c>_Memex</c> "dotfile" namespace — <c>_Memex</c> is NOT a registered satellite suffix,
/// so the write and the path-based read BOTH hit <c>mesh_nodes</c> (contrast the dead
/// <c>_ThreadTemplate</c>, which split write→<c>threads</c> from read→<c>mesh_nodes</c>).</para>
///
/// <para><b>Content is intentionally NULL.</b> The node only needs to EXIST so routing resolves;
/// the composer falls back to its configuration defaults (no draft, default harness) until the
/// user's first interaction writes the real draft/selection. Avoids hand-serializing a
/// <c>Thread</c> payload (with its polymorphic <c>$type</c>) in SQL.</para>
///
/// <para>Discovery mirrors V17: per-user schemas are those with a <c>mesh_nodes</c> table holding
/// the user-identity row (<c>namespace='' node_type='User'</c>). The <c>auth</c> mirror schema is
/// excluded explicitly (it carries MIRRORED User rows, not a real per-user partition). Idempotent
/// via an existence check + <c>ON CONFLICT (namespace, id) DO NOTHING</c>.</para>
/// </summary>
public sealed class V33_SeedThreadComposerForExistingUsers : IMigration
{
    public int Version => 33;
    public string Description => "Seed {user}/_Memex/ThreadComposer singleton for every existing user partition";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Candidate schemas: any with a mesh_nodes table, excluding infra/mirror schemas.
        var schemas = new List<string>();
        await using (var discoverCmd = ctx.DataSource.CreateCommand("""
            SELECT t.table_schema
            FROM information_schema.tables t
            WHERE t.table_name = 'mesh_nodes'
              AND t.table_schema NOT IN
                  ('information_schema','pg_catalog','pg_toast','public','admin','auth','doc')
              AND t.table_schema NOT LIKE '%\_versions'
            ORDER BY t.table_schema
            """))
        await using (var rdr = await discoverCmd.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
                schemas.Add(rdr.GetString(0));
        }

        // Keep only real per-user partitions: schemas whose mesh_nodes hold the
        // user-identity row (namespace='', node_type='User'). Capture the canonical
        // (original-case) userId from that row so the seeded path matches what the
        // app reads (never derive it from the lower-cased schema name).
        var confirmed = new List<(string Schema, string UserId)>();
        foreach (var schema in schemas)
        {
            var quotedSchema = schema.Replace("\"", "\"\"");
            string? userId = null;
            await using (var idCmd = ctx.DataSource.CreateCommand($"""
                SELECT id FROM "{quotedSchema}".mesh_nodes
                WHERE namespace = '' AND node_type = 'User'
                LIMIT 1
                """))
            await using (var idRdr = await idCmd.ExecuteReaderAsync())
            {
                if (await idRdr.ReadAsync())
                    userId = idRdr.IsDBNull(0) ? null : idRdr.GetString(0);
            }
            if (!string.IsNullOrEmpty(userId))
                confirmed.Add((schema, userId));
        }

        ctx.Logger.LogInformation(
            "Repair v33: found {Count} per-user schema(s): [{Schemas}]",
            confirmed.Count, string.Join(", ", confirmed.Select(t => t.Schema)));

        var inserted = 0;
        foreach (var (schema, userId) in confirmed)
        {
            var quotedSchema = schema.Replace("\"", "\"\"");
            var ns = $"{userId}/_Memex";
            const string id = "ThreadComposer";
            var mainNode = $"{userId}/_Memex/ThreadComposer";

            bool exists;
            await using (var chkCmd = ctx.DataSource.CreateCommand($"""
                SELECT 1 FROM "{quotedSchema}".mesh_nodes
                WHERE namespace = $1 AND id = $2
                LIMIT 1
                """))
            {
                chkCmd.Parameters.AddWithValue(ns);
                chkCmd.Parameters.AddWithValue(id);
                exists = await chkCmd.ExecuteScalarAsync() is not null;
            }
            if (exists)
            {
                ctx.Logger.LogDebug("Repair v33: '{Schema}' — ThreadComposer already present, skipping", schema);
                continue;
            }

            // state=2 (Active). content NULL. path is GENERATED ALWAYS, so it's omitted.
            await using (var insertCmd = ctx.DataSource.CreateCommand($"""
                INSERT INTO "{quotedSchema}".mesh_nodes
                    (namespace, id, name, node_type, state, content, main_node, last_modified, version)
                VALUES ($1, $2, 'Chat Input', 'ThreadComposer', 2, NULL, $3, now(), 1)
                ON CONFLICT (namespace, id) DO NOTHING
                """))
            {
                insertCmd.Parameters.AddWithValue(ns);
                insertCmd.Parameters.AddWithValue(id);
                insertCmd.Parameters.AddWithValue(mainNode);
                var rows = await insertCmd.ExecuteNonQueryAsync();
                if (rows > 0)
                {
                    ctx.Logger.LogInformation("Repair v33: '{Schema}' — seeded {Path}", schema, mainNode);
                    inserted++;
                }
            }
        }

        ctx.Logger.LogInformation("Repair v33: done — {Inserted} ThreadComposer node(s) seeded", inserted);
    }
}
