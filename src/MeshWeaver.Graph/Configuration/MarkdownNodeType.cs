using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides configuration for Markdown node types in the graph.
/// Markdown nodes are document nodes with collaborative editing support,
/// read/edit views, comments, attachments, and notebook visualization.
/// <para>
/// The MeshNode's <c>Content</c> is a <see cref="MarkdownContent"/> record
/// carrying the parsed markdown plus per-document metadata (Authors, Tags,
/// Thumbnail, Abstract) lifted off the YAML front-matter by
/// <c>MarkdownFileParser</c>. Registering <see cref="MarkdownContent"/> as
/// the data source's content type ensures it round-trips through the
/// per-node hub's type registry (without it, the JSON polymorphic resolver
/// downgrades the metadata to <c>JsonElement</c> and the rendering layer
/// fails to surface Authors / Tags).
/// </para>
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
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
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
        HubConfiguration = config => config
            .AddMarkdownViews()
            .AddMeshDataSource(s => s.WithContentType<MarkdownContent>())
            .AddContentCollections()
            .AddComments()
            .AddTracking()
            .AddApprovals()
    };
}
