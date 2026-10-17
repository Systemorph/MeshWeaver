using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Unified Settings page with Splitter layout: left NavMenu + right content pane.
/// URL pattern: /{nodePath}/Settings/{tabId}
/// The host.Reference.Id determines which tab to show.
/// Tabs are registered via <see cref="SettingsMenuItemsExtensions.AddSettingsMenuItems(MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Mesh.SettingsMenuItemProvider[])"/>.
/// </summary>
public static class SettingsLayoutArea
{
    internal const string MetadataTab = "Metadata";
    internal const string NodeTypesTab = "NodeTypes";
    internal const string AccessControlTab = "AccessControl";
    internal const string GroupsTab = "Groups";
    internal const string EffectiveAccessTab = "EffectiveAccess";
    internal const string VersionsTab = "Versions";

    /// <summary>
    /// Renders the unified Settings page with Splitter layout.
    /// Left pane: NavMenu with tab links (dynamically built from registered providers).
    /// Right pane: Content based on host.Reference.Id (tab selection).
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Settings(LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();
        var hubAddress = host.Hub.Address;
        // Reference.Id carries any query string appended by ApplicationPage.UpdateFromContext
        // (e.g. "GitHubSync?connect=github-ok" after an OAuth callback redirect). The tab id is
        // the part before '?', mirroring ApplicationPage.GetDisplayNameFromId — otherwise a query
        // string never matches item.Id and the content pane silently falls back to the first
        // (Metadata) tab while the nav still highlights the URL tab.
        var tabId = host.Reference.Id?.ToString()?.Split('?')[0];

        // A tab that moved — into the Admin app, into the person app, up to the partition root, or
        // into another tab of this page — answers an old link with a redirect to where it lives now,
        // rather than silently falling back to this page's first tab.
        if (SettingsRedirect.For(host, tabId) is { } redirect)
            return Observable.Return<UiControl?>(redirect);

        var ownNode = host.Workspace.GetMeshNodeStream();
        var permsStream = host.Hub.GetEffectivePermissions(hubPath);
        var itemsStream = host.Hub.Configuration.ObserveSettingsMenuItems(host, ctx);

        return MenuRenderInputs(ownNode, permsStream, itemsStream)
            .Select(t =>
            {
                var selectedTab = string.IsNullOrEmpty(tabId) && t.Items.Count > 0
                    ? t.Items[0].Id
                    : (tabId ?? MetadataTab);
                return (UiControl?)BuildSettingsPage(
                    host, t.Node, hubAddress, hubPath, selectedTab, t.CanEdit, t.Items);
            });
    }

    /// <summary>
    /// The ORDERING seam of the settings page: the node, the viewer's effective permissions and
    /// the (unfiltered) tab set folded into ONE live chain, so the newest value of every input
    /// wins — the shape the node menu has always used
    /// (<c>NodeMenuItemsExtensions.GetMenuContext</c>).
    ///
    /// <para>🚨 <b>Why this is a named seam rather than three lines inline.</b> It used to be
    /// <c>perms.SelectMany(p =&gt; items.Select(filter through p))</c>, which builds a SEPARATE live
    /// provider chain per permission value, each frozen on the value it captured — and never
    /// unsubscribes the old ones. Both inputs are long-lived and enrich on their own schedules:
    /// <c>PermissionEvaluator</c> emits a low seed (an empty assignment set reads as
    /// <see cref="Permission.None"/>) before the synced-assignment answer arrives, and a provider
    /// re-emits whenever its own live check settles (a global-admin probe, a GitHub call, a
    /// cross-partition query). So the FIRST chain — the one holding <see cref="Permission.None"/> —
    /// stays subscribed and re-renders the whole menu the next time any provider speaks, replacing
    /// the correct menu with the one an anonymous viewer would see. Nothing errors and nothing
    /// logs; the tabs simply disappear, and which chain wins is a race that a busy install loses
    /// far more often than a quiet one (#1962: the display-time-zone tab absent on the cloud
    /// portal and present locally, with the <see cref="Permission.None"/> Notifications entry
    /// beside it surviving — the exact fingerprint of a menu rendered through
    /// <see cref="Permission.None"/>).</para>
    ///
    /// <para>Pure in its inputs (three observables in, one out), so the interleaving that produced
    /// the defect is assertable with subjects — no hub, no circuit, no rendered area.</para>
    /// </summary>
    internal static IObservable<(MeshNode? Node, Permission Perms, bool CanEdit,
            IReadOnlyList<SettingsMenuItemDefinition> Items)>
        MenuRenderInputs(
            IObservable<MeshNode> ownNode,
            IObservable<Permission> perms,
            IObservable<IReadOnlyList<SettingsMenuItemDefinition>> items)
        => Observable.CombineLatest(ownNode, perms, items,
            (node, p, i) => ((MeshNode?)node, p, p.HasFlag(Permission.Update),
                SettingsMenuItemsExtensions.FilterByPermission(i, p)));

