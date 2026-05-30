using System.Collections.Immutable;
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
/// Providers are <b>reactive</b> — each yields an <see cref="IObservable{T}"/> of its complete
/// item set and re-emits whenever its inputs (node content, the viewer's effective permissions)
/// change. Items are organised into named menu contexts — the header renders one dropdown per
/// context.
/// <para>
/// 🚨 Why reactive (and not <c>IAsyncEnumerable</c> + <c>await foreach</c>): the old contract
/// took the <b>first</b> permission snapshot and locked it in (<c>yield break</c>), with no
/// re-render. A runtime <c>AccessAssignment</c> (e.g. granting Editor) only reaches
/// <see cref="HubPermissionExtensions.GetEffectivePermissions(IMessageHub, string)"/> on its
/// <c>enriched</c> stream — <i>after</i> the synced query catches up — so a render that beat
/// propagation baked in Viewer-level perms forever (the menu access race behind the flaky
/// <c>Menu_Editor_ShowsCreateItems</c> test). Reactive providers re-emit when perms enrich, the
/// renderer pushes the updated <see cref="MenuControl"/> via <c>host.UpdateArea</c>, and the menu
/// self-corrects. Full reference: <c>Doc/GUI/NodeMenu.md</c>.
/// </para>
/// </summary>
public static class NodeMenuItemsExtensions
{
    /// <summary>Menu context name for per-node operations (Edit, Delete, Versions, …).</summary>
    public const string NodeMenuContext = "Node";

    /// <summary>Menu context name for mesh-level operations (Create, Import, Export subtree).</summary>
    public const string MeshMenuContext = "Mesh";

    /// <summary>Shared empty slice — providers emit this (never <c>Observable.Empty</c>) when they contribute nothing.</summary>
    private static readonly IReadOnlyCollection<NodeMenuItemDefinition> EmptyItems = [];

