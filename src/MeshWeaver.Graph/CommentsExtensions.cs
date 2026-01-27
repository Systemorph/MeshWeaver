using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Extension methods for adding comments support to a message hub.
/// Comments are stored per-node as individual JSON files in _Comment/ sub-partition.
/// Each comment file is named by its GUID ID.
/// </summary>
public static class CommentsExtensions
{
    /// <summary>
    /// The sub-partition name where comments are stored.
    /// </summary>
    public const string CommentPartition = "_Comment";

    /// <summary>
    /// Marker type used to detect if comments are enabled in a hub configuration.
    /// </summary>
    public record CommentsEnabled;

    /// <summary>
    /// Registers the Comment type in the type registry at the mesh level.
    /// This must be called during mesh configuration to enable polymorphic JSON deserialization.
    /// </summary>
    public static TBuilder WithCommentType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            var typeRegistry = services.BuildServiceProvider().GetService<ITypeRegistry>();
            typeRegistry?.WithType(typeof(Comment), nameof(Comment));
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Adds comments support to the message hub configuration.
    /// This registers the Comment type, adds it to the data source, and enables the Comments view area.
    /// Comments are stored as individual JSON files in _Comment/ sub-partition with GUID filenames.
    /// Call this after AddMeshDataSource() to enable comments for a node type.
    /// </summary>
    public static MessageHubConfiguration AddComments(this MessageHubConfiguration configuration)
    {
        return configuration
            .WithType<Comment>(nameof(Comment))  // Register Comment in type registry
            .Set(new CommentsEnabled())
            .AddData(data => data.WithDataSource(_ =>
                new MeshDataSource(Guid.NewGuid().AsString(), data.Workspace)
                    .WithType<Comment>(CommentPartition, nameof(Comment))))
            .AddLayout(layout => layout
                .WithView(MeshNodeLayoutAreas.CommentsArea, CommentsView.Comments));
    }

    /// <summary>
    /// Checks if comments are enabled in the configuration.
    /// </summary>
    public static bool HasComments(this MessageHubConfiguration configuration)
        => configuration.Get<CommentsEnabled>() != null;
}

/// <summary>
/// Reusable view implementations for comments.
/// These can be used directly in layouts or embedded in other views.
/// </summary>
public static class CommentsView
{
    private const int CommentsPageSize = 10;

    /// <summary>
    /// Renders the Comments area showing comments for the node (Facebook-style).
    /// Shows 10 most recent with "Load more" functionality.
    /// Uses GetStream for reactive data binding when Comment is registered as mapped type.
    /// </summary>
    public static IObservable<UiControl?> Comments(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();

        return host.StreamView<Comment>(
            (comments, _) => BuildFacebookStyleComments(host, comments.ToList(), nodePath),
            "Comments");
    }

    /// <summary>
    /// Builds an inline comments section for embedding in other views.
    /// Shows a header, instructions, and embeds the Comments layout area.
    /// </summary>
    public static UiControl BuildInlineCommentsSection(LayoutAreaHost host)
    {
        var section = Controls.Stack.WithWidth("100%").WithStyle("margin-top: 32px; border-top: 1px solid #e0e0e0; padding-top: 16px;");

        section = section.WithView(Controls.Html("<h3>Comments</h3>"));
        section = section.WithView(Controls.Html("<p style=\"color: #888; font-size: 0.9em; margin-bottom: 16px;\">Use the AI agent to add comments. Example: \"Add a comment saying 'This looks good'\"</p>"));

        section = section.WithView(Controls.LayoutArea(host.Hub.Address, MeshNodeLayoutAreas.CommentsArea));

        return section;
    }

    private static UiControl BuildFacebookStyleComments(LayoutAreaHost _, List<Comment> comments, string __)
    {
        var container = Controls.Stack.WithWidth("100%");

        if (comments.Count == 0)
        {
            container = container.WithView(
                Controls.Html("<p style=\"color: #888; font-style: italic;\">No comments yet.</p>"));
            return container;
        }

        var recentComments = comments.OrderByDescending(c => c.CreatedAt).Take(CommentsPageSize).ToList();

        foreach (var comment in recentComments)
        {
            container = container.WithView(BuildCommentCard(comment));
        }

        if (comments.Count > CommentsPageSize)
        {
            var remainingCount = comments.Count - CommentsPageSize;
            container = container.WithView(
                Controls.Button($"View {remainingCount} more comment{(remainingCount > 1 ? "s" : "")}")
                    .WithAppearance(Appearance.Lightweight)
                    .WithStyle("margin-top: 8px;"));
        }

        return container;
    }

    private static UiControl BuildCommentCard(Comment comment)
    {
        var card = Controls.Stack
            .WithStyle("padding: 12px; margin: 8px 0; background: #f8f9fa; border-radius: 12px;");

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 8px; margin-bottom: 4px;");

        var initial = !string.IsNullOrEmpty(comment.Author) ? comment.Author[0].ToString().ToUpper() : "?";
        headerRow = headerRow.WithView(
            Controls.Html($"<div style=\"width: 32px; height: 32px; border-radius: 50%; background: #0078d4; color: white; display: flex; align-items: center; justify-content: center; font-weight: bold; font-size: 14px;\">{initial}</div>"));

        var authorInfo = Controls.Stack.WithStyle("gap: 2px;");
        authorInfo = authorInfo.WithView(
            Controls.Html($"<strong style=\"font-size: 0.95em;\">{comment.Author}</strong>"));
        authorInfo = authorInfo.WithView(
            Controls.Html($"<span style=\"font-size: 0.8em; color: #888;\">{FormatTimeAgo(comment.CreatedAt)}</span>"));

        headerRow = headerRow.WithView(authorInfo);
        card = card.WithView(headerRow);

        card = card.WithView(
            Controls.Html($"<p style=\"margin: 8px 0 0 40px; line-height: 1.4;\">{comment.Text}</p>"));

        return card;
    }

    private static string FormatTimeAgo(DateTimeOffset dateTime)
    {
        var timeSpan = DateTimeOffset.UtcNow - dateTime;

        if (timeSpan.TotalMinutes < 1)
            return "Just now";
        if (timeSpan.TotalMinutes < 60)
            return $"{(int)timeSpan.TotalMinutes}m ago";
        if (timeSpan.TotalHours < 24)
            return $"{(int)timeSpan.TotalHours}h ago";
        if (timeSpan.TotalDays < 7)
            return $"{(int)timeSpan.TotalDays}d ago";
        if (timeSpan.TotalDays < 30)
            return $"{(int)(timeSpan.TotalDays / 7)}w ago";

        return dateTime.ToString("MMM d, yyyy");
    }
}
