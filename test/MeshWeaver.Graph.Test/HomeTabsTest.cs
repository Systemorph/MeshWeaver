using System;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The home surface (<see cref="UserActivityLayoutAreas.BuildHome"/>) — ONE
/// <see cref="MeshSearchControl"/> whose SCOPE TABS are the phone-home tabs: Shared with me (only
/// with grants; store items and User roots excluded) · Pinned (only with pins) · Apps (the
/// viewer's OWN <c>{owner}/_App</c> records — a SINGLE-PARTITION query, which is why it loads
/// fast; records are materialized from config defaults + install manifests, and Threads is an
/// ordinary record) · Spaces (catalog without store items) · All (everything, every depth). The
/// scopes share one search bar by construction. <see cref="HomeStyle.Catalog"/> switches back to
/// the legacy single list (covered by <see cref="HomeCatalogTest"/>).
/// </summary>
public class HomeTabsTest
{
    private const string NodePath = "rbuergi";

    private static MeshSearchControl Search(UiControl home) =>
        home.Should().BeOfType<MeshSearchControl>().Subject;

    private static string[] ScopeLabels(MeshSearchControl search) =>
        search.ScopeTabs!.Select(t => t.Label).ToArray();

    // ── Structure ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Home_NoShareNoPin_ScopesAreAppsSpacesAll()
    {
        // No dock, no extra thing: the home is the ONE scoped search surface.
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath));

        ScopeLabels(search).Should().Equal("Apps", "Spaces", "All");
    }

    [Fact]
    public void Home_WithSharesAndPins_ScopeOrder()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath,
            sharedTargets: ["OrgA/Module"], user: new User { PinnedPaths = ["Doc/GUI"] }));

        ScopeLabels(search).Should().Equal("Shared with me", "Pinned", "Apps", "Spaces", "All");
    }

    [Fact]
    public void Home_OneSharedSearchBar_DesktopOn()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath,
            sharedTargets: ["OrgA/Module"]));

        search.ShowSearchBox.Should().Be(true);
        search.HiddenQuery!.ToString().Should().Be(search.ScopeTabs![0].Query);
        search.SortOptions!.Select(o => o.Query)
            .Should().Equal(search.ScopeTabs![0].SortOptions!.Select(o => o.Query));
    }

    [Fact]
    public void Home_StyleCatalog_FallsBackToTheLegacySingleList()
    {
        var home = UserActivityLayoutAreas.BuildHome(NodePath,
            new HomeConfig { Style = HomeStyle.Catalog });

        home.Should().BeOfType<MeshSearchControl>().Subject
            .ScopeTabs.Should().BeNull("the legacy escape hatch is the scope-less catalog");
    }

    // ── Shared-with-me scope ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Shared_ExcludesStoreItemsAndUserRoots_AndKeepsTheCompletenessFallback()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath,
            sharedTargets: ["OrgA/Module", "OrgB/Deck"]));

        var shared = search.ScopeTabs![0];
        shared.Label.Should().Be("Shared with me");
        var legs = shared.Query.Split('\n');
        legs.Should().HaveCount(2, "the accessed leg alone would hide a never-opened share");
        legs[0].Should().Contain("source:accessed");
        legs[1].Should().NotContain("source:accessed");
        foreach (var leg in legs)
        {
            leg.Should().Contain("path:OrgA/Module|OrgB/Deck");
            leg.Should().Contain("-nodeType:Store/Plugin");
            leg.Should().Contain("-nodeType:Store/Catalog");
            leg.Should().Contain("-nodeType:User",
                "a grant resolving to another user's home partition must not list that person's space as shared");
        }
    }

    // ── Apps scope: the viewer's own records, single partition ──────────────────────────────────

    [Fact]
    public void Apps_QueriesTheViewersOwnRecords_SinglePartition()
    {
        // THE point of the record model: the old cover-path alternation fanned out across every
        // partition schema (the multi-second home lag); the records query names ONE partition.
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath));

        var apps = search.ScopeTabs!.Single(t => t.Label == "Apps");
        apps.Query.Should().Contain($"path:{NodePath}/_App scope:children nodeType:InstalledApp");
        apps.Query.Should().NotContain(" OR ", "no path alternation — the records query is the bound");
        apps.ItemArea.Should().Be(AppTileLayoutArea.AppTileArea,
            "records render through their tile area, never as generic record cards");
        // Alphabetical default; source:accessed is meaningless on records, so that option sorts by
        // modified instead of hiding never-opened apps behind an INNER join.
        apps.SortOptions![0].Query.Should().Be(apps.Query);
        apps.Query.Should().Contain("sort:Name-asc");
        apps.SortOptions!.Select(o => o.Query).Should().OnlyContain(q => !q.Contains("source:accessed"));
    }

    // ── Record materialization specs ────────────────────────────────────────────────────────────

    [Fact]
    public void AppRecordSpecs_DefaultsCarryProductNamesAndIcons_ThreadsIsAnOrdinaryRecord()
    {
        var specs = UserActivityLayoutAreas.AppRecordSpecs(new HomeConfig(), NodePath, manifestItems: null);

        specs.Select(s => s.Id).Should().Equal("Store", "Doc", "Chat");
        specs.Single(s => s.Id == "Store").Name.Should().Be("Store");
        specs.Single(s => s.Id == "Doc").Name.Should().Be("Documentation");
        var threads = specs.Single(s => s.Id == "Chat");
        threads.Name.Should().Be("Threads");
        threads.Icon.Should().Be("/static/NodeTypeIcons/chat.svg");
        threads.OpenPath.Should().Be($"{NodePath}/Chat", "the Threads record opens the viewer's own Chat area");
        threads.Plugin.Should().BeNull();
    }

    [Fact]
    public void AppRecordSpecs_ManifestItemsAppend_DedupedAgainstDefaults()
    {
        var specs = UserActivityLayoutAreas.AppRecordSpecs(
            new HomeConfig(), NodePath, manifestItems: ["Chess", "Store", "Chess"]);

        specs.Select(s => s.Id).Should().Equal("Store", "Doc", "Chat", "Chess");
        var chess = specs.Single(s => s.Id == "Chess");
        chess.Source.Should().Be("install");
        chess.Plugin.Should().Be("Chess");
        // The pre-existing default keeps its product identity — the manifest doesn't re-add it.
        specs.Single(s => s.Id == "Store").Source.Should().Be("default");
    }

    // ── The tile ────────────────────────────────────────────────────────────────────────────────

    private static MeshNode Record(string id, App content, string? name = null, string? icon = null) =>
        MeshNode.FromPath($"{NodePath}/_App/{id}") with
        {
            NodeType = AppNodeType.NodeType,
            Name = name ?? id,
            Icon = icon,
            Content = content,
        };

    [Fact]
    public void AppTile_NavigatesToThePluginOrOpenPath_NeverTheRecord()
    {
        var options = new JsonSerializerOptions();

        var plugin = AppTileLayoutArea.BuildTile(
                Record("Chess", new App { Plugin = "Chess" }, name: "Chess"),
                $"{NodePath}/_App/Chess", options)
            .Should().BeOfType<MeshNodeCardControl>().Subject;
        plugin.NodePath.Should().Be("Chess", "the tile opens the APP, not the record");
        plugin.Title.Should().Be("Chess");

        var threads = AppTileLayoutArea.BuildTile(
                Record("Chat", new App { OpenPath = $"{NodePath}/Chat" }, name: "Threads",
                    icon: "/static/NodeTypeIcons/chat.svg"),
                $"{NodePath}/_App/Chat", options)
            .Should().BeOfType<MeshNodeCardControl>().Subject;
        threads.NodePath.Should().Be($"{NodePath}/Chat");
        threads.ImageUrl.Should().Be("/static/NodeTypeIcons/chat.svg");
    }

    [Fact]
    public void AppTile_WithoutATarget_RendersInert()
    {
        var tile = AppTileLayoutArea.BuildTile(
                Record("Broken", new App()), $"{NodePath}/_App/Broken", new JsonSerializerOptions())
            .Should().BeOfType<MeshNodeCardControl>().Subject;

        tile.DisableNavigation.Should().Be(true, "a record without a target must not navigate to itself");
    }

    // ── Install manifests → materialization input ───────────────────────────────────────────────

    [Fact]
    public void InstalledItemsOf_YieldsOnlyItemsWithALiveInstall()
    {
        var manifest = JsonSerializer.SerializeToElement(new
        {
            repo = "https://github.com/Systemorph/MeshWeaver.Plugins",
            items = new object[]
            {
                new { item = "Chess", installedPath = "rbuergi/Chess", installedAt = "2026-08-01" },
                new { item = "Publish", installedPath = (string?)null },        // un-installed: keeps entitlement only
                new { item = (string?)null, installedPath = "rbuergi/Ghost" },  // malformed: no item id
                new { item = "Training/", installedPath = "rbuergi/Training" }, // normalizes
            },
        });

        UserActivityLayoutAreas.InstalledItemsOf(manifest, new JsonSerializerOptions())
            .Should().Equal("Chess", "Training");
    }

    [Fact]
    public void InstalledItemsOf_ToleratesGarbage()
    {
        var options = new JsonSerializerOptions();
        UserActivityLayoutAreas.InstalledItemsOf(null, options).Should().BeEmpty();
        UserActivityLayoutAreas.InstalledItemsOf(JsonSerializer.SerializeToElement(42), options).Should().BeEmpty();
        UserActivityLayoutAreas.InstalledItemsOf(
                JsonSerializer.SerializeToElement(new { items = "nope" }), options)
            .Should().BeEmpty();
    }

    // ── Config ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HomeConfig_ShippedDefaults_TabbedWithStoreDocAndThreads()
    {
        HomeConfigNodeType.Defaults.Style.Should().Be(HomeStyle.Tabs);
        HomeConfigNodeType.Defaults.DefaultApps.Should().Equal("Store", "Doc", "~/Chat");
    }
}
