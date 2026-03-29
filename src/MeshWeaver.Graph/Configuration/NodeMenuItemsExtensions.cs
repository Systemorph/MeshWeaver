using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for registering node-type-specific menu item providers.
/// Providers yield items via IAsyncEnumerable during layout rendering.
/// </summary>
public static class NodeMenuItemsExtensions
{
    /// <summary>
    /// Registers the menu infrastructure with default menu items.
    /// Registers a predicate-based renderer that evaluates all providers and stores
    /// results at $Menu in the entity store (same pattern as $Dialog).
    /// Also renders any named-context menus (e.g., "SidePanel") at $Menu:{context}.
    /// </summary>
    public static MessageHubConfiguration AddDefaultMeshMenu(this MessageHubConfiguration config)
    {
        if (config.Get<bool>(nameof(AddDefaultMeshMenu)))
            return config;
        config = config.Set(true, nameof(AddDefaultMeshMenu));

        return config
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition))
            .AddNodeMenuItems(DefaultMenuProvider)
            .AddLayout(layout => layout
                .WithRenderer(
                    _ => true,
                    async (host, ctx, store) =>
                    {
                        // Main menu (default context)
                        var items = await host.Hub.Configuration.EvaluateMenuItemsAsync(host, ctx);
                        var menuControl = (IUiControl)new MenuControl(items);
                        var result = menuControl.Render(host, new RenderingContext(MenuControl.MenuArea), store);

                        // Named contexts (e.g., "SidePanel")
                        var contexts = host.Hub.Configuration.Get<RegisteredMenuContexts>();
                        if (contexts != null)
                        {
                            foreach (var menuContext in contexts.Contexts)
                            {
                                var contextItems = await host.Hub.Configuration.EvaluateMenuItemsAsync(host, ctx, menuContext);
                                if (contextItems.Count > 0)
                                {
                                    var contextMenu = (IUiControl)new MenuControl(contextItems);
                                    var contextResult = contextMenu.Render(host,
                                        new RenderingContext(MenuControl.GetMenuArea(menuContext)), result.Store);
                                    result = new EntityStoreAndUpdates(contextResult.Store,
                                        result.Updates.Concat(contextResult.Updates), result.ChangedBy);
                                }
                            }
                        }

                        return result;
                    }));
    }

    /// <summary>
    /// Default menu provider that yields standard menu items with inline permission checks.
    /// Delegates to individual layout area classes for menu item definitions.
    /// </summary>
    private static async IAsyncEnumerable<NodeMenuItemDefinition> DefaultMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var hubPath = host.Hub.Address.ToString();

        // Get the current node to determine name and type
        var nodes = await (host.Workspace.GetStream<MeshNode>()
                ?.Select(n => n ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>()))
            .FirstAsync();
        var node = nodes.FirstOrDefault(n => n.Path == hubPath);

        // For satellite nodes (threads, comments, etc.), the menu should refer to the main node
        var menuPath = node != null && node.MainNode != node.Path ? node.MainNode : hubPath;
        var menuNode = menuPath != hubPath ? nodes.FirstOrDefault(n => n.Path == menuPath) : node;
        var nodeName = menuNode?.Name ?? node?.Name ?? "";

        var perms = await PermissionHelper.GetEffectivePermissionsAsync(host.Hub, menuPath);

        // Each area class provides its own GetMenuItem — factored for reuse
        var edit = MeshNodeLayoutAreas.GetEditMenuItem(menuPath, nodeName, perms);
        if (edit != null) yield return edit;

        var files = MeshNodeLayoutAreas.GetFilesMenuItem(menuPath, perms);
        if (files != null) yield return files;

        yield return MeshNodeLayoutAreas.GetThreadsMenuItem(menuPath);

        var versions = VersionLayoutArea.GetMenuItem(menuPath, perms);
        if (versions != null) yield return versions;

        var settings = MeshNodeLayoutAreas.GetSettingsMenuItem(menuPath, perms);
        if (settings != null) yield return settings;

        // Group Create, Copy, Move, Import, Export, Delete under "Actions" sub-menu
        var actionsChildren = new List<NodeMenuItemDefinition>();

        var create = CreateLayoutArea.GetMenuItem(menuPath, menuNode, perms);
        if (create != null) actionsChildren.Add(create);

        var copy = CopyLayoutArea.GetMenuItem(menuPath, perms);
        if (copy != null) actionsChildren.Add(copy);

        var move = MoveLayoutArea.GetMenuItem(menuPath, perms);
        if (move != null) actionsChildren.Add(move);

        var import = ImportLayoutArea.GetMenuItem(menuPath, perms);
        if (import != null) actionsChildren.Add(import);

        var export = ExportLayoutArea.GetMenuItem(menuPath, nodeName, perms);
        if (export != null) actionsChildren.Add(export);

        var delete = DeleteLayoutArea.GetMenuItem(menuPath, nodeName, perms);
        if (delete != null) actionsChildren.Add(delete);

        if (actionsChildren.Count > 0)
            yield return new NodeMenuItemDefinition("Actions", "Actions", Order: 95, Children: actionsChildren);
    }

    /// <summary>
    /// Registers additional menu item providers for the default (main) menu context.
    /// </summary>
    public static MessageHubConfiguration AddNodeMenuItems(
        this MessageHubConfiguration config,
        params NodeMenuItemProvider[] providers)
    {
        var existing = config.Get<NodeMenuProviderCollection>() ?? new NodeMenuProviderCollection([]);
        var updated = existing.AddRange(providers);
        return config.Set(updated);
    }

    /// <summary>
    /// Registers additional menu item providers for a specific named menu context (e.g., "SidePanel").
    /// </summary>
    public static MessageHubConfiguration AddNodeMenuItems(
        this MessageHubConfiguration config,
        string menuContext,
        params NodeMenuItemProvider[] providers)
    {
        var existing = config.Get<NodeMenuProviderCollection>(menuContext)
            ?? new NodeMenuProviderCollection([]);
        var updated = existing.AddRange(providers);
        config = config.Set(updated, menuContext);

        // Track the context name so the renderer knows to evaluate it
        var contexts = config.Get<RegisteredMenuContexts>() ?? new(new HashSet<string>());
        config = config.Set(contexts.Add(menuContext));

        return config;
    }

    /// <summary>
    /// Registers additional static menu items for the default (main) menu context.
    /// Each definition is wrapped in a trivial provider that always yields it.
    /// </summary>
    public static MessageHubConfiguration AddNodeMenuItems(
        this MessageHubConfiguration config,
        params NodeMenuItemDefinition[] items)
    {
        var providers = items.Select(item =>
        {
            var captured = item;
            return new NodeMenuItemProvider((_, _) => YieldSingle(captured));
        }).ToArray();
        return config.AddNodeMenuItems(providers);
    }

    /// <summary>
    /// Registers additional static menu items for a specific named menu context.
    /// Each definition is wrapped in a trivial provider that always yields it.
    /// </summary>
    public static MessageHubConfiguration AddNodeMenuItems(
        this MessageHubConfiguration config,
        string menuContext,
        params NodeMenuItemDefinition[] items)
    {
        var providers = items.Select(item =>
        {
            var captured = item;
            return new NodeMenuItemProvider((_, _) => YieldSingle(captured));
        }).ToArray();
        return config.AddNodeMenuItems(menuContext, providers);
    }

    private static async IAsyncEnumerable<NodeMenuItemDefinition> YieldSingle(NodeMenuItemDefinition item)
    {
        await Task.CompletedTask;
        yield return item;
    }

    /// <summary>
    /// Evaluates all registered providers for the default (main) menu context.
    /// </summary>
    internal static async Task<IReadOnlyList<NodeMenuItemDefinition>> EvaluateMenuItemsAsync(
        this MessageHubConfiguration config, LayoutAreaHost host, RenderingContext ctx)
        => await config.EvaluateMenuItemsAsync(host, ctx, null);

    /// <summary>
    /// Evaluates all registered providers for a specific menu context, collects items, and sorts by Order.
    /// </summary>
    internal static async Task<IReadOnlyList<NodeMenuItemDefinition>> EvaluateMenuItemsAsync(
        this MessageHubConfiguration config, LayoutAreaHost host, RenderingContext ctx, string? menuContext)
    {
        var collection = config.Get<NodeMenuProviderCollection>(menuContext);
        if (collection == null)
            return [];

        var items = new List<NodeMenuItemDefinition>();
        foreach (var provider in collection.Providers)
        {
            await foreach (var item in provider(host, ctx))
            {
                items.Add(item);
            }
        }

        items.Sort((a, b) => a.Order.CompareTo(b.Order));
        return items;
    }
}

/// <summary>
/// Internal holder for accumulated menu item providers, stored via config.Set.
/// </summary>
internal record NodeMenuProviderCollection(IReadOnlyList<NodeMenuItemProvider> Providers)
{
    public NodeMenuProviderCollection AddRange(IEnumerable<NodeMenuItemProvider> newProviders)
        => new(Providers.Concat(newProviders).ToList());
}

/// <summary>
/// Tracks registered named menu contexts so the renderer knows which to evaluate.
/// </summary>
internal record RegisteredMenuContexts(HashSet<string> Contexts)
{
    public RegisteredMenuContexts Add(string context)
        => new(new HashSet<string>(Contexts) { context });
}
