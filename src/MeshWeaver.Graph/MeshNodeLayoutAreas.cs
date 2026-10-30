using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using MeshWeaver.Messaging.Serialization;

namespace MeshWeaver.Graph;

/// <summary>
/// Marker record indicating that the catalog should operate in NodeType mode.
/// When set, the catalog reads NodeTypeDefinition from workspace to build the query dynamically.
/// </summary>
public record NodeTypeCatalogMode;

/// <summary>
/// Page layout options that can be set per node type via hub configuration.
/// Use <c>configuration.Set(new PageLayoutOptions { MaxWidth = "960px" })</c>.
/// </summary>
public record PageLayoutOptions
{
    /// <summary>
    /// Maximum width for the page content area (e.g., "960px", "1200px").
    /// Applied as CSS max-width with centered margins.
    /// Default: null (no constraint, full width).
    /// </summary>
    public string? MaxWidth { get; init; }
}

/// <summary>
/// The newest thing that happened UNDER a node, for pages whose subject is a container rather than
/// a document.
///
/// <para>🚨 <b>Why a container cannot use its own <c>LastModified</c>.</b> A partition root — a CRM
/// client, a space — is written when it is opened and then barely again; the work lives in its
/// children. So its own row reports whatever last touched the ROOT, which on a retyped client is
/// the migration, stamped <c>system-security</c>, while the deals and documents underneath moved
/// days later. Rendering that as "Updated" is not an incomplete answer, it is a wrong one: it tells
/// a reader the account has been quiet since a date on which nothing about the account happened.
/// Supplying this record replaces that segment with the question the page is actually asked.</para>
/// </summary>
/// <param name="At">When it happened (UTC; rendered in the viewer's zone like every other stamp).</param>
/// <param name="By">Who did it, or null when the source does not record one.</param>
/// <param name="What">A few words naming it, or null. Appended after an em dash.</param>
public record NodeActivity(DateTimeOffset At, string? By = null, string? What = null);

/// <summary>
/// One segment of a node's provenance line — the label's translation KEY, the rendered value, and
/// an optional link target.
///
/// <para>It carries the key rather than the text because <see cref="MeshNodeLayoutAreas.BuildMetaEntries"/>
/// is deliberately pure: no host, no service provider, no <c>AccessService</c>. That is what makes
/// the line assertable without standing up a mesh, and it is why the localization happens in the
/// renderer that has a host to localize with.</para>
/// </summary>
/// <param name="LabelKey">Translation key for the label (e.g. <c>node.meta.created</c>).</param>
/// <param name="Text">The value, already formatted in the viewer's zone.</param>
/// <param name="Href">Where the value links, or null for plain text.</param>
/// <param name="By">Who did it, or null. Carried SEPARATELY rather than appended to
/// <paramref name="Text"/> because the word joining them is <c>node.meta.by</c> — a translated
/// string, and the whole reason this record holds keys instead of sentences. Folding it in would
/// put a hard-coded English "by" inside a value the viewer reads in their own language.</param>
/// <param name="What">What happened, or null. Author-written and rendered as authored — it is
/// content, not chrome, so it is never translated.</param>
public record NodeMetaEntry(
    string LabelKey, string Text, string? Href = null, string? By = null, string? What = null);

/// <summary>
/// Layout areas for mesh node content.
/// - Overview: Main content display with action menu (readonly content + navigation)
/// - Thumbnail: Compact card view for use in catalogs and lists
/// - Metadata: Node metadata display (name, type, path)
/// - Settings: Node settings with NodeType link navigation
/// - Children: Child nodes grouped by type
/// </summary>
public static class MeshNodeLayoutAreas
{
    /// <summary>Area name for the node Overview layout area (the default view showing main content and the action menu).</summary>
    public const string OverviewArea = "Overview";

    /// <summary>
    /// Area ID of the provenance line INSIDE whatever page renders it — the standard header's row 2
    /// (<see cref="BuildHeader(LayoutAreaHost, MeshNode?, bool)"/>) and the row
    /// <see cref="WithNodePage"/> composes for a page that does not draw its own.
    ///
    /// <para>🚨 A stable ID rather than the positional auto-name, so "does this page carry
    /// provenance?" is a question anyone can ASK of a rendered page: nested containers render into
    /// <c>{parentArea}/{childId}</c> (<c>UiControl.GetContextForArea</c>), so the strip's depth
    /// varies by page but its store key always ENDS in <c>/NodeMeta</c>. #4500 stayed open partly
    /// because the only way to answer the question was to read the rendered HTML for the English
    /// word "Created:" — an assertion that a German reader sees an English page.</para>
    /// </summary>
    public const string NodeMetaArea = "NodeMeta";
    /// <summary>Area name for the node Thumbnail layout area (compact card view for catalogs and lists).</summary>
    public const string ThumbnailArea = "Thumbnail";
    /// <summary>Area name for the node Metadata layout area (name, type, path and related metadata).</summary>
    public const string MetadataArea = "Metadata";
    /// <summary>Area name for the node Settings layout area.</summary>
    public const string SettingsArea = "Settings";
    /// <summary>Area name for the node Comments layout area.</summary>
    public const string CommentsArea = "Comments";

    /// <summary>
    /// The inline comments section: the node's own <see cref="CommentsArea"/> embedded as a layout
    /// area. Pure composition — the platform never calls into the collaboration module, it only
    /// names the area the module registers, which is what lets the module ship separately.
    /// </summary>
    /// <param name="host">The layout area host whose node the comments belong to.</param>
    /// <returns>The section control.</returns>
    public static UiControl BuildInlineCommentsSection(LayoutAreaHost host)
        => Controls.Stack
            .WithWidth("100%")
            .WithStyle("margin-top: 32px; border-top: 1px solid var(--neutral-stroke-rest); padding-top: 16px;")
            .WithView(Controls.LayoutArea(host.Hub.Address, CommentsArea).WithShowProgress(false));
    /// <summary>Area name for the node Search layout area.</summary>
    public const string SearchArea = "Search";
    /// <summary>
    /// Area name for the slot a NodeType definition's <see cref="SearchArea"/> renders its instance
    /// list in (<see cref="NodeTypeInstances"/>) — the one part of that page computed from the
    /// definition; the page around it renders at once.
    /// </summary>
    public const string NodeTypeInstancesArea = "NodeTypeInstances";
    /// <summary>Area name for the node Files layout area.</summary>
    public const string FilesArea = "Files";
    /// <summary>Area name for the NodeTypes layout area.</summary>
    public const string NodeTypesArea = "NodeTypes";
    /// <summary>Area name for the Access Control layout area.</summary>
    public const string AccessControlArea = "AccessControl";
    /// <summary>Area name for the Groups layout area.</summary>
    public const string GroupsArea = "Groups";
    /// <summary>Area name for the Create node layout area.</summary>
    public const string CreateNodeArea = "Create";
    /// <summary>Area name for the Edit node layout area.</summary>
    public const string EditArea = "Edit";
    /// <summary>Area name for the Delete node layout area.</summary>
    public const string DeleteArea = "Delete";
    /// <summary>Area name for the Threads layout area.</summary>
    public const string ThreadsArea = "Threads";
    /// <summary>Area name for the Chat layout area.</summary>
    public const string ChatArea = "Chat";
    /// <summary>
    /// Area name for the prompt composer a markdown ```` ```prompt ```` fence lowers to (#2511).
    /// Takes the authored prompt as its reference ID; the constant is shared with the renderer that
    /// emits the marker so the two cannot drift apart.
    /// </summary>
    public const string PromptArea = PromptFence.AreaName;
    /// <summary>Area name for the Import mesh nodes layout area.</summary>
    public const string ImportMeshNodesArea = "ImportMeshNodes";
    /// <summary>Area name for the Export layout area.</summary>
    public const string ExportArea = "Export";
    /// <summary>Area name for the Copy node layout area.</summary>
    public const string CopyArea = "Copy";
    /// <summary>Area name for the Move node layout area.</summary>
    public const string MoveArea = "Move";
    /// <summary>Area name for the Recycle layout area.</summary>
    public const string RecycleArea = "Recycle";
    /// <summary>Area name for the Versions layout area.</summary>
    public const string VersionsArea = "Versions";
    /// <summary>Area name for the Version diff layout area.</summary>
    public const string VersionDiffArea = "VersionDiff";

    // UCR (Unified Content Reference) special areas
    /// <summary>Area name for the UCR Content layout area.</summary>
    public const string ContentArea = "$Content";
    /// <summary>Area name for the UCR Data layout area.</summary>
    public const string DataArea = "$Data";

    /// <summary>Area name of the node's reflected content page (<see cref="ContentData"/>).</summary>
    public const string ContentDataArea = "Data";
    /// <summary>Area name for the UCR Schema layout area.</summary>
    public const string SchemaArea = "$Schema";
    /// <summary>Area name for the UCR Model layout area.</summary>
    public const string ModelArea = "$Model";

    /// <summary>
    /// Marker that records whether <see cref="AddDefaultLayoutAreas(MeshWeaver.Messaging.MessageHubConfiguration)"/> has
    /// already run on this <see cref="MessageHubConfiguration"/>, so a second
    /// call is a safe no-op. Necessary because <c>AddGraph</c> now applies
    /// <c>AddDefaultLayoutAreas</c> via <c>ConfigureDefaultNodeHub</c>, and
    /// many hard-coded NodeType <c>HubConfiguration</c>s also call it directly
    /// — without dedup, the framework would register
    /// <c>WithHandler&lt;RollbackNodeRequest&gt;</c> / <c>UndoActivityRequest</c>
    /// twice, and AddDefaultMeshMenu / AddDefaultSettingsMenuItems would also
    /// double-emit.
    /// </summary>
    private sealed record DefaultLayoutAreasMarker;

    /// <summary>
    /// Adds the mesh node views (Details, Thumbnail, Metadata, Settings, Catalog, Calendar) to the hub's layout.
    /// Requires AddMeshDataSource() to be called first to enable GetStream&lt;MeshNode&gt;() in views.
    /// Catalog is set as the default area for browsing children with search.
    /// For comments support, call AddComments() after this method.
    ///
    /// <para><b>Idempotent</b>: a second call on the same configuration is a
    /// no-op. The marker is keyed on the <see cref="MessageHubConfiguration"/>
    /// instance, so the default node hub config and a per-NodeType
    /// <c>HubConfiguration</c> both calling this layer their respective
    /// <c>AddLayout(WithView(...))</c> overrides on top of the same single
    /// registration of the framework-level handlers/menus.</para>
    /// </summary>
    public static MessageHubConfiguration AddDefaultLayoutAreas(this MessageHubConfiguration configuration)
    {
        if (configuration.Get<DefaultLayoutAreasMarker>() is not null)
            return configuration;
        return configuration
            .Set(new DefaultLayoutAreasMarker())
            // Always wire MeshDataSource so the canonical
            // workspace.GetMeshNodeStream() / MeshNodeReference reducer is
            // available even on hubs that don't declare a ContentType. Every
            // default layout area (Overview, Thumbnail, Settings, …) reads the
            // OWN MeshNode through this reducer; without it, GetDataRequest
            // with MeshNodeReference fails with "Failed to create stream" and
            // the layout area handler silently drops responses.
            .AddMeshDataSource()
            .AddDefaultMeshMenu()
            .AddDefaultSettingsMenuItems()
            .WithHandler<RollbackNodeRequest>(VersionLayoutArea.HandleRollbackNodeRequest)
            .WithHandler<UndoActivityRequest>(VersionLayoutArea.HandleUndoActivityRequest)
            .AddLayout(layout => layout.AddDefaultLayoutAreas());
    }

    /// <summary>
    /// Registers all default mesh node layout areas (Overview, Thumbnail, Settings, Search, Files, Children,
    /// Threads, Chat, NodeTypes, Access Control, Groups, Create, Edit, Import, Export, Copy, Move, Recycle,
    /// Versions, Delete, pinning, sync, and the UCR Data/Schema/Model areas) onto the given layout definition,
    /// with Overview as the default area.
    /// </summary>
    /// <param name="layout">The layout definition to register the default areas onto.</param>
    /// <returns>The same layout definition with the default areas registered.</returns>
    public static LayoutDefinition AddDefaultLayoutAreas(this LayoutDefinition layout)
        => layout
            .WithDefaultArea(OverviewArea)
            // The landing page, registered as one: WithNodePage records that this renderer draws
            // the provenance line itself (Overview → BuildDetailsContent → BuildHeader), and the
            // record is DISCARDED the moment a node type replaces this renderer — which is how a
            // type taking over its own landing page becomes visible instead of silent (#4500).
            .WithNodePage(OverviewArea, Overview, NodePageProvenance.RenderedByThePage)
            .WithView(ThumbnailArea, Thumbnail)
            .WithView(SettingsArea, SettingsLayoutArea.Settings)
            .WithView(SearchArea, Search)
            .WithView(NodeTypeInstancesArea, NodeTypeInstances)
            .WithView(FilesArea, Files)
            .WithView(ThreadsArea, Threads)
            .WithView(ChatArea, Chat)
            .WithView(PromptArea, PromptComposerArea)
            .WithView(NodeTypesArea, NodeTypes)
            // AccessControl and Groups VIEWS ride the MeshWeaver.Graph.Views MODULE. Their area
            // names stay here (the node menu links them) and so do their base helpers —
            // AccessControlLayoutArea's deserializer / delete / add-dialog, which UserNodeType
            // calls, and GroupsLayoutArea's membership deserializer.
            .WithView(CreateNodeArea, CreateNode)
            .WithView(EditArea, EditNode)
            // The property form's fallback slot, for a hub whose configuration names no content
            // type (OverviewLayoutArea.ContentForm) — the one place the node page still reads the
            // node to decide STRUCTURE.
            .WithView(OverviewLayoutArea.ContentFormArea, OverviewLayoutArea.ContentForm)
            .WithView(ContentDataArea, ContentData)
            // The import VIEW rides the MeshWeaver.Graph.Views MODULE; its menu descriptor stays.
            .WithView(ExportArea, ExportLayoutArea.Export)
            .WithView(RecycleArea, RecycleLayoutArea.Recycle)
            // The Versions and VersionDiff VIEWS ride the MeshWeaver.Graph.Views MODULE. The
            // ROLLBACK / UNDO handlers above stay here — they are message handlers, not views.
            // Copy / Move / Pin / Unpin / PinnedThumbnail VIEWS ride the MeshWeaver.Graph.Views
            // MODULE, registered on every per-node hub like OgCard below. Their area names and
            // menu descriptors stay platform-side — the node menu is assembled here.
            // Presentation mode (#1803): the per-node mark lives beside Pin because it is the same
            // shape — a viewer-scoped list of paths on the viewer's OWN profile, never on the node.
            .WithView(PresentationLayoutArea.HideArea, PresentationLayoutArea.Hide)
            .WithView(PresentationLayoutArea.ShowArea, PresentationLayoutArea.Show)
            // The OgCard link-preview area rides the MeshWeaver.OgCard MODULE (its attribute
            // registers on every per-node hub) — delisting it drops the server-side external
            // URL-fetch surface.
            .WithView(MarkdownOverviewLayoutArea.SuppliedNavArea, MarkdownOverviewLayoutArea.SuppliedNavigationMenu)
            // One folder of the left-hand index, opened on demand from the page being read.
            .WithView(SuppliedNavigationRail.FolderArea, MarkdownOverviewLayoutArea.IndexFolder)
            // StopSync's VIEW rides the MeshWeaver.Graph.Views MODULE, registered on every
            // per-node hub exactly as OgCard above — the platform keeps the area name and the menu
            // descriptor (StopSyncLayoutArea), which is what the node menu is assembled from.
            // UCR special areas
            .WithView(DataArea, Data)
            .WithView(SchemaArea, Schema)
            .WithView(ModelArea, DataModelLayoutArea.DataModel)
            // Education areas ship in the Edu plugin's in-mesh source (CourseShellAreas,
            // EduCourseNavigationProvider) — core registers no course UI anywhere.
            .AddDomainLayoutAreas();

