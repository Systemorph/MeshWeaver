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

    private static MeshSearchControl Content(UiControl home, string? locale = null) =>
        UserActivityLayoutAreas.BuildContentSection(NodePath, null, null, locale, null);

    private static string[] ScopeLabels(MeshSearchControl search) =>
        search.ScopeTabs!.Select(t => t.Label).ToArray();

    // ── Structure: TWO sections, apps first ────────────────────────────────────────────────────

    [Fact]
    public void Home_IsAppsThenContent_TwoSections()
    {
        // "separate content from apps in two section … apps first, then content": launching an app
        // and searching for content are different acts, so they are different sections — not two
        // lenses on one tab strip.
        var home = UserActivityLayoutAreas.BuildHome(NodePath);

        var stack = home.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCount(2, "apps on top, content below");
    }

    [Fact]
    public void Home_WithShares_StaysTwoSections_AndFoldsThemIntoAll()
    {
        // "Shared with me is not required" — but the items are. Cross-partition invitations
        // (#385) are unreachable by the scope queries, so dropping the band without folding them
        // into All would have silently lost every invitation.
        var home = UserActivityLayoutAreas.BuildHome(NodePath, sharedTargets: ["OrgA/Module"]);

        home.Should().BeOfType<StackControl>().Subject
            .Areas.Should().HaveCount(2, "apps and content — no separate shared band any more");

        var all = UserActivityLayoutAreas
            .BuildContentSection(NodePath, null, null, null, null, ["OrgA/Module"])
            .ScopeTabs!.Single(t => t.Label == "All");
        all.Query.Should().Contain("path:OrgA/Module",
            "a shared module is content the viewer can reach — it belongs IN the list");
    }

    [Fact]
    public void Content_NoPins_LeadsWithAll_AndKeepsMine()
    {
        // "tabs below: as discussed, include All, then Pinned, Mine, Shared". Pinned and Shared are
        // conditional — a pin list with no pins and a path union with no paths both match nothing,
        // so an always-present tab would be an always-empty one. Mine is unconditional: your own
        // space is the one tab that is never empty for you.
        var content = Content(UserActivityLayoutAreas.BuildHome(NodePath));

        ScopeLabels(content).Should().Equal("All", "Mine");
        content.HiddenQuery!.ToString().Should().Contain("is:main")
            .And.Contain("is:content", "the home lists content; it is not the search box")
            .And.Contain("-nodeType:Store/Plugin", "apps live in the Apps section, never twice");
    }

    [Fact]
    public void Content_AtTheRootLevel_ListsSpacesAndNothingElse()
    {
        // 🚨 THE regression guard for the deny-list this replaced. On memex.meshweaver.cloud the
        // home listed `Posts` under a "Posts Hubs" heading (a SocialMedia/PostsHub partition root)
        // and `Event` under "Event Hubs": the root leg excluded Store/Plugin, Store/Catalog and
        // User, so every OTHER root type leaked in and minted its own type group. A deny-list is
        // only as complete as the last person who remembered to extend it, so the root leg is an
        // ALLOW-list now — a new root NodeType has to opt in, not remember to opt out.
        var content = UserActivityLayoutAreas.BuildContentSection(NodePath, null, null, null, null);

        var all = content.ScopeTabs!.Single(t => t.Label == "All");
        foreach (var sort in all.SortOptions!)
        {
            var rootLeg = sort.Query.Split('\n')
                .Single(leg => leg.StartsWith("namespace: ", StringComparison.Ordinal));
            rootLeg.Should().Contain("nodeType:Space",
                "the root level of the home list is the workspaces you can reach");
            rootLeg.Should().NotContain("-nodeType:",
                "an allow-list subsumes every exclusion; a leftover deny term is dead weight that reads as coverage");
        }
    }

    [Fact]
    public void Content_OwnPartitionLeg_IsNotTypeFiltered()
    {
        // The Spaces filter is the ROOT leg's. Your own home partition holds no Spaces at all, so
        // applying it there would empty the list of everything you ever authored. What keeps app
        // chrome ("{Package} — my workspace") out of it is `is:content` honouring the node's own
        // ExcludeFromContext — a property of the node, not a term in this query.
        var content = UserActivityLayoutAreas.BuildContentSection(NodePath, null, null, null, null);
        var mine = content.ScopeTabs!.Single(t => t.Label == "Mine");

        mine.Query.Should().Contain($"namespace:{NodePath}").And.Contain("is:content");
        mine.Query.Should().NotContain("nodeType:Space");
    }

    [Fact]
    public void Content_FansOutByNodeType_BiggestGroupFirst()
    {
        // "All and then fan out in different types, sorted by frequency … get the top level
        // partition node type and bring category by this ⇒ then you get Spaces, Clients, …":
        // the categories are whatever types the viewer's own top-level nodes have, not a taxonomy
        // the home invents, and the type you have most of leads.
        var content = Content(UserActivityLayoutAreas.BuildHome(NodePath));

        content.RenderMode.Should().Be(MeshSearchRenderMode.Grouped);
        content.Grouping!.GroupByProperty.Should().Be("NodeType");
        content.GroupByFrequency.Should().Be(true);
        content.Sections!.ShowCounts.Should().Be(true, "a frequency order is only readable with counts");
    }

    [Fact]
    public void Content_WithPins_AllLeads_PinnedFollows()
    {
        // "the pinned i would still keep … as separate tab if we have any".
        var content = UserActivityLayoutAreas.BuildContentSection(
            NodePath, null, new User { PinnedPaths = ["Doc/GUI"] }, null, null);

        // "put All first and default tab": the view activates scopes[0], so All leads and the
        // narrower lenses follow it, in the order asked for — Pinned, Mine, then Shared.
        ScopeLabels(content).Should().Equal("All", "Pinned", "Mine");
        content.ScopeTabs![1].Query.Should().Contain("Doc/GUI");
        content.HiddenQuery!.ToString().Should().Be(content.ScopeTabs![0].Query,
            "the control-level fallback IS the default tab's query");
    }

    [Fact]
    public void Content_HasTheSearchBar_AppsDoesNot()
    {
        // The apps section is a LAUNCHER (no search box, no view options); content is where you
        // search, and its scopes share that one bar.
        var content = Content(UserActivityLayoutAreas.BuildHome(NodePath));
        content.ShowSearchBox.Should().Be(true);
        content.HiddenQuery!.ToString().Should().Be(content.ScopeTabs![0].Query);

        UserActivityLayoutAreas.BuildAppsBand(NodePath, null).ShowSearchBox.Should().Be(false);
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
            UserActivityLayoutAreas.BuildContentSection(
                NodePath, null, new User { PinnedPaths = ["Doc/GUI"] }, null, null),
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
        var scope = UserActivityLayoutAreas.BuildAppsBand(NodePath, null).ScopeTabs!.Single();

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
        var scope = apps.ScopeTabs!.Single();
        scope.Sortable.Should().BeTrue("the view reads the per-scope setting while the scope is active");
        scope.SortByAccess.Should().BeTrue("tiles the viewer has never placed keep the phone order behind the placed ones");
    }

    [Fact]
    public void Apps_RendersTheIconGridFromTheQueryRows_NavigatingToTheApp()
    {
        // "should load from mesh, icon should be the icon of the app, then it should render
        // insta" — Icons mode paints tiles from the rows (no per-record hub, no content), and
        // NavigateToMainNode makes a tile open the APP (the record's MainNode), not the record.
        var apps = UserActivityLayoutAreas.BuildAppsBand(NodePath, null);

        apps.RenderMode.Should().Be(MeshSearchRenderMode.Icons);
        var scope = apps.ScopeTabs!.Single();
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
        specs.Single(s => s.Id == "Doc").Name.Should().Be("Documentation");
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