    private static UiControl BuildSettingsPage(
        LayoutAreaHost host,
        MeshNode? node,
        object hubAddress,
        string hubPath,
        string tabId,
        bool canEdit,
        IReadOnlyList<SettingsMenuItemDefinition> items)
    {
        // The settings page flows at its natural content height and the PAGE scrolls
        // (.body-content is the scroll container) — NO viewport-pinning, NO inner scroller.
        // FluentMultiSplitter's own stylesheet pins `.fluent-multi-splitter { height: 100% }`,
        // which inside the flex-column `.layout-area-container` resolves to exactly the visible
        // pane height and (with the old pane overflow:hidden) clipped everything below the fold.
        // The inline style beats that class deterministically:
        //   height:auto     — content decides the height, never the viewport;
        //   flex:1 0 auto   — a SHORT page still grows to the bottom (background fills), but
        //                     shrink:0 stops the fill-area rule (`flex:1 1 auto; min-height:0`
        //                     on the layout area's child) from shrink-clipping a LONG page back
        //                     to the container height. Content taller than the viewport simply
        //                     overflows the container and .body-content scrolls — like every
        //                     other page.
        var settingsPage = Controls.Splitter
            .WithClass("settings-splitter")
            .WithStyle("height: auto; flex: 1 0 auto;")
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%"))
            .WithView(
                BuildMenuPane(host, node, hubAddress, hubPath, items, tabId),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                BuildContentPane(host, node, tabId, items),
                skin => skin.WithSize("*")
            );

        if (!canEdit)
        {
            // A flowing flex column: the banner takes its natural height, the splitter its
            // content height. No height:100%, no overflow:hidden — the page scrolls as a whole
            // (flex-shrink:0 for the same fill-area shrink-clipping reason as on the splitter).
            return Controls.Stack.WithWidth("100%")
                .WithStyle("display: flex; flex-direction: column; flex: 1 0 auto;")
                .WithView(Controls.MessageBar(host.Localize("settings.readOnlyNotice")).WithIntent(MessageBarIntent.Info))
                .WithView(settingsPage);
        }

        return settingsPage;
    }

    /// <summary>
    /// Left pane: a pinned search box on top + the (live-filtered) nav menu below.
    /// The search box is its OWN static area so it keeps focus while typing — only the
    /// "SettingsMenu" sub-area re-renders as the query data stream emits, filtering the
    /// tabs by Label/Group. (Field-level content search would need searchable keywords
    /// on each <see cref="SettingsMenuItemDefinition"/>.)
    /// </summary>
    private static UiControl BuildMenuPane(
        LayoutAreaHost host,
        MeshNode? node,
        object hubAddress,
        string hubPath,
        IReadOnlyList<SettingsMenuItemDefinition> items,
        string selectedTab)
    {
        var searchDataId = $"settingsMenuSearch_{hubPath.Replace('/', '_')}";
        host.UpdateData(searchDataId, string.Empty);

        // Translate tab labels and group names for THIS subscriber before anything reads them, so
        // the search box below filters on what the viewer actually sees (a German user finds
        // "Datenschutz" by typing it, not only by typing "Privacy").
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        items = [.. items.Select(i => i.Localized(access))];
        var title = Title(host, node);

        // The platform's text field, bound to the search slot — no class, no stylesheet rule of
        // its own: the pane's vertical gap is the only spacing it gets.
        var searchBox = new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder(host.Localize(MenuSearchPlaceholderKey))
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(searchDataId) };

