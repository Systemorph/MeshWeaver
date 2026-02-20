using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Unified Settings page with Splitter layout: left NavMenu + right content pane.
/// URL pattern: /{nodePath}/Settings/{tabId}
/// The host.Reference.Id determines which tab to show.
/// </summary>
public static class SettingsLayoutArea
{
    public const string PropertiesTab = "Properties";
    public const string NodeTypesTab = "NodeTypes";
    public const string FilesTab = "Files";
    public const string AccessControlTab = "AccessControl";
    public const string EffectiveAccessTab = "EffectiveAccess";
    public const string AppearanceTab = "Appearance";

    private const string SelectionDataId = "settingsSelection";

    /// <summary>
    /// Renders the unified Settings page with Splitter layout.
    /// Left pane: NavMenu with tab links.
    /// Right pane: Content based on host.Reference.Id (tab selection).
    /// </summary>
    [Browsable(false)]
    public static IObservable<UiControl?> Settings(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var hubAddress = host.Hub.Address;
        var tabId = host.Reference.Id?.ToString();

        // Default to Properties tab
        if (string.IsNullOrEmpty(tabId))
            tabId = PropertiesTab;

        // Get the node from the workspace stream
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var node = nodes.FirstOrDefault(n => n.Path == hubPath);
            var canEdit = await PermissionHelper.CanEditAsync(host.Hub, hubPath);
            return (UiControl?)BuildSettingsPage(host, node, hubAddress, hubPath, tabId, canEdit);
        });
    }

    private static UiControl BuildSettingsPage(
        LayoutAreaHost host,
        MeshNode? node,
        object hubAddress,
        string hubPath,
        string tabId,
        bool canEdit = true)
    {
        var settingsPage = Controls.Splitter
            .WithSkin(s => s.WithOrientation(Orientation.Horizontal).WithWidth("100%").WithHeight("calc(100vh - 100px)"))
            .WithView(
                BuildNavMenu(host, node, hubAddress, hubPath, tabId),
                skin => skin.WithSize("280px").WithMin("200px").WithMax("400px").WithCollapsible(true)
            )
            .WithView(
                BuildContentPane(host, node, hubPath, tabId),
                skin => skin.WithSize("*")
            );

        if (!canEdit)
        {
            // Show read-only indicator at the top
            return Controls.Stack.WithWidth("100%")
                .WithView(Controls.Html(
                    "<div style=\"padding: 8px 16px; background: var(--neutral-layer-3); border-bottom: 1px solid var(--neutral-stroke-rest); " +
                    "color: var(--neutral-foreground-hint); font-size: 0.85rem; text-align: center;\">Read-only — you do not have edit permissions</div>"))
                .WithView(settingsPage);
        }

        return settingsPage;
    }

    private static UiControl BuildNavMenu(
        LayoutAreaHost host,
        MeshNode? node,
        object hubAddress,
        string hubPath,
        string activeTab)
    {
        var navMenu = Controls.NavMenu.WithSkin(s => s.WithWidth(280).WithCollapsible(false));

        // Back to node link
        var backHref = $"/{hubPath}";
        var nodeName = node?.Name ?? "Back";
        navMenu = navMenu.WithView(
            new NavLinkControl(nodeName, FluentIcons.ArrowLeft(), backHref)
        );

        // Properties tab
        var propertiesHref = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = PropertiesTab }.ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("Properties", FluentIcons.Settings(), propertiesHref)
        );

        // Node Types group
        var nodeTypesGroup = new NavGroupControl("Management")
            .WithIcon(FluentIcons.Document())
            .WithSkin(s => s.WithExpanded(true));

        var nodeTypesHref = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = NodeTypesTab }.ToHref(hubAddress);
        nodeTypesGroup = nodeTypesGroup.WithView(
            new NavLinkControl("Node Types", FluentIcons.Document(), nodeTypesHref)
        );

        var filesHref = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = FilesTab }.ToHref(hubAddress);
        nodeTypesGroup = nodeTypesGroup.WithView(
            new NavLinkControl("Files", FluentIcons.Folder(), filesHref)
        );

        navMenu = navMenu.WithNavGroup(nodeTypesGroup);

        // Security group
        var securityGroup = new NavGroupControl("Security")
            .WithIcon(FluentIcons.Shield())
            .WithSkin(s => s.WithExpanded(true));

        var accessControlHref = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = AccessControlTab }.ToHref(hubAddress);
        securityGroup = securityGroup.WithView(
            new NavLinkControl("Access Control", FluentIcons.Shield(), accessControlHref)
        );

        var effectiveAccessHref = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = EffectiveAccessTab }.ToHref(hubAddress);
        securityGroup = securityGroup.WithView(
            new NavLinkControl("Effective Access", FluentIcons.PersonSearch(), effectiveAccessHref)
        );

        navMenu = navMenu.WithNavGroup(securityGroup);

        // Appearance tab (always visible)
        var appearanceHref = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = AppearanceTab }.ToHref(hubAddress);
        navMenu = navMenu.WithView(
            new NavLinkControl("Appearance", FluentIcons.PaintBrush(), appearanceHref)
        );

        return navMenu;
    }

    private static UiControl BuildContentPane(
        LayoutAreaHost host,
        MeshNode? node,
        string hubPath,
        string tabId)
    {
        var stack = Controls.Stack
            .WithWidth("100%")
            .WithStyle("padding: 24px; height: 100%; overflow: auto;");

        return tabId switch
        {
            PropertiesTab => BuildPropertiesTab(host, node, stack),
            NodeTypesTab => BuildNodeTypesTab(host, hubPath, stack),
            FilesTab => BuildFilesTab(host, stack),
            AccessControlTab => BuildAccessControlTab(host, node, hubPath, stack),
            EffectiveAccessTab => BuildEffectiveAccessTab(host, hubPath, stack),
            AppearanceTab => BuildAppearanceTab(stack),
            _ => BuildPropertiesTab(host, node, stack),
        };
    }

    private static UiControl BuildPropertiesTab(LayoutAreaHost host, MeshNode? node, StackControl stack)
    {
        if (node == null)
        {
            stack = stack.WithView(Controls.Html("<p><em>Node not found.</em></p>"));
            return stack;
        }

        var nodePath = node.Namespace ?? host.Hub.Address.ToString();
        var props = MeshNodeProperties.FromNode(node);

        // Set up local data for editing
        var dataId = $"nodeProps_{nodePath.Replace("/", "_")}";
        host.UpdateData(dataId, props);

        // Auto-save: map changes back to MeshNode and persist
        SetupNodePropertiesAutoSave(host, dataId, props, node);

        // Build click-to-edit property form (same pattern as OverviewLayoutArea)
        stack = stack.WithView(EditLayoutArea.BuildContentView(host, new ContentViewOptions
        {
            DataId = dataId,
            ContentType = typeof(MeshNodeProperties),
            CanEdit = true,
            IsToggleable = true
        }));

        return stack;
    }

    private static void SetupNodePropertiesAutoSave(
        LayoutAreaHost host,
        string dataId,
        MeshNodeProperties initial,
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

                    if (updated is not MeshNodeProperties updatedProps)
                        return;

                    var updatedNode = updatedProps.ApplyTo(node);

                    host.Hub.Post(
                        new DataChangeRequest { ChangedBy = host.Stream.ClientId }.WithUpdates(updatedNode),
                        o => o.WithTarget(host.Hub.Address));
                }));
    }

    private static UiControl BuildNodeTypesTab(LayoutAreaHost host, string hubPath, StackControl stack)
    {
        // Delegate to the existing NodeTypes view content
        stack = stack.WithView(Controls.H2("Node Types").WithStyle("margin: 0 0 24px 0;"));
        stack = stack.WithView(
            (h, ctx) => MeshNodeLayoutAreas.NodeTypes(h, ctx)!,
            "NodeTypesContent"
        );
        return stack;
    }

    private static UiControl BuildFilesTab(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Files").WithStyle("margin: 0 0 24px 0;"));

        var contentService = host.Hub.ServiceProvider.GetService<IContentService>();
        var collections = contentService?.GetAllCollectionConfigs()?.ToList();

        if (collections is { Count: > 0 })
        {
            foreach (var config in collections)
            {
                stack = stack.WithView(Controls.H3(config.DisplayName ?? config.Name)
                    .WithStyle("margin: 16px 0 8px 0;"));
                stack = stack.WithView(new FileBrowserControl(config.Name));
            }
        }
        else
        {
            // Fallback: default "content" collection
            stack = stack.WithView(new FileBrowserControl("content"));
        }

        return stack;
    }

    private static UiControl BuildAccessControlTab(LayoutAreaHost host, MeshNode? node, string hubPath, StackControl stack)
    {
        // Delegate to the existing AccessControl view
        stack = stack.WithView(
            (h, ctx) => MeshNodeLayoutAreas.AccessControl(h, ctx)!,
            "AccessControlContent"
        );
        return stack;
    }

    private static UiControl BuildEffectiveAccessTab(LayoutAreaHost host, string hubPath, StackControl stack)
    {
        var securityService = host.Hub.ServiceProvider.GetService<ISecurityService>();
        if (securityService == null)
        {
            return stack.WithView(Controls.Html(
                "<p style=\"color: var(--warning-color);\">Row-Level Security is not enabled.</p>"));
        }

        stack = stack.WithView(Controls.H2("Effective Access").WithStyle("margin: 0 0 16px 0;"));
        stack = stack.WithView(Controls.Html(
            "<p style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">" +
            "Test what permissions a user has on this node. Enter a user ID and press Enter or click Check.</p>"));

        // Form data area
        var formId = $"effectiveAccess_{hubPath.Replace("/", "_")}";
        var resultId = $"effectiveAccessResult_{hubPath.Replace("/", "_")}";
        host.UpdateData(formId, new Dictionary<string, object?> { ["userId"] = "" });
        host.UpdateData(resultId, "");

        // User ID input
        var userField = new TextFieldControl(new JsonPointerReference("userId"))
        {
            Placeholder = "Enter user ID (e.g. alice@example.com)...",
            Label = "User ID",
            Immediate = true,
            DataContext = LayoutAreaReference.GetDataPointer(formId)
        };
        stack = stack.WithView(userField);

        // Check button
        stack = stack.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithStyle("margin-top: 12px; gap: 8px;")
            .WithView(Controls.Button("Check")
                .WithAppearance(Appearance.Accent)
                .WithClickAction(async ctx =>
                {
                    var userId = "";
                    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
                        .Take(1)
                        .Subscribe(data => userId = data?.GetValueOrDefault("userId")?.ToString()?.Trim() ?? "");

                    if (string.IsNullOrEmpty(userId))
                    {
                        ctx.Host.UpdateData(resultId, "<p style=\"color: var(--warning-color);\">Please enter a user ID.</p>");
                        return;
                    }

                    var perms = await securityService.GetEffectivePermissionsAsync(hubPath, userId);
                    var html = BuildPermissionResultHtml(userId, perms);
                    ctx.Host.UpdateData(resultId, html);
                })));

        // Result area — data-bound
        stack = stack.WithView((h, _) =>
        {
            return h.Stream.GetDataStream<string>(resultId)
                .Select(html => string.IsNullOrEmpty(html)
                    ? (UiControl)Controls.Stack
                    : (UiControl)Controls.Html(html));
        });

        return stack;
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

    private static UiControl BuildAppearanceTab(StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Appearance").WithStyle("margin: 0 0 24px 0;"));
        stack = stack.WithView(new AppearanceControl());
        return stack;
    }
}

