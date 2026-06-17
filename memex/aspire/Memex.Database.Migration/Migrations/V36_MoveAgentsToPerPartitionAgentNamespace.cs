using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Move each partition's OWN agents into a dedicated <c>{partition}/Agent</c> sub-namespace, to match
/// the per-partition agent registry (<c>AgentPickerProjection.BuildAgentQuery</c> →
/// <c>namespace:{user}/Agent|{space}/Agent|Agent nodeType:Agent</c> — exact membership, no graph search).
///
/// Before: a space/user dropped agents directly in its partition (e.g. atioz had
/// <c>AgenticPension/Datenextraktion</c>, namespace <c>AgenticPension</c>). The new registry only lists
/// <c>{partition}/Agent</c>, so those agents would no longer surface. This migration rewrites every
/// <c>nodeType=Agent</c> row in each partition schema to <c>namespace = '{partition}/Agent'</c> (and
/// fixes its <c>main_node</c> to the new path), so it appears in that space's <c>/agent</c> picker.
///
/// <para><b>Scope — agents only.</b> Models stay on the <c>_Provider</c> catalog for now (the model
/// picker still queries <c>_Provider</c> subtrees); the per-partition <c>/Model</c> move ships with the
/// model-registry code change.</para>
///
/// <para><b>Skipped:</b> the platform <c>Agent</c> partition itself (its agents ARE the platform
/// defaults at namespace <c>Agent</c> — moving them to <c>Agent/Agent</c> would hide them). Rows already
/// at <c>{partition}/Agent</c> (or nested under it) are left untouched, so the migration is idempotent.</para>
/// </summary>
public sealed class V36_MoveAgentsToPerPartitionAgentNamespace : IMigration
{
    public int Version => 36;
    public string Description => "Move each partition's own agents into {partition}/Agent (per-partition agent registry)";

    /// <summary>The dedicated sub-namespace segment for a partition's own agents — mirrors
    /// <c>AgentPickerProjection.AgentSubNamespace</c>.</summary>
    private const string AgentSub = "Agent";

    public async Task RunAsync(MigrationContext ctx)
    {
        var partitions = await DiscoverPartitionsAsync(ctx);
        ctx.Logger.LogInformation(
            "Repair v36: inspecting {Count} partition(s) for agents to relocate into /{Sub}.",
            partitions.Count, AgentSub);

        var grandTotal = 0;
        foreach (var (partitionId, schemaName) in partitions)
        {
            // Skip the platform Agent catalog partition — its agents are the platform defaults at
            // namespace 'Agent' and must NOT move to 'Agent/Agent'.
            if (string.Equals(partitionId, AgentSub, StringComparison.OrdinalIgnoreCase))
                continue;

            var targetNs = $"{partitionId}/{AgentSub}";
            var quotedTarget = targetNs.Replace("'", "''");

            // namespace → '{partition}/Agent'; main_node → '{partition}/Agent/{id}' (agents are main
            // nodes, so main_node was the old path). Skip rows already at or under the target, and any
            // legacy 'Agent'-namespaced row (defensive — shouldn't exist in a non-Agent partition).
            await using var cmd = ctx.DataSource.CreateCommand($"""
                UPDATE "{schemaName}".mesh_nodes SET
                    namespace = '{quotedTarget}',
                    main_node = CASE WHEN main_node IS NULL THEN NULL ELSE '{quotedTarget}/' || id END
                WHERE node_type = 'Agent'
                  AND namespace <> '{quotedTarget}'
                  AND namespace NOT LIKE '{quotedTarget}/%'
                """);
            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected > 0)
            {
                ctx.Logger.LogInformation(
                    "Repair v36: \"{Schema}\" — moved {Count} agent(s) into namespace '{Target}'.",
                    schemaName, affected, targetNs);
                grandTotal += affected;
            }
        }

        ctx.Logger.LogInformation("Repair v36: relocated {Total} agent(s) into per-partition /{Sub} namespaces.",
            grandTotal, AgentSub);
    }

    private static async Task<List<(string Partition, string Schema)>> DiscoverPartitionsAsync(MigrationContext ctx)
    {
        var partitions = new List<(string, string)>();
        // MeshDataSource records live at (ns='Source', id=<partitionId>) in admin.mesh_nodes.
        await using var cmd = ctx.DataSource.CreateCommand("""
            SELECT id FROM admin.mesh_nodes
            WHERE namespace = 'Source' AND node_type = 'MeshDataSource'
            ORDER BY id
            """);
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            var partitionId = rdr.GetString(0);
            var schemaName = SchemaHelpers.SanitizeSchemaName(partitionId);
            if (string.IsNullOrEmpty(schemaName)) continue;
            if (!await SchemaHelpers.SchemaExistsAsync(ctx.DataSource, schemaName)) continue;
            partitions.Add((partitionId, schemaName));
        }
        return partitions;
    }
}
