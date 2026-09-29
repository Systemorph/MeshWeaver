using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// The settings-menu group the moved administration tabs used to share. Kept for modules that
    /// still name it; a tab should carry one of the SECTION groups instead —
    /// <see cref="PeopleGroup"/>, <see cref="OperationsGroup"/>, <see cref="CommercialGroup"/>,
    /// <see cref="FleetGroup"/>.
    /// </summary>
    public const string AdministrationGroup = "Administration";

    /// <summary>Localization key of <see cref="AdministrationGroup"/>.</summary>
    public const string AdministrationGroupKey = "settings.groupAdministration";

    /// <summary>Section: who may sign in and who administers — Administrators, Sign-in providers,
    /// Invitations, Privacy, Published to the web.</summary>
    public const string PeopleGroup = "People & sign-in";

    /// <summary>Localization key of <see cref="PeopleGroup"/>.</summary>
    public const string PeopleGroupKey = "settings.groupPeople";

    /// <summary>Section: running the instance — Updates, Registration and Control lane, Data sources,
    /// Partitions, Inbox.</summary>
    public const string OperationsGroup = "Operations";

    /// <summary>Localization key of <see cref="OperationsGroup"/>.</summary>
    public const string OperationsGroupKey = "settings.groupOperations";

    /// <summary>Section: what the instance sells and spends — Coupons, Instance grants, Composition,
    /// AI usage and cost.</summary>
    public const string CommercialGroup = "Commercial";

    /// <summary>Localization key of <see cref="CommercialGroup"/>.</summary>
    public const string CommercialGroupKey = "settings.groupCommercial";

    /// <summary>Section: the other instances this one controls (the control instance only).</summary>
    public const string FleetGroup = "Fleet";

    /// <summary>Localization key of <see cref="FleetGroup"/>.</summary>
    public const string FleetGroupKey = "settings.groupFleet";

    /// <summary>First slot of the <see cref="PeopleGroup"/> band (300-399).</summary>
    public const int PeopleOrder = 300;

    /// <summary>First slot of the <see cref="OperationsGroup"/> band (400-499).</summary>
    public const int OperationsOrder = 400;

    /// <summary>First slot of the <see cref="CommercialGroup"/> band (500-599).</summary>
    public const int CommercialOrder = 500;

    /// <summary>First slot of the <see cref="FleetGroup"/> band (600-699).</summary>
    public const int FleetOrder = 600;

    /// <summary>
    /// The instance's NAME — the host of its public address (<c>Portal:BaseUrl</c>, else
    /// <c>PublicBaseUrl</c>), e.g. <c>memex.systemorph.com</c>. The Admin app is titled with it, so
    /// the page says WHICH instance it administers; <c>null</c> when neither key is configured.
    /// </summary>
    /// <param name="configuration">The host configuration.</param>
    public static string? InstanceName(IConfiguration? configuration)
    {
        var baseUrl = configuration?["Portal:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = configuration?["PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;
        return Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
            : baseUrl.Trim();
    }

    /// <summary>The instance's name, read from the service provider's configuration
    /// (<see cref="InstanceName(IConfiguration?)"/>).</summary>
    /// <param name="services">The service provider.</param>
    public static string? InstanceName(IServiceProvider services)
        => InstanceName(services.GetService<IConfiguration>());

    /// <summary>
    /// The viewer's platform-admin verdict, LIVE: <c>false</c> first (the menu renders at once),
    /// then every change the evaluator reports — a grant arriving AND a grant being revoked —
    /// de-duplicated, failing closed to <c>false</c> on a fault. Never a one-shot: a stream that
    /// feeds a live view must keep answering (never <c>.Take(1)</c>).
    /// </summary>
    /// <param name="hub">The hub to evaluate on.</param>
    /// <param name="viewerId">The viewer.</param>
    public static IObservable<bool> LiveAdminVerdict(IMessageHub hub, string viewerId)
        => hub.IsGlobalAdmin(viewerId)
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .StartWith(false)
            .DistinctUntilChanged();

    /// <summary>
    /// The viewer's platform-admin verdict, LIVE, for a surface that must not paint BEFORE it is
    /// known — the Admin app's settings nav. Unlike <see cref="LiveAdminVerdict"/> it opens with no
    /// synthetic <c>false</c>: its first emission is the evaluator's first ANSWER, then every change
    /// (a grant arriving AND a grant being revoked), de-duplicated, failing closed to <c>false</c> on
    /// a fault. Never a one-shot (never <c>.Take(1)</c>).
    ///
    /// <para>🚨 Why the settings page takes this one. The page already waits for the viewer's
    /// permissions on the node — the same evaluator fold — before it renders anything, so a verdict
    /// seeded <c>false</c> buys no earlier paint; it only lets the first frame carry "not an admin"
    /// for a lane whose own evaluation has not answered yet. Each lane subscribes its own verdict, so
    /// which lanes made the first frame was a race: the compiled Admin-app tabs did, the seeded ones
    /// (Invitations, Privacy, Published to the web, Updates, Control lane, Inbox) did not — and a
    /// reader that takes ONE frame (an MCP <c>get @Admin/area/Settings</c>, a first paint) saw them
    /// missing. <c>AdminAppFirstFrameTest</c> pins it.</para>
    /// </summary>
    /// <param name="hub">The hub to evaluate on.</param>
    /// <param name="viewerId">The viewer.</param>
    public static IObservable<bool> AnsweredAdminVerdict(IMessageHub hub, string viewerId)
        => hub.IsGlobalAdmin(viewerId)
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .DistinctUntilChanged();

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
    /// The admin gate for one tab: nothing off the Admin hub; on it, the tab exactly while the viewer
    /// IS a platform admin. The admin verdict is observed LIVE for the life of the menu — a grant
    /// that arrives adds the tab, a grant that is revoked removes it again — so the menu never
    /// latches an earlier answer (a one-shot positive would keep offering administration after the
    /// grant is gone). It starts empty so the menu renders at once, and a faulted verdict stream
    /// fails CLOSED to no tab. The tab lane emits nothing until the verdict has ANSWERED
    /// (<see cref="AnsweredAdminVerdict"/>), so the nav never paints without it. Public so a module whose provider is itself public API can delegate to
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

        // The ANSWERED verdict, never a seeded false: this lane is part of the settings nav, which
        // must not paint before the verdict is known (see AnsweredAdminVerdict).
        return AnsweredAdminVerdict(host.Hub, viewerId)
            .Select(isAdmin => isAdmin ? (IReadOnlyList<SettingsMenuItemDefinition>)[tab] : none);
    }

    /// <summary>
    /// The default node-settings tabs that mean nothing in the Admin app — the Admin root's metadata,
    /// node types, groups and versions are not administration. Its Access Control lists exactly the
    /// platform-admin grants, so it is not a tab of its own: it is part of the ONE Administrators tab
    /// (<see cref="GlobalAdministrationTab"/>). Effective Access is a node-management probe ("what may
    /// this person do on THIS node"), and on the instance it read as a global answer it never was.
    /// </summary>
    internal static readonly ImmutableArray<string> HiddenDefaultTabs =
    [
        SettingsLayoutArea.MetadataTab, SettingsLayoutArea.NodeTypesTab, SettingsLayoutArea.GroupsTab,
        SettingsLayoutArea.AccessControlTab, SettingsLayoutArea.EffectiveAccessTab,
        SettingsLayoutArea.VersionsTab,
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
        // Every declaration node carries its definition — readers of NodeType nodes (the deployment
        // report's adopted-framework inventory among them) refuse one without it. The one instance
        // lives at the root.
        Content = new NodeTypeDefinition { DefaultNamespace = "", RestrictedToNamespaces = [""] },
        HubConfiguration = config => config
            .AddDefaultLayoutAreas()
            .HideSettingsTabs([.. HiddenDefaultTabs])
            // The app is titled with the INSTANCE's name — the page says which instance it administers.
            .WithSettingsTitle((host, _) => InstanceName(host.Hub.ServiceProvider))
            .AddAdminAppTab(GlobalAdministrationTab.Definition)
            .AddAdminAppTab(GlobalSettingsLayoutArea.DataSourcesAdminTab)
            // The Admin node's own grants ARE the administrators: one tab, not two.
            .AliasSettingsTab(SettingsLayoutArea.AccessControlTab, GlobalAdministrationTab.TabId)
            .AliasSettingsTab(SettingsLayoutArea.EffectiveAccessTab, GlobalAdministrationTab.TabId)
            // "Who am I" acts on the person, not the instance: it is the person app's Account tab.
            .RelocateSettingsTabToPersonApp(WhoAmISettingsTab.TabId, PersonApp.AccountTab)
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
