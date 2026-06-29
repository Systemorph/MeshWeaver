using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Re-run <c>rebuild_user_effective_permissions</c> after the role-bitmask fix.
///
/// <c>PostgreSqlSchemaInitializer</c> used to emit Admin=127 / Editor=119 / Viewer=33 etc
/// as the role-bitmask fallback in the rebuild stored procedure. These miss <c>Api</c>
/// (bit 128) and <c>Export</c> (bit 256), so users with Admin role got Read/Create/
/// Update/Delete/Comment/Execute/Thread but NOT Api — which broke ApiToken creation
/// (the satellite-access rule checks <c>Permission.Api</c> on MainNode).
///
/// The schema initializer now emits the correct bitmasks (Admin=511 = Permission.All,
/// etc.) and the unnest also emits Api/Export. Re-run rebuild for every existing
/// partition to backfill the missing permission rows.
///
/// Note: there is no v12. v12 just called the function which still had the old
/// definition; v13 force-replaces the function via <c>InitializeMeshTablesAsync</c>
/// before invoking it.
/// </summary>
public sealed class V13_RebuildPermissionsForApiBitmask : IMigration
{
    public int Version => 13;
    public string Description => "Update rebuild function (Admin=511 / Api+Export bits) and re-rebuild";

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = await SchemaHelpers.DiscoverAccessSchemasAsync(ctx.DataSource);

        foreach (var schema in schemas)
        {
            // Re-run InitializeMeshTablesAsync per schema so the rebuild function is
            // re-created with the new bitmasks + Api/Export unnest.
            await using var schemaDs = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, schema);
            var schemaOpts = SchemaHelpers.BuildSchemaOptions(ctx.ConnectionString, schema, ctx.Options.VectorDimensions);

            try
            {
                await PostgreSqlSchemaInitializer.InitializeMeshTablesAsync(schemaDs, schemaOpts);
                ctx.Logger.LogInformation("Repair v13: \"{Schema}\" — function updated", schema);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Repair v13: \"{Schema}\" — InitializeMeshTablesAsync failed", schema);
                continue;
            }

            try
            {
                await using var rebuildCmd = ctx.DataSource.CreateCommand(
                    $"SELECT \"{schema}\".rebuild_user_effective_permissions()");
                await rebuildCmd.ExecuteNonQueryAsync();
                ctx.Logger.LogInformation("Repair v13: \"{Schema}\".rebuild_user_effective_permissions() OK", schema);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "Repair v13: rebuild failed for \"{Schema}\"", schema);
            }
        }
    }
}
