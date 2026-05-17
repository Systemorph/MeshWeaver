using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Drops the <c>Admin/Partition/*</c> routing-registry rows from
/// <c>admin.mesh_nodes</c>. Routing no longer consults these — the
/// PostgreSQL partition provider does a lazy
/// <c>information_schema.schemata</c> lookup per first-segment with a
/// 5-minute TTL cache; schema existence is the source of truth for
/// "is this a partition?".
///
/// <para>The Admin/Partition rows were originally written so the routing
/// layer could pre-load a static partition list at startup. That model
/// breaks across silos (each silo needs the same pre-load to settle) and
/// adds a chicken-and-egg dependency on the catalog. Removing the rows
/// makes routing stateless: any schema reachable via
/// <c>information_schema.schemata</c> is a valid partition, period.</para>
///
/// <para><b>What stays</b>: <c>user.mesh_nodes</c> (login-by-email index)
/// and <c>apitoken.mesh_nodes</c> (token-auth index) remain as their own
/// partitions/schemas — they're routable through the same lazy-lookup
/// mechanism, no special-casing needed.</para>
///
/// <para><b>Idempotent</b>: <c>DELETE</c> on a non-existent row is a no-op.</para>
/// </summary>
public sealed class V22_ConsolidateGlobalCatalogsInAdmin : IMigration
{
    public int Version => 22;
    public string Description =>
        "Drop Admin/Partition routing-registry rows; routing now uses information_schema.schemata";

    public async Task RunAsync(MigrationContext ctx)
    {
        if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, "admin"))
        {
            ctx.Logger.LogInformation(
                "Repair v22: 'admin' schema missing — nothing to clean up");
            return;
        }

        await using var cmd = ctx.DataSource.CreateCommand("""
            DELETE FROM admin.mesh_nodes
            WHERE namespace = 'Admin/Partition'
            """);
        var rows = await cmd.ExecuteNonQueryAsync();
        ctx.Logger.LogInformation(
            "Repair v22: dropped {Rows} Admin/Partition row(s). Routing now consults " +
            "information_schema.schemata directly with a 5-min TTL cache.",
            rows);
    }
}
