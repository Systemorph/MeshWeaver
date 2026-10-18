using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The default index core derives for a markdown page (<see cref="DefaultNodeNavigation"/>).
///
/// <para>Reported 2026-09-14 on <c>Infrastructure/Inference</c>: the root page listed its
/// sub-pages, and clicking any of them lost the list — the index was the page's OWN children, and
/// a sub-page has none. The index is now the tree under the page's <see cref="DefaultNodeNavigation.IndexRoot"/>,
/// the same on every page of the tree, with the page being read marked as current. Each rule below
/// is a case here, on the pure <see cref="DefaultNodeNavigation.Build(string, System.Collections.Generic.IReadOnlyCollection{MeshWeaver.Mesh.MeshNode}, string)"/>.</para>
/// </summary>
public class DefaultNodeNavigationTest
{
    private const string Root = "Infrastructure/Inference";

    private static MeshNode Node(string path, string? name = null, int? order = null)
    {
        var i = path.LastIndexOf('/');
        var id = i < 0 ? path : path[(i + 1)..];
        var ns = i < 0 ? "" : path[..i];
        return new MeshNode(id, ns) { Name = name ?? id, Order = order };
    }

    /// <summary>The Inference tree: the root, three sub-pages, one of them with a sub-page of its own.</summary>
    private static IReadOnlyCollection<MeshNode> Tree() =>
    [
        Node(Root, "AI Inference"),
        Node(Root + "/Consumption", "Consumption", 1),
        Node(Root + "/HardwareOptions", "Hardware Options", 2),
        Node(Root + "/HardwareOptions/Gpus", "GPU tiers"),
        Node(Root + "/CapacityModel", "Capacity Model"),
        Node(Root + "/_Access", "_Access"),
        Node(Root + "/HardwareOptions/_Thread", "_Thread"),
    ];

    [Fact]
    public void TheIndexRootIsTheSpace()
    {
        DefaultNodeNavigation.IndexRoot("Infrastructure/Inference/Consumption").Should().Be("Infrastructure");
        DefaultNodeNavigation.IndexRoot("Infrastructure/Inference/HardwareOptions/Gpus").Should().Be("Infrastructure");
        DefaultNodeNavigation.IndexRoot("Infrastructure/Options").Should().Be("Infrastructure",
            "a page directly under the space is a chapter of the space's document, not a document of its own (2026-09-15)");
        DefaultNodeNavigation.IndexRoot("Infrastructure").Should().Be("Infrastructure",
            "the space's own root page shows the same index");
    }

    /// <summary>The Infrastructure space: chapters at the top, one of them with sub-pages.</summary>
    private static IReadOnlyCollection<MeshNode> Space() =>
    [
        Node("Infrastructure", "Infrastructure"),
        Node("Infrastructure/Options", "Options & Decisions", 1),
        Node("Infrastructure/Inference", "AI Inference", 2),
        Node("Infrastructure/Inference/Inceptron", "Inceptron"),
        Node("Infrastructure/Inference/Consumption", "Consumption"),
        Node("Infrastructure/Platform", "Platform", 3),
        Node("Infrastructure/Platform/Providers", "Providers"),
    ];

    [Fact]
    public void APageDirectlyUnderTheSpaceShowsTheSpaceIndexWithItselfMarked()
    {
        var nav = DefaultNodeNavigation.Build("Infrastructure", Space(), "Infrastructure/Options")!;

        nav.Title.Should().Be("Infrastructure");
        nav.TitlePath.Should().Be("Infrastructure");
        nav.Entries.Select(e => e.Path).Should().Equal(
            "Infrastructure/Options", "Infrastructure/Inference", "Infrastructure/Platform");
        nav.Entries.Single(e => e.Path == "Infrastructure/Options").IsCurrent.Should().BeTrue();
        nav.Entries.Where(e => e.Path != "Infrastructure/Options").Should().OnlyContain(e => !e.IsCurrent);
        nav.Entries.Single(e => e.Path == "Infrastructure/Inference").Children.Should().HaveCount(2,
            "a chapter's sub-pages travel with the index; whether the group is open is the rail's decision (SuppliedNavigationRail.Plan)");
    }

