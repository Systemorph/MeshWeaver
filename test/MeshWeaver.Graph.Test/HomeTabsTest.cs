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
/// <see cref="MeshSearchControl"/> whose SCOPE TABS are the phone-home tabs: Pinned (only with
/// pins) · Apps (the viewer's OWN <c>{owner}/_App</c> records — a SINGLE-PARTITION query, which is
/// why it loads fast; records are materialized from config defaults + install manifests, Threads
/// is an ordinary record, and the grid paints icon tiles straight from the query rows) · Spaces
/// (catalog without store items) · All (everything, every depth). Shared with me is its OWN band
/// below the search (only with grants; store items and User roots excluded). The scopes share one
/// search bar by construction. <see cref="HomeStyle.Catalog"/> switches back to the legacy single
/// list (covered by <see cref="HomeCatalogTest"/>).
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
    public void Home_WithPins_PinnedScopeComesFirst()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath,
            user: new User { PinnedPaths = ["Doc/GUI"] }));

        ScopeLabels(search).Should().Equal("Pinned", "Apps", "Spaces", "All");
    }

    [Fact]
    public void Home_WithShares_SharedIsItsOwnBandBelowTheSearch_NotAScope()
    {
        // "shared with me can be separate section" — cross-partition invitations are a distinct
        // kind of content, not another lens on the catalog.
        var home = UserActivityLayoutAreas.BuildHome(NodePath, sharedTargets: ["OrgA/Module"]);

        var stack = home.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCount(2, "the scoped search on top, the shared band below");
    }

    [Fact]
    public void Home_OneSharedSearchBar_DesktopOn()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath));

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

    // ── Shared-with-me band ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void SharedBand_ExcludesStoreItemsAndUserRoots_AndKeepsTheCompletenessFallback()
    {
        var shared = UserActivityLayoutAreas.BuildSharedBand(
            ["OrgA/Module", "OrgB/Deck"], locale: null)!;

        shared.Title!.ToString().Should().Be("Shared with me");
        shared.ShowSearchBox.Should().Be(false, "the band is a list, not a second search bar");
        var legs = shared.HiddenQuery!.ToString()!.Split('\n');
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

    [Fact]
    public void SharedBand_WithoutGrants_IsAbsent()
    {
        UserActivityLayoutAreas.BuildSharedBand([], locale: null).Should().BeNull();
    }

    // ── Apps scope: the viewer's own records, single partition, icon grid ───────────────────────

    [Fact]
    public void Apps_QueriesTheViewersOwnRecords_SinglePartition()
    {
        // THE point of the record model: the old cover-path alternation fanned out across every
        // partition schema (the multi-second home lag); the records query names ONE partition.
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath));

        var apps = search.ScopeTabs!.Single(t => t.Label == "Apps");
        apps.Query.Should().Contain($"path:{NodePath}/_App scope:children nodeType:InstalledApp");
        apps.Query.Should().NotContain(" OR ", "no path alternation — the records query is the bound");
        // Alphabetical default; source:accessed is meaningless on records, so that option sorts by
        // modified instead of hiding never-opened apps behind an INNER join.
        apps.SortOptions![0].Query.Should().Be(apps.Query);
        apps.Query.Should().Contain("sort:Name-asc");
        apps.SortOptions!.Select(o => o.Query).Should().OnlyContain(q => !q.Contains("source:accessed"));
    }

    [Fact]
    public void Apps_RendersTheIconGridFromTheQueryRows_NavigatingToTheApp()
    {
        // "should load from mesh, icon should be the icon of the app, then it should render
        // insta" — Icons mode paints tiles from the rows (no per-record hub, no content), and
        // NavigateToMainNode makes a tile open the APP (the record's MainNode), not the record.
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath));

        var apps = search.ScopeTabs!.Single(t => t.Label == "Apps");
        apps.RenderMode.Should().Be(nameof(MeshSearchRenderMode.Icons));
        apps.NavigateToMainNode.Should().Be(true);
        apps.ItemArea.Should().BeNull("a per-record tile area meant one hub activation per result");
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

    // ── Record target + healing (pure) ──────────────────────────────────────────────────────────

    private static MeshNode Record(string id, App content, string? name = null,
        string? icon = null, string? mainNode = null) =>
        MeshNode.FromPath($"{NodePath}/_App/{id}") with
        {
            NodeType = AppNodeType.NodeType,
            Name = name ?? id,
            Icon = icon,
            MainNode = mainNode ?? $"{NodePath}/_App/{id}",
            Content = content,
        };

    [Fact]
    public void AppTargetOf_PluginPathOrOwnerArea()
    {
        UserActivityLayoutAreas.AppTargetOf(
                new UserActivityLayoutAreas.AppRecordSpec("Chess", "Chess",
                    UserActivityLayoutAreas.GenericAppIcon, "Chess", null, "install"))
            .Should().Be("Chess");
        UserActivityLayoutAreas.AppTargetOf(
                new UserActivityLayoutAreas.AppRecordSpec("Chat", "Threads",
                    "/static/NodeTypeIcons/chat.svg", null, $"{NodePath}/Chat", "default"))
            .Should().Be($"{NodePath}/Chat");
    }

    [Fact]
    public void NeedsHealing_TriggersOnDefaultMainNodeOrGenericIcon_NotOnAFinishedRecord()
    {
        var spec = new UserActivityLayoutAreas.AppRecordSpec(
            "Chess", "Chess", UserActivityLayoutAreas.GenericAppIcon, "Chess", null, "install");

        // MainNode still the record's own path (the pre-Icons rounds never stamped a target).
        UserActivityLayoutAreas.NeedsHealing(
                Record("Chess", new App { Plugin = "Chess" }, icon: "/covers/chess.png"), spec)
            .Should().BeTrue();
        // Generic icon even with a target stamped.
        UserActivityLayoutAreas.NeedsHealing(
                Record("Chess", new App { Plugin = "Chess" },
                    icon: UserActivityLayoutAreas.GenericAppIcon, mainNode: "Chess"), spec)
            .Should().BeTrue();
        // Finished: real icon, real target.
        UserActivityLayoutAreas.NeedsHealing(
                Record("Chess", new App { Plugin = "Chess" },
                    icon: "/covers/chess.png", mainNode: "Chess"), spec)
            .Should().BeFalse();
    }

    [Fact]
    public void HealAppRecord_StampsTargetAndFace_TouchingOnlyWhatImproves()
    {
        var stale = Record("Chess", new App { Plugin = "Chess" },
            icon: UserActivityLayoutAreas.GenericAppIcon);

        var healed = UserActivityLayoutAreas.HealAppRecord(
            stale, "Chess Trainer", "/covers/chess.png", "Chess");

        healed.MainNode.Should().Be("Chess", "the tile must open the APP, not the record");
        healed.Icon.Should().Be("/covers/chess.png");
        healed.Name.Should().Be("Chess", "a non-empty name is never overwritten — the owner may have renamed it");
    }

    [Fact]
    public void HealAppRecord_NothingToImprove_ReturnsTheSameInstance()
    {
        // The materializer skips the write entirely on an identity heal — an incurable record
        // (cover has no icon either) must not cost a patch per home render.
        var finished = Record("Chess", new App { Plugin = "Chess" },
            icon: "/covers/chess.png", mainNode: "Chess");

        UserActivityLayoutAreas.HealAppRecord(finished, "Chess", "/covers/chess.png", "Chess")
            .Should().BeSameAs(finished);
    }

    [Fact]
    public void ResolveAppFace_PrefersTheCover_FallsBackToTheSpec()
    {
        var spec = new UserActivityLayoutAreas.AppRecordSpec(
            "Chess", "Chess", UserActivityLayoutAreas.GenericAppIcon, "Chess", null, "install");
        var cover = MeshNode.FromPath("Chess") with { Name = "Chess Trainer", Icon = "/covers/chess.png" };

        var withCover = UserActivityLayoutAreas.ResolveAppFace(spec,
            new[] { cover }.ToDictionary(n => n.Path, StringComparer.OrdinalIgnoreCase));
        withCover.Name.Should().Be("Chess Trainer");
        withCover.Icon.Should().Be("/covers/chess.png");

        var withoutCover = UserActivityLayoutAreas.ResolveAppFace(spec,
            new System.Collections.Generic.Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase));
        withoutCover.Name.Should().Be("Chess");
        withoutCover.Icon.Should().Be(UserActivityLayoutAreas.GenericAppIcon);
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
