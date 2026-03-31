using MeshWeaver.Data;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using Mesh = MeshWeaver.Mesh;

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
        // Register all comment and collaborative editing domain types
        builder.ConfigureHub(config => config
            .WithType<Mesh.Comment>(nameof(Mesh.Comment))
            .WithType<Mesh.CommentStatus>(nameof(Mesh.CommentStatus))
            .WithType<Mesh.TrackedChange>(nameof(Mesh.TrackedChange))
            .WithType<Mesh.TrackedChangeType>(nameof(Mesh.TrackedChangeType))
            .WithType<Mesh.TrackedChangeStatus>(nameof(Mesh.TrackedChangeStatus))
            .WithType<CreateCommentRequest>(nameof(CreateCommentRequest))
            .WithType<CreateCommentResponse>(nameof(CreateCommentResponse))
            .WithType<ResolveCommentRequest>(nameof(ResolveCommentRequest))
            .WithType<ResolveCommentResponse>(nameof(ResolveCommentResponse))
            .WithType<DeleteCommentRequest>(nameof(DeleteCommentRequest))
            .WithType<DeleteCommentResponse>(nameof(DeleteCommentResponse))
            .WithType<CreateSuggestedEditRequest>(nameof(CreateSuggestedEditRequest))
            .WithType<CreateSuggestedEditResponse>(nameof(CreateSuggestedEditResponse))
            .WithType<AcceptChangeRequest>(nameof(AcceptChangeRequest))
            .WithType<AcceptChangeResponse>(nameof(AcceptChangeResponse))
            .WithType<RejectChangeRequest>(nameof(RejectChangeRequest))
            .WithType<RejectChangeResponse>(nameof(RejectChangeResponse)));
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
