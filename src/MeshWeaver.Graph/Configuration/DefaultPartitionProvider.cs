using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides the default partition nodes that are created during system initialization.
/// Admin: system administration data.
/// User: user profiles, settings, and all user-scoped satellite data.
/// Portal: portal session nodes (own partition, not a satellite).
/// Kernel: kernel session nodes (own partition, not a satellite).
/// Satellite nodes (Activity, UserActivity, Thread, etc.) are stored in the same schema
/// as their parent, routed to dedicated tables via TableMappings.
/// </summary>
internal class DefaultPartitionProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return CreatePartition("Admin", "admin", "System administration",
            tableMappings: null);

        yield return CreatePartition("User", "user", "User profiles, settings, and user-scoped data",
            tableMappings: PartitionDefinition.StandardTableMappings);

        yield return CreatePartition("Portal", "portal", "Portal sessions",
            tableMappings: null);

        yield return CreatePartition("Kernel", "kernel", "Kernel sessions",
            tableMappings: null);
    }

    private static MeshNode CreatePartition(
        string id, string schema, string description,
        Dictionary<string, string>? tableMappings) =>
        new(id, PartitionNodeType.Namespace)
        {
            NodeType = PartitionNodeType.NodeType,
            Name = id,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = id,
                DataSource = "default",
                Schema = schema,
                Table = "mesh_nodes",
                TableMappings = tableMappings,
                Description = description,
            }
        };
}
