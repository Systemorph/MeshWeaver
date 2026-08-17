using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout area for recycling the current node's hub: renders a confirmation, and on the user's
/// click tears the hub down so the next access re-activates it against the latest compiled build.
/// Lets a user clear a cached / stuck grain (e.g. after fixing a compilation error, or to pick up
/// a newer NodeType build) without restarting the whole portal.
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
            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.RecycleArea))
            { LabelKey = "menu.recycle" };
    }

    /// <summary>
    /// Entry point for the Recycle layout area — a CONFIRMATION, with the teardown on the button.
    ///
    /// <para>🚨 <b>Rendering this area must have no side effect.</b> It used to post
    /// <see cref="DisposeRequest"/> from inside the render and then rely on
    /// <c>Observable.Timer(100ms)</c> to deliver a <see cref="RedirectControl"/> — through the very
    /// hub it had just told to die. Two defects in one: merely NAVIGATING to the URL recycled the
    /// hub (no confirmation, and any re-render did it again), and the redirect raced its own
    /// dispose, so the page usually sat on "Recycling…" forever and the action looked like it did
    /// nothing. The 100 ms was the tell — a sleep standing in for an ordering guarantee.</para>
    ///
    /// <para><b>The ordering that removes the race.</b> In the click action the redirect is pushed
    /// FIRST and the <see cref="DisposeRequest"/> posted SECOND. The click runs on the hub's own
    /// turn, so the dispose is queued BEHIND it: the redirect emission has already left for the
    /// client before the hub begins tearing down. Nothing has to survive the teardown, so there is
    /// nothing to wait for and no timer.</para>
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Recycle(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.Path;
        var targetAddress = host.Hub.Address;
        var overviewHref = MeshNodeLayoutAreas.BuildUrl(nodePath, MeshNodeLayoutAreas.OverviewArea);

        var card = Controls.Stack
            .WithStyle("padding: 24px; max-width: 640px;")
            .WithVerticalGap(16)
            .WithView(Controls.Markdown(host.Localize("ui.mdRecycleConfirm")))
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(12)
                .WithView(Controls.Button(host.Localize("common.cancel"))
                    .WithAppearance(Appearance.Neutral)
                    .WithNavigateToHref(overviewHref))
                .WithView(Controls.Button(host.Localize("menu.recycle"))
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.BinRecycle())
                    .WithClickAction(ctx =>
                    {
                        // Redirect FIRST — see the remarks above. This emission is on the wire
                        // before the dispose below is dequeued, so the client never depends on a
                        // hub that is shutting down.
                        ctx.Host.UpdateArea(ctx.Area, new RedirectControl(overviewHref));
                        ctx.Host.Hub.Post(new DisposeRequest(), o => o.WithTarget(targetAddress));
                        return Task.CompletedTask;
                    })));

        return Observable.Return((UiControl?)card);
    }
}
