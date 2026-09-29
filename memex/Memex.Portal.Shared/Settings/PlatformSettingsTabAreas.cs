using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// The platform's settings tabs served as <see cref="UiContribution"/> DATA instead of compiled
/// provider registrations (design #1645). The CONTENT stays compiled — each tab's existing
/// <c>BuildContent</c> is exposed as a normal layout AREA — while the menu ENTRY (label, icon,
/// order, grouping, admin gate) is a seeded <c>UiContribution</c> node the compiled aggregator
/// projects under the closed gate vocabulary. A plugin contributes a settings tab the exact same
/// way; these seeds prove the lane on the platform's own tabs.
///
/// <para>Tranche 1 (WS7 slice 2, #1648): What's New, About and Privacy. Tranche 2 (WS7 slice 4)
/// adds the platform-admin Administration tabs: <b>Invitations</b> and <b>Inbox</b> (the
/// invitation-only onboarding manager and the non-user mail inbox — the pair that used to be the
/// dedicated Admin menu), <b>Updates</b> (the <c>Admin/UpdatePolicy</c> auto-update strategy —
/// stable/continuous/none), <b>Published to the web</b> (every page a logged-out visitor can open,
/// read from the SAME enumeration <c>/sitemap.xml</c> renders, so the two cannot drift) and
/// <b>Token Usage</b> (per-model <c>_Usage</c> analytics with cost from ModelPricing).</para>
/// </summary>
public static class PlatformSettingsTabAreas
{
    /// <summary>Layout area rendering <see cref="WhatsNewSettingsTab.BuildContent"/>.</summary>
    public const string WhatsNewArea = "SettingsWhatsNew";

    /// <summary>Layout area rendering <see cref="AboutSettingsTab.BuildContent"/>.</summary>
    public const string AboutArea = "SettingsAbout";

    /// <summary>Layout area rendering <see cref="PrivacySettingsTab.BuildContent"/> (admin-gated).</summary>
    public const string PrivacyArea = "SettingsPrivacy";

    /// <summary>Layout area rendering <see cref="InvitationsSettingsTab.BuildInvitationsContent"/> (admin-gated).</summary>
    public const string InvitationsArea = "SettingsInvitations";

    /// <summary>Layout area rendering <see cref="InboxSettingsTab.BuildInboxContent"/> (admin-gated).</summary>
    public const string InboxArea = "SettingsInbox";

    /// <summary>Layout area rendering <see cref="UpdatePolicySettingsTab.BuildContent"/> (admin-gated).</summary>
    public const string UpdatePolicyArea = "SettingsUpdatePolicy";

    /// <summary>Layout area rendering <see cref="PublishedSettingsTab.BuildContent"/> (admin-gated).</summary>
    public const string PublishedArea = "SettingsPublished";

    /// <summary>Layout area rendering <see cref="ControlLaneSettingsTab.BuildContent"/> (admin-gated).</summary>
    public const string ControlLaneArea = "SettingsControlLane";

    /// <summary>Layout area rendering <see cref="ServiceIdentitiesSettingsTab.BuildContent"/> (admin-gated).</summary>
    public const string ServiceIdentitiesArea = "SettingsServiceIdentities";


    /// <summary>
    /// Registers the tranches' tab contents as layout areas on the per-node hubs. The settings
    /// pane embeds them via the contributed entries' <c>Area</c>; the pane supplies the
    /// padding/scroll container, so each area builds into a plain full-width stack.
    /// </summary>
    public static MessageHubConfiguration AddPlatformSettingsTabAreas(this MessageHubConfiguration config)
        => config
        // The Administration tabs are Admin-app tabs now; recorded on every node hub so an old
        // /_Setting/GlobalSettings/{id} (or any other settings page's) link redirects into the app.
        .RelocateSettingsTabsToAdminApp([.. AdminAppTabIds])
        .AddLayout(layout => layout
            .WithView(WhatsNewArea, (host, _) => WhatsNewSettingsTab.BuildContent(host, PaneStack()))
            .WithView(AboutArea, (host, _) => AboutSettingsTab.BuildContent(host, PaneStack()))
            // Privacy's tab content is the ADMIN EDITOR of the public statement (the statement
            // itself is served anonymously at /privacy).
            .WithView(PrivacyArea, (host, _) => AdminGated(host,
                () => PrivacySettingsTab.BuildContent(host, PaneStack())))
            .WithView(InvitationsArea, (host, _) => AdminGated(host,
                () => InvitationsSettingsTab.BuildInvitationsContent(host, PaneStack())))
            .WithView(InboxArea, (host, _) => AdminGated(host,
                () => InboxSettingsTab.BuildInboxContent(host, PaneStack())))
            .WithView(UpdatePolicyArea, (host, _) => AdminGated(host,
                () => UpdatePolicySettingsTab.BuildContent(host, PaneStack())))
            .WithView(PublishedArea, (host, _) => AdminGated(host,
                () => PublishedSettingsTab.BuildContent(host, PaneStack())))
            .WithView(ControlLaneArea, (host, _) => AdminGated(host,
                () => ControlLaneSettingsTab.BuildContent(host, PaneStack())))
            .WithView(ServiceIdentitiesArea, (host, _) => AdminGated(host,
                () => ServiceIdentitiesSettingsTab.BuildContent(host, PaneStack()))));

