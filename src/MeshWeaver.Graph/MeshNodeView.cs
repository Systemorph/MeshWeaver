using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for mesh node content.
/// - Details: Main content display with action menu (readonly content + navigation)
/// - Thumbnail: Compact card view for use in catalogs and lists
/// - Metadata: Node metadata display (name, type, description, path)
/// - Settings: Node settings with NodeType link navigation
/// - Comments: Comments section (Facebook-style)
/// </summary>
public static class MeshNodeView
{
    public const string DetailsArea = "Details";
    public const string ThumbnailArea = "Thumbnail";
    public const string MetadataArea = "Metadata";
    public const string SettingsArea = "Settings";
    public const string CommentsArea = "Comments";

    /// <summary>
    /// Adds the mesh node views (Details, Thumbnail, Metadata, Settings, Comments) to the hub's layout.
    /// Requires AddMeshDataSource() to be called first to enable GetStream&lt;MeshNode&gt;() in views.
    /// Details is set as the default area for empty path requests.
    /// </summary>
    public static MessageHubConfiguration AddDefaultViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(DetailsArea)
                .WithView(DetailsArea, Details)
                .WithView(ThumbnailArea, Thumbnail)
                .WithView(MetadataArea, Metadata)
                .WithView(SettingsArea, Settings)
                .WithView(CommentsArea, Comments));

    /// <summary>
    /// Renders the Details area showing the node's main content with action menu.
    /// This is the default view for a node, showing content and providing navigation.
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    public static IObservable<UiControl?> Details(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Use GetStream<MeshNode> to get node data reactively from MeshDataSource
        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                var children = nodes.Where(n => n.ParentPath == hubPath).ToList();
                return BuildDetailsContent(h, node, children);
            },
            hubPath);
    }

    private static UiControl BuildDetailsContent(this LayoutAreaHost host, MeshNode? node, IEnumerable<MeshNode> children)
    {
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
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
                        BuildThumbnailContent(child, child.Namespace ?? ""),
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
        stack = stack.WithView(BuildInlineCommentsSection(host));

        return stack;
    }

    /// <summary>
    /// Builds icon-only action buttons for Edit, Metadata, and Settings.
    /// </summary>
    private static UiControl BuildActionButtons(LayoutAreaHost host, MeshNode? node)
    {
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
        var buttons = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("gap: 8px;");

        // Edit button (icon only)
        if (node != null)
        {
            var editHref = $"/{node.Namespace}/{MeshCatalogView.EditorArea}";
            buttons = buttons.WithView(
                Controls.Button("")
                    .WithIconStart(FluentIcons.Edit(IconSize.Size16))
                    .WithAppearance(Appearance.Stealth)
                    .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(editHref))));
        }

        // Settings button (icon only) - navigates to node settings with NodeType link
        var settingsHref = $"/{nodePath}/{SettingsArea}";
        buttons = buttons.WithView(
            Controls.Button("")
                .WithIconStart(FluentIcons.Settings(IconSize.Size16))
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(settingsHref))));

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
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Use GetStream<MeshNode> to get node data reactively from MeshDataSource
        return host.StreamView<MeshNode>(
            (nodes, _) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildThumbnailContent(node, hubPath);
            },
            hubPath);
    }

    private static UiControl BuildThumbnailContent(MeshNode? node, string hubPath)
    {
        return MeshNodeThumbnailControl.FromNode(node, hubPath);
    }

    /// <summary>
    /// Builds an inline comments section for the bottom of the page (Facebook-style).
    /// </summary>
    private static UiControl BuildInlineCommentsSection(LayoutAreaHost host)
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
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    public static IObservable<UiControl?> Metadata(LayoutAreaHost host, RenderingContext _1)
    {
        var hubPath = host.Hub.Address.ToString();

        // Use GetStream<MeshNode> to get node data reactively from MeshDataSource
        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildMetadataContent(h, node);
            },
            "Metadata");
    }

    private static UiControl BuildMetadataContent(LayoutAreaHost host, MeshNode? node)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header with back link
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
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
        stack = stack.WithView(Controls.Html($"<p><strong>Path:</strong> {node.Namespace}</p>"));

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

    /// <summary>
    /// Renders the Settings area showing node properties with navigatable NodeType link.
    /// Provides read-only view of node metadata with ability to navigate to type definition.
    /// Uses GetStream for reactive data binding instead of direct persistence access.
    /// </summary>
    public static IObservable<UiControl?> Settings(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        // Use GetStream<MeshNode> to get node data reactively from MeshDataSource
        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return BuildSettingsContent(h, node);
            },
            "Node Settings");
    }

    private static UiControl BuildSettingsContent(LayoutAreaHost host, MeshNode? node)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Header with back link
        var nodePath = node?.Namespace ?? host.Hub.Address.ToString();
        var backHref = $"/{nodePath}/{DetailsArea}";
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; justify-content: space-between; margin-bottom: 24px;")
            .WithView(Controls.Html("<h2 style=\"margin: 0;\">Node Settings</h2>"))
            .WithView(Controls.Button("Back to Content")
                .WithAppearance(Appearance.Outline)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(backHref)))));

        if (node == null)
        {
            stack = stack.WithView(Controls.Html("<p><em>Node not found.</em></p>"));
            return stack;
        }

        // Settings card
        var card = Controls.Stack
            .WithStyle("background: #f8f9fa; border-radius: 8px; padding: 20px;");

        // Name
        card = card.WithView(BuildSettingsRow("Name", node.Name ?? "<no name>"));

        // Path
        card = card.WithView(BuildSettingsRow("Path", node.Namespace ?? ""));

        // NodeType with navigatable link
        if (!string.IsNullOrEmpty(node.NodeType))
        {
            var typeHref = $"/type/{node.NodeType}";
            var typeLink = Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px;")
                .WithView(Controls.Html($"<span>{node.NodeType}</span>"))
                .WithView(Controls.Button("View Type Definition")
                    .WithAppearance(Appearance.Lightweight)
                    .WithIconStart(FluentIcons.Open(IconSize.Size16))
                    .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(typeHref))));

            card = card.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 12px 0; border-bottom: 1px solid #e0e0e0;")
                .WithView(Controls.Html("<strong style=\"width: 150px; flex-shrink: 0;\">Node Type:</strong>"))
                .WithView(typeLink));
        }

        // Description
        if (!string.IsNullOrWhiteSpace(node.Description))
        {
            card = card.WithView(BuildSettingsRow("Description", node.Description));
        }

        // Icon
        if (!string.IsNullOrWhiteSpace(node.IconName))
        {
            card = card.WithView(BuildSettingsRow("Icon", node.IconName));
        }

        // Display Order
        card = card.WithView(BuildSettingsRow("Display Order", node.DisplayOrder.ToString()));

        // Is Persistent
        card = card.WithView(BuildSettingsRow("Persistent", node.IsPersistent ? "Yes" : "No"));

        // Parent Path
        if (!string.IsNullOrEmpty(node.ParentPath))
        {
            var parentHref = $"/{node.ParentPath}/{DetailsArea}";
            var parentLink = Controls.Button(node.ParentPath)
                .WithAppearance(Appearance.Lightweight)
                .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(parentHref)));

            card = card.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("padding: 12px 0; border-bottom: 1px solid #e0e0e0;")
                .WithView(Controls.Html("<strong style=\"width: 150px; flex-shrink: 0;\">Parent:</strong>"))
                .WithView(parentLink));
        }

        stack = stack.WithView(card);

        return stack;
    }

    private static UiControl BuildSettingsRow(string label, string value)
    {
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("padding: 12px 0; border-bottom: 1px solid #e0e0e0;")
            .WithView(Controls.Html($"<strong style=\"width: 150px; flex-shrink: 0;\">{label}:</strong>"))
            .WithView(Controls.Html($"<span>{value}</span>"));
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
    /// Uses GetStream for reactive data binding when Comment is registered as mapped type.
    /// </summary>
    public static IObservable<UiControl?> Comments(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();

        // Use GetStream<Comment> to get comment data reactively from MeshDataSource
        return host.StreamView<Comment>(
            (comments, _) => BuildFacebookStyleComments(host, comments.ToList(), nodePath),
            "Comments");
    }

    private static UiControl BuildFacebookStyleComments(LayoutAreaHost _1, List<Comment> comments, string _2)
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
