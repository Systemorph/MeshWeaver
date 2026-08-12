using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Security;
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
/// <see cref="MeshWeaver.Mesh.HubPermissionExtensions.GetEffectivePermissions(MeshWeaver.Messaging.IMessageHub, string)"/> on its
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

    /// <summary>Menu context for AI concerns (Threads / Models / Agents / Skills). Injectable like any
    /// other context — register an <see cref="INodeMenuProvider"/> with <c>Context = "AI"</c>, or call
    /// <see cref="AddNodeMenuItems(MessageHubConfiguration, string, NodeMenuItemProvider[])"/> with this
    /// context, to contribute more items (sorted by <see cref="NodeMenuItemDefinition.Order"/>).</summary>
    public const string AiMenuContext = "AI";

    /// <summary>Menu context for a Space's GITHUB actions (Sync now / Update / Re-import / PR),
    /// contributed by an <see cref="INodeMenuProvider"/> that emits only when the Space has a
    /// configured <c>_GitSync</c>. Its own dropdown, separate from the Node menu. (Instance sync
    /// lives in the Node menu as "Synchronizations", not a separate context.)</summary>
    public const string GitHubMenuContext = "GitHub";

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
            // MenuPresentation/MenuEntryPresentation are registered so the catalog node round-trips
            // through the type registry — it is authored and edited as ordinary node content.
            .WithTypes(typeof(MenuControl), typeof(NodeMenuItemDefinition),
                typeof(MenuPresentation), typeof(MenuEntryPresentation))
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

        // THE place menu labels are translated. Every provider builds its items with an English
        // Label plus a LabelKey; this host renders for ONE subscriber, whose AccessContext carries
        // their language, so resolving here gives each viewer their own menu without threading a
        // locale through ~20 static provider helpers. Applied AFTER the dedup so the comparison
        // still runs on the stable untranslated form.
        var access = host.Hub.ServiceProvider.GetService<AccessService>();

        foreach (var (context, items) in CollectMenuItemStreamsByContext(host, ctx))
        {
            // Default (unnamed) context lands on "$Menu"; named contexts on "$Menu:{context}".
            var area = context.Length == 0 ? MenuControl.MenuArea : MenuControl.GetMenuArea(context);
            var areaContext = new RenderingContext(area);
            // 🚨 ReplaceDisposable keyed on the WRITTEN area — never the appending
            // RegisterForDisposal, and never one shared key for every context (issue #606).
            //
            // RenderMenus is a GLOBAL predicate renderer (`WithRenderer(_ => true, …)`), so it runs
            // on EVERY area render — and a render re-runs on every emission of the node/permission
            // stream the providers themselves are built on. The appending overload therefore stacked
            // one MORE live GetMeshNodeStream+permissions subscription per re-render, under the
            // constant key "$Menu" which no area teardown ever reaps (DisposeExistingAreas /
            // DisposeChildAreas only remove keys that StartsWith(context.Area), and "$Menu" never
            // starts with "Overview"/"Edit"/…). Every stacked subscription stayed live for the
            // host's whole lifetime, each one holding the node stream + the permission stream (and
            // so pinning a MeshNodeStreamCache entry, which blocks the idle release of that path's
            // upstream sync stream), and each one an additional writer of the SAME $Menu area.
            //
            // Keyed PER CONTEXT (not the shared MenuArea constant): each menu context writes its
            // OWN area, so one live writer per area is exactly right — two live subscriptions
            // writing the same area were racing writers, not a feature. The shared constant would
            // make context B's registration dispose context A's. The key is deliberately NOT the
            // bare area name either: RenderArea registers the rendered UiControls under the area
            // key, and replacing that bucket would dispose the live menu control alongside the
            // stale subscription.
            // The DATA half of the menu: presentation (text / icon / order / grouping / hidden) comes
            // from an editable catalog node, so re-wording or re-grouping an entry is a node edit
            // rather than a build + image + rollout. Seeded with the standard presentation, and
            // fail-safe in both directions — no catalog, an unreadable one, or an entry naming an
            // unknown area all reduce to "render the compiled defaults". Combined (not merged) so a
            // catalog change re-renders the menu live, exactly like a permission change does.
            var catalogStream = MenuPresentationOverlay.CatalogStream(
                host.Workspace, host.Hub.JsonSerializerOptions, context, logger);

            host.ReplaceDisposable(
                $"menu-subscription:{area}",
                items
                    .DistinctUntilChanged(MenuItemsSequenceComparer.Instance)
                    .CombineLatest(catalogStream, (slice, catalog) => (slice, catalog))
                    .Select(t => (IReadOnlyList<NodeMenuItemDefinition>)
                    [
                        // 🚨 Normalize runs AFTER the overlay, never before. The overlay is itself a
                        // source of nesting — a catalog entry's `parent` moves an item under another
                        // — so a normalization that ran on the pre-overlay slice would sort and prune
                        // a tree that does not exist yet, and a grouping created purely by DATA would
                        // silently skip both. Running here means one rule for both origins: children
                        // ordered by Order at every depth, and a group left with no surviving children
                        // pruned bottom-up, whether the grouping came from a provider or the catalog.
                        .. Normalize(MenuPresentationOverlay
                                .Apply(t.slice, t.catalog, access.ViewerLocale(),
                                    reason => logger?.LogWarning(
                                        "Menu catalog for context '{Context}': {Reason}", context, reason)))
                            .Select(i => i.Localized(access))
                    ])
                    // The catalog is a SECOND emission source, so the pre-overlay dedup above no
                    // longer covers the whole pipeline: a catalog re-publish carrying identical
                    // content would push an equal-but-not-identical MenuControl and re-render every
                    // layout area (the render-storm cascade the Children-by-sequence equality was
                    // added to close). Dedup the FINAL list too — value equality, Children folded in.
                    .DistinctUntilChanged(MeshWeaver.Mesh.MenuItemsSequenceComparer.Instance)
                    .Subscribe(
                        list => host.UpdateArea(areaContext, new MenuControl(list)),
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
            var (menuPath, _, menuNode, perms) = menuCtx;
            var items = ImmutableList.CreateBuilder<NodeMenuItemDefinition>();

            // The node menu is grouped into sections (rendered with "_separator" dividers), each
            // item carrying an emoji so it reads at a glance. The Order encodes the grouping — the
            // aggregator re-sorts every provider's items by Order, so InstanceSync's
            // "Synchronizations" item (Order 36) slots into the middle section on its own.
            //   10-18  edit / organize            ✏️ 🔖 ➡️ 📋 🗑️
            //   30-38  content / history / sync    📁 🕘 🔌 (🔄)
            //   50     lifecycle                   ♻️

            // A USER partition root (the user's home) is NOT a normal editable node: Edit / Move /
            // Copy / Delete on the root would rewrite, relocate, duplicate or WIPE the entire
            // partition (the node-menu-delete-wiped-my-partition incident). Suppress those four on
            // a protected root — the viewer-scoped Pin stays. Delete is additionally hard-blocked
            // server-side by PartitionRootDeletionGuard; this keeps it off the menu in the first place.
            var isProtectedRoot = PartitionRootDeletionGuard.IsUserPartitionRoot(menuNode);

            // ── Group 1: edit / organize ──
            if (!isProtectedRoot)
            {
                var edit = MeshNodeLayoutAreas.GetEditMenuItem(menuPath, perms);
                if (edit != null) items.Add(edit with { Order = 10, Icon = "✏️" });
            }

            var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
            var viewerId = accessService?.Context?.ObjectId
                           ?? accessService?.CircuitContext?.ObjectId;
            var pin = PinLayoutArea.GetMenuItem(menuPath, viewerId);
            if (pin != null) items.Add(pin with { Order = 12, Icon = "🔖" });

            if (!isProtectedRoot)
            {
                var move = MoveLayoutArea.GetMenuItem(menuPath, perms);
                if (move != null) items.Add(move with { Order = 14, Icon = "➡️" });

                var copy = CopyLayoutArea.GetMenuItem(menuPath, perms);
                if (copy != null) items.Add(copy with { Order = 16, Icon = "📋" });

                var delete = DeleteLayoutArea.GetMenuItem(menuPath, perms);
                if (delete != null) items.Add(delete with { Order = 18, Icon = "🗑️" });
            }
            var hasGroup1 = items.Count > 0;

            // ── Group 2: content / history / sync ── (InstanceSync adds "Synchronizations" at 36)
            var beforeGroup2 = items.Count;
            var files = MeshNodeLayoutAreas.GetFilesMenuItem(menuPath, perms);
            if (files != null) items.Add(files with { Order = 30, Icon = "📁" });

            // Read-gated: the underlying record stays reachable for viewers even when the type's
            // Overview is a designed page and Edit is hidden behind Update permission.
            var data = MeshNodeLayoutAreas.GetDataMenuItem(menuPath, perms);
            if (data != null) items.Add(data with { Order = 31, Icon = "🧾" });

            var versions = VersionLayoutArea.GetMenuItem(menuPath, perms);
            if (versions != null) items.Add(versions with { Order = 32, Icon = "🕘" });

            var stopSync = StopSyncLayoutArea.GetMenuItem(menuNode, menuPath, perms);
            if (stopSync != null) items.Add(stopSync with { Order = 34, Icon = "🔌" });
            var hasGroup2 = items.Count > beforeGroup2;

            // ── Group 3: lifecycle ──
            var recycle = RecycleLayoutArea.GetMenuItem(menuPath, perms);
            if (recycle != null) items.Add(recycle with { Order = 50, Icon = "♻️" });
            var hasGroup3 = recycle != null;

            // Threads moved to the dedicated top-bar AI menu (AiMenuContext).

            // Section dividers at the seams — only where both adjacent sections carry items.
            if (hasGroup1 && (hasGroup2 || hasGroup3))
                items.Add(new NodeMenuItemDefinition("", "_separator", Order: 20));
            if ((hasGroup1 || hasGroup2) && hasGroup3)
                items.Add(new NodeMenuItemDefinition("", "_separator", Order: 40));

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
    /// Shared node lookup: resolves the node being viewed, its name, and the user's permissions.
    /// Reads the OWN MeshNode via the canonical <c>MeshNodeReference</c> reducer (per
    /// <c>Doc/Architecture/AsynchronousCalls.md</c> — never <c>GetStream&lt;MeshNode&gt;().FirstOrDefault</c>).
    /// The result is a <b>live</b> stream: it re-emits on node content changes and — via the
    /// <c>CombineLatest</c> with
    /// <see cref="MeshWeaver.Mesh.HubPermissionExtensions.GetEffectivePermissions(MeshWeaver.Messaging.IMessageHub, string)"/> — on
    /// permission changes (the access-race fix).
    ///
    /// <para>🚨 The menu targets the node <b>being viewed</b> (<paramref name="host"/>'s own hub
    /// path), NEVER a satellite's owner. The old code retargeted a satellite's menu to
    /// <c>own.MainNode</c>, so opening the node menu on a thread anchored under a user's home
    /// (<c>MainNode = {user}</c>) and hitting Delete posted
    /// <c>DeleteNodeRequest({user}, Recursive)</c> — deleting the entire partition root, not the
    /// thread (the catastrophic node-menu delete). Every per-node op (Edit/Move/Copy/Delete) now
    /// operates on <c>hubPath</c>: "delete this thread" deletes the thread. A hard guard
    /// (<see cref="MeshWeaver.Graph.Security.PartitionRootDeletionGuard"/>) additionally blocks
    /// deleting a user partition root even if some other caller targets it.</para>
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
            .Select(own => (menuPath: hubPath, nodeName: own?.Name ?? "", menuNode: (MeshNode?)own));

        // 🔒 The node/mesh menu is an AUTHORING surface — it must fail CLOSED when logged out.
        // A logged-out (anonymous / virtual) visitor gets NO menu items, even on a public-read
        // node: they may still READ the content (a separate path), but every menu item is gated on
        // the viewer's effective Permission, and anonymous would otherwise pick up the public-read
        // grant (Read) and surface read-based items (Files, Versions). Force Permission.None for an
        // unauthenticated viewer so the whole menu drops — each item's null-guard already reacts to
        // it. Same anonymous/virtual test as PermissionEvaluator.ResolveUserId.
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewerContext = accessService?.Context ?? accessService?.CircuitContext;
        var isAuthenticated = !string.IsNullOrEmpty(viewerContext?.ObjectId)
                              && viewerContext?.IsVirtual != true;

        var permsStream = isAuthenticated
            ? host.Hub.GetEffectivePermissions(hubPath)
            : Observable.Return(Permission.None);

        return menuContext
            .CombineLatest(permsStream,
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

        // A DI-registered INodeMenuProvider self-registers its Context — so declaring a provider
        // with Context = "Sync" / "GitHub" makes that dropdown render without a separate
        // AddNodeMenuItems seed. The renderer writes the context's slot even when the provider
        // currently emits nothing (integration not configured), so the client can subscribe it.
        foreach (var provider in diProviders)
            if (provider.Context is { Length: > 0 } c)
                contexts.Add(c);

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
    /// Recursively sorts every nested <see cref="NodeMenuItemDefinition.Children"/> list by the same
    /// <see cref="MenuItemComparer"/> the top level uses, and drops grouping parents that have no
    /// surviving children.
    ///
    /// <para><b>Why the server and not each renderer.</b> The top-level merge sorts through an
    /// <see cref="ImmutableSortedSet{T}"/>, but that comparer never looks at <c>Children</c> — so before
    /// this, a submenu came out in whatever order it happened to be appended, and each of the four
    /// renderers would have had to re-sort it identically. Doing it once here means <c>Order</c> means the
    /// same thing at every depth, on every client.</para>
    ///
    /// <para><b>Why AFTER the overlay.</b> Nesting has TWO origins: a provider emitting
    /// <c>Children</c>, and a <c>MenuPresentation</c> catalog entry whose <c>Parent</c> moves an item
    /// under another (<see cref="MenuPresentationOverlay"/>). Normalizing before the overlay would sort
    /// and prune a tree that does not exist yet, and a grouping created purely by DATA would skip both
    /// rules. Running last gives one rule for both origins.</para>
    ///
    /// <para><b>Why empty groups are dropped.</b> Menu items are permission-filtered by the PROVIDER, not
    /// by this renderer (see <see cref="INodeMenuProvider.GetItems"/>) — a provider that gates each child
    /// individually can legitimately end up emitting a parent whose children all vanished for this viewer.
    /// Rendering that would give a submenu that opens onto nothing. Only a pure
    /// <see cref="NodeMenuItemDefinition.IsGroup"/> parent is pruned; a parent with a real navigable
    /// <c>Area</c> of its own survives, because it still has somewhere to go — which is also what keeps
    /// the overlay's documented "a dangling <c>Parent</c> leaves the entry top-level" behaviour intact.
    /// Pruning runs bottom-up, so a group whose only child was itself an emptied group disappears too.</para>
    /// </summary>
    // internal for unit testing (InternalsVisibleTo MeshWeaver.Graph.Test) — the composition with
    // MenuPresentationOverlay.Apply is the part worth pinning, and both halves are internal.
    internal static IReadOnlyCollection<NodeMenuItemDefinition> Normalize(
        IReadOnlyCollection<NodeMenuItemDefinition> items)
    {
        var result = ImmutableList.CreateBuilder<NodeMenuItemDefinition>();
        foreach (var item in items)
        {
            if (!item.HasChildren)
            {
                // A group that never had children (or lost them upstream) has nothing to open.
                if (item.IsGroup)
                    continue;
                result.Add(item);
                continue;
            }

            // OrderBy, not a sorted set: children are a LIST, and two children that compare equal are a
            // provider's business to emit, not ours to silently collapse.
            var children = Normalize([.. item.Children!.OrderBy(c => c, MenuItemComparer)]);
            if (children.Count == 0 && item.IsGroup)
                continue;
            result.Add(item with { Children = [.. children] });
        }
        return result.ToImmutable();
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