    /// <summary>
    /// Registers the menu infrastructure with default menu items.
    /// Registers a predicate-based renderer that subscribes to every provider's reactive stream
    /// and writes the merged, sorted result to $Menu:{context} in the entity store (same pattern as
    /// $Dialog). Built-in items are registered into the "Node" and "Mesh" contexts; the Portal
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
            .AddLayout(layout => layout.WithRenderer(_ => true, RenderMenus));
    }

    /// <summary>
    /// Predicate renderer (runs once per area render — see <c>LayoutDefinition.RenderLayoutArea</c>).
    /// For every registered context it subscribes to the merged provider stream and pushes the
    /// resulting <see cref="MenuControl"/> into $Menu:{context} via <c>host.UpdateArea</c> on every
    /// emission, so the menu re-renders live when permissions or node content change. The
    /// subscription is tied to the $Menu area's lifecycle via <c>RegisterForDisposal</c> — same
    /// shape the framework's own reactive <c>RenderArea</c> overloads use.
    /// </summary>
    private static EntityStoreAndUpdates RenderMenus(LayoutAreaHost host, RenderingContext ctx, EntityStore store)
    {
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(NodeMenuItemsExtensions));

        foreach (var (context, items) in CollectMenuItemStreamsByContext(host, ctx))
        {
            // Default (unnamed) context lands on "$Menu"; named contexts on "$Menu:{context}".
            var area = context.Length == 0 ? MenuControl.MenuArea : MenuControl.GetMenuArea(context);
            var areaContext = new RenderingContext(area);
            host.RegisterForDisposal(
                MenuControl.MenuArea,
                items
                    .DistinctUntilChanged(MenuItemsSequenceComparer.Instance)
                    .Subscribe(
                        slice => host.UpdateArea(areaContext, new MenuControl([.. slice])),
                        ex => logger?.LogWarning(ex, "Menu render failed for context '{Context}'", context)));
        }

        // Areas are populated reactively via UpdateArea; return the store unchanged.
        return new EntityStoreAndUpdates(store, [], host.Stream.StreamId);
    }

    /// <summary>
    /// Default provider for the "Node" menu — per-node operations. Composes the live node +
    /// permission streams (<see cref="GetMenuContext"/>) and re-projects the full item set on every
    /// change, so granting/revoking a role re-renders the menu without a reload.
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> DefaultNodeMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
        => GetMenuContext(host).Select(menuCtx =>
        {
            var (menuPath, _, _, perms) = menuCtx;
            var items = ImmutableList.CreateBuilder<NodeMenuItemDefinition>();

            var edit = MeshNodeLayoutAreas.GetEditMenuItem(menuPath, perms);
            if (edit != null) items.Add(edit);

            var files = MeshNodeLayoutAreas.GetFilesMenuItem(menuPath, perms);
            if (files != null) items.Add(files);

            items.Add(MeshNodeLayoutAreas.GetThreadsMenuItem(menuPath));

            var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
            var viewerId = accessService?.Context?.ObjectId
                           ?? accessService?.CircuitContext?.ObjectId;
            var pin = PinLayoutArea.GetMenuItem(menuPath, viewerId);
            if (pin != null) items.Add(pin);

            var versions = VersionLayoutArea.GetMenuItem(menuPath, perms);
            if (versions != null) items.Add(versions);

            var copy = CopyLayoutArea.GetMenuItem(menuPath, perms);
            if (copy != null) items.Add(copy);

            var move = MoveLayoutArea.GetMenuItem(menuPath, perms);
            if (move != null) items.Add(move);

            var recycle = RecycleLayoutArea.GetMenuItem(menuPath, perms);
            if (recycle != null) items.Add(recycle);

            var delete = DeleteLayoutArea.GetMenuItem(menuPath, perms);
            if (delete != null) items.Add(delete);

            return (IReadOnlyCollection<NodeMenuItemDefinition>)items.ToImmutable();
        });

    /// <summary>
    /// Default provider for the "Mesh" menu — mesh-level operations.
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> DefaultMeshMenuProvider(
        LayoutAreaHost host, RenderingContext ctx)
        => GetMenuContext(host).Select(menuCtx =>
        {
            var (menuPath, _, menuNode, perms) = menuCtx;
            var items = ImmutableList.CreateBuilder<NodeMenuItemDefinition>();

            var create = CreateLayoutArea.GetMenuItem(menuPath, menuNode, perms);
            if (create != null) items.Add(create);

            var import = ImportLayoutArea.GetMenuItem(menuPath, perms);
            if (import != null) items.Add(import);

            var export = ExportLayoutArea.GetMenuItem(menuPath, perms);
            if (export != null) items.Add(export);

            return (IReadOnlyCollection<NodeMenuItemDefinition>)items.ToImmutable();
        });

    /// <summary>
    /// Shared node lookup: resolves the effective menu node (satellite → main), its name,
    /// and the user's permissions. Reads the OWN MeshNode via the canonical
    /// <c>MeshNodeReference</c> reducer (per <c>Doc/Architecture/AsynchronousCalls.md</c> —
    /// never <c>GetStream&lt;MeshNode&gt;().FirstOrDefault</c>); if the own node is a
    /// satellite, fetches the main node via <see cref="MeshNodeStreamExtensions.GetMeshNodeStream(IWorkspace,string)"/>
    /// (which routes to the owning hub's reducer remotely). The result is a <b>live</b> stream:
    /// it re-emits on node content changes and — via the <c>CombineLatest</c> with
    /// <see cref="HubPermissionExtensions.GetEffectivePermissions(IMessageHub, string)"/> — on
    /// permission changes (the access-race fix).
    /// </summary>
    private static IObservable<(string menuPath, string nodeName, MeshNode? menuNode, Permission perms)>
        GetMenuContext(LayoutAreaHost host)
    {
        var hubPath = host.Hub.Address.ToString();

        // Hubs without a MeshDataSource registered (e.g., temporary scratch hubs,
        // hubs in startup before data context init, hubs past Started mid-dispose)
        // don't have the MeshNodeReference reducer — GetMeshNodeStream() throws
        // synchronously in those cases. Catch and emit a "no node" tuple so the
        // menu still renders (header, breadcrumb) without an effective MeshNode.
        var ownNodeStream = host.Workspace.GetMeshNodeStream()
            .Catch<MeshNode, Exception>(_ => Observable.Return<MeshNode>(null!));

        var menuContext = ownNodeStream
            .SelectMany(own =>
            {
                var isSatellite = own != null && own.MainNode != own.Path;
                var menuPath = isSatellite ? own!.MainNode : hubPath;
                if (!isSatellite || string.Equals(menuPath, hubPath, StringComparison.Ordinal))
                {
                    return Observable.Return((menuPath: menuPath, nodeName: own?.Name ?? "", menuNode: own));
                }
                // Satellite: resolve main node via the standard remote-stream path.
                return host.Workspace.GetMeshNodeStream(menuPath)
                    .Select(main => (menuPath: menuPath, nodeName: main?.Name ?? own?.Name ?? "", menuNode: (MeshNode?)main))
                    .Catch<(string menuPath, string nodeName, MeshNode? menuNode), Exception>(_ =>
                        Observable.Return((menuPath: menuPath, nodeName: own?.Name ?? "", menuNode: (MeshNode?)null)));
            });

        return menuContext
            .CombineLatest(host.Hub.GetEffectivePermissions(hubPath),
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
    /// Each definition is wrapped in a trivial provider that always emits it.
    /// </summary>
    public static MessageHubConfiguration AddNodeMenuItems(
        this MessageHubConfiguration config,
        params NodeMenuItemDefinition[] items)
        => config.AddNodeMenuItems(NodeMenuContext, items);

    /// <summary>
    /// Registers additional static menu items for a specific named menu context.
    /// Each definition is wrapped in a trivial provider that always emits it.
    /// </summary>
    public static MessageHubConfiguration AddNodeMenuItems(
        this MessageHubConfiguration config,
        string menuContext,
        params NodeMenuItemDefinition[] items)
    {
        var providers = items.Select(item =>
        {
            IReadOnlyCollection<NodeMenuItemDefinition> single = [item];
            return new NodeMenuItemProvider((_, _) => Observable.Return(single));
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
    /// Builds one merged item stream per registered menu context. Within a context, every provider's
    /// reactive stream is combined via <see cref="Observable.CombineLatest{TSource, TResult}(IEnumerable{IObservable{TSource}}, Func{IList{TSource}, TResult})"/>
    /// so the merged set re-emits whenever <i>any</i> provider re-emits (e.g. permissions enrich).
    /// Each provider stream is <c>StartWith(empty)</c>-seeded so <c>CombineLatest</c> fires
    /// immediately instead of stalling on a slow provider, and wrapped so a faulting provider
    /// degrades to empty rather than crashing the whole menu. Items are inserted into an
    /// <see cref="ImmutableSortedSet{T}"/> keyed on <see cref="MenuItemComparer"/> — sorted on every
    /// add, no post-hoc OrderBy. Aggregates two sources:
    /// 1. Legacy delegate-based providers registered via <see cref="AddNodeMenuItems(MessageHubConfiguration, NodeMenuItemProvider[])"/>.
    /// 2. DI-registered <see cref="INodeMenuProvider"/> instances whose <see cref="INodeMenuProvider.Context"/>
    ///    identifies their target bucket — same pattern as <c>IAutocompleteProvider</c>.
    /// </summary>
    internal static IReadOnlyDictionary<string, IObservable<IReadOnlyCollection<NodeMenuItemDefinition>>>
        CollectMenuItemStreamsByContext(LayoutAreaHost host, RenderingContext ctx)
    {
        var config = host.Hub.Configuration;

        // The renderer must write every registered context (even empty ones) so a subscriber on
        // "$Menu:Node" never waits on an area no provider populates.
        var contexts = new HashSet<string> { "" };
        var registered = config.Get<RegisteredMenuContexts>()?.Contexts;
        if (registered != null) contexts.UnionWith(registered);

        var diProviders = host.Hub.ServiceProvider.GetServices<INodeMenuProvider>().ToList();

        var result = new Dictionary<string, IObservable<IReadOnlyCollection<NodeMenuItemDefinition>>>(StringComparer.Ordinal);
        foreach (var context in contexts)
        {
            var providerStreams = new List<IObservable<IReadOnlyCollection<NodeMenuItemDefinition>>>();

            // Legacy delegate-based providers — each context has its own provider collection.
            var legacyKey = context.Length == 0 ? null : context;
            var coll = config.Get<NodeMenuProviderCollection>(legacyKey);
            if (coll != null)
                foreach (var provider in coll.Providers)
                    providerStreams.Add(SafeProvider(provider(host, ctx), host, context));

            // DI-registered providers — routed to their declared Context.
            foreach (var provider in diProviders)
                if ((provider.Context ?? "") == context)
                    providerStreams.Add(SafeProvider(provider.GetItems(host, ctx), host, context));

            result[context] = CombineProviderStreams(providerStreams);
        }
        return result;
    }

    /// <summary>
    /// Best-effort wrap: a single broken provider must not crash the whole menu. The most common
    /// failure path is a transient throw on disposing/uninitialised hubs. On error we log and
    /// degrade that provider's slice to empty — the rest of the menu still renders.
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> SafeProvider(
        IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> source, LayoutAreaHost host, string context)
        => source.Catch<IReadOnlyCollection<NodeMenuItemDefinition>, Exception>(ex =>
        {
            host.Hub.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(NodeMenuItemsExtensions))
                .LogWarning(ex, "Menu provider for context '{Context}' faulted; items skipped", context);
            return Observable.Return(EmptyItems);
        });

    /// <summary>
    /// Merges the provider streams for one context into a single sorted, deduped item stream.
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> CombineProviderStreams(
        IReadOnlyList<IObservable<IReadOnlyCollection<NodeMenuItemDefinition>>> providerStreams)
    {
        if (providerStreams.Count == 0)
            return Observable.Return(EmptyItems);

        return providerStreams
            .Select(s => s.StartWith(EmptyItems))
            .CombineLatest(slices =>
            {
                var builder = ImmutableSortedSet.CreateBuilder(MenuItemComparer);
                foreach (var slice in slices)
                    if (slice != null)
                        foreach (var item in slice)
                            builder.Add(item);
                return (IReadOnlyCollection<NodeMenuItemDefinition>)builder.ToImmutable();
            });
    }

    /// <summary>
    /// Sequence equality over a menu slice so <c>DistinctUntilChanged</c> suppresses redundant
    /// re-renders when an upstream stream re-emits an identical item set (e.g. the synced
    /// AccessAssignment query re-publishes the same snapshot). <see cref="NodeMenuItemDefinition"/>
    /// is a record (value equality), so <c>SequenceEqual</c> compares item-by-item.
    /// </summary>
    private sealed class MenuItemsSequenceComparer : IEqualityComparer<IReadOnlyCollection<NodeMenuItemDefinition>>
    {
        public static readonly MenuItemsSequenceComparer Instance = new();

        public bool Equals(IReadOnlyCollection<NodeMenuItemDefinition>? x, IReadOnlyCollection<NodeMenuItemDefinition>? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Count != y.Count) return false;
            return x.SequenceEqual(y);
        }

        public int GetHashCode(IReadOnlyCollection<NodeMenuItemDefinition> obj)
        {
            var hash = new HashCode();
            foreach (var item in obj) hash.Add(item);
            return hash.ToHashCode();
        }
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
