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
/// The home's Catalog region (<see cref="UserActivityLayoutAreas.BuildHome"/>) — two icon grids,
/// <b>Apps</b> (the viewer's own app records or the app directory, single partition, painted from
/// the query rows) then <b>Spaces</b> (the profile's space manifest plus the spaces the viewer was
/// invited into), and nothing else (policy <c>home-is-composer-apps-spaces</c>). <see cref="HomeStyle.Catalog"/>
/// switches back to the legacy single list (covered by <see cref="HomeCatalogTest"/>).
/// </summary>
public class HomeTabsTest
{
    private const string NodePath = "rbuergi";

    // ── Structure: Apps, then Spaces — and nothing else ─────────────────────────────────────────

    [Fact]
    public void Home_IsAppsThenSpaces_TwoIconGrids()
    {
        // Policy home-is-composer-apps-spaces: the message box, apps, then spaces. The message
        // box is the welcome template's Composer region; this is the Catalog region below it.
        var home = UserActivityLayoutAreas.BuildHome(NodePath, sharedTargets: ["OrgA/Module"]);

        var stack = home.Should().BeOfType<StackControl>().Subject;
        // The container keeps its child views protected; read them the way the layout renderer receives them.
        var bands = ((System.Collections.IEnumerable)typeof(StackControl).BaseType!
                .GetProperty("Views", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(stack)!)
            .OfType<MeshSearchControl>().ToArray();
        bands.Select(b => b.Title!.ToString()).Should().Equal("Apps", "Spaces");
        bands.Should().OnlyContain(b => Equals(b.RenderMode, MeshSearchRenderMode.Icons), "both are launchers");
        bands.Should().OnlyContain(b => Equals(b.ShowSearchBox, false), "a launcher, not a search surface");
        bands[1].HiddenQuery!.ToString().Should().Contain("path:OrgA/Module",
            "a space someone shared with the viewer is a space they can open (#385)");
    }

    [Fact]
    public void Apps_NoLongerCarriesTheSpacesFilter()
    {
        UserActivityLayoutAreas.BuildAppsBand(NodePath, null).ScopeTabs!.Select(t => t.Label)
            .Should().Equal(["Apps"], "spaces have their own band below the apps");
        UserActivityLayoutAreas.BuildAppsBand(NodePath, null, fromDirectory: true).ScopeTabs!.Select(t => t.Label)
            .Should().Equal("Apps", "Hidden");
    }

    [Fact]
    public void Home_RendersNothingThroughAForeignItemArea()
    {
        // 🚨 THE regression guard. A MeshSearch ItemArea resolves an area on the RESULT node's own
        // hub — one hub activation per row, on a hub the home does not own. In the distributed
        // portal that failed as "AppTile not found" (and the thread rail's `RailItem` had the same
        // shape) while a monolith resolved it happily, so only a structural assertion catches it.
        // Every home surface must paint from query ROWS.
        var sections = new[]
        {
            UserActivityLayoutAreas.BuildAppsBand(NodePath, null),
            UserActivityLayoutAreas.BuildSpacesBand(NodePath, null, ["Acme"], ["OrgA/Module"]),
            UserActivityLayoutAreas.BuildSharedBand(["OrgA/Module"], null)!,
        };

        foreach (var section in sections)
        {
            section.ItemArea.Should().BeNull($"'{section.Title}' must render from query rows");
            foreach (var scope in section.ScopeTabs ?? [])
                scope.ItemArea.Should().BeNull($"scope '{scope.Label}' must render from query rows");
        }
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

    // ── The Apps section: the viewer's own records, single partition, icon grid ────────────────

    [Fact]
    public void Apps_QueriesTheViewersOwnRecords_SinglePartition()
    {
        // THE point of the record model: the old cover-path alternation fanned out across every
        // partition schema (the multi-second home lag); the records query names ONE partition.
        var apps = UserActivityLayoutAreas.BuildAppsBand(NodePath, null);

        var query = apps.HiddenQuery!.ToString()!;
        query.Should().Contain($"path:{NodePath}/_App scope:children nodeType:InstalledApp");
        query.Should().NotContain(" OR ", "no path alternation — the records query is the bound");
        // 🚨 No source:accessed: it is an INNER JOIN keyed by the row's OWN path, so on records it
        // would drop every never-opened app and match nothing anyway (opening an app records a
        // visit to the APP, never to the record pointing at it).
        query.Should().NotContain("source:accessed");
    }

    [Fact]
    public void Apps_OrderMostRecentlyUsedFirst_AtPaint()
    {
        // "apps should be ordered by last accessed not by alphabet" — the phone-home rule, applied
        // at PAINT from the viewer's own access log (see MeshSearchScopeTab.SortByAccess).
        var scope = UserActivityLayoutAreas.BuildAppsBand(NodePath, null).ScopeTabs![0];

        scope.SortByAccess.Should().BeTrue();
    }

    [Fact]
    public void Apps_AreRearrangeableByDragAndDrop_InGroupsTheRecordsThemselvesCarry()
    {
        // "group the applications into different groups on the user screen and allow for
        // re-ordering via drag and drop (similar to iPhone)" — the grid is Sortable, its sections
        // are the records' own App.Group, and a drop writes order/group back onto the records.
        var apps = UserActivityLayoutAreas.BuildAppsBand(NodePath, null);

        apps.Sortable.Should().Be(true);
        apps.Grouping!.GroupByProperty.Should().Be(nameof(App.Group));
        var scope = apps.ScopeTabs![0];
        scope.Sortable.Should().BeTrue("the view reads the per-scope setting while the scope is active");
        scope.SortByAccess.Should().BeTrue("tiles the viewer has never placed keep the phone order behind the placed ones");
    }

    [Fact]
    public void Spaces_AreAnIconGridOfTheViewersSpaces_RecentlyUsedFirst()
    {
        // "can we also integrate spaces in this? recently used? show an icon for each space?" — and
        // now in a band of their own, below the apps (policy home-is-composer-apps-spaces).
        var band = UserActivityLayoutAreas.BuildSpacesBand(NodePath, null, ["Globex", "Initech"], ["Acme", "Contoso"]);

        band.Title!.ToString().Should().Be("Spaces");
        band.NavigateToMainNode.Should().Be(true);
        var spaces = band.ScopeTabs!.Should().ContainSingle().Subject;
        spaces.RenderMode.Should().Be(nameof(MeshSearchRenderMode.Icons), "an icon for each space");
        spaces.NavigateToMainNode.Should().BeTrue();
        spaces.SortByAccess.Should().BeTrue("recently used first, from the viewer's own access log");
        spaces.Sortable.Should().BeFalse(
            "the arrangement lives on App records — a space has none, so a drop would have nowhere to write");
        band.HiddenQuery!.ToString().Should().Be(spaces.Query);

        var legs = spaces.Query.Split('\n');
        legs.Should().HaveCount(2, "the viewer's space manifest, then the spaces the viewer was invited into");
        legs[0].Should().Contain("nodeType:Space").And.Contain("path:Globex|Initech");
        legs[1].Should().Contain("path:Acme|Contoso");
        spaces.Query.Should().NotContain(ParsedQuery.CrossPartitionQualifier,
            "both legs are anchored on the profile's manifests — no mesh-wide read on the home");

        UserActivityLayoutAreas.SpacesQuery(NodePath, ["Globex"], null).Should().NotContain("\n",
            "with no invitations there is only the manifest leg");
        UserActivityLayoutAreas.SpacesQuery(NodePath, ["Globex"], []).Should().NotContain("\n");
    }

    [Fact]
    public void Apps_RendersTheIconGridFromTheQueryRows_NavigatingToTheApp()
    {
        // "should load from mesh, icon should be the icon of the app, then it should render
        // insta" — Icons mode paints tiles from the rows (no per-record hub, no content), and
        // NavigateToMainNode makes a tile open the APP (the record's MainNode), not the record.
        var apps = UserActivityLayoutAreas.BuildAppsBand(NodePath, null);

        apps.RenderMode.Should().Be(MeshSearchRenderMode.Icons);
        var scope = apps.ScopeTabs![0];
        scope.RenderMode.Should().Be(nameof(MeshSearchRenderMode.Icons));
        scope.NavigateToMainNode.Should().Be(true);
        scope.ItemArea.Should().BeNull("a per-record tile area meant one hub activation per result");
    }

    // ── Default-app records: the platform BOOTSTRAP (everything else is the STORE's) ────────────

    [Fact]
    public void AppRecordSpecs_DefaultsCarryProductNamesAndIcons_ThreadsIsAnOrdinaryRecord()
    {
        var specs = UserActivityLayoutAreas.AppRecordSpecs(new HomeConfig(), NodePath);

        specs.Select(s => s.Id).Should().Equal("Store", "Doc", "Chat");
        specs.Single(s => s.Id == "Store").Name.Should().Be("Store");
        specs.Single(s => s.Id == "Doc").Name.Should().Be("Docs", "a tile label fits the 88px tile on one line");
        var threads = specs.Single(s => s.Id == "Chat");
        threads.Name.Should().Be("Threads");
        // Inline artwork now, not a static glyph URL — the grey chat.svg read as unfinished beside
        // the Store's tile. Asserted by shape rather than by markup so the artwork can be redrawn
        // without touching this test; AppIconRenderingTest pins the rules the markup must obey.
        threads.Icon.Should().StartWith("<svg");
        threads.OpenPath.Should().Be($"{NodePath}/Chat", "the Threads record opens the viewer's own Chat area");
        threads.Plugin.Should().BeNull();
        threads.Target.Should().Be($"{NodePath}/Chat");
    }

    [Fact]
    public void AppRecordSpecs_CoverTheDefaultsOnly_InstalledAppsBelongToTheStore()
    {
        // Core no longer reads the Store's install manifests: WHAT a viewer has installed is the
        // Store's to record when it installs it. Only the platform defaults are seeded here — the
        // bootstrap that keeps a brand-new home from being a blank screen with no way to the Store.
        var specs = UserActivityLayoutAreas.AppRecordSpecs(
            new HomeConfig { DefaultApps = ["Store", "Chess"] }, NodePath);

        specs.Select(s => s.Id).Should().Equal("Store", "Chess");
        specs.Should().OnlyContain(s => s.Source == "default");
        var chess = specs.Single(s => s.Id == "Chess");
        chess.Plugin.Should().Be("Chess");
        chess.Target.Should().Be("Chess", "a tile opens the APP, never the record");
        chess.Icon.Should().Be(UserActivityLayoutAreas.GenericAppIcon,
            "core must not guess a third-party app's icon — the Store stamps the real one on install");
    }

    [Fact]
    public void BuildAppRecord_PutsTheWholeTileOnTheNode()
    {
        // Name, Icon and MainNode live on the NODE, so the grid paints from query rows alone.
        var spec = UserActivityLayoutAreas.AppRecordSpecs(new HomeConfig(), NodePath)
            .Single(s => s.Id == "Store");

        var node = UserActivityLayoutAreas.BuildAppRecord(NodePath, spec);

        node.Path.Should().Be($"{NodePath}/_App/Store");
        node.NodeType.Should().Be(AppNodeType.NodeType);
        node.Name.Should().Be("Store");
        node.Icon.Should().Be("/static/NodeTypeIcons/shopping-bag.svg");
        node.MainNode.Should().Be("Store", "a tile opens the APP, never the record");
    }

    // ── Config ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HomeConfig_ShippedDefaults_TabbedWithStoreDocAndThreads()
    {
        HomeConfigNodeType.Defaults.Style.Should().Be(HomeStyle.Tabs);
        HomeConfigNodeType.Defaults.DefaultApps.Should().Equal("Store", "Doc", "~/Chat");
    }
}
