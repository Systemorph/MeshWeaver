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
/// Layout view for displaying a catalog of MeshNode children.
/// Shows metadata like Name, NodeType, Description in a DataGrid.
/// </summary>
public static class MeshCatalogView
{

    /// <summary>
    /// Adds the mesh catalog view to the hub's layout.
    /// This enables browsing child mesh nodes in a DataGrid.
    /// </summary>
    public static MessageHubConfiguration AddMeshCatalogView(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(NodesArea, MeshCatalogView.Nodes));

    public const string NodesArea = $"_{nameof(Nodes)}";

    /// <summary>
    /// Renders the catalog of child mesh nodes for the current hub address.
    /// </summary>
    public static IObservable<UiControl> Nodes(LayoutAreaHost host, RenderingContext _)
    {
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        var parentPath = host.Hub.Address.ToString();

        if (meshCatalog == null)
        {
            return Observable.Return(Controls.Stack
                .WithView(Controls.Html($"<h2>Browse {parentPath}</h2>"))
                .WithView(Controls.Html("<p>Mesh catalog not available.</p>")));
        }

        // Stream the children from the mesh catalog
        var stream = Observable.FromAsync(async ct =>
            await meshCatalog.Persistence.GetChildrenAsync(parentPath, ct));

        var id = Guid.NewGuid().AsString();
        host.RegisterForDisposal(stream
            .Select(nodes => nodes.Select(n => new MeshNodeViewModel(n)).ToList())
            .Subscribe(x => host.UpdateData(id, x)));

        return Observable.Return(Controls.Stack
            .WithView(Controls.Html($"<h2>Browse {parentPath}</h2>"))
            .WithView(CreateDataGrid(id)));
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
