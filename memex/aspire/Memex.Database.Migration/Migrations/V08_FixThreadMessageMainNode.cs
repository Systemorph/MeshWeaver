using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Fix <c>ThreadMessage.MainNode</c>.
///
/// Thread message nodes created from the UI may have <c>MainNode</c> set to the thread
/// path (e.g., <c>Org/_Thread/thread-id</c>) instead of the thread's content node
/// (e.g., <c>Org</c>). This causes "Access denied" because <c>SatelliteAccessRule</c>
/// delegates to MainNode. Fix: set <c>MainNode = part-before-/_Thread/</c> for all
/// ThreadMessage nodes.
/// </summary>
public sealed class V08_FixThreadMessageMainNode : IMigration
{
    public int Version => 8;
    public string Description => "Fix ThreadMessage MainNode";

    public async Task RunAsync(MigrationContext ctx)
    {
        var totalFixed = 0;
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);

        foreach (var schema in schemas)
        {
            // Skip the admin schema — it has mesh_nodes but isn't a content partition.
            if (string.Equals(schema, "admin", StringComparison.OrdinalIgnoreCase)) continue;

            await using var fixCmd = ctx.DataSource.CreateCommand($"""
                UPDATE "{schema}".mesh_nodes
                SET main_node = split_part(main_node, '/_Thread/', 1)
                WHERE node_type = 'ThreadMessage'
                  AND main_node LIKE '%/_Thread/%'
                """);
            var affected = await fixCmd.ExecuteNonQueryAsync();
            if (affected > 0)
            {
                ctx.Logger.LogInformation("Repair v8: Fixed {Count} ThreadMessage MainNode(s) in schema {Schema}", affected, schema);
                totalFixed += affected;
            }
        }

        ctx.Logger.LogInformation("Repair v8: fixed {Total} ThreadMessage MainNode(s)", totalFixed);
    }
}
