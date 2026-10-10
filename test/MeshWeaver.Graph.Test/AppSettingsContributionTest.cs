using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The <see cref="UiContribution.AppSettingsContext"/> lane: a contribution names ONE host app and
/// becomes a tab of that app's settings page only — the surface Administration › Manage apps
/// (<c>Host = "Admin"</c>) and Threads › Models (<c>Host = "AI/AiThreads"</c>) are contributed on.
/// </summary>
public class AppSettingsContributionTest
{
    private static (MeshNode, UiContribution) At(string path, UiContribution content)
    {
        var slash = path.LastIndexOf('/');
        return (new MeshNode(path[(slash + 1)..], path[..slash]) { NodeType = UiContributionNodeType.NodeType }, content);
    }

    private static MeshNode Seed((MeshNode Node, UiContribution Content) c) => c.Node with { Content = c.Content };

    private static readonly MeshNode AdminNode = new("Admin") { NodeType = AdminAppNodeType.NodeType };
    private static readonly MeshNode ThreadsNode = new("AiThreads", "AI") { NodeType = "AiThreads" };

    private static UiContribution ManageApps => new()
    {
        Context = UiContribution.AppSettingsContext,
        Host = "Admin",
        Address = "Store",
        Area = "ManageApps",
        Label = "Manage apps",
        LabelKey = "admin.manageApps",
        Gates = new UiContributionGates { AdminOnly = true },
    };

    [Fact]
    public void AnAppSettingsTab_JoinsItsHostsSettingsPage()
    {
        var tab = Assert.Single(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("Store/AdminTabs/ManageApps", ManageApps)], "Admin", AdminNode, isAdmin: true, viewerId: "alice"));
        Assert.Equal("ManageApps", tab.Id);
        Assert.Equal("Manage apps", tab.Label);
    }

    [Fact]
    public void AnAppSettingsTab_NeverJoinsAnotherNodesSettingsPage()
    {
        Assert.Empty(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("Store/AdminTabs/ManageApps", ManageApps)], "AI/AiThreads", ThreadsNode, isAdmin: true, viewerId: "alice"));
    }

    [Theory]
    [InlineData("/ai/aithreads/")]
    [InlineData("AI/AiThreads")]
    [InlineData(" ai/AITHREADS ")]
    public void TheHostMatch_IgnoresCaseAndSlashes(string host)
    {
        var models = new UiContribution { Context = UiContribution.AppSettingsContext, Host = host.Trim(), Area = "ThreadsModels" };
        Assert.Single(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("AI/AppSettings/Models", models)], "AI/AiThreads", ThreadsNode, isAdmin: false, viewerId: "alice"));
    }

    [Fact]
    public void AnAppSettingsTab_WithoutAHost_RendersNowhere()
    {
        Assert.Empty(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("Store/AdminTabs/ManageApps", ManageApps with { Host = null })], "Admin", AdminNode, isAdmin: true, viewerId: "alice"));
    }

    [Fact]
    public void AdminOnly_StillSubtracts_ForANonAdmin()
    {
        Assert.Empty(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("Store/AdminTabs/ManageApps", ManageApps)], "Admin", AdminNode, isAdmin: false, viewerId: "bob"));
    }

    [Fact]
    public void AnAddressInsideTheContributorsPartition_IsKept_AndOneOutsideDropsTheTab()
    {
        Assert.Single(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("Store/AdminTabs/ManageApps", ManageApps with { Address = "Store/Catalog" })], "Admin", AdminNode, isAdmin: true, viewerId: "alice"));
        // Contributed from Acme but embedding Store: the author does not control Store.
        Assert.Empty(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("Acme/AdminTabs/ManageApps", ManageApps)], "Admin", AdminNode, isAdmin: true, viewerId: "alice"));
    }

    [Fact]
    public void WithoutAnAddress_TheTabEmbedsTheHostsOwnHub()
    {
        var tab = Assert.Single(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("AI/AppSettings/Models", new UiContribution
            {
                Context = UiContribution.AppSettingsContext, Host = "AI/AiThreads", Area = "ThreadsModels",
            })], "AI/AiThreads", ThreadsNode, isAdmin: false, viewerId: "alice"));
        Assert.Equal("Models", tab.Id);
    }

    [Fact]
    public void ANodeSettingsTab_IsUnchanged_AndIgnoresHost()
    {
        var plain = new UiContribution { Context = UiContribution.NodeSettingsContext, Area = "X", Host = "Admin" };
        Assert.Single(UiContributionProjection.ProjectNodeSettingsTabs(
            [At("Store/Tabs/X", plain)], "AI/AiThreads", ThreadsNode, isAdmin: false, viewerId: "alice"));
    }

    [Fact]
    public void SeedValidation_KnowsTheContext_AndReportsAMissingHost()
    {
        Assert.Empty(UiContributionSeedValidation.Validate([Seed(At("Store/AdminTabs/ManageApps", ManageApps))]));
        var problem = Assert.Single(UiContributionSeedValidation.Validate(
            [Seed(At("Store/AdminTabs/Hostless", ManageApps with { Host = null }))]));
        Assert.Contains("without a Host", problem);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("///")]
    [InlineData("  ")]
    public void SeedValidation_ReportsAHostThatTrimsToNothing(string host)
    {
        var seed = At("Store/AdminTabs/Slashes", ManageApps with { Host = host });
        Assert.Contains(UiContributionSeedValidation.Validate([Seed(seed)]), p => p.Contains("without a Host"));
        Assert.Empty(UiContributionProjection.ProjectNodeSettingsTabs([seed], "Admin", AdminNode, isAdmin: true, viewerId: "alice"));
    }

    [Fact]
    public void SeedValidation_ReportsAnAppSettingsAddressOutsideThePartition()
    {
        Assert.Contains(UiContributionSeedValidation.Validate([Seed(At("Acme/AdminTabs/ManageApps", ManageApps))]),
            p => p.Contains("outside the contribution's own partition"));
    }

    [Fact]
    public void RequireAddressAccess_IsInertOnThisLane_AndValidationSaysSo()
    {
        var gated = At("Store/AdminTabs/ManageApps",
            ManageApps with { Gates = new UiContributionGates { AdminOnly = true, RequireAddressAccess = true } });
        Assert.Single(UiContributionProjection.ProjectNodeSettingsTabs([gated], "Admin", AdminNode, isAdmin: true, viewerId: "alice"));
        Assert.Contains(UiContributionSeedValidation.Validate([Seed(gated)]), p => p.Contains("inert"));
    }

    [Fact]
    public void TheEmbeddedHub_IsTheDeclaredAddress_OrTheHostsOwn_NeverAForeignOne()
    {
        Assert.Equal("Store", UiContributionProjection.AppSettingsAddress(ManageApps, "Store/AdminTabs/ManageApps", "Admin"));
        Assert.Equal("Store/Catalog", UiContributionProjection.AppSettingsAddress(
            ManageApps with { Address = "/Store/Catalog/" }, "Store/AdminTabs/ManageApps", "Admin"));
        Assert.Equal("AI/AiThreads", UiContributionProjection.AppSettingsAddress(
            ManageApps with { Address = null }, "AI/AppSettings/Models", "AI/AiThreads"));
        Assert.Null(UiContributionProjection.AppSettingsAddress(ManageApps, "Acme/AdminTabs/ManageApps", "Admin"));
    }

    [Theory]
    [InlineData("/", "")]
    [InlineData("///", "/")]
    [InlineData(" ", "")]
    public void ASlashOnlyHost_NeverMatchesARootPage(string host, string menuPath)
        => Assert.False(UiContributionProjection.IsHost(host, menuPath));
}
