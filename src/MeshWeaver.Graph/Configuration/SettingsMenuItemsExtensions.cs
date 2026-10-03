using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Extension methods for registering settings menu item providers.
/// Follows the same decentralized pattern as <see cref="NodeMenuItemsExtensions"/>.
/// </summary>
public static class SettingsMenuItemsExtensions
{
    /// <summary>
    /// Registers settings menu item providers. Providers are accumulated
    /// in SettingsMenuProviderCollection stored via config.Set().
    /// </summary>
    public static MessageHubConfiguration AddSettingsMenuItems(
        this MessageHubConfiguration config,
        params SettingsMenuItemProvider[] providers)
    {
        var existing = config.Get<SettingsMenuProviderCollection>()
            ?? new SettingsMenuProviderCollection([]);
        var updated = existing.AddRange(providers);
        return config.Set(updated);
    }

    /// <summary>
    /// Registers static settings menu items. Each definition is wrapped
    /// in a trivial provider that always yields it.
    /// </summary>
    public static MessageHubConfiguration AddSettingsMenuItems(
        this MessageHubConfiguration config,
        params SettingsMenuItemDefinition[] items)
    {
        var providers = items.Select(item =>
        {
            var captured = item;
            return new SettingsMenuItemProvider((_, _) =>
                Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>(new[] { captured }));
        }).ToArray();
        return config.AddSettingsMenuItems(providers);
    }

