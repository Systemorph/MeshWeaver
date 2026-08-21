using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;

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
        return host.StreamView<MeshNode>(
            (nodes, _) => BuildRow(nodes.FirstOrDefault(n => n.Path == hubPath), hubPath),
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

    private static UiControl BuildRow(MeshNode? node, string hubPath)
    {
        var title = node?.Name ?? (hubPath.Contains('/') ? hubPath[(hubPath.LastIndexOf('/') + 1)..] : hubPath);

        var row = Controls.Stack
            .WithStyle("position: relative; width: 100%; box-sizing: border-box;")
            .WithView(Controls.Button(title)
                .WithAppearance(Appearance.Stealth)
                .WithNavigateToHref($"/{hubPath}")
                .WithStyle("width: 100%; justify-content: flex-start; text-align: left; " +
                           "padding: 8px 36px 8px 12px; overflow: hidden; " +
                           "text-overflow: ellipsis; white-space: nowrap;"));

        // ✕ overlaid INSIDE the row, top-right — an absolutely-positioned wrapper Stack, the same
        // shape as the pinned card's unpin toggle (a position:absolute style on the button alone
        // stays in flex flow). MarkThreadDone is the canonical, self-subscribing close: it refuses
        // while a round is executing and logs its own failures.
        var close = Controls.Stack
            .WithStyle("position: absolute; top: 6px; right: 6px; z-index: 5;")
            .WithView(Controls.Button("")
                .WithIconStart(FluentIcons.Dismiss())
                .WithAppearance(Appearance.Stealth)
                .WithStyle("min-width: 24px; width: 24px; height: 24px; padding: 0; border-radius: 50%;")
                .WithClickAction(ctx =>
                {
                    ctx.Host.Hub.MarkThreadDone(hubPath, done: true);
                    return Task.CompletedTask;
                }));

        return row.WithView(close);
    }
}
