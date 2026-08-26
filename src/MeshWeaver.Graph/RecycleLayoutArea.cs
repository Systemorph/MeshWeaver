using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Recycling a node's hub — tearing it down so the next access re-activates it against the latest
/// compiled build. Lets a user clear a cached / stuck grain (e.g. after fixing a compilation error,
/// or to pick up a newer NodeType build) without restarting the whole portal.
///
/// <para>🚨 <b>Recycle is an ACTION run by the PAGE, never a layout area on the target hub</b>
/// (#2084 + #2202 — one defect seen from two sides). This class used to render a confirmation
/// hosted on the very hub the confirmation kills: the confirm button pushed a
/// <c>RedirectControl</c> into the area stream and posted <see cref="DisposeRequest"/> to that same
/// hub, so the redirect had to outrun a teardown of the stream carrying it. It did not — the hub
/// recycled and the button read as dead — and afterwards the landing page came back as a per-area
/// refresh mosaic, because every module's stream faulted and re-subscribed independently
/// (maintainer: <i>"refresh is a matter of the page, not each module"</i>). Re-ordering the two
/// posts did not fix it; the race is structural.</para>
///
/// <para>What is left here is therefore deliberately inert: a menu ENTRY carrying
/// <see cref="MenuActions.Recycle"/>, and a passive progress card for the
/// <c>/{path}/Recycle</c> URL. <b>Neither performs the recycle.</b> The page shell owns the whole
/// flow — confirm, <c>hub.RecycleNode(path)</c> from the CIRCUIT's hub, then ONE page-level
/// navigation once the address answers again. Rendering this area has no side effect at all, which
/// also closes the older defect where merely NAVIGATING to the URL recycled the hub.</para>
/// </summary>
public static class RecycleLayoutArea
{
    /// <summary>
    /// Where the flow lands: the node's DEFAULT page (<c>/{path}</c>, empty area) — the same
    /// rule the breadcrumbs follow, and never a hardcoded <c>Overview</c>. For a plugin node the
    /// default page is its COVER; the Overview area is the generic raw-body dump, and sending a
    /// user there read as a broken page (memex, 2026-08-25: Cancel on OpenStreetMap/Recycle landed
    /// on the un-rendered cover HTML). Pure.
    /// </summary>
    public static string LandingHref(string nodePath) => $"/{(nodePath ?? "").Trim('/')}";

    /// <summary>
    /// The node path a <c>/{path}/Recycle</c> URL names, or <c>null</c> when
    /// <paramref name="relativePath"/> cannot be one. A bare <c>/Recycle</c> (no node) returns null
    /// rather than the empty path — recycling "the root" is not a thing this URL can express.
    ///
    /// <para>🚨 A cheap PRE-FILTER, not the decision. The string cannot tell a node's Recycle AREA
    /// from a node that is itself called <c>…/Recycle</c>, so the page shell uses this only to
    /// avoid resolving on every navigation and then asks <c>IPathResolver</c> — the same resolution
    /// the page itself performs — for the authoritative prefix/remainder split.</para>
    /// </summary>
    public static string? TryGetTargetFromUrl(string? relativePath)
    {
        var trimmed = (relativePath ?? "").Trim('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash < 1)
            return null;
        if (!string.Equals(trimmed[(lastSlash + 1)..], MeshNodeLayoutAreas.RecycleArea,
                StringComparison.OrdinalIgnoreCase))
            return null;
        return trimmed[..lastSlash];
    }

    /// <summary>
    /// Returns the Recycle menu item if the user has Update permission.
    /// Sort order 90 places it just above Delete (100).
    ///
    /// <para>🚨 An ACTION entry (<see cref="MenuActions.Recycle"/>), so a renderer that knows the
    /// id runs it in place. <see cref="NodeMenuItemDefinition.Href"/> is the LANDING page rather
    /// than the confirmation URL — for an action the href is where the page ends up, and it is what
    /// an unaware renderer falls back to (the node's own page: harmless, never a dead URL).
    /// <see cref="NodeMenuItemDefinition.Area"/> stays <c>Recycle</c> because it is the stable key
    /// the <c>MenuPresentation</c> catalog matches on — changing it would orphan every admin
    /// override.</para>
    /// </summary>
    public static NodeMenuItemDefinition? GetMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Update))
            return null;
        return new("Recycle", MeshNodeLayoutAreas.RecycleArea,
            RequiredPermission: Permission.Update, Order: 90,
            Href: LandingHref(hubPath))
            { LabelKey = "menu.recycle", TooltipKey = "menu.recycleTooltip", Action = MenuActions.Recycle };
    }

    /// <summary>
    /// The <c>/{path}/Recycle</c> area: a passive "Recycling…" card, and nothing else.
    ///
    /// <para>🚨 <b>No button, no <see cref="DisposeRequest"/>, no redirect — by design.</b> The URL
    /// is kept live so the stale-build banner's link and existing bookmarks do not 404, but the
    /// hub that renders it is the hub about to be torn down, and anything it emits races that
    /// teardown (see the remarks on this class). The page shell recognises the URL, runs the flow
    /// on the CIRCUIT, and navigates away when the address answers again; this card is the ONE
    /// page-level progress indication the user sees meanwhile.</para>
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Recycle(LayoutAreaHost host, RenderingContext _)
        => Observable.Return((UiControl?)Controls.Stack
            .WithStyle("padding: 24px; max-width: 640px;")
            .WithVerticalGap(16)
            .WithView(Controls.Markdown(host.Localize("ui.mdRecyclingHub"))));
}
