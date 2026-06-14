using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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
    internal const string FilesTab = "Files";
    internal const string AccessControlTab = "AccessControl";
    internal const string GroupsTab = "Groups";
    internal const string EffectiveAccessTab = "EffectiveAccess";
    internal const string AppearanceTab = "Appearance";

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

        var ownNode = host.Workspace.GetMeshNodeStream();
        var permsStream = host.Hub.GetEffectivePermissions(hubPath);

        return ownNode.CombineLatest(permsStream, (node, perms) => (Node: node, Perms: perms))
            .SelectMany(t => host.Hub.Configuration.EvaluateSettingsMenuItems(host, ctx, t.Perms)
                .Select(items =>
                {
                    var canEdit = t.Perms.HasFlag(Permission.Update);
                    var selectedTab = string.IsNullOrEmpty(tabId) && items.Count > 0 ? items[0].Id : (tabId ?? MetadataTab);
                    return (UiControl?)BuildSettingsPage(host, t.Node, hubAddress, hubPath, selectedTab, canEdit, items);
                }));
    }

    private static UiControl BuildSettingsPage(
        LayoutAreaHost host,
        MeshNode? node,
        object hubAddress,
        string hubPath,
        string tabId,
        bool canEdit,
        IReadOnlyList<SettingsMenuItemDefinition> items)
    {
        // `settings-splitter` (standard-page-layout.css) gives each pane a definite-height
        // flex context so the right content pane scrolls internally — mirrors the treatment
        // PortalLayoutBase applies to its outer `.body-splitter`. Height fills the parent
        // `.layout-area-container` (flex column) rather than a viewport-minus-magic-number
        // guess, which was overshooting the real header/messagebar height and clipping the
        // bottom of the content out of reach.
        // Do NOT set Height="100%" on the splitter skin. FluentMultiSplitter renders its pane
        // chrome in shadow DOM, so the global `.settings-splitter` CSS can't reach the inner
        // host; the only height the component honours is the `Height` parameter fed from this
        // skin. An inline `height:100%` resolves against an indefinite-height ancestor → it
        // collapses to content height, the splitter grows past the viewport, and nothing
        // scrolls. Instead let the splitter take its height from the `flex:1 1 auto; min-height:0`
        // it already gets via the `.settings-splitter` class inside the flex-column
        // `.layout-area-container` — a real, bounded height the `.settings-content-pane`
        // (overflow-y:auto) scrolls within.
        var settingsPage = Controls.Splitter
            .WithClass("settings-splitter")
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
            // Fill the layout-area-container as a flex column: the banner takes its natural
            // height and the splitter (flex: 1, min-height: 0) shrinks to fill the rest, so
            // the inner content pane still scrolls.
            return Controls.Stack.WithWidth("100%")
                .WithStyle("height: 100%; min-height: 0; display: flex; flex-direction: column; overflow: hidden;")
                .WithView(Controls.Html(
                    "<div style=\"padding: 8px 16px; background: var(--neutral-layer-3); border-bottom: 1px solid var(--neutral-stroke-rest); " +
                    "color: var(--neutral-foreground-hint); font-size: 0.85rem; text-align: center;\">Read-only — you do not have edit permissions</div>"))
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

        var searchBox = (new TextFieldControl(new JsonPointerReference(""))
                .WithPlaceholder("Search settings…")
                .WithImmediate(true) with { DataContext = LayoutAreaReference.GetDataPointer(searchDataId) })
            .WithClass("settings-menu-search");

        return Controls.Stack
            .WithClass("settings-menu-pane")
            .WithWidth("100%")
            .WithView(searchBox)
            .WithView(
                (h, c) => h.GetDataStream<string>(searchDataId)
                    .StartWith(string.Empty)
                    .Select(q => (UiControl)BuildNavMenu(node, hubAddress, hubPath, FilterMenuItems(items, q), selectedTab)),
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

    private static UiControl BuildNavMenu(
        MeshNode? node,
        object hubAddress,
        string hubPath,
        IReadOnlyList<SettingsMenuItemDefinition> items,
        string selectedTab)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Back to node link (always present)
        var backHref = $"/{hubPath}";
        var nodeName = node?.Name ?? "Back";
        navMenu = navMenu.WithView(
            new NavLinkControl(nodeName, FluentIcons.ArrowLeft(), backHref)
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
        // Scroll the content pane internally via the flex-fill pattern defined in
        // standard-page-layout.css — the same treatment PortalLayoutBase applies to its
        // .body-splitter. FluentMultiSplitter renders each pane as LIGHT DOM — a plain
        // <div class="fluent-multi-splitter-pane"> (verified against the FluentUI source;
        // it is NOT a web component / shadow root), so the stylesheet selector
        // `.settings-splitter .fluent-multi-splitter-pane` reaches it: it overrides the
        // component's default `overflow:hidden` pane into a definite-height flex column, makes the
        // content wrapper fill it, and `.settings-content-pane` (flex:1 1 auto; min-height:0;
        // overflow-y:auto) scrolls within. The styling therefore lives in the class — only the
        // local padding is inline. (A prior inline `height:100%` here overrode the class and is the
        // hack this removes.)
        var stack = Controls.Stack
            .WithClass("settings-content-pane")
            .WithWidth("100%");

        var matchedItem = items.FirstOrDefault(i => i.Id == tabId)
            ?? items.FirstOrDefault();

        if (matchedItem == null)
            return stack.WithView(Controls.Html("<p><em>No settings tabs available.</em></p>"));

        try
        {
            return matchedItem.ContentBuilder(host, stack, node);
        }
        catch (Exception ex)
        {
            return stack.WithView(Controls.Html(
                $"<p style=\"color: var(--warning-color);\">Failed to load tab: {System.Web.HttpUtility.HtmlEncode(ex.Message)}</p>"));
        }
    }

    #region Default Tab Content Builders

    internal static UiControl BuildMetadataTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        if (node == null)
        {
            stack = stack.WithView(Controls.Html("<p><em>Node not found.</em></p>"));
            return stack;
        }

        var nodePath = node.Namespace ?? host.Hub.Address.ToString();
        var meta = MeshNodeMetadata.FromNode(node);

        var dataId = $"nodeMeta_{nodePath.Replace("/", "_")}";
        host.UpdateData(dataId, meta);

        SetupNodeMetadataAutoSave(host, dataId, meta, node);

        stack = stack.WithView(BuildSection("Identity", BuildIdentitySection(meta)));
        stack = stack.WithView(BuildSection("Display", BuildDisplaySection(host, dataId)));
        stack = stack.WithView(BuildSection("Timestamps", BuildTimestampsSection(meta)));

        return stack;
    }

    internal static UiControl BuildNodeTypesTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        stack = stack.WithView(Controls.H2("Node Types").WithStyle("margin: 0 0 24px 0;"));
        stack = stack.WithView(
            (h, ctx) => MeshNodeLayoutAreas.NodeTypes(h, ctx)!,
            "NodeTypesContent"
        );
        return stack;
    }

    internal static UiControl BuildFilesTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        stack = stack.WithView(Controls.H2("Files").WithStyle("margin: 0 0 24px 0;"));

        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.ToList();

        if (collections is not { Count: > 0 })
        {
            stack = stack.WithView(new FileBrowserControl("content"));
            return stack;
        }

        var options = collections
            .Select(c => (Option)new Option<string>(c.Name, c.DisplayName ?? c.Name))
            .ToArray();

        var selectDataId = "filesTabCollectionSelect";
        var optionsDataId = "filesTabCollectionOptions";

        host.UpdateData(selectDataId, new Dictionary<string, object?> { ["collection"] = collections[0].Name });
        host.UpdateData(optionsDataId, options);

        stack = stack.WithView(new ComboboxControl(
            new JsonPointerReference("collection"),
            new JsonPointerReference(LayoutAreaReference.GetDataPointer(optionsDataId)))
        {
            Label = "Collection",
            Autocomplete = ComboboxAutocomplete.Both,
            DataContext = LayoutAreaReference.GetDataPointer(selectDataId)
        });

        stack = stack.WithView((h, _) =>
            h.Stream.GetDataStream<Dictionary<string, object?>>(selectDataId)
                .Select(data =>
                {
                    var selected = data?.GetValueOrDefault("collection")?.ToString();
                    if (string.IsNullOrEmpty(selected))
                        return (UiControl?)Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Select a collection.</p>");
                    return (UiControl?)new FileBrowserControl(selected);
                }));

        return stack;
    }

    internal static UiControl BuildAccessControlTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        stack = stack.WithView(
            (h, ctx) => MeshNodeLayoutAreas.AccessControl(h, ctx)!,
            "AccessControlContent"
        );
        return stack;
    }

    internal static UiControl BuildGroupsTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var hubPath = host.Hub.Address.ToString();
        stack = stack.WithView(Controls.H2("Groups").WithStyle("margin: 0 0 16px 0;"));

        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshQuery == null)
        {
            stack = stack.WithView(Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">Query service not available.</p>"));
            return stack;
        }

        stack = stack.WithView((h, _) =>
            meshQuery
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{hubPath} nodeType:Group"))
                .Select(change =>
                {
                    var groupNodes = change.Items?.ToList() ?? [];
                    if (groupNodes.Count == 0)
                        return (UiControl?)Controls.Html("<p style=\"color: var(--neutral-foreground-hint);\">No groups defined at this level.</p>");

                    var container = Controls.Stack.WithStyle("gap: 8px;");
                    foreach (var groupNode in groupNodes.OrderBy(n => n.Order).ThenBy(n => n.Name))
                    {
                        container = container.WithView(
                            MeshNodeThumbnailControl.FromNode(groupNode, groupNode.Path));
                    }
                    return (UiControl?)container;
                }));

        return stack;
    }

    internal static UiControl BuildEffectiveAccessTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var hubPath = host.Hub.Address.ToString();
        if (host.Hub.Configuration.Get<EffectivePermissionsDelegate>() is null)
        {
            return stack.WithView(Controls.Html(
                "<p style=\"color: var(--warning-color);\">Row-Level Security is not enabled.</p>"));
        }

        stack = stack.WithView(Controls.H2("Effective Access").WithStyle("margin: 0 0 16px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "Test what permissions a user has on this node. Enter a user ID and press Enter or click Check.</p>"));

        var formId = $"effectiveAccess_{hubPath.Replace("/", "_")}";
        var resultId = $"effectiveAccessResult_{hubPath.Replace("/", "_")}";
        host.UpdateData(formId, new Dictionary<string, object?> { ["userId"] = "" });
        host.UpdateData(resultId, "");

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
            .WithStyle("margin-top: 12px; gap: 8px;")
            .WithView(Controls.Button("Check")
                .WithAppearance(Appearance.Accent)
                .WithClickAction((Action<UiActionContext>)(ctx =>
                {
                    // Pure reactive — Subscribe to the form value, then SelectMany
                    // into the permission stream and write the rendered HTML back
                    // to the result data slot. No await, no Task bridging.
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                        .Take(1)
                        .SelectMany(data =>
                        {
                            var userId = data?.GetValueOrDefault("userId")?.ToString()?.Trim() ?? "";
                            if (string.IsNullOrEmpty(userId))
                            {
                                ctx.Host.UpdateData(resultId, "<p style=\"color: var(--warning-color);\">Please enter a user ID.</p>");
                                return Observable.Empty<(string UserId, Permission Perms)>();
                            }
                            return host.Hub.GetEffectivePermissions(hubPath, userId)
                                .Take(1)
                                .Select(perms => (UserId: userId, Perms: perms));
                        })
                        .Subscribe(t => ctx.Host.UpdateData(resultId,
                            BuildPermissionResultHtml(t.UserId, t.Perms)));
                }))));

        stack = stack.WithView((h, _) =>
        {
            return h.Stream.GetDataStream<string>(resultId)
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl)Controls.Stack
                    : (UiControl)Controls.Html(html));
        });

        return stack;
    }

    internal static UiControl BuildAppearanceTab(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        stack = stack.WithView(Controls.H2("Appearance").WithStyle("margin: 0 0 24px 0;"));
        stack = stack.WithView(new AppearanceControl());
        return stack;
    }

    #endregion

    #region Helpers

    // Modern-business section card. The card / header / body styling lives in
    // standard-page-layout.css (.settings-section[-header|-body]) so it's consistent across tabs
    // and tweakable in one place — only the title text is dynamic here.
    private static UiControl BuildSection(string title, UiControl content)
    {
        return Controls.Stack
            .WithClass("settings-section")
            .WithWidth("100%")
            .WithView(Controls.Html(
                $"<div class=\"settings-section-header\">{System.Web.HttpUtility.HtmlEncode(title)}</div>"))
            .WithView(Controls.Stack.WithClass("settings-section-body").WithWidth("100%").WithView(content));
    }

    // Responsive metadata grid: label/value cells flow into as many columns as the width
    // allows — one column on narrow screens, several on a wide one — instead of a fixed
    // two-column (label | value) layout that wasted horizontal space.
    private const string MetaGridStyle =
        "display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); " +
        "gap: 14px 28px; font-size: 0.9rem; align-items: start;";

    private static UiControl BuildIdentitySection(MeshNodeMetadata meta)
    {
        var grid = Controls.Stack.WithWidth("100%").WithStyle(MetaGridStyle);
        // Node Type first, as a direct link to the type's Configuration area.
        grid = AddNodeTypeField(grid, meta.NodeType);
        grid = AddReadOnlyField(grid, "Id", meta.Id);
        grid = AddReadOnlyField(grid, "Namespace", meta.Namespace);
        grid = AddReadOnlyField(grid, "State", meta.State.ToString());
        grid = AddReadOnlyField(grid, "Version", meta.Version.ToString());
        return grid;
    }

    /// <summary>
    /// Node Type cell: NodeTypes are themselves MeshNodes, so the type name links straight
    /// to the type definition's Configuration area (same pattern as the header meta row).
    /// </summary>
    private static StackControl AddNodeTypeField(StackControl grid, string? nodeType)
    {
        string valueHtml;
        if (string.IsNullOrEmpty(nodeType) || nodeType == MeshNode.NodeTypePath)
        {
            valueHtml = $"<span style=\"word-break: break-word;\">{System.Web.HttpUtility.HtmlEncode(nodeType ?? "—")}</span>";
        }
        else
        {
            var href = MeshNodeLayoutAreas.BuildUrl(nodeType, NodeTypeLayoutAreas.ConfigurationArea);
            var label = nodeType.Contains('/') ? nodeType.Split('/').Last() : nodeType;
            valueHtml =
                $"<a href=\"{System.Web.HttpUtility.HtmlEncode(href)}\" " +
                "style=\"color: var(--accent-fill-rest); font-weight: 500; text-decoration: none; word-break: break-word;\">" +
                $"{System.Web.HttpUtility.HtmlEncode(label)}</a>";
        }
        return grid.WithView(Controls.Html(BuildMetaCell("Node Type", valueHtml)));
    }

    private static StackControl AddReadOnlyField(StackControl grid, string label, string? value)
    {
        var valueHtml = $"<span style=\"word-break: break-word;\">{System.Web.HttpUtility.HtmlEncode(value ?? "—")}</span>";
        return grid.WithView(Controls.Html(BuildMetaCell(label, valueHtml)));
    }

    private static string BuildMetaCell(string label, string valueHtml) =>
        "<div style=\"display: flex; flex-direction: column; gap: 2px; min-width: 0;\">" +
        "<span style=\"color: var(--neutral-foreground-hint); font-weight: 600; font-size: 0.72rem; " +
        $"text-transform: uppercase; letter-spacing: 0.04em;\">{System.Web.HttpUtility.HtmlEncode(label)}</span>" +
        valueHtml +
        "</div>";

    private static UiControl BuildDisplaySection(LayoutAreaHost host, string dataId)
    {
        var dataPointer = LayoutAreaReference.GetDataPointer(dataId);
        var stack = Controls.Stack.WithWidth("100%").WithStyle("gap: 16px;");

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("Name"))
        {
            Label = "Name",
            Immediate = true,
            DataContext = dataPointer
        }.WithWidth("100%"));

        // Description + Generate button on its own row, matching the icon layout below.
        stack = stack.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("gap: 8px;")
            .WithView(Controls.Html("<label style=\"font-weight: 500; font-size: 0.85rem;\">Description</label>"))
            .WithView(new MarkdownEditorControl
            {
                Value = new JsonPointerReference("Description"),
                Height = "300px",
                Placeholder = "Long-form description. Seeds AI Name/Id/Icon generation and appears in detail views.",
                DataContext = dataPointer
            }.WithStyle("width: 100%;"))
            .WithView(Controls.Stack
                .WithWidth("100%")
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithStyle("justify-content: flex-end;")
                .WithView(Controls.Button("Generate")
                    .WithAppearance(Appearance.Neutral)
                    .WithIconStart(FluentIcons.Sparkle())
                    .WithClickAction(actx => RegenerateDescriptionFromMetadata(actx, dataId)))));

        stack = stack.WithView(new TextFieldControl(new JsonPointerReference("Category"))
        {
            Label = "Category",
            Immediate = true,
            DataContext = dataPointer
        }.WithWidth("100%"));

        stack = stack.WithView(BuildIconPicker(host, dataId));

        stack = stack.WithView(new NumberFieldControl(new JsonPointerReference("Order"), "Int32?")
        {
            Label = "Order",
            Immediate = true,
            DataContext = dataPointer
        }.WithWidth("100%"));

        return stack;
    }

    private static UiControl BuildIconPicker(LayoutAreaHost host, string metadataDataId)
    {
        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.ToList() ?? [];
        var editableCollection = collections.FirstOrDefault(c => c.IsEditable);
        var metadataPointer = LayoutAreaReference.GetDataPointer(metadataDataId);

        var section = Controls.Stack.WithWidth("100%").WithStyle("gap: 8px;");
        section = section.WithView(Controls.Html(
            "<label style=\"font-weight: 500; font-size: 0.85rem;\">Icon</label>"));

        // Live preview + Regenerate button — mirrors the Create form's layout so the
        // two surfaces feel consistent.
        section = section.WithView(Controls.Stack
            .WithWidth("100%")
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("align-items: center;")
            .WithView((h, _) => h.Stream.GetDataStream<MeshNodeMetadata>(metadataDataId)
                .Select(meta =>
                {
                    var icon = meta?.Icon ?? "";
                    return string.IsNullOrEmpty(icon)
                        ? Controls.Html("<div style=\"width:48px;height:48px;border:1px dashed var(--neutral-stroke-rest);border-radius:6px;\"></div>")
                        : CreateLayoutArea.BuildIconPreview(icon);
                }))
            .WithView(Controls.Button("Generate")
                .WithAppearance(Appearance.Neutral)
                .WithIconStart(FluentIcons.Sparkle())
                .WithClickAction(actx => RegenerateIconFromMetadata(actx, metadataDataId))));

        section = section.WithView(new TextFieldControl(new JsonPointerReference("Icon"))
        {
            Label = "Icon Path",
            Placeholder = "content:logo.png, /static/…, data:image/svg+xml;… or an absolute URL",
            Immediate = true,
            DataContext = metadataPointer
        }.WithWidth("100%"));

        // Quick-pick row — after uploading a file via the browser below, type its name here
        // and click "Use as Icon". Writes "content:<filename>" which resolves to the node's
        // content collection at render time.
        if (editableCollection != null)
        {
            var quickPickDataId = $"iconQuickPick_{metadataDataId}";
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
                .WithView(Controls.Button("Use as Icon")
                    .WithAppearance(Appearance.Neutral)
                    .WithClickAction(actx => UseFileAsIcon(actx, metadataDataId, quickPickDataId))));
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

            var pickerDataId = $"iconPicker_{metadataDataId}";
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
    /// typed, writes <c>content:&lt;filename&gt;</c> into the metadata's Icon field. The
    /// icon resolver turns that into <c>/static/storage/content/{nodePath}/{filename}</c>
    /// at render time.
    /// </summary>
    private static void UseFileAsIcon(UiActionContext actx, string metadataDataId, string quickPickDataId)
    {
        actx.Host.Stream.GetDataStream<Dictionary<string, object?>>(quickPickDataId)
            .Take(1)
            .Subscribe(data =>
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

                actx.Host.Stream.GetDataStream<MeshNodeMetadata>(metadataDataId)
                    .Take(1)
                    .Subscribe(meta =>
                    {
                        var updated = (meta ?? new MeshNodeMetadata()) with { Icon = iconRef };
                        actx.Host.UpdateData(metadataDataId, updated);
                    });
            });
    }

    /// <summary>
    /// Click handler for the Regenerate-icon button in the Settings Metadata tab.
    /// Reads Name + Description from the MeshNodeMetadata stream, invokes the
    /// IIconGenerator, and writes the resulting SVG back into the metadata object
    /// so the auto-save subscription persists it on the node.
    /// </summary>
    private static void RegenerateIconFromMetadata(UiActionContext actx, string metadataDataId)
    {
        var generator = actx.Host.Hub.ServiceProvider.GetService<IIconGenerator>();
        if (generator == null)
        {
            ShowSettingsErrorDialog(actx, "Regenerate Icon",
                "Icon generator service is not registered. Call AddAgentChatServices().");
            return;
        }
        actx.Host.Stream.GetDataStream<MeshNodeMetadata>(metadataDataId)
            .Take(1)
            .Subscribe(meta =>
            {
                var name = meta?.Name ?? "";
                var description = meta?.Description;
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(description))
                {
                    ShowSettingsErrorDialog(actx, "Regenerate Icon",
                        "Enter a Name or Description first — the agent uses those to craft the icon.");
                    return;
                }
                generator.GenerateSvgAsync(name, description).Subscribe(
                    svg =>
                    {
                        // Replace the Icon field on the metadata record; the Throttled
                        // auto-save subscription picks this up and posts UpdateNodeRequest.
                        var updated = (meta ?? new MeshNodeMetadata()) with { Icon = svg };
                        actx.Host.UpdateData(metadataDataId, updated);
                    },
                    ex => ShowSettingsErrorDialog(actx, "Icon Generation Failed", ex.Message));
            });
    }

    /// <summary>
    /// Click handler for the Generate-description button in the Settings Display section.
    /// Reads Name + Category from the MeshNodeMetadata stream, invokes the
    /// IDescriptionGenerator, and writes the resulting text back into the metadata object
    /// so the auto-save subscription persists it on the node.
    /// </summary>
    private static void RegenerateDescriptionFromMetadata(UiActionContext actx, string metadataDataId)
    {
        var generator = actx.Host.Hub.ServiceProvider.GetService<IDescriptionGenerator>();
        if (generator == null)
        {
            ShowSettingsErrorDialog(actx, "Generate Description",
                "Description generator service is not registered. Call AddAgentChatServices().");
            return;
        }
        actx.Host.Stream.GetDataStream<MeshNodeMetadata>(metadataDataId)
            .Take(1)
            .Subscribe(meta =>
            {
                var name = meta?.Name ?? "";
                var category = meta?.Category;
                if (string.IsNullOrWhiteSpace(name))
                {
                    ShowSettingsErrorDialog(actx, "Generate Description",
                        "Enter a Name first — the agent uses it to write the description.");
                    return;
                }
                generator.GenerateDescriptionAsync(name, category).Subscribe(
                    description =>
                    {
                        var updated = (meta ?? new MeshNodeMetadata()) with { Description = description };
                        actx.Host.UpdateData(metadataDataId, updated);
                    },
                    ex => ShowSettingsErrorDialog(actx, "Description Generation Failed", ex.Message));
            });
    }

    private static void ShowSettingsErrorDialog(UiActionContext ctx, string title, string message)
    {
        var errorDialog = Controls.Dialog(
            Controls.Markdown($"**{title}:**\n\n{message}"),
            title
        ).WithSize("M").WithClosable(true);
        ctx.Host.UpdateArea(DialogControl.DialogArea, errorDialog);
    }

    private static UiControl BuildTimestampsSection(MeshNodeMetadata meta)
    {
        var grid = Controls.Stack.WithWidth("100%").WithStyle(MetaGridStyle);
        grid = AddReadOnlyField(grid, "Created",
            meta.CreatedDate == default ? "—" : meta.CreatedDate.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        grid = AddReadOnlyField(grid, "Last Modified",
            meta.LastModified == default ? "—" : meta.LastModified.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        return grid;
    }

    private static void SetupNodeMetadataAutoSave(
        LayoutAreaHost host,
        string dataId,
        MeshNodeMetadata initial,
        MeshNode node)
    {
        var current = (object)initial;

        host.RegisterForDisposal($"autosave_{dataId}",
            host.Stream.GetDataStream<object>(dataId)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Subscribe(updated =>
                {
                    if (object.Equals(current, updated))
                        return;

                    current = updated;

                    if (updated is not MeshNodeMetadata updatedMeta)
                        return;

                    // Write through the shared cache so every reader of the
                    // node's stream sees the change (AGENTS.md "NodeMutations:
                    // stream.Update only"). updatedMeta.ApplyTo composes the
                    // patch inside the cache.Update lambda against the live
                    // node — applying the metadata patch on top of whichever
                    // version the cache holds, not the stale node captured at
                    // SetupNodeMetadataAutoSave time.
                    if (node.Path is { Length: > 0 } metaPath)
                    {
                        var cache = host.Hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();
                        cache.Update(metaPath, current => updatedMeta.ApplyTo(current), host.Hub.JsonSerializerOptions)
                            .Subscribe(
                                _ => { },
                                ex => host.Hub.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()
                                    ?.CreateLogger(typeof(SettingsLayoutArea).FullName!)
                                    .LogWarning(ex, "Metadata auto-save failed for {Path}", metaPath));
                    }
                }));
    }

    private static string BuildPermissionResultHtml(string userId, Permission perms)
    {
        var allPerms = new[] { Permission.Read, Permission.Create, Permission.Update, Permission.Delete, Permission.Comment };
        var rows = string.Join("", allPerms.Select(p =>
        {
            var has = perms.HasFlag(p);
            var icon = has ? "<span style=\"color: #4ade80;\">&#x2713;</span>" : "<span style=\"color: #f87171;\">&#x2717;</span>";
            return $"<tr><td style=\"padding: 6px 12px;\">{p}</td><td style=\"padding: 6px 12px;\">{icon} {(has ? "Allowed" : "Denied")}</td></tr>";
        }));

        return $@"
            <div style=""margin-top: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 6px; overflow: hidden;"">
                <div style=""padding: 10px 12px; background: var(--neutral-layer-2); font-weight: 600; font-size: 0.85rem;"">
                    Permissions for <em>{System.Web.HttpUtility.HtmlEncode(userId)}</em> on this node
                </div>
                <table style=""width: 100%; border-collapse: collapse; font-size: 0.85rem;"">
                    <thead>
                        <tr style=""border-bottom: 1px solid var(--neutral-stroke-rest); background: var(--neutral-layer-3);"">
                            <th style=""padding: 6px 12px; text-align: left;"">Permission</th>
                            <th style=""padding: 6px 12px; text-align: left;"">Status</th>
                        </tr>
                    </thead>
                    <tbody>{rows}</tbody>
                </table>
            </div>";
    }

    #endregion
}

/// <summary>
/// DTO record exposing editable MeshNode metadata (excluding Content)
/// for the Settings Metadata tab with click-to-edit support.
/// </summary>
public record MeshNodeMetadata
{
    [Editable(false)]
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    [Editable(false)]
    public string? Namespace { get; init; }

    [Editable(false)]
    public string? NodeType { get; init; }

    public string? Category { get; init; }

    public string? Icon { get; init; }

    public int? Order { get; init; }

    [Editable(false)]
    public MeshNodeState State { get; init; }

    [Editable(false)]
    public DateTimeOffset CreatedDate { get; init; }

    [Editable(false)]
    public DateTimeOffset LastModified { get; init; }

    [Editable(false)]
    public long Version { get; init; }

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

    public MeshNode ApplyTo(MeshNode node) => node with
    {
        Name = Name,
        Description = Description,
        Category = Category,
        Icon = Icon,
        Order = Order,
    };
}