        return Controls.Stack
            .WithClass("settings-menu-pane")
            .WithWidth("100%")
            .WithVerticalGap(8)
            .WithView(searchBox)
            .WithView(
                (h, c) => h.GetDataStream<string>(searchDataId)
                    .StartWith(string.Empty)
                    .Select(q => (UiControl)BuildNavMenu(title, hubAddress, hubPath, FilterMenuItems(items, q), selectedTab)),
                "SettingsMenu");
    }

    /// <summary>
    /// Case-insensitive substring filter over a settings tab's <see cref="SettingsMenuItemDefinition.Label"/>,
    /// <see cref="SettingsMenuItemDefinition.Group"/>, and <see cref="SettingsMenuItemDefinition.Keywords"/>
    /// (the terms describing the fields inside each section). Empty query returns all items unchanged.
    /// </summary>
    internal static IReadOnlyList<SettingsMenuItemDefinition> FilterMenuItems(
        IReadOnlyList<SettingsMenuItemDefinition> items, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return items;
        var q = query.Trim();
        return items.Where(i =>
                (i.Label?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (i.Group?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (i.Keywords?.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)) ?? false))
            .ToList();
    }

    /// <summary>
    /// The settings page's title: the hub's <see cref="SettingsMenuItemsExtensions.WithSettingsTitle"/>
    /// when it names one, else the node's own name, else the hub path.
    /// </summary>
    internal static string Title(LayoutAreaHost host, MeshNode? node)
    {
        string? title = null;
        if (host.Hub.Configuration.Get<SettingsTitle>() is { } custom)
        {
            try { title = custom.Title(host, node); }
            catch (Exception ex)
            {
                host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                    ?.CreateLogger(typeof(SettingsLayoutArea).FullName ?? nameof(SettingsLayoutArea))
                    .LogWarning(ex, "Settings title failed for {Path}; falling back to the node name",
                        host.Hub.Address);
            }
        }
        return title is { Length: > 0 } ? title
            : node?.Name is { Length: > 0 } name ? name
            : host.Hub.Address.ToString();
    }

    private static UiControl BuildNavMenu(
        string title,
        object hubAddress,
        string hubPath,
        IReadOnlyList<SettingsMenuItemDefinition> items,
        string selectedTab)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Back to node link (always present). Its label is the page's TITLE — the app this settings
        // page is: the instance's name on the Admin app, the person's name on the person app, the
        // node's own name on a node's settings (WithSettingsTitle).
        var backHref = $"/{hubPath}";
        navMenu = navMenu.WithView(
            new NavLinkControl(title, FluentIcons.ArrowLeft(), backHref)
        );

        // Separate top-level items from grouped items
        var topLevel = items.Where(i => i.Group == null).ToList();
        var grouped = items.Where(i => i.Group != null)
            .GroupBy(i => i.Group!)
            .OrderBy(g => g.Min(i => i.Order))
            .ToList();

        // Interleave top-level and groups by order
        int topIdx = 0, grpIdx = 0;
        while (topIdx < topLevel.Count || grpIdx < grouped.Count)
        {
            var topOrder = topIdx < topLevel.Count ? topLevel[topIdx].Order : int.MaxValue;
            var grpOrder = grpIdx < grouped.Count ? grouped[grpIdx].Min(i => i.Order) : int.MaxValue;

            if (topOrder <= grpOrder && topIdx < topLevel.Count)
            {
                var item = topLevel[topIdx++];
                var href = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = item.Id }.ToHref(hubAddress);
                navMenu = navMenu.WithView(new NavLinkControl(item.Label, item.Icon, href)
                    .WithIsActive(item.Id == selectedTab));
            }
            else if (grpIdx < grouped.Count)
            {
                var group = grouped[grpIdx++];
                var groupIcon = group.Select(i => i.GroupIcon).FirstOrDefault(gi => gi != null);
                var navGroup = new NavGroupControl(group.Key)
                    .WithSkin(s => s.WithExpanded(true));
                if (groupIcon != null)
                    navGroup = navGroup.WithIcon(groupIcon);

                foreach (var item in group.OrderBy(i => i.Order))
                {
                    var href = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = item.Id }.ToHref(hubAddress);
                    navGroup = navGroup.WithView(new NavLinkControl(item.Label, item.Icon, href)
                        .WithIsActive(item.Id == selectedTab));
                }

                navMenu = navMenu.WithNavGroup(navGroup);
            }
        }

        return navMenu;
    }

    private static UiControl BuildContentPane(
        LayoutAreaHost host,
        MeshNode? node,
        string tabId,
        IReadOnlyList<SettingsMenuItemDefinition> items)
    {
        // No inner scroller: the content pane takes its natural height and the PAGE scrolls
        // (.body-content). FluentMultiSplitter renders each pane as LIGHT DOM — a plain
        // <div class="fluent-multi-splitter-pane"> (verified against the FluentUI source;
        // it is NOT a web component / shadow root), so the stylesheet selector
        // `.settings-splitter .fluent-multi-splitter-pane` reaches it and un-hides the
        // component's default `overflow:hidden` (standard-page-layout.css). The
        // `.settings-content-pane` class carries only padding/background/reading-column.
        var stack = Controls.Stack
            .WithClass("settings-content-pane")
            .WithWidth("100%");

        var matchedItem = items.FirstOrDefault(i => i.Id == tabId)
            ?? items.FirstOrDefault();

        if (matchedItem == null)
            return stack.WithView(Controls.MessageBar(host.Localize("settings.noTabs")));

        try
        {
            return matchedItem.ContentBuilder(host, stack, node);
        }
        catch (Exception ex)
        {
            // Plain text in a message bar: the exception message is data, never markup.
            return stack.WithView(Controls.MessageBar(ex.Message)
                .WithIntent(MessageBarIntent.Error)
                .WithTitle(host.Localize("settings.tabFailed")));
        }
    }

    #region Default Tab Content Builders

    internal static UiControl BuildMetadataTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        if (node == null)
        {
            stack = stack.WithView(Controls.MessageBar(host.Localize("settings.nodeNotFound")));
            return stack;
        }

        var meta = MeshNodeMetadata.FromNode(node);

        // Display fields are bound DIRECTLY to the node (node-bound DataContext, fields-mode):
        // Name/Description/Category/Icon/Order map onto the node's own top-level fields, and each
        // edit writes straight back to the node stream (IMeshNodeStreamCache). No /data replica of
        // the node, no SetupNodeMetadataAutoSave save subscription — ONE source of truth.
        // (Identity + Timestamps are read-only and rendered from the snapshot `meta`.)
        var nodeContext = LayoutAreaReference.GetMeshNodeDataContext(node.Path, bindContent: false);

        stack = stack.WithView(BuildSection(host.Localize("settings.meta.identity"), BuildIdentitySection(host, meta)));
        stack = stack.WithView(BuildSection(host.Localize("settings.meta.display"), BuildDisplaySection(host, node.Path, nodeContext)));
        stack = stack.WithView(BuildSection(host.Localize("settings.meta.timestamps"), BuildTimestampsSection(host, meta, host.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId())));

        return stack;
    }

    internal static UiControl BuildNodeTypesTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        stack = stack.WithView(Controls.H2(host.Localize("settings.nodeTypes")).WithStyle("margin: 0 0 24px 0;"));
        stack = stack.WithView(
            (h, ctx) => MeshNodeLayoutAreas.NodeTypes(h, ctx)!,
            "NodeTypesContent"
        );
        return stack;
    }

    internal static UiControl BuildAccessControlTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        // EMBEDDED AREA, not a compiled call: the AccessControl view rides the
        // MeshWeaver.Graph.Views module now, so the settings tab names the area and lets whoever
        // serves it render — the same shape the inline comments section uses.
        stack = stack.WithView(
            Controls.LayoutArea(host.Hub.Address, MeshNodeLayoutAreas.AccessControlArea)
                .WithShowProgress(false),
            "AccessControlContent"
        );
        return stack;
    }

    internal static UiControl BuildGroupsTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
        => stack
            .WithView(Controls.H2(host.Localize("settings.groups")).WithStyle("margin: 0 0 16px 0;"))
            .WithView(GroupsList(host.Hub.Address.ToString()));

    /// <summary>
    /// The groups defined at <paramref name="hubPath"/>, listed by the GUI — the viewer's client runs
    /// the query and draws each group as a card bound to its node, so the tab renders at once
    /// instead of waiting on a hub-side query and baking one thumbnail per loaded node
    /// (Doc/GUI/DataBinding → "Templates first, data later").
    /// </summary>
    /// <param name="hubPath">The node whose groups are listed.</param>
    internal static MeshSearchControl GroupsList(string hubPath)
        => Controls.MeshSearch
            .WithHiddenQuery($"namespace:{hubPath} nodeType:Group sort:order")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.List)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithReactiveMode(true);

    internal static UiControl BuildEffectiveAccessTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var hubPath = host.Hub.Address.ToString();
        if (host.Hub.Configuration.Get<EffectivePermissionsDelegate>() is null)
        {
            return stack.WithView(Controls.MessageBar(host.Localize("effectiveAccess.rlsDisabled"))
                .WithIntent(MessageBarIntent.Warning));
        }

        stack = stack.WithView(Controls.H2(host.Localize("settings.effectiveAccess")).WithStyle("margin: 0 0 16px 0;"));
        stack = stack.WithView(Controls.Markdown(host.Localize("effectiveAccess.intro"))
            .WithStyle("font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;"));

        var formId = $"effectiveAccess_{hubPath.Replace("/", "_")}";
        var resultId = $"effectiveAccessResult_{hubPath.Replace("/", "_")}";
        var rowsId = $"effectiveAccessRows_{hubPath.Replace("/", "_")}";
        var noticeId = $"effectiveAccessNotice_{hubPath.Replace("/", "_")}";
        host.UpdateData(formId, new Dictionary<string, object?> { ["userId"] = "" });
        host.UpdateData(resultId, "");
        host.UpdateData(noticeId, "");
        // The texts the click writes, resolved HERE on the render turn — the click's continuation
        // runs off the viewer's delivery, where the viewer's language is no longer ambient.
        var needUser = host.Localize("effectiveAccess.needUser");
        var allowed = host.Localize("effectiveAccess.allowed");
        var denied = host.Localize("effectiveAccess.denied");
        var locale = host.ViewerLocale();
        var permissionTitle = host.Localize("effectiveAccess.colPermission");
        var statusTitle = host.Localize("effectiveAccess.colStatus");

        var userField = new TextFieldControl(new JsonPointerReference("userId"))
        {
            Placeholder = "Enter user ID (e.g. alice@example.com)...",
            Label = "User ID",
            Immediate = true,
            DataContext = LayoutAreaReference.GetDataPointer(formId)
        };
        stack = stack.WithView(userField);

        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(8)
            .WithView(Controls.Button(host.Localize("ui.check"))
                .WithAppearance(Appearance.Accent)
                .WithReactiveClickAction(ctx =>
                {
                    // Pure reactive — read the form value, then SelectMany into the permission
                    // stream and write the verdict rows + their heading back to the data slots.
                    // RETURNED to the click, so a fault in either read is the click's visible refusal.
                    return ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                        .Take(1)
                        .SelectMany(data =>
                        {
                            var userId = data?.GetValueOrDefault("userId")?.ToString()?.Trim() ?? "";
                            if (string.IsNullOrEmpty(userId))
                            {
                                ctx.Host.UpdateData(resultId, "");
                                ctx.Host.UpdateData(noticeId, needUser);
                                return Observable.Empty<(string UserId, Permission Perms)>();
                            }
                            return host.Hub.GetEffectivePermissions(hubPath, userId)
                                .Take(1)
                                .Select(perms => (UserId: userId, Perms: perms));
                        })
                        .Do(t =>
                        {
                            ctx.Host.UpdateData(noticeId, "");
                            ctx.Host.UpdateData(rowsId, PermissionRows(t.Perms, allowed, denied));
                            ctx.Host.UpdateData(resultId, t.UserId);
                        })
                        .Select(_ => System.Reactive.Unit.Default);
                })));

        // The validation message, when the check ran without a user id.
        stack = stack.WithView((h, _) => h.Stream.GetDataStream<string>(noticeId)
            .Select(notice => string.IsNullOrEmpty(notice)
                ? (UiControl)Controls.Stack
                : Controls.MessageBar(notice).WithIntent(MessageBarIntent.Warning)));

        // The verdict: a heading + a DataGrid bound to the rows slot (a template — the grid reads
        // the rows the click wrote), keyed on the user the check ran for.
        stack = stack.WithView((h, _) =>
        {
            return h.Stream.GetDataStream<string>(resultId)
                .Select(userId => string.IsNullOrEmpty(userId)
                    ? (UiControl)Controls.Stack
                    : Section(LocalizationCatalog.Get("effectiveAccess.heading", locale, userId),
                            Controls.DataGrid(new JsonPointerReference(LayoutAreaReference.GetDataPointer(rowsId)))
                                .WithColumn(new PropertyColumnControl<string>
                                    { Property = nameof(PermissionRow.Permission).ToCamelCase() }.WithTitle(permissionTitle))
                                .WithColumn(new PropertyColumnControl<string>
                                    { Property = nameof(PermissionRow.Status).ToCamelCase() }.WithTitle(statusTitle))));
        });

        return stack;
    }

    internal static UiControl BuildVersionsTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
        // EMBEDDED AREA, like Access Control: the Versions view rides the MeshWeaver.Graph.Views
        // module; the tab names the area and whoever serves it renders.
        => stack.WithView(
            Controls.LayoutArea(host.Hub.Address, MeshNodeLayoutAreas.VersionsArea)
                .WithShowProgress(false),
            "VersionsContent");

    #endregion

    #region Helpers

    /// <summary>
    /// One settings section: a card (<see cref="Skins.Card"/>) with the section title as an H4
    /// heading above its content — the platform's card, not a stylesheet class of this page's own.
    /// For any settings tab that groups its fields into sections.
    /// </summary>
    /// <param name="title">The section title, already localized.</param>
    /// <param name="content">The section body.</param>
    /// <returns>The section control.</returns>
    public static UiControl Section(string title, UiControl content)
        => Controls.Stack
            .WithWidth("100%")
            .WithView(Controls.Stack
                .WithWidth("100%")
                .WithVerticalGap(12)
                .WithView(Controls.H4(title))
                .WithView(content))
            .AddSkin(Skins.Card.WithWidth("100%"));

    private static UiControl BuildSection(string title, UiControl content) => Section(title, content);

    /// <summary>
    /// A responsive grid of read-only facts: each cell spans the full width on a phone, half on a
    /// tablet and a third on a wide screen (<see cref="LayoutGridControl"/>).
    /// </summary>
    private static LayoutGridControl FactGrid(IEnumerable<UiControl> facts)
        => facts.Aggregate(Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3)),
            (grid, fact) => grid.WithView(fact, cell => cell.WithXs(12).WithSm(6).WithMd(4)));

    private static UiControl BuildIdentitySection(LayoutAreaHost host, MeshNodeMetadata meta)
        => FactGrid([
            // Node Type first, as a direct link to the type's Configuration area.
            NodeTypeFact(host.Localize("settings.meta.nodeType"), meta.NodeType),
            Fact(host.Localize("settings.meta.id"), meta.Id),
            Fact(host.Localize("settings.meta.namespace"), meta.Namespace),
            Fact(host.Localize("settings.meta.state"), meta.State.ToString()),
            Fact(host.Localize("settings.meta.version"), meta.Version.ToString()),
        ]);

    /// <summary>
    /// Node Type cell: NodeTypes are themselves MeshNodes, so the type name links straight
    /// to the type definition's Configuration area (same pattern as the header meta row).
    /// </summary>
    private static UiControl NodeTypeFact(string label, string? nodeType)
    {
        if (string.IsNullOrEmpty(nodeType) || nodeType == MeshNode.NodeTypePath)
            return Fact(label, nodeType);
        var href = MeshNodeLayoutAreas.BuildUrl(nodeType, NodeTypeLayoutAreas.ConfigurationArea);
        var typeName = nodeType.Contains('/') ? nodeType.Split('/').Last() : nodeType;
        return Controls.Stack
            .WithVerticalGap(2)
            .WithView(Controls.Subject(label))
            .WithView(Controls.Link(typeName, href));
    }

    /// <summary>A label/value fact: the label in the subject typography, the value as body text.</summary>
    private static UiControl Fact(string label, string? value)
        => Controls.Stack
            .WithVerticalGap(2)
            .WithView(Controls.Subject(label))
            .WithView(Controls.Body(string.IsNullOrEmpty(value) ? "—" : value));

    private static UiControl BuildDisplaySection(LayoutAreaHost host, string nodePath, string nodeContext)
    {
        var stack = Controls.Stack.WithWidth("100%").WithVerticalGap(16);

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Name)))
        {
            Label = "Name",
            Immediate = true,
            DataContext = nodeContext
        }.WithWidth("100%"));

        // Description + Generate button on its own row, matching the icon layout below.
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithVerticalGap(8)
            .WithView(Controls.Subject(host.Localize("settings.meta.description")))
            .WithView(new MarkdownEditorControl
            {
                Value = new JsonPointerReference(nameof(MeshNode.Description)),
                Height = "300px",
                Placeholder = "Long-form description. Seeds AI Name/Id/Icon generation and appears in detail views.",
                DataContext = nodeContext
            }.WithStyle("width: 100%;"))
            .WithView(Controls.Stack
                .WithWidth("100%")
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithStyle("justify-content: flex-end;")
                .WithView(Controls.Button(host.Localize("ui.generate"))
                    .WithAppearance(Appearance.Neutral)
                    .WithIconStart(FluentIcons.Sparkle())
                    .WithReactiveClickAction(actx => RegenerateDescriptionFromNode(actx, nodePath)))));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Category)))
        {
            Label = "Category",
            Immediate = true,
            DataContext = nodeContext
        }.WithWidth("100%"));

        stack = stack.WithView(BuildIconPicker(host, nodePath, nodeContext));

        stack = stack.WithView(new NumberFieldControl(new JsonPointerReference(nameof(MeshNode.Order)), "Int32?")
        {
            Label = "Order",
            Immediate = true,
            DataContext = nodeContext
        }.WithWidth("100%"));

        return stack;
    }

    private static UiControl BuildIconPicker(LayoutAreaHost host, string nodePath, string nodeContext)
    {
        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.ToList() ?? [];
        var editableCollection = collections.FirstOrDefault(c => c.IsEditable);

        var section = Controls.Stack.WithWidth("100%").WithVerticalGap(8);
        section = section.WithView(Controls.Subject(host.Localize("settings.meta.icon")));

        // Live preview + Regenerate button. The preview is the platform's ImageControl bound
        // DIRECTLY to the node's Icon (the same field the Icon Path box below binds to), so it is
        // drawn at once and tracks edits live; ImageControl renders every shape a node icon holds
        // (image URL / data URI / content: reference, inline svg as an image, glyph, Fluent name).
        section = section.WithView(Controls.Stack
            .WithWidth("100%")
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithVerticalAlignment(VerticalAlignment.Center)
            // Decorative alt: the "Icon" label above already says what it is. NodePath lets the
            // renderer resolve a `content:` reference (what "Use as Icon" writes) to its URL.
            .WithView(Controls.Image(new JsonPointerReference(nameof(MeshNode.Icon)), "")
                .WithNodePath(nodePath)
                with { DataContext = nodeContext })
            .WithView(Controls.Button(host.Localize("ui.generate"))
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.Sparkle())
                .WithReactiveClickAction(actx => RegenerateIconFromNode(actx, nodePath))));

        section = section.WithView(new TextFieldControl(new JsonPointerReference(nameof(MeshNode.Icon)))
        {
            Label = "Icon Path",
            Placeholder = "content:logo.png, data:image/svg+xml;… or an absolute URL",
            Immediate = true,
            DataContext = nodeContext
        }.WithWidth("100%"));

        // Quick-pick row — after uploading a file via the browser below, type its name here
        // and click "Use as Icon". Writes "content:<filename>" straight to the node's Icon field.
        // The filename text box is transient form state (not node content), so it stays in /data.
        if (editableCollection != null)
        {
            var quickPickDataId = $"iconQuickPick_{nodePath.Replace("/", "_")}";
            host.UpdateData(quickPickDataId, new Dictionary<string, object?> { ["fileName"] = "" });

            section = section.WithView(Controls.Stack
                .WithWidth("100%")
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithStyle("align-items: flex-end;")
                .WithView(new TextFieldControl(new JsonPointerReference("fileName"))
                {
                    Label = "Filename in content collection",
                    Placeholder = "logo.png",
                    Immediate = true,
                    DataContext = LayoutAreaReference.GetDataPointer(quickPickDataId)
                }.WithStyle("flex: 1;"))
                .WithView(Controls.Button(host.Localize("ui.useAsIcon"))
                    .WithAppearance(Appearance.Neutral)
                    .WithReactiveClickAction(actx => UseFileAsIcon(actx, nodePath, quickPickDataId))));
        }

        section = section.WithView(Controls.Body(
            "Tip: upload an image via the browser below, then type its filename and click \"Use as Icon\" — " +
            "or type \"content:logo.png\" directly. You can also paste an absolute URL, an inline <svg>…</svg>, " +
            "or a data:image/svg+xml URI. Click \"Generate\" to have the Node Initializer agent craft one from Name + Description.")
            .WithStyle("color: var(--neutral-foreground-hint); font-size: 12px; margin-top: 4px;"));

        if (collections.Count > 0)
        {
            var collectionOptions = collections
                .Select(c => (Option)new Option<string>(c.Name, c.DisplayName ?? c.Name))
                .ToArray();

            var pickerDataId = $"iconPicker_{nodePath.Replace("/", "_")}";
            var defaultCollection = editableCollection?.Name ?? "";
            host.UpdateData(pickerDataId, new Dictionary<string, object?> { ["collection"] = defaultCollection });
            host.UpdateData($"{pickerDataId}_options", collectionOptions);

            section = section.WithView(new ComboboxControl(
                new JsonPointerReference("collection"),
                new JsonPointerReference(LayoutAreaReference.GetDataPointer($"{pickerDataId}_options")))
            {
                Label = "Browse Collection",
                Placeholder = "Select a collection to browse images...",
                Autocomplete = ComboboxAutocomplete.Both,
                DataContext = LayoutAreaReference.GetDataPointer(pickerDataId)
            });

            section = section.WithView((h, _) =>
                h.Stream.GetDataStream<Dictionary<string, object?>>(pickerDataId)
                    .Select(data =>
                    {
                        var selectedCollection = data?.GetValueOrDefault("collection")?.ToString();
                        if (string.IsNullOrEmpty(selectedCollection))
                            return (UiControl?)Controls.Stack;
                        return (UiControl?)new FileBrowserControl(selectedCollection);
                    }));
        }

        return section;
    }

    /// <summary>
    /// Click handler for the quick-pick "Use as Icon" button: reads the filename the user
    /// typed (transient form state), then writes <c>content:&lt;filename&gt;</c> straight to the
    /// node's <see cref="MeshNode.Icon"/> via the node stream. The icon resolver turns that into
    /// <c>/api/content/{nodePath}/{filename}</c> at render time.
    /// </summary>
    private static IObservable<System.Reactive.Unit> UseFileAsIcon(UiActionContext actx, string nodePath, string quickPickDataId)
    {
        return actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(quickPickDataId)
            .Take(1)
            .Do(data =>
            {
                var fileName = data?.GetValueOrDefault("fileName")?.ToString()?.Trim() ?? "";
                if (string.IsNullOrEmpty(fileName))
                {
                    ShowSettingsErrorDialog(actx, "Use as Icon",
                        "Type the filename (e.g. \"logo.png\") after uploading it to the content collection.");
                    return;
                }

                // Accept a leading slash and strip it; users may paste paths copied from the browser.
                fileName = fileName.TrimStart('/');
                var iconRef = $"content:{fileName}";

                WriteIcon(actx, nodePath, iconRef);
            }).Select(_ => System.Reactive.Unit.Default);
    }

    /// <summary>
    /// Click handler for the Regenerate-icon button in the Settings Metadata tab. Reads Name +
    /// Description from the node stream, invokes the <see cref="IIconGenerator"/>, and writes the
    /// resulting SVG straight back to the node's <see cref="MeshNode.Icon"/> — ONE source of truth.
    /// </summary>
    private static IObservable<System.Reactive.Unit> RegenerateIconFromNode(UiActionContext actx, string nodePath)
    {
        var generator = actx.Host.Hub.ServiceProvider.GetService<IIconGenerator>();
        if (generator == null)
        {
            ShowSettingsErrorDialog(actx, "Regenerate Icon",
                "Icon generator service is not registered. Call AddAgentChatServices().");
            return Observable.Return(System.Reactive.Unit.Default);
        }
        return actx.Host.Workspace.GetMeshNodeStream(nodePath)
            .Where(n => n is not null)
            .Take(1)
            .Do(node =>
            {
                var name = node!.Name ?? "";
                var description = node.Description;
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(description))
                {
                    ShowSettingsErrorDialog(actx, "Regenerate Icon",
                        "Enter a Name or Description first — the agent uses those to craft the icon.");
                    return;
                }
                generator.GenerateSvgAsync(name, description).Subscribe(
                    svg => WriteIcon(actx, nodePath, svg),
                    ex => ShowSettingsErrorDialog(actx, "Icon Generation Failed", ex.Message));
            }).Select(_ => System.Reactive.Unit.Default);
    }

    /// <summary>
    /// Click handler for the Generate-description button in the Settings Display section. Reads
    /// Name + Category from the node stream, invokes the <see cref="IDescriptionGenerator"/>, and
    /// writes the resulting text straight back to the node's <see cref="MeshNode.Description"/>.
    /// </summary>
    private static IObservable<System.Reactive.Unit> RegenerateDescriptionFromNode(UiActionContext actx, string nodePath)
    {
        var generator = actx.Host.Hub.ServiceProvider.GetService<IDescriptionGenerator>();
        if (generator == null)
        {
            ShowSettingsErrorDialog(actx, "Generate Description",
                "Description generator service is not registered. Call AddAgentChatServices().");
            return Observable.Return(System.Reactive.Unit.Default);
        }
        return actx.Host.Workspace.GetMeshNodeStream(nodePath)
            .Where(n => n is not null)
            .Take(1)
            .Do(node =>
            {
                var name = node!.Name ?? "";
                var category = node.Category;
                if (string.IsNullOrWhiteSpace(name))
                {
                    ShowSettingsErrorDialog(actx, "Generate Description",
                        "Enter a Name first — the agent uses it to write the description.");
                    return;
                }
                generator.GenerateDescriptionAsync(name, category).Subscribe(
                    description => actx.Host.Workspace.GetMeshNodeStream(nodePath)
                        .Update(n => n with { Description = description })
                        .Subscribe(_ => { }, ex => actx.Host.Hub.ServiceProvider
                            .GetService<ILoggerFactory>()?.CreateLogger(typeof(SettingsLayoutArea).FullName!)
                            .LogWarning(ex, "Description write failed for {Path}", nodePath)),
                    ex => ShowSettingsErrorDialog(actx, "Description Generation Failed", ex.Message));
            }).Select(_ => System.Reactive.Unit.Default);
    }

    private static void WriteIcon(UiActionContext actx, string nodePath, string icon) =>
        actx.Host.Workspace.GetMeshNodeStream(nodePath)
            .Update(n => n with { Icon = icon })
            .Subscribe(_ => { }, ex => actx.Host.Hub.ServiceProvider
                .GetService<ILoggerFactory>()?.CreateLogger(typeof(SettingsLayoutArea).FullName!)
                .LogWarning(ex, "Icon write failed for {Path}", nodePath));

    private static void ShowSettingsErrorDialog(UiActionContext ctx, string title, string message)
    {
        var errorDialog = Controls.Dialog(
            Controls.Markdown($"**{title}:**\n\n{message}"),
            title
        ).WithSize("M").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
    }

    private static UiControl BuildTimestampsSection(LayoutAreaHost host, MeshNodeMetadata meta, string? zoneId)
        // Stored UTC → the viewer's zone. The "zzz" offset stays, so the rendered value
        // still says which offset it is rather than being an unlabelled wall clock.
        => FactGrid([
            Fact(host.Localize("settings.meta.created"),
                meta.CreatedDate == default ? "—" : DisplayTimeExtensions.ToDisplayTime(meta.CreatedDate, zoneId).ToString("yyyy-MM-dd HH:mm:ss zzz")),
            Fact(host.Localize("settings.meta.lastModified"),
                meta.LastModified == default ? "—" : DisplayTimeExtensions.ToDisplayTime(meta.LastModified, zoneId).ToString("yyyy-MM-dd HH:mm:ss zzz")),
        ]);

    /// <summary>The permissions the Effective Access check reports, in display order.</summary>
    internal static readonly IReadOnlyList<Permission> CheckedPermissions =
        [Permission.Read, Permission.Create, Permission.Update, Permission.Delete, Permission.Comment];

    /// <summary>One row of the Effective Access verdict grid.</summary>
    /// <param name="Permission">The permission's name (a wire identifier, not translated).</param>
    /// <param name="Status">The localized "Allowed" or "Denied".</param>
    internal sealed record PermissionRow(string Permission, string Status);

    /// <summary>The verdict rows for <paramref name="perms"/> — pure, so a test reads them without rendering.</summary>
    /// <param name="perms">The user's effective permissions on the node.</param>
    /// <param name="allowed">The viewer's word for a granted permission.</param>
    /// <param name="denied">The viewer's word for a withheld permission.</param>
    internal static IReadOnlyList<PermissionRow> PermissionRows(Permission perms, string allowed, string denied)
        => [.. CheckedPermissions.Select(p => new PermissionRow(p.ToString(), perms.HasFlag(p) ? allowed : denied))];

    /// <summary>The catalog key of the menu search box's placeholder.</summary>
    internal const string MenuSearchPlaceholderKey = "settings.searchPlaceholder";

    #endregion
}

