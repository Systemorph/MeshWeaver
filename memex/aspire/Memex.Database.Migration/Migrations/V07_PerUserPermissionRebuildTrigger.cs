using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Deploy the per-user permission-rebuild trigger.
///
/// The trigger function <c>trg_access_changed()</c> previously called
/// <c>rebuild_user_effective_permissions()</c> which rebuilt ALL users' permissions —
/// causing deadlocks under concurrent access. The new trigger calls
/// <c>rebuild_user_permissions_for(affected_user)</c> — only touches one user's rows.
///
/// The schema initializer already creates the new functions; we just need to re-run
/// schema init per partition to deploy the updated trigger function.
/// </summary>
public sealed class V07_PerUserPermissionRebuildTrigger : IMigration
{
    public int Version => 7;
    public string Description => "Deploy per-user permission rebuild trigger";

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = await SchemaHelpers.DiscoverAccessSchemasAsync(ctx.DataSource);

        foreach (var schema in schemas)
        {
            ctx.Logger.LogInformation("Repair v7: Updating trigger functions for schema {Schema}...", schema);

            await using var schemaDs = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, schema);
            var schemaOpts = SchemaHelpers.BuildSchemaOptions(ctx.ConnectionString, schema, ctx.Options.VectorDimensions);

            await PostgreSqlSchemaInitializer.InitializeMeshTablesAsync(schemaDs, schemaOpts);
            ctx.Logger.LogInformation("Repair v7: Schema {Schema} — trigger updated", schema);
        }
    }
}