    /// <summary>
    /// Hides the named tabs on this hub's settings page, whoever registered them. For a node type
    /// whose settings page is an APP rather than node management (the Admin app hides the default
    /// Metadata / Files / … tabs): the defaults are registered on every node hub, so the only
    /// order-independent way to leave them out is to filter them where the page collects its tabs.
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="tabIds">The tab ids to hide.</param>
    public static MessageHubConfiguration HideSettingsTabs(
        this MessageHubConfiguration config, params string[] tabIds)
    {
        var existing = config.Get<HiddenSettingsTabs>()
            ?? new HiddenSettingsTabs(System.Collections.Immutable.ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase));
        return config.Set(existing with { Ids = existing.Ids.Union(tabIds) });
    }

    /// <summary>
    /// Restricts the named tabs to a PARTITION ROOT — a hub whose path is a single segment (a Space,
    /// a person's partition, the Admin app). A tab that acts on a whole Space (its node types, its
    /// groups, its GitHub sync, its working tree) means nothing on a descendant's settings page, where
    /// it used to be offered on every node below the root. The tab is filtered out wherever the page
    /// is not a root, whoever registered it, and a link to it on a descendant redirects to the same
    /// tab on the root (<see cref="SettingsRedirect"/>).
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="tabIds">The tab ids that belong to the partition root only.</param>
    public static MessageHubConfiguration RestrictSettingsTabsToPartitionRoot(
        this MessageHubConfiguration config, params string[] tabIds)
    {
        var existing = config.Get<PartitionRootSettingsTabs>() ?? PartitionRootSettingsTabs.Empty;
        return config.Set(existing with { Ids = existing.Ids.Union(tabIds) });
    }

    /// <summary>
    /// On THIS hub's settings page, a request for <paramref name="fromTabId"/> opens
    /// <paramref name="toTabId"/> instead (a redirect, so the address bar shows the tab that is
    /// rendered). For tabs merged into one: the retired id keeps answering.
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="fromTabId">The retired tab id.</param>
    /// <param name="toTabId">The tab that now carries its content.</param>
    public static MessageHubConfiguration AliasSettingsTab(
        this MessageHubConfiguration config, string fromTabId, string toTabId)
    {
        var existing = config.Get<SettingsTabAliases>() ?? SettingsTabAliases.Empty;
        return config.Set(existing with { Map = existing.Map.SetItem(fromTabId, toTabId) });
    }

    /// <summary>
    /// The title of this hub's settings page — the name of the APP the page is, shown at the top of
    /// its navigation. Without one the page is titled with the node's own name. The Admin app is
    /// titled with the instance's name, the person app with the person's name: every settings page
    /// says whose things it changes.
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="title">The title, given the page's host and node; <c>null</c> falls back to the
    /// node's name.</param>
    public static MessageHubConfiguration WithSettingsTitle(
        this MessageHubConfiguration config, Func<LayoutAreaHost, MeshNode?, string?> title)
        => config.Set(new SettingsTitle(title));

    /// <summary>True when <paramref name="hubPath"/> is a partition root (a single path segment).</summary>
    /// <param name="hubPath">The hub path.</param>
    internal static bool IsPartitionRoot(string? hubPath)
        => !string.IsNullOrEmpty(hubPath) && !hubPath.Contains('/');

    /// <summary>
    /// The live, UNFILTERED settings-tab set: every registered provider subscribed once
    /// (subscribe-all-upfront via <c>CombineLatest</c>), merged and sorted by <c>Order</c>,
    /// re-emitting whenever any provider's live check (e.g. global-admin, a GitHub probe)
    /// resolves.
    ///
    /// <para>🚨 <b>The permission filter is deliberately NOT applied here</b>, and that separation
    /// is the whole point. This stream is long-lived and re-emits for reasons that have nothing to
    /// do with the viewer; the viewer's effective permissions are a SECOND long-lived stream that
    /// enriches on its own schedule (<c>PermissionEvaluator</c> emits a low static seed, then the
    /// synced-assignment answer). Baking a permission SNAPSHOT into this stream produces one live
    /// provider chain per permission value, each frozen on the value it was built with — so a late
    /// provider emission re-renders the menu through a STALE, lower permission set and silently
    /// removes every tab the viewer is entitled to. See
    /// <see cref="FilterByPermission"/> and the composition in <c>SettingsLayoutArea.Settings</c>,
    /// which combines the two streams so the LATEST permissions always win (#1962; the node menu
    /// has always composed it that way — <c>NodeMenuItemsExtensions.GetMenuContext</c>).</para>
    /// </summary>
    internal static IObservable<IReadOnlyList<SettingsMenuItemDefinition>>
        ObserveSettingsMenuItems(
            this MessageHubConfiguration config,
            LayoutAreaHost host,
            RenderingContext ctx)
    {
        var collection = config.Get<SettingsMenuProviderCollection>();
        var providerDelegates = collection?.Providers ?? (IReadOnlyList<SettingsMenuItemProvider>)[];

        var streams = providerDelegates.Select(provider =>
            // Skip failing providers so one broken tab can't crash all settings.
            provider(host, ctx).Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(
                _ => Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([])))
            .ToList();

        // Data-contributed tabs (UiContribution nodes with Context = NodeSettings, design #1645):
        // the per-node twin of GlobalSettingsMenuItemsExtensions.ContributedSettingsTabs. This lane
        // is added UNCONDITIONALLY — a mesh with zero compiled providers still renders its
        // contributed tabs, which is the point of the fallback the compiled defaults shrink to.
        streams.Add(ContributedSettingsTabs(host)
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(
                _ => Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([])));

        // Data-contributed PERSON-APP tabs (UiContribution nodes with Context = PersonApp): the
        // lane an in-app extension appears on inside the viewer's own settings app
        // (Doc/Architecture/InAppExtensions). Only on the person-app hub — never on a Space's, a
        // node's or another person's settings page. Seeded empty so a slow access probe never
        // holds the built-in tabs back.
        // The lane is added LAST, and the fold below never lets one of its tabs shadow a tab the
        // compiled providers or the NodeSettings lane already registered under the same id — a
        // contribution must not swap a surface like Sharing in under a familiar label.
        var personAppLane = -1;
        if (host.IsPersonAppHub())
        {
            personAppLane = streams.Count;
            streams.Add(ContributedPersonAppTabs(host)
                .StartWith([])
                .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(
                    _ => Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([])));
        }

        var hidden = config.Get<HiddenSettingsTabs>()?.Ids;
        // Space-root-only tabs leave every page that is not a partition root.
        var rootOnly = IsPartitionRoot(host.Hub.Address.ToString())
            ? null
            : config.Get<PartitionRootSettingsTabs>()?.Ids;
        return Observable.CombineLatest(streams)
            .Select(lists =>
            {
                var items = new List<SettingsMenuItemDefinition>();
                for (var lane = 0; lane < lists.Count; lane++)
                {
                    var list = lists[lane];
                    if (list is null)
                        continue;
                    var visible = list.Where(i =>
                        !(hidden?.Contains(i.Id) ?? false) && !(rootOnly?.Contains(i.Id) ?? false)).ToList();
                    items.AddRange(lane == personAppLane ? WithoutShadowingTabs(items, visible) : visible);
                }
                items.Sort((a, b) => a.Order.CompareTo(b.Order));
                return (IReadOnlyList<SettingsMenuItemDefinition>)items;
            });
    }

    /// <summary>
    /// The DATA-contributed PER-NODE settings tabs: every <see cref="UiContribution"/> in the
    /// shared mesh-scoped catalog declaring <see cref="UiContribution.NodeSettingsContext"/>,
    /// projected through the closed gate vocabulary in compiled code (#3055). The twin of
    /// <c>GlobalSettingsMenuItemsExtensions.ContributedSettingsTabs</c>; the differences are the
    /// ones the surface forces:
    ///
    /// <para>🚨 <b>Permission is NOT applied here.</b> The projection stamps
    /// <see cref="SettingsMenuItemDefinition.RequiredPermission"/> (floored at
    /// <see cref="Permission.Read"/>) and <see cref="FilterByPermission"/> applies it at the render
    /// fold against the LATEST permission value — see <see cref="ObserveSettingsMenuItems"/> for
    /// why a permission snapshot must never be baked into this stream (#1962).</para>
    ///
    /// <para>Fails CLOSED for an anonymous or virtual viewer, the same way the node menu forces
    /// <see cref="Permission.None"/> for one: a settings page is an authoring surface, and a
    /// public-read node must not hand a logged-out visitor its contributed tabs.</para>
    ///
    /// <para>The node-shape gates need the anchoring node, so the projection is combined with the
    /// live own-node stream — which also makes a contributed tab appear/disappear the moment the
    /// node's shape changes (a claim flipping <c>SyncBehavior</c>, say), with no reload.</para>
    /// </summary>
    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>>
        ContributedSettingsTabs(LayoutAreaHost host)
    {
        var catalog = host.Hub.ServiceProvider.GetService<UiContributionCatalog>();
        if (catalog is null)
            return Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([]);

        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewer = accessService?.Context ?? accessService?.CircuitContext;
        // The pattern binds the viewer's id for the admin verdict below, so "no authenticated
        // viewer" and "no id to evaluate" are one test — never a null-forgiven dereference.
        if (viewer is not { ObjectId: { Length: > 0 } viewerObjectId, IsVirtual: false })
            return Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([]);

        var viewerId = accessService.ViewerId();
        var menuPath = host.Hub.Address.ToString();
        // 🚨 The admin verdict is bound to the viewer resolved HERE, on the render turn — never
        // inside the Defer below. The parameterless IsGlobalAdmin() reads the ambient
        // AccessService context at the moment it is called; inside the Defer that is SUBSCRIBE
        // time, which on a distributed mesh runs off the viewer's delivery with no context, so the
        // viewer read as anonymous and AdminOnly never passed.
        // AdminAppTest.SeededAdminTabs_SurviveASubscriptionOffTheViewersDelivery.
        // 🚨 And it is the ANSWERED verdict, not one seeded false: this lane is combined into a
        // page that already waits for the viewer's permissions, so a seed painted "not an admin"
        // into the first frame while the compiled Admin-app tabs (their own verdicts) had already
        // answered — every seeded Admin-app tab (Invitations, Privacy, Published, Updates, Control
        // lane, Inbox) missing from the frame an MCP read or a first paint takes.
        // AdminAppFirstFrameTest.
        var adminVerdict = AdminAppNodeType.AnsweredAdminVerdict(host.Hub, viewerObjectId);

        // Deferred so a hub without a MeshDataSource — where GetMeshNodeStream() throws
        // SYNCHRONOUSLY — surfaces as OnError into the caller's Catch rather than as a throw out
        // of the aggregator, taking every other settings tab with it.
        return Observable.Defer(() =>
        {
            var ownNode = host.Workspace.GetMeshNodeStream()
                .Catch<MeshNode, Exception>(_ => Observable.Return<MeshNode>(null!));
            return catalog.Contributions
                .CombineLatest(ownNode, adminVerdict,
                    (contributions, node, isAdmin) => UiContributionProjection
                        .ProjectNodeSettingsTabs(contributions, menuPath, node, isAdmin, viewerId));
        });
    }

    /// <summary>
    /// The DATA-contributed PERSON-APP tabs: every <see cref="UiContribution"/> in the shared
    /// catalog declaring <see cref="UiContribution.PersonAppContext"/>, projected through the closed
    /// gate vocabulary (<see cref="UiContributionProjection.ProjectPersonAppTabs"/>). The caller adds
    /// this lane on the viewer's own user root only. Fails closed for an anonymous or virtual viewer.
    ///
    /// <para><see cref="UiContributionGates.RequireAddressAccess"/> is applied HERE, live: for each
    /// tab that demands it, the viewer's Read verdict on the embedded address is read
    /// (<c>CheckPermissionOutcome</c>), folded by <see cref="ApplyAddressAccess"/> — seeded false so a
    /// pending verdict hides the tab rather than stalling the page — and the tab passes only on a
    /// GRANTED verdict. An undetermined verdict hides it too, and is logged as a degraded dependency
    /// rather than read as "not held". That is what makes an in-app extension's tab appear the
    /// moment the viewer acquires it and disappear when the grant goes — the same stream, no reload.</para>
    /// </summary>
    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>>
        ContributedPersonAppTabs(LayoutAreaHost host)
    {
        var catalog = host.Hub.ServiceProvider.GetService<UiContributionCatalog>();
        if (catalog is null)
            return Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([]);

        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var viewer = accessService?.Context ?? accessService?.CircuitContext;
        if (viewer is not { ObjectId: { Length: > 0 } viewerObjectId, IsVirtual: false })
            return Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([]);

        var viewerId = accessService.ViewerId();
        var userPath = host.Hub.Address.ToString();
        // Bound on the render turn, never inside the Defer (see ContributedSettingsTabs).
        var adminVerdict = AdminAppNodeType.LiveAdminVerdict(host.Hub, viewerObjectId);
        var logger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Configuration.PersonAppTabs");

        return Observable.Defer(() =>
        {
            var ownNode = host.Workspace.GetMeshNodeStream()
                .Select(node => (MeshNode?)node)
                .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null));
            return catalog.Contributions
                .CombineLatest(ownNode, adminVerdict,
                    (contributions, node, isAdmin) => UiContributionProjection
                        .ProjectPersonAppTabs(contributions, userPath, node, isAdmin, viewerId))
                .Select(projected => ApplyAddressAccess(projected,
                    address => host.Hub.CheckPermissionOutcome(address, viewerObjectId, Permission.Read),
                    (address, reason) => logger?.LogWarning(
                        "Person-app tab hidden: no verdict on Read of '{Address}' for viewer {Viewer} (degraded dependency): {Reason}",
                        address, viewerObjectId, reason)))
                .Switch();
        });
    }

    /// <summary>
    /// Folds the live <see cref="UiContributionGates.RequireAddressAccess"/> answers into the
    /// projected tabs: a tab with no probe passes as is; a probed one passes only on a GRANTED
    /// verdict for <see cref="Permission.Read"/> on its address. A pending probe hides the tab
    /// (seeded false). An UNDETERMINED verdict — the fold faulted or never answered — also hides it
    /// (fail closed), but as the projection of a named outcome, never a swallowed exception: the
    /// reason goes to <paramref name="onUndetermined"/> so a degraded dependency is logged, not read
    /// as "the viewer does not hold this extension".
    /// </summary>
    /// <param name="projected">The projected tabs with the address each must probe (or null).</param>
    /// <param name="outcomeOf">The viewer's live Read verdict on an address
    /// (<c>CheckPermissionOutcome</c>, which classifies faults and silence as undetermined).</param>
    /// <param name="onUndetermined">Told the address and reason of every undetermined verdict.</param>
    internal static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> ApplyAddressAccess(
        IReadOnlyList<(SettingsMenuItemDefinition Tab, string? AccessAddress)> projected,
        Func<string, IObservable<PermissionCheckOutcome>> outcomeOf,
        Action<string, string>? onUndetermined = null)
    {
        if (projected.Count == 0)
            return Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([]);
        var verdicts = projected.Select(entry => entry.AccessAddress is not { Length: > 0 } address
                ? Observable.Return(true)
                : Observable.Defer(() => outcomeOf(address))
                    .Do(outcome =>
                    {
                        if (outcome is { UndeterminedReason: { } reason })
                            onUndetermined?.Invoke(address, reason);
                    })
                    .Select(PassesAddressAccess)
                    .StartWith(false)
                    .DistinctUntilChanged())
            .ToList();
        return Observable.CombineLatest(verdicts)
            .Select(passes => (IReadOnlyList<SettingsMenuItemDefinition>)projected
                .Where((_, i) => passes[i])
                .Select(entry => entry.Tab)
                .ToList());
    }

    /// <summary>The <see cref="UiContributionGates.RequireAddressAccess"/> verdict for one outcome:
    /// GRANTED Read on the embedded address. Denied and undetermined both hide the tab. Pure.</summary>
    /// <param name="outcome">The viewer's Read verdict on the address.</param>
    internal static bool PassesAddressAccess(PermissionCheckOutcome outcome) => outcome.IsGranted;

    /// <summary>
    /// The contributed person-app tabs that do NOT shadow an established tab: a tab whose id is
    /// already on the page (a compiled person-app tab such as <c>Sharing</c> or <c>Preferences</c>,
    /// or a NodeSettings contribution) is dropped, so a package can never swap a surface in under a
    /// familiar label. Case-insensitive, like the settings routes. Pure.
    /// </summary>
    /// <param name="established">The tabs already on the page.</param>
    /// <param name="contributed">The person-app lane's tabs.</param>
    internal static IReadOnlyList<SettingsMenuItemDefinition> WithoutShadowingTabs(
        IReadOnlyList<SettingsMenuItemDefinition> established,
        IReadOnlyList<SettingsMenuItemDefinition> contributed)
    {
        var taken = new HashSet<string>(established.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        return contributed.Where(t => taken.Add(t.Id)).ToList();
    }

    /// <summary>
    /// The settings menu's permission gate — PURE, so both directions are assertable without a
    /// hub, a circuit or a rendered area.
    ///
    /// <para>A tab declaring <see cref="Permission.None"/> is chrome every viewer sees; anything
    /// else must be held by the viewer on the node whose settings page this is. That asymmetry is
    /// the fingerprint of a lost/stale permission snapshot: when the fold hands this a
    /// <see cref="Permission.None"/> viewer, the ONLY tabs left standing are the
    /// <see cref="Permission.None"/> ones — which is exactly how #1962 was reported ("the
    /// display-time-zone tab is absent; the Notifications entry beside it survives because it
    /// requires <see cref="Permission.None"/>").</para>
    /// </summary>
    /// <param name="items">The unfiltered, already-sorted tab set.</param>
    /// <param name="userPermissions">The viewer's effective permissions on the settings node.</param>
    internal static IReadOnlyList<SettingsMenuItemDefinition> FilterByPermission(
        IReadOnlyList<SettingsMenuItemDefinition> items,
        Permission userPermissions)
    {
        var result = new List<SettingsMenuItemDefinition>(items.Count);
        foreach (var item in items)
            if (item.RequiredPermission == Permission.None
                || userPermissions.HasFlag(item.RequiredPermission))
                result.Add(item);
        return result;
    }

    /// <summary>
    /// Registers the default NODE settings tabs — the ones that act on the node whose page this is:
    /// Metadata, Access Control, Effective Access (check what a person may do on THIS node) and
    /// Versions on every node, plus Node Types and Groups on a partition root only
    /// (<see cref="RestrictSettingsTabsToPartitionRoot"/>).
    ///
    /// <para>What is deliberately NOT here: <b>Files</b> (the node's ⋯ menu opens the file browser —
    /// a second copy on the settings page was the same browser under another name) and
    /// <b>Appearance</b> (the theme is the viewer's own preference, not the node's — it lives in the
    /// person app's Preferences tab, <see cref="PersonApp"/>). Guarded against double registration.</para>
    /// </summary>
    public static MessageHubConfiguration AddDefaultSettingsMenuItems(
        this MessageHubConfiguration config)
    {
        if (config.Get<bool>(nameof(AddDefaultSettingsMenuItems)))
            return config;
        config = config.Set(true, nameof(AddDefaultSettingsMenuItems));

        return config.AddSettingsMenuItems(
            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.MetadataTab,
                Label: "Metadata",
                ContentBuilder: SettingsLayoutArea.BuildMetadataTab,
                Icon: FluentIcons.Info(),
                Order: 0,
                Keywords: ["name", "description", "category", "icon", "order", "id",
                    "namespace", "node type", "state", "version", "created", "modified",
                    "timestamps", "identity", "display"])
            { LabelKey = "settings.metadata" },

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.NodeTypesTab,
                Label: "Node Types",
                ContentBuilder: SettingsLayoutArea.BuildNodeTypesTab,
                Group: "Management",
                Icon: FluentIcons.Document(),
                GroupIcon: FluentIcons.Document(),
                Order: 100,
                Keywords: ["node types", "types", "definitions", "schema", "data model",
                    "creatable types"])
            { LabelKey = "settings.nodeTypes", GroupKey = "settings.groupManagement" },

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.AccessControlTab,
                Label: "Access Control",
                ContentBuilder: SettingsLayoutArea.BuildAccessControlTab,
                Group: "Security",
                Icon: FluentIcons.Shield(),
                GroupIcon: FluentIcons.Shield(),
                Order: 200,
                Keywords: ["access", "permissions", "roles", "assignments", "users",
                    "sharing", "security", "grant", "deny"])
            { LabelKey = "settings.accessControl", GroupKey = "settings.groupSecurity" },

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.GroupsTab,
                Label: "Groups",
                ContentBuilder: SettingsLayoutArea.BuildGroupsTab,
                Group: "Security",
                Icon: FluentIcons.People(),
                Order: 210,
                Keywords: ["groups", "members", "membership", "teams", "roles"])
            { LabelKey = "settings.groups", GroupKey = "settings.groupSecurity" },

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.EffectiveAccessTab,
                Label: "Check access",
                ContentBuilder: SettingsLayoutArea.BuildEffectiveAccessTab,
                Group: "Security",
                Icon: FluentIcons.PersonSearch(),
                Order: 220,
                Keywords: ["effective access", "check access", "permissions", "test", "user", "check",
                    "evaluate", "who can", "audit"])
            { LabelKey = "settings.effectiveAccess", GroupKey = "settings.groupSecurity" })
            .AddSettingsMenuItems(new SettingsMenuItemProvider(VersionsTab))
            // The Space's own management — never offered on the nodes below it.
            .RestrictSettingsTabsToPartitionRoot(SettingsLayoutArea.NodeTypesTab, SettingsLayoutArea.GroupsTab);
    }

    /// <summary>
    /// The node's Versions — the history view, embedded — offered only where the Versions area has a
    /// renderer (it rides the optional <c>MeshWeaver.Graph.Views</c> module; see
    /// <see cref="MeshNodeLayoutAreas.CanRenderArea"/>, which fails OPEN when the definition cannot say).
    /// </summary>
    private static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> VersionsTab(
        LayoutAreaHost host, RenderingContext _)
        => Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>(
            MeshNodeLayoutAreas.CanRenderArea(host.LayoutDefinition, MeshNodeLayoutAreas.VersionsArea)
                ? [VersionsTabDefinition]
                : []);

    private static SettingsMenuItemDefinition VersionsTabDefinition { get; } = new(
        Id: SettingsLayoutArea.VersionsTab,
        Label: "Versions",
        ContentBuilder: SettingsLayoutArea.BuildVersionsTab,
        Icon: FluentIcons.History(),
        Order: 50,
        Keywords: ["versions", "history", "restore", "compare", "previous", "undo"])
    { LabelKey = "menu.versions" };
}

