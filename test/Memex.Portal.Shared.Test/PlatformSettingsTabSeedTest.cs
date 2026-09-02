using Memex.Portal.Shared.Settings;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Seed-integrity contract for the settings tabs riding the <c>UiContribution</c> lane
/// (<see cref="PlatformSettingsTabAreas"/>, WS7 slices 2 + 4). Every seed must project into the
/// global settings menu (Context = Settings), point at an AREA the same class actually registers
/// (a dangling area renders the standard not-found placeholder — silently, to every user), keep
/// its node id equal to the tab's former compiled tab id (the <c>/GlobalSettings/{Id}</c> deep-link
/// contract), and the admin tabs must carry <c>Gates.AdminOnly</c> — dropping the gate would list
/// an Administration tab in every signed-in user's settings menu.
/// </summary>
public class PlatformSettingsTabSeedTest
{
    /// <summary>The former compiled tab ids that must survive as node ids, exactly.</summary>
    private static readonly string[] ExpectedIds =
    [
        WhatsNewSettingsTab.TabId,
        AboutSettingsTab.TabId,
        PrivacySettingsTab.TabId,
        InvitationsSettingsTab.TabId,
        InboxSettingsTab.TabId,
        UpdatePolicySettingsTab.TabId,
        PublishedSettingsTab.TabId,
    ];

    /// <summary>The tabs whose compiled providers gated on the platform-admin check.</summary>
    private static readonly string[] AdminTabIds =
    [
        PrivacySettingsTab.TabId,
        InvitationsSettingsTab.TabId,
        InboxSettingsTab.TabId,
        UpdatePolicySettingsTab.TabId,
        PublishedSettingsTab.TabId,
    ];

    [Fact]
    public void Every_Seed_Targets_The_Settings_Context_And_A_Registered_Area()
    {
        Assert.NotEmpty(PlatformSettingsTabAreas.Seeds);
        Assert.All(PlatformSettingsTabAreas.Seeds, seed =>
        {
            var contribution = Assert.IsType<UiContribution>(seed.Content);
            Assert.Equal(UiContribution.SettingsContext, contribution.Context);
            Assert.NotNull(contribution.Area);
            Assert.NotEqual("", contribution.Area);
            Assert.Contains(contribution.Area, PlatformSettingsTabAreas.Areas);
            Assert.False(string.IsNullOrEmpty(contribution.LabelKey),
                $"'{seed.Id}' must localize its label");
        });

        // 1:1 — every registered area is claimed by exactly one seed, so neither list can grow
        // without the other (an area without a seed is unreachable; a seed without an area dangles).
        var claimed = PlatformSettingsTabAreas.Seeds
            .Select(s => Assert.IsType<UiContribution>(s.Content).Area ?? "")
            .ToList();
        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.Equal(
            PlatformSettingsTabAreas.Areas.OrderBy(a => a, StringComparer.Ordinal),
            claimed.OrderBy(a => a, StringComparer.Ordinal));
    }

    [Fact]
    public void Node_Ids_Are_The_Former_Compiled_Tab_Ids()
    {
        // The node id becomes the /GlobalSettings/{Id} route segment (ProjectSettingsTabs), so a
        // renamed seed silently breaks every bookmarked deep link of the tab it migrated.
        Assert.Equal(
            ExpectedIds.OrderBy(i => i, StringComparer.Ordinal),
            PlatformSettingsTabAreas.Seeds.Select(s => s.Id).OrderBy(i => i, StringComparer.Ordinal));
    }

    [Fact]
    public void Admin_Tabs_Carry_The_AdminOnly_Gate_And_Ungated_Tabs_Do_Not()
    {
        Assert.All(PlatformSettingsTabAreas.Seeds, seed =>
        {
            var contribution = Assert.IsType<UiContribution>(seed.Content);
            if (AdminTabIds.Contains(seed.Id))
                Assert.True(contribution.Gates?.AdminOnly,
                    $"'{seed.Id}' was admin-gated as a compiled provider and must stay admin-only");
            else
                Assert.NotEqual(true, contribution.Gates?.AdminOnly);
        });
    }

    [Fact]
    public void Seeds_Live_In_The_Admin_UiContribution_Namespace_As_UiContribution_Nodes()
    {
        Assert.All(PlatformSettingsTabAreas.Seeds, seed =>
        {
            Assert.Equal(UiContributionNodeType.NodeType, seed.NodeType);
            Assert.Equal("Admin/UiContribution", seed.Namespace);
        });
    }

    [Fact]
    public void Every_Seed_Passes_The_Contribution_Integrity_Check()
    {
        // 🚨 The check core did not have. A contributed entry naming a context nobody declares
        // renders NOWHERE — no error, no warning, not even an area-not-found placeholder; six
        // shipped entries were dark for nine days that way (Systemorph/MeshWeaver.Plugins#1162,
        // which is why that repo grew scripts/check-menu-contexts.py). Core seeds its
        // contributions from COMPILED code, so the cheap equivalent is a pinning test over the
        // seed list — this one.
        var problems = UiContributionSeedValidation.Validate(
            PlatformSettingsTabAreas.Seeds,
            registeredAreas: PlatformSettingsTabAreas.Areas);
        Assert.Empty(problems);

        // CONTROL ARM 1 — the subject is still there. A Seeds list that emptied (a moved module, a
        // renamed property, a refactor that stopped populating it) would make the assertion above
        // pass having checked nothing at all.
        Assert.Equal(ExpectedIds.Length, PlatformSettingsTabAreas.Seeds.Count);

        // CONTROL ARM 2 — the checker can still fail. Break one real seed in each of the ways that
        // ship dark and require the validator to say so; a validator degenerated into "no
        // problems" (or one whose vocabulary drifted away from the projection's) reds HERE.
        var sample = PlatformSettingsTabAreas.Seeds[0];
        var content = Assert.IsType<UiContribution>(sample.Content);

        Assert.NotEmpty(UiContributionSeedValidation.Validate(
            [sample with { Content = content with { Context = "SettingsTypo" } }],
            registeredAreas: PlatformSettingsTabAreas.Areas));
        Assert.NotEmpty(UiContributionSeedValidation.Validate(
            [sample with { Content = content with { Area = null } }],
            registeredAreas: PlatformSettingsTabAreas.Areas));
        Assert.NotEmpty(UiContributionSeedValidation.Validate(
            [sample with { Content = content with { Area = "NotRegisteredAnywhere" } }],
            registeredAreas: PlatformSettingsTabAreas.Areas));
        Assert.NotEmpty(UiContributionSeedValidation.Validate(
            [sample with { Content = content with { Href = "https://evil.example/phish" } }],
            registeredAreas: PlatformSettingsTabAreas.Areas));
        Assert.NotEmpty(UiContributionSeedValidation.Validate(
            [sample with { Content = content with { LabelKey = null } }],
            registeredAreas: PlatformSettingsTabAreas.Areas));
        Assert.NotEmpty(UiContributionSeedValidation.Validate(
            [sample with { NodeType = "Markdown" }],
            registeredAreas: PlatformSettingsTabAreas.Areas));
    }
}
