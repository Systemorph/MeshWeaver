using Microsoft.Extensions.Logging;

namespace Memex.Database.Migration.Migrations;

/// <summary>
/// Robust re-run of <see cref="V36_MoveAgentsToPerPartitionAgentNamespace"/>: move each partition's own
/// agents into a dedicated <c>{partition}/Agent</c> sub-namespace, but discover partitions from the
/// actual Postgres <b>schemas</b> (every schema that owns a <c>mesh_nodes</c> table) instead of from
/// <c>admin.mesh_nodes</c> MeshDataSource records — V36 found none for user-created Spaces (e.g. atioz's
/// <c>AgenticPension</c>), so it moved 0 agents.
///
/// <para>The new namespace is derived <b>per row</b> from the agent's own namespace prefix
/// (<c>split_part(namespace,'/',1) || '/Agent'</c>), so it is correct regardless of how the partition
/// was registered and regardless of case. Matches the per-partition agent registry
/// (<c>AgentPickerProjection.BuildAgentQuery</c> → <c>namespace:{user}/Agent|{space}/Agent|Agent nodeType:Agent</c>).</para>
///
/// <para><b>Skipped:</b> the platform <c>Agent</c> namespace itself (an agent at namespace <c>Agent</c> is
/// a platform default and must NOT become <c>Agent/Agent</c>), and rows already at/under <c>{x}/Agent</c>
/// — so the migration is idempotent. Models stay on <c>_Provider</c> (the <c>/Model</c> move ships with
/// the model-registry code).</para>
/// </summary>
public sealed class V37_MoveAgentsToAgentNamespaceBySchema : IMigration
{
    public int Version => 37;
    public string Description => "Move each partition's own agents into {partition}/Agent (schema-discovered, per-row)";

    // Schemas that never hold a space/user's own agents (framework/system + the platform catalogs).
    private static readonly HashSet<string> ExcludedSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "admin", "auth", "doc", "_provider", "agent", "model",
        "command", "harness", "apitoken", "system_access", "information_schema",
    };

    public async Task RunAsync(MigrationContext ctx)
    {
        var schemas = new List<string>();
        await using (var cmd = ctx.DataSource.CreateCommand("""
            SELECT table_schema FROM information_schema.tables
            WHERE table_name = 'mesh_nodes'
            ORDER BY table_schema
            """))
        await using (var rdr = await cmd.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
            {
                var s = rdr.GetString(0);
                if (s.StartsWith("pg_", StringComparison.OrdinalIgnoreCase)) continue;
                if (ExcludedSchemas.Contains(s)) continue;
                schemas.Add(s);
            }
        }

        ctx.Logger.LogInformation("Repair v37: inspecting {Count} partition schema(s) for agents to relocate into /Agent.",
            schemas.Count);

        var grandTotal = 0;
        foreach (var schema in schemas)
        {
            // namespace → '{firstSegment}/Agent'; main_node → '{firstSegment}/Agent/{id}' (agents are
            // main nodes). split_part(...) in the SET reads the OLD namespace value. Skip the platform
            // Agent namespace and rows already at/under {x}/Agent (idempotent).
            await using var cmd = ctx.DataSource.CreateCommand($"""
                UPDATE "{schema}".mesh_nodes SET
                    namespace = split_part(namespace, '/', 1) || '/Agent',
                    main_node = CASE WHEN main_node IS NULL THEN NULL
                                     ELSE split_part(namespace, '/', 1) || '/Agent/' || id END
                WHERE node_type = 'Agent'
                  AND namespace <> ''
                  AND split_part(namespace, '/', 1) <> 'Agent'
                  AND namespace NOT LIKE '%/Agent'
                  AND namespace NOT LIKE '%/Agent/%'
                """);
            var affected = await cmd.ExecuteNonQueryAsync();
            if (affected > 0)
            {
                ctx.Logger.LogInformation("Repair v37: \"{Schema}\" — moved {Count} agent(s) into /Agent.",
                    schema, affected);
                grandTotal += affected;
            }
        }

        ctx.Logger.LogInformation("Repair v37: relocated {Total} agent(s) into per-partition /Agent namespaces.", grandTotal);
    }
}
