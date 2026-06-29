using MeshWeaver.Hosting.PostgreSql;
using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Gives existing users the same Documentation shortcuts that new users now receive at onboarding
/// (<c>UserOnboardingService</c> seeds the four doc sections into <c>User.PinnedPaths</c>).
///
/// <para>Non-destructive and idempotent: a User node is only updated when its <c>pinnedPaths</c> is
/// missing/empty, OR when it still carries the legacy single <c>["Doc"]</c> root pin (which an older
/// onboarding seeded and which does not render as a card). Users who have curated their own pins are
/// left untouched. Runs across every content-partition schema.</para>
/// </summary>
public sealed class V29_PinDocsForExistingUsers : IMigration
{
    public int Version => 29;
    public string Description => "Pin documentation sections for existing users (empty or legacy [\"Doc\"] pins)";

    // The four section landing pages — each renders as a card on the Pinned tab.
    private const string DocPins = "[\"Doc/Architecture\", \"Doc/DataMesh\", \"Doc/GUI\", \"Doc/AI\"]";

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = await SchemaHelpers.DiscoverPartitionSchemasAsync(ctx.DataSource);
        foreach (var schema in schemas)
        {
            try
            {
                await using var ds = SchemaHelpers.BuildSchemaDataSource(ctx.ConnectionString, schema);
                await using var cmd = ds.CreateCommand(
                    """
                    UPDATE mesh_nodes
                       SET content = jsonb_set(coalesce(content, '{}'::jsonb), '{pinnedPaths}', $1::jsonb, true),
                           last_modified = NOW(),
                           version = version + 1
                     WHERE node_type = 'User'
                       AND (content->'pinnedPaths' IS NULL
                            OR jsonb_typeof(content->'pinnedPaths') <> 'array'
                            OR jsonb_array_length(content->'pinnedPaths') = 0
                            OR content->'pinnedPaths' = '["Doc"]'::jsonb)
                    """);
                cmd.Parameters.AddWithValue(DocPins);
                var updated = await cmd.ExecuteNonQueryAsync();
                if (updated > 0)
                    ctx.Logger.LogInformation(
                        "[V29] Pinned documentation sections for {Count} user(s) in schema {Schema}", updated, schema);
            }
            catch (Exception ex)
            {
                ctx.Logger.LogWarning(ex, "[V29] Skipped schema {Schema} while pinning docs", schema);
            }
        }
    }
}
