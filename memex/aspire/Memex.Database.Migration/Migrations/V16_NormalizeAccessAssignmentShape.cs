using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Normalize legacy <c>AccessAssignment</c> content shapes that V14/V15 missed.
///
/// <para>Two stale fields kept appearing in <c>{partition}.access</c> rows on
/// production after the user-prefix migrations had already run:</para>
/// <list type="number">
///   <item><c>content.accessObject</c> like <c>"User/&lt;userid&gt;"</c> — should be
///         the bare user id (<c>"&lt;userid&gt;"</c>). The original V10 sweep
///         touched <c>mesh_nodes</c> but not the <c>access</c> satellite, and a
///         post-V10 grant path on prod kept emitting the legacy prefix.</item>
///   <item><c>content.roles[i].role</c> like <c>"Role/&lt;name&gt;"</c> — should
///         be the bare role name (<c>"Admin"</c>, <c>"Editor"</c>, …). Same root
///         cause: a writer was carrying the role node's full path instead of
///         the role enum value, and SecurityService treats the prefixed form
///         as a non-match.</item>
/// </list>
///
/// <para>Both classes leave the <em>holder</em> with zero effective permissions:
/// <c>SecurityService.GetUserRolesAsync</c> compares the granted
/// <c>accessObject</c> against the bare user id, so a single <c>User/</c>
/// prefix wins them nothing. The fix is purely textual — strip the prefix.</para>
///
/// <para>Idempotent. The <c>WHERE</c> guards ensure rows that already conform
/// to the convention are skipped, so re-running V16 on a clean DB is a no-op.
/// Bumps <c>version</c> + <c>last_modified</c> on touched rows so the
/// workspace stream picks up the change on next portal restart.</para>
/// </summary>
public sealed class V16_NormalizeAccessAssignmentShape : IMigration
{
    public int Version => 16;
    public string Description => "Strip legacy 'User/' + 'Role/' prefixes from AccessAssignment content";

    public async Task RunAsync(MigrationContext ctx)
    {
        // Discover every schema that has an `access` satellite. AccessAssignment
        // rows live in `{partition}.access`, NOT `{partition}.mesh_nodes` (the
        // path segment `_Access` routes them to the satellite per
        // AGENTS.md → "Satellite tables are routed by path segment, not nodeType").
        var schemas = new List<string>();
        await using (var cmd = ctx.DataSource.CreateCommand("""
            SELECT t.table_schema FROM information_schema.tables t
            WHERE t.table_name = 'access'
              AND t.table_schema NOT IN ('information_schema', 'pg_catalog', 'pg_toast')
            ORDER BY t.table_schema
            """))
        await using (var rdr = await cmd.ExecuteReaderAsync())
            while (await rdr.ReadAsync()) schemas.Add(rdr.GetString(0));

        var totalFixed = 0;
        foreach (var schema in schemas)
        {
            var qSchema = schema.Replace("\"", "\"\"");
            // Single statement rewrites BOTH fields. The CASE inside each
            // jsonb_set keeps the original value when the legacy prefix is
            // absent — required because jsonb_set always replaces the key.
            //
            // substring(... FROM 6) drops the leading 'User/' (5 chars) /
            // 'Role/' (5 chars) — both prefixes are exactly 5 characters.
            var sql =
                "UPDATE \"" + qSchema + "\".access SET " +
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

            await using var cmd = ctx.DataSource.CreateCommand(sql);
            var n = await cmd.ExecuteNonQueryAsync();
            if (n > 0)
            {
                ctx.Logger.LogInformation(
                    "Repair v16: \"{Schema}\".access — normalized {Count} legacy AccessAssignment row(s)",
                    schema, n);
                totalFixed += n;
            }
        }

        if (totalFixed > 0)
            ctx.Logger.LogInformation("Repair v16: total rows normalized: {Total}", totalFixed);
    }
}
