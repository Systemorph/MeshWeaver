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

        // Core's default when nothing is supplied: the tree under the page's index root
        // (DefaultNodeNavigation) — the same index on every page of a document tree, the reader's
        // position marked. Not opened for an embed either.
        var defaultStream = hideHeader
            ? Observable.Return<NodeNavigation?>(null)
            : DefaultNodeNavigation.Observe(host);

        // The node's PARTITION ROOT, for the header icon's package-mark inheritance (#2075 item 2).
        // Starts null and never gates: a page that inherits nothing renders exactly as before.
        var partitionRootStream = host.Workspace.ObservePartitionRoot(host.Hub.Address.Path);

        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(permissionsStream, defaultStream, suppliedStream,
                partitionRootStream,
                (node, perms, defaultNavigation, supplied, partitionRoot) =>
            {
                var canComment = perms.HasFlag(Permission.Comment) || perms.HasFlag(Permission.Update);
                var canEdit = perms.HasFlag(Permission.Update);
                var content = (UiControl)BuildOverview(host, node, canComment, canEdit, hideHeader, partitionRoot);

                // A markdown page in a tree gets the tree's index beside it. Skipped for @@ embeds
                // and when there is nothing to index (no sub-nodes, no siblings). A module's own
                // index wins over the default; the default keeps docs and spaces looking the same
                // on their root page and gives every sub-page the index it used to lose.
                if (hideHeader)
                    return (UiControl?)content;
                var navigation = supplied is { Entries.Count: > 0 } ? supplied : defaultNavigation;
                return (UiControl?)(navigation is { Entries.Count: > 0 }
                    ? BuildWithSubNodeNav(host, content, navigation)
                    : content);
            });
    }

    // Sub-nodes for the side menu, ordered by the node's declared Order (nulls last, per the
    // MeshNode.Order contract) then by name — mirroring the graph navigator and every other child
    // list in the codebase. Every level of DefaultNodeNavigation orders with this. internal for
    // unit testing (InternalsVisibleTo MeshWeaver.Graph.Test).
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
    /// CSS class core stamps on the index pane, so the portal's shell can recognise it: the shell
    /// hosts the pane's collapse toggle in its top bar (beside the logo, the way a browser's sidebar
    /// button sits) and persists the collapsed state across pages. A shell that does not know the
    /// class leaves the splitter bar's own collapse chevron in charge.
    /// </summary>
    public const string NavigationPaneClass = "nav-rail-pane";

    /// <summary>
    /// Wraps the page content with the left-hand index: the navigation a module supplied for this
    /// page when there is one, otherwise core's default tree (<see cref="DefaultNodeNavigation"/>).
    /// Either way the same rail renders it, so a course and a document tree read alike.
    /// </summary>
    private static UiControl BuildWithSubNodeNav(LayoutAreaHost host, UiControl content, NodeNavigation navigation)
    {
        // Rendered by the shared rail, whose shape is pinned by SuppliedNavigationRail's tests.
        // The nav renders NON-collapsible: the Splitter below owns both resize and collapse.
        var nav = SuppliedNavigationRail.Render(
            SuppliedNavigationRail.Plan(navigation, host.Hub.Address.ToString()), collapsible: false);

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
            .WithView(nav, x => x.WithId(NavigationArea).WithClass(NavigationPaneClass)
                .AddSkin(new SplitterPaneSkin().WithSize("260px").WithMin("180px").WithMax("480px").WithCollapsible(true)))
            .WithView(
                Controls.Stack.WithStyle("min-width: 0; padding-left: 16px;").WithView(content),
                skin => skin.WithSize("*"));
    }

    // partitionRoot: the node's partition root, when the caller holds it — the header icon inherits
    // its package mark from there (#2075 item 2). Null keeps the NodeType glyph.
    private static UiControl BuildOverview(
        LayoutAreaHost host, MeshNode? node, bool canComment, bool canEdit, bool hideHeader,
        MeshNode? partitionRoot = null)
    {
        var nodePath = node?.Path ?? host.Hub.Address.ToString();
        var read = ReadMarkdownContent(node);

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
        container = container.WithView(BuildMarkdownReadView(host, nodePath, read, canComment, canEdit, hideAnnotations: hideHeader));

        // No hardcoded children section: a node page is a markdown space — children (or any other
        // content) are injected INLINE with the @@(query) operator, never auto-listed (that doubled
        // the children on every page that already referenced them).

        // Approvals section — DELEGATED to the Approvals PACKAGE when it is installed. Approvals
        // are node-native now: nothing is registered onto this hub, so the platform cannot ask a
        // configuration marker whether they exist. It asks the mesh instead, through the same
        // bounded index probe the Versions page uses (#737), and the package's own area decides
        // whether THIS document has anything to show. No package ⇒ no section, no cost.
        container = container.WithView(ApprovalsSection(host, nodePath));

        // Signatures section — the same delegation to the e-Signature PACKAGE (DeepSign / Skribble,
        // MeshWeaver.Plugins#1682): its shared desk renders this document's signature block — signed
        // requests as signatures, open ones with their Sign button — and decides whether there is
        // anything to show. Same bounded index probe, same reference-id hand-over. No package ⇒ no
        // section, no cost.
        container = container.WithView(SignaturesSection(host, nodePath));

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

    /// <summary>The e-Signature package's shared desk — the instance that serves every document.</summary>
    internal const string SignatureDeskPath = "DeepSign/Workspace";

    /// <summary>The desk area rendering one document's signature block.</summary>
    internal const string SignatureArea = "Signature";

    /// <summary>
    /// The document's signatures, rendered by the e-Signature package when that package is on the
    /// mesh — an empty stack otherwise, never an "area not found" card. The document path rides as
    /// the layout-area REFERENCE, exactly as the approvals section hands it over.
    /// </summary>
    private static IObservable<UiControl?> SignaturesSection(LayoutAreaHost host, string nodePath)
        => PluginSurfaceProbe
            .Exists(host.Hub.ServiceProvider.GetService<IMeshService>(), SignatureDeskPath)
            .Select(installed => installed
                ? (UiControl?)Controls.LayoutArea(SignatureDeskPath, SignatureArea, nodePath)
                    .WithShowProgress(false)
                : Controls.Stack);

    /// <summary>
    /// Returns the actual markdown body control (a <see cref="CollaborativeMarkdownControl"/>
    /// when there is content, the authoring placeholder when the node is empty, and a DIAGNOSTIC
    /// when its content is present but unreadable) — NOT wrapped in an extra
    /// Stack. The caller is expected to add this directly to its container so consumers
    /// can identify the markdown body via <c>OfType&lt;CollaborativeMarkdownControl&gt;</c>
    /// without skipping a wrapper layer.
    ///
    /// <para>🚨 <b>The empty state and the unreadable state must not be the same view</b> (#4600).
    /// "No content yet. Use the menu to start editing." is an INVITATION, and rendering it over a
    /// document whose bytes are still in the store invites the one action that destroys them: the
    /// reader edits, and the first save replaces the payload nobody could read with whatever they
    /// typed. The unreadable state therefore says what is stored and does not invite anything.</para>
    /// </summary>
    internal static UiControl BuildMarkdownReadView(
        LayoutAreaHost host, string nodePath, MarkdownRead read, bool canComment, bool canEdit, bool hideAnnotations)
    {
        if (read.State == MarkdownContentState.Present && !string.IsNullOrWhiteSpace(read.Text))
        {
            return new CollaborativeMarkdownControl()
                .WithValue(read.Text)
                .WithNodePath(nodePath)
                .WithHubAddress(host.Hub.Address.ToString())
                .WithCanComment(canComment)
                .WithCanEdit(canEdit)
                .WithHideAnnotations(hideAnnotations);
        }

        if (read.State == MarkdownContentState.Unreadable)
        {
            // Warning, not Debug: this is a node whose authored text is in the store and reachable
            // by nobody, and until it is repaired every render of the page is the only place that
            // says so. Bounded by human page views. The read seam logs the SAME node only when its
            // payload carries a `$type` — a discriminator-less one (the measured case) returns from
            // MeshNodeTypeSource.ResolveJsonElementContent before any line is written, so without
            // this there is nothing at all.
            // A LOCAL and an explicit null check, not `factory?.CreateLogger(t).LogWarning(…)`:
            // LogWarning is an EXTENSION method, so whether `?.` short-circuits past it is a
            // question a reader should not have to answer (review on #4626). A host without a
            // logger factory is a minimal test host, and it must render the notice all the same.
            var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(MarkdownOverviewLayoutArea));
            logger?.LogWarning(
                "Markdown node '{Path}' has content that no reader can interpret — stored "
                + "member(s)/kind: {Shape}. The page shows a diagnostic instead of the "
                + "authoring placeholder, because inviting an edit over unreadable content is "
                + "how the stored text gets overwritten. Systemorph/MeshWeaver#4600.",
                nodePath, read.Shape);

            // ONE control, not a Stack of two: the notice has to be legible as a whole, and a
            // consumer (or a test) that asks what this page says must get the sentence rather than
            // two anonymous sub-area references. Same idiom as the unresolved-content-type notice.
            return Controls.Markdown(
                $"**{host.Localize("ui.markdownContentUnreadable")}**\n\n"
                + host.Localize("ui.markdownContentUnreadableHint", read.Shape));
        }

        return Controls.Body(host.Localize("ui.noContentYet"))
            .WithStyle("color: var(--neutral-foreground-hint); font-style: italic;");
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
    /// How a node's content answered <see cref="ReadMarkdownContent"/>.
    /// </summary>
    public enum MarkdownContentState
    {
        /// <summary>There is no content — the node is genuinely empty and inviting an edit is right.</summary>
        Absent,

        /// <summary>The markdown was read.</summary>
        Present,

        /// <summary>
        /// Content is THERE and nothing here can interpret it — a payload no reader can
        /// materialise. Distinct from <see cref="Absent"/> on purpose: see
        /// <see cref="ReadMarkdownContent"/>.
        /// </summary>
        Unreadable,
    }

    /// <summary>The outcome of reading a node's markdown.</summary>
    /// <param name="State">Absent / Present / Unreadable.</param>
    /// <param name="Text">The markdown, empty unless <paramref name="State"/> is Present.</param>
    /// <param name="Shape">
    /// For <see cref="MarkdownContentState.Unreadable"/>, the actual shape of the stored payload —
    /// the members it carries, or its JSON kind — so a diagnostic and a log line can SAY what is
    /// there instead of the reader guessing. Empty otherwise.
    /// </param>
    public record MarkdownRead(MarkdownContentState State, string Text, string Shape);

    /// <summary>
    /// Reads a node's markdown, distinguishing "there is none" from "there is some and nothing here
    /// can interpret it".
    ///
    /// <para>🚨 <b>Those two were the same answer, and that is the defect</b>
    /// (Systemorph/MeshWeaver#4600). <see cref="GetMarkdownContent"/> returned
    /// <see cref="string.Empty"/> from its final fall-through for every payload shape it does not
    /// recognise, so the caller rendered the authoring placeholder — <i>"No content yet. Use the
    /// menu to start editing."</i> — over a full document, with nothing logged. Measured on a
    /// customer workspace 2026-09-17: a <c>Markdown</c> node whose v1 content was
    /// <c>{"markdown": "# … Company Profile\n…"}</c>, a member <see cref="MarkdownContent"/> does not
    /// declare, rendered as an empty node from birth. A reader who believes that placeholder starts
    /// editing, and the first save overwrites the text that was still there.</para>
    ///
    /// <para><b>Only a raw <see cref="JsonElement"/> can be Unreadable.</b> Typed CLR content that
    /// is not a markdown shape reads as Absent exactly as it always did — this area is registered on
    /// the <c>Markdown</c> NodeType's hub alone, but <see cref="GetMarkdownContent"/> is called from
    /// the version diff and the notebook view for other shapes too, and calling a node's own typed
    /// content "unreadable" would be false. A JsonElement is the shape content has when NOTHING in
    /// the process could type it, which is precisely the claim being made.</para>
    /// </summary>
    /// <param name="node">The node to read; may be null.</param>
    /// <returns>The read outcome.</returns>
    public static MarkdownRead ReadMarkdownContent(MeshNode? node)
    {
        if (node?.Content == null)
            return new MarkdownRead(MarkdownContentState.Absent, string.Empty, string.Empty);

        if (node.Content is MarkdownContent markdownContent)
            return Text(markdownContent.Content);

        if (node.Content is string stringContent)
            return Text(stringContent);

        if (node.Content is JsonElement jsonElement)
            return FromJson(jsonElement);

        // Typed content of some other shape: there is no markdown here, and saying so is honest.
        return new MarkdownRead(MarkdownContentState.Absent, string.Empty, string.Empty);

        static MarkdownRead Text(string? value) =>
            string.IsNullOrEmpty(value)
                ? new MarkdownRead(MarkdownContentState.Absent, string.Empty, string.Empty)
                : new MarkdownRead(MarkdownContentState.Present, value, string.Empty);
    }

    private static MarkdownRead FromJson(JsonElement jsonElement)
    {
        switch (jsonElement.ValueKind)
        {
            case JsonValueKind.String:
                var raw = jsonElement.GetString();
                return string.IsNullOrEmpty(raw)
                    ? new MarkdownRead(MarkdownContentState.Absent, string.Empty, string.Empty)
                    : new MarkdownRead(MarkdownContentState.Present, raw, string.Empty);
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return new MarkdownRead(MarkdownContentState.Absent, string.Empty, string.Empty);
            case JsonValueKind.Object:
                break;
            default:
                // A number, a boolean, an array: content is there and it is not markdown.
                return new MarkdownRead(
                    MarkdownContentState.Unreadable, string.Empty,
                    jsonElement.ValueKind.ToString().ToLowerInvariant());
        }

        if (jsonElement.TryGetProperty("$type", out var typeProperty))
        {
            var typeName = typeProperty.GetString();
            if ((typeName == "MarkdownDocument" || typeName == "MarkdownContent")
                && jsonElement.TryGetProperty("content", out var contentProperty))
                return Content(contentProperty);
        }

        // Fallback: try "content" property without $type check
        if (jsonElement.TryGetProperty("content", out var fallbackContent)
            && fallbackContent.ValueKind == JsonValueKind.String)
            return Content(fallbackContent);

        // Nothing here carries markdown. An object with NO authored member is an empty node; one
        // that carries members is a payload holding something nobody can read.
        var members = jsonElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => !string.Equals(n, "$type", StringComparison.Ordinal))
            .ToArray();
        return members.Length == 0
            ? new MarkdownRead(MarkdownContentState.Absent, string.Empty, string.Empty)
            : new MarkdownRead(
                MarkdownContentState.Unreadable, string.Empty, string.Join(", ", members));

        // 🚨 A `content` member that is NOT a string is the same defect one level in (review on
        // #4626): mapping it to Absent would put the invitation back over a payload that is there
        // and unreadable — an object, an array, a number under the very member the declaration
        // names. Only a string (or an explicit null / empty string) is an answer about emptiness.
        static MarkdownRead Content(JsonElement value) =>
            value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() is { Length: > 0 } text
                    ? new MarkdownRead(MarkdownContentState.Present, text, string.Empty)
                    : new MarkdownRead(MarkdownContentState.Absent, string.Empty, string.Empty),
                JsonValueKind.Null or JsonValueKind.Undefined =>
                    new MarkdownRead(MarkdownContentState.Absent, string.Empty, string.Empty),
                _ => new MarkdownRead(
                    MarkdownContentState.Unreadable, string.Empty,
                    "content: " + value.ValueKind.ToString().ToLowerInvariant()),
            };
    }

    /// <summary>
    /// Extracts markdown content from a MeshNode, or the empty string when there is none.
    ///
    /// <para>🚨 This answer CANNOT tell an empty node from an unreadable one — both are
    /// <see cref="string.Empty"/> — so a caller that renders an empty state off it renders that
    /// state over a full document (#4600). Callers that only ask "is there prose to show or diff"
    /// are unaffected and keep using this; a caller that tells the USER the node is empty must read
    /// <see cref="ReadMarkdownContent"/> instead.</para>
    /// </summary>
    public static string GetMarkdownContent(MeshNode? node) => ReadMarkdownContent(node).Text;

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
