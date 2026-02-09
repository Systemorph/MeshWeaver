using MeshWeaver.Data;
using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Graph-specific extensions for Comment node types.
/// Creates MeshNode definitions with HubConfiguration.
/// </summary>
public static class CommentNodeConfiguration
{
    /// <summary>
    /// Creates a MeshNode definition for the Comment node type.
    /// This provides HubConfiguration for nodes with nodeType="Comment".
    /// </summary>
    public static MeshNode CreateMeshNode() => new(CommentNodeType.NodeType)
    {
        Name = "Comment",
        Description = "A comment on a document node",
        Icon = "Comment",
        HubConfiguration = config => config
            .AddCommentNodeViews()
            .AddMeshDataSource(source => source.WithContentType<Comment>())
    };
}
