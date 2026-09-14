using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
/// is a case here, on the pure <see cref="DefaultNodeNavigation.Build"/>.</para>
/// </summary>
public class DefaultNodeNavigationTest
{
    private const string Root = "Infrastructure/Inference";

    private static MeshNode Node(string path, string? name = null, int? order = null)
    {
        var i = path.LastIndexOf('/');
        return new MeshNode(path[(i + 1)..], path[..i]) { Name = name ?? path[(i + 1)..], Order = order };
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
    public void TheIndexRootIsTheSecondSegment()
    {
        DefaultNodeNavigation.IndexRoot("Infrastructure/Inference/Consumption").Should().Be(Root);
        DefaultNodeNavigation.IndexRoot("Infrastructure/Inference/HardwareOptions/Gpus").Should().Be(Root);
        DefaultNodeNavigation.IndexRoot(Root).Should().Be(Root, "a page one level below the partition is its own root");
        DefaultNodeNavigation.IndexRoot("Infrastructure").Should().Be("Infrastructure",
            "a partition root has nothing above it to index from");
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

    private static IEnumerable<string> Paths(IEnumerable<NodeNavigationEntry> entries)
        => entries.SelectMany(e => new[] { e.Path }.Concat(Paths(e.Children)));
}
