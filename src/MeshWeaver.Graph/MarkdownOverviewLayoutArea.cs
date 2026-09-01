using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Overview (read-only) layout area for Markdown nodes.
/// Renders a CollaborativeMarkdownControl for the content with annotation support.
/// Uses the standard MeshNodeLayoutAreas action menu and children patterns.
/// </summary>
public static class MarkdownOverviewLayoutArea
{
    /// <summary>
    /// Renders the read-only Overview layout area for a Markdown node, including the
    /// collaborative markdown body, children, approvals, and inline comments.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>An observable stream of the view for the Overview layout area.</returns>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var permissionsStream = host.Hub.GetEffectivePermissions(hubPath);

        // A full-page render shows the node header; an @@ embed carries ?showHeader=false
        // (re-attached client-side from the data-show-header the renderer emits) — suppress the
        // header, comments and side menu. Hidden only when showHeader is explicitly "false".
        var hideHeader = host.Reference.HasParameter("showHeader")
            && string.Equals(host.Reference.GetParameterValue("showHeader"), "false", System.StringComparison.OrdinalIgnoreCase);

        // A module that OWNS this page may supply its own menu (a course lists the WHOLE course, not
        // the branch you are in). An @@ embed never gets a side menu, so its providers are not even
        // asked — no query is opened for a page that cannot show the result.
        var suppliedStream = hideHeader
            ? Observable.Return<NodeNavigation?>(null)
            : SuppliedNavigation(host);