/// <summary>
/// DTO record exposing editable MeshNode metadata (excluding Content)
/// for the Settings Metadata tab with click-to-edit support.
/// </summary>
public record MeshNodeMetadata
{
    /// <summary>The unique identifier of the mesh node (read-only).</summary>
    [Editable(false)]
    public string? Id { get; init; }

    /// <summary>The display name of the mesh node.</summary>
    public string? Name { get; init; }

    /// <summary>The human-readable description of the mesh node.</summary>
    public string? Description { get; init; }

    /// <summary>The namespace (partition-qualified) the node belongs to (read-only).</summary>
    [Editable(false)]
    public string? Namespace { get; init; }

    /// <summary>The node type of the mesh node (read-only).</summary>
    [Editable(false)]
    public string? NodeType { get; init; }

    /// <summary>The category used to group the node in listings.</summary>
    public string? Category { get; init; }

    /// <summary>The icon associated with the mesh node.</summary>
    public string? Icon { get; init; }

    /// <summary>The sort order of the node within its grouping.</summary>
    public int? Order { get; init; }

    /// <summary>The lifecycle state of the mesh node (read-only).</summary>
    [Editable(false)]
    public MeshNodeState State { get; init; }

    /// <summary>The timestamp when the node was created (read-only).</summary>
    [Editable(false)]
    public DateTimeOffset CreatedDate { get; init; }

