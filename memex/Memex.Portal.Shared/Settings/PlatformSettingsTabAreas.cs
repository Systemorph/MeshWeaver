using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// The first tranche of settings tabs served as <see cref="UiContribution"/> DATA instead of
/// compiled provider registrations (WS7 slice 2, design #1645): What's New, About and Privacy.
/// The CONTENT stays compiled — each tab's existing <c>BuildContent</c> is exposed as a normal
/// layout AREA — while the menu ENTRY (label, icon, order, grouping, admin gate) is a seeded
/// <c>UiContribution</c> node the compiled aggregator projects under the closed gate vocabulary.
/// A plugin contributes a settings tab the exact same way; these seeds prove the lane on the
/// platform's own tabs.
/// </summary>
public static class PlatformSettingsTabAreas
{
    /// <summary>Layout area rendering <see cref="WhatsNewSettingsTab.BuildContent"/>.</summary>
    public const string WhatsNewArea = "SettingsWhatsNew";

    /// <summary>Layout area rendering <see cref="AboutSettingsTab.BuildContent"/>.</summary>
    public const string AboutArea = "SettingsAbout";

    /// <summary>Layout area rendering <see cref="PrivacySettingsTab.BuildContent"/> (admin-gated).</summary>
    public const string PrivacyArea = "SettingsPrivacy";

    /// <summary>
    /// Registers the tranche's tab contents as layout areas on the per-node hubs. The settings
    /// pane embeds them via the contributed entries' <c>Area</c>; the pane supplies the
    /// padding/scroll container, so each area builds into a plain full-width stack.
    /// </summary>
    public static MessageHubConfiguration AddPlatformSettingsTabAreas(this MessageHubConfiguration config)
        => config.AddLayout(layout => layout
            .WithView(WhatsNewArea, (host, _) => WhatsNewSettingsTab.BuildContent(host, PaneStack()))
            .WithView(AboutArea, (host, _) => AboutSettingsTab.BuildContent(host, PaneStack()))
            // Privacy's tab content is the ADMIN EDITOR of the public statement (the statement
            // itself is served anonymously at /privacy). The contributed entry hides the tab via
            // Gates.AdminOnly, but an area is directly addressable by URL — so the area re-asserts
            // the same gate instead of trusting the menu to be the only door. TakeLast(1) waits for
            // the gate to RESOLVE (it opens with a synthetic false so menus render ungated tabs
            // immediately) — a neutral pane shows meanwhile, so an admin never flashes the denial.
            .WithView(PrivacyArea, (host, _) => AdminMenuGate.IsPlatformAdmin(host)
                .TakeLast(1)
                .Select(isAdmin => isAdmin
                    ? PrivacySettingsTab.BuildContent(host, PaneStack())
                    : (UiControl)Controls.Markdown(host.Localize("ui.accessDeniedAdminsOnly")))
                .StartWith((UiControl)PaneStack())));

    /// <summary>
    /// Seeds the tranche's menu entries as platform-static <c>UiContribution</c> nodes under
    /// <c>Admin/UiContribution</c>. The node id is the former compiled tab id, keeping every
    /// <c>/GlobalSettings/{Id}</c> deep link stable across the migration.
    /// </summary>
    public static MeshBuilder AddPlatformSettingsTabContributions(this MeshBuilder builder)
        => builder.AddMeshNodes(
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
            Seed(PrivacySettingsTab.TabId, "Privacy", new UiContribution
            {
                Context = UiContribution.SettingsContext,
                Area = PrivacyArea,
                Label = "Privacy",
                LabelKey = "settings.privacy",
                Icon = "Shield",
                Group = "Administration",
                GroupKey = "settings.groupAdministration",
                GroupIcon = "Shield",
                Order = 330,
                Gates = new UiContributionGates { AdminOnly = true },
            }));

    private static MeshNode Seed(string id, string name, UiContribution content)
        => new(id, "Admin/UiContribution")
        {
            NodeType = UiContributionNodeType.NodeType,
            Name = name,
            Content = content,
        };

    private static StackControl PaneStack() => Controls.Stack.WithWidth("100%");
}
