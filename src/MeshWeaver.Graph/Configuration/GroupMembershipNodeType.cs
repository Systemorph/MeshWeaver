using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for GroupMembership nodes in the graph.
/// GroupMembership nodes are children of Group nodes, linking members to groups.
/// </summary>
public static class GroupMembershipNodeType
{
    /// <summary>
    /// The NodeType value used to identify group membership nodes.
    /// </summary>
    public const string NodeType = "GroupMembership";

    /// <summary>
    /// Registers the built-in "GroupMembership" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddGroupMembershipType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the GroupMembership node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Group Membership",
        Icon = "PersonAdd",
        AssemblyLocation = typeof(GroupMembershipNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddGroupMembershipViews()
            .AddMeshDataSource(source => source
                .WithContentType<GroupMembership>())
    };
}
