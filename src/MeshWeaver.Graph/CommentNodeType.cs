using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides the NodeType constant and configuration for Comment nodes.
/// Comments are stored as child MeshNodes under document nodes.
/// </summary>
public static class CommentNodeType
{
    /// <summary>
    /// The NodeType value used to identify comment nodes.
    /// </summary>
    public const string NodeType = "Comment";

    /// <summary>
    /// When true, only the comment author can edit the comment text.
    /// Other users can still view the comment but cannot switch to edit mode.
    /// </summary>
    public const bool AuthorEditOnly = true;

    /// <summary>
    /// Registers the built-in "Comment" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddCommentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Comment node type.
    /// This provides HubConfiguration for nodes with nodeType="Comment".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Comment",
        Icon = "/static/NodeTypeIcons/comment.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(CommentNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddCommentNodeViews()
            .AddMeshDataSource(source => source.WithContentType<Comment>())
    };
}
