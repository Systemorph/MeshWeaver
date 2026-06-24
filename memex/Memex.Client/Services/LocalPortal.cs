using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;

namespace Memex.Client.Services;

/// <summary>
/// The local-mesh "home" portal area, served by the LOCAL hub and rendered natively by the MeshWeaver.Maui
/// view pack: an intro plus a live <see cref="DataGridControl"/> of the in-process SQLite mesh's real nodes.
/// Uses only pack-supported controls (Stack / Markdown / DataGrid), so it renders fully without Blazor —
/// this is the actual portal layout shown in the frame, driven by real mesh data.
/// </summary>
public static class LocalPortal
{
    /// <summary>A flat row for the node DataGrid (camelCase JSON props drive the column bindings).</summary>
    public record NodeRow(string Name, string Path, string Type);

    /// <summary>Reactive area generator for the "home" area: re-renders whenever the mesh's node set changes.</summary>
    public static IObservable<UiControl> Home(LayoutAreaHost host, RenderingContext context) =>
        host.Hub.GetQuery("home-nodes", "is:main")
            .Select(nodes => (UiControl)Controls.Stack
                .WithView(Controls.Markdown(
                    "# Local Mesh\n\nRendered **natively** with the MeshWeaver.Maui view pack — live from this "
                    + "device's in-process SQLite mesh (no Blazor, no WebView)."), "intro")
                .WithView(Controls.DataGrid(nodes
                        .OrderBy(n => n.Path)
                        .Select(n => new NodeRow(n.Name ?? n.Path, n.Path, n.NodeType ?? ""))
                        .ToArray())
                    .WithColumn(new PropertyColumnControl<string> { Property = "name" }.WithTitle("Name"))
                    .WithColumn(new PropertyColumnControl<string> { Property = "path" }.WithTitle("Path"))
                    .WithColumn(new PropertyColumnControl<string> { Property = "type" }.WithTitle("Type")), "nodes"));
}
