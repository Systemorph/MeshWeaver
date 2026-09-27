using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The <b>Admin app</b> — the node at <c>Admin</c>, the root of the system partition that holds the
/// platform-admin grants (<c>Admin/_Access</c>), the update policy, the setup-link claim and the
/// platform catalogs. Installed on every instance by <see cref="GraphConfigurationExtensions"/>, so
/// it is part of the image's closed type set (the control image included) and needs no package.
///
/// <para><b>Why a type of its own.</b> The partition root used to be either ABSENT — so
/// <c>PathResolutionService</c> synthesized a typeless placeholder that activated on the default
/// hub configuration and rendered an empty page — or a persisted <c>Space</c> row, which listed the
/// system partition among the viewer's workspaces and rendered it as an empty Space. Neither is a
/// page anyone wants. This type is the page: <c>/Admin</c> opens the per-node Settings page of the
/// Admin node, and every platform-admin surface registers there (see
/// <see cref="AddAdminAppTab"/>) instead of on a person's own settings page.</para>
///
/// <para><b>Access.</b> Nothing here widens a grant. The node lives in the Admin partition, which
/// only a platform admin (an <c>Admin</c>-role grant in <c>Admin/_Access</c>) can read, so a
/// non-admin opening <c>/Admin</c> is refused by the partition exactly as before; and every tab a
/// module registers through <see cref="AddAdminAppTab"/> ALSO waits for a positive
/// <c>IsGlobalAdmin</c> answer, so a menu entry never outruns the grant.</para>
///
/// <para>See <c>Doc/Architecture/AdminApp</c> for the inventory of surfaces and where each went.</para>
/// </summary>
public static class AdminAppNodeType
{
    /// <summary>The NodeType of the Admin partition root.</summary>
    public const string NodeType = "AdminApp";

    /// <summary>The path of the Admin app — the Admin partition root.</summary>
    public const string Path = "Admin";

    /// <summary>The app's own href.</summary>
    public const string Href = "/" + Path;

    /// <summary>The settings-menu group the moved administration tabs keep inside the app.</summary>
    public const string AdministrationGroup = "Administration";

    /// <summary>Localization key of <see cref="AdministrationGroup"/>.</summary>
    public const string AdministrationGroupKey = "settings.groupAdministration";

    /// <summary>
    /// How long a tab waits for a POSITIVE <c>IsGlobalAdmin</c> answer before it stays hidden. The
    /// grant is a runtime AccessAssignment row, so the evaluator emits an empty seed first and the
    /// admin answer arrives with the synced query — the same bound every admin tab used before.
    /// </summary>
    private static readonly TimeSpan AdminCheckBound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The href of one tab inside the Admin app: <c>/Admin/Settings/{tabId}</c>. Built through
    /// <see cref="LayoutAreaReference.ToHref(object)"/> so it cannot drift from the menu's own links.
    /// </summary>
    /// <param name="tabId">The tab's settings-menu id.</param>
    public static string TabHref(string tabId) =>
        "/" + new LayoutAreaReference(MeshNodeLayoutAreas.SettingsArea) { Id = tabId }.ToHref(Path);