    [Fact]
    public void ADeepPageShowsTheSpaceIndexWithItsChapterOpenAndItselfMarked()
    {
        var nav = DefaultNodeNavigation.Build("Infrastructure", Space(), "Infrastructure/Inference/Inceptron")!;

        var inference = nav.Entries.Single(e => e.Path == "Infrastructure/Inference");
        inference.IsCurrent.Should().BeFalse("the reader is on a page inside the chapter, not on the chapter");
        inference.Children.Select(c => c.Path).Should().Equal(
            "Infrastructure/Inference/Consumption", "Infrastructure/Inference/Inceptron");
        inference.Children.Single(c => c.Path == "Infrastructure/Inference/Inceptron").IsCurrent.Should().BeTrue();
        nav.Entries.Single(e => e.Path == "Infrastructure/Options").IsCurrent.Should().BeFalse();
        nav.Entries.Single(e => e.Path == "Infrastructure/Platform").Children.Should().ContainSingle(c => c.Path == "Infrastructure/Platform/Providers");
    }

    [Fact]
    public void ASubPageKeepsTheIndexWithItselfMarkedCurrent()
    {
        var nav = DefaultNodeNavigation.Build(Root, Tree(), Root + "/Consumption")!;

        nav.Title.Should().Be("AI Inference", "the heading names the tree, not the page");
        nav.TitlePath.Should().Be(Root, "…and links back to its root");
        nav.Entries.Select(e => e.Path).Should().Equal(
            Root + "/Consumption", Root + "/HardwareOptions", Root + "/CapacityModel");
        nav.Entries.Single(e => e.IsCurrent).Path.Should().Be(Root + "/Consumption");
    }

    [Fact]
    public void TheIndexIsTheSameWhereverTheReaderStands()
    {
        var shapes = new[] { Root, Root + "/Consumption", Root + "/HardwareOptions/Gpus" }
            .Select(p => Paths(DefaultNodeNavigation.Build(Root, Tree(), p)!.Entries).ToList())
            .ToList();

        shapes.Should().OnlyContain(paths => paths.SequenceEqual(shapes[0]),
            "standing somewhere else moves the marker — it never re-scopes the index");
    }

    [Fact]
    public void AnEntryWithSubPagesCarriesThemAsNestedEntries()
    {
        var nav = DefaultNodeNavigation.Build(Root, Tree(), Root + "/HardwareOptions/Gpus")!;
        var hardware = nav.Entries.Single(e => e.Path == Root + "/HardwareOptions");

        hardware.Children.Select(c => c.Path).Should().Equal(Root + "/HardwareOptions/Gpus");
        hardware.Children.Single().IsCurrent.Should().BeTrue("a page two levels down still has its line");
        nav.Entries.Single(e => e.Path == Root + "/Consumption").Children.Should().BeEmpty();
    }

    [Fact]
    public void OrderThenNameAtEveryLevel()
    {
        var nav = DefaultNodeNavigation.Build(Root, Tree(), Root)!;

        nav.Entries.Select(e => e.Label).Should().Equal(
            new[] { "Consumption", "Hardware Options", "Capacity Model" },
            "declared Order first (1, 2), the unordered entry last — never alphabetical across them");
    }

    [Fact]
    public void InternalSatellitesAreLeftOut()
    {
        var nav = DefaultNodeNavigation.Build(Root, Tree(), Root)!;

        Paths(nav.Entries).Should().NotContain(p => p.Contains("/_"),
            "_Access, _Thread and their kind are plumbing, not pages");
    }

    [Fact]
    public void OnTheRootNothingIsCurrentButTheHeadingLinksToIt()
    {
        var nav = DefaultNodeNavigation.Build(Root, Tree(), Root)!;

        Paths(nav.Entries).Should().NotContain(Root, "the root is the heading, not an entry of itself");
        nav.Entries.Should().OnlyContain(e => !e.IsCurrent);
        nav.TitlePath.Should().Be(Root, "the rail marks the heading current instead (SuppliedNavigationRail)");
    }

