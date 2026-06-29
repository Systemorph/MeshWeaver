using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Installs <c>public.ensure_partition_schema(partition_name text)</c> — the single
/// source of truth for per-partition provisioning. The proc idempotently creates a
/// partition's schema + <c>{partition}.mesh_nodes</c> + every satellite table from
/// <c>PartitionDefinition.StandardTableMappings</c> + the permission-rebuild functions
/// and notify/mirror/history triggers, byte-faithful to the C#
/// <c>PostgreSqlSchemaInitializer.GetVersionedPartitionDdl</c> /
/// <c>GetSatelliteTableScript</c> bodies it embeds.
///
/// <para><b>Why a migration too?</b> <c>SchemaInitialization.RunAsync</c> already calls
/// <c>PostgreSqlSchemaInitializer.InitializeAsync</c> on the public schema on every run,
/// which now also <c>CREATE OR REPLACE</c>s this proc — so fresh and existing DBs get it.
/// This migration is the explicit, versioned anchor for the proc (the documented place to
/// evolve it) and guarantees the function exists even on the data-repair path. The runtime
/// per-partition provisioner (<c>PostgreSqlPartitionStorageProvider.EnsureSchemaAsync</c>)
/// and the eager Space-create hook both <c>SELECT public.ensure_partition_schema(@partition)</c>.</para>
///
/// <para><b>Idempotent</b>: pure <c>CREATE OR REPLACE FUNCTION</c>. Safe to re-run; does
/// not touch any partition schema (the proc is only invoked lazily/eagerly when a partition
/// is actually provisioned).</para>
/// </summary>
public sealed class V30_EnsurePartitionSchemaStoredProc : IMigration
{
    public int Version => 30;
    public string Description =>
        "Install public.ensure_partition_schema(text) — single source of truth for per-partition DDL";

    public async Task RunAsync(MigrationContext ctx)
    {
        var procDdl = PostgreSqlSchemaInitializer.GetEnsurePartitionSchemaProcScript(
            ctx.Options.VectorDimensions);

        await using var cmd = ctx.DataSource.CreateCommand(procDdl);
        await cmd.ExecuteNonQueryAsync();

        ctx.Logger.LogInformation(
            "Repair v30: installed public.ensure_partition_schema(text) stored proc");
    }
}
