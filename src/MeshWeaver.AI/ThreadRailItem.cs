using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// One row of the Threads-app vertical rail (the GitHub-Copilot-style thread list): the thread's
/// title as a full-width navigation button, plus an ✕ overlay that CLOSES the thread via the
/// canonical <see cref="HubThreadExtensions.MarkThreadDone"/> — the row leaves the rail reactively
/// (the rail's query excludes <c>content.status:Done</c>) while the thread stays searchable and
/// reopenable; closing is never deleting. Registered on every thread hub as
/// <see cref="ThreadNodeType.RailItemArea"/> and consumed as a <c>MeshSearch.ItemArea</c>.
/// Mirrors the PinLayoutArea.PinnedThumbnail overlay pattern (icon-only glyph, no translated
/// label — the i18n-preferred shape).
/// </summary>
public static class ThreadRailItem
{
    /// <summary>The rail-row renderer — runs on the thread's own hub, so the ✕ writes locally.</summary>
    public static IObservable<UiControl?> View(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var locale = host.ViewerLocale();
        return host.StreamView<MeshNode>(
            (nodes, _) => BuildRow(nodes.FirstOrDefault(n => n.Path == hubPath), hubPath, locale),
            BuildSkeleton(hubPath));
    }

    /// <summary>A row-height loading skeleton so the rail doesn't jump while a thread hub warms up.</summary>
    private static UiControl BuildSkeleton(string hubPath)
    {
        var shortName = hubPath.Contains('/') ? hubPath[(hubPath.LastIndexOf('/') + 1)..] : hubPath;
        return Controls.Stack
            .WithStyle("width: 100%; min-height: 40px; padding: 8px 12px; box-sizing: border-box; " +
                       "display: flex; justify-content: center; opacity: 0.6;")
            .WithView(Controls.Markdown($"_{shortName}…_"));
    }

    private static UiControl BuildRow(MeshNode? node, string hubPath, string? locale)
    {
        var title = node?.Name ?? (hubPath.Contains('/') ? hubPath[(hubPath.LastIndexOf('/') + 1)..] : hubPath);

        // Title and ✕ are SIBLINGS in one flex row — never an overlay. The previous
        // absolutely-positioned ✕ sat on top of the full-width navigation button and its click
        // reached the NAV surface instead of closing the thread (the "✕ navigates" bug); as a
        // sibling, each button owns its own hit area unambiguously.
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("width: 100%; box-sizing: border-box; align-items: center; gap: 4px;")
            .WithView(Controls.Button(title)
                .WithAppearance(Appearance.Stealth)
                .WithNavigateToHref($"/{hubPath}")
                .WithStyle("flex: 1 1 auto; min-width: 0; justify-content: flex-start; " +
                           "text-align: left; padding: 8px 8px 8px 12px; overflow: hidden; " +
                           "text-overflow: ellipsis; white-space: nowrap;"))
            // MarkThreadDone is the canonical, self-subscribing close: the thread leaves the rail
            // reactively (the rail's query excludes Done) while staying searchable/reopenable. It
            // refuses while a round is executing and logs its own failures.
            .WithView(Controls.Button("")
                .WithIconStart(FluentIcons.Dismiss())
                // Label = the button's aria-label/tooltip (ButtonView): the icon-only ✕ still
                // needs an accessible, localized name for screen readers.
                .WithLabel(LocalizationCatalog.Get("thread.close", locale))
                .WithAppearance(Appearance.Stealth)
                .WithStyle("flex: 0 0 auto; min-width: 24px; width: 24px; height: 24px; " +
                           "padding: 0; border-radius: 50%;")
                .WithClickAction(ctx =>
                {
                    ctx.Host.Hub.MarkThreadDone(hubPath, done: true);
                    return Task.CompletedTask;
                }));
    }
}