    [Fact]
    public void ATreeWithNothingToIndexHasNoRail()
    {
        DefaultNodeNavigation.Build(Root, [Node(Root, "Alone")], Root).Should().BeNull(
            "a childless page directly under the partition renders with no rail, as it always did");
        DefaultNodeNavigation.Build(Root, [], Root).Should().BeNull("nothing answered yet");
    }

    [Fact]
    public void AMissingRootNodeStillGetsAHeading()
    {
        var nav = DefaultNodeNavigation.Build(Root, Tree().Where(n => n.Path != Root).ToList(), Root + "/Consumption")!;

        nav.Title.Should().Be("Inference", "the last segment stands in for a root that did not arrive");
        nav.Entries.Should().HaveCount(3);
    }

    /// <summary>
    /// End to end through the rail: a sub-page's rail has the heading as a link, the current
    /// sub-page as an ACTIVE link, and the entry with children as a group that is open only when
    /// the reader is inside it.
    /// </summary>
    [Fact]
    public void ThroughTheRailTheSubPageIsAnActiveLinkInTheTree()
    {
        var current = Root + "/HardwareOptions/Gpus";
        var rail = SuppliedNavigationRail.Plan(DefaultNodeNavigation.Build(Root, Tree(), current)!, current);

        rail.Home.Href.Should().Be("/" + Root);
        rail.Home.IsCurrent.Should().BeFalse();
        var hardware = rail.Items.OfType<SuppliedNavigationRail.RailGroup>().Single();
        hardware.Expanded.Should().BeTrue("the reader is inside it");
        hardware.Items.OfType<SuppliedNavigationRail.RailLink>().Single(l => l.IsCurrent).Path.Should().Be(current);
        rail.Items.OfType<SuppliedNavigationRail.RailLink>().Select(l => l.Path)
            .Should().Equal(Root + "/Consumption", Root + "/CapacityModel");
    }

    // ── the change feed ──────────────────────────────────────────────────────────────────────

    private static QueryResultChange<MeshNode> Change(QueryChangeType type, params MeshNode[] items)
        => new() { ChangeType = type, Items = items };

    /// <summary>
    /// The query is a change feed, and only Initial/Reset carry the whole tree. Projecting each
    /// change's Items straight into the index read as correct on the first emission and rebuilt
    /// the rail from ONE row at the first live change (Copilot on #4321).
    /// </summary>
    [Fact]
    public void ALiveChangeFoldsIntoTheWholeTreeInsteadOfReplacingIt()
    {
        var tree = DefaultNodeNavigation.Fold(ImmutableDictionary<string, MeshNode>.Empty,
            Change(QueryChangeType.Initial, [.. Tree()]));
        tree.Should().HaveCount(Tree().Count);

        // One page renamed: the feed carries that one row.
        var renamed = Node(Root + "/Consumption", "Consumption, revised", 1);
        tree = DefaultNodeNavigation.Fold(tree, Change(QueryChangeType.Updated, renamed));
        tree.Should().HaveCount(Tree().Count, "an update replaces a row, it does not shrink the tree");
        tree[Root + "/Consumption"].Name.Should().Be("Consumption, revised");

        // One page added, one removed.
        tree = DefaultNodeNavigation.Fold(tree, Change(QueryChangeType.Added, Node(Root + "/Glossary", "Glossary")));
        tree = DefaultNodeNavigation.Fold(tree, Change(QueryChangeType.Removed, Node(Root + "/CapacityModel")));
        var nav = DefaultNodeNavigation.Build(Root, tree.Values.ToList(), Root)!;
        Paths(nav.Entries).Should().Contain(Root + "/Glossary").And.NotContain(Root + "/CapacityModel");
        nav.Entries.Should().HaveCount(3);

        // A reset starts over from what it carries.
        tree = DefaultNodeNavigation.Fold(tree, Change(QueryChangeType.Reset, Node(Root, "AI Inference")));
        tree.Should().HaveCount(1);
    }

