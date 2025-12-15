using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
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
/// - Details: Main entity display (readonly content)
/// - Comments: Comments section
/// - Overview: TabsControl routing to Details, Comments, and child nodes by type
/// </summary>
public static class MeshNodeView
{
    public const string OverviewArea = "Overview";
    public const string DetailsArea = "Details";
    public const string CommentsArea = "Comments";

    /// <summary>
    /// Adds the mesh node views (Overview, Details, Comments) to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddMeshNodeView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(OverviewArea, Overview)
            .WithView(DetailsArea, Details)
            .WithView(CommentsArea, Comments));

    /// <summary>
    /// Renders the Overview as a TabsControl that routes to other areas.
    /// Dynamically creates tabs for Details, Comments, and each child node type.
    /// </summary>
    public static IObservable<UiControl> Overview(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();

        if (meshCatalog == null)
        {
            return Observable.Return(Controls.Tabs
                .WithView(Controls.LayoutArea(host.Hub.Address, DetailsArea), tab => tab.WithLabel("Details"))
                .WithView(Controls.LayoutArea(host.Hub.Address, CommentsArea), tab => tab.WithLabel("Comments")));
        }

        // Load children to determine which node types exist
        return Observable.FromAsync(async ct =>
        {
            var children = await meshCatalog.Persistence.GetChildrenAsync(hubPath, ct);
            return BuildOverviewTabs(host, children);
        });
    }

    private static UiControl BuildOverviewTabs(LayoutAreaHost host, IEnumerable<MeshNode> children)
    {
        var tabs = Controls.Tabs
            .WithView(Controls.LayoutArea(host.Hub.Address, DetailsArea), tab => tab.WithLabel("Details"))
            .WithView(Controls.LayoutArea(host.Hub.Address, CommentsArea), tab => tab.WithLabel("Comments"));

        // Group children by node type and create a tab for each type
        var nodeTypeGroups = children
            .Where(n => !string.IsNullOrEmpty(n.NodeType))
            .GroupBy(n => n.NodeType!)
            .OrderBy(g => g.Key);

        foreach (var group in nodeTypeGroups)
        {
            var nodeType = group.Key;
            var displayName = GetNodeTypeDisplayName(nodeType, group.Count());
            tabs = tabs.WithView(
                Controls.LayoutArea(host.Hub.Address, MeshCatalogView.NodesArea, nodeType),
                tab => tab.WithLabel(displayName));
        }

        return tabs;
    }

    private static string GetNodeTypeDisplayName(string nodeType, int count)
    {
        // Capitalize first letter and add count
        var display = char.ToUpper(nodeType[0]) + nodeType.Substring(1);
        return $"{display}s ({count})";
    }

    /// <summary>
    /// Renders the Details area showing the node's main content (readonly).
    /// </summary>
    public static IObservable<UiControl> Details(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (persistence == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html($"<h2>{hubPath}</h2>"))
                .WithView(Controls.Html("<p>Persistence service not available.</p>")));
        }

        // Load node data asynchronously
        return Observable.FromAsync(async ct =>
        {
            var node = await persistence.GetNodeAsync(hubPath, ct);
            return BuildDetailsContent(host, node);
        });
    }

    private static UiControl BuildDetailsContent(LayoutAreaHost host, MeshNode? node)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Title with Edit button
        var title = node?.Name ?? host.Hub.Address.ToString();
        var headerStack = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithView(Controls.Html($"<h1 style=\"flex: 1;\">{title}</h1>"));

        if (node != null)
        {
            var editorHref = $"/{node.Prefix}/{MeshCatalogView.EditorArea}";
            headerStack = headerStack.WithView(
                Controls.Button("Edit")
                    .WithClickAction(ctx => ctx.Host.UpdateArea(ctx.Area, new RedirectControl(editorHref))));
        }

        stack = stack.WithView(headerStack);

        // Node type badge if available
        if (!string.IsNullOrEmpty(node?.NodeType))
        {
            stack = stack.WithView(Controls.Html($"<span class=\"badge\" style=\"margin-bottom: 16px;\">{node.NodeType}</span>"));
        }

        // Description (summary)
        if (!string.IsNullOrWhiteSpace(node?.Description))
        {
            stack = stack.WithView(Controls.Html($"<p><em>{node.Description}</em></p>"));
        }

        // Main content based on node type
        var content = GetNodeContent(node);
        if (!string.IsNullOrWhiteSpace(content))
        {
            stack = stack.WithView(new MarkdownControl(content));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p><em>No content available.</em></p>"));
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

    /// <summary>
    /// Renders the Comments area showing comments for the node.
    /// </summary>
    public static IObservable<UiControl> Comments(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        if (persistence == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html("<h2>Comments</h2>"))
                .WithView(Controls.Html("<p>Persistence service not available.</p>")));
        }

        // Stream the comments
        var commentsDataId = Guid.NewGuid().AsString();

        var stream = Observable.FromAsync(async ct =>
            await persistence.GetCommentsAsync(nodePath, ct));

        host.RegisterForDisposal(stream.Subscribe(comments =>
        {
            var viewModels = comments.Select(c => new CommentViewModel(c)).ToList();
            host.UpdateData(commentsDataId, viewModels);
        }));

        return Observable.Return(BuildCommentsView(commentsDataId));
    }

    private static UiControl BuildCommentsView(string commentsDataId)
    {
        return Controls.Stack
            .WithWidth("100%")
            .WithView(Controls.Html("<h2>Comments</h2>"))
            .WithView(Controls.Html("<p>Use the AI agent to add comments. Example: \"Add a comment saying 'This looks good'\"</p>"))
            .WithView(BuildCommentsList(commentsDataId));
    }

    private static UiControl BuildCommentsList(string commentsDataId)
    {
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(commentsDataId)))
            .WithColumn(new PropertyColumnControl<string> { Property = "author" }.WithTitle("Author"))
            .WithColumn(new PropertyColumnControl<string> { Property = "text" }.WithTitle("Comment"))
            .WithColumn(new PropertyColumnControl<string> { Property = "createdAt" }.WithTitle("Date"));
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
