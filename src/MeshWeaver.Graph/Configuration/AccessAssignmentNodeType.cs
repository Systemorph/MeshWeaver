using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for AccessAssignment nodes in the graph.
/// AccessAssignment nodes grant or deny a role to a subject at a specific scope.
/// </summary>
public static class AccessAssignmentNodeType
{
    /// <summary>
    /// The NodeType value used to identify access assignment nodes.
    /// </summary>
    public const string NodeType = "AccessAssignment";

    /// <summary>
    /// Registers the built-in "AccessAssignment" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddAccessAssignmentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the AccessAssignment node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Access Assignment",
        Icon = "Shield",
        AssemblyLocation = typeof(AccessAssignmentNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddAccessAssignmentViews()
            .AddMeshDataSource(source => source
                .WithContentType<AccessAssignment>())
    };
}
