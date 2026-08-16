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
        TokenUsageSettingsTab.TabId,
    ];

    /// <summary>The tabs whose compiled providers gated on the platform-admin check.</summary>
    private static readonly string[] AdminTabIds =
    [
        PrivacySettingsTab.TabId,
        InvitationsSettingsTab.TabId,
        InboxSettingsTab.TabId,
        UpdatePolicySettingsTab.TabId,
        PublishedSettingsTab.TabId,
        TokenUsageSettingsTab.TabId,
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
}
