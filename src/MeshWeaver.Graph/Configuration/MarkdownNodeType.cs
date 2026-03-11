using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Markdown node types in the graph.
/// Markdown nodes are document nodes with collaborative editing support,
/// read/edit views, comments, attachments, and notebook visualization.
/// </summary>
public static class MarkdownNodeType
{
    /// <summary>
    /// The NodeType value used to identify markdown documentation nodes.
    /// </summary>
    public const string NodeType = "Markdown";

    /// <summary>
    /// Registers the built-in "Markdown" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddMarkdownType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Markdown node type.
    /// This provides HubConfiguration for nodes with nodeType="Markdown".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Markdown",
        Icon = "/static/NodeTypeIcons/document.svg",
        AssemblyLocation = typeof(MarkdownNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddMarkdownViews()
            .AddMeshDataSource()
            .AddContentCollections()
            .AddComments()
            .AddTracking()
            .AddApprovals()
    };
}
