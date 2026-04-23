using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for exporting mesh node subtrees as downloadable ZIP archives
/// using file persister formats (.md, .cs, .json) for round-trip compatibility with import.
/// </summary>
[Browsable(false)]
public static class ExportLayoutArea
{
    public const string ExportArea = "Export";

    /// <summary>
    /// Returns the Export menu item if the user has Export permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Export))
            return null;
        return new("Export", MeshNodeLayoutAreas.ExportArea,
            RequiredPermission: Permission.Export, Order: 26,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.ExportArea));
    }

    /// <summary>
    /// Layout area handler for the Export action.
    /// Detects available satellite types and passes them to NodeExportControl for checkbox display.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Export(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();

        // Own node via MeshNodeReference stream — no QueryAsync, no Observable.FromAsync.
        var ownNode = host.Workspace.GetMeshNodeStream();

        // Descendants via ObserveQuery initial snapshot — listing is legitimate observable.
        var descendants = meshService.ObserveQuery<MeshNode>(
                MeshQueryRequest.FromQuery($"path:{hubPath} scope:descendants"))
            .Take(1)
            .Select(c => c.Items);

        return ownNode.Take(1).CombineLatest(descendants, (node, descs) =>
        {
            var satelliteTypes = new HashSet<string>();
            foreach (var desc in descs)
            {
                if ((desc.IsSatelliteType || (desc.MainNode != null && desc.MainNode != desc.Path))
                    && desc.NodeType != null)
                    satelliteTypes.Add(desc.NodeType);
            }

            return (UiControl?)new NodeExportControl
            {
                SourcePath = hubPath,
                NodeName = node?.Name,
                AvailableSatelliteTypes = satelliteTypes.OrderBy(t => t).ToList()
            };
        });
    }
}
