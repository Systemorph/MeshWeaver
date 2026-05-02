using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Rewrite <c>apitoken.mesh_nodes.tokenPath</c> to drop the <c>User/</c> prefix.
///
/// V10 moved per-user content (including <c>ApiToken/*</c> nodes) out of the shared
/// <c>user</c> schema into per-user schemas, dropping the <c>User/&lt;id&gt;</c> namespace
/// prefix in flight. However the dedicated <c>apitoken</c> partition still holds
/// <c>ApiTokenIndex</c> rows whose <c>content.tokenPath</c> references the old
/// <c>User/&lt;userid&gt;/ApiToken/&lt;hash&gt;</c> path — validation chain breaks because the
/// indirect lookup target no longer exists at that path. Rewrite to
/// <c>&lt;userid&gt;/ApiToken/&lt;hash&gt;</c> so post-v10 token validation resolves through
/// the per-user partition.
/// </summary>
public sealed class V11_RewriteApiTokenPaths : IMigration
{
    public int Version => 11;
    public string Description => "Rewrite apitoken tokenPath to drop User/ prefix";

    public async Task RunAsync(MigrationContext ctx)
    {
        var apitokenSchemaExists = await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "apitoken");
        if (!apitokenSchemaExists)
        {
            ctx.Logger.LogInformation("Repair v11: no \"apitoken\" schema present — skipping.");
            return;
        }

        await using var fixCmd = ctx.DataSource.CreateCommand("""
            UPDATE apitoken.mesh_nodes
            SET content = jsonb_set(
                content,
                '{tokenPath}',
                to_jsonb(regexp_replace(content->>'tokenPath', '^User/([^/]+)/', '\1/'))
            ),
            last_modified = NOW(),
            version = version + 1
            WHERE node_type = 'ApiToken'
              AND content->>'tokenPath' LIKE 'User/%'
            """);
        var updated = await fixCmd.ExecuteNonQueryAsync();
        ctx.Logger.LogInformation("Repair v11: rewrote {Count} apitoken.mesh_nodes tokenPath(s)", updated);
    }
}