    /// <summary>True when <paramref name="host"/> renders on the Admin app's own hub.</summary>
    /// <param name="host">The layout host.</param>
    public static bool IsAdminAppHub(this LayoutAreaHost host)
        => string.Equals(host.Hub.Address.ToString(), Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registers <paramref name="tab"/> as a tab of the Admin app. The provider is registered like
    /// every settings provider (so it can ride the default node hub, as the tabs it replaces did),
    /// but it yields the tab ONLY on the Admin hub and ONLY once <c>IsGlobalAdmin</c> confirms the
    /// viewer POSITIVELY; everywhere else it yields nothing. The tab id is also recorded as
    /// RELOCATED, so an old link to it on any other settings page redirects into the app
    /// (<see cref="RelocatedSettingsTabs"/>).
    /// </summary>
    /// <param name="config">The hub configuration to register on.</param>
    /// <param name="tab">The tab. Its <see cref="SettingsMenuItemDefinition.RequiredPermission"/> is
    /// applied as well, against the viewer's permissions on the Admin node.</param>
    /// <param name="relocated">
    /// <c>true</c> (the default) when the tab used to live on another settings page and old links to
    /// it must redirect here. Pass <c>false</c> for a tab that ALSO exists elsewhere under the same
    /// id (a shared view such as About), which must keep answering there.
    /// </param>
    public static MessageHubConfiguration AddAdminAppTab(
        this MessageHubConfiguration config, SettingsMenuItemDefinition tab, bool relocated = true)
    {
        var withProvider = config.AddSettingsMenuItems(new SettingsMenuItemProvider((host, _) => AdminOnlyTab(host, tab)));
        return relocated ? withProvider.RelocateSettingsTabsToAdminApp(tab.Id) : withProvider;
    }

    /// <summary>
    /// Records <paramref name="tabIds"/> as tabs that moved INTO the Admin app — for surfaces that
    /// register their entry as data (a <c>UiContribution</c> gated to <see cref="NodeType"/>) rather
    /// than through <see cref="AddAdminAppTab"/>. A settings page on any other hub that is asked for
    /// one of these ids redirects to <see cref="TabHref"/> instead of falling back to its first tab.
    /// </summary>
    /// <param name="config">The hub configuration.</param>
    /// <param name="tabIds">The relocated tab ids.</param>
    public static MessageHubConfiguration RelocateSettingsTabsToAdminApp(
        this MessageHubConfiguration config, params string[] tabIds)
    {
        var existing = config.Get<RelocatedSettingsTabs>() ?? RelocatedSettingsTabs.Empty;
        return config.Set(existing with { Ids = existing.Ids.Union(tabIds) });
    }

    /// <summary>
    /// The redirect a settings page answers when asked for a tab that moved into the Admin app, or
    /// <c>null</c> when the tab is not relocated or the page IS the Admin app.
    /// </summary>
    /// <param name="host">The layout host of the settings page.</param>
    /// <param name="tabId">The requested tab id (query string already stripped).</param>
    internal static UiControl? RedirectIfRelocated(LayoutAreaHost host, string? tabId)
    {
        if (string.IsNullOrEmpty(tabId) || host.IsAdminAppHub())
            return null;
        var relocated = host.Hub.Configuration.Get<RelocatedSettingsTabs>();
        return relocated is not null && relocated.Ids.Contains(tabId)
            ? Controls.Redirect(TabHref(tabId))
            : null;
    }

    /// <summary>
    /// The admin gate for one tab: nothing off the Admin hub; on it, the tab once the viewer is
    /// confirmed a platform admin. Waits for the POSITIVE answer (the evaluator's first emission can
    /// be the premature empty seed) within <see cref="AdminCheckBound"/>, and starts empty so the
    /// menu renders at once. Public so a module whose provider is itself public API can delegate to
    /// it; everything else registers through <see cref="AddAdminAppTab"/>.
    /// </summary>
    /// <param name="host">The layout host of the settings page being rendered.</param>
    /// <param name="tab">The tab to contribute.</param>
    public static IObservable<IReadOnlyList<SettingsMenuItemDefinition>> AdminOnlyTab(
        LayoutAreaHost host, SettingsMenuItemDefinition tab)
    {
        IReadOnlyList<SettingsMenuItemDefinition> none = [];
        if (!host.IsAdminAppHub())
            return Observable.Return(none);
        var viewerId = host.Hub.ServiceProvider.GetService<AccessService>().ViewerId();
        if (string.IsNullOrEmpty(viewerId))
            return Observable.Return(none);

        return host.Hub.IsGlobalAdmin(viewerId)
            .Where(isAdmin => isAdmin)
            .Take(1)
            .Select(_ => (IReadOnlyList<SettingsMenuItemDefinition>)[tab])
            .Timeout(AdminCheckBound)
            .Catch<IReadOnlyList<SettingsMenuItemDefinition>, Exception>(_ => Observable.Return(none))
            .StartWith(none);
    }

    /// <summary>
    /// The default per-node settings tabs that mean nothing on the Admin partition root — its
    /// metadata, node types, files, groups and appearance are not administration. Access Control
    /// (the Admin partition's own grants) and Effective Access stay: they are the platform's role
    /// management.
    /// </summary>
    internal static readonly ImmutableArray<string> HiddenDefaultTabs =
    [
        SettingsLayoutArea.MetadataTab, SettingsLayoutArea.NodeTypesTab, SettingsLayoutArea.FilesTab,
        SettingsLayoutArea.GroupsTab, SettingsLayoutArea.AppearanceTab,
    ];

    /// <summary>
    /// Registers the Admin app: its NodeType and the <c>Admin</c> root node itself, so a fresh
    /// instance resolves <c>/Admin</c> to this type instead of synthesizing a typeless placeholder.
    /// </summary>
    /// <typeparam name="TBuilder">The mesh builder type.</typeparam>
    /// <param name="builder">The mesh builder.</param>
    public static TBuilder AddAdminAppType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode(), CreateRootNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// The <c>Admin</c> partition root. Hidden from search, create and content listings — it is an
    /// app, reached from the profile menu and the header, never browsed as a workspace.
    /// </summary>
    public static MeshNode CreateRootNode() => new(Path)
    {
        NodeType = NodeType,
        Name = "Administration",
        State = MeshNodeState.Active,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
    };

    /// <summary>The NodeType definition node for <see cref="NodeType"/>.</summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Administration",
        Icon = "/static/NodeTypeIcons/settings.svg",
        // A declaration declares itself a NodeType, never the type it declares (UserNodeType).
        NodeType = MeshNode.NodeTypePath,
        IsSatelliteType = false,
        ExcludeFromContext = new HashSet<string> { "search", "create", "content" },
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .HideSettingsTabs([.. HiddenDefaultTabs])
            .AddAdminAppTab(WhoAmISettingsTab.AdminAppTab, relocated: false)
            .AddAdminAppTab(GlobalAdministrationTab.Definition)
            .AddAdminAppTab(GlobalSettingsLayoutArea.DataSourcesAdminTab)
            .ApplyNodeHubContributions(NodeType)
            .AddLayout(layout => layout
                .WithDefaultArea(MeshNodeLayoutAreas.SettingsArea)
                // The landing page IS the settings page of the Admin node; registering it as the
                // node page keeps the provenance line above it (#4500).
                .WithNodePage(MeshNodeLayoutAreas.SettingsArea, SettingsLayoutArea.Settings))
    };
}

/// <summary>
/// The settings tab ids that moved into the Admin app (<see cref="AdminAppNodeType.RelocateSettingsTabsToAdminApp"/>).
/// Held on the hub configuration — per mesh, never static.
/// </summary>
/// <param name="Ids">The relocated tab ids.</param>
internal sealed record RelocatedSettingsTabs(ImmutableHashSet<string> Ids)
{
    /// <summary>No relocated tabs.</summary>
    public static RelocatedSettingsTabs Empty { get; } = new(ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase));
}
