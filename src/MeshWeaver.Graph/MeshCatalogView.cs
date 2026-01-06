using System.Reactive.Linq;
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
/// Layout view for displaying mesh node children filtered by type.
/// - _Nodes: All child nodes
/// - _Nodes/{nodeType}: Child nodes filtered by type
/// - _Editor: Node editor with back button
/// </summary>
public static class MeshCatalogView
{
    public const string NodesArea = "_Nodes";
    public const string EditorArea = "_Editor";

    /// <summary>
    /// Adds the mesh catalog view to the hub's layout.
    /// This enables browsing child mesh nodes and editing.
    /// </summary>
    public static MessageHubConfiguration AddMeshCatalogView(this MessageHubConfiguration configuration)
        => MeshNodeView.AddMeshNodeViews(configuration) // Add Overview, Details, and Comments views
            .AddLayout(layout => layout
                .WithView(NodesArea, Nodes)
                .WithView(EditorArea, Editor));

    /// <summary>
    /// Renders a DataGrid of child nodes, optionally filtered by node type.
    /// The area reference ID can contain the node type filter (e.g., "_Nodes/story").
    /// </summary>
    public static IObservable<UiControl> Nodes(LayoutAreaHost host, RenderingContext ctx)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        var parentPath = host.Hub.Address.ToString();

        if (meshCatalog == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html($"<h2>Browse {parentPath}</h2>"))
                .WithView(Controls.Html("<p>Mesh catalog not available.</p>")));
        }

        // Extract node type filter from the area reference ID if present
        // The ID format is "nodeType" when accessing _Nodes/nodeType
        var nodeTypeFilter = host.Reference?.Id as string;

        return Observable.FromAsync(async ct =>
        {
            var children = new List<MeshNode>();
            await foreach (var child in meshCatalog.Persistence.GetChildrenAsync(parentPath).WithCancellation(ct))
            {
                // Filter by node type if specified
                if (!string.IsNullOrEmpty(nodeTypeFilter))
                {
                    if (!string.Equals(child.NodeType, nodeTypeFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                children.Add(child);
            }

            return BuildNodesView(host, parentPath, nodeTypeFilter, children);
        });
    }

    private static UiControl BuildNodesView(LayoutAreaHost host, string parentPath, string? nodeTypeFilter, IEnumerable<MeshNode> children)
    {
        var stack = Controls.Stack.WithWidth("100%");

        // Title
        var title = string.IsNullOrEmpty(nodeTypeFilter)
            ? $"All Children of {parentPath}"
            : $"{char.ToUpper(nodeTypeFilter[0])}{nodeTypeFilter.Substring(1)}s";
        stack = stack.WithView(Controls.Html($"<h2>{title}</h2>"));

        // Create data grid with children
        var dataId = Guid.NewGuid().AsString();
        var viewModels = children.Select(n => new MeshNodeViewModel(n)).ToList();
        host.UpdateData(dataId, viewModels);

        stack = stack.WithView(CreateDataGrid(dataId));

        return stack;
    }

    private static DataGridControl CreateDataGrid(string dataId)
    {
        return new DataGridControl(new JsonPointerReference(LayoutAreaReference.GetDataPointer(dataId)))
            .WithColumn(new PropertyColumnControl<string> { Property = "name" }.WithTitle("Name"))
            .WithColumn(new PropertyColumnControl<string> { Property = "nodeType" }.WithTitle("Type"))
            .WithColumn(new PropertyColumnControl<string> { Property = "description" }.WithTitle("Description"))
            .WithClickAction(HandleNodeClick);
    }

    private static void HandleNodeClick(UiActionContext context)
    {
        if (context.Payload is DataGridCellClick { Item: MeshNodeViewModel node })
        {
            // Navigate to child node's Details (main content view)
            context.Host.UpdateArea(context.Area,
                new RedirectControl($"/{node.Path}/{MeshNodeView.DetailsArea}"));
        }
    }

    /// <summary>
    /// Renders the mesh node editor view for editing node metadata and content.
    /// Includes a back button to return to Details.
    /// </summary>
    public static IObservable<UiControl> Editor(LayoutAreaHost host, RenderingContext ctx)
    {
        var nodePath = host.Hub.Address.ToString();
        var persistence = host.Hub.ServiceProvider.GetService<IPersistenceService>();

        return Observable.FromAsync(async ct =>
        {
            var node = await persistence?.GetNodeAsync(nodePath, ct)!;

            // Wrap editor control with a back button
            var stack = Controls.Stack.WithWidth("100%");

            // Back button
            var detailsHref = $"/{nodePath}/{MeshNodeView.DetailsArea}";
            stack = stack.WithView(
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithView(Controls.Button("← Back to Content")
                        .WithClickAction(c => c.Host.UpdateArea(c.Area, new RedirectControl(detailsHref)))));

            // Editor control
            stack = stack.WithView(new MeshNodeEditorControl
            {
                NodePath = nodePath,
                NodeType = node?.NodeType
            });

            return (UiControl)stack;
        });
    }
}


/// <summary>
/// View model for displaying MeshNode metadata in the catalog.
/// </summary>
public record MeshNodeViewModel
{
    public string Path { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? NodeType { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }

    public MeshNodeViewModel() { }

    public MeshNodeViewModel(MeshNode node)
    {
        Path = node.Path;
        Name = node.Name;
        NodeType = node.NodeType;
        Description = node.Description;
        Icon = node.Icon;
    }
}
