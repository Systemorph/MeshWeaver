using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

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
    /// Returns a NodeExportControl that the Blazor layer renders as an export + download UI.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Export(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();

        return host.StreamView<MeshNode>(
            (nodes, h) =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                return (UiControl?)new NodeExportControl
                {
                    SourcePath = hubPath,
                    NodeName = node?.Name
                };
            },
            hubPath);
    }
}