    // ── the page never waits for the index ───────────────────────────────────────────────────

    /// <summary>
    /// Core CD #8599 (2026-09-14): the first set this index shipped in timed out two read-view
    /// tests that had passed on the set before — on that mesh the subtree query never answered,
    /// and the Overview's CombineLatest waited for it. The guard makes the page independent of
    /// the query's health: an immediate null, then the index whenever it comes.
    /// </summary>
    [Fact]
    public void ASilentQueryStillLetsThePageRender()
    {
        var silent = new Subject<NodeNavigation?>();
        var seen = new List<NodeNavigation?>();

        using var _ = DefaultNodeNavigation.Guard(silent, _ => Assert.Fail("nothing faulted")).Subscribe(seen.Add);

        seen.Should().Equal([null], "the page renders without its index before the query has answered");

        var index = new NodeNavigation("Docs", [new NodeNavigationEntry("A", "Docs/A")]);
        silent.OnNext(index);
        seen.Should().HaveCount(2);
        seen[1].Should().BeSameAs(index, "…and gets the index when the query answers");
    }

    [Fact]
    public void AFaultingQueryIsLoggedAndReadAsNoIndex()
    {
        var faults = new List<Exception>();
        var boom = new InvalidOperationException("the provider refused the query");

        // Observable.Throw faults synchronously on subscribe, so a plain subscription collects the
        // whole sequence — no blocking bridge (the test ratchet refuses .Wait()/.Result on an observable).
        var seen = new List<NodeNavigation?>();
        using var _ = DefaultNodeNavigation.Guard(Observable.Throw<NodeNavigation?>(boom), faults.Add).Subscribe(seen.Add);

        seen.Should().Equal([null, null], "the immediate null, then 'no index' for the fault — never an error into the page");
        faults.Should().Equal([boom], "the fault is seen (logged) before it is swallowed");
    }

    [Fact]
    public void TheIndexLoadsOnlyTheLevelsOnTheReadersPath()
    {
        // "don't eager load the whole thing": the root's children, each ancestor's, the page's own.
        DefaultNodeNavigation.PathLevels("Infrastructure", "Infrastructure/Inference/Inceptron")
            .Should().Equal("Infrastructure", "Infrastructure/Inference", "Infrastructure/Inference/Inceptron");
        DefaultNodeNavigation.PathLevels("Infrastructure", "Infrastructure")
            .Should().Equal("Infrastructure");
        DefaultNodeNavigation.PathLevels("Infrastructure", "Elsewhere/Page")
            .Should().Equal("Infrastructure");
    }

    [Fact]
    public void ALevelWithMoreEndsWithLoadMore_AndMoreRaisesOnlyItsLevel()
    {
        var more = ImmutableDictionary<string, string>.Empty
            .Add("Infrastructure", "Infrastructure/Options?more=Infrastructure:100");
        var nav = DefaultNodeNavigation.Build("Infrastructure", Space(), "Infrastructure/Options", more, "Mehr laden")!;

        nav.Entries.Last().Label.Should().Be("Mehr laden", "the label is the viewer's, passed in by the page");
        nav.Entries.Last().Path.Should().Be("Infrastructure/Options?more=Infrastructure:100");
        nav.Entries.Single(e => e.Path == "Infrastructure/Inference").Children
            .Should().NotContain(e => e.Label == "Mehr laden", "only the level that holds more gets the entry");

        // The value arrives DECODED (LayoutAreaReference.ParseParameters): a separator is a '/', and
        // a literal escape in a level name stays as it is.
        DefaultNodeNavigation.ParseMore("Infrastructure/Inference:100").Should().Be(("Infrastructure/Inference", 100));
        DefaultNodeNavigation.ParseMore("Odd%2FName:100").Should().Be(("Odd%2FName", 100));
        DefaultNodeNavigation.ParseMore($"Infrastructure:{DefaultNodeNavigation.LevelCap}").Should().BeNull("never below the first batch");
        DefaultNodeNavigation.ParseMore("garbage").Should().BeNull();
        DefaultNodeNavigation.ParseMore(null).Should().BeNull();
        DefaultNodeNavigation.ParseMore("X:999999")!.Value.Cap.Should().Be(DefaultNodeNavigation.MaxCap, "bounded");
    }

