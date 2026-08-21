using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// The per-node half of PRESENTATION MODE (issue #1803): marking a node "hide in presentation
/// mode", and taking the mark off again.
///
/// <para>🚨 <b>This is a display preference, not a permission.</b> The mark is written to the
/// VIEWER's own profile (<see cref="User.HiddenPaths"/>) — never to the node — so it changes
/// nothing for anybody else and nothing about what the marker themselves may read. Marking is
/// allowed on any node the viewer can see, and it is deliberately NOT gated on
/// <see cref="Permission.Update"/> of the target: you are editing your own home page, not that
/// node. The mark only takes effect while <see cref="User.PresentationMode"/> is on
/// (<see cref="PresentationScreen"/>), so turning the mode off is a complete, one-click undo with
/// no restore step — the failure of the "rename it before the demo" workaround #1803 describes.</para>
///
/// <para>The shape deliberately mirrors <see cref="PinLayoutArea"/> — a viewer-scoped list of paths
/// on the viewer's own <c>User</c> node, reached from the node menu — because a user who has
/// pinned something already knows how this works. The write goes through
/// <c>GetMeshNodeStream(path).Update(...)</c>, the one mutation API, so it carries the caller's
/// identity and the owning hub serialises concurrent marks.</para>
/// </summary>
public static class PresentationLayoutArea
{
    /// <summary>Area name for the "hide in presentation mode" action (marks this node's path).</summary>
    public const string HideArea = "HideInPresentation";

    /// <summary>Area name for the "show in presentation mode" action (un-marks this node's path).</summary>
    public const string ShowArea = "ShowInPresentation";

    /// <summary>Area name for the presentation-mode toggle itself (flips the viewer's own mode).</summary>
    public const string ToggleArea = "PresentationMode";

    /// <summary>
    /// The node-menu item: <b>Hide in presentation mode</b> for an unmarked node, <b>Show in
    /// presentation mode</b> for one the viewer already marked. The provider composes the viewer's
    /// live screen, so the entry flips the moment the mark is written — no reload.
    ///
    /// <para>Null for an anonymous viewer (there is no profile to write) and on the viewer's OWN
    /// home root: hiding your own home would empty the page you are reading, and nothing on it
    /// belongs to someone else's screen share.</para>
    /// </summary>
    /// <param name="hubPath">The node the menu is being rendered for.</param>
    /// <param name="viewerId">The viewer's partition key, or null when anonymous.</param>
    /// <param name="screen">The viewer's live screen — its marks decide which of the two items this is.</param>
    public static NodeMenuItemDefinition? GetMenuItem(
        string hubPath, string? viewerId, PresentationScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (string.IsNullOrEmpty(viewerId) || string.IsNullOrEmpty(hubPath))
            return null;
        if (hubPath.Equals(viewerId, StringComparison.OrdinalIgnoreCase))
            return null;

        var marked = screen.MarkedPaths.Contains(hubPath.Trim('/'));
        return marked
            ? new NodeMenuItemDefinition(
                "Show in presentation mode", ShowArea,
                Icon: "👓",
                Order: 13,
                Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ShowArea),
                Tooltip: "Stop hiding this from your own tiles and suggestions while presenting")
                { LabelKey = "menu.showInPresentation", TooltipKey = "menu.showInPresentationTooltip" }
            : new NodeMenuItemDefinition(
                "Hide in presentation mode", HideArea,
                Icon: "🕶️",
                Order: 13,
                Href: MeshNodeLayoutAreas.BuildUrl(hubPath, HideArea),
                Tooltip: "Keep this off your own tiles and suggestions while presentation mode is on")
                { LabelKey = "menu.hideInPresentation", TooltipKey = "menu.hideInPresentationTooltip" };
    }

    /// <summary>Marks this node's path as hidden in the viewer's presentation mode (idempotent).</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Hide(LayoutAreaHost host, RenderingContext _)
        => MarkAndRender(host, hide: true);

    /// <summary>Removes this node's path from the viewer's presentation-mode marks (idempotent).</summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Show(LayoutAreaHost host, RenderingContext _)
        => MarkAndRender(host, hide: false);

    private static IObservable<UiControl?> MarkAndRender(LayoutAreaHost host, bool hide)
    {
        var hubPath = host.Hub.Address.ToString();
        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.OverviewArea);
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        // Captured on the render turn — the write below lands on a later emission, where the
        // ambient context no longer flows.
        var viewerId = accessService.ViewerId();
        var options = host.Hub.JsonSerializerOptions;
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.PresentationLayoutArea");

        if (!PresentationScreenExtensions.IsPersonalViewer(viewerId))
            return Observable.Return<UiControl?>(Message(
                host, "presentation.signInRequiredTitle", "presentation.signInRequiredBody", backHref));

        // The ONE mutation API: the owning user hub serialises every writer, so two marks made from
        // two tabs cannot clobber each other, and the update lambda touches only HiddenPaths.
        host.Hub.GetMeshNodeStream(viewerId!)
            .Update(node =>
            {
                var user = node.ContentAs<User>(options) ?? new User();
                var marks = PresentationPreference.ApplyMark(user.HiddenPaths, hubPath, hide);
                return ReferenceEquals(marks, user.HiddenPaths)
                    ? node
                    : node with { Content = user with { HiddenPaths = marks } };
            })
            .Subscribe(_ => { }, ex => logger?.LogWarning(ex,
                "Presentation mark failed for {Viewer} on {Path}", viewerId, hubPath));

        return Observable.Return<UiControl?>(Message(
            host,
            hide ? "presentation.markedTitle" : "presentation.unmarkedTitle",
            hide ? "presentation.markedBody" : "presentation.unmarkedBody",
            backHref,
            hubPath));
    }

    private static UiControl Message(
        LayoutAreaHost host, string titleKey, string bodyKey, string backHref, string? path = null)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px;")
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: center; margin-bottom: 16px;")
                .WithView(Controls.Button(host.Localize("common.back"))
                    .WithAppearance(Appearance.Lightweight)
                    .WithNavigateToHref(backHref))
                .WithView(Controls.H2(host.Localize(titleKey)).WithStyle("margin: 0;")))
            // Controls.Markdown, never a hand-built HTML string: the framework renders the code span.
            .WithView(Controls.Markdown(path is null
                ? host.Localize(bodyKey)
                : $"{host.Localize(bodyKey)}\n\n`{path}`"));
}