/// <summary>
/// DTO record exposing editable MeshNode properties (excluding Content)
/// for the Settings Properties tab with click-to-edit support.
/// </summary>
public record MeshNodeProperties
{
    [Editable(false)]
    public string? Id { get; init; }

    public string? Name { get; init; }

    [Editable(false)]
    public string? Namespace { get; init; }

    [Editable(false)]
    public string? NodeType { get; init; }

    public string? Category { get; init; }

    public string? Icon { get; init; }

    public int? DisplayOrder { get; init; }

    [Editable(false)]
    public MeshNodeState State { get; init; }

    [Editable(false)]
    public DateTimeOffset LastModified { get; init; }

    [Editable(false)]
    public long Version { get; init; }

    public static MeshNodeProperties FromNode(MeshNode node) => new()
    {
        Id = node.Id,
        Name = node.Name,
        Namespace = node.Namespace,
        NodeType = node.NodeType,
        Category = node.Category,
        Icon = node.Icon,
        DisplayOrder = node.DisplayOrder,
        State = node.State,
        LastModified = node.LastModified,
        Version = node.Version,
    };

    public MeshNode ApplyTo(MeshNode node) => node with
    {
        Name = Name,
        Category = Category,
        Icon = Icon,
        DisplayOrder = DisplayOrder,
    };
}
