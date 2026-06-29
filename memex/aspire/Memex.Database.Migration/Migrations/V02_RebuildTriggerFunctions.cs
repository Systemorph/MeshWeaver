using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Re-create trigger functions per partition and populate <c>partition_access</c>.
/// The schema initializer now includes partition_access sync in
/// <c>rebuild_user_effective_permissions()</c> with a hardcoded schema name. For existing
/// DBs: re-run schema init per schema to update the function, then rebuild permissions
/// (which populates partition_access).
/// </summary>
public sealed class V02_RebuildTriggerFunctions : IMigration
{
    public int Version => 2;
    public string Description => "Update trigger functions and populate partition_access";

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);

        foreach (var schema in schemas)
        {
            ctx.Logger.LogInformation("Repair v2: Updating trigger function for schema {Schema}...", schema);

            await using var schemaDs = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, schema);
            var schemaOpts = SchemaHelpers.BuildSchemaOptions(ctx.ConnectionString, schema, ctx.Options.VectorDimensions);

            var versionsSchema = schema + "_versions";
            var hasVersions = await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, versionsSchema);

            if (hasVersions)
            {
                // Use BuildSchemaDataSource for the versions schema too — it sets up SSL +
                // AAD password provider for Azure, which a raw NpgsqlDataSourceBuilder skips,
                // causing `28000: no pg_hba.conf entry … no encryption` against prod.
                await using var versionsDs = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, versionsSchema, useVector: false);
                await PostgreSqlSchemaInitializer.InitializeWithVersionsSchemaAsync(
                    ctx.DataSource, schemaDs, versionsDs, schemaOpts, versionsSchema);
            }
            else
            {
                await PostgreSqlSchemaInitializer.InitializeMeshTablesAsync(schemaDs, schemaOpts);
            }

            try
            {
                await using var rebuildCmd = ctx.DataSource.CreateCommand(
                    $"SELECT \"{schema}\".rebuild_user_effective_permissions()");
                await rebuildCmd.ExecuteNonQueryAsync();
                ctx.Logger.LogInformation("Repair v2: Schema {Schema} — rebuilt permissions + partition_access", schema);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Repair v2: Schema {Schema} — rebuild failed", schema);
            }
        }
    }
}
