using MeshWeaver.Blazor.Monaco;
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
    /// Creates a MeshNode definition for the Markdown node type.
    /// This provides HubConfiguration for nodes with nodeType="Markdown".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Markdown",
        Description = "A markdown node with collaborative editing support",
        Icon = "Document",
        HubConfiguration = config => config
            .AddMarkdownViews()
            .AddMonacoViews()
            .AddMeshDataSource()
            .AddContentCollections()
    };
}
