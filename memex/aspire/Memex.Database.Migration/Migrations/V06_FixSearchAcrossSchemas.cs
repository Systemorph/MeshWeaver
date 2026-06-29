using MeshWeaver.Hosting.PostgreSql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Fix <c>search_across_schemas</c> to enforce <c>partition_access</c>.
///
/// Bug: <c>public_read</c> node types bypassed partition_access entirely, leaking
/// cross-partition data in search (e.g., a meshweaver user could see PartnerRe content).
/// Fix: partition_access is now always required; <c>public_read</c> only skips node-level
/// permission checks within accessible partitions.
///
/// The stored proc is re-created by <c>InitializePartitionAccessTableAsync</c> (idempotent).
/// </summary>
public sealed class V06_FixSearchAcrossSchemas : IMigration
{
    public int Version => 6;
    public string Description => "Fix search_across_schemas access control";

    public async Task RunAsync(MigrationContext ctx)
    {
        await PostgreSqlSchemaInitializer.InitializePartitionAccessTableAsync(ctx.DataSource);
    }
}