    [Fact]
    public void TheCapIsAppliedInTheRenderedOrder_NeverInTheStoresRowOrder()
    {
        // An Order = 1 page whose path sorts LAST, behind hidden satellites that sort first: it must
        // lead the level, and the satellites must not use up the budget.
        ImmutableList<MeshNode> level =
        [
            Node("S/_Access", "internal"), Node("S/_Comment", "internal"),
            Node("S/Alpha", "Alpha"), Node("S/Beta", "Beta"), Node("S/Zulu", "Zulu", 1),
        ];

        DefaultNodeNavigation.VisibleInOrder(level).Select(n => n.Path)
            .Should().Equal("S/Zulu", "S/Alpha", "S/Beta");
    }

    [Fact]
    public void TheReadersOwnBranch_SurvivesTheCut()
    {
        // Sixty siblings; the reader is on the 60th, past the cut, and on its child.
        var siblings = Enumerable.Range(1, 60).Select(i => Node($"S/Item{i:D3}")).ToImmutableList();
        var page = Node("S/Item060");
        var child = Node("S/Item060/Detail");
        var rows = DefaultNodeNavigation.RenderedRows(
            [(DefaultNodeNavigation.VisibleInOrder(siblings), DefaultNodeNavigation.LevelCap),
             (DefaultNodeNavigation.VisibleInOrder([child]), DefaultNodeNavigation.LevelCap)],
            [page, child, Node("S")]);

        rows.Select(n => n.Path).Should().Contain("S/Item060", "the page the reader is on is never cut");
        rows.Count(n => n.Namespace == "S").Should().Be(DefaultNodeNavigation.LevelCap + 1,
            "the level still shows its first LevelCap, plus the reader's own entry");

        var nav = DefaultNodeNavigation.Build("S", rows, "S/Item060/Detail")!;
        Paths(nav.Entries).Should().Contain("S/Item060/Detail",
            "the reader's child hangs from its parent instead of being orphaned");
    }

