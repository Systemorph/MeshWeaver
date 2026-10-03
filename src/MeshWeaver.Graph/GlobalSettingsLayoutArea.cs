using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Global settings page with Splitter layout: left NavMenu + right content pane.
/// URL pattern: /_Setting/GlobalSettings/{tabId} — the node path is
/// <see cref="GlobalSettingsNodeType.SettingsPath"/>, and in-app links are built with
/// <see cref="GlobalSettingsNodeType.TabHref"/> rather than spelled out (#1817).
/// Tabs are registered via <see cref="GlobalSettingsMenuItemsExtensions.AddGlobalSettingsMenuItems(MeshWeaver.Messaging.MessageHubConfiguration, MeshWeaver.Mesh.GlobalSettingsMenuItemProvider[])"/>.
/// Follows the same pattern as <see cref="SettingsLayoutArea"/> for node settings.
/// </summary>
public static class GlobalSettingsLayoutArea
{
    /// <summary>Area name for the GlobalSettings layout area.</summary>
    public const string GlobalSettingsArea = "GlobalSettings";
    internal const string DataSourcesTab = "DataSources";

    /// <summary>
    /// Renders the global settings page with Splitter layout.
    /// Left pane: NavMenu with tab links (dynamically built from registered providers).
    /// Right pane: Content based on host.Reference.Id (tab selection).
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> GlobalSettings(LayoutAreaHost host, RenderingContext ctx)
    {
        var tabId = host.Reference.Id?.ToString();

        // A platform-admin tab that moved into the Admin app (Invitations, Updates, Data Sources, …)
        // redirects an old /_Setting/GlobalSettings/{id} link into the app.
        if (SettingsRedirect.For(host, tabId?.Split('?')[0]) is { } redirect)
            return Observable.Return<UiControl?>(redirect);

        // Reactive menu evaluation — re-renders when a provider's live admin check resolves.
        return host.Hub.Configuration.EvaluateGlobalSettingsMenuItems(host, ctx)
            .Select(items =>
            {
                var selectedTab = string.IsNullOrEmpty(tabId) && items.Count > 0 ? items[0].Id : (tabId ?? "");
                return (UiControl?)BuildGlobalSettingsPage(host, selectedTab, items);
            });
    }

    private static UiControl BuildGlobalSettingsPage(
        LayoutAreaHost host,
        string tabId,
        IReadOnlyList<GlobalSettingsMenuItemDefinition> items)
    {
        var hubAddress = host.Hub.Address;

        // Translate tab labels and group names for THIS subscriber (see
        // SettingsLayoutArea.BuildMenuPane — same seam, node-independent variant).
        var access = host.Hub.ServiceProvider.GetService<AccessService>();
        items = [.. items.Select(i => i.Localized(access))];

        return Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                BuildNavMenu(hubAddress, items),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                BuildContentPane(host, tabId, items),
                skin => skin.WithSize("*")
            );
    }

    private static UiControl BuildNavMenu(
        object hubAddress,
        IReadOnlyList<GlobalSettingsMenuItemDefinition> items)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Back to root link
        navMenu = navMenu.WithView(
            new NavLinkControl("Home", FluentIcons.Home(), "/")
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
                var href = new LayoutAreaReference(GlobalSettingsArea) { Id = item.Id }.ToHref(hubAddress);
                navMenu = navMenu.WithView(new NavLinkControl(item.Label, item.Icon, href));
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
                    var href = new LayoutAreaReference(GlobalSettingsArea) { Id = item.Id }.ToHref(hubAddress);
                    navGroup = navGroup.WithView(new NavLinkControl(item.Label, item.Icon, href));
                }

                navMenu = navMenu.WithNavGroup(navGroup);
            }
        }

        return navMenu;
    }

    private static UiControl BuildContentPane(
        LayoutAreaHost host,
        string tabId,
        IReadOnlyList<GlobalSettingsMenuItemDefinition> items)
    {
        // Pin a max-height + overflow-y so the tab content scrolls. `height: 100%`
        // on a fluent-stack inside a splitter pane often resolves against
        // natural content height, not the pane — long tabs (Data Sources with
        // many repos) used to overflow the page instead.
        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; max-height: calc(100vh - 100px); overflow-y: auto; overflow-x: hidden;");

        var matchedItem = items.FirstOrDefault(i => i.Id == tabId)
            ?? items.FirstOrDefault();

        if (matchedItem == null)
            return stack.WithView(Controls.Html("<p><em>No global settings tabs available.</em></p>"));

        try
        {
            return matchedItem.ContentBuilder(host, stack);
        }
        catch (Exception ex)
        {
            return stack.WithView(Controls.Html(
                $"<p style=\"color: var(--warning-color);\">Failed to load tab: {System.Web.HttpUtility.HtmlEncode(ex.Message)}</p>"));
        }
    }

    #region Default Tab Content Builders

    /// <summary>
    /// The Data Sources tab as an Admin-app tab. It used to be the first tab of the global settings
    /// page, where every signed-in viewer landed on it; the sources are platform configuration, so
    /// it lives in the Admin app now (platform admins only) and an old link redirects there.
    /// </summary>
    internal static SettingsMenuItemDefinition DataSourcesAdminTab { get; } = new(
        Id: DataSourcesTab,
        Label: "Data Sources",
        ContentBuilder: (host, stack, _) => BuildDataSourcesTab(host, stack),
        Group: AdminAppNodeType.OperationsGroup,
        Icon: FluentIcons.Database(),
        GroupIcon: FluentIcons.Wrench(),
        Order: AdminAppNodeType.OperationsOrder + 30,
        Keywords: ["data sources", "sources", "repositories", "install", "export"])
    { LabelKey = "settings.dataSources", GroupKey = AdminAppNodeType.OperationsGroupKey };

    /// <summary>
    /// Data Sources tab: the registered MeshDataSource nodes, listed by the GUI
    /// (<see cref="DataSourcesList"/>) — a template, so the tab renders at once and the list fills in.
    /// </summary>
    internal static UiControl BuildDataSourcesTab(LayoutAreaHost host, StackControl stack)
        => stack
            .WithView(Controls.H2(host.Localize("settings.dataSources")).WithStyle("margin: 0 0 8px 0;"))
            .WithView(Controls.Markdown(host.Localize("settings.dataSourcesIntro")))
            .WithView(DataSourcesList());

    /// <summary>
    /// The registered data sources as a query control: the viewer's client runs the query and
    /// renders each source as a card that opens it, and keeps the list live. It used to be loaded
    /// here and drawn as hand-built HTML cards, so the tab waited on the query and showed a
    /// snapshot (Doc/GUI/DataBinding → "Templates first, data later").
    /// </summary>
    internal static MeshSearchControl DataSourcesList()
        => Controls.MeshSearch
            .WithHiddenQuery(
                $"namespace:{MeshDataSourceNodeType.SourcesNamespace} nodeType:{MeshDataSourceNodeType.NodeType} sort:name")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.List)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithReactiveMode(true);

    #endregion
}
