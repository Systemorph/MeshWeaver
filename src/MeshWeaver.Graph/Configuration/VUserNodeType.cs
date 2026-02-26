using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for VUser (virtual/anonymous user) nodes in the graph.
/// VUser nodes represent unauthenticated visitors identified by a browser cookie.
/// They are kept separate from real User nodes so they never appear in user pickers,
/// login screens, or access assignment dialogs.
/// </summary>
public static class VUserNodeType
{
    /// <summary>
    /// The NodeType value used to identify virtual user nodes.
    /// </summary>
    public const string NodeType = "VUser";

    /// <summary>
    /// Registers the built-in "VUser" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddVUserType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the VUser node type.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Virtual User",
        Icon = "/static/NodeTypeIcons/person.svg",
        NodeType = NodeType,
        AssemblyLocation = typeof(VUserNodeType).Assembly.Location,
        Content = new NodeTypeDefinition { DefaultNamespace = "VUser", RestrictedToNamespaces = ["VUser"] },
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<AccessObject>())
            .AddDefaultLayoutAreas()
    };
}
