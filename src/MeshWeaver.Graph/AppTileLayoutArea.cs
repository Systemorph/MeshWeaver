using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph;

/// <summary>
/// The APP TILE renderer — one card of the home's Apps grid, rendered from the viewer's own
/// <c>InstalledApp</c> RECORD (<c>{user}/_App/{appId}</c>, see <see cref="Configuration.AppNodeType"/>):
/// the record carries the tile's name and icon, and its <see cref="App.OpenPath"/> /
/// <see cref="App.Plugin"/> is where the card NAVIGATES — never the record node itself. Rendering
/// from the record (not the target's cover node) is what makes the Apps grid a SINGLE-PARTITION
/// query: the old cover-based grid resolved a top-level path alternation across every partition
/// schema, the multi-second home lag. Registered on the record's own hub and consumed as the Apps
/// scope's per-item area.
/// </summary>
public static class AppTileLayoutArea
{
    /// <summary>Area name, consumed via <c>MeshSearchScopeTab.ItemArea</c>.</summary>
    public const string AppTileArea = "AppTile";

    /// <summary>The tile renderer — runs on the record's own hub, reading the hub's OWN node via
    /// the dedicated <see cref="MeshNodeReference"/> reducer (never a whole-collection scan). The
    /// viewer's presentation screen (#1803) filters HERE: the Apps query is generic
    /// (<c>{owner}/_App</c>), so no marked path can reach a query string — the tile is where a
    /// marked app's name would otherwise be painted, and a marked target renders nothing.</summary>
    public static IObservable<UiControl?> View(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var options = host.Hub.JsonSerializerOptions;
        var syncStream = host.Workspace.GetStream(new MeshNodeReference());
        return syncStream!
            .CombineLatest(host.ViewerScreen(),
                (change, screen) => BuildTile(change.Value, hubPath, options, screen))
            .StartWith(BuildSkeleton(hubPath));
    }

    /// <summary>A compact loading skeleton so the grid doesn't jump while a record hub warms up.</summary>
    private static UiControl BuildSkeleton(string hubPath)
    {
        var shortName = hubPath.Contains('/') ? hubPath[(hubPath.LastIndexOf('/') + 1)..] : hubPath;
        return Controls.Stack
            .WithStyle("width: 100%; min-height: 92px; padding: 10px 12px; box-sizing: border-box; " +
                       "display: flex; justify-content: center; opacity: 0.6;")
            .WithView(Controls.Markdown($"**{shortName}**"));
    }

    /// <summary>
    /// The card: title + icon come from the RECORD; the click target is the app itself —
    /// <see cref="App.OpenPath"/> when set, else the <see cref="App.Plugin"/> path. A target the
    /// viewer's presentation screen marks renders NOTHING (the marked name must not be painted).
    /// Pure and null-tolerant, exposed for tests.
    /// </summary>
    internal static UiControl? BuildTile(
        MeshNode? node, string hubPath, JsonSerializerOptions options, PresentationScreen? screen = null)
    {
        var app = node.ContentAs<App>(options);
        var target = (app?.OpenPath ?? app?.Plugin ?? "").Trim('/');
        if (target.Length > 0 && (screen ?? PresentationScreen.Off).Retain([target]).Count == 0)
            return null;
        var title = node?.Name
                    ?? (hubPath.Contains('/') ? hubPath[(hubPath.LastIndexOf('/') + 1)..] : hubPath);
        if (target.Length == 0)
            // A record without a target renders inert rather than navigating to the record itself.
            return new MeshNodeCardControl(hubPath, Title: title,
                ImageUrl: node?.Icon ?? "/static/NodeTypeIcons/puzzlepiece.svg", DisableNavigation: true);
        return new MeshNodeCardControl(target, Title: title,
            ImageUrl: node?.Icon ?? "/static/NodeTypeIcons/puzzlepiece.svg");
    }
}
