using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Provides dedicated views for Comment nodes.
/// Shows author, highlighted text quote, comment text, status, and child replies.
/// </summary>
public static class CommentLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string EditArea = "Edit";

    /// <summary>
    /// Adds the comment node views to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddCommentNodeViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(EditArea, Edit)
                .WithView(MeshNodeLayoutAreas.ThumbnailArea, Thumbnail));

    /// <summary>
    /// Renders the Overview area for a Comment node.
    /// Shows author, highlighted text, comment text, status, and replies.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        // Query for reply MeshNodes (Comment children with scope:descendants)
        var replyNodesStream = meshQuery != null
            ? meshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{hubPath} nodeType:{CommentNodeType.NodeType} scope:descendants"))
                .Scan(new List<MeshNode>(), (list, change) =>
                {
                    if (change.ChangeType == QueryChangeType.Initial || change.ChangeType == QueryChangeType.Reset)
                        return change.Items.ToList();
                    foreach (var item in change.Items)
                    {
                        if (change.ChangeType == QueryChangeType.Added)
                            list.Add(item);
                        else if (change.ChangeType == QueryChangeType.Removed)
                            list.RemoveAll(n => n.Path == item.Path);
                        else if (change.ChangeType == QueryChangeType.Updated)
                        {
                            list.RemoveAll(n => n.Path == item.Path);
                            list.Add(item);
                        }
                    }
                    return list;
                })
                .Select(list => list as IReadOnlyList<MeshNode>)
            : Observable.Return<IReadOnlyList<MeshNode>>(Array.Empty<MeshNode>());

        return nodeStream
            .CombineLatest(replyNodesStream, (nodes, replyNodes) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildOverview(host, node, hubPath, replyNodes ?? Array.Empty<MeshNode>(), currentUser);
            });
    }

    /// <summary>
    /// Renders the Edit area for a Comment node.
    /// Uses BuildPropertyOverview for auto-generated MarkdownEditor on the Text field.
    /// Only the comment author can edit; others see a read-only message.
    /// </summary>
    public static IObservable<UiControl?> Edit(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name ?? "";

        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return<MeshNode[]>(Array.Empty<MeshNode>());

        return nodeStream.Select(nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            return (UiControl?)BuildEditContent(host, node, hubPath, currentUser);
        });
    }

    internal static UiControl BuildOverview(LayoutAreaHost host, MeshNode? node, string hubPath, IReadOnlyList<MeshNode> replyNodes, string currentUser)
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

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; margin-bottom: 12px;");

        headerRow = headerRow.WithView(Controls.Html($@"
            <div style=""display: flex; align-items: center; gap: 8px; flex: 1;"">
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

        // Action menu for the comment author
        if (!string.IsNullOrEmpty(currentUser) && string.Equals(comment.Author, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            headerRow = headerRow.WithView(BuildCommentActionMenu(host, hubPath));
        }

        container = container.WithView(headerRow);

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
            container = container.WithView(new MarkdownControl(comment.Text)
                .WithStyle("font-size: 0.95rem; line-height: 1.5; margin-bottom: 16px;"));
        }

        // Replies from MeshNode children — expandable catalog
        var matchingReplies = replyNodes
            .Where(n => n.Content is Comment c && c.ParentCommentId == comment.Id)
            .OrderBy(n => ((Comment)n.Content!).CreatedAt)
            .ToList();

        if (matchingReplies.Count > 0)
        {
            container = container.WithView(BuildRepliesCatalog(matchingReplies));
        }

        return container;
    }

    /// <summary>
    /// Builds the action menu (Edit / Delete) for a comment owned by the current user.
    /// </summary>
    private static UiControl BuildCommentActionMenu(LayoutAreaHost host, string hubPath)
    {
        var menu = Controls.MenuItem("", FluentIcons.MoreHorizontal(IconSize.Size20))
            .WithAppearance(Appearance.Stealth)
            .WithIconOnly();

        // Edit option
        var editHref = MeshNodeLayoutAreas.BuildContentUrl(hubPath, EditArea);
        menu = menu.WithView(new NavLinkControl("Edit", FluentIcons.Edit(IconSize.Size16), editHref));

        // Delete option
        menu = menu.WithView(
            Controls.MenuItem("Delete", FluentIcons.Delete(IconSize.Size16))
                .WithClickAction(async _ =>
                {
                    var meshCatalog = host.Hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
                    await meshCatalog.DeleteNodeAsync(hubPath, recursive: true);
                }));

        return menu;
    }

    /// <summary>
    /// Builds the Edit content for a Comment node.
    /// Uses BuildPropertyOverview for auto-generated MarkdownEditor on the Text field.
    /// </summary>
    private static UiControl BuildEditContent(LayoutAreaHost host, MeshNode? node, string hubPath, string currentUser)
    {
        var stack = Controls.Stack.WithWidth("100%").WithStyle("padding: 16px; max-width: 600px;");

        if (node == null)
        {
            return stack.WithView(Controls.Html("<p style=\"color: var(--warning-color);\">Comment not found.</p>"));
        }

        var comment = node.Content as Comment;

        // Author check — only the author can edit
        if (comment != null && !string.IsNullOrEmpty(currentUser)
            && !string.Equals(comment.Author, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            return stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">You can only edit your own comments.</p>"))
                .WithView(Controls.Button("Back")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(MeshNodeLayoutAreas.BuildContentUrl(hubPath, OverviewArea)));
        }

        // Header with Done button
        var doneHref = MeshNodeLayoutAreas.BuildContentUrl(hubPath, OverviewArea);

        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(16)
            .WithStyle("align-items: center; margin-bottom: 16px; justify-content: space-between;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center;")
                .WithView(Controls.Button("Done")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.ArrowLeft())
                    .WithNavigateToHref(doneHref))
                .WithView(Controls.H2("Edit Comment").WithStyle("margin: 0;"))));

        // Property editor (auto-generates MarkdownEditorControl for [Markdown] Text field)
        if (node.Content != null)
        {
            stack = stack.WithView(OverviewLayoutArea.BuildPropertyOverview(host, node));
        }

        return stack;
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

    /// <summary>
    /// Builds a collapsible "Replies (N)" catalog from reply MeshNodes.
    /// Each reply is rendered as a Thumbnail.
    /// </summary>
    internal static UiControl BuildRepliesCatalog(IReadOnlyList<MeshNode> replyNodes)
    {
        var items = replyNodes.Select(r => (UiControl)BuildThumbnail(r)).ToImmutableList();
        return new CatalogControl
            {
                CollapsibleSections = true,
                ShowCounts = true,
                Xs = 12, Sm = 12, Md = 12, Lg = 12,
                CardHeight = 60,
                Spacing = 1,
                SectionGap = 0,
            }
            .WithGroup(new CatalogGroup
            {
                Key = "replies",
                Label = "Replies",
                IsExpanded = false,
                TotalCount = replyNodes.Count,
                Items = items,
            });
    }

    internal static UiControl BuildThumbnail(MeshNode? node)
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
                .WithView(Controls.Icon(FluentIcons.Comment(IconSize.Size16)).WithStyle("color: #3b82f6;"))
                .WithView(Controls.Html($"<span style=\"font-size: 0.85rem; font-weight: 500;\">{System.Web.HttpUtility.HtmlEncode(author)}</span>")))
            .WithView(Controls.Html($"<p style=\"margin: 4px 0 0 0; font-size: 0.85rem; color: var(--neutral-foreground-hint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{System.Web.HttpUtility.HtmlEncode(preview)}</p>"));
    }
}
