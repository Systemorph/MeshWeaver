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
/// URL pattern: /_settings/GlobalSettings/{tabId}
/// Tabs are registered via <see cref="GlobalSettingsMenuItemsExtensions.AddGlobalSettingsMenuItems"/>.
/// Follows the same pattern as <see cref="SettingsLayoutArea"/> for node settings.
/// </summary>
public static class GlobalSettingsLayoutArea
{
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

        return Observable.FromAsync(async () =>
        {
            var items = await host.Hub.Configuration
                .EvaluateGlobalSettingsMenuItemsAsync(host, ctx);

            if (string.IsNullOrEmpty(tabId) && items.Count > 0)
                tabId = items[0].Id;
            tabId ??= DataSourcesTab;

            return (UiControl?)BuildGlobalSettingsPage(host, tabId, items);
        });
    }

    private static UiControl BuildGlobalSettingsPage(
        LayoutAreaHost host,
        string tabId,
        IReadOnlyList<GlobalSettingsMenuItemDefinition> items)
    {
        var hubAddress = host.Hub.Address;

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
        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; height: 100%; overflow: auto;");

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
    /// Data Sources tab: lists all MeshDataSource nodes with status and actions.
    /// </summary>
    internal static UiControl BuildDataSourcesTab(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Data Sources").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "Registered data source repositories. Enable or disable sources, install data, or export subtrees.</p>"));

        var meshService = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (meshService == null)
        {
            stack = stack.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint);\">Mesh service not available.</p>"));
            return stack;
        }

        stack = stack.WithView((h, _) =>
            meshService
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{MeshDataSourceNodeType.SourcesNamespace} nodeType:{MeshDataSourceNodeType.NodeType}"))
                .Select(change =>
                {
                    var nodes = change.Items?.ToList() ?? [];
                    if (nodes.Count == 0)
                        return (UiControl?)Controls.Html(
                            "<p style=\"color: var(--neutral-foreground-hint);\">No data sources registered.</p>");

                    return (UiControl?)BuildDataSourcesList(nodes);
                }));

        return stack;
    }

    private static UiControl BuildDataSourcesList(List<MeshNode> nodes)
    {
        var container = Controls.Stack.WithWidth("100%").WithStyle("gap: 12px;");

        foreach (var node in nodes.OrderBy(n => n.Name))
        {
            var config = node.Content as MeshDataSourceConfiguration;
            var isEnabled = config?.Enabled ?? true;
            var isSearchable = config?.IncludeInSearch ?? true;
            var isInstalled = !string.IsNullOrEmpty(config?.InstalledTo);

            var card = Controls.Stack.WithWidth("100%")
                .WithStyle("padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; gap: 8px;");

            // Header row: name + provider badge + status
            var header = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("align-items: center; gap: 8px;");
            header = header.WithView(Controls.Html(
                $"<div style=\"font-size: 1.05rem; font-weight: 600;\">{Esc(node.Name ?? node.Path)}</div>"));

            if (config?.ProviderType != null)
            {
                header = header.WithView(Controls.Html(
                    $"<span style=\"font-size: 0.75rem; padding: 2px 8px; background: var(--neutral-layer-2); border-radius: 4px;\">{Esc(config.ProviderType)}</span>"));
            }

            var statusColor = isEnabled ? "#4ade80" : "#f87171";
            var statusText = isEnabled ? "Enabled" : "Disabled";
            header = header.WithView(Controls.Html(
                $"<span style=\"margin-left: auto; font-size: 0.8rem; font-weight: 600; color: {statusColor};\">{statusText}</span>"));

            card = card.WithView(header);

            // Description
            if (!string.IsNullOrEmpty(config?.Description))
            {
                card = card.WithView(Controls.Html(
                    $"<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin: 0;\">{Esc(config.Description)}</p>"));
            }

            // Installed note
            if (isInstalled)
            {
                card = card.WithView(Controls.Html(
                    $"<div style=\"padding: 6px 10px; background: var(--warning-fill-rest); border-radius: 6px; font-size: 0.8rem;\">" +
                    $"Installed to <strong>{Esc(config!.InstalledTo!)}</strong>" +
                    (config.LastSyncedAt.HasValue ? $" — last synced: {config.LastSyncedAt.Value:yyyy-MM-dd HH:mm}" : "") +
                    "</div>"));
            }

            // Info row: search status + storage path
            var info = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 16px; font-size: 0.8rem; color: var(--neutral-foreground-hint);");
            info = info.WithView(Controls.Html(
                $"<span>Search: <strong>{(isSearchable ? "Included" : "Excluded")}</strong></span>"));
            if (config?.StorageConfig?.BasePath != null)
            {
                info = info.WithView(Controls.Html(
                    $"<span>Path: <code>{Esc(config.StorageConfig.BasePath)}</code></span>"));
            }
            card = card.WithView(info);

            // Action buttons
            var buttonRow = Controls.Stack.WithOrientation(Orientation.Horizontal)
                .WithStyle("gap: 8px; margin-top: 4px;");

            buttonRow = buttonRow.WithView(Controls.Button("Open")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(ctx =>
                {
                    var navService = ctx.Host.Hub.ServiceProvider.GetService<INavigationService>();
                    navService?.NavigateTo($"/{node.Path}");
                    return Task.CompletedTask;
                }));

            card = card.WithView(buttonRow);

            container = container.WithView(card);
        }

        return container;
    }

    #endregion

    private static string Esc(string s) => System.Web.HttpUtility.HtmlEncode(s);
}
