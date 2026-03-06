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
        => MeshNodeLayoutAreas.AddDefaultLayoutAreas(configuration) // Add Overview, Details, and Comments views
            .AddLayout(layout => layout
                .WithView(NodesArea, Nodes)
                .WithView(EditorArea, Editor));

    /// <summary>
    /// Renders a DataGrid of child nodes, optionally filtered by node type.
    /// The area reference ID can contain the node type filter (e.g., "_Nodes/story").
    /// </summary>
    public static IObservable<UiControl> Nodes(LayoutAreaHost host, RenderingContext ctx)
    {
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        var parentPath = host.Hub.Address.ToString();

        if (meshQuery == null)
        {
            return Observable.Return(Controls.Stack
                .WithWidth("100%")
                .WithView(Controls.Html($"<h2>Browse {parentPath}</h2>"))
                .WithView(Controls.Html("<p>Query service not available.</p>")));
        }

        // Extract node type filter from the area reference ID if present
        // The ID format is "nodeType" when accessing _Nodes/nodeType
        var nodeTypeFilter = host.Reference?.Id as string;

        return Observable.FromAsync(async ct =>
        {
            // Build query with optional nodeType filter
            var query = string.IsNullOrEmpty(nodeTypeFilter)
                ? $"path:{parentPath} scope:children"
                : $"path:{parentPath} nodeType:{nodeTypeFilter} scope:children";

            IReadOnlyList<MeshNode> children;
            try
            {
                children = await meshQuery.QueryAsync<MeshNode>(query, ct: ct).ToListAsync(ct);
            }
            catch
            {
                children = Array.Empty<MeshNode>();
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
            .WithClickAction(HandleNodeClick);
    }

    private static void HandleNodeClick(UiActionContext context)
    {
        if (context.Payload is DataGridCellClick { Item: MeshNodeViewModel node })
        {
            // Navigate to child node's Overview (main content view)
            context.Host.UpdateArea(context.Area,
                new RedirectControl($"/{node.Path}/{MeshNodeLayoutAreas.OverviewArea}"));
        }
    }

    /// <summary>
    /// Renders the mesh node editor view for editing node metadata and content.
    /// Includes a back button to return to Details.
    /// </summary>
    public static IObservable<UiControl> Editor(LayoutAreaHost host, RenderingContext ctx)
    {
        var nodePath = host.Hub.Address.ToString();
        var meshQuery = host.Hub.ServiceProvider.GetRequiredService<IMeshQuery>();

        return Observable.FromAsync(async ct =>
        {
            var node = await meshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync(ct);

            // Wrap editor control with a back button
            var stack = Controls.Stack.WithWidth("100%");

            // Back button
            var overviewHref = $"/{nodePath}/{MeshNodeLayoutAreas.OverviewArea}";
            var nodeName = node?.Name ?? nodePath.Split('/').LastOrDefault() ?? "Overview";
            stack = stack.WithView(
                Controls.Stack
                    .WithOrientation(Orientation.Horizontal)
                    .WithView(Controls.Button(nodeName)
                        .WithNavigateToHref(overviewHref)));

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
    public string? Icon { get; init; }

    public MeshNodeViewModel() { }

    public MeshNodeViewModel(MeshNode node)
    {
        Path = node.Path;
        Name = node.Name;
        NodeType = node.NodeType;
        Icon = node.Icon;
    }
}
