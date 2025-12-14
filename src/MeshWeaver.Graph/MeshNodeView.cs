using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for mesh node content: Overview (main entity) and Comments.
/// Used alongside MeshCatalogView (Nodes tab) in the tabbed node interface.
/// </summary>
public static class MeshNodeView
{
    public const string OverviewArea = "Overview";
    public const string CommentsArea = "Comments";

    /// <summary>
    /// Adds the mesh node views (Overview, Comments) to the hub's layout.
    /// </summary>
    public static MessageHubConfiguration AddMeshNodeView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(OverviewArea, Overview)
            .WithView(CommentsArea, Comments));

    /// <summary>
    /// Renders the Overview tab showing the node's main content.
    /// For nodes with NodeDescription content, displays markdown with edit capability.
    /// </summary>
    public static IObservable<UiControl> Overview(LayoutAreaHost host, RenderingContext _)
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
            return BuildOverviewContent(host, node);
        });
    }

    private static UiControl BuildOverviewContent(LayoutAreaHost host, MeshNode? node)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Title
        var title = node?.Name ?? host.Hub.Address.ToString();
        stack = stack.WithView(Controls.Html($"<h1>{title}</h1>"));

        // Node type badge if available
        if (!string.IsNullOrEmpty(node?.NodeType))
        {
            stack = stack.WithView(Controls.Html($"<span class=\"badge\">{node.NodeType}</span>"));
        }

        // Description content
        var description = GetNodeDescription(node);
        if (!string.IsNullOrWhiteSpace(description))
        {
            stack = stack.WithView(new MarkdownControl(description));
        }
        else
        {
            stack = stack.WithView(Controls.Html("<p><em>No description available.</em></p>"));
        }

        // Edit functionality using a simple approach with LayoutArea
        // The edit mode can be implemented with a separate area or via domain details
        if (node != null)
        {
            var editHref = DomainViews.GetDetailsReference(nameof(MeshNode), node.Prefix).ToHref(host.Hub.Address);
            stack = stack.WithView(Controls.Html(
                $"<p><a href=\"{editHref}\">Edit Details</a></p>"));
        }

        return stack;
    }

    private static string GetNodeDescription(MeshNode? node)
    {
        // Check Content first (NodeDescription or other types)
        if (node?.Content is NodeDescription nd)
            return nd.Description;

        // Fall back to MeshNode.Description (summary field)
        return node?.Description ?? string.Empty;
    }

    /// <summary>
    /// Renders the Comments tab showing comments for the node.
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