    /// <summary>
    /// Renders the Overview area showing the node's main content with action menu.
    /// This is the default view for a node, showing content and providing navigation.
    ///
    /// <para>A TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): the page is emitted
    /// as soon as the viewer's permissions are known and BINDS what it shows — the header, the
    /// property form and the markdown body read the node through pointers
    /// (<see cref="BuildDetailsTemplate"/>). It used to wait for the node, the permissions AND the
    /// partition root, and rebuild the whole page out of the node's values on every edit.</para>
    ///
    /// <para>The one read it keeps is the PERMISSION, and it decides STRUCTURE only: whether the
    /// viewer sees the page or the denial, and whether the form is click-to-edit.</para>
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        // Select + Switch, never SelectMany: the page for an earlier permission verdict (its rail
        // included, which never completes) is DISPOSED when the verdict changes, so a revoked page
        // cannot keep emitting beside the denial that replaced it.
        return PermissionGate(host, hubPath)
            .Select(gate =>
            {
                if (gate.Read)
                    // The left-hand index, as on every document page: children are navigated in the
                    // rail, never listed at the bottom of the page.
                    return MarkdownOverviewLayoutArea.WithIndexRail(host,
                        Observable.Return((UiControl?)host.BuildDetailsTemplate(gate.Update)));
                // Read denied: the partition's declared funnel page (RedirectOnDenied — e.g.
                // a product's glossy marketing brochure) takes the viewer there instead of a
                // dead-end Request-Access wall. Rendering the denial INSIDE the area used to
                // pre-empt the area-level redirect entirely, so the policy never applied on
                // full-page Overviews. Fail-safe: no/looping/erroring policy → the denial page.
                return host.Hub.GetRedirectOnDenied(hubPath)
                    .Take(1)
                    .Catch<string?, Exception>(_ => Observable.Return<string?>(null))
                    .Select(redirect => redirect is { Length: > 0 }
                        && !string.Equals(redirect, hubPath, StringComparison.OrdinalIgnoreCase)
                        ? (UiControl?)Controls.Stack
                            .WithView(Controls.Redirect("/" + redirect.TrimStart('/')), "Redirect")
                            .WithView(Controls.Markdown(
                                $"[Continue here →](/{redirect.TrimStart('/')})"))
                        : BuildAccessDenied(hubPath, locale: host.ViewerLocale()));
            })
            .Switch();
    }

    /// <summary>
    /// The two permission facts a node page's STRUCTURE depends on — may the viewer read it, may
    /// they edit it — emitted only when one of them changes, so a permission re-evaluation that
    /// changes neither does not rebuild the page.
    /// </summary>
    private static IObservable<(bool Read, bool Update)> PermissionGate(LayoutAreaHost host, string hubPath)
        => host.Hub.GetEffectivePermissions(hubPath)
            .Select(p => (Read: p.HasFlag(Permission.Read), Update: p.HasFlag(Permission.Update)))
            .DistinctUntilChanged();

    /// <summary>
    /// The node's DATA page — the framework's reflected content view (header + property overview,
    /// exactly what the default <see cref="Overview"/> renders), always available at
    /// <c>/{node}/Data</c> even when a type overrides Overview with a designed page. Read-only for
    /// viewers; editors keep click-to-edit. This is what the read-gated "Data" node-menu item opens,
    /// so a viewer without Update rights can always see the underlying record.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> ContentData(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return PermissionGate(host, hubPath)
            .Select(gate => gate.Read
                ? (UiControl?)host.BuildDetailsTemplate(gate.Update)
                : BuildAccessDenied(hubPath, locale: host.ViewerLocale()));
    }

    /// <summary>
    /// Returns the Data menu item — read-gated: every viewer can open the node's underlying record
    /// even when the type's Overview is a designed page and Edit is hidden behind Update.
    /// </summary>
    public static NodeMenuItemDefinition? GetDataMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Read))
            return null;
        return new("Data", ContentDataArea, Order: 31, Href: BuildUrl(hubPath, ContentDataArea))
            { LabelKey = "menu.data" };
    }

    public static UiControl BuildAccessDenied(string nodePath, string? locale = null)
    {
        var nodeName = nodePath.Split('/').LastOrDefault() ?? nodePath;
        return Controls.Stack.WithWidth("100%").WithStyle("padding: 48px 24px; align-items: center; text-align: center;")
            .WithView(Controls.Icon(FluentIcons.ShieldKeyhole())
                .WithStyle("font-size: 64px; color: var(--neutral-foreground-hint); margin-bottom: 16px;"))
            .WithView(Controls.H2(LocalizationCatalog.Get("error.accessDenied", locale)).WithStyle("margin: 0;"))
            // A Label, not markup: the node name is the viewer's text, never page structure.
            .WithView(Controls.Body(LocalizationCatalog.Get(AccessDeniedDetailKey, locale, nodeName))
                .WithColor(HintColor)
                .WithStyle("max-width: 480px;"))
            .WithView(Controls.Button(LocalizationCatalog.Get("ui.requestAccess", locale))
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.PersonAdd())
                .WithClickAction(ctx =>
                {
                    // Show a confirmation that the request was noted
                    var dialog = Controls.Dialog(
                        Controls.Body(LocalizationCatalog.Get(AccessRequestedBodyKey, locale, nodeName)),
                        LocalizationCatalog.Get(AccessRequestedTitleKey, locale)
                    ).WithSize("S").WithClosable(true);
                    ctx.Host.UpdateArea(DialogControl.DialogArea, dialog);
                    return Task.CompletedTask;
                }));
    }

    /// <summary>
    /// The standard node-page container style: centered, padded, constrained to the page max
    /// width (node-type override → <see cref="PageLayoutOptions"/> → 1200px default).
    /// Public module-facing contract: out-of-tree modules (e.g. Approvals) apply this to their
    /// own layout-area roots so module pages share the built-in node-page geometry instead of
    /// duplicating the style string. Changing the returned style reflows every module page.
    /// </summary>
    public static string GetContainerStyle(LayoutAreaHost host, NodeTypeDefinition? typeDef = null, string? maxWidthOverride = null)
    {
        var pageMaxWidth = maxWidthOverride
            ?? typeDef?.PageMaxWidth
            ?? host.Hub.Configuration.Get<PageLayoutOptions>()?.MaxWidth
            ?? "1200px";
        return $"position: relative; max-width: {pageMaxWidth}; margin: 0 auto; padding: 0 24px;";
    }

    /// <summary>
    /// The node page — header, property overview, markdown body, comments — as a TEMPLATE: every
    /// control is emitted at once, and what it shows is BOUND. Field values bind straight to the
    /// node (<see cref="LayoutAreaReference.GetMeshNodeDataContext"/>); what has to be computed
    /// first (the header's title, icon and provenance line; the markdown body rendered to HTML) is a
    /// projection the hub feeds into <c>/data</c> (<see cref="NodePageProjections"/>, bound with
    /// <see cref="Template"/>'s stream <c>Bind</c>). Which form to draw is STRUCTURE and is read from
    /// the hub's configuration (<see cref="ConfiguredContentType(LayoutAreaHost)"/>), never the node.
    /// </summary>
    /// <param name="host">The node hub's layout host.</param>
    /// <param name="canEdit">Whether the viewer may edit — click-to-edit fields and the icon picker.</param>
    internal static UiControl BuildDetailsTemplate(this LayoutAreaHost host, bool canEdit)
    {
        var containerStyle = GetContainerStyle(host);
        var content = Controls.Stack.WithWidth("100%").WithStyle(containerStyle)
            .WithView(BuildHeaderTemplate(host, canEdit))
            .WithView(BuildPropertySection(host, canEdit));

        // The markdown body is a DIRECT child of the outer stack — agents, tests and the document
        // export locate it there — bound to a projection of the node, hidden while there is none.
        // STRETCH, explicitly (#6036): the body has no width of its own, so under the stack's default
        // start alignment one table wider than the column made the body as wide as the table and the
        // pane clipped it with no scrollbar. Its siblings are already width:100%, so only the body moves.
        var outer = Controls.Stack.WithWidth("100%")
            .WithHorizontalAlignment(HorizontalAlignment.Stretch)
            .WithView(content)
            .WithView(OverviewLayoutArea.BuildMarkdownBodyTemplate(host));

        // No hardcoded children section. A node page is a MARKDOWN SPACE: it shows exactly what its
        // body contains, and children (or any other content) are injected INLINE with the @@(query)
        // operator. Browsing child nodes is still available via the Catalog / Search areas.

        // Comments — back in constrained width. The section is an EMBEDDED AREA, not a compiled
        // call: the platform knows the area's name and nothing else, and the collaboration module
        // serves it. Without the module HasComments() is false and no section is emitted.
        if (host.Hub.Configuration.HasComments())
        {
            outer = outer.WithView(
                Controls.Stack
                    .WithWidth("100%")
                    .WithStyle(containerStyle + " margin-top: 32px; padding-top: 24px; border-top: 1px solid var(--neutral-stroke-rest);")
                    .WithView(BuildInlineCommentsSection(host)));
        }

        return outer;
    }

    /// <summary>
    /// The property section of the node page, chosen from CONFIGURATION: a NodeType definition's
    /// description, the content type's click-to-edit form, or — on a hub whose configuration names
    /// no content type — the <see cref="OverviewLayoutArea.ContentFormArea"/> slot, which is the
    /// one place the page still reads the node, to find the form's shape.
    /// </summary>
    private static UiControl BuildPropertySection(LayoutAreaHost host, bool canEdit)
    {
        if (IsNodeTypeDefinitionHub(host))
            return NodePageProjections.TypeInfo(host)
                .Bind(p => new MarkdownControl(p.Markdown), "nodeTypeInfo");

        return ConfiguredContentType(host) is { } contentType
            ? OverviewLayoutArea.BuildPropertyOverviewTemplate(host, contentType, canEdit)
            : OverviewLayoutArea.ContentFormSlot(host, OverviewLayoutArea.ContentFormOverview);
    }

    /// <summary>
    /// The content type this node hub is CONFIGURED with: its <see cref="MeshDataSource"/>'s
    /// (<c>WithContentType</c>), else the one the mesh registered for its NodeType. Null when
    /// neither names one.
    /// </summary>
    internal static Type? ConfiguredContentType(LayoutAreaHost host)
    {
        if (ConfiguredContentType(host.Workspace) is { } configured)
            return configured;
        var nodeType = host.Hub.Configuration.Get<NodeTypePathHolder>()?.Path;
        return !string.IsNullOrEmpty(nodeType)
               && host.Hub.ServiceProvider.GetService<IMeshContentTypeRegistry>() is { } registry
               && registry.TryResolveByNodeType(nodeType, out var registered)
            ? registered
            : null;
    }

    /// <summary>Whether this hub serves a NodeType DEFINITION — from its configuration, not its node.</summary>
    internal static bool IsNodeTypeDefinitionHub(LayoutAreaHost host)
        => host.Hub.Configuration.Get<NodeTypePathHolder>()?.Path == MeshNode.NodeTypePath
           || ConfiguredContentType(host.Workspace) == typeof(NodeTypeDefinition);

    /// <summary>
    /// Builds a description section for built-in type nodes.
    /// Shows the type description from NodeTypeDefinition or a default message.
    /// </summary>
    private static UiControl BuildTypeInfoSection(MeshNode node, NodeTypeDefinition typeDef)
    {
        var description = typeDef.Description
            ?? $"Built-in type for managing {node.Name ?? node.NodeType ?? "content"} nodes.";

        return Controls.Markdown(description);
    }

    /// <summary>
    /// Builds the header: icon + title + identity action row (Move/Copy/Delete/Edit,
    /// plus Configuration on NodeType nodes), followed by a meta row with the node-type
    /// link and the Created/LastModified/LastModifiedBy timestamps.
    /// Clicking the icon opens an icon-picker dialog; clicking the title (when the
    /// content has a Title property and the user can edit) switches it to inline edit.
    /// Public module-facing contract: out-of-tree modules (e.g. Approvals) compose this header
    /// atop their own layout areas so module pages carry the same icon/title/action/meta chrome
    /// as built-in node pages — the alternative is a hand-built copy that drifts.
    /// </summary>
    public static UiControl BuildHeader(LayoutAreaHost host, MeshNode? node, bool canEdit = true)
        => BuildHeader(host, node, canEdit, null);

    /// <summary>
    /// <see cref="BuildHeader(LayoutAreaHost, MeshNode?, bool)"/> with the node's PARTITION ROOT in
    /// hand, so a page under a marked package wears that package's mark instead of a generic type
    /// glyph (#2075 item 2).
    ///
    /// <para>🚨 A separate overload rather than a fourth optional parameter, for the reason
    /// <c>PageIcon.Rel</c> spells out: this method is a module-facing contract, and adding a
    /// parameter — default or not — REPLACES the signature every already-compiled module was built
    /// against. An overload is additive, so a module compiled against the three-argument form keeps
    /// binding to it.</para>
    ///
    /// <para>Inheritance is opt-in per surface, deliberately. A page identifies ONE node, so the
    /// package mark is pure gain there; a LIST of siblings is not — the NodeType glyph is what tells
    /// a doc from a code node from a thread in a mixed child listing, and flattening a rail to one
    /// repeated package mark would take that away. So the page header opts in and the nav rail's
    /// child links do not.</para>
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="node">The node whose header is being built.</param>
    /// <param name="canEdit">Whether the viewer may edit (icon picker, inline title).</param>
    /// <param name="partitionRoot">The node's partition root, or null to skip inheritance.</param>
    public static UiControl BuildHeader(
        LayoutAreaHost host, MeshNode? node, bool canEdit, MeshNode? partitionRoot)
        => BuildHeader(host, node, canEdit, partitionRoot, null);

    /// <summary>
    /// <see cref="BuildHeader(LayoutAreaHost, MeshNode?, bool, MeshNode?)"/> for a page whose
    /// subject is a CONTAINER: <paramref name="lastActivity"/> replaces the provenance line's
    /// <c>Updated</c> segment with the newest thing that happened underneath the node.
    ///
    /// <para>🚨 A fifth OVERLOAD rather than a fifth optional parameter, for the reason the
    /// four-argument form spells out above: this method is a module-facing contract compiled
    /// against by out-of-tree modules, and adding a parameter — default or not — replaces the
    /// signature every already-compiled module was built against. An overload is additive.</para>
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="node">The node whose header is being built.</param>
    /// <param name="canEdit">Whether the viewer may edit (icon picker, inline title).</param>
    /// <param name="partitionRoot">The node's partition root, or null to skip inheritance.</param>
    /// <param name="lastActivity">The newest activity beneath the node, or null to report the node's own row.</param>
    public static UiControl BuildHeader(
        LayoutAreaHost host, MeshNode? node, bool canEdit, MeshNode? partitionRoot,
        NodeActivity? lastActivity)
    {
        // Chrome-less pages: a node excluded from the "header" context ships without the
        // icon/title/meta block — the content (a marketing hero, a landing page) starts
        // immediately. One mechanism, reused from MeshNodeVisibility — no parallel flag.
        if (node?.IsExcludedFromContext(MeshNodeVisibility.HeaderContext) == true)
            return Controls.Stack;
        var hubPath = host.Hub.Address.ToString();
        var nodePath = node?.Path ?? hubPath;
        var title = node?.Name ?? node?.Id ?? hubPath;
        var iconValue = MeshNodeImageHelper.ResolveNodeIcon(node, partitionRoot);
        var rawIcon = node?.Icon;

        // Row 1 — icon + title (+ action buttons on the right)
        var identityRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; gap: 20px; margin-top: 16px;");

        identityRow = identityRow.WithView(BuildClickableIcon(host, node, iconValue, rawIcon, canEdit));

        // Title column takes remaining width so action buttons sit at the far right.
        var titleColumn = Controls.Stack.WithStyle("flex: 1; min-width: 0;");

        bool hasTitleProperty = false;
        if (node?.Content is JsonElement jsonElement && jsonElement.TryGetProperty("$type", out var typeProperty))
        {
            var typeName = typeProperty.GetString();
            var typeRegistry = host.Hub.ServiceProvider.GetService<ITypeRegistry>();
            var contentType = !string.IsNullOrEmpty(typeName) ? typeRegistry?.GetType(typeName) : null;
            hasTitleProperty = contentType?.GetProperty("Title") != null;
        }

        if (hasTitleProperty && node != null)
        {
            var dataId = EditLayoutArea.GetDataId(node.Namespace ?? hubPath);
            titleColumn = titleColumn.WithView(OverviewLayoutArea.BuildTitle(host, node, dataId, canEdit));
        }
        else
        {
            titleColumn = titleColumn.WithView(Controls.H1(title).WithStyle(TitleStyle));
        }

        identityRow = identityRow.WithView(titleColumn);
        identityRow = identityRow.WithView(BuildHeaderActionRow(host, node?.NodeType == MeshNode.NodeTypePath, nodePath, canEdit));

        // Row 2 — node-type link + timestamps
        var metaRow = BuildMetaRow(host, node, lastActivity);

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding-bottom: 20px; margin-bottom: 24px; border-bottom: 1px solid var(--neutral-stroke-rest); gap: 8px;")
            .WithView(identityRow)
            // Named, not auto-named: see NodeMetaArea. The identity row keeps its auto name "1"
            // (GetAutoName is Renderers.Count + 1, so naming the SECOND view shifts nothing).
            .WithView(metaRow, NodeMetaArea);
    }

    /// <summary>The <c>/data</c> id the node page header's projection is published under.</summary>
    internal const string HeaderDataId = "nodeHeader";

    private const string HeaderStyle =
        "padding-bottom: 20px; margin-bottom: 24px; border-bottom: 1px solid var(--neutral-stroke-rest); gap: 8px;";

    private const string TitleStyle =
        "margin: 0; font-size: 2rem; font-weight: 700; letter-spacing: -0.02em; line-height: 1.15;";

    /// <summary>
    /// The node page header — icon, title, object actions, provenance line — as a TEMPLATE of
    /// the hub's own node. Same shape as <see cref="BuildHeader(LayoutAreaHost, MeshNode?, bool)"/>
    /// (the identity row, then the <see cref="NodeMetaArea"/> row), emitted at once: the title,
    /// the icon and the provenance line are bound to <see cref="NodePageProjections.Header"/>, the
    /// object actions are chosen from configuration, and a node excluded from the header context
    /// hides the header rather than delaying the page to find out.
    /// </summary>
    /// <param name="host">The node hub's layout host.</param>
    /// <param name="canEdit">Whether the viewer may edit — the icon picker, Edit, an editable title.</param>
    internal static UiControl BuildHeaderTemplate(LayoutAreaHost host, bool canEdit)
    {
        var hubPath = host.Hub.Address.ToString();

        UiControl tileFrame = canEdit
            ? Controls.Stack.WithStyle("cursor: pointer;").WithClickAction(ctx =>
            {
                NodePageProjections.OpenIconPicker(host, ctx);
                return Task.CompletedTask;
            })
            : Controls.Stack;

        // A content type with a Title property is titled by THAT field, click-to-edit; every other
        // node by its name.
        var editableTitle = ConfiguredContentType(host)?.GetProperty("Title") != null
            ? OverviewLayoutArea.BuildTitleTemplate(host, hubPath, EditLayoutArea.GetDataId(hubPath), canEdit)
            : null;
        var actions = BuildHeaderActionRow(host, IsNodeTypeDefinitionHub(host), hubPath, canEdit);

        return NodePageProjections.Header(host, HeaderStyle, NodePageProjections.Viewer.Of(host))
            .Bind(p => HeaderTemplate(p.Style, p.Title, p.Icon, p.IconSize, p.IconFit, p.TileStyle,
                new MetaSlots(
                    p.Meta.TypeStyle, p.Meta.TypeLabel, p.Meta.TypeText, p.Meta.TypeHref,
                    p.Meta.CreatedStyle, p.Meta.CreatedLabel, p.Meta.CreatedText,
                    p.Meta.ChangedStyle, p.Meta.ChangedLabel, p.Meta.ChangedText),
                tileFrame, editableTitle, actions), HeaderDataId);
    }

    /// <summary>
    /// The header's controls with its bound values as POINTERS (<see cref="Template"/> substitutes
    /// them). Every other part is the static structure the caller built.
    /// </summary>
    internal static StackControl HeaderTemplate(
        object style, object title, object icon, object iconSize, object iconFit, object tileStyle, MetaSlots meta,
        UiControl tileFrame, UiControl? editableTitle, UiControl actions)
    {
        var tile = (tileFrame as StackControl ?? Controls.Stack)
            .WithView(IconTileControl(icon, iconSize, iconFit, tileStyle));

        var titleColumn = Controls.Stack.WithStyle("flex: 1; min-width: 0;")
            .WithView(editableTitle ?? Controls.H1(title).WithStyle(TitleStyle));

        var identityRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithStyle("align-items: center; gap: 20px; margin-top: 16px;")
            .WithView(tile)
            .WithView(titleColumn)
            .WithView(actions);

        return (Controls.Stack.WithWidth("100%") with { Style = style })
            .WithView(identityRow)
            // Named, not auto-named: see NodeMetaArea.
            .WithView(MetaRow(meta), NodeMetaArea);
    }

    /// <summary>
    /// The node icon tile: a <c>Stack</c> carrying the tile style and ONE <see cref="ImageControl"/>
    /// — decorative (empty alt), because the node's name stands right beside it. Every value is a
    /// literal or a pointer. The image control is what draws an inline svg as an inert image and
    /// refuses an unsafe URL, so the tile no longer carries markup of its own.
    /// </summary>
    internal static StackControl IconTileControl(object icon, object size, object fit, object tileStyle)
        => (Controls.Stack with { Style = tileStyle })
            .WithView(Controls.Image(icon, "")
                .WithSize(size)
                .WithFit(fit)
                .WithShape(ImageShape.Rounded));

    /// <summary>
    /// Renders the node icon as a clickable tile that opens the icon-picker dialog when the
    /// user has edit rights. Falls back to a placeholder (dashed border) when no icon is set.
    /// </summary>
    internal static UiControl BuildClickableIcon(
        LayoutAreaHost host, MeshNode? node, string? iconValue, string? rawIcon, bool canEdit)
    {
        var decided = IconTile.Of(iconValue, rawIcon);
        UiControl tile = IconTileControl(decided.Icon, decided.Size, decided.Fit, decided.Style);

        if (!canEdit || node == null)
            return tile;

        // Wrap in a clickable stack that opens the icon-picker dialog.
        return Controls.Stack
            .WithStyle("cursor: pointer;")
            .WithView(tile)
            .WithClickAction(ctx =>
            {
                ctx.Host.UpdateArea(DialogControl.DialogArea, NodeIconPickerDialog.Build(host, node));
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Builds the right-aligned OBJECT ACTIONS beside the node's title: Configuration on NodeType
    /// nodes, <b>Edit</b> as the one labelled primary button, and a labelled <b>⋯ More</b> dropdown
    /// carrying every other entry of the node's menu.
    ///
    /// <para>🧭 <b>Why here, and why this shape.</b> The node menu used to be reachable only from an
    /// unlabelled cube icon in the portal's top bar — detached from the object it acts on, and
    /// read by nobody as "the actions for this page". GitHub, and the
    /// Fluent / Material guidance, put an object's actions BESIDE its title: one or two labelled
    /// primary buttons, then an overflow menu, grouped with dividers, destructive entries last and
    /// red. Copy / Move / Delete used to be separate buttons here as well; they now live in ⋯, so
    /// the page offers each operation once.</para>
    ///
    /// <para>🚨 <b>⋯ renders the SAME <c>$Menu:Node</c> list the top bar reads</b> — it reads the
    /// finished <see cref="MenuControl"/> the <c>RenderMenus</c> renderer writes on this host, so it
    /// is permission-filtered, catalog-overlaid, localized and divider-derived exactly once, in one
    /// place, and a data contribution (the e-Signature package's <c>Request signature</c>) appears
    /// in it with no code here. It re-renders whenever that list changes (a grant landing, a
    /// package installed) and is hidden while the list is empty.</para>
    ///
    /// <para>Every button is gated on its area rendering HERE (<see cref="CanRenderArea"/>, #3604)
    /// — the menu list already dropped the unrenderable entries for the same reason.</para>
    /// </summary>
    private static UiControl BuildHeaderActionRow(
        LayoutAreaHost host, bool isNodeTypeDefinition, string nodePath, bool canEdit)
    {
        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 8px; margin-left: auto; flex-wrap: wrap; justify-content: flex-end;");

        // Configuration button for NodeType definition nodes — points at their own Configuration area.
        if (isNodeTypeDefinition)
        {
            row = row.WithView(Controls.Button(host.Localize("ui.configuration"))
                .WithAppearance(Appearance.Accent)
                .WithIconStart(FluentIcons.Settings())
                .WithNavigateToHref(BuildUrl(nodePath, NodeTypeLayoutAreas.ConfigurationArea)));
        }

        var editShown = canEdit && CanRenderArea(host.LayoutDefinition, EditArea);
        if (editShown)
            row = row.WithView(Controls.Button(host.Localize("common.edit"))
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.Edit())
                .WithNavigateToHref(BuildUrl(nodePath, EditArea)));

        return row.WithView(MoreActions(host, nodePath, editShown), area => area.WithId(NodeActionsArea));
    }

    /// <summary>The named sub-area of the node header that holds the ⋯ More dropdown.</summary>
    public const string NodeActionsArea = "NodeActions";

    /// <summary>The header's Edit + More row as a page-level slot, for a landing page that replaces the standard header (MeshWeaver.Plugins#2787).</summary>
    /// <param name="host">The node hub's layout host.</param>
    internal static IObservable<UiControl?> HeaderActionsSlot(LayoutAreaHost host)
    {
        var hubPath = host.Hub.Address.ToString();
        return PermissionGate(host, hubPath)
            .Select(gate => (UiControl?)(gate.Read
                ? Controls.Stack.WithWidth("100%").WithStyle("align-items: flex-end; padding: 8px 0 0 0;")
                    .WithView(BuildHeaderActionRow(host, false, hubPath, gate.Update))
                : Controls.Stack));
    }

    /// <summary>
    /// The stable CSS class on the ⋯ More trigger — the hook for tests and client scripts, since
    /// its visible word follows the viewer's language.
    /// </summary>
    public const string MoreActionsClass = "node-actions-more";

    /// <summary>The CSS class on every entry inside the ⋯ More dropdown.</summary>
    public const string MoreActionsItemClass = "node-actions-item";

    /// <summary>Translation key for the ⋯ More trigger's word.</summary>
    public const string MoreActionsKey = "node.actions.more";

    /// <summary>
    /// The live ⋯ More dropdown: the node menu's finished list, read off this host's own
    /// <c>$Menu:Node</c> slot. Seeded EMPTY so the header paints at once (hidden ⋯) rather than
    /// waiting on the menu's first emission.
    /// </summary>
    private static IObservable<UiControl?> MoreActions(LayoutAreaHost host, string nodePath, bool editShown)
    {
        // Resolved on the render turn — the viewer's locale is an AsyncLocal read at call time.
        var more = host.Localize(MoreActionsKey);
        return host.Stream
            .GetControlStream(MenuControl.GetMenuArea(NodeMenuItemsExtensions.NodeMenuContext))
            .OfType<MenuControl>()
            .Select(menu => menu.Items)
            .StartWith((IReadOnlyList<NodeMenuItemDefinition>)[])
            .DistinctUntilChanged(MenuItemsSequenceComparer.Instance)
            .Select(items => (UiControl?)BuildMoreActions(ArrangeMoreActions(items, editShown), nodePath, more));
    }

    /// <summary>
    /// The ⋯ list as a pure function of the node menu's finished entries: drops <b>Edit</b> when the
    /// header already shows it as the primary button, moves <b>Delete</b> to the END behind its own
    /// divider (destructive last), and leaves no divider leading, trailing or doubled.
    /// </summary>
    /// <param name="items">The node menu's finished entries, dividers included.</param>
    /// <param name="editShown">True when the header renders Edit as a button of its own.</param>
    internal static ImmutableList<NodeMenuItemDefinition> ArrangeMoreActions(
        IReadOnlyList<NodeMenuItemDefinition> items, bool editShown)
    {
        var destructive = items.Where(IsDestructive).ToImmutableList();
        var rest = items
            .Where(i => !IsDestructive(i))
            .Where(i => !(editShown && !i.IsAction && !i.IsSubmenuParent && i.Area == EditArea));

        var arranged = ImmutableList.CreateBuilder<NodeMenuItemDefinition>();
        foreach (var item in rest)
        {
            if (IsSeparator(item) && (arranged.Count == 0 || IsSeparator(arranged[^1])))
                continue;
            arranged.Add(item);
        }
        while (arranged.Count > 0 && IsSeparator(arranged[^1]))
            arranged.RemoveAt(arranged.Count - 1);

        if (!destructive.IsEmpty)
        {
            if (arranged.Count > 0)
                arranged.Add(new NodeMenuItemDefinition("", NodeMenuItemDefinition.SeparatorArea));
            arranged.AddRange(destructive);
        }
        return arranged.ToImmutable();
    }

    private static bool IsSeparator(NodeMenuItemDefinition item) => item.Area == NodeMenuItemDefinition.SeparatorArea;

    private static bool IsDestructive(NodeMenuItemDefinition item)
        => !item.IsAction && !item.IsSubmenuParent && item.Area == DeleteArea;

    /// <summary>
    /// The ⋯ More dropdown for an arranged list — a platform <see cref="MenuItemControl"/> whose
    /// children are navigation buttons, dividers and nested submenus; an EMPTY stack (no ⋯ at all)
    /// when there is nothing to offer.
    /// </summary>
    /// <param name="items">The arranged entries (<see cref="ArrangeMoreActions"/>).</param>
    /// <param name="nodePath">The node the entries act on.</param>
    /// <param name="moreLabel">The localized word on the trigger.</param>
    internal static UiControl BuildMoreActions(
        IReadOnlyList<NodeMenuItemDefinition> items, string nodePath, string moreLabel)
    {
        if (!items.Any(i => !IsSeparator(i)))
            return Controls.Stack;

        return AddMenuEntries(
            Controls.MenuItem(moreLabel, FluentIcons.MoreHorizontal()).WithClass(MoreActionsClass),
            items, nodePath);
    }

    private static MenuItemControl AddMenuEntries(
        MenuItemControl menu, IEnumerable<NodeMenuItemDefinition> items, string nodePath)
    {
        foreach (var item in items)
            menu = menu.WithView(MenuEntry(item, nodePath));
        return menu;
    }

    private static UiControl MenuEntry(NodeMenuItemDefinition item, string nodePath)
    {
        if (IsSeparator(item))
            return Controls.Stack.WithStyle(
                "border-top: 1px solid var(--neutral-stroke-divider-rest); margin: 4px 8px; height: 0;");

        var text = string.IsNullOrEmpty(item.Icon) ? item.Label : $"{item.Icon} {item.Label}";
        if (item.IsSubmenuParent)
            return AddMenuEntries(Controls.MenuItem(text).WithClass(MoreActionsItemClass),
                item.Children ?? [], nodePath);

        var style = "width: 100%; justify-content: flex-start; white-space: nowrap;";
        if (IsDestructive(item))
            style += " color: var(--error, #d32f2f);";
        return Controls.Button(text)
            .WithAppearance(Appearance.Stealth)
            .WithLabel(item.Tooltip ?? item.Label)
            .WithClass(MoreActionsItemClass)
            .WithStyle(style)
            .WithNavigateToHref(MoreActionHref(item, nodePath));
    }

    /// <summary>
    /// Where a ⋯ entry goes. A <see cref="MenuActions.Recycle"/> ACTION navigates to the node's
    /// <c>/{path}/Recycle</c> URL, which the portal intercepts and runs on the CIRCUIT — the one
    /// place that survives the hub it tears down (#2202); this dropdown is hosted ON that hub, so it
    /// must never run the recycle itself. Any other entry follows its href (an unknown action's
    /// href is its documented graceful degradation), or the node's own area URL.
    /// </summary>
    internal static string MoreActionHref(NodeMenuItemDefinition item, string nodePath)
        => item.Action == MenuActions.Recycle
            ? BuildUrl(nodePath, RecycleArea)
            : item.Href ?? BuildUrl(nodePath, item.Area);

    /// <summary>Translation key for the provenance line's node-type label.</summary>
    public const string MetaTypeKey = "node.meta.type";

    /// <summary>Translation key for the provenance line's created label.</summary>
    public const string MetaCreatedKey = "node.meta.created";

    /// <summary>Translation key for the provenance line's updated label.</summary>
    public const string MetaUpdatedKey = "node.meta.updated";

    /// <summary>Translation key for the provenance line's last-activity label.</summary>
    public const string MetaLastActivityKey = "node.meta.lastActivity";

    /// <summary>The word joining a stamp to whoever made it — a format string ("by {0}" / "von {0}"),
    /// which is why the actor is a FIELD on the entry and not part of its text.</summary>
    public const string MetaByKey = "node.meta.by";

    /// <summary>The one timestamp format the provenance line uses.</summary>
    private const string MetaStampFormat = "yyyy-MM-dd HH:mm";

    // ── Text keys of the node page's own chrome (strings.{en,de}.json) ─────────────────────────────
    internal const string AccessDeniedDetailKey = "node.accessDenied.detail";
    internal const string AccessRequestedTitleKey = "node.accessRequested.title";
    internal const string AccessRequestedBodyKey = "node.accessRequested.body";
    internal const string MetadataTitleKey = "settings.metadata";
    internal const string MetadataNameKey = "node.metadata.name";
    internal const string MetadataPathKey = "node.metadata.path";
    internal const string MetadataParentKey = "node.metadata.parent";
    internal const string NodeNotFoundKey = "node.notFound";
    internal const string NodeNotFoundAtKey = "node.notFoundAt";
    internal const string SelectCollectionKey = "node.files.selectCollection";
    internal const string DownloadPdfKey = "content.downloadPdf";
    internal const string InsertedFromKey = "content.insertedFrom";
    internal const string NavigateToKey = "content.navigateTo";

    /// <summary>The secondary text colour of the node page's chrome — the provenance values, hints.</summary>
    internal const string HintColor = "var(--neutral-foreground-hint)";

    /// <summary>The primary text colour — the provenance labels.</summary>
    internal const string RestColor = "var(--neutral-foreground-rest)";

    /// <summary>The provenance line's text size, on every label of the row.</summary>
    private const string MetaTextStyle = "font-size: 0.85rem;";

    /// <summary>
    /// The provenance line's segments — <c>Type</c>, <c>Created</c>, and either <c>Updated</c> or,
    /// when <paramref name="lastActivity"/> is supplied, <c>Last activity</c>.
    ///
    /// <para>🚨 <b>Pure on purpose.</b> No host, no service provider, no <c>AccessService</c>: the
    /// viewer's zone arrives as an IANA id and every stamp goes through the deterministic
    /// <see cref="DisplayTimeExtensions.ToDisplayTime(DateTimeOffset, string?)"/>. That is what lets
    /// the line be asserted without standing up a mesh — the renderer below is then only markup and
    /// localization, which is where this used to hide three hard-coded English words.</para>
    ///
    /// <para>A stamp that was never set emits NO segment rather than an em dash or the epoch: a node
    /// with no recorded date should say nothing, not something false. Equally, a null
    /// <paramref name="lastActivity"/> falls through to the node's own <c>LastModified</c>, so a
    /// container with nothing under it yet still reports when it was itself last written.</para>
    /// </summary>
    /// <param name="node">The node whose provenance is described, or null for no segments.</param>
    /// <param name="timeZoneId">The viewer's named IANA zone; null/unknown renders UTC.</param>
    /// <param name="lastActivity">The newest activity beneath a CONTAINER node — see <see cref="NodeActivity"/>.</param>
    public static ImmutableArray<NodeMetaEntry> BuildMetaEntries(
        MeshNode? node, string? timeZoneId, NodeActivity? lastActivity = null)
    {
        if (node is null)
            return ImmutableArray<NodeMetaEntry>.Empty;

        var entries = ImmutableArray.CreateBuilder<NodeMetaEntry>(3);

        // A NodeType node linking to its own Configuration area would link to the page you are on.
        if (!string.IsNullOrEmpty(node.NodeType) && node.NodeType != MeshNode.NodeTypePath)
            entries.Add(new NodeMetaEntry(
                MetaTypeKey,
                node.NodeType.Contains('/') ? node.NodeType.Split('/').Last() : node.NodeType,
                BuildUrl(node.NodeType, NodeTypeLayoutAreas.ConfigurationArea)));

        if (node.CreatedDate != default)
            entries.Add(new NodeMetaEntry(
                MetaCreatedKey, Stamp(node.CreatedDate, timeZoneId), By: NullIfBlank(node.CreatedBy)));

        // The override REPLACES the node's own row rather than joining it: two "last changed"
        // answers on one line, one of them a migration's, is worse than the weaker of the two.
        if (lastActivity is { } activity)
            entries.Add(new NodeMetaEntry(
                MetaLastActivityKey, Stamp(activity.At, timeZoneId),
                By: NullIfBlank(activity.By), What: NullIfBlank(activity.What)));
        else if (node.LastModified != default)
            entries.Add(new NodeMetaEntry(
                MetaUpdatedKey, Stamp(node.LastModified, timeZoneId),
                By: NullIfBlank(node.LastModifiedBy)));

        return entries.ToImmutable();
    }

    /// <summary>The instant, in the viewer's zone. Nothing else.
    ///
    /// <para>🚨 It used to append <c>" by {who}"</c> and <c>" — {what}"</c> here, and that was an
    /// i18n defect hiding inside a pure function: "by" is a translated word (<c>node.meta.by</c>),
    /// and a pure helper has no host to translate it with. Composing it here would have quietly
    /// reverted the localization of this very line and put an English word in front of a German
    /// reader. The actor and the description travel as fields; the renderer, which HAS a host,
    /// joins them.</para></summary>
    private static string Stamp(DateTimeOffset at, string? timeZoneId)
        => DisplayTimeExtensions.ToDisplayTime(at, timeZoneId).ToString(MetaStampFormat);

    /// <summary>Blank and absent mean the same thing here: no segment rather than a dangling "by".</summary>
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The provenance line as a control — node type linking to its Configuration area, then the
    /// timestamps, every label localized.
    ///
    /// <para>Public module-facing contract, and the narrow one: a module page that builds its own
    /// body but wants the standard provenance line calls THIS rather than
    /// <see cref="BuildHeader(LayoutAreaHost, MeshNode?, bool)"/>, which would also impose the
    /// icon/title/action block the page has already drawn itself. Pass
    /// <paramref name="lastActivity"/> on a container page — see <see cref="NodeActivity"/>.</para>
    /// </summary>
    public static UiControl BuildMetaRow(
        LayoutAreaHost host, MeshNode? node, NodeActivity? lastActivity = null)
    {
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        var zoneId = access?.Context?.TimeZoneId ?? access?.CircuitContext?.TimeZoneId;
        // The SAME composition the bound template draws, fed literal values instead of pointers.
        return MetaRow(MetaSlots.Of(MetaLine.Of(BuildMetaEntries(node, zoneId, lastActivity), host.ViewerLocale())));
    }

    /// <summary>
    /// The provenance line's slots as the controls receive them: each a literal value
    /// (<see cref="MetaSlots.Of"/>) or a <see cref="JsonPointerReference"/> into a bound
    /// <see cref="MetaLine"/> — the template header builds it from the
    /// projection's pointers inside its <c>Bind</c> expression.
    /// </summary>
    internal sealed record MetaSlots(
        object TypeStyle, object TypeLabel, object TypeText, object TypeHref,
        object CreatedStyle, object CreatedLabel, object CreatedText,
        object ChangedStyle, object ChangedLabel, object ChangedText)
    {
        /// <summary>The literal slots of <paramref name="line"/>.</summary>
        public static MetaSlots Of(MetaLine line) => new(
            line.TypeStyle, line.TypeLabel, line.TypeText, line.TypeHref,
            line.CreatedStyle, line.CreatedLabel, line.CreatedText,
            line.ChangedStyle, line.ChangedLabel, line.ChangedText);
    }

    /// <summary>
    /// The provenance line composed from platform controls — a wrapping horizontal <c>Stack</c> of
    /// three segments, each a <c>Label</c> and its value: the node type as a <c>Link</c> to its
    /// Configuration area, the two stamps as <c>Label</c>s. No markup: every value is text the
    /// renderer encodes, and the link's href is held to <see cref="LinkHrefs"/> by the renderer.
    /// </summary>
    /// <param name="slots">The values — literals, or pointers into a bound <see cref="MetaLine"/>.</param>
    internal static StackControl MetaRow(MetaSlots slots)
        => Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(24)
            .WithWrap(true)
            .WithStyle("align-items: center;")
            .WithView(MetaSegment(slots.TypeStyle,
                MetaLabel(slots.TypeLabel, HintColor),
                Controls.Link(slots.TypeText, slots.TypeHref).WithStyle(MetaTextStyle + " font-weight: 500;")))
            .WithView(MetaSegment(slots.CreatedStyle,
                MetaLabel(slots.CreatedLabel, RestColor),
                MetaLabel(slots.CreatedText, HintColor)))
            .WithView(MetaSegment(slots.ChangedStyle,
                MetaLabel(slots.ChangedLabel, RestColor),
                MetaLabel(slots.ChangedText, HintColor)));

    private static StackControl MetaSegment(object style, UiControl label, UiControl value)
        => (Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(6) with { Style = style })
            .WithView(label)
            .WithView(value);

    private static LabelControl MetaLabel(object text, string color)
        => Controls.Label(text).WithColor(color).WithStyle(MetaTextStyle);

    /// <summary>
    /// Registers the page a node LANDS ON, and says what it does about provenance. Use this — not
    /// the bare <c>WithView(area, …)</c> — for any area a node type names with
    /// <c>WithDefaultArea</c>.
    ///
    /// <para><b>The default carries the line — in the shell, not on the page.</b> Called without a
    /// verdict, the page is registered as it is and the portal shell shows the node's provenance as
    /// the tooltip of the breadcrumb's current crumb, on every page, so a landing page written
    /// without an opinion about provenance still SHIPS WITH IT (Systemorph/MeshWeaver#4500: the line
    /// used to ride on one renderer, so replacing that renderer dropped it). Until 2026-10-26 the
    /// framework composed a <c>Type · Created · Updated</c> strip above the page here; the maintainer
    /// moved it into the breadcrumb tooltip because a technical strip above a designed page (a
    /// marketing cover above all) looked bad.</para>
    ///
    /// <para><b>The two other answers are declared, never inferred.</b> A page that draws its own
    /// header passes <see cref="NodePageProvenance.RenderedByThePage"/>; a page that wants none passes
    /// <see cref="NodePageProvenance.Declined"/> WITH A REASON. All three are recorded on the
    /// <see cref="LayoutDefinition"/> and readable back through
    /// <see cref="LayoutDefinition.GetNodePageProvenance"/>, so "someone weighed this" and "nobody
    /// thought about it" stop looking identical from outside.</para>
    /// </summary>
    /// <param name="layout">The layout definition to register on.</param>
    /// <param name="area">The landing area — the one this hub's <c>WithDefaultArea</c> names.</param>
    /// <param name="page">The page's own content.</param>
    /// <param name="provenance">The verdict; null means <see cref="NodePageProvenance.FrameworkSupplied"/>.</param>
    public static LayoutDefinition WithNodePage(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, IObservable<UiControl?>> page,
        NodePageProvenance? provenance = null)
    {
        var verdict = provenance ?? NodePageProvenance.FrameworkSupplied;

        // 🚨 Order is load-bearing: every WithView overload funnels through WithNamedRenderer,
        // which REMOVES the area's verdict (LayoutDefinition.NodePages) so a verdict can never
        // outlive the renderer it describes. Record it AFTER registering, never before.
        var registered = layout.WithView(area, page);

        return registered.WithNodePageProvenance(area, verdict);
    }

    /// <summary>
    /// <see cref="WithNodePage(LayoutDefinition, string, Func{LayoutAreaHost, RenderingContext, IObservable{UiControl}}, NodePageProvenance)"/>
    /// for a landing page whose content is produced SYNCHRONOUSLY. Same contract, same default:
    /// no verdict means the shell's breadcrumb carries the provenance line.
    /// </summary>
    /// <typeparam name="T">The UiControl subtype the page factory returns.</typeparam>
    /// <param name="layout">The layout definition to register on.</param>
    /// <param name="area">The landing area — the one this hub's <c>WithDefaultArea</c> names.</param>
    /// <param name="page">The page's own content.</param>
    /// <param name="provenance">The verdict; null means <see cref="NodePageProvenance.FrameworkSupplied"/>.</param>
    public static LayoutDefinition WithNodePage<T>(
        this LayoutDefinition layout,
        string area,
        Func<LayoutAreaHost, RenderingContext, T> page,
        NodePageProvenance? provenance = null) where T : UiControl?
    {
        var verdict = provenance ?? NodePageProvenance.FrameworkSupplied;
        var registered = layout.WithView(area, page);

        return registered.WithNodePageProvenance(area, verdict);
    }

    /// <summary>
    /// Builds a content URL for navigating to a specific layout area of a node.
    /// </summary>
    /// <param name="nodePath">The path of the node</param>
    /// <param name="area">The layout area to navigate to</param>
    /// <param name="queryString">Optional query string (without leading ?)</param>
    /// <returns>The full URL path</returns>
    public static string BuildUrl(string nodePath, string area, string? queryString = null)
    {
        var url = $"/{nodePath}/{area}";
        if (!string.IsNullOrEmpty(queryString))
            url += $"?{queryString}";
        return url;
    }

    /// <summary>
    /// Whether <paramref name="area"/> has a renderer on <paramref name="layout"/> — the ONE probe
    /// behind every "do not offer what this hub cannot do" decision (issue #3604), used by the node
    /// menu (<c>NodeMenuItemsExtensions.WithoutUnrenderableAreas</c>) and by the header's own
    /// Edit / Copy / Move / Delete buttons.
    ///
    /// <para>Why it is needed: the platform emits those affordances, but the RENDERERS for Delete,
    /// Copy, Move, Versions, Pin, Import, Stop sync, Access control and Groups ship in the optional
    /// <c>MeshWeaver.Graph.Views</c> (<c>DefaultViews</c>) package. On a mesh without it, each one
    /// was still offered and every click landed on the layout engine's diagnostic page —
    /// <c>"Area not found — No renderer is registered for area `Delete`"</c>.</para>
    ///
    /// <para>🚨 <b>It FAILS OPEN, and the <see cref="OverviewArea"/> probe is the whole reason.</b>
    /// <c>HasNamedRenderer</c> answers a boolean about something it had to READ, and "this
    /// definition is empty, or is not the one that serves this node" must never be collapsed into
    /// "this area has no renderer" — that direction silently removes Delete, Copy and Move from
    /// every portal at once. A node hub ALWAYS carries <see cref="OverviewArea"/>
    /// (<c>AddDefaultLayoutAreas</c> registers it in the same call that registers the menu, and
    /// nothing can unregister it), so a definition that does not know Overview is one that cannot be
    /// trusted to answer for the rest: everything is kept, and the visible diagnostic page remains
    /// the outcome.</para>
    /// </summary>
    /// <param name="layout">The layout definition of the hub the affordance is rendered on.</param>
    /// <param name="area">The area name the affordance navigates to.</param>
    /// <returns><c>true</c> when the area renders here, or when the definition cannot be trusted to say.</returns>
    internal static bool CanRenderArea(LayoutDefinition? layout, string area)
        => layout is null
           || !layout.HasNamedRenderer(OverviewArea)
           || layout.HasNamedRenderer(area);

    /// <summary>
    /// Returns the Edit menu item if the user has Update permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetEditMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Update))
            return null;
        return new("Edit", EditArea,
            RequiredPermission: Permission.Update, Order: -10, Href: BuildUrl(hubPath, EditArea))
            { LabelKey = "menu.edit" };
    }

    /// <summary>
    /// Returns the Files menu item if the user has Read permission.
    /// </summary>
    public static NodeMenuItemDefinition? GetFilesMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Read))
            return null;
        return new("Files", FilesArea, Order: 25, Href: BuildUrl(hubPath, FilesArea))
            { LabelKey = "menu.files" };
    }

    /// <summary>
    /// The node menu's "Settings…" entry — opens THIS node's settings page
    /// (<c>/{node}/Settings</c>). Offered to a viewer who may read the node: the page itself shows
    /// each tab only under the tab's own permission, and read-only for a viewer without Update.
    /// </summary>
    /// <param name="hubPath">The node path.</param>
    /// <param name="perms">The viewer's effective permissions on the node.</param>
    public static NodeMenuItemDefinition? GetSettingsMenuItem(string hubPath, Permission perms)
    {
        if (!perms.HasFlag(Permission.Read))
            return null;
        return new("Settings…", SettingsArea, Order: 45, Href: BuildUrl(hubPath, SettingsArea))
            { LabelKey = "menu.settings" };
    }

    /// <summary>
    /// Returns the Threads menu item (always visible).
    /// </summary>
    public static NodeMenuItemDefinition GetThreadsMenuItem(string hubPath)
        => new("Threads", ThreadsArea, Order: 50, Href: BuildUrl(hubPath, ThreadsArea))
            { LabelKey = "menu.threads" };

    /// <summary>
    /// Gets the display name for a node type with count (e.g., "Project (5)").
    /// </summary>
    public static string GetGroupDisplayName(string nodeType, int count)
    {
        // Extract just the last segment if it's a path
        var typeName = nodeType.Contains('/') ? nodeType.Split('/').Last() : nodeType;
        // Capitalize first letter
        var display = char.ToUpper(typeName[0]) + typeName.Substring(1);
        return $"{display} ({count})";
    }

    /// <summary>
    /// Renders a compact thumbnail/card view of a node for use in catalogs and lists.
    /// A TEMPLATE: the card is declared by path and its view binds the node through
    /// <c>IMeshNodeStreamCache</c>, so the area emits at once rather than after the node's read
    /// (Doc/GUI/DataBinding → "Templates first, data later").
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Thumbnail(LayoutAreaHost host, RenderingContext _)
        => Observable.Return<UiControl?>(MeshNodeThumbnailControl.ForPath(host.Hub.Address.ToString()));

    /// <summary>
    /// Renders the Metadata area showing node properties (name, path, type, parent) as a TEMPLATE:
    /// a heading and a key/value list of <c>Label</c>s and <c>Link</c>s, emitted at once and bound
    /// to <see cref="MetadataView"/> — a projection of the hub's own node — so nothing waits on the
    /// node and a rename or move updates the values in place.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Metadata(LayoutAreaHost host, RenderingContext _1)
    {
        var hubPath = host.Hub.Address.ToString();
        // The viewer's language is read HERE, on the render turn; the projection runs later, off it.
        var locale = host.ViewerLocale();
        var title = LocalizationCatalog.Get(MetadataTitleKey, locale);
        var nameLabel = LocalizationCatalog.Get(MetadataNameKey, locale);
        var pathLabel = LocalizationCatalog.Get(MetadataPathKey, locale);
        var typeLabel = LocalizationCatalog.Get(MetaTypeKey, locale);
        var parentLabel = LocalizationCatalog.Get(MetadataParentKey, locale);
        return Observable.Return<UiControl?>(host.Workspace.GetMeshNodeStream()
            .Select(node => ProjectMetadata(node, hubPath, locale))
            .DistinctUntilChanged()
            .Bind(p => MetadataTemplate(
                    title, p.BackText, p.BackHref, p.NotFoundStyle, p.NotFound, p.FieldsStyle,
                    nameLabel, p.Name, pathLabel, p.Path,
                    p.TypeStyle, typeLabel, p.Type,
                    p.ParentStyle, parentLabel, p.ParentPath, p.ParentHref),
                MetadataDataId));
    }

    /// <summary>The <c>/data</c> id the Metadata page's projection is published under.</summary>
    internal const string MetadataDataId = "nodeMetadata";

    /// <summary>What the Metadata page shows — every field a string, absent rows carry the hidden style.</summary>
    /// <param name="BackText">The node's name — the link back to its Overview.</param>
    /// <param name="BackHref">The node's Overview.</param>
    /// <param name="NotFoundStyle">Visible only when the node cannot be read.</param>
    /// <param name="NotFound">"Node not found.", localized.</param>
    /// <param name="FieldsStyle">Hidden when the node cannot be read.</param>
    /// <param name="Name">The node's name.</param>
    /// <param name="Path">The node's path.</param>
    /// <param name="TypeStyle">Hidden for a node without a type.</param>
    /// <param name="Type">The node's type.</param>
    /// <param name="ParentStyle">Hidden for a root node.</param>
    /// <param name="ParentPath">The parent's path.</param>
    /// <param name="ParentHref">The parent's Overview.</param>
    internal sealed record MetadataView(
        string BackText, string BackHref, string NotFoundStyle, string NotFound, string FieldsStyle,
        string Name, string Path, string TypeStyle, string Type,
        string ParentStyle, string ParentPath, string ParentHref);

    /// <summary>Projects the node into <see cref="MetadataView"/>. Pure; the viewer's language is passed in.</summary>
    internal static MetadataView ProjectMetadata(MeshNode? node, string hubPath, string? locale)
    {
        var path = node?.Path ?? hubPath;
        var backText = node?.Name ?? path.Split('/').LastOrDefault() ?? path;
        if (node is null)
            return new MetadataView(backText, $"/{path}/{OverviewArea}", MetaLine.Visible,
                LocalizationCatalog.Get(NodeNotFoundKey, locale), NodePageProjections.Hidden,
                "", "", NodePageProjections.Hidden, "", NodePageProjections.Hidden, "", "");
        var parentPath = node.GetParentPath() ?? "";
        return new MetadataView(
            backText, $"/{path}/{OverviewArea}", NodePageProjections.Hidden, "", MetaLine.Visible,
            node.Name ?? node.Id, path,
            string.IsNullOrEmpty(node.NodeType) ? NodePageProjections.Hidden : MetaLine.Visible, node.NodeType ?? "",
            string.IsNullOrEmpty(parentPath) ? NodePageProjections.Hidden : MetaLine.Visible,
            parentPath, string.IsNullOrEmpty(parentPath) ? "" : $"/{parentPath}/{OverviewArea}");
    }

    /// <summary>
    /// The Metadata page's controls: an <c>H2</c> with a link back to the node, then one
    /// label/value row per field. Values are literals or pointers into <see cref="MetadataView"/>.
    /// </summary>
    internal static StackControl MetadataTemplate(
        object title, object backText, object backHref, object notFoundStyle, object notFound, object fieldsStyle,
        object nameLabel, object name, object pathLabel, object path,
        object typeStyle, object typeLabel, object type,
        object parentStyle, object parentLabel, object parentPath, object parentHref)
        => Controls.Stack.WithWidth("100%").WithVerticalGap(8)
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(16)
                .WithStyle("align-items: baseline;")
                .WithView(Controls.H2(title))
                .WithView(Controls.Link(backText, backHref)))
            .WithView(Controls.Label(notFound).WithColor(HintColor) with { Style = notFoundStyle })
            .WithView((Controls.Stack.WithVerticalGap(6) with { Style = fieldsStyle })
                .WithView(KeyValueRow(MetaLine.Visible, nameLabel, Controls.Label(name)))
                .WithView(KeyValueRow(MetaLine.Visible, pathLabel, Controls.Label(path)))
                .WithView(KeyValueRow(typeStyle, typeLabel, Controls.Label(type)))
                .WithView(KeyValueRow(parentStyle, parentLabel, Controls.Link(parentPath, parentHref))));

    /// <summary>One label/value row: the label in bold, then the value.</summary>
    private static StackControl KeyValueRow(object style, object label, UiControl value)
        => (Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(8) with { Style = style })
            .WithView(Controls.Label(label).WithWeight(FontWeight.Bold))
            .WithView(value);


    private static string GetNodeContent(MeshNode? node)
    {
        if (node?.Content == null)
            return string.Empty;

        // Handle MarkdownContent (from MarkdownFileParser)
        if (node.Content is MarkdownContent markdownContent)
            return markdownContent.Content;

        // Handle MarkdownDocument/MarkdownContent content (JSON with $type and content fields)
        if (node.Content is System.Text.Json.JsonElement jsonElement)
        {
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

        // Handle Story content using reflection to avoid circular dependency
        var nodeType = node.NodeType?.ToLowerInvariant();
        if (nodeType == "story")
        {
            // Try to get the Text property via reflection
            var textProperty = node.Content.GetType().GetProperty("Text");
            if (textProperty != null)
            {
                var textValue = textProperty.GetValue(node.Content) as string;
                if (!string.IsNullOrEmpty(textValue))
                    return textValue;
            }
        }

        // Check for NodeDescription
        if (node.Content is NodeDescription nd)
            return nd.Description;

        return string.Empty;
    }


    /// <summary>
    /// Renders the Search view showing nodes as thumbnails with search.
    /// Uses MeshSearchControl for unified search and display.
    /// For NodeType nodes, shows instances of that type (nodeType:name scope:subtree).
    /// For instance nodes, uses CatalogQuery if set, otherwise defaults to namespace query.
    /// Excludes NodeType nodes from results (use NodeTypes area to view those).
    /// Render mode is determined by CatalogMode property (hierarchical or grouped).
    /// Reads search term from ?q= query parameter.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Search(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        // Every catalog knob is URL-driven so one area serves every shape — read by
        // ReadCatalogOptions (see the "Mesh Search" doc): ?groupBy ?subtree ?searchBar
        // ?emptyMessage ?loading ?counts ?limit ?maxRows ?maxColumns ?collapsible ?reactive
        // ?title ?placeholder ?q. The fallback render mode differs by branch (NodeType
        // instances → Hierarchical, content catalog → NamespaceTree).
        //
        // 🚨 Which catalog to show is decided from the hub's CONFIGURATION, never by reading the
        // node (Doc/GUI/DataBinding → "Templates first, data later"). NodeType catalog mode is
        // used when either (a) the hub opts in via NodeTypeCatalogMode (e.g. AddNodeTypeView), or
        // (b) the node is a NodeType DEFINITION — this hub then applies the "NodeType" type's own
        // configuration, which the activation funnel records as NodeTypePathHolder. Both branches
        // are templates emitted at once: a definition's instance list — whose query is computed
        // from the definition — renders into its own skeleton slot (NodeTypeInstances).
        if (!IsNodeTypeCatalog(host.Hub.Configuration))
            return Observable.Return<UiControl?>(InstanceCatalog(host, hubPath));
        return Observable.Return<UiControl?>(
            NodeTypeCatalogTemplate(host.Hub.Address, hubPath, host.Reference.Id?.ToString()));
    }

    /// <summary>
    /// Whether <see cref="Search"/> serves a NodeType's catalog of its instances — answered from the
    /// hub configuration alone, so the ordinary catalog never waits on a read.
    /// </summary>
    /// <param name="configuration">The node hub's configuration.</param>
    internal static bool IsNodeTypeCatalog(MessageHubConfiguration configuration)
        => configuration.Get<NodeTypeCatalogMode>() != null
           || configuration.Get<NodeTypePathHolder>()?.Path == MeshNode.NodeTypePath;

    /// <summary>
    /// The instance-node catalog: this node's own content. Defaults to the re-rooting graph
    /// navigator: the next populated level below (skipping empty namespace segments) + the
    /// ancestors above, navigable along the graph's edges. Every knob is still ?param-overridable —
    /// ?groupBy=tree restores the lazy namespace tree, ?groupBy=flat the grid, etc. (The Space
    /// "Children" catalog stays on the namespace tree.) A template: the GUI runs the query.
    /// </summary>
    private static UiControl InstanceCatalog(LayoutAreaHost host, string hubPath)
        => WithBreadcrumbs(
            BuildCatalog(hubPath, ReadCatalogOptions(host, MeshSearchRenderMode.GraphNavigator)),
            hubPath);

    /// <summary>
    /// A NodeType definition's catalog page — a TEMPLATE that reads nothing: the breadcrumb trail
    /// and a <see cref="NodeTypeInstancesArea"/> slot with the skeleton loading shape. The slot
    /// carries the page's own query string (<c>?groupBy</c>, <c>?q</c>, …), so every catalog knob
    /// still reaches the list.
    /// </summary>
    /// <param name="address">The node hub's address — the slot renders on the same hub.</param>
    /// <param name="hubPath">The definition's path (the breadcrumb trail).</param>
    /// <param name="queryString">The Search area's reference id: its query string, forwarded.</param>
    internal static UiControl NodeTypeCatalogTemplate(object address, string hubPath, string? queryString)
        => WithBreadcrumbs(
            Controls.LayoutArea(address, NodeTypeInstancesArea, queryString)
                .WithSpinnerType(SpinnerType.Skeleton),
            hubPath);

    /// <summary>
    /// The <see cref="NodeTypeInstancesArea"/> slot: a NodeType definition's list of its instances.
    /// Its hidden query and create link are built from the DEFINITION
    /// (<see cref="NodeTypeDefinition.DefaultNamespace"/>,
    /// <see cref="NodeTypeDefinition.RestrictedToNamespaces"/>), which only the node carries — a
    /// STRUCTURE read, deferred to this slot alone (Doc/GUI/DataBinding → the toolkit's last row).
    /// The list itself is still run by the GUI, and the slot re-renders only when the query or the
    /// create link changes.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="_">The rendering context (unused).</param>
    /// <returns>The instance list, or the ordinary catalog while no definition is readable.</returns>
    [Browsable(false)]
    public static IObservable<UiControl?> NodeTypeInstances(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var options = host.Hub.JsonSerializerOptions;
        return host.Workspace.GetMeshNodeStream()
            .Select(node => node is null
                ? null
                : NodeTypeCatalogQuery.From(node.Path, node.ContentAs<NodeTypeDefinition>(options)))
            .DistinctUntilChanged()
            .Select(query => (UiControl?)(query is null
                // No definition to read (yet): the ordinary catalog, as before.
                ? BuildCatalog(hubPath, ReadCatalogOptions(host, MeshSearchRenderMode.GraphNavigator))
                // Instances of a NodeType default to a hierarchical list; every knob is still
                // ?param-overridable (?groupBy ?searchBar ?maxColumns ?emptyMessage ?title …).
                : BuildNodeTypeSearch(hubPath, ReadCatalogOptions(host, MeshSearchRenderMode.Hierarchical), query)));
    }

    /// <summary>
    /// What a NodeType definition decides about its instance list: the hidden query and the create
    /// link. Pure — <see cref="From"/> is the whole decision.
    /// </summary>
    /// <param name="HiddenQuery">The query listing the type's instances.</param>
    /// <param name="CreateHref">The create link, pre-filled with the type and its namespaces.</param>
    internal sealed record NodeTypeCatalogQuery(string HiddenQuery, string CreateHref)
    {
        /// <summary>The query and create link for the definition at <paramref name="nodeTypePath"/>.</summary>
        /// <param name="nodeTypePath">The definition node's path.</param>
        /// <param name="definition">The definition, or null when the content is not one.</param>
        /// <returns>The decision.</returns>
        public static NodeTypeCatalogQuery From(string nodeTypePath, NodeTypeDefinition? definition)
        {
            // If DefaultNamespace is set, scope to that namespace and filter by this NodeType
            // (canonical group case — instances declare nodeType = path).
            //
            // Otherwise scope by namespace + descendants. The nodeType filter is dropped here
            // because LOCAL NodeType nodes (e.g. FutuRe/EuropeRe/LineOfBusiness inside the
            // FutuRe/LineOfBusiness root type) reuse the GROUP-level nodeType on their instances —
            // filtering by the local NodeType node's own path matches zero instances.
            //
            // `is:main` drops satellites that carry an explicit MainNode pointer — _Activity
            // compile-activity nodes (NodeType="Activity", MainNode=<owner>) are the case the old
            // `-nodeType:Code` enumeration missed when compile-activity landed, surfacing a
            // "Compile {path}" row. It does NOT catch Source/Code files: the file-system loader
            // leaves their MainNode null, so they read as main nodes — hence `-nodeType:Code`
            // stays. `-nodeType:NodeType -nodeType:Markdown` also stay: definition nodes are main
            // nodes too.
            var defaultNs = definition?.DefaultNamespace;
            var hiddenQuery = defaultNs != null
                ? $"nodeType:{nodeTypePath} namespace:{defaultNs}"
                : $"namespace:{nodeTypePath} scope:subtree is:main -nodeType:Code -nodeType:NodeType -nodeType:Markdown";

            var createQs = $"type={Uri.EscapeDataString(nodeTypePath)}";
            if (!string.IsNullOrEmpty(defaultNs))
                createQs += $"&namespace={Uri.EscapeDataString(defaultNs)}";
            if (definition?.RestrictedToNamespaces is { Count: > 0 } nsRestrictions)
                createQs += $"&namespaces={string.Join(",", nsRestrictions.Select(Uri.EscapeDataString))}";
            return new NodeTypeCatalogQuery(hiddenQuery, $"/create?{createQs}");
        }
    }

    /// <summary>The instance list of a NodeType — a query the GUI runs. Pure.</summary>
    /// <param name="hubPath">The definition's hub path (the search's namespace).</param>
    /// <param name="typeOpts">The catalog knobs read from the page's query string.</param>
    /// <param name="query">The definition's query and create link.</param>
    /// <returns>The search control.</returns>
    internal static MeshSearchControl BuildNodeTypeSearch(string hubPath, CatalogOptions typeOpts, NodeTypeCatalogQuery query)
    {
        var typeSearch = Controls.MeshSearch
            .WithHiddenQuery(query.HiddenQuery)
            .WithVisibleQuery(typeOpts.SearchTerm ?? "")
            .WithNamespace(hubPath)
            .WithPlaceholder(typeOpts.Placeholder)
            .WithRenderMode(typeOpts.Mode)
            .WithShowSearchBox(typeOpts.ShowSearchBox)
            .WithShowEmptyMessage(typeOpts.ShowEmptyMessage)
            .WithMaxColumns(typeOpts.MaxColumns)
            .WithCreateHref(query.CreateHref);
        if (!string.IsNullOrEmpty(typeOpts.GroupByProperty))
            typeSearch = typeSearch.WithGroupBy(typeOpts.GroupByProperty);
        return typeSearch;
    }

    /// <summary>Best-effort truthy parse for boolean query params (<c>true/1/yes/on</c>).</summary>
    internal static bool ParseTruthy(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value is "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Maps the <c>?groupBy=</c> query value to a <see cref="MeshSearchRenderMode"/> and, for the
    /// <see cref="MeshSearchRenderMode.Grouped"/> modes, the node property to group on. Unknown /
    /// missing values fall back to <paramref name="fallback"/>.
    /// <list type="bullet">
    ///   <item><c>namespace</c> (a.k.a. <c>ns</c>, <c>tree</c>) → <see cref="MeshSearchRenderMode.NamespaceTree"/> — lazy per-level drilldown.</item>
    ///   <item><c>type</c> (a.k.a. <c>nodeType</c>) → <see cref="MeshSearchRenderMode.Grouped"/> by <c>NodeType</c>.</item>
    ///   <item><c>category</c> → <see cref="MeshSearchRenderMode.Grouped"/> by <c>Category</c>.</item>
    ///   <item><c>flat</c> (a.k.a. <c>none</c>, <c>grid</c>) → <see cref="MeshSearchRenderMode.Flat"/>.</item>
    ///   <item><c>hierarchy</c> (a.k.a. <c>hierarchical</c>) → <see cref="MeshSearchRenderMode.Hierarchical"/>.</item>
    /// </list>
    /// </summary>
    internal static (MeshSearchRenderMode Mode, string? GroupByProperty) ResolveCatalogView(
        string? groupBy, MeshSearchRenderMode fallback)
        => groupBy?.ToLowerInvariant() switch
        {
            "namespace" or "ns" or "tree" => (MeshSearchRenderMode.NamespaceTree, null),
            "graph" or "nav" or "navigator" => (MeshSearchRenderMode.GraphNavigator, null),
            "type" or "nodetype" => (MeshSearchRenderMode.Grouped, "NodeType"),
            "category" or "cat" => (MeshSearchRenderMode.Grouped, "Category"),
            "flat" or "none" or "grid" => (MeshSearchRenderMode.Flat, null),
            "hierarchy" or "hierarchical" => (MeshSearchRenderMode.Hierarchical, null),
            _ => (fallback, null)
        };

    /// <summary>
    /// Every catalog knob, each overridable via a query param so the single <see cref="Search"/>
    /// area serves every shape. Defaults reproduce the classic namespace-tree catalog.
    /// </summary>
    internal sealed record CatalogOptions
    {
        /// <summary>Render mode — from <c>?groupBy</c> via <see cref="ResolveCatalogView"/>.</summary>
        public MeshSearchRenderMode Mode { get; init; } = MeshSearchRenderMode.NamespaceTree;
        /// <summary>Node property to group on for the Grouped modes (NodeType / Category), else null.</summary>
        public string? GroupByProperty { get; init; }
        /// <summary>Whether to query the whole descendant subtree (the default) vs only direct
        /// children. <c>?subtree=false</c> restricts to direct children.</summary>
        public bool IncludeSubtree { get; init; } = true;
        /// <summary><c>?q=</c> — the initial search term (visible query).</summary>
        public string? SearchTerm { get; init; }
        /// <summary><c>?searchBar=false</c> hides the search box.</summary>
        public bool ShowSearchBox { get; init; } = true;
        /// <summary><c>?emptyMessage=true</c> shows the "No items found." message.</summary>
        public bool ShowEmptyMessage { get; init; }
        /// <summary><c>?loading=true</c> shows the skeleton loading indicator.</summary>
        public bool ShowLoadingIndicator { get; init; }
        /// <summary><c>?counts=false</c> hides the per-section counts.</summary>
        public bool SectionCounts { get; init; } = true;
        /// <summary><c>?limit=N</c> — items per section.</summary>
        public int ItemLimit { get; init; } = 50;
        /// <summary><c>?maxRows=N</c> — collapsed rows per section.</summary>
        public int MaxRows { get; init; } = 3;
        /// <summary><c>?maxColumns=N</c> — grid columns.</summary>
        public int MaxColumns { get; init; } = 3;
        /// <summary><c>?collapsible=false</c> keeps every section expanded.</summary>
        public bool Collapsible { get; init; } = true;
        /// <summary><c>?hidden=true</c> reveals underscore-prefixed satellite namespaces
        /// (<c>_Entitlements</c>, <c>_Access</c>, …) that the catalog hides by default.</summary>
        public bool IncludeHidden { get; init; }
        /// <summary><c>?reactive=false</c> disables live updates on data change.</summary>
        public bool Reactive { get; init; } = true;
        /// <summary><c>?title=</c> — the section title.</summary>
        public string Title { get; init; } = "Catalog";
        /// <summary><c>?placeholder=</c> — the search box placeholder.</summary>
        public string Placeholder { get; init; } = "Search... (use @ for references)";
    }

    /// <summary>
    /// Whether a catalog should query the whole descendant subtree, by render mode.
    ///
    /// <para>🚨 Only the NAMESPACE TREE needs it — that renderer reveals deeper levels lazily and
    /// would otherwise stop at direct children. Every other mode shows ONE level, so a subtree
    /// query buys nothing and costs correctness: the item limit is spent on descendants before the
    /// level being displayed is complete.</para>
    ///
    /// <para>Measured on the 14-lesson <c>AdvancedBusinessRules</c> course, whose Contents index is
    /// this catalog: the subtree query returned only <b>5 of the 14 lessons</b>, in no useful order,
    /// interleaved with every <c>Quiz</c>, <c>MyExercises</c>, <c>Solution/</c> and <c>Exercise/</c>
    /// node, and was STILL truncated. Scoped to children it returns all 14 in order, plus the three
    /// real siblings — 17 rows, complete. Nothing about depth is lost: each card carries a
    /// drill-down into its own Search area, which is how browsing was always meant to descend.</para>
    /// </summary>
    internal static bool DefaultIncludeSubtree(MeshSearchRenderMode mode)
        => mode == MeshSearchRenderMode.NamespaceTree;

    /// <summary>Reads every catalog knob from the layout area's query string (see <see cref="CatalogOptions"/>).
    /// Booleans accept <c>true/1/yes/on</c> (and their negation by absence); ints must be positive.</summary>
    private static CatalogOptions ReadCatalogOptions(LayoutAreaHost host, MeshSearchRenderMode fallbackMode)
    {
        var (mode, groupProp) = ResolveCatalogView(host.GetQueryStringParamValue("groupBy")?.Trim(), fallbackMode);
        return new CatalogOptions
        {
            Mode = mode,
            GroupByProperty = groupProp,
            // 🚨 The subtree is the NAMESPACE TREE's need, not every catalog's. Defaulting it on
            // for all modes made the Contents index of a multi-part document unusable: on a
            // 14-lesson course the query returned the whole descendant tree — every Quiz,
            // MyExercises invite, Solution/ and Exercise/ node — and the item limit was spent
            // before most of the lessons were reached. Measured on AdvancedBusinessRules: only
            // 5 of 14 lessons appeared, in no particular order, and the result was still
            // truncated. `scope:children` returns the 14 in order plus the three real siblings,
            // 17 rows, complete. Depth is not lost — every card carries a drill-down to its own
            // Search area (WithDrillDownArea below), which is how browsing was meant to descend.
            // `?subtree=true` still opts back in.
            IncludeSubtree = ReadBool(host, "subtree", DefaultIncludeSubtree(mode)),
            SearchTerm = host.GetQueryStringParamValue("q")?.Trim(),
            ShowSearchBox = ReadBool(host, "searchBar", true),
            ShowEmptyMessage = ReadBool(host, "emptyMessage", false),
            ShowLoadingIndicator = ReadBool(host, "loading", false),
            SectionCounts = ReadBool(host, "counts", true),
            ItemLimit = ReadInt(host, "limit", 50),
            MaxRows = ReadInt(host, "maxRows", 3),
            MaxColumns = ReadInt(host, "maxColumns", 3),
            Collapsible = ReadBool(host, "collapsible", true),
            IncludeHidden = ReadBool(host, "hidden", false),
            Reactive = ReadBool(host, "reactive", true),
            Title = host.GetQueryStringParamValue("title")?.Trim() is { Length: > 0 } t ? t : "Catalog",
            Placeholder = host.GetQueryStringParamValue("placeholder")?.Trim() is { Length: > 0 } p
                ? p : "Search... (use @ for references)",
        };
    }

    /// <summary>Reads a boolean query param, falling back to <paramref name="fallback"/> when absent.</summary>
    private static bool ReadBool(LayoutAreaHost host, string name, bool fallback)
        => host.GetQueryStringParamValue(name) is { } v ? ParseTruthy(v) : fallback;

    /// <summary>Reads a positive-int query param, falling back to <paramref name="fallback"/> when absent/invalid.</summary>
    private static int ReadInt(LayoutAreaHost host, string name, int fallback)
        => int.TryParse(host.GetQueryStringParamValue(name), out var v) && v > 0 ? v : fallback;

    /// <summary>
    /// Builds the node-content catalog (the shared body of the <see cref="Search"/> instance view and
    /// the legacy <c>Children</c> area): a <see cref="MeshSearchControl"/> over
    /// <c>namespace:{nodePath}</c> — the node's DIRECT CHILDREN — or over
    /// <c>namespace:{nodePath} scope:subtree</c> (the whole descendant subtree) when the caller asks
    /// for it, excluding NodeType definitions. Every display knob comes from <paramref name="o"/>
    /// (see <see cref="CatalogOptions"/>).
    ///
    /// <para>🚨 The scope is decided by RENDER MODE, not by a blanket default — see
    /// <see cref="DefaultIncludeSubtree"/>. Only the namespace tree takes the subtree, because it
    /// alone reveals deeper levels lazily; every other mode renders one level, where a subtree query
    /// cannot show more and merely spends the item limit on descendants. <c>?subtree=true</c>
    /// overrides either way.</para>
    /// </summary>
    internal static MeshSearchControl BuildCatalog(string nodePath, CatalogOptions o)
    {
        var scope = o.IncludeSubtree ? " scope:subtree" : "";
        var search = Controls.MeshSearch
            .WithTitle(o.Title)
            // Exclude NodeType definitions — they belong to type admin, not the instance catalog.
            // is:content, not context:search — this lists a node's children for a person to browse.
            // It said "search" only to borrow that context's registration filtering, and then still
            // needed `-nodeType:NodeType` on top because borrowing does not say what you mean.
            .WithHiddenQuery($"namespace:{nodePath}{scope} is:main is:content")
            .WithVisibleQuery(o.SearchTerm ?? "")
            .WithNamespace(nodePath)
            .WithPlaceholder(o.Placeholder)
            .WithReactiveMode(o.Reactive)
            .WithShowSearchBox(o.ShowSearchBox)
            .WithShowEmptyMessage(o.ShowEmptyMessage)
            .WithShowLoadingIndicator(o.ShowLoadingIndicator)
            .WithRenderMode(o.Mode)
            .WithSectionCounts(o.SectionCounts)
            .WithItemLimit(o.ItemLimit)
            .WithMaxRows(o.MaxRows)
            .WithMaxColumns(o.MaxColumns)
            .WithCollapsibleSections(o.Collapsible)
            // Each card/folder gets a secondary "Drill down" link to /{path}/Search,
            // so users keep browsing INTO a namespace; the primary click still opens
            // the node's default page /{path} (empty area, never a hardcoded "Overview").
            // Governance satellites stay out of a browsing catalog by default — ?hidden=true
            // reveals them (MeshSearchView.HasSatelliteSegmentUnder is the rule).
            .WithIncludeHidden(o.IncludeHidden)
            .WithDrillDownArea(SearchArea)
            .WithCreateHref($"/{nodePath}/{CreateNodeArea}?namespace={Uri.EscapeDataString(nodePath)}");
        return string.IsNullOrEmpty(o.GroupByProperty) ? search : search.WithGroupBy(o.GroupByProperty);
    }

    /// <summary>
    /// Builds a breadcrumb trail for <paramref name="nodePath"/>: each ANCESTOR
    /// segment is a <c>Controls.NavLink</c> to that ancestor's DEFAULT page
    /// (<c>/{cumulative}</c> — empty area, never a hardcoded "Overview"), separated
    /// by a "/" glyph; the LAST segment (the current node) is plain bold text.
    /// Returns null when the path has no ancestors (single segment / empty) so the
    /// caller can skip an empty row.
    /// </summary>
    private static UiControl? BuildBreadcrumbs(string nodePath)
    {
        var segments = (nodePath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
            return null;

        var row = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("align-items: center; gap: 6px; flex-wrap: wrap; margin-bottom: 12px; font-size: 0.9rem;");

        var cumulative = "";
        for (var i = 0; i < segments.Length; i++)
        {
            cumulative = i == 0 ? segments[i] : $"{cumulative}/{segments[i]}";
            // The platform has no Breadcrumb control yet: a horizontal Stack of links separated by
            // a glyph Label is the composition until it does. The "/" glyph needs no translation.
            if (i > 0)
                row = row.WithView(Controls.Label("/").WithColor(HintColor));

            if (i < segments.Length - 1)
                // Ancestor → default page /{cumulative} (empty area).
                row = row.WithView(Controls.NavLink(segments[i], $"/{cumulative}"));
            else
                // Current node — plain bold text, not a link.
                row = row.WithView(Controls.Label(segments[i]).WithWeight(FontWeight.Bold));
        }

        return row;
    }

    /// <summary>
    /// Prepends the <see cref="BuildBreadcrumbs"/> trail to a Search/catalog control so
    /// every node's Search area shows where the user is and lets them step back up to any
    /// ancestor's default page. When the path has no ancestors the catalog is returned
    /// unchanged.
    /// </summary>
    private static UiControl WithBreadcrumbs(UiControl catalog, string nodePath)
    {
        var crumbs = BuildBreadcrumbs(nodePath);
        return crumbs is null
            ? catalog
            : Controls.Stack.WithWidth("100%").WithView(crumbs).WithView(catalog);
    }


    /// <summary>
    /// Renders the Threads catalog showing child Thread nodes using MeshSearchControl.
    /// Includes a "Create Thread" button for starting new conversations.
    /// </summary>
    [Browsable(false)]
    public static UiControl Threads(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var createUrl = $"/{hubPath}/Create?type={Uri.EscapeDataString("Thread")}&namespace={Uri.EscapeDataString($"{hubPath}/_Thread")}";

        return Controls.Stack
            .WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithStyle("justify-content: flex-end; padding: 0 0 12px 0;")
                .WithView(Controls.Button(host.Localize("ui.createThread"))
                    .WithAppearance(Appearance.Accent)
                    .WithIconStart(FluentIcons.Add())
                    .WithNavigateToHref(createUrl)))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery($"nodeType:Thread namespace:{hubPath}/_Thread sort:lastModified-desc")
                .WithNamespace(hubPath)
                .WithRenderMode(MeshSearchRenderMode.Flat));
    }

    /// <summary>
    /// Renders the NodeTypes view showing NodeType nodes defined at this level.
    /// Shows the node's own type (if any) and any NodeType children.
    /// Accessible from the menu as a separate page.
    ///
    /// <para>A TEMPLATE (Doc/GUI/DataBinding → "Templates first, data later"): the own type comes
    /// from the hub's configuration (<see cref="NodeTypePathHolder"/> — the type this hub was bound
    /// to), its card binds the type node by path, and the children are a
    /// <see cref="MeshSearchControl"/> whose query the GUI runs. It used to wait for the node, a
    /// one-shot query snapshot and a point read of the type before it emitted anything, and then
    /// froze that snapshot: a NodeType added at this level never appeared until the page was
    /// reloaded.</para>
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> NodeTypes(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(NodeTypesTemplate(
            host.Hub.Address.ToString(),
            host.Hub.Configuration.Get<NodeTypePathHolder>()?.Path,
            host.ViewerLocale()));

    /// <summary>
    /// The NodeTypes page for <paramref name="nodePath"/> — pure, so its shape is assertable
    /// without a hub.
    /// </summary>
    /// <param name="nodePath">The node whose level is listed.</param>
    /// <param name="ownTypePath">The node's own NodeType path, or null when it has none.</param>
    /// <param name="locale">The viewer's locale, for the headings.</param>
    internal static UiControl NodeTypesTemplate(string nodePath, string? ownTypePath, string? locale)
    {
        var stack = Controls.Stack.WithWidth("100%");

        if (!string.IsNullOrEmpty(ownTypePath))
            stack = stack
                .WithView(Controls.H3(LocalizationCatalog.Get("node.types.ownType", locale))
                    .WithStyle("margin: 0 0 16px 0;"))
                .WithView(Controls.LayoutGrid.WithSkin(s => s.WithSpacing(2))
                    .WithView(MeshNodeThumbnailControl.ForPath(ownTypePath),
                        itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4)));

        return stack
            .WithView(Controls.H3(LocalizationCatalog.Get("node.types.inNamespace", locale, nodePath))
                .WithStyle(string.IsNullOrEmpty(ownTypePath) ? "margin: 0 0 16px 0;" : "margin: 24px 0 16px 0;"))
            .WithView(Controls.MeshSearch
                .WithHiddenQuery(NodeTypesAtLevelQuery(nodePath, ownTypePath))
                .WithNamespace(nodePath)
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(true)
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithMaxColumns(3));
    }

    /// <summary>
    /// The query listing the NodeTypes defined at <paramref name="nodePath"/>'s level. The node's own
    /// type, when it lives at that level, is already shown as the own-type card above the list, so
    /// the query excludes it (<c>-path:</c>) — the GUI runs the query, so the de-duplication the
    /// hub-side loop used to do has to be expressed in the query itself.
    /// </summary>
    /// <param name="nodePath">The node whose level is listed.</param>
    /// <param name="ownTypePath">The node's own NodeType path, or null when it has none.</param>
    internal static string NodeTypesAtLevelQuery(string nodePath, string? ownTypePath)
        => string.IsNullOrEmpty(ownTypePath)
            ? $"namespace:{nodePath} nodeType:{MeshNode.NodeTypePath} sort:order"
            : $"namespace:{nodePath} nodeType:{MeshNode.NodeTypePath} -path:{ownTypePath} sort:order";

    private static DateTime GetWeekStart(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }

    private static string GetPlatforms(MeshNode node)
    {
        if (node.Content is System.Text.Json.JsonElement json && json.TryGetProperty("platforms", out var platformsProp))
        {
            if (platformsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var platforms = new List<string>();
                foreach (var p in platformsProp.EnumerateArray())
                {
                    if (p.GetString() is string platform)
                        platforms.Add(platform);
                }
                return string.Join(" • ", platforms);
            }
        }
        return "";
    }

    /// <summary>
    /// Renders a file browser for the node's content directory.
    /// Uses FileBrowserControl to display and manage files in the content collection.
    /// Reads ?collection= query parameter to select which collection to browse.
    /// </summary>
    [Browsable(false)]
    public static UiControl Files(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var backHref = BuildUrl(hubPath, OverviewArea);

        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.Where(c => c.IsEditable).ToList();

        var stack = Controls.Stack
            .WithView(Controls.Button(host.Localize("common.back"))
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref));

        if (collections is not { Count: > 0 })
            return stack;

        foreach (var col in collections)
        {
            if (collections.Count > 1)
                stack = stack.WithView(Controls.Title(col.DisplayName ?? col.Name, 3));

            var colConfig = col;
            // Navigation mirrors into the collection's OWN URL space —
            // /{node}/{collection}/{p1}/{p2} (the collection-named layout area) — so the
            // address bar carries the full path under the MOUNTED collection name (a
            // collection can be mounted under any name), and deep links / refresh land in
            // the right folder. The Files tab itself always shows the collection roots.
            stack = stack.WithView(new FileBrowserControl(colConfig.Name)
                .WithCollectionConfiguration(colConfig)
                .WithCollectionInfo(colConfig.SourceType, colConfig.BasePath, colConfig.Settings)
                .WithUrlBasePath(BuildUrl(hubPath, ContentCollectionsExtensions.EncodeCollectionName(colConfig.Name)))
                .CreatePath());
        }

        return stack;
    }

    private static UiControl BuildFilesView(LayoutAreaHost host, string hubPath, bool readOnly)
    {
        var backHref = BuildUrl(hubPath, OverviewArea);

        var stack = Controls.Stack
            .WithView(Controls.Button(host.Localize("common.back"))
                .WithAppearance(Appearance.Lightweight)
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithNavigateToHref(backHref));

        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.Where(c => c.IsEditable).ToList();

        if (collections is not { Count: > 0 })
            return stack;

        if (collections.Count == 1)
        {
            stack = stack.WithView(new FileBrowserControl(collections[0].Name).WithReadOnly(readOnly));
            return stack;
        }

        // Multiple collections: show combobox for selection
        var initialCollection = host.GetQueryStringParamValue("collection") ?? collections[0].Name;
        var selectCollectionHint = host.Localize(SelectCollectionKey);

        var options = collections
            .Select(c => (Option)new Option<string>(c.Name, c.DisplayName ?? c.Name))
            .ToArray();

        var selectDataId = "filesCollectionSelect";
        var optionsDataId = "filesCollectionOptions";

        host.UpdateData(selectDataId, new Dictionary<string, object?> { ["collection"] = initialCollection });
        host.UpdateData(optionsDataId, options);

        stack = stack.WithView(new ComboboxControl(
            new JsonPointerReference("collection"),
            new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsDataId)))
        {
            Label = "Collection",
            Autocomplete = ComboboxAutocomplete.Both,
            DataContext = LayoutAreaReference.GetDataPointer(selectDataId)
        });

        stack = stack.WithView((h, _2) =>
            h.Stream.GetDataStream<Dictionary<string, object?>>(selectDataId)
                .Select(data =>
                {
                    var selected = data?.GetValueOrDefault("collection")?.ToString();
                    if (string.IsNullOrEmpty(selected))
                        return (UiControl?)Controls.Body(selectCollectionHint).WithColor(HintColor);
                    return (UiControl?)new FileBrowserControl(selected).WithReadOnly(readOnly);
                }));

        return stack;
    }

    #region UCR Special Areas

    /// <summary>
    /// Renders content from the node's content collection.
    /// For images: renders inline. For markdown: renders the content.
    /// For other files: shows a download link.
    /// For self-reference (no path): shows the node's icon/logo.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Content(LayoutAreaHost host, RenderingContext _)
    {
        var contentPath = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(contentPath))
            return NodeIconView(host, hubPath);

        // Determine content type from extension
        var extension = Path.GetExtension(contentPath)?.ToLowerInvariant() ?? "";

        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" =>
                RenderImageAsync(host, contentPath, extension),
            ".md" or ".markdown" =>
                Observable.Return<UiControl?>(RenderMarkdownContent(host, contentPath)),
            ".pdf" =>
                Observable.Return<UiControl?>(RenderPdf(host, contentPath)),
            ".json" =>
                Observable.Return<UiControl?>(RenderJsonContent(host, contentPath)),
            _ => Observable.Return<UiControl?>(RenderDownloadLink(host, contentPath, extension))
        };
    }

    /// <summary>The icon a self-reference shows for a node that has none.</summary>
    internal const string DefaultSelfReferenceIcon = "/static/NodeTypeIcons/document.svg";

    /// <summary>The <c>/data</c> id the self-reference's projection is published under.</summary>
    internal const string SelfReferenceDataId = "nodeIcon";

    /// <summary>What a node referencing itself in its content shows: its icon and its name.</summary>
    /// <param name="Icon">The node's own icon (any <see cref="ImageControl"/> source form), or empty.</param>
    /// <param name="Text">The node's name, or a localized not-found line.</param>
    internal sealed record SelfReferenceView(string Icon, string Text);

    /// <summary>
    /// Self-reference: the node's icon and name as a TEMPLATE — an <see cref="ImageControl"/> and a
    /// <c>Label</c> bound to a projection of the node, returned at once instead of after the node's
    /// read (Doc/GUI/DataBinding → "Templates first, data later"). The image control draws whichever
    /// form the icon takes — a URL, a node's <c>content:</c> file, inline svg as an inert image, a
    /// glyph — and the document glyph when the node has none.
    /// </summary>
    private static IObservable<UiControl?> NodeIconView(LayoutAreaHost host, string hubPath)
    {
        var notFound = host.Localize(NodeNotFoundAtKey, hubPath);
        return Observable.Return<UiControl?>(host.Workspace.GetMeshNodeStream()
            .Select(node => ProjectSelfReference(node, notFound))
            .DistinctUntilChanged()
            .Bind(p => SelfReferenceTemplate(p.Icon, p.Text, hubPath), SelfReferenceDataId));
    }

    /// <summary>Projects the node into <see cref="SelfReferenceView"/>. Pure.</summary>
    internal static SelfReferenceView ProjectSelfReference(MeshNode? node, string notFound)
        => node is null
            ? new SelfReferenceView("", notFound)
            : new SelfReferenceView(node.Icon ?? "", node.Name ?? node.Id);

    /// <summary>The self-reference's controls; values are literals or pointers.</summary>
    internal static StackControl SelfReferenceTemplate(object icon, object text, string nodePath)
        => Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithStyle("align-items: center;")
            .WithView(Controls.Image(icon, "")
                .WithSize(24)
                .WithNodePath(nodePath)
                .WithFallback(DefaultSelfReferenceIcon))
            .WithView(Controls.Label(text));

    private static IObservable<UiControl?> RenderImageAsync(LayoutAreaHost host, string contentPath, string _)
    {
        // Access-controlled content URL: /api/content/{address}/{defaultCollection}/{filePath}.
        // NEVER /static (issue #587) — that route carries build assets only and applies no
        // permission check, so an image in a private Space would be world-readable there.
        var address = host.Hub.Address.ToString();
        var staticUrl = ContentCollectionsExtensions.GetContentFileUrl(
            address, ContentCollectionsExtensions.DefaultCollectionName, contentPath);

        // The file name is the alt text: it is what the author called the picture. The image
        // control has no natural-height mode, so the picture is contained in a page-wide box.
        return Observable.Return<UiControl?>(
            Controls.Image(staticUrl, Path.GetFileName(contentPath))
                .WithWidth("100%")
                .WithHeight("60vh")
                .WithFit(ImageFit.Contain));
    }

    private static UiControl RenderMarkdownContent(LayoutAreaHost host, string contentPath)
    {
        // For markdown files, show text indicating content is inserted and provide navigation link
        var address = host.Hub.Address.ToString();
        var fileName = Path.GetFileName(contentPath);

        return Controls.Stack
            .WithVerticalGap(8)
            .WithView(Controls.Label(host.Localize(InsertedFromKey,
                    $"@{address}/{ContentCollectionsExtensions.DefaultCollectionName}:{contentPath}"))
                .WithColor(HintColor))
            .WithView(Controls.Link(host.Localize(NavigateToKey, fileName), $"/{address}/$Content/{contentPath}"));
    }

    private static UiControl RenderPdf(LayoutAreaHost host, string contentPath)
    {
        var contentUrl = $"/api/content/{host.Hub.Address}/{contentPath}";
        var fileName = Path.GetFileName(contentPath);
        // The viewer is the browser's own PDF reader in a platform embed frame (a Document of this
        // portal: never sandboxed, since a browser refuses a PDF in a sandboxed frame), with the
        // download as a platform Link beside it.
        return Controls.Stack
            .WithWidth("100%")
            .WithVerticalGap(8)
            .WithView(Controls.Embed(contentUrl, fileName))
            .WithView(Controls.Link(host.Localize(DownloadPdfKey), contentUrl).WithDownload(fileName));
    }

    private static UiControl RenderJsonContent(LayoutAreaHost host, string contentPath)
    {
        var contentUrl = $"/api/content/{host.Hub.Address}/{contentPath}";
        return Controls.Markdown($"```json\n// Loading {contentPath}...\n```");
    }

    private static UiControl RenderDownloadLink(LayoutAreaHost host, string contentPath, string _1)
    {
        var contentUrl = $"/api/content/{host.Hub.Address}/{contentPath}";
        var fileName = Path.GetFileName(contentPath);
        // A file card: the document glyph, the file's name and a download link, on the platform's
        // card skin (theme-aware — the old #f5f5f5 box was a light-theme colour on both themes).
        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("align-items: center;")
            .WithView(Controls.Image("📄", "").WithSize(30))
            .WithView(Controls.Stack
                .WithView(Controls.Label(fileName).WithWeight(FontWeight.Bold))
                .WithView(Controls.Link(host.Localize("common.download"), contentUrl).WithDownload(fileName)))
            .AddSkin(Skins.Card);
    }

    /// <summary>
    /// Renders data entities from the node's data context.
    /// If Id is specified, renders that specific entity/collection/type.
    /// If no Id (self-reference), shows the current MeshNode data.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Data(LayoutAreaHost host, RenderingContext context)
    {
        var dataPath = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(dataPath))
            return NodeDataView(host, hubPath);

        // Check if dataPath is a collection name or a type name
        if (host.Workspace.DataContext.TypeSources.TryGetValue(dataPath, out var typeSource))
        {
            // It's a collection name - show catalog for this collection
            return Observable.Return<UiControl?>(Controls.MeshSearch
                .WithHiddenQuery($"namespace:{host.Hub.Address} type:{dataPath}")
                .WithPlaceholder($"Search {dataPath}...")
                .WithRenderMode(MeshSearchRenderMode.Hierarchical));
        }

        // Render specific collection or entity
        // The dataPath could be "CollectionName/entityId"
        var parts = dataPath.Split('/', 2);
        var collectionName = parts[0];
        var entityId = parts.Length > 1 ? parts[1] : null;

        if (!host.Workspace.DataContext.TypeSources.TryGetValue(collectionName, out typeSource))
        {
            // Not a known collection - might be a type name, search for it
            return Observable.Return<UiControl?>(Controls.MeshSearch
                .WithHiddenQuery($"namespace:{host.Hub.Address} {dataPath}")
                .WithPlaceholder($"Search {dataPath}...")
                .WithRenderMode(MeshSearchRenderMode.Hierarchical));
        }

        if (string.IsNullOrEmpty(entityId))
        {
            // Show catalog for this collection
            return Observable.Return<UiControl?>(Controls.MeshSearch
                .WithHiddenQuery($"namespace:{host.Hub.Address} type:{collectionName}")
                .WithShowSearchBox(true)
                .WithPlaceholder($"Search {collectionName}...")
                .WithRenderMode(MeshSearchRenderMode.Hierarchical)
                .WithMaxColumns(3));
        }

        // Show specific entity as navigation link
        var entityPath = $"{host.Hub.Address}/{collectionName}/{entityId}";
        return Observable.Return<UiControl?>(Controls.Markdown(
            $"[View {collectionName}: {entityId}](/{entityPath})"));
    }

    /// <summary>
    /// Self-reference: the current node as JSON, as a TEMPLATE — a <see cref="MarkdownControl"/>
    /// bound to a projection of the node, returned at once and following every later write
    /// (Doc/GUI/DataBinding → "Templates first, data later").
    /// </summary>
    private static IObservable<UiControl?> NodeDataView(LayoutAreaHost host, string hubPath)
        => Observable.Return<UiControl?>(
            NodeJsonMarkdown(host.Workspace, hubPath, host.Hub.JsonSerializerOptions).BoundMarkdown("nodeJson"));

    /// <summary>The live JSON code block of the hub's own node, or a not-found line.</summary>
    private static IObservable<string> NodeJsonMarkdown(
        IWorkspace workspace, string hubPath, JsonSerializerOptions jsonOptions)
        => workspace.GetMeshNodeStream().Select(node => node == null
            ? $"*Node not found: {hubPath}*"
            : RenderMeshNodeJson(node, jsonOptions));

    internal static string RenderMeshNodeJson(MeshNode node, JsonSerializerOptions jsonOptions)
    {
        // Serialize the MeshNode as JSON
        var json = JsonSerializer.Serialize(node, new JsonSerializerOptions(jsonOptions)
        {
            WriteIndented = true
        });

        return $"```json\n{json}\n```";
    }

    /// <summary>
    /// Renders JSON schema for a type.
    /// If Id is specified, shows schema for that type name.
    /// If no Id (self-reference), shows schema for MeshNode and content type.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Schema(LayoutAreaHost host, RenderingContext context)
    {
        var typeName = host.Reference.Id?.ToString();
        var hubPath = host.Hub.Address.ToString();

        if (string.IsNullOrEmpty(typeName))
            // Self-reference: the MeshNode schema and the content type's. The content type is the
            // one this hub's MeshDataSource was CONFIGURED with — structure, not data — so the page
            // is a template that needs no read of the node at all (Doc/GUI/DataBinding →
            // "Templates first, data later").
            return OnDocumentationReadEdge(host.Hub, () => RenderNodeSchema(ConfiguredContentType(host.Workspace), host.Hub));

        // Try to get the type from the registry
        var typeRegistry = host.Hub.ServiceProvider.GetService<ITypeRegistry>();
        if (typeRegistry == null)
            return Observable.Return<UiControl?>(Controls.Markdown($"*Type registry not available.*"));

        var typeDef = typeRegistry.GetTypeDefinition(typeName);
        if (typeDef == null)
            return Observable.Return<UiControl?>(Controls.Markdown($"*Type '{typeName}' not found.*"));

        // Generate JSON schema for the type using hub's JSON options
        return OnDocumentationReadEdge(host.Hub, () =>
            new MarkdownControl($"## JSON Schema: {typeName}\n\n```json\n{GenerateJsonSchema(typeDef.Type, host.Hub)}\n```"));
    }

    /// <summary>
    /// Runs a schema render on the mesh's file-system I/O pool. The schema exporter walks the type's
    /// whole property graph and reads the XML-doc summary of every property it meets — documentation
    /// files of assemblies nobody can name in advance — so the render runs where a blocking read
    /// belongs, never on the thread building the view (Doc/Architecture/XmlDocumentationReader).
    /// </summary>
    private static IObservable<UiControl?> OnDocumentationReadEdge(IMessageHub hub, Func<UiControl> render)
        => (hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded)
            .InvokeBlocking<UiControl?>(_ => render());

    /// <summary>
    /// The content type this node hub's <see cref="MeshDataSource"/> was configured with
    /// (<c>WithContentType</c>), or null when it declares none.
    /// </summary>
    internal static Type? ConfiguredContentType(IWorkspace workspace)
        => workspace.DataContext.DataSources
            .OfType<MeshDataSource>()
            .Select(ds => ds.ContentType)
            .FirstOrDefault(t => t != null);

    internal static UiControl RenderNodeSchema(Type? contentType, IMessageHub hub)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Schema");
        sb.AppendLine();

        // MeshNode schema
        sb.AppendLine("### MeshNode");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(GenerateJsonSchema(typeof(MeshNode), hub));
        sb.AppendLine("```");

        // Content type schema if the hub declares one
        if (contentType != null)
        {
            sb.AppendLine();
            sb.AppendLine($"### Content Type: {contentType.Name}");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(GenerateJsonSchema(contentType, hub));
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("### Content Type");
            sb.AppendLine();
            sb.AppendLine("*No content defined for this node.*");
        }

        return new MarkdownControl(sb.ToString());
    }

    private static string GenerateJsonSchema(Type type, IMessageHub hub)
    {
        // Use the built-in JsonSchemaExporter from System.Text.Json.Schema
        var options = hub.JsonSerializerOptions;
        var docs = hub.XmlDocs();

        var schema = options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (ctx, node) =>
            {
                // Add documentation from XML docs (XmlDocs)
                if (ctx.TypeInfo.Type == type)
                {
                    // Add title for the main type
                    node["title"] = type.Name;

                    // Add description for the main type
                    var typeDescription = docs.Summary(type);
                    if (!string.IsNullOrEmpty(typeDescription))
                    {
                        node["description"] = typeDescription;
                    }
                }

                // Add descriptions for properties
                if (ctx.PropertyInfo != null && node is JsonObject jsonObj)
                {
                    // Get the actual PropertyInfo from the declaring type
                    var declaringType = ctx.PropertyInfo.DeclaringType;
                    var propertyName = ctx.PropertyInfo.Name;
                    var actualPropertyInfo = declaringType.GetProperty(propertyName.ToPascalCase()!);
                    if (actualPropertyInfo != null)
                    {
                        var propertyDescription = docs.Summary(actualPropertyInfo);
                        if (!string.IsNullOrEmpty(propertyDescription))
                        {
                            jsonObj["description"] = propertyDescription;
                        }
                    }
                }

                return node;
            }
        });

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    #endregion

    #region Access Control



    #endregion

    #region Chat

    /// <summary>
    /// Renders a standalone ThreadChatControl for the current node.
    /// Can be embedded in markdown via @@("path/Chat").
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Chat(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var nodeName = nodePath.Contains('/') ? nodePath[(nodePath.LastIndexOf('/') + 1)..] : nodePath;

        return Observable.Return<UiControl?>(new ThreadChatControl()
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName(nodeName));
    }

    /// <summary>
    /// The composer a markdown ```` ```prompt ```` fence lowers to (#2511) — the SAME
    /// <see cref="ThreadChatControl"/> the side panel and the Threads app mount, pre-filled with the
    /// prompt the page author wrote and editable in place. The authored text arrives as the area's
    /// reference ID (base64url — <see cref="PromptFence.DecodeDraft"/>), because the fence renderer
    /// has no other channel to a layout area.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> PromptComposerArea(LayoutAreaHost host, RenderingContext _)
        // NormalizeScalar, not a cast: the reference ID round-trips through JSON, so it arrives as a
        // JsonElement on the way back from a client and `Id as string` would be a silent null — the
        // composer would then render with no draft and nobody would see why.
        => Observable.Return<UiControl?>(PromptComposer(
            host.Hub.Address.ToString(),
            PromptFence.DecodeDraft(LayoutAreaReference.NormalizeScalar(host.Reference.Id))));

    /// <summary>
    /// The prompt composer's composition — pure (no hub) so the shape is directly assertable, the
    /// same way <c>UserActivityLayoutAreas.ThreadsAppComposer</c> is.
    ///
    /// <para>🚨 <c>HideEmptyState</c> is the load-bearing flag, not cosmetics: <c>ThreadChatView</c>
    /// reads it as <c>isCompact</c> and, on submit, navigates to the newly created thread FULL PAGE
    /// (<c>NavigateTo("/{path}")</c>) instead of handing it to the side panel. "Submit starts a
    /// full-page thread" IS this flag.</para>
    /// </summary>
    /// <param name="nodePath">The page node the prompt was authored on — the thread's context.</param>
    /// <param name="draft">The authored prompt, or null for a composer with nothing seeded.</param>
    internal static ThreadChatControl PromptComposer(string nodePath, string? draft)
    {
        var nodeName = nodePath.Contains('/') ? nodePath[(nodePath.LastIndexOf('/') + 1)..] : nodePath;
        return new ThreadChatControl()
            .WithHideEmptyState(true)
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName(nodeName)
            // WithInitialDraft takes a nullable: no draft simply leaves the composer empty.
            .WithInitialDraft(draft);
    }

    #endregion

    #region Create Node

    /// <summary>
    /// Renders the Create Node area showing available types to create.
    /// Delegates to CreateLayoutArea.Create for the actual implementation.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> CreateNode(LayoutAreaHost host, RenderingContext ctx)
        => CreateLayoutArea.Create(host, ctx);

    /// <summary>
    /// Renders the Edit area showing all content type fields in pure edit mode with auto-save.
    /// Unlike Overview (which is toggleable click-to-edit), Edit shows all fields as editable immediately.
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> EditNode(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return PermissionGate(host, hubPath)
            .Select(gate => gate.Update
                ? (UiControl?)BuildEditTemplate(host)
                : BuildAccessDenied(hubPath, locale: host.ViewerLocale()));
    }

    /// <summary>
    /// The Edit page as a TEMPLATE: the header and the content type's form in pure edit mode, every
    /// field bound to the node (<see cref="LayoutAreaReference.GetMeshNodeDataContext"/>) so each
    /// write goes straight back to it. Which form is STRUCTURE, read from configuration
    /// (<see cref="ConfiguredContentType(LayoutAreaHost)"/>); a hub that names no content type
    /// falls back to the <see cref="OverviewLayoutArea.ContentFormArea"/> slot.
    /// </summary>
    internal static UiControl BuildEditTemplate(LayoutAreaHost host)
    {
        var nodePath = host.Hub.Address.ToString();
        var container = Controls.Stack.WithWidth("100%").WithStyle(GetContainerStyle(host));

        // Skip edit form for NodeTypeDefinition content (type root nodes)
        if (IsNodeTypeDefinitionHub(host))
            return container
                .WithView(BuildHeaderTemplate(host, canEdit: false))
                .WithView(Controls.Markdown(host.Localize("ui.mdBuiltInNotEditable"))
                    .WithStyle("color: var(--neutral-foreground-hint);"));

        container = container.WithView(BuildHeaderTemplate(host, canEdit: true));
        if (ConfiguredContentType(host) is not { } contentType)
            return container.WithView(OverviewLayoutArea.ContentFormSlot(host, OverviewLayoutArea.ContentFormEdit));

        // The form binds DIRECTLY to the node's Content (node-bound DataContext): each field reads
        // from and writes straight back to the node stream — ONE source of truth, no /data replica,
        // no SetupAutoSave save subscription. The one-way /data mirror keeps the derived-label read
        // views (dimension/options/date) correct from the Layout layer.
        var dataId = EditLayoutArea.GetDataId(nodePath);
        var boundContext = LayoutAreaReference.GetMeshNodeDataContext(nodePath, bindContent: true);
        return container.WithView(EditLayoutArea.BuildPropertyForm(
                host, contentType, dataId, canEdit: true, isToggleable: false, boundDataContext: boundContext)
            .MirrorContent(nodePath, dataId));
    }

    #endregion

}

