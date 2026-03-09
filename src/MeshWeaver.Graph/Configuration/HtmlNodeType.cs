using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for HTML node types in the graph.
/// HTML nodes store raw HTML/SVG content as a string and render it directly in the overview.
/// </summary>
public static class HtmlNodeType
{
    /// <summary>
    /// The NodeType value used to identify HTML nodes.
    /// </summary>
    public const string NodeType = "Html";

    /// <summary>
    /// Registers the built-in "Html" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddHtmlType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Html node type.
    /// This provides HubConfiguration for nodes with nodeType="Html".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Html",
        Icon = "/static/NodeTypeIcons/code.svg",
        AssemblyLocation = typeof(HtmlNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddHtmlViews()
            .AddMeshDataSource()
    };
}
