using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

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

        return Observable.CombineLatest(streams)
            .Select(lists =>
            {
                var items = new List<SettingsMenuItemDefinition>();
                foreach (var list in lists)
                    if (list is not null)
                        items.AddRange(list);
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
        var isAuthenticated = !string.IsNullOrEmpty(viewer?.ObjectId) && viewer?.IsVirtual != true;
        if (!isAuthenticated)
            return Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>([]);

        var viewerId = accessService.ViewerId();
        var menuPath = host.Hub.Address.ToString();

        // Deferred so a hub without a MeshDataSource — where GetMeshNodeStream() throws
        // SYNCHRONOUSLY — surfaces as OnError into the caller's Catch rather than as a throw out
        // of the aggregator, taking every other settings tab with it.
        return Observable.Defer(() =>
        {
            var ownNode = host.Workspace.GetMeshNodeStream()
                .Catch<MeshNode, Exception>(_ => Observable.Return<MeshNode>(null!));
            return catalog.Contributions
                .CombineLatest(ownNode, host.Hub.IsGlobalAdmin().StartWith(false),
                    (contributions, node, isAdmin) => UiContributionProjection
                        .ProjectNodeSettingsTabs(contributions, menuPath, node, isAdmin, viewerId));
        });
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
    /// Registers the default settings menu items (Metadata, NodeTypes, Files,
    /// AccessControl, Groups, EffectiveAccess, Appearance).
    /// Guarded to avoid double registration.
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
                Id: SettingsLayoutArea.FilesTab,
                Label: "Files",
                ContentBuilder: SettingsLayoutArea.BuildFilesTab,
                Group: "Management",
                Icon: FluentIcons.Folder(),
                Order: 110,
                Keywords: ["files", "documents", "uploads", "attachments", "content",
                    "collections", "blobs"])
            { LabelKey = "settings.files", GroupKey = "settings.groupManagement" },

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
                Label: "Effective Access",
                ContentBuilder: SettingsLayoutArea.BuildEffectiveAccessTab,
                Group: "Security",
                Icon: FluentIcons.PersonSearch(),
                Order: 220,
                Keywords: ["effective access", "permissions", "test", "user", "check",
                    "evaluate", "who can", "audit"])
            { LabelKey = "settings.effectiveAccess", GroupKey = "settings.groupSecurity" },

            new SettingsMenuItemDefinition(
                Id: SettingsLayoutArea.AppearanceTab,
                Label: "Appearance",
                ContentBuilder: SettingsLayoutArea.BuildAppearanceTab,
                Icon: FluentIcons.PaintBrush(),
                Order: 900,
                Keywords: ["appearance", "theme", "color", "dark mode", "light mode",
                    "display", "style", "layout"])
            { LabelKey = "settings.appearance" }
        );
    }
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