    /// <summary>
    /// The admin-gated area body. Each admin tab's contributed entry hides the tab via
    /// <c>Gates.AdminOnly</c>, but an area is directly addressable by URL — so the area re-asserts
    /// the same gate instead of trusting the menu to be the only door. TakeLast(1) waits for the
    /// gate to RESOLVE (it opens with a synthetic false so menus render ungated tabs immediately) —
    /// a neutral pane shows meanwhile, so an admin never flashes the denial.
    /// </summary>
    private static IObservable<UiControl> AdminGated(LayoutAreaHost host, Func<UiControl> content)
        => AdminMenuGate.IsPlatformAdmin(host)
            .TakeLast(1)
            .Select(isAdmin => isAdmin
                ? content()
                : (UiControl)Controls.Markdown(host.Localize("ui.accessDeniedAdminsOnly")))
            .StartWith((UiControl)PaneStack());

    /// <summary>
    /// Every area <see cref="AddPlatformSettingsTabAreas"/> registers — MUST list exactly the
    /// <c>WithView</c> entries above. The seed-integrity test asserts each seed's <c>Area</c>
    /// points at one of these, so a seed can never dangle against an unregistered area.
    /// </summary>
    internal static IReadOnlyList<string> Areas { get; } =
    [
        WhatsNewArea, AboutArea, PrivacyArea, InvitationsArea, InboxArea,
        UpdatePolicyArea, PublishedArea, ControlLaneArea, ServiceIdentitiesArea,
    ];

