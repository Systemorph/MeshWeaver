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
/// Layout view for displaying mesh node content in a tabbed interface.
/// Tab 1: Overview - Main entity display (NodeDescription or custom type)
/// Tab 2: Nodes - Child nodes DataGrid
/// Tab 3: Comments - Comments section
/// </summary>
public static class MeshCatalogView
{

    /// <summary>
    /// Adds the mesh catalog view to the hub's layout.
    /// This enables browsing child mesh nodes in a tabbed interface.
    /// </summary>
    public static MessageHubConfiguration AddMeshCatalogView(this MessageHubConfiguration configuration)
        => configuration
            .AddMeshNodeView() // Add Overview and Comments views
            .AddLayout(layout => layout
                .WithView(NodesArea, MeshCatalogView.Nodes));

    public const string NodesArea = $"_{nameof(Nodes)}";

    /// <summary>
    /// Renders the tabbed view for mesh nodes.
    /// Tab 1: Overview - Node content from MeshNodeView.Overview
    /// Tab 2: Nodes - Child nodes DataGrid
    /// Tab 3: Comments - Comments from MeshNodeView.Comments
    /// </summary>
    public static IObservable<UiControl> Nodes(LayoutAreaHost host, RenderingContext ctx)
    {
        return Observable.Return(Controls.Tabs
            .WithView(BuildOverviewTab(host, ctx), tab => tab.WithLabel("Overview"))
            .WithView(BuildNodesTab(host), tab => tab.WithLabel("Nodes"))
            .WithView(BuildCommentsTab(host, ctx), tab => tab.WithLabel("Comments")));
    }

    private static UiControl BuildOverviewTab(LayoutAreaHost host, RenderingContext ctx)
    {
        // Use the LayoutArea control to render the Overview area
        return Controls.LayoutArea(host.Hub.Address, MeshNodeView.OverviewArea);
    }

    private static UiControl BuildNodesTab(LayoutAreaHost host)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        var parentPath = host.Hub.Address.ToString();

        if (meshCatalog == null)
        {
            return Controls.Stack
                .WithView(Controls.Html($"<h2>Browse {parentPath}</h2>"))
                .WithView(Controls.Html("<p>Mesh catalog not available.</p>"));
        }

        // Stream the children from the mesh catalog
        var stream = Observable.FromAsync(async ct =>
            await meshCatalog.Persistence.GetChildrenAsync(parentPath, ct));

        var id = Guid.NewGuid().AsString();
        host.RegisterForDisposal(stream
            .Select(nodes => nodes.Select(n => new MeshNodeViewModel(n)).ToList())
            .Subscribe(x => host.UpdateData(id, x)));

        return Controls.Stack
            .WithView(Controls.Html($"<h2>Browse {parentPath}</h2>"))
            .WithView(CreateDataGrid(id));
    }

    private static UiControl BuildCommentsTab(LayoutAreaHost host, RenderingContext ctx)
    {
        // Use the LayoutArea control to render the Comments area
        return Controls.LayoutArea(host.Hub.Address, MeshNodeView.CommentsArea);
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
            // Navigate to child node's Mesh view
            context.Host.UpdateArea(context.Area,
                new RedirectControl($"/{node.Path}/{NodesArea}"));
        }
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
    public string? IconName { get; init; }

    public MeshNodeViewModel() { }

    public MeshNodeViewModel(MeshNode node)
    {
        Path = node.Prefix;
        Name = node.Name;
        NodeType = node.NodeType;
        Description = node.Description;
        IconName = node.IconName;
    }
}
