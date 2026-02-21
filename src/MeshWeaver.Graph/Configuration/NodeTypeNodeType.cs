using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for NodeType definition nodes in the graph.
/// These are meta-nodes that describe other node types.
/// </summary>
public static class NodeTypeNodeType
{
    /// <summary>
    /// The NodeType value used to identify node type definition nodes.
    /// </summary>
    public const string NodeType = MeshNode.NodeTypePath;

    /// <summary>
    /// Registers the built-in "NodeType" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddNodeTypeType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the NodeType node type.
    /// This provides HubConfiguration for nodes with nodeType="NodeType".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Node Type",
        Icon = "/static/NodeTypeIcons/code.svg",
        AssemblyLocation = typeof(NodeTypeNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMeshDataSource(source => source
                .WithContentType<NodeTypeDefinition>())
            .AddNodeTypeView()
    };
}
