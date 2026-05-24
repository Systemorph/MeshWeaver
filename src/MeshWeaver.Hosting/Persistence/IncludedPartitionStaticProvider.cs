using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Surfaces every <see cref="PartitionInclusion"/> registered via
/// <c>IncludePartition</c> / <c>AddAcme</c> / <c>AddSystemorph</c> / etc.
/// as a discoverable <c>Partition</c> MeshNode under <c>Admin/Partition/</c>.
///
/// <para>Without this, the chat-completion orchestrator's
/// <c>ProducePartitionList</c> query (<c>namespace:Admin/Partition nodeType:Partition</c>)
/// only sees the framework's built-in partitions (Admin, User, Portal, Kernel)
/// — none of the sample-data partitions a host opts into via
/// <c>AddSystemorph()</c> etc. show up, so <c>@/Sys</c> autocomplete returns
/// nothing (repro:
/// <c>ChatCompletionOrchestratorTest.AtSlashWithFilter_FiltersPartitions</c>).</para>
///
/// <para>Each emitted node carries a <see cref="PartitionDefinition"/>
/// with <c>Namespace = Name</c>, <c>Schema = Name.ToLowerInvariant()</c>,
/// and the standard satellite <see cref="PartitionDefinition.TableMappings"/>
/// so the entry is shaped identically to the framework's own static
/// partitions and the Postgres provider can route writes correctly if it
/// ever provisions a schema for this namespace.</para>
/// </summary>
internal sealed class IncludedPartitionStaticProvider(
    IEnumerable<PartitionInclusion> inclusions) : IStaticNodeProvider
{
    private readonly IReadOnlyList<PartitionInclusion> _inclusions = inclusions.ToList();

    /// <summary>
    /// Namespaces already covered by <c>DefaultPartitionProvider</c> — skip them
    /// so we don't emit duplicate Partition nodes for the framework's own
    /// built-in partitions (Admin, Auth, Portal, Kernel) or the global-scope
    /// satellite namespaces (_Access, _Activity, _UserActivity, _Thread).
    /// <para>
    /// "Auth" was historically "User" before V27 renamed the central
    /// auth-lookup partition. Keep "User" in the set so any live deployment
    /// that still references the old name is filtered out alongside the new
    /// "Auth" name.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Admin", "Auth", "User", "Portal", "Kernel",
        "_Access", "_Activity", "_UserActivity", "_Thread",
    };

    public IEnumerable<MeshNode> GetStaticNodes()
    {
        foreach (var inc in _inclusions)
        {
            if (string.IsNullOrEmpty(inc.Name) || ReservedNames.Contains(inc.Name))
                continue;
            var schema = inc.Name.ToLowerInvariant();
            yield return new MeshNode(inc.Name, PartitionNodeType.Namespace)
            {
                NodeType = PartitionNodeType.NodeType,
                Name = inc.Name,
                State = MeshNodeState.Active,
                Content = new PartitionDefinition
                {
                    Namespace = inc.Name,
                    DataSource = "default",
                    Schema = schema,
                    Table = "mesh_nodes",
                    TableMappings = PartitionDefinition.StandardTableMappings,
                    Versioned = true,
                    Description = $"Sample data partition '{inc.Name}'",
                }
            };
        }
    }
}
