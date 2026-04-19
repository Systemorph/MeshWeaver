using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for recycling the current node's hub — sends <see cref="DisposeRequest"/>
/// to the hub address, waits 100ms, and redirects back to Overview. Lets the user clear
/// a cached / stuck grain (e.g. after fixing a compilation error) without restarting the
/// whole portal.
/// </summary>
public static class RecycleLayoutArea
{
    /// <summary>
    /// Returns the Recycle menu item if the user has Update permission.
    /// Sort order 90 places it just above Delete (100).
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Update))
            return null;
        return new("Recycle", MeshNodeLayoutAreas.RecycleArea,
            RequiredPermission: Permission.Update, Order: 90,
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.RecycleArea));
    }

    /// <summary>
    /// Entry point for the Recycle layout area. Posts DisposeRequest immediately,
    /// then emits a transient "Recycling…" message followed by a RedirectControl
    /// back to Overview after 100ms — enough time for the hub to tear down and
    /// come up fresh on the next request.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Recycle(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.Path;
        var targetAddress = host.Hub.Address;
        var overviewHref = MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);

        // Fire the dispose synchronously — no await. The hub receives it and shuts
        // down; the grain's next access will re-initialize (and, in Orleans setups,
        // a fresh activation compiles with whatever sources the NodeType now lists).
        host.Hub.Post(new DisposeRequest(), o => o.WithTarget(targetAddress));

        var recyclingMessage = (UiControl?)Controls.Stack
            .WithStyle("padding: 24px;")
            .WithView(Controls.Markdown("**Recycling hub…** redirecting in a moment."));

        var redirect = (UiControl?)new RedirectControl(overviewHref);

        return Observable.Return(recyclingMessage)
            .Concat(Observable.Timer(TimeSpan.FromMilliseconds(100)).Select(_ => redirect));
    }
}
