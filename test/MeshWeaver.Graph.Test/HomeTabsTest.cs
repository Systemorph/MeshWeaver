using System;
using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The TABBED home surface (<see cref="UserActivityLayoutAreas.BuildHome"/>) — the phone-home model:
/// <b>Shared with me</b> first (only with cross-partition grants, last-accessed ranking with a
/// completeness fallback), then <b>Pinned</b> (only with pins), then <b>Apps</b> (config-declared
/// default apps ∪ the owner's installed <c>App</c> records — every app exactly once), then
/// <b>Spaces</b> (the catalog WITHOUT store items, so nothing is listed twice, and WITHOUT an
/// embedded search box — the chrome search covers it). <see cref="HomeStyle.Catalog"/> switches
/// back to the legacy single-list <see cref="UserActivityLayoutAreas.BuildCatalog"/> (covered by
/// <see cref="HomeCatalogTest"/>).
/// </summary>
public class HomeTabsTest
{
    private const string NodePath = "rbuergi";

    private static string[] TabLabels(UiControl home) =>
        home.Should().BeOfType<TabsControl>().Subject.Areas
            .Select(a => a.Skins.OfType<TabSkin>().Single().Label!.ToString()!)
            .ToArray();

    [Fact]
    public void Home_Default_IsTabbed_AppsThenSpaces()
    {
        // No shares, no pins → exactly the two always-present tabs, Apps before Spaces.
        var home = UserActivityLayoutAreas.BuildHome(NodePath);

        TabLabels(home).Should().Equal("Apps", "Spaces");
    }

    [Fact]
    public void Home_WithShares_SharedWithMeComesFirst()
    {
        var home = UserActivityLayoutAreas.BuildHome(NodePath, sharedTargets: ["OrgA/Module"]);

        TabLabels(home).Should().Equal("Shared with me", "Apps", "Spaces");
    }

    [Fact]
    public void Home_WithPins_PinnedComesBeforeApps()
    {
        var home = UserActivityLayoutAreas.BuildHome(NodePath,
            sharedTargets: ["OrgA/Module"], user: new User { PinnedPaths = ["Doc/GUI"] });

        TabLabels(home).Should().Equal("Shared with me", "Pinned", "Apps", "Spaces");
    }

    [Fact]
    public void Home_StyleCatalog_FallsBackToTheLegacySingleList()
    {
        var home = UserActivityLayoutAreas.BuildHome(NodePath,
            new HomeConfig { Style = HomeStyle.Catalog });

        home.Should().BeOfType<MeshSearchControl>("the legacy escape hatch is the tab-less catalog");
    }

    // ── Apps tab ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Apps_UnionsConfigDefaultsAndInstalled_EachAppExactlyOnce()
    {
        var apps = UserActivityLayoutAreas.BuildApps(
                new HomeConfig(), installedApps: ["Chess", "store", "Chess"])
            .Should().BeOfType<MeshSearchControl>().Subject;

        // Shipped defaults (Store, Doc) ∪ installed (Chess) — "store" dedupes case-insensitively.
        var query = apps.HiddenQuery!.ToString()!;
        query.Should().Contain("path:(Store OR Doc OR Chess)");
        query.Should().NotContain("store OR");
    }

    [Fact]
    public void Apps_DefaultOrderIsAlphabetical_AllThreeSortsOffered()
    {
        var apps = UserActivityLayoutAreas.BuildApps(new HomeConfig(), installedApps: null)
            .Should().BeOfType<MeshSearchControl>().Subject;

        apps.SortOptions![0].Label.Should().Be("Alphabetical");
        apps.SortOptions![0].Query.Should().Be(apps.HiddenQuery!.ToString());
        apps.HiddenQuery!.ToString().Should().Contain("sort:Name-asc");
        apps.SortOptions!.Select(o => o.Label).OrderBy(l => l, StringComparer.Ordinal).Should()
            .Equal("Alphabetical", "Last accessed", "Last modified");
    }

    [Fact]
    public void Apps_LastAccessedSort_IsAUnionWithACompletenessFallback()
    {
        // source:accessed is an INNER join on the caller's access log — alone it would HIDE a
        // never-opened app. The last-accessed option must therefore be a two-leg path-keyed union:
        // accessed-ranked first, plain fallback second.
        var apps = UserActivityLayoutAreas.BuildApps(new HomeConfig(), installedApps: null)
            .Should().BeOfType<MeshSearchControl>().Subject;

        var lastAccessed = apps.SortOptions!.Single(o => o.Label == "Last accessed").Query;
        var legs = lastAccessed.Split('\n');
        legs.Should().HaveCount(2);
        legs[0].Should().Contain("source:accessed");
        legs[1].Should().NotContain("source:accessed");
        legs[1].Should().Contain("path:(");
    }

    [Fact]
    public void Apps_NoAppsAnywhere_RendersAHint()
    {
        UserActivityLayoutAreas.BuildApps(new HomeConfig { DefaultApps = [] }, installedApps: [])
            .Should().BeOfType<MarkdownControl>("an empty Apps tab must explain itself, not render blank");
    }

    // ── Spaces tab (dedup) ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Spaces_ExcludesStoreItems_TheDedupRule()
    {
        // An app is represented exactly once: plugin covers + the store root live on the Apps tab
        // (or in the Store), so the Spaces tab filters them out of every sort option's query.
        var spaces = UserActivityLayoutAreas.BuildSpaces(NodePath)
            .Should().BeOfType<MeshSearchControl>().Subject;

        foreach (var option in spaces.SortOptions!)
        {
            option.Query.Should().Contain("-nodeType:Store/Plugin");
            option.Query.Should().Contain("-nodeType:Store/Catalog");
        }
    }

    [Fact]
    public void Spaces_HasNoEmbeddedSearchBox()
    {
        // Every client chrome already carries a global search; an embedded box would double it
        // (the two-search-bars problem on the mobile clients).
        UserActivityLayoutAreas.BuildSpaces(NodePath)
            .Should().BeOfType<MeshSearchControl>().Subject
            .ShowSearchBox.Should().Be(false);
    }

    // ── Shared-with-me tab ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SharedWithMe_DefaultsToLastAccessed_WithACompletenessFallback()
    {
        var shared = UserActivityLayoutAreas.BuildSharedWithMe(["OrgA/Module", "OrgB/Deck"])
            .Should().BeOfType<MeshSearchControl>().Subject;

        shared.SortOptions![0].Label.Should().Be("Last accessed");
        var legs = shared.HiddenQuery!.ToString()!.Split('\n');
        legs.Should().HaveCount(2, "the accessed leg alone would hide a share the caller never opened");
        legs[0].Should().Contain("source:accessed");
        legs[0].Should().Contain("path:OrgA/Module|OrgB/Deck");
        legs[1].Should().NotContain("source:accessed");
        legs[1].Should().Contain("path:OrgA/Module|OrgB/Deck");
    }

    // ── Config ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HomeConfig_ShippedDefaults_TabbedWithStoreAndDoc()
    {
        HomeConfigNodeType.Defaults.Style.Should().Be(HomeStyle.Tabs);
        HomeConfigNodeType.Defaults.DefaultApps.Should().Equal("Store", "Doc");
    }
}
