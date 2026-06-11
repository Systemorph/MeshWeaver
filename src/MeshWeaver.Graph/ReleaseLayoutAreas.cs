using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Layout views for <c>Release</c> nodes (<see cref="NodeTypeRelease"/> content at
/// <c>{nodeTypePath}/Release/{version}</c>). The Overview is the release detail page:
/// status, version, release notes, a link back to the owning NodeType and the compile
/// activity, and — the navigation payload — the source and test files exactly as they
/// were at release time (<see cref="NodeTypeRelease.SourceVersions"/> /
/// <see cref="NodeTypeRelease.TestVersions"/>), each linking to the file and its
/// version history.
/// </summary>
public static class ReleaseLayoutAreas
{
    /// <summary>
    /// Registers the Release detail view as the node's Overview (the standard landing
    /// area every navigation surface links to).
    /// </summary>
    public static MessageHubConfiguration AddReleaseViews(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithDefaultArea(MeshNodeLayoutAreas.OverviewArea)
            .WithView(MeshNodeLayoutAreas.OverviewArea, Overview));

    /// <summary>
    /// Release detail page, bound to the Release node's own stream so status/notes
    /// updates re-render live.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext ctx)
        => host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node == null)
                    return (UiControl?)Controls.Body("Loading…");
                var release = node.Content as NodeTypeRelease;

                var stack = Controls.Stack.WithWidth("100%")
                    .WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host)
                        + " padding-top: 8px; padding-bottom: 32px; gap: 8px; overflow-y: auto;");
                stack = stack.WithView(MeshNodeLayoutAreas.BuildHeader(host, node, false));

                if (release == null)
                {
                    return (UiControl?)stack.WithView(Controls.Body(
                            "This node carries no readable release payload.")
                        .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;"));
                }

                stack = stack.WithView(BuildSummaryPanel(release));

                if (!string.IsNullOrWhiteSpace(release.Notes?.Content))
                {
                    stack = stack.WithView(SectionTitle("Release notes"));
                    stack = stack.WithView(Controls.Markdown(release.Notes!.Content));
                }

                stack = stack.WithView(BuildCodeSection("Sources", release.SourceVersions));
                stack = stack.WithView(BuildCodeSection("Tests", release.TestVersions));

                return (UiControl?)stack;
            });

    private static UiControl SectionTitle(string title)
        => Controls.H3(title).WithStyle(
            "margin: 24px 0 6px 0; padding-bottom: 6px; border-bottom: 1px solid var(--neutral-stroke-divider);");

    /// <summary>
    /// Status badge + the release's key facts (version, created, framework, owning
    /// NodeType, compile activity, durable assembly reference).
    /// </summary>
    private static UiControl BuildSummaryPanel(NodeTypeRelease release)
    {
        var failed = string.Equals(release.Status, "Failed", StringComparison.OrdinalIgnoreCase);
        var statusColor = failed ? "var(--error)" : "var(--accent-fill-rest)";
        var statusLabel = failed ? "Failed" : "Succeeded";

        var stack = Controls.Stack.WithWidth("100%")
            .WithStyle($"padding: 12px 16px; background: var(--neutral-layer-2); border-radius: 6px; " +
                       $"border-left: 3px solid {statusColor}; gap: 4px;");

        stack = stack.WithView(Controls.Html(
            $"<span style=\"font-weight: 600; padding: 2px 10px; border-radius: 12px; " +
            $"background: {statusColor}20; color: {statusColor}; font-size: 0.85rem;\">" +
            $"{System.Net.WebUtility.HtmlEncode(statusLabel)}</span>"));

        stack = stack.WithView(InfoRow("Version", release.Version ?? release.Release));
        stack = stack.WithView(InfoRow("Created", $"{release.CreatedAt:g}"));
        stack = stack.WithView(InfoRow("Framework", release.FrameworkVersion));
        stack = stack.WithView(InfoRowLink("NodeType", release.NodeTypePath, $"/{release.NodeTypePath}"));
        if (!string.IsNullOrEmpty(release.CompilationActivityPath))
            stack = stack.WithView(InfoRowLink("Compile log", release.CompilationActivityPath!,
                $"/{release.CompilationActivityPath}"));
        if (!string.IsNullOrEmpty(release.AssemblyCollection) && !string.IsNullOrEmpty(release.AssemblyContentPath))
            stack = stack.WithView(InfoRow("Assembly",
                $"{release.AssemblyCollection}/{release.AssemblyContentPath}"));

        return stack;
    }

    private static UiControl InfoRow(string label, string value)
        => Controls.Html(
            $"<div style=\"display: flex; gap: 8px; font-size: 0.9rem;\">" +
            $"<span style=\"min-width: 110px; font-weight: 600;\">{System.Net.WebUtility.HtmlEncode(label)}</span>" +
            $"<span style=\"word-break: break-all;\">{System.Net.WebUtility.HtmlEncode(value)}</span></div>");

    private static UiControl InfoRowLink(string label, string text, string href)
        => Controls.Html(
            $"<div style=\"display: flex; gap: 8px; font-size: 0.9rem;\">" +
            $"<span style=\"min-width: 110px; font-weight: 600;\">{System.Net.WebUtility.HtmlEncode(label)}</span>" +
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(href)}\" style=\"word-break: break-all;\">" +
            $"{System.Net.WebUtility.HtmlEncode(text)}</a></div>");

    /// <summary>
    /// The navigable file list for one bucket (Sources or Tests): each row links to
    /// the Code node, shows the as-of timestamp the release captured
    /// (<c>LastModified.UtcTicks</c> at compile time), and links to the node's
    /// version history so the matching snapshot is one click away.
    /// </summary>
    private static UiControl BuildCodeSection(string title, IReadOnlyDictionary<string, long>? versions)
    {
        var stack = Controls.Stack.WithWidth("100%")
            .WithView(SectionTitle(title));

        if (versions == null || versions.Count == 0)
        {
            return stack.WithView(Controls.Body(
                    $"No {title.ToLowerInvariant()} captured on this release.")
                .WithStyle("color: var(--neutral-foreground-hint); font-style: italic; padding: 4px 0;"));
        }

        foreach (var (path, ticks) in versions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var asOf = new DateTimeOffset(ticks, TimeSpan.Zero);
            var historyHref = MeshNodeLayoutAreas.BuildUrl(path, MeshNodeLayoutAreas.VersionsArea);
            stack = stack.WithView(Controls.Html(
                $"<div style=\"display: flex; align-items: center; gap: 12px; padding: 4px 0; font-size: 0.9rem;\">" +
                $"<a href=\"/{System.Net.WebUtility.HtmlEncode(path)}\" style=\"flex: 1; word-break: break-all;\">" +
                $"{System.Net.WebUtility.HtmlEncode(path)}</a>" +
                $"<span style=\"color: var(--neutral-foreground-hint);\">as of {asOf:g}</span>" +
                $"<a href=\"{System.Net.WebUtility.HtmlEncode(historyHref)}\" " +
                $"style=\"color: var(--neutral-foreground-hint);\">history</a></div>"));
        }

        return stack;
    }
}
