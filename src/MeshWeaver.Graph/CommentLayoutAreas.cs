using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for Comment nodes.
/// Shows author, highlighted text quote, comment text, status, and child replies.
/// </summary>
public static class CommentLayoutAreas
{
    public const string OverviewArea = "Overview";

    /// <summary>
    /// Adds the comment node views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddCommentNodeViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail));

    /// <summary>
    /// Renders the Overview area for a Comment node.
    /// Shows author, highlighted text, comment text, status, and replies.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildOverview(node, hubPath);
        });
    }

    private static UiControl BuildOverview(MeshNode? node, string hubPath)
    {
        var comment = node?.Content as Comment;
        if (comment == null)
        {
            return Controls.Html("<div style=\"color: var(--neutral-foreground-hint); padding: 8px;\">No comment content</div>");
        }

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("max-width: 600px; padding: 16px;");

        // Author and status header
        var author = comment.Author;
        var authorInitial = !string.IsNullOrEmpty(author) ? author[0].ToString().ToUpper() : "?";
        var statusColor = comment.Status == CommentStatus.Resolved ? "#22c55e" : "#3b82f6";
        var statusLabel = comment.Status == CommentStatus.Resolved ? "Resolved" : "Active";

        container = container.WithView(Controls.Html($@"
            <div style=""display: flex; align-items: center; gap: 8px; margin-bottom: 12px;"">
                <div style=""width: 36px; height: 36px; border-radius: 50%; background: #3b82f6; color: white;
                            display: flex; align-items: center; justify-content: center; font-weight: 600; font-size: 16px;"">
                    {System.Web.HttpUtility.HtmlEncode(authorInitial)}
                </div>
                <div style=""flex: 1;"">
                    <div style=""font-weight: 600;"">{System.Web.HttpUtility.HtmlEncode(author)}</div>
                    <div style=""font-size: 0.8rem; color: var(--neutral-foreground-hint);"">{comment.CreatedAt:g}</div>
                </div>
                <span style=""padding: 2px 8px; border-radius: 4px; font-size: 0.8rem; background: {statusColor}20; color: {statusColor};"">{statusLabel}</span>
            </div>"));

        // Highlighted text quote
        if (!string.IsNullOrEmpty(comment.HighlightedText))
        {
            container = container.WithView(Controls.Html($@"
                <div style=""font-size: 0.9rem; color: var(--neutral-foreground-rest); padding: 10px 12px;
                            background: rgba(59, 130, 246, 0.1); border-radius: 4px; margin-bottom: 12px;
                            border-left: 3px solid #3b82f6; font-style: italic;"">
                    ""{System.Web.HttpUtility.HtmlEncode(comment.HighlightedText)}""
                </div>"));
        }

        // Comment text
        if (!string.IsNullOrEmpty(comment.Text))
        {
            container = container.WithView(Controls.Html($@"
                <div style=""font-size: 0.95rem; line-height: 1.5; margin-bottom: 16px;"">
                    {System.Web.HttpUtility.HtmlEncode(comment.Text)}
                </div>"));
        }

        // Replies
        if (comment.Replies.Count > 0)
        {
            container = container.WithView(Controls.Html("<div style=\"font-weight: 600; font-size: 0.9rem; margin-bottom: 8px;\">Replies</div>"));
            foreach (var reply in comment.Replies)
            {
                var replyInitial = !string.IsNullOrEmpty(reply.Author) ? reply.Author[0].ToString().ToUpper() : "?";
                container = container.WithView(Controls.Html($@"
                    <div style=""padding: 8px; margin: 4px 0; background: var(--neutral-layer-2);
                                border-radius: 4px; border-left: 3px solid var(--accent-fill-rest);"">
                        <div style=""font-weight: 600; font-size: 0.85rem; color: var(--accent-fill-rest); margin-bottom: 4px;"">{System.Web.HttpUtility.HtmlEncode(reply.Author)}</div>
                        <div style=""font-size: 0.9rem;"">{System.Web.HttpUtility.HtmlEncode(reply.Text)}</div>
                    </div>"));
            }
        }

        return container;
    }

    /// <summary>
    /// Renders a compact thumbnail for comment nodes.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return BuildThumbnail(node);
        });
    }

    private static UiControl BuildThumbnail(MeshNode? node)
    {
        var comment = node?.Content as Comment;
        var author = comment?.Author ?? "Unknown";
        var preview = comment?.Text ?? "";
        if (preview.Length > 50) preview = preview[..47] + "...";

        return Controls.Stack
            .WithStyle("padding: 8px 12px; background: var(--neutral-layer-card-container); border: 1px solid var(--neutral-stroke-rest); border-radius: 6px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px;")
                .WithView(Controls.Icon("Comment").WithStyle("font-size: 16px; color: #3b82f6;"))
                .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; font-weight: 500;\">{System.Web.HttpUtility.HtmlEncode(author)}</span>")))
            .WithView(Controls.Html($"<p style=\"margin: 4px 0 0 0; font-size: 0.85rem; color: var(--neutral-foreground-hint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{System.Web.HttpUtility.HtmlEncode(preview)}</p>"));
    }
}
