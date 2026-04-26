using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive;
using Microsoft.Extensions.DependencyInjection;

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

        // Snapshot of registered contexts at renderer-registration time. Captured so the
        // renderer always writes to EVERY registered $Menu:{context} area, even when the
        // bucket for a context comes back empty. Without this, a subscriber that asks for
        // "$Menu:Node" on a node whose built-in menu items were all permission-gated out
        // would hang forever (no control ever lands on that area).
        return config
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition))
            .AddNodeMenuItems(NodeMenuContext, DefaultNodeMenuProvider)
            .AddNodeMenuItems(MeshMenuContext, DefaultMeshMenuProvider)
            .AddLayout(layout => layout
                .WithRenderer(
                    _ => true,
                    async (host, ctx, store) =>
                    {
                        // Enumerate every provider exactly once for this render pass and bucket
                        // the yielded items by context. Prevents repeated IAsyncEnumerable
                        // evaluations that would otherwise happen once per target context.
                        // Items are inserted into a sorted list on the fly (no final OrderBy).
                        var byContext = await CollectMenuItemsByContextAsync(host, ctx);

                        // Default (unnamed) context — kept for back-compat, typically empty.
                        var defaultItems = byContext.TryGetValue("", out var d)
                            ? d.ToImmutableList()
                            : ImmutableList<NodeMenuItemDefinition>.Empty;
                        var menuControl = (IUiControl)new MenuControl(defaultItems);
                        var result = menuControl.Render(host, new RenderingContext(MenuControl.MenuArea), store);

                        // Write every registered named context so subscribers never wait on
                        // an area that would otherwise never be populated. The union of
                        // "buckets that produced items" and "contexts that were registered"
                        // covers both runtime-discovered providers and statically configured
                        // contexts whose providers happened to yield nothing on this pass.
                        var registered = host.Hub.Configuration.Get<RegisteredMenuContexts>()?.Contexts
                            ?? (IReadOnlyCollection<string>)Array.Empty<string>();
                        var contextsToRender = new HashSet<string>(byContext.Keys);
                        contextsToRender.UnionWith(registered);

                        foreach (var key in contextsToRender)
                        {
                            if (key.Length == 0) continue; // default already rendered above
                            var items = byContext.TryGetValue(key, out var bucket)
                                ? bucket.ToImmutableList()
                                : ImmutableList<NodeMenuItemDefinition>.Empty;
                            var contextMenu = (IUiControl)new MenuControl(items);
                            var contextResult = contextMenu.Render(host,
                                new RenderingContext(MenuControl.GetMenuArea(key)), result.Store);
                            result = new EntityStoreAndUpdates(contextResult.Store,
                                result.Updates.Concat(contextResult.Updates), result.ChangedBy);
                        }

                        return result;
                    }));
    }

    /// <summary>
    /// Default provider for the "Node" menu — per-node operations.
    /// Bridges the live <see cref="GetMenuContext"/> observable into the IAsyncEnumerable
    /// contract via <c>await foreach</c> + early <c>yield break</c> — first snapshot wins
    /// per render. (TODO: emit live menu updates when the menu pipeline is fully reactive.)
    /// </summary>
    private static async IAsyncEnumerable<NodeMenuItemDefinition> DefaultNodeMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        await foreach (var menuCtx in GetMenuContext(host).ToAsyncEnumerableSequence())
        {
            var (menuPath, _, _, perms) = menuCtx;

            var edit = MeshNodeLayoutAreas.GetEditMenuItem(menuPath, perms);
            if (edit != null) yield return edit;

            var files = MeshNodeLayoutAreas.GetFilesMenuItem(menuPath, perms);
            if (files != null) yield return files;

            yield return MeshNodeLayoutAreas.GetThreadsMenuItem(menuPath);

            var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
            var viewerId = accessService?.Context?.ObjectId
                           ?? accessService?.CircuitContext?.ObjectId;
            var pin = PinLayoutArea.GetMenuItem(menuPath, viewerId);
            if (pin != null) yield return pin;

            var versions = VersionLayoutArea.GetMenuItem(menuPath, perms);
            if (versions != null) yield return versions;

            var copy = CopyLayoutArea.GetMenuItem(menuPath, perms);
            if (copy != null) yield return copy;

            var move = MoveLayoutArea.GetMenuItem(menuPath, perms);
            if (move != null) yield return move;

            var recycle = RecycleLayoutArea.GetMenuItem(menuPath, perms);
            if (recycle != null) yield return recycle;

            var delete = DeleteLayoutArea.GetMenuItem(menuPath, perms);
            if (delete != null) yield return delete;
            yield break;
        }
    }

    /// <summary>
    /// Default provider for the "Mesh" menu — mesh-level operations.
    /// </summary>
    private static async IAsyncEnumerable<NodeMenuItemDefinition> DefaultMeshMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
    {
        await foreach (var menuCtx in GetMenuContext(host).ToAsyncEnumerableSequence())
        {
            var (menuPath, _, menuNode, perms) = menuCtx;

            var create = CreateLayoutArea.GetMenuItem(menuPath, menuNode, perms);
            if (create != null) yield return create;

            var import = ImportLayoutArea.GetMenuItem(menuPath, perms);
            if (import != null) yield return import;

            var export = ExportLayoutArea.GetMenuItem(menuPath, perms);
            if (export != null) yield return export;
            yield break;
        }
    }

    /// <summary>
    /// Shared node lookup: resolves the effective menu node (satellite → main), its name,
    /// and the user's permissions. Pure reactive — composes the workspace MeshNode
    /// stream with the live permission stream via <see cref="Observable.CombineLatest"/>.
    /// </summary>
    private static IObservable<(string menuPath, string nodeName, MeshNode? menuNode, Permission perms)>
        GetMenuContext(LayoutAreaHost host)
    {
        var hubPath = host.Hub.Address.ToString();
        var nodeStream = host.Workspace.GetStream<MeshNode>()
                ?.Select(n => n ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream
            .Select(nodes =>
            {
                var node = nodes.FirstOrDefault(n => n.Path == hubPath);
                // For satellite nodes (threads, comments, etc.), the menu should refer to the main node
                var menuPath = node != null && node.MainNode != node.Path ? node.MainNode : hubPath;
                var menuNode = menuPath != hubPath ? nodes.FirstOrDefault(n => n.Path == menuPath) : node;
                var nodeName = menuNode?.Name ?? node?.Name ?? "";
                return (menuPath, nodeName, menuNode);
            })
            .CombineLatest(PermissionHelper.GetEffectivePermissions(host.Hub, hubPath),
                (ctx, perms) => (ctx.menuPath, ctx.nodeName, ctx.menuNode, perms));
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

    private static IAsyncEnumerable<NodeMenuItemDefinition> YieldSingle(NodeMenuItemDefinition item)
        => Observable.Return(item).ToAsyncEnumerableSequence();

    /// <summary>
    /// Comparer for <see cref="NodeMenuItemDefinition"/> used by the per-context sorted sets.
    /// Primary key: <see cref="NodeMenuItemDefinition.Order"/> (ascending). Tiebreaker: <c>Label</c>
    /// then <c>Area</c> ordinal — both are needed so items with the same Order but different
    /// identity don't collide when the set dedupes on comparer equality.
    /// </summary>
    private static readonly IComparer<NodeMenuItemDefinition> MenuItemComparer =
        Comparer<NodeMenuItemDefinition>.Create((a, b) =>
        {
            var c = a.Order.CompareTo(b.Order);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Label, b.Label);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Area, b.Area);
        });

    /// <summary>
    /// Single-pass collector: enumerates every registered provider exactly once per render and
    /// inserts each yielded item directly into an <see cref="ImmutableSortedSet{T}.Builder"/>
    /// keyed on <see cref="NodeMenuItemDefinition.Order"/>. No post-hoc <c>OrderBy</c>, no sort
    /// at the end — the sorted set maintains order on every <c>Add</c>. Duplicates (same Order
    /// + Label + Area) collapse via the comparer.
    /// Aggregates from two sources:
    /// 1. Legacy delegate-based providers registered via <see cref="AddNodeMenuItems(MessageHubConfiguration, NodeMenuItemProvider[])"/>.
    /// 2. DI-registered <see cref="INodeMenuProvider"/> instances whose <see cref="INodeMenuProvider.Context"/>
    ///    identifies their target bucket — same pattern as <c>IAutocompleteProvider</c>.
    /// </summary>
    internal static async Task<ImmutableDictionary<string, ImmutableSortedSet<NodeMenuItemDefinition>>>
        CollectMenuItemsByContextAsync(LayoutAreaHost host, RenderingContext ctx)
    {
        var config = host.Hub.Configuration;
        var buckets = new Dictionary<string, ImmutableSortedSet<NodeMenuItemDefinition>.Builder>();

        ImmutableSortedSet<NodeMenuItemDefinition>.Builder GetBucket(string key)
            => buckets.TryGetValue(key, out var b)
                ? b
                : buckets[key] = ImmutableSortedSet.CreateBuilder(MenuItemComparer);

        async Task ConsumeAsync(string key, IAsyncEnumerable<NodeMenuItemDefinition> items)
        {
            var bucket = GetBucket(key);
            await foreach (var item in items)
                bucket.Add(item);  // sorted-set Add inserts in position, dedupes via comparer
        }

        // Legacy delegate-based providers — each context has its own provider collection.
        // We call each provider's IAsyncEnumerable exactly once per render pass.
        var seenContexts = new HashSet<string> { "" };
        var registered = config.Get<RegisteredMenuContexts>()?.Contexts;
        if (registered != null) seenContexts.UnionWith(registered);

        foreach (var ctxKey in seenContexts)
        {
            var legacyKey = ctxKey.Length == 0 ? null : ctxKey;
            var coll = config.Get<NodeMenuProviderCollection>(legacyKey);
            if (coll == null) continue;
            foreach (var provider in coll.Providers)
                await ConsumeAsync(ctxKey, provider(host, ctx));
        }

        // DI-registered providers — each invoked exactly once, items routed to their declared Context.
        foreach (var provider in host.Hub.ServiceProvider.GetServices<INodeMenuProvider>())
            await ConsumeAsync(provider.Context ?? "", provider.GetItemsAsync(host, ctx));

        var result = ImmutableDictionary.CreateBuilder<string, ImmutableSortedSet<NodeMenuItemDefinition>>();
        foreach (var kvp in buckets)
            result[kvp.Key] = kvp.Value.ToImmutable();
        return result.ToImmutable();
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
