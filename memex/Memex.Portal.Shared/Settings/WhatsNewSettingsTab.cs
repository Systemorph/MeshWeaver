using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Global settings "What's New" tab — lists the platform release-note entries shipped as documentation
/// nodes under <see cref="WhatsNewNamespace"/> (one node per entry, produced by the <c>/pullrequest</c>
/// skill and shipped with the build), newest first. Each entry links to its full note in the normal
/// documentation view. Ungated: visible to every user. Reactive + pure <see cref="Controls"/> (no
/// hand-built HTML), and it only LISTS children (a valid query use) — the note content is read by the
/// doc view when opened, never from the lagging query index (CQRS rule).
/// </summary>
public static class WhatsNewSettingsTab
{
    /// <summary>The tab id under <c>/_settings/GlobalSettings</c>.</summary>
    public const string TabId = "WhatsNew";

    /// <summary>The documentation namespace holding per-entry release-note nodes (served under <c>Doc/</c>).</summary>
    public const string WhatsNewNamespace = "Doc/WhatsNew";

    /// <summary>Registers the What's New tab with the global settings menu (ungated).</summary>
    public static MessageHubConfiguration AddWhatsNewSettingsTab(this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>(new[]
        {
            new GlobalSettingsMenuItemDefinition(
                Id: TabId,
                Label: "What's New",
                ContentBuilder: BuildContent,
                Icon: FluentIcons.Sparkle(),
                Order: 910)
        });

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("What's New").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "Recent changes shipped in the platform, newest first. Open an entry to read the full note."));

        // Live list of entry nodes under Doc/WhatsNew. Listing children is a valid query use; the
        // entry content is rendered by the doc view when the link is opened (never read from the
        // lagging query index). Sorted newest-first by node path — entries are named with a leading
        // ISO date, so path order is chronological.
        stack = stack.WithView((h, _) =>
            h.Hub.GetQuery("whatsnew-list", $"path:{WhatsNewNamespace} scope:children")
            .Select(nodes => (UiControl?)Controls.Markdown(RenderList(nodes)))
            .Catch<UiControl?, Exception>(ex => Observable.Return(
                (UiControl?)Controls.Markdown($"_Could not load What's New: {ex.Message}_")))
            .StartWith((UiControl?)Controls.Markdown("_Loading…_")));

        return stack;
    }

    /// <summary>Markdown bulleted list of entry links, newest first; a friendly note when empty.</summary>
    private static string RenderList(IEnumerable<MeshNode> nodes)
    {
        var entries = (nodes ?? [])
            .Where(n => n is not null)
            .OrderByDescending(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count == 0)
            return "_No release notes yet._";

        return string.Join("\n", entries.Select(n =>
            $"- [{n.Name ?? LastSegment(n.Path)}](/{n.Path})"));
    }

    private static string LastSegment(string path)
        => string.IsNullOrEmpty(path) ? path : path[(path.LastIndexOf('/') + 1)..];
}
