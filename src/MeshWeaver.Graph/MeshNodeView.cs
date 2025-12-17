using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for mesh node content.
/// - Details: Main content display with action menu (readonly content + navigation)
/// - Thumbnail: Compact card view for use in catalogs and lists
/// - Metadata: Node metadata display (name, type, description, path)
/// - Comments: Comments section (Facebook-style)
/// </summary>
public static class MeshNodeView
{
    public const string DetailsArea = "Details";
    public const string ThumbnailArea = "Thumbnail";
    public const string MetadataArea = "Metadata";
    public const string CommentsArea = "Comments";

    /// <summary>
    /// Adds the mesh node views (Details, Thumbnail, Metadata, Comments) to the hub's layout.
    /// Details is set as the default area for empty path requests.
    /// </summary>
    public static MessageHubConfiguration AddMeshNodeView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(DetailsArea)
            .WithView(DetailsArea, Details)
            .WithView(ThumbnailArea, Thumbnail)
            .WithView(MetadataArea, Metadata)
            .WithView(CommentsArea, Comments));

    /// <summary>
    /// Renders the Details area showing the node's main content with action menu.
    /// This is the default view for a node, showing content and providing navigation.
    /// </summary>
    public static IObservable<UiControl> Details(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();

        if (persistence == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html($"<h2>{hubPath}</h2>"))
                .WithView(Controls.Html("<p>Persistence service not available.</p>")));
        }

        // Load node and children data asynchronously
        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            IEnumerable<MeshNode> children = [];
            if (meshCatalog != null)
                children = await meshCatalog.Persistence.GetChildrenAsync(hubPath, ct);

            return BuildDetailsContent(host, node, children);
        });
    }

    private static UiControl BuildDetailsContent(LayoutAreaHost host, MeshNode? node, IEnumerable<MeshNode> children)
    {
        var nodePath = node?.Prefix ?? host.Hub.Address.ToString();
        var stack = Controls.Stack.WithWidth("100%");

        // Header: title on left, icon buttons on right
        var title = node?.Name ?? host.Hub.Address.ToString();
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; justify-content: space-between;")
            .WithView(Controls.Html($"<h1 style=\"margin: 0;\">{title}</h1>"))
            .WithView(BuildActionButtons(host, node));

        stack = stack.WithView(headerStack);

        // Main content based on node type
        var content = GetNodeContent(node);
        if (!string.IsNullOrWhiteSpace(content))
        {
            stack = stack.WithView(new MarkdownControl(content));
        }

        // Child node sections using a unified grid
        var childTypes = children
            .Where(c => !string.IsNullOrEmpty(c.NodeType))
            .GroupBy(c => c.NodeType!)
            .OrderBy(g => g.Key)
            .ToList();

        if (childTypes.Count > 0)
        {
            // Single unified grid for all child types
            var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2));

            foreach (var group in childTypes)
            {
                var recentNodes = group.Take(10).ToList();
                var displayName = GetNodeTypeDisplayName(group.Key, group.Count());

                // Section header spans full width
                grid = grid.WithView(
                    Controls.Html($"<h3 style=\"margin: 24px 0 8px 0;\">{displayName}</h3>"),
                    itemSkin => itemSkin.WithXs(12));

                // Thumbnails in grid: xs=12, sm=6, md=4, lg=3
                foreach (var child in recentNodes)
                {
                    grid = grid.WithView(
                        BuildThumbnailContent(child, child.Prefix),
                        itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(3));
                }

                // "Show more" button if there are more than 10
                if (group.Count() > 10)
                {
                    var showMoreHref = $"/{nodePath}/{MeshCatalogView.NodesArea}/{group.Key}";
                    grid = grid.WithView(
                        Controls.Button($"Show all {group.Count()}")
                            .WithAppearance(Appearance.Lightweight)
                            .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(showMoreHref))),
                        itemSkin => itemSkin.WithXs(12));
                }
            }

            stack = stack.WithView(grid);
        }

        // Comments section at the bottom
        stack = stack.WithView(BuildInlineCommentsSection(host, nodePath));

        return stack;
    }

    /// <summary>
    /// Builds icon-only action buttons for Edit and Metadata.
    /// </summary>
    private static UiControl BuildActionButtons(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Prefix ?? host.Hub.Address.ToString();
        var buttons = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;");

        // Edit button (icon only)
        if (node != null)
        {
            var editHref = $"/{node.Prefix}/{MeshCatalogView.EditorArea}";
            buttons = buttons.WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit(IconSize.Size16))
                    .WithAppearance(Appearance.Stealth)
                    .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(editHref))));
        }

        // Metadata button (icon only)
        var metadataHref = $"/{nodePath}/{MetadataArea}";
        buttons = buttons.WithView(
            Controls.Button("")
                .WithIconStart(FluentIcons.Info(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(metadataHref))));

        return buttons;
    }

    /// <summary>
    /// Renders a compact thumbnail/card view of a node for use in catalogs and lists.
    /// </summary>
    public static IObservable<UiControl> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (persistence == null)
        {
            return Observable.Return(Controls.Html($"<div>{hubPath}</div>"));
        }

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            return BuildThumbnailContent(node, hubPath);
        });
    }

    private static UiControl BuildThumbnailContent(MeshNode? node, string hubPath)
    {
        return MeshNodeThumbnailControl.FromNode(node, hubPath);
    }

    /// <summary>
    /// Builds an inline comments section for the bottom of the page (Facebook-style).
    /// </summary>
    private static UiControl BuildInlineCommentsSection(LayoutAreaHost host, string nodePath)
    {
        var section = Controls.Stack.WithWidth("100%").WithStyle("margin-top: 32px; border-top: 1px solid #e0e0e0; padding-top: 16px;");

        section = section.WithView(Controls.Html("<h3>Comments</h3>"));
        section = section.WithView(Controls.Html("<p style=\"color: #888; font-size: 0.9em; margin-bottom: 16px;\">Use the AI agent to add comments. Example: \"Add a comment saying 'This looks good'\"</p>"));

        // Embed the comments layout area (which now renders Facebook-style)
        section = section.WithView(Controls.LayoutArea(host.Hub.Address, CommentsArea));

        return section;
    }

    private static string GetNodeTypeDisplayName(string nodeType, int count)
    {
        // Capitalize first letter and add count
        var display = char.ToUpper(nodeType[0]) + nodeType.Substring(1);
        return $"{display}s ({count})";
    }

    /// <summary>
    /// Renders the Metadata area showing node properties (name, type, description, path).
    /// </summary>
    public static IObservable<UiControl> Metadata(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (persistence == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html("<h2>Metadata</h2>"))
                .WithView(Controls.Html("<p>Persistence service not available.</p>")));
        }

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            return BuildMetadataContent(host, node);
        });
    }

    private static UiControl BuildMetadataContent(LayoutAreaHost host, MeshNode? node)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header with back link
        var nodePath = node?.Prefix ?? host.Hub.Address.ToString();
        var backHref = $"/{nodePath}/{DetailsArea}";
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithView(Controls.Html("<h2>Metadata</h2>"))
            .WithView(Controls.Button("Back to Content")
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(backHref)))));

        if (node == null)
        {
            stack = stack.WithView(Controls.Html("<p><em>Node not found.</em></p>"));
            return stack;
        }

        // Display metadata fields
        stack = stack.WithView(Controls.Html($"<p><strong>Name:</strong> {node.Name}</p>"));
        stack = stack.WithView(Controls.Html($"<p><strong>Path:</strong> {node.Prefix}</p>"));

        if (!string.IsNullOrEmpty(node.NodeType))
        {
            stack = stack.WithView(Controls.Html($"<p><strong>Type:</strong> {node.NodeType}</p>"));
        }

        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            stack = stack.WithView(Controls.Html($"<p><strong>Description:</strong> {node.Description}</p>"));
        }

        if (!string.IsNullOrEmpty(node.ParentPath))
        {
            var parentHref = $"/{node.ParentPath}/{DetailsArea}";
            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithView(Controls.Html("<p><strong>Parent:</strong> </p>"))
                .WithView(Controls.Button(node.ParentPath)
                    .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(parentHref)))));
        }

        return stack;
    }

    private static string GetNodeContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        // Handle Article content
        if (node.Content is Article article)
            return article.Content ?? string.Empty;

        // Handle Story content using reflection to avoid circular dependency
        var nodeType = node.NodeType?.ToLowerInvariant();
        if (nodeType == "story")
        {
            // Try to get the Text property via reflection
            var textProperty = node.Content.GetType().GetProperty("Text");
            if (textProperty != null)
            {
                var textValue = textProperty.GetValue(node.Content) as string;
                if (!string.IsNullOrEmpty(textValue))
                    return textValue;
            }
        }

        // Check for NodeDescription
        if (node.Content is NodeDescription nd)
            return nd.Description;

        // Fall back to Description field
        return node.Description ?? string.Empty;
    }

    private const int CommentsPageSize = 10;

    /// <summary>
    /// Renders the Comments area showing comments for the node (Facebook-style).
    /// Shows 10 most recent with "Load more" functionality.
    /// </summary>
    public static IObservable<UiControl> Comments(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (persistence == null)
        {
            return Observable.Return(Controls.Html("<p style=\"color: #888;\">Comments not available.</p>"));
        }

        return Observable.FromAsync(async ct =>
        {
            var comments = await persistence.GetCommentsAsync(nodePath, ct);
            return BuildFacebookStyleComments(host, comments.ToList(), nodePath);
        });
    }

    private static UiControl BuildFacebookStyleComments(LayoutAreaHost host, List<Comment> comments, string nodePath)
    {
        var container = Controls.Stack.WithWidth("100%");

        if (comments.Count == 0)
        {
            container = container.WithView(
                Controls.Html("<p style=\"color: #888; font-style: italic;\">No comments yet.</p>"));
            return container;
        }

        // Show up to 10 most recent (reverse order - newest first)
        var recentComments = comments.OrderByDescending(c => c.CreatedAt).Take(CommentsPageSize).ToList();

        foreach (var comment in recentComments)
        {
            container = container.WithView(BuildCommentCard(comment));
        }

        // "Load more" button if there are more than 10
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

        // Author and timestamp row
        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 8px; margin-bottom: 4px;");

        // Author avatar placeholder (circle with initial)
        var initial = !string.IsNullOrEmpty(comment.Author) ? comment.Author[0].ToString().ToUpper() : "?";
        headerRow = headerRow.WithView(
            Controls.Html($"<div style=\"width: 32px; height: 32px; border-radius: 50%; background: #0078d4; color: white; display: flex; align-items: center; justify-content: center; font-weight: bold; font-size: 14px;\">{initial}</div>"));

        // Author name and timestamp
        var authorInfo = Controls.Stack.WithStyle("gap: 2px;");
        authorInfo = authorInfo.WithView(
            Controls.Html($"<strong style=\"font-size: 0.95em;\">{comment.Author}</strong>"));
        authorInfo = authorInfo.WithView(
            Controls.Html($"<span style=\"font-size: 0.8em; color: #888;\">{FormatTimeAgo(comment.CreatedAt)}</span>"));

        headerRow = headerRow.WithView(authorInfo);
        card = card.WithView(headerRow);

        // Comment text
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

/// <summary>
/// View model for displaying comments in the DataGrid.
/// </summary>
public record CommentViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;

    public CommentViewModel() { }

    public CommentViewModel(Comment comment)
    {
        Id = comment.Id;
        Author = comment.Author;
        Text = comment.Text;
        CreatedAt = comment.CreatedAt.ToString("g");
    }
}
