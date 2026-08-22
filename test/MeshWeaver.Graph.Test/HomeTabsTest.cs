using System;
using System.Linq;
using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The home surface (<see cref="UserActivityLayoutAreas.BuildHome"/>) — ONE
/// <see cref="MeshSearchControl"/> whose SCOPE TABS are the phone-home tabs: Shared with me (only
/// with grants, store items excluded) · Pinned (only with pins) · Apps (default ∪ installed apps,
/// 24-budgeted) · Spaces (catalog without store items) · All (everything, every depth). The scopes
/// share one search bar by construction — the typed term survives tab switches and every tab is
/// searchable. The search input is desktop-only (the view hides it on mobile); the <c>~/</c>
/// system tiles render as a dock row above the search. <see cref="HomeStyle.Catalog"/> switches
/// back to the legacy single list (covered by <see cref="HomeCatalogTest"/>).
/// </summary>
public class HomeTabsTest
{
    private const string NodePath = "rbuergi";

    /// <summary>A config whose DefaultApps carry no ~/ entry, so BuildHome returns the bare search
    /// control (no dock stack) and its scopes are directly assertable.</summary>
    private static HomeConfig NoDock(params string[] apps) =>
        new() { DefaultApps = apps.Length > 0 ? apps : ["Store", "Doc"] };

    private static MeshSearchControl Search(UiControl home) =>
        home.Should().BeOfType<MeshSearchControl>().Subject;

    private static string[] ScopeLabels(MeshSearchControl search) =>
        search.ScopeTabs!.Select(t => t.Label).ToArray();

    // ── Structure ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Home_Default_IsDockPlusOneSearchSurface()
    {
        // Shipped DefaultApps include ~/Chat → a dock row above ONE search control.
        UserActivityLayoutAreas.BuildHome(NodePath)
            .Should().BeOfType<StackControl>().Subject
            .Areas.Should().HaveCount(2, "system-tile dock + the single scoped search surface");
    }

    [Fact]
    public void Home_NoShareNoPin_ScopesAreAppsSpacesAll()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath, NoDock()));

        ScopeLabels(search).Should().Equal("Apps", "Spaces", "All");
    }

    [Fact]
    public void Home_WithSharesAndPins_ScopeOrder()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath, NoDock(),
            sharedTargets: ["OrgA/Module"], user: new User { PinnedPaths = ["Doc/GUI"] }));

        ScopeLabels(search).Should().Equal("Shared with me", "Pinned", "Apps", "Spaces", "All");
    }

    [Fact]
    public void Home_OneSharedSearchBar_DesktopOn()
    {
        // The bar is ON (the view hides the input responsively on mobile); the control-level
        // hidden query and sort options are the FIRST scope's — the fallback contract for clients
        // without scope support.
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath, NoDock(),
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
    public void Shared_ExcludesStoreItems_AndKeepsTheCompletenessFallback()
    {
        // The silent per-viewer entitlement grants (StandardPacks) made every plugin partition
        // read as "shared with me" — store items are excluded here because an app is represented
        // on the Apps scope, exactly once. And source:accessed is an INNER join, so the default
        // last-accessed option stays a two-leg union with a plain fallback.
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath, NoDock(),
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

    // ── Apps scope ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apps_UnionsConfigDefaultsAndInstalled_EachAppExactlyOnce()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath, NoDock(),
            installedApps: ["Chess", "store", "Chess"]));

        var apps = search.ScopeTabs!.Single(t => t.Label == "Apps");
        // Defaults (Store, Doc) ∪ installed (Chess) — "store" dedupes case-insensitively.
        apps.Query.Should().Contain("path:(Store OR Doc OR Chess)");
        apps.Query.Should().NotContain("store OR");
        // Default order alphabetical; all three sorts offered, scope-locally.
        apps.SortOptions![0].Query.Should().Be(apps.Query);
        apps.Query.Should().Contain("sort:Name-asc");
        apps.SortOptions.Should().HaveCount(3);
    }

    [Fact]
    public void Apps_BudgetOf24_BoundsTheQueryItself()
    {
        var many = NoDock(Enumerable.Range(1, 30).Select(i => $"P{i}").ToArray());
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath, many));

        var apps = search.ScopeTabs!.Single(t => t.Label == "Apps");
        var firstLeg = apps.Query.Split('\n')[0];
        firstLeg.Split(" OR ").Should().HaveCount(24, "the AppBudget slice bounds the path alternation");
        firstLeg.Should().NotContain("P25", "entries beyond the budget are dropped in config order");
    }

    [Fact]
    public void AppEntries_BudgetCountsDockTiles_SliceBeforeTheSplit()
    {
        var cfg = new HomeConfig
        {
            DefaultApps = new[] { "~/Chat" }
                .Concat(Enumerable.Range(1, 30).Select(i => $"P{i}"))
                .ToList(),
        };

        var (systemAreas, paths) = UserActivityLayoutAreas.AppEntries(cfg, installedApps: null);

        systemAreas.Should().Equal("~/Chat");
        paths.Should().HaveCount(23, "24 total minus the dock tile — dock counts against the budget");
    }

    // ── Spaces + All scopes ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Spaces_ExcludesStoreItems_TheDedupRule()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath, NoDock()));

        var spaces = search.ScopeTabs!.Single(t => t.Label == "Spaces");
        foreach (var option in spaces.SortOptions!)
        {
            option.Query.Should().Contain("-nodeType:Store/Plugin");
            option.Query.Should().Contain("-nodeType:Store/Catalog");
        }
    }

    [Fact]
    public void All_SearchesEverything_AtEveryDepth()
    {
        var search = Search(UserActivityLayoutAreas.BuildHome(NodePath, NoDock()));

        var all = search.ScopeTabs!.Last();
        all.Label.Should().Be("All");
        all.Query.Should().Contain("is:main context:search");
        all.Query.Should().NotContain("namespace:", "All is the SUBTREE query — everything, every depth");
        all.Query.Should().NotContain("-nodeType:Store/Plugin", "the All scope hides nothing");
    }

    // ── System (dock) tiles ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("~/Chat")]
    [InlineData("~/Chat/")]   // extra slashes normalize — the known-tile lookup keys on the TRIMMED segment
    public void SystemTile_ThreadsDockTile_TargetsTheViewersChatArea(string entry)
    {
        var tile = UserActivityLayoutAreas.BuildSystemAppTile(NodePath, entry);

        tile.Should().NotBeNull();
        tile!.NodePath.Should().Be($"{NodePath}/Chat", "the tile opens the viewer's own Threads app");
        tile.Title.Should().Be("Threads");
        tile.ImageUrl.Should().Be("/static/NodeTypeIcons/chat.svg");
    }

    [Fact]
    public void SystemTile_UnknownAreaFallsBack_MalformedIsNull()
    {
        var unknown = UserActivityLayoutAreas.BuildSystemAppTile(NodePath, "~/Foo");
        unknown!.Title.Should().Be("Foo");
        unknown.ImageUrl.Should().Be("/static/NodeTypeIcons/puzzlepiece.svg");

        UserActivityLayoutAreas.BuildSystemAppTile(NodePath, "~/").Should().BeNull();
        UserActivityLayoutAreas.BuildSystemAppTile(NodePath, "Store").Should().BeNull();
    }

    // ── Install manifests → installed apps ──────────────────────────────────────────────────────

    [Fact]
    public void InstalledItemsOf_YieldsOnlyItemsWithALiveInstall()
    {
        // The Store's per-user install manifest ({owner}/_Install/{slug}) is mesh-compiled and read
        // UNTYPED by design: items[] with a non-empty installedPath ARE the installed apps.
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