    /// <summary>The timestamp when the node was last modified (read-only).</summary>
    [Editable(false)]
    public DateTimeOffset LastModified { get; init; }

    /// <summary>The version number of the mesh node (read-only).</summary>
    [Editable(false)]
    public long Version { get; init; }

    /// <summary>Builds a MeshNodeMetadata from a mesh node, capturing its editable metadata.</summary>
    /// <param name="node">The mesh node to read values from.</param>
    /// <returns>The populated metadata DTO.</returns>
    public static MeshNodeMetadata FromNode(MeshNode node) => new()
    {
        Id = node.Id,
        Name = node.Name,
        Description = node.Description,
        Namespace = node.Namespace,
        NodeType = node.NodeType,
        Category = node.Category,
        Icon = node.Icon,
        Order = node.Order,
        State = node.State,
        CreatedDate = node.CreatedDate,
        LastModified = node.LastModified,
        Version = node.Version,
    };

    /// <summary>Applies the editable metadata values from this DTO onto a mesh node.</summary>
    /// <param name="node">The mesh node to apply the values onto.</param>
    /// <returns>The updated mesh node.</returns>
    public MeshNode ApplyTo(MeshNode node) => node with
    {
        Name = Name,
        Description = Description,
        Category = Category,
        Icon = Icon,
        Order = Order,
    };
}