/// <summary>
/// Internal holder for accumulated settings menu item providers.
/// </summary>
internal record SettingsMenuProviderCollection(
    IReadOnlyList<SettingsMenuItemProvider> Providers)
{
    public SettingsMenuProviderCollection AddRange(
        IEnumerable<SettingsMenuItemProvider> newProviders)
        => new(Providers.Concat(newProviders).ToList());
}

/// <summary>
/// Tab ids shown only on a partition root (<see cref="SettingsMenuItemsExtensions.RestrictSettingsTabsToPartitionRoot"/>).
/// </summary>
/// <param name="Ids">The root-only tab ids.</param>
internal sealed record PartitionRootSettingsTabs(System.Collections.Immutable.ImmutableHashSet<string> Ids)
{
    /// <summary>No root-only tabs.</summary>
    public static PartitionRootSettingsTabs Empty { get; } =
        new(System.Collections.Immutable.ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Retired tab id → the tab that now carries it (<see cref="SettingsMenuItemsExtensions.AliasSettingsTab"/>).
/// </summary>
/// <param name="Map">The aliases.</param>
internal sealed record SettingsTabAliases(System.Collections.Immutable.ImmutableDictionary<string, string> Map)
{
    /// <summary>No aliases.</summary>
    public static SettingsTabAliases Empty { get; } =
        new(System.Collections.Immutable.ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>The settings page's title (<see cref="SettingsMenuItemsExtensions.WithSettingsTitle"/>).</summary>
/// <param name="Title">The title function.</param>
internal sealed record SettingsTitle(Func<LayoutAreaHost, MeshNode?, string?> Title);

/// <summary>
/// Tab ids a hub's settings page leaves out (<see cref="SettingsMenuItemsExtensions.HideSettingsTabs"/>).
/// </summary>
/// <param name="Ids">The hidden tab ids.</param>
internal sealed record HiddenSettingsTabs(System.Collections.Immutable.ImmutableHashSet<string> Ids);