        // The node's PARTITION ROOT, for the header icon's package-mark inheritance (#2075 item 2).
        // Starts null and never gates: a page that inherits nothing renders exactly as before.
        var partitionRootStream = host.Workspace.ObservePartitionRoot(host.Hub.Address.Path);

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissionsStream, host.ObserveChildren("is:main"), suppliedStream,
                partitionRootStream,
                (node, perms, children, supplied, partitionRoot) =>
            {
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                var canEdit = perms.HasFlag(Permission.Update);
                var content = (UiControl)BuildOverview(host, node, canComment, canEdit, hideHeader, partitionRoot);

                // A markdown node with sub-nodes gets a collapsible side menu of them. Skipped for
                // @@ embeds and when there are none; internal satellites (_Access, _Thread, …) excluded.
                if (hideHeader)
                    return (UiControl?)content;
                var subNodes = OrderSubNodes(
                    children.Where(c => !LastSegment(c.Path).StartsWith('_')));
                // Nothing supplied → core's default child list, unchanged — which is why docs and
                // spaces are untouched by this.
                return (UiControl?)(subNodes.Count == 0 && supplied is not { Entries.Count: > 0 }
                    ? content
                    : BuildWithSubNodeNav(host, node, subNodes, content, supplied));
            });
    }

    private static string LastSegment(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    // Sub-nodes for the side menu, ordered by the node's declared Order (nulls last, per the
    // MeshNode.Order contract) then by name — mirroring the graph navigator and every other child
    // list in the codebase. internal for unit testing (InternalsVisibleTo MeshWeaver.Graph.Test).
    internal static List<MeshNode> OrderSubNodes(IEnumerable<MeshNode> children) =>
        children
            .OrderBy(c => c.Order ?? int.MaxValue)
            .ThenBy(c => c.Name ?? c.Id, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Area id of the collapsible left-hand navigation, so consumers and tests can address the menu
    /// without walking anonymous auto-named slots.
    /// </summary>
    public const string NavigationArea = "Navigation";

    /// <summary>
    /// The glyph that used to mark where the reader stands. The rail marks position on the LINK now
    /// (<see cref="NavLinkControl.IsActive"/> — accent bar, background and weight, none of them
    /// colour-only), because prepending a glyph meant rendering that line as bare text, which took
    /// it out of the tree: no icon, no indentation, and visibly detached from the group it belongs
    /// to. Kept as a published constant — consumers pin their own marker against it — and still the
    /// right glyph for any rail that needs a textual one.
    /// </summary>
    public const string CurrentMarker = "▸";

    /// <summary>
    /// Area id of the STANDALONE supplied-navigation menu, registered on every node
    /// (<c>MeshNodeLayoutAreas.AddDefaultLayoutAreas</c>). A page whose layout is NOT the markdown
    /// Overview — a plugin's own lesson area, say — embeds this by name
    /// (<c>new LayoutAreaControl(address, new LayoutAreaReference(SuppliedNavArea))</c>) to get the
    /// SAME whole-course left menu the markdown pages get. Renders null when no provider claims
    /// the page, so embedding it on a non-course page costs an empty area, never an error box.
    /// </summary>
    public const string SuppliedNavArea = "NodeNavigation";

    /// <summary>
    /// The standalone supplied-navigation menu — the nav rail of
    /// <see cref="BuildWithSubNodeNav"/> without the content pane, for layouts that compose their
    /// own page around it. Null (nothing) when no <see cref="INodeNavigationProvider"/> claims the
    /// page: unlike the Overview there is no default-child-list fallback here, because the layouts
    /// that embed this render their own content and only want the course index.
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    public static IObservable<UiControl?> SuppliedNavigationMenu(LayoutAreaHost host, RenderingContext _)
        => SuppliedNavigation(host)
            .Select(supplied => supplied is { Entries.Count: > 0 }
                ? (UiControl?)SuppliedNavigationRail.Render(
                    SuppliedNavigationRail.Plan(supplied, host.Hub.Address.ToString()))
                : null);

    /// <summary>
    /// Asks each registered <see cref="INodeNavigationProvider"/> for this page's navigation, first
    /// one that claims it wins. A provider that declines — synchronously by returning null, or
    /// reactively by emitting null / no entries — leaves core's default child list in place, and one
    /// that throws is logged and skipped: a module's navigation is a nicety, the page is not.
    /// </summary>
    private static IObservable<NodeNavigation?> SuppliedNavigation(LayoutAreaHost host)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.NodeNavigation");
        var streams = new List<IObservable<NodeNavigation?>>();
        foreach (var provider in host.Hub.ServiceProvider.GetServices<INodeNavigationProvider>())
        {
            IObservable<NodeNavigation?>? stream;
            try
            {
                stream = provider.GetNavigation(host);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Navigation provider {Provider} threw for {Path} — using the default child list",
                    provider.GetType().Name, host.Hub.Address);
                continue;
            }
            if (stream is null)
                continue;
            var declined = provider.GetType().Name;
            streams.Add(stream
                // Always emit, immediately: the stream is CombineLatest'd with the node and
                // permission streams, so one that stayed silent would hold the whole page back.
                .StartWith((NodeNavigation?)null)
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex, "Navigation provider {Provider} faulted for {Path} — using the default child list",
                        declined, host.Hub.Address);
                    return Observable.Return<NodeNavigation?>(null);
                }));
        }

        return streams.Count switch
        {
            0 => Observable.Return<NodeNavigation?>(null),
            1 => streams[0],
            _ => Observable.CombineLatest(streams)
                .Select(supplied => supplied.FirstOrDefault(n => n is { Entries.Count: > 0 })),
        };
    }

    /// <summary>
    /// Wraps the page content with the collapsible left-hand NavMenu: the navigation a module
    /// supplied for this page when there is one, otherwise core's default list of the node's own
    /// sub-nodes.
    /// </summary>
    private static UiControl BuildWithSubNodeNav(
        LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> subNodes, UiControl content,
        NodeNavigation? supplied = null)
    {
        // A module that OWNS this page supplied the whole index — rendered by the shared rail, whose
        // shape is pinned by SuppliedNavigationRail's tests. Nothing supplied → core's default list
        // of the node's own children, unchanged, so docs and spaces are untouched by this.
        // The nav renders NON-collapsible: the Splitter below owns both resize and collapse.
        var nav = supplied is { Entries.Count: > 0 }
            ? SuppliedNavigationRail.Render(
                SuppliedNavigationRail.Plan(supplied, host.Hub.Address.ToString()), collapsible: false)
            : Controls.NavMenu
                .WithSkin(s => s.WithCollapsible(false))
                .WithNavGroup(BuildChildrenGroup(host, node, subNodes));

        // A SPLITTER, not a Stack — the same idiom as the Settings pages, and for the same reason:
        // the divider is a real, draggable resize handle (FluentMultiSplitter, client-side), and
        // its collapse arrow replaces the rail's own toggle. A fixed-width Stack answered "the
        // index is page-wide" but not "let me choose how wide" (user report 2026-08-23). Styles
        // mirror SettingsLayoutArea: the splitter takes its content height and the page scrolls
        // as a whole.
        return Controls.Splitter
            .WithStyle("height: auto; flex: 1 0 auto;")
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%"))
            // WithId, not WithArea: the addressable area path is {context}/{Id} — PrepareRendering
            // OVERWRITES Area from Id, so naming Area here would leave the pane on an auto id and
            // break every consumer addressing …/Navigation (Copilot on #2098).
            .WithView(nav, x => x.WithId(NavigationArea)
                .AddSkin(new SplitterPaneSkin().WithSize("260px").WithMin("180px").WithMax("480px").WithCollapsible(true)))
            .WithView(
                Controls.Stack.WithStyle("min-width: 0; padding-left: 16px;").WithView(content),
                skin => skin.WithSize("*"));
    }

    // Core's default: the node's own children, one flat list.
    private static NavGroupControl BuildChildrenGroup(
        LayoutAreaHost host, MeshNode? node, IReadOnlyList<MeshNode> subNodes)
    {
        var group = new NavGroupControl(node?.Name ?? host.Localize("nav.contents"))
            .WithSkin(s => s.WithExpanded(true));
        // The heading shows the DOCUMENT's icon, same resolution as its children below — without it
        // the doc title was the one line of the index with no icon.
        if (MeshNodeImageHelper.ResolveNodeIcon(node) is { } groupIcon)
            group = group.WithIcon(groupIcon);
        foreach (var child in subNodes)
        {
            var href = $"/{child.Path}";
            var icon = MeshNodeImageHelper.ResolveNodeIcon(child);
            group = icon is null
                ? group.WithView(new NavLinkControl(child.Name ?? child.Id, null, href))
                : group.WithView(new NavLinkControl(child.Name ?? child.Id, icon, href));
        }
        return group;
    }

    // partitionRoot: the node's partition root, when the caller holds it — the header icon inherits
    // its package mark from there (#2075 item 2). Null keeps the NodeType glyph.
    private static UiControl BuildOverview(
        LayoutAreaHost host, MeshNode? node, bool canComment, bool canEdit, bool hideHeader,
        MeshNode? partitionRoot = null)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var rawContent = GetMarkdownContent(node);

        // Markdown pages render full width (max-width: 100%), not the centered 1200px reading column.
        var container = Controls.Stack.WithWidth("100%").WithStyle(MeshNodeLayoutAreas.GetContainerStyle(host, maxWidthOverride: "100%"));

        // Standard header with title/icon (skipped for @@ embeds)
        if (!hideHeader)
            container = container.WithView(
                MeshNodeLayoutAreas.BuildHeader(host, node, false, partitionRoot));

        // Read-only markdown content — the CollaborativeMarkdownControl is added as
        // a DIRECT child of `container` so agents and tests can locate it without
        // walking through an intermediate Stack wrapper. An @@ embed (hideHeader) renders the
        // body WITHOUT collaboration UI — commenting happens on the embedded node's own page.
        container = container.WithView(BuildMarkdownReadView(host, nodePath, rawContent, canComment, canEdit, hideAnnotations: hideHeader));

        // No hardcoded children section: a node page is a markdown space — children (or any other
        // content) are injected INLINE with the @@(query) operator, never auto-listed (that doubled
        // the children on every page that already referenced them).

        // Approvals section — DELEGATED to the Approvals PACKAGE when it is installed. Approvals
        // are node-native now: nothing is registered onto this hub, so the platform cannot ask a
        // configuration marker whether they exist. It asks the mesh instead, through the same
        // bounded index probe the Versions page uses (#737), and the package's own area decides
        // whether THIS document has anything to show. No package ⇒ no section, no cost.
        container = container.WithView(ApprovalsSection(host, nodePath));

        // Standard inline comments section (if comments enabled)
        if (!hideHeader && host.Hub.Configuration.HasComments())
        {
            container = container.WithView(MeshNodeLayoutAreas.BuildInlineCommentsSection(host));
        }

        return container;
    }

    /// <summary>The Approvals package's shared desk — the instance that serves every document.</summary>
    internal const string ApprovalDeskPath = "Approvals/Workspace";

    /// <summary>The desk area listing one document's approvals.</summary>
    internal const string ApprovalsArea = "Approvals";

    /// <summary>
    /// The document's approvals, rendered by the Approvals package when that package is on the
    /// mesh — an empty stack otherwise. The document path rides as the layout-area REFERENCE, which
    /// is how one desk instance serves every document.
    /// </summary>
    private static IObservable<UiControl?> ApprovalsSection(LayoutAreaHost host, string nodePath)
        => PluginSurfaceProbe
            .Exists(host.Hub.ServiceProvider.GetService<IMeshService>(), ApprovalDeskPath)
            .Select(installed => installed
                ? (UiControl?)Controls.LayoutArea(ApprovalDeskPath, ApprovalsArea, nodePath)
                    .WithShowProgress(false)
                : Controls.Stack);

    /// <summary>
    /// Returns the actual markdown body control (a <see cref="CollaborativeMarkdownControl"/>
    /// when there is content, an HTML placeholder when empty) — NOT wrapped in an extra
    /// Stack. The caller is expected to add this directly to its container so consumers
    /// can identify the markdown body via <c>OfType&lt;CollaborativeMarkdownControl&gt;</c>
    /// without skipping a wrapper layer.
    /// </summary>
    private static UiControl BuildMarkdownReadView(
        LayoutAreaHost host, string nodePath, string rawContent, bool canComment, bool canEdit, bool hideAnnotations)
    {
        if (!string.IsNullOrWhiteSpace(rawContent))
        {
            return new CollaborativeMarkdownControl()
                .WithValue(rawContent)
                .WithNodePath(nodePath)
                .WithHubAddress(host.Hub.Address.ToString())
                .WithCanComment(canComment)
                .WithCanEdit(canEdit)
                .WithHideAnnotations(hideAnnotations);
        }
        return Controls.Html("<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No content yet. Use the menu to start editing.</p>");
    }

    /// <summary>
    /// Renders the Thumbnail layout area — a compact card representation of the Markdown node.
    /// </summary>
    /// <param name="host">The layout area host rendering the area.</param>
    /// <param name="_">The rendering context for the area.</param>
    /// <returns>The view for the Thumbnail layout area.</returns>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return Controls.Stack
            .WithView((h, c) => host.Workspace.GetMeshNodeStream()
                .Select(node => MeshNodeThumbnailControl.FromNode(node, hubPath)));
    }

    /// <summary>
    /// Extracts markdown content from a MeshNode.
    /// </summary>
    public static string GetMarkdownContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

        if (node.Content is string stringContent)
            return stringContent;

        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                return jsonElement.GetString() ?? string.Empty;

            if (jsonElement.TryGetProperty("$type", out var typeProperty))
            {
                var typeName = typeProperty.GetString();
                if ((typeName == "MarkdownDocument" || typeName == "MarkdownContent") && jsonElement.TryGetProperty("content", out var contentProperty))
                {
                    return contentProperty.GetString() ?? string.Empty;
                }
            }

            // Fallback: try "content" property without $type check
            if (jsonElement.TryGetProperty("content", out var fallbackContent) && fallbackContent.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return fallbackContent.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Writes <paramref name="markdown"/> back into <paramref name="node"/>, PRESERVING the content
    /// shape <see cref="GetMarkdownContent"/> reads: a typed <see cref="MarkdownContent"/> keeps its
    /// front-matter metadata (Authors / Tags / Thumbnail / Abstract), a plain string stays a string,
    /// and a <c>JsonElement</c> frame (the shape content arrives in over a query / remote seam) is
    /// materialised first. Replacing the whole content with a fresh <c>MarkdownContent</c> would
    /// silently drop that metadata.
    /// <para>Throws when the node does not hold markdown — a silent no-op reads to the caller as a
    /// lost write.</para>
    /// </summary>
    public static MeshNode WithMarkdownContent(MeshNode node, string markdown, JsonSerializerOptions options)
    {
        switch (node.Content)
        {
            case MarkdownContent typedContent:
                return node with { Content = typedContent with { Content = markdown } };
            case string:
                return node with { Content = markdown };
            case JsonElement { ValueKind: JsonValueKind.String }:
                return node with { Content = markdown };
            case JsonElement { ValueKind: JsonValueKind.Object } element:
            {
                var isMarkdownShaped = element.TryGetProperty("$type", out var typeProperty)
                    ? typeProperty.GetString() is "MarkdownContent" or "MarkdownDocument"
                    : element.TryGetProperty("content", out var probe) && probe.ValueKind == JsonValueKind.String;
                if (isMarkdownShaped && node.ContentAs<MarkdownContent>(options) is { } materialised)
                    return node with { Content = materialised with { Content = markdown } };
                break;
            }
        }

        throw new InvalidOperationException(
            $"'{node.Path}' does not hold markdown content — cannot write markdown to it.");
    }
}
