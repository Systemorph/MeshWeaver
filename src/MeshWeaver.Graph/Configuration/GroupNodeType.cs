using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Group nodes in the graph.
/// Group nodes represent collections of users or nested groups.
/// Instances can be created anywhere in the node hierarchy.
/// </summary>
public static class GroupNodeType
{
    /// <summary>
    /// The NodeType value used to identify group nodes.
    /// </summary>
    public const string NodeType = "Group";

    /// <summary>
    /// Registers the built-in "Group" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddGroupType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Group node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Group",
        Icon = "/static/NodeTypeIcons/people.svg",
        AssemblyLocation = typeof(GroupNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddGroupViews()
            .AddMeshDataSource(source => source
                .WithContentType<AccessObject>())
    };
}
