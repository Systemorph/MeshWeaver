using MeshWeaver.Data;
using MeshWeaver.Messaging;
using MeshWeaver.Graph.Security;
using MeshWeaver.Markdown.Collaboration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
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
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<INodeTypeAccessRule>(sp =>
                new SatelliteAccessRule(NodeType, sp.GetRequiredService<IMessageHub>()));
            return services;
        });
        // Register all comment and collaborative editing domain types
        builder.ConfigureHub(config => config
            .WithType<Mesh.Comment>(nameof(Mesh.Comment))
            .WithType<Mesh.CommentStatus>(nameof(Mesh.CommentStatus))
            .WithType<Mesh.TrackedChange>(nameof(Mesh.TrackedChange))
            .WithType<Mesh.TrackedChangeType>(nameof(Mesh.TrackedChangeType))
            .WithType<Mesh.TrackedChangeStatus>(nameof(Mesh.TrackedChangeStatus))
            .WithType<CreateCommentRequest>(nameof(CreateCommentRequest))
            .WithType<CreateCommentResponse>(nameof(CreateCommentResponse))
            .WithType<CreateSuggestedEditRequest>(nameof(CreateSuggestedEditRequest))
            .WithType<CreateSuggestedEditResponse>(nameof(CreateSuggestedEditResponse)));
        // Resolve/Delete/Accept/Reject request records are deliberately NOT registered any more:
        // they never had a handler, so posting one hung the caller to the timeout. Those operations
        // are node writes on the satellite / document (see CollaborativeMarkdownView and the
        // Collaboration plugin), not message verbs.
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
        HubConfiguration = config => config
            .AddCommentNodeViews()
            .AddMeshDataSource(source => source.WithContentType<Comment>())
    };
}