    /// <summary>
    /// The seeded menu entries, exposed for the pinning tests (same contract as
    /// <c>AiMenuContributions.Seeds</c>). Node id = the tab's former compiled tab id,
    /// keeping every <c>/GlobalSettings/{Id}</c> deep link stable across the migration; every
    /// label/icon/order/group is copied EXACTLY from the former compiled definition.
    /// </summary>
    internal static IReadOnlyList<MeshNode> Seeds { get; } =
    [
        // Ungated tabs — visible to every signed-in viewer.
        Seed(WhatsNewSettingsTab.TabId, "What's New", new UiContribution
        {
            Context = UiContribution.SettingsContext,
            Area = WhatsNewArea,
            Label = "What's New",
            LabelKey = "settings.whatsNew",
            Icon = "Sparkle",
            Order = 910,
        }),
        Seed(AboutSettingsTab.TabId, "About", new UiContribution
        {
            Context = UiContribution.SettingsContext,
            Area = AboutArea,
            Label = "About",
            LabelKey = "settings.about",
            Icon = "Info",
            Order = 900,
        }),
        // Tabs of the ADMIN APP (/Admin/Settings/{id}), platform admins only, in the app's sections —
        // "People & sign-in" (Invitations, Privacy, Published) and "Operations" (Updates, Control
        // lane, Inbox): the per-node settings lane (NodeSettings) gated to the AdminApp node type and
        // AdminOnly, and the area re-asserts the gate for direct URLs. They used to be global-settings
        // tabs (/_Setting/GlobalSettings/{id}); an old link redirects into the app (see
        // AddPlatformSettingsTabAreas). Ordered by tab Order within the section.
        Seed(InvitationsSettingsTab.TabId, "Invitations", new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = InvitationsArea,
            Label = "Invitations",
            LabelKey = "settings.invitations",
            Icon = "Mail",
            Group = AdminAppNodeType.PeopleGroup,
            GroupKey = AdminAppNodeType.PeopleGroupKey,
            GroupIcon = "People",
            Order = AdminAppNodeType.PeopleOrder + 10,
            Gates = AdminAppOnly,
        }),
        Seed(InboxSettingsTab.TabId, "Inbox", new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = InboxArea,
            Label = "Inbox",
            LabelKey = "settings.inbox",
            Icon = "Mail",
            Group = AdminAppNodeType.OperationsGroup,
            GroupKey = AdminAppNodeType.OperationsGroupKey,
            GroupIcon = "Wrench",
            Order = AdminAppNodeType.OperationsOrder + 40,
            Gates = AdminAppOnly,
        }),
        Seed(UpdatePolicySettingsTab.TabId, "Updates", new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = UpdatePolicyArea,
            Label = "Updates",
            LabelKey = "settings.updates",
            Icon = "ArrowSync",
            Group = AdminAppNodeType.OperationsGroup,
            GroupKey = AdminAppNodeType.OperationsGroupKey,
            GroupIcon = "Wrench",
            Order = AdminAppNodeType.OperationsOrder,
            Gates = AdminAppOnly,
        }),
        Seed(ControlLaneSettingsTab.TabId, "Control lane", new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = ControlLaneArea,
            Label = "Control lane",
            LabelKey = "settings.controlLane",
            Icon = "Key",
            Group = AdminAppNodeType.OperationsGroup,
            GroupKey = AdminAppNodeType.OperationsGroupKey,
            GroupIcon = "Wrench",
            Order = AdminAppNodeType.OperationsOrder + 10,
            Gates = AdminAppOnly,
        }),
        Seed(PublishedSettingsTab.TabId, "Published to the web", new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = PublishedArea,
            Label = "Published to the web",
            LabelKey = "settings.published",
            Icon = "Globe",
            Group = AdminAppNodeType.PeopleGroup,
            GroupKey = AdminAppNodeType.PeopleGroupKey,
            GroupIcon = "People",
            Order = AdminAppNodeType.PeopleOrder + 30,
            Gates = AdminAppOnly,
        }),
        Seed(ServiceIdentitiesSettingsTab.TabId, "Service identities", new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = ServiceIdentitiesArea,
            Label = "Service identities",
            LabelKey = "settings.serviceIdentities",
            Icon = "Bot",
            Group = AdminAppNodeType.PeopleGroup,
            GroupKey = AdminAppNodeType.PeopleGroupKey,
            GroupIcon = "People",
            Order = AdminAppNodeType.PeopleOrder + 40,
            Gates = AdminAppOnly,
        }),
        Seed(PrivacySettingsTab.TabId, "Privacy", new UiContribution
        {
            Context = UiContribution.NodeSettingsContext,
            Area = PrivacyArea,
            Label = "Privacy",
            LabelKey = "settings.privacy",
            Icon = "Shield",
            Group = AdminAppNodeType.PeopleGroup,
            GroupKey = AdminAppNodeType.PeopleGroupKey,
            GroupIcon = "People",
            Order = AdminAppNodeType.PeopleOrder + 20,
            Gates = AdminAppOnly,
        }),
    ];

    /// <summary>
    /// Seeds the menu entries as platform-static <c>UiContribution</c> nodes under
    /// <c>Admin/UiContribution</c>.
    /// </summary>
    public static MeshBuilder AddPlatformSettingsTabContributions(this MeshBuilder builder)
        => builder.AddMeshNodes(Seeds);

    /// <summary>
    /// The gate every Administration seed carries: the Admin app's node only, platform admins only.
    /// </summary>
    private static UiContributionGates AdminAppOnly => new()
    {
        AdminOnly = true,
        NodeTypes = [AdminAppNodeType.NodeType],
    };

    /// <summary>
    /// The seeded tabs that are Admin-app tabs — the ids an old global-settings link may still name.
    /// </summary>
    internal static IReadOnlyList<string> AdminAppTabIds { get; } =
    [
        InvitationsSettingsTab.TabId, InboxSettingsTab.TabId, UpdatePolicySettingsTab.TabId,
        PublishedSettingsTab.TabId, PrivacySettingsTab.TabId, ControlLaneSettingsTab.TabId,
        ServiceIdentitiesSettingsTab.TabId,
    ];

    private static MeshNode Seed(string id, string name, UiContribution content)
        => new(id, "Admin/UiContribution")
        {
            NodeType = UiContributionNodeType.NodeType,
            Name = name,
            Content = content,
        };

    private static StackControl PaneStack() => Controls.Stack.WithWidth("100%");
}
