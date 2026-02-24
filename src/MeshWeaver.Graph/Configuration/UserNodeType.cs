using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for User nodes in the graph.
/// User nodes represent people with access to the system.
/// Instances can be created anywhere in the node hierarchy.
/// </summary>
public static class UserNodeType
{
    /// <summary>
    /// The NodeType value used to identify user nodes.
    /// </summary>
    public const string NodeType = "User";

    /// <summary>
    /// Registers the built-in "User" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddUserType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the User node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "User",
        Icon = "/static/NodeTypeIcons/person.svg",
        AssemblyLocation = typeof(UserNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AccessObject>())
            .AddDefaultLayoutAreas()
    };
}