    private static MeshNode Stamped(string path, string name, int? order, int minutesAgo) =>
        Node(path, name, order) with { LastModified = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero).AddMinutes(-minutesAgo) };

    [Fact]
    public void ALevelShowsTenAtFirst()
    {
        DefaultNodeNavigation.LevelCap.Should().Be(10, "the rail loads a short level, then Load more / Show all");
        DefaultNodeNavigation.MoreStep.Should().Be(10);
    }

    [Fact]
    public void TheDefaultOrderIsDeclaredOrderThenMostRecentlyModified()
    {
        ImmutableList<MeshNode> level =
        [
            Stamped("S/Old", "Aardvark", null, 300),
            Stamped("S/Fresh", "Zebra", null, 1),
            Stamped("S/Second", "Mid", 2, 900),
            Stamped("S/First", "Yak", 1, 999),
            Stamped("S/_Access", "internal", null, 0),
        ];

        DefaultNodeNavigation.VisibleInOrder(level).Select(n => n.Path)
            .Should().Equal(["S/First", "S/Second", "S/Fresh", "S/Old"],
                "declared Order first, then newest — never alphabetical (2026-10-18)");
    }

    [Fact]
    public void EachSortModeOrdersTheLevelItsOwnWay()
    {
        ImmutableList<MeshNode> level =
        [
            Stamped("S/B", "Bravo", 1, 50),
            Stamped("S/A", "alpha", null, 10),
            Stamped("S/C", "Charlie", null, 5),
        ];

        DefaultNodeNavigation.SortLevel(level, IndexSort.Modified).Select(n => n.Id).Should().Equal("C", "A", "B");
        DefaultNodeNavigation.SortLevel(level, IndexSort.Name).Select(n => n.Id).Should().Equal(["A", "B", "C"], "case-insensitive");
        DefaultNodeNavigation.SortLevel(level, IndexSort.Order).Select(n => n.Id).Should().Equal("B", "C", "A");
        var viewed = ImmutableDictionary<string, int>.Empty.Add("S/A", 0);
        DefaultNodeNavigation.SortLevel(level, IndexSort.Viewed, viewed).Select(n => n.Id)
            .Should().Equal(["A", "C", "B"], "opened first, then the never-opened newest first");
        DefaultNodeNavigation.SortLevel(level, "future-mode").Select(n => n.Id)
            .Should().Equal(["B", "C", "A"], "an unknown sort renders the default order");
    }

    [Fact]
    public void NameSortsUnnamedEntriesLast_TheWayTheStoreClipsThem()
    {
        // The store sorts lower(name) with NULLS LAST; rendering by `Name ?? Id` would put an unnamed
        // "A" first — a row the clip never read (review on #6947).
        ImmutableList<MeshNode> level =
        [
            new MeshNode("A", "S") { Name = null },
            Node("S/Z1", "Zulu"),
            Node("S/Y1", "yankee"),
        ];

        DefaultNodeNavigation.SortLevel(level, IndexSort.Name).Select(n => n.Id).Should().Equal("Y1", "Z1", "A");
        DefaultNodeNavigation.LevelQueries("S", IndexSort.Name, 10).Should().ContainSingle()
            .Which.Should().Contain("sort:lower(name)-asc");
    }

    [Fact]
    public void ALevelReadsOnlyWhatItShowsPlusOne_OrderedByTheStore_WithoutHiddenRows()
    {
        var order = DefaultNodeNavigation.LevelQueries("S", IndexSort.Order, 10);
        order.Should().HaveCount(2, "the ordered rows and the newest rows (the tie leg is added once the first answers)");
        order.Should().OnlyContain(q => q.Contains("namespace:S ") && q.Contains("limit:11") && q.Contains("-id:_*"),
            "never the whole level, and a hidden `_` row never takes a visible row's place in the window");
        order[0].Should().Contain("sort:order-asc");
        order[1].Should().Contain("sort:lastModified-desc");
        DefaultNodeNavigation.LevelQueries("S", IndexSort.Modified, 20).Should().ContainSingle()
            .Which.Should().Contain("sort:lastModified-desc").And.Contain("limit:21");
        var viewed = DefaultNodeNavigation.LevelQueries("S", IndexSort.Viewed, 10);
        viewed[0].Should().Contain("source:accessed").And.Contain("sort:lastModified-desc",
            "without an explicit sort the merge re-orders an accessed read by score and path");
        viewed[1].Should().Contain("sort:lastModified-desc", "a page never opened is still listed");
        DefaultNodeNavigation.LevelQueries("S", "future-mode", 10).Should().Equal(order, "an unknown sort reads as the default");
    }

    [Fact]
    public void RowsTiedOnTheCutOrder_AreReadNewestFirst()
    {
        // Review on #6947: A01–A11 and Z share Order 1, Z is the newest; eleven still-newer unordered
        // rows. The Order leg (cut by path) keeps A01–A11, the newest leg only unordered rows — Z,
        // which leads the rail, was in neither.
        var orderLeg = Enumerable.Range(1, 11).Select(i => Stamped($"S/A{i:D2}", $"A{i:D2}", 1, 100 + i)).ToList();
        DefaultNodeNavigation.TieOrder(orderLeg, 10).Should().Be(1, "the leg is full, so rows beyond it may share Order 1");
        DefaultNodeNavigation.TieOrder(orderLeg.Take(5), 10).Should().BeNull("a short leg already holds every ordered row");
        DefaultNodeNavigation.TieLegQuery("S", 1, 10).Should().Contain("order:1").And.Contain("sort:lastModified-desc")
            .And.Contain("limit:11").And.Contain("-id:_*");

        var z = Stamped("S/Z", "Z", 1, 1);
        var newest = Enumerable.Range(1, 11).Select(i => Stamped($"S/N{i:D2}", $"N{i:D2}", null, 0)).ToList();
        var tieLeg = new[] { z }.Concat(orderLeg.Take(10)).ToList();
        var rows = DefaultNodeNavigation.LevelRows.Of(IndexSort.Order,
            [Dict(orderLeg), Dict(newest), Dict(tieLeg)]);
        DefaultNodeNavigation.VisibleInOrder(rows.Nodes.Values, IndexSort.Order, null).First().Path
            .Should().Be("S/Z", "the newest of the rows sharing the first Order value leads");
    }

    [Fact]
    public void SortTravelsInTheUrl_AndAnUnknownModeIsKeptNotGuessed()
    {
        foreach (var mode in IndexSort.All)
            DefaultNodeNavigation.ParseSort($"Doc/Architecture:{mode}").Should().Be(("Doc/Architecture", mode));
        DefaultNodeNavigation.ParseSort("Doc:Future").Should().Be(("Doc", "future"), "kept as written, so the page can report it");
        DefaultNodeNavigation.ParseSort(":name").Should().BeNull();
        DefaultNodeNavigation.ParseSort("Doc:").Should().BeNull();
        DefaultNodeNavigation.ParseSort(null).Should().BeNull();
    }

    [Fact]
    public void ShowAllOpensTheLevelInTheMeshSearch()
    {
        var href = DefaultNodeNavigation.ShowAllHref("Doc/Architecture", IndexSort.Order);
        href.Should().StartWith("/search?ns=Doc%2FArchitecture&hq=");
        Uri.UnescapeDataString(href[(href.IndexOf("hq=", StringComparison.Ordinal) + 3)..])
            .Should().Be("namespace:Doc/Architecture is:main -id:_* sort:order-asc", "the level's direct children, not its subtree");
    }

    [Fact]
    public void RecentlyViewedRanksByTheAccessTime_NotByTheFeedsDeltas()
    {
        // Review on #6947: an Updated row (a rename) or an Added one (a backfill after a delete) is
        // not "just opened". The rank comes from the access leg's own timestamps.
        var accessLeg = Dict([Stamped("S/Old", "Old", null, 300), Stamped("S/Recent", "Recent", null, 1)]);
        var plainLeg = Dict([Stamped("S/Old", "Old", null, 0), Stamped("S/Never", "Never", null, 5)]);
        var rows = DefaultNodeNavigation.LevelRows.Of(IndexSort.Viewed, [accessLeg, plainLeg]);

        rows.ViewedRank!["S/Recent"].Should().Be(0);
        rows.ViewedRank!["S/Old"].Should().Be(1);
        rows.ViewedRank.Should().NotContainKey("S/Never");
        rows.Nodes["S/Old"].LastModified.Should().Be(plainLeg["S/Old"].LastModified,
            "the row's own lastModified, not the access time the accessed read projects into it");
        DefaultNodeNavigation.LevelRows.Of(IndexSort.Order, [accessLeg]).ViewedRank.Should().BeNull();
    }

    [Fact]
    public void TheStoreReadsEveryLevelQueryTheWayTheRailMeansIt()
    {
        // A qualifier the parser does not know becomes FREE TEXT — a relevance search that matches
        // nothing like the level. So each query is parsed by the real parser and run by the real
        // in-memory evaluator, not just string-compared.
        var parser = new QueryParser();
        var queries = IndexSort.All.SelectMany(m => DefaultNodeNavigation.LevelQueries("S", m, 10))
            .Append(DefaultNodeNavigation.TieLegQuery("S", 1, 10));
        foreach (var query in queries)
        {
            var parsed = parser.Parse(query);
            parsed.TextSearch.Should().BeNullOrEmpty($"'{query}' must parse to qualifiers only");
            parsed.Limit.Should().Be(11, query);
            parsed.OrderBy.Should().NotBeNull($"'{query}' is clipped, so the store must order it");
        }

        var evaluator = new MeshWeaver.Hosting.Persistence.Query.QueryEvaluator();
        var hidden = Node("S/_Access");
        var tied = Node("S/Tied", order: 1);
        var other = Node("S/Other", order: 2);
        var tie = parser.Parse(DefaultNodeNavigation.TieLegQuery("S", 1, 10));
        evaluator.Matches(hidden, tie).Should().BeFalse("-id:_* keeps internal rows out of the window");
        evaluator.Matches(tied, tie).Should().BeTrue("order:1 matches the tied row");
        evaluator.Matches(other, tie).Should().BeFalse("…and only it");

        var byName = parser.Parse(DefaultNodeNavigation.LevelQueries("S", IndexSort.Name, 10).Single());
        evaluator.OrderResults(
                [new MeshNode("A", "S") { Name = null }, Node("S/Z1", "Zulu"), Node("S/Y1", "yankee")],
                byName.OrderBy!)
            .Select(n => n.Id)
            .Should().Equal(["Y1", "Z1", "A"], "the store's clip order is the rail's render order (SortLevel)");
    }

    private static ImmutableDictionary<string, MeshNode> Dict(IEnumerable<MeshNode> rows)
        => rows.ToImmutableDictionary(n => n.Path, StringComparer.Ordinal);

    [Fact]
    public void ALoadedLevelOffersItsSortDropdown_AndEndsWithLoadMoreAndShowAll()
    {
        NavSortOption[] options = [new("Order", "/Infrastructure?sort=Infrastructure:order", true)];
        var levels = ImmutableDictionary<string, IndexLevel>.Empty
            .Add("Infrastructure", new IndexLevel
            {
                Sort = IndexSort.Name,
                MoreHref = "/Infrastructure/Options?more=Infrastructure:20",
                ShowAllHref = "/search?ns=Infrastructure",
                SortOptions = options,
            })
            .Add("Infrastructure/Inference", new IndexLevel { SortOptions = options });
        var nav = DefaultNodeNavigation.Build("Infrastructure", Space(), "Infrastructure/Inference",
            levels, new IndexLabels("Mehr laden", "Alle anzeigen", m => m))!;

        nav.Entries.Take(3).Select(e => e.Label).Should().Equal(["AI Inference", "Options & Decisions", "Platform"],
            "the level renders in ITS sort — Name here");
        (nav.Entries[^2].Label, nav.Entries[^2].Href).Should().Be(("Mehr laden", "/Infrastructure/Options?more=Infrastructure:20"));
        (nav.Entries[^1].Label, nav.Entries[^1].Href).Should().Be(("Alle anzeigen", "/search?ns=Infrastructure"));
        nav.SortOptions.Should().Equal(options, "the top level's dropdown sits on the menu");
        nav.Entries.Single(e => e.Path == "Infrastructure/Inference").SortOptions.Should().Equal(options,
            "a loaded folder offers the dropdown for its own children");
        nav.Entries.Single(e => e.Path == "Infrastructure/Platform").SortOptions.Should().BeEmpty(
            "a folder off the reader's path is not loaded, so there is nothing to sort");

        var rail = SuppliedNavigationRail.Plan(nav, "Infrastructure/Inference");
        rail.SortOptions.Should().Equal(options);
        rail.Items.OfType<SuppliedNavigationRail.RailGroup>().Single(g => g.Path == "Infrastructure/Inference")
            .SortOptions.Should().Equal(options);
        rail.Items.OfType<SuppliedNavigationRail.RailLink>().Last().Href.Should().Be("/search?ns=Infrastructure",
            "Show all links to the search, not to a node");
    }

    private static IEnumerable<string> Paths(IEnumerable<NodeNavigationEntry> entries)
        => entries.SelectMany(e => new[] { e.Path }.Concat(Paths(e.Children)));
}
