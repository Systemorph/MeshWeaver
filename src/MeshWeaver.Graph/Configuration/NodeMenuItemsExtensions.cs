using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for registering node-type-specific menu item providers.
/// Providers yield items via IAsyncEnumerable during layout rendering.
/// Items are organised into named menu contexts — the header renders one dropdown per context.
/// </summary>
public static class NodeMenuItemsExtensions
{
    /// <summary>Menu context name for per-node operations (Edit, Delete, Versions, …).</summary>
    public const string NodeMenuContext = "Node";

    /// <summary>Menu context name for mesh-level operations (Create, Import, Export subtree).</summary>
    public const string MeshMenuContext = "Mesh";

    /// <summary>
    /// Registers the menu infrastructure with default menu items.
    /// Registers a predicate-based renderer that evaluates all providers and stores
    /// results at $Menu:{context} in the entity store (same pattern as $Dialog).
    /// Built-in items are registered into the "Node" and "Mesh" contexts; the Portal
    /// renders one header dropdown per context.
    /// </summary>
    public static MessageHubConfiguration AddDefaultMeshMenu(this MessageHubConfiguration config)
    {
        if (config.Get<bool>(nameof(AddDefaultMeshMenu)))
            return config;
        config = config.Set(true, nameof(AddDefaultMeshMenu));

        return config
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition))
            .AddNodeMenuItems(NodeMenuContext, DefaultNodeMenuProvider)
            .AddNodeMenuItems(MeshMenuContext, DefaultMeshMenuProvider)
            .AddLayout(layout => layout
                .WithRenderer(
                    _ => true,
                    async (host, ctx, store) =>
                    {
                        // Default (unnamed) context — kept for back-compat, typically empty.
                        var items = await host.Hub.Configuration.EvaluateMenuItemsAsync(host, ctx);
                        var menuControl = (IUiControl)new MenuControl(items);
                        var result = menuControl.Render(host, new RenderingContext(MenuControl.MenuArea), store);

                        // Named contexts ("Node", "Mesh", "SidePanel", …)
                        var contexts = host.Hub.Configuration.Get<RegisteredMenuContexts>();
                        if (contexts != null)
                        {
                            foreach (var menuContext in contexts.Contexts)
                            {
                                var contextItems = await host.Hub.Configuration.EvaluateMenuItemsAsync(host, ctx, menuContext);
                                var contextMenu = (IUiControl)new MenuControl(contextItems);
                                var contextResult = contextMenu.Render(host,
                                    new RenderingContext(MenuControl.GetMenuArea(menuContext)), result.Store);
                                result = new EntityStoreAndUpdates(contextResult.Store,
                                    result.Updates.Concat(contextResult.Updates), result.ChangedBy);
                            }
                        }

                        return result;
                    }));
    }

    /// <summary>
    /// Default provider for the "Node" menu — per-node operations.
    /// </summary>
    private static async IAsyncEnumerable<NodeMenuItemDefinition> DefaultNodeMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var (menuPath, nodeName, _, perms) = await GetMenuContextAsync(host);

        var edit = MeshNodeLayoutAreas.GetEditMenuItem(menuPath, nodeName, perms);
        if (edit != null) yield return edit;

        var files = MeshNodeLayoutAreas.GetFilesMenuItem(menuPath, perms);
        if (files != null) yield return files;

        yield return MeshNodeLayoutAreas.GetThreadsMenuItem(menuPath);

        var versions = VersionLayoutArea.GetMenuItem(menuPath, perms);
        if (versions != null) yield return versions;

        var copy = CopyLayoutArea.GetMenuItem(menuPath, perms);
        if (copy != null) yield return copy;

        var move = MoveLayoutArea.GetMenuItem(menuPath, perms);
        if (move != null) yield return move;

        var delete = DeleteLayoutArea.GetMenuItem(menuPath, nodeName, perms);
        if (delete != null) yield return delete;
    }

    /// <summary>
    /// Default provider for the "Mesh" menu — mesh-level operations.
    /// </summary>
    private static async IAsyncEnumerable<NodeMenuItemDefinition> DefaultMeshMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        var (menuPath, nodeName, menuNode, perms) = await GetMenuContextAsync(host);

        var create = CreateLayoutArea.GetMenuItem(menuPath, menuNode, perms);
        if (create != null) yield return create;

        var import = ImportLayoutArea.GetMenuItem(menuPath, perms);
        if (import != null) yield return import;

        var export = ExportLayoutArea.GetMenuItem(menuPath, nodeName, perms);
        if (export != null) yield return export;
    }

    /// <summary>
    /// Shared node lookup: resolves the effective menu node (satellite → main), its name, and the user's permissions.
    /// </summary>
    private static async Task<(string menuPath, string nodeName, MeshNode? menuNode, Permission perms)>
        GetMenuContextAsync(LayoutAreaHost host)
    {
        var hubPath = host.Hub.Address.ToString();

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
        return (menuPath, nodeName, menuNode, perms);
    }

    /// <summary>
    /// Registers additional menu item providers for the "Node" menu (per-node operations).
    /// For mesh-level operations use <see cref="AddMeshMenuItems(MessageHubConfiguration, NodeMenuItemProvider[])"/>,
    /// or specify a context explicitly via <see cref="AddNodeMenuItems(MessageHubConfiguration, string, NodeMenuItemProvider[])"/>.
    /// </summary>
    public static MessageHubConfiguration AddNodeMenuItems(
        this MessageHubConfiguration config,
        params NodeMenuItemProvider[] providers)
        => config.AddNodeMenuItems(NodeMenuContext, providers);

    /// <summary>
    /// Registers additional menu item providers for a specific named menu context (e.g., "Node", "Mesh", "SidePanel").
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
    /// Registers additional static menu items for the "Node" menu (per-node operations).
    /// Each definition is wrapped in a trivial provider that always yields it.
    /// </summary>
    public static MessageHubConfiguration AddNodeMenuItems(
        this MessageHubConfiguration config,
        params NodeMenuItemDefinition[] items)
        => config.AddNodeMenuItems(NodeMenuContext, items);

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

    /// <summary>
    /// Registers additional menu item providers for the "Mesh" menu (mesh-level operations like Create, Import, Export).
    /// </summary>
    public static MessageHubConfiguration AddMeshMenuItems(
        this MessageHubConfiguration config,
        params NodeMenuItemProvider[] providers)
        => config.AddNodeMenuItems(MeshMenuContext, providers);

    /// <summary>
    /// Registers additional static menu items for the "Mesh" menu.
    /// </summary>
    public static MessageHubConfiguration AddMeshMenuItems(
        this MessageHubConfiguration config,
        params NodeMenuItemDefinition[] items)
        => config.AddNodeMenuItems(MeshMenuContext, items);

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
