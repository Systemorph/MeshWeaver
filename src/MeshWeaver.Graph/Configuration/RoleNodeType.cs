using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Role nodes in the graph.
/// Role nodes define named permission sets (e.g., Admin, Editor, Viewer).
/// Instances can be created anywhere in the node hierarchy.
/// </summary>
public static class RoleNodeType
{
    /// <summary>
    /// The NodeType value used to identify role nodes.
    /// </summary>
    public const string NodeType = "Role";

    /// <summary>
    /// Registers the built-in "Role" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddRoleType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Role node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Role",
        Icon = "Shield",
        AssemblyLocation = typeof(RoleNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<Role>())
            .AddDefaultLayoutAreas()
    };
}
