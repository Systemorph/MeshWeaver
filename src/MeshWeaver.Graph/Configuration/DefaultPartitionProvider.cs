using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides the default partition nodes that are created during system initialization.
/// Admin: system administration data (PlatformAdmin only).
/// User: user profiles, settings, and all user-scoped satellite data (public read).
/// Portal: portal session nodes (public read, unversioned).
/// Kernel: kernel session nodes (public read, unversioned).
/// Also provides static AccessAssignment nodes granting Public Viewer on non-admin partitions.
/// </summary>
internal class DefaultPartitionProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return CreatePartition("Admin", "admin", "System administration",
            tableMappings: PartitionDefinition.StandardTableMappings);

        yield return CreatePartition("User", "user", "User profiles, settings, and user-scoped data",
            tableMappings: PartitionDefinition.StandardTableMappings);

        yield return CreatePartition("Portal", "portal", "Portal sessions",
            tableMappings: PartitionDefinition.StandardTableMappings, versioned: false);

        yield return CreatePartition("Kernel", "kernel", "Kernel sessions",
            tableMappings: PartitionDefinition.StandardTableMappings, versioned: false);

        // Grant Public (all authenticated users) Viewer role on non-admin partitions
        foreach (var ns in new[] { "User", "Portal", "Kernel" })
            yield return CreatePublicViewerAccess(ns);
    }

    private static MeshNode CreatePartition(
        string id, string schema, string description,
        Dictionary<string, string>? tableMappings,
        bool versioned = true) =>
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
                Versioned = versioned,
                Description = description,
            }
        };

    /// <summary>
    /// Creates a static AccessAssignment granting the Public user Viewer role
    /// on a namespace. This gives all authenticated users read access.
    /// </summary>
    private static MeshNode CreatePublicViewerAccess(string ns) =>
        new($"{WellKnownUsers.Public}_Access", ns)
        {
            NodeType = "AccessAssignment",
            Name = $"{WellKnownUsers.Public} Access",
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = "All authenticated users",
                Roles = [new RoleAssignment { Role = "Viewer" }]
            }
        };
}
