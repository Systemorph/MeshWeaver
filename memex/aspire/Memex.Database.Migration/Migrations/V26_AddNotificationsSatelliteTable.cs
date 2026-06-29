using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Adds the <c>notifications</c> satellite table to every existing partition
/// schema. Code-side this mapping was added to
/// <c>PartitionDefinition.StandardTableMappings</c> ("_Notification" →
/// "notifications") so new partitions get the table automatically via
/// <c>PostgreSqlPartitionStorageProvider.EnsureSchemaAsync</c>'s first-touch
/// init. But existing partition schemas only re-run that init when a fresh
/// pod processes the partition for the first time after deploy — until then,
/// any attempt to write a Notification would hit a missing-table error.
///
/// <para>This migration walks every content-partition schema (each has a
/// <c>mesh_nodes</c> table) and runs
/// <see cref="PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync"/> with
/// just <c>notifications</c> as the target. The function uses
/// <c>CREATE TABLE IF NOT EXISTS</c> + <c>DROP/CREATE TRIGGER</c>, so it's
/// idempotent and safe to re-run.</para>
///
/// <para>No data move — this is a structure add only. Notifications are a new
/// satellite type; nothing has been written to <c>mesh_nodes</c> for them
/// (the routing was correct from day one).</para>
/// </summary>
public sealed class V26_AddNotificationsSatelliteTable : IMigration
{
    public int Version => 26;
    public string Description => "Add `notifications` satellite table to every partition schema";

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);
        if (schemas.Count == 0)
        {
            ctx.Logger.LogInformation(
                "V26: no content-partition schemas found — skipping (fresh DB; first-touch init will create the table per-partition)");
            return;
        }

        var satelliteTables = new[] { "notifications" };

        var created = 0;
        foreach (var schema in schemas)
        {
            await using var schemaDs = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, schema);
            await PostgreSqlSchemaInitializer.CreateSatelliteTablesAsync(
                schemaDs, ctx.Options, satelliteTables);
            created++;
            ctx.Logger.LogInformation(
                "V26: ensured notifications table in schema '{Schema}'", schema);
        }

        ctx.Logger.LogInformation(
            "V26: notifications satellite table ensured across {Count} partition schema(s)", created);
    }
}
