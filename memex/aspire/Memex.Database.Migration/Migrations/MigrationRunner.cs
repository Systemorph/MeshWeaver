using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Runs the registered <see cref="IMigration"/>s in version order, gating on the
/// db_version recorded in <c>admin.mesh_nodes</c>.
///
/// Fresh-DB rule: if <see cref="MigrationContext.IsFreshDb"/> is true, ALL data
/// repairs are skipped and the version is fast-forwarded to <see cref="LatestVersion"/>.
/// Schema initialization (which always runs) has already brought the new DB to the
/// latest schema; there is no legacy data to repair.
/// </summary>
public sealed class MigrationRunner
{
    private readonly IReadOnlyList<IMigration> _migrations;

    public MigrationRunner(IEnumerable<IMigration> migrations)
    {
        _migrations = migrations.OrderBy(m => m.Version).ToList();
    }

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    public async Task<int> RunAsync(MigrationContext ctx)
    {
        var currentVersion = await ReadCurrentVersionAsync(ctx.DataSource);
        ctx.Logger.LogInformation("Current DB version: {Version}", currentVersion);

        if (ctx.IsFreshDb)
        {
            ctx.Logger.LogInformation(
                "Fresh database detected — skipping all data repairs and fast-forwarding version {Current} → {Target}.",
                currentVersion, LatestVersion);
            currentVersion = LatestVersion;
        }
        else
        {
            foreach (var migration in _migrations)
            {
                if (migration.Version <= currentVersion) continue;

                ctx.Logger.LogInformation("Running repair v{Version}: {Description}",
                    migration.Version, migration.Description);
                await migration.RunAsync(ctx);
                currentVersion = migration.Version;
                ctx.Logger.LogInformation("Repair v{Version} completed.", migration.Version);
            }
        }

        await SaveVersionAsync(ctx.DataSource, currentVersion);
        return currentVersion;
    }

    private static async Task<int> ReadCurrentVersionAsync(NpgsqlDataSource dataSource)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand("""
                SELECT (content->>'Version')::int FROM admin.mesh_nodes
                WHERE id = 'db_version' AND namespace = '' LIMIT 1
                """);
            var result = await cmd.ExecuteScalarAsync();
            return result switch
            {
                int v => v,
                long l => (int)l,
                _ => 0
            };
        }
        catch
        {
            // Table may not exist yet — version = 0 (fresh DB)
            return 0;
        }
    }

    private static async Task SaveVersionAsync(NpgsqlDataSource dataSource, int version)
    {
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO admin.mesh_nodes (namespace, id, name, node_type, state, content, last_modified, main_node)
            VALUES ('', 'db_version', 'Database Version', 'Settings', 2,
                    jsonb_build_object('Version', @version, 'LastMigration', now()::text),
                    now(), 'db_version')
            ON CONFLICT (namespace, id) DO UPDATE SET
                content = jsonb_build_object('Version', @version, 'LastMigration', now()::text),
                last_modified = now()
            """);
        cmd.Parameters.AddWithValue("@version", version);
        await cmd.ExecuteNonQueryAsync();
    }
}
