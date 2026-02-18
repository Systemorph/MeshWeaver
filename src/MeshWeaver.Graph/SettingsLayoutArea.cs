using System.ComponentModel;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Mesh;
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
    public const string CommentsTab = "Comments";
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
            new NavLinkControl($"\u2190 {nodeName}", FluentIcons.ArrowLeft(), backHref)
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

        // Comments tab (only if enabled)
        if (host.Hub.Configuration.HasComments())
        {
            var commentsHref = new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = CommentsTab }.ToHref(hubAddress);
            securityGroup = securityGroup.WithView(
                new NavLinkControl("Comments", FluentIcons.Comment(), commentsHref)
            );
        }

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
            CommentsTab => BuildCommentsTab(host, stack),
            AppearanceTab => BuildAppearanceTab(stack),
            _ => BuildPropertiesTab(host, node, stack),
        };
    }

    private static UiControl BuildPropertiesTab(LayoutAreaHost host, MeshNode? node, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Properties").WithStyle("margin: 0 0 24px 0;"));

        if (node == null)
        {
            stack = stack.WithView(Controls.Html("<p><em>Node not found.</em></p>"));
            return stack;
        }

        // Reuse the existing node markdown (properties table)
        var markdown = BuildNodeMarkdown(node);
        stack = stack.WithView(new MarkdownControl(markdown));

        // Types catalog - show NodeType children using standard grid
        var meshQuery = host.Hub.ServiceProvider.GetService<IMeshQuery>();
        if (meshQuery != null)
        {
            var hubPath = host.Hub.Address.ToString();
            stack = stack.WithView(
                (h, _) => Observable.FromAsync(async () =>
                {
                    try
                    {
                        var nodeTypes = await meshQuery.QueryAsync<MeshNode>($"path:{hubPath} nodeType:NodeType scope:descendants").ToListAsync();
                        if (nodeTypes.Count == 0)
                            return (UiControl?)null;

                        var typesStack = Controls.Stack.WithWidth("100%");
                        typesStack = typesStack.WithView(Controls.Html("<h3 style=\"margin: 32px 0 16px 0;\">Types</h3>"));

                        var grid = Controls.LayoutGrid.WithSkin(s => s.WithSpacing(3));
                        foreach (var typeNode in nodeTypes.OrderBy(t => t.Name))
                        {
                            grid = grid.WithView(
                                MeshNodeThumbnailControl.FromNode(typeNode, typeNode.Path),
                                itemSkin => itemSkin.WithXs(12).WithSm(6).WithMd(4).WithLg(4));
                        }
                        typesStack = typesStack.WithView(grid);
                        return (UiControl?)typesStack;
                    }
                    catch
                    {
                        return null;
                    }
                }),
                "Types"
            );
        }

        return stack;
    }

    private static string BuildNodeMarkdown(MeshNode node)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("## Node Properties");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| **Id** | `{node.Id}` |");
        sb.AppendLine($"| **Name** | {node.Name ?? "*not set*"} |");
        sb.AppendLine($"| **Path** | `{node.Path}` |");
        sb.AppendLine($"| **Namespace** | `{node.Namespace ?? ""}` |");
        sb.AppendLine($"| **NodeType** | {(string.IsNullOrEmpty(node.NodeType) ? "*not set*" : $"[{node.NodeType}](/{node.NodeType})")} |");
        sb.AppendLine($"| **Icon** | {node.Icon ?? "*not set*"} |");
        sb.AppendLine($"| **DisplayOrder** | {node.DisplayOrder} |");
        sb.AppendLine($"| **State** | {node.State} |");
        sb.AppendLine($"| **LastModified** | {node.LastModified:yyyy-MM-dd HH:mm:ss} |");
        sb.AppendLine($"| **Version** | {node.Version} |");

        if (!string.IsNullOrEmpty(node.AssemblyLocation))
            sb.AppendLine($"| **AssemblyLocation** | `{node.AssemblyLocation}` |");

        return sb.ToString();
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

    private static UiControl BuildCommentsTab(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Comments").WithStyle("margin: 0 0 24px 0;"));
        stack = stack.WithView(
            (h, ctx) => CommentsView.Comments(h, ctx)!,
            "CommentsContent"
        );
        return stack;
    }

    private static UiControl BuildAppearanceTab(StackControl stack)
    {
        stack = stack.WithView(Controls.H2("Appearance").WithStyle("margin: 0 0 24px 0;"));
        stack = stack.WithView(new AppearanceControl());
        return stack;
    }
}
