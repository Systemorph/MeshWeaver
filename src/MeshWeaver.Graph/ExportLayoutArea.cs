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
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, string? nodeName, Permission perms)
    {
        if (!perms.HasFlag(Permission.Export))
            return null;
        var label = string.IsNullOrEmpty(nodeName) ? "Export" : $"Export {nodeName}";
        return new(label, MeshNodeLayoutAreas.ExportArea,
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

        return Observable.FromAsync(async () =>
        {
            var meshService = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();

            var node = await meshService.QueryAsync<MeshNode>($"path:{hubPath}").FirstOrDefaultAsync();

            // Find which satellite types exist under this subtree
            var satelliteTypes = new HashSet<string>();
            await foreach (var desc in meshService.QueryAsync<MeshNode>($"path:{hubPath} scope:descendants"))
            {
                if (desc.IsSatelliteType || (desc.MainNode != null && desc.MainNode != desc.Path))
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
