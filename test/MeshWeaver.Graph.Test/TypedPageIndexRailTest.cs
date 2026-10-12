using System.Reactive.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A TYPED node's default Overview carries the left-hand index (policy <c>explorer-navigation</c>):
/// the page is wrapped in a splitter whose first pane is the rail — listing the node's neighbours
/// and carrying a search box scoped to the page's space — and whose second pane is the page.
/// An <c>@@</c> embed (<c>?showHeader=false</c>) gets the page alone.
/// </summary>
public class TypedPageIndexRailTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string WidgetType = "RailWidget";
    private const string Space = "RailTyped";
    private const string WidgetPath = "RailTyped/Alpha";
    private const string SiblingPath = "RailTyped/Beta";
    private const string RootPath = "RailRoot";
    private const string RootName = "The Rail Root";
    private const string ReshapeRootPath = "RailReshape";

    /// <summary>The content type the widget NodeType is configured with.</summary>
    public record RailWidget
    {
        /// <summary>A free text field.</summary>
        public string? Label { get; init; }
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) => base.ConfigureMesh(builder)
        .AddMeshNodes(
            new MeshNode(WidgetType)
            {
                Name = "Widget",
                HubConfiguration = config => config.AddMeshDataSource(s => s.WithContentType<RailWidget>()),
            },
            new MeshNode(Space) { NodeType = "Space", Name = Space },
            new MeshNode("Alpha", Space) { Name = "Alpha", NodeType = WidgetType, Content = new RailWidget { Label = "a" } },
            new MeshNode("Beta", Space) { Name = "Beta", NodeType = WidgetType, Content = new RailWidget { Label = "b" } },
            // A typed node AT a partition root, with a child: its own page heads the rail.
            new MeshNode(RootPath) { Name = RootName, NodeType = WidgetType, Content = new RailWidget { Label = "r" } },
            new MeshNode("Leaf", RootPath) { Name = "Leaf", NodeType = WidgetType, Content = new RailWidget { Label = "l" } },
            // A typed partition root with NOTHING under it yet: its page has no index until a test
            // creates a child — the forced re-shape.
            new MeshNode(ReshapeRootPath) { Name = "Reshape Root", NodeType = WidgetType, Content = new RailWidget { Label = "x" } });

    private ISynchronizationStream<JsonElement> Open(string query = "")
    {
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea) { Id = query };
        return GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(WidgetPath), reference);
    }

    [Fact]
    public async Task ATypedPageWithNeighbours_IsWrappedWithTheIndexRail_AndItsSearchIsScopedToTheSpace()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = Open();

        // The page paints first and the index arrives a beat later: wait for the two-pane wrapper.
        var root = await stream.GetControlStream(MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(c => c is SplitterControl { Areas.Count: 2 }, cancellationToken: ct);
        var splitter = Assert.IsType<SplitterControl>(root);

        // The rail paints progressively: the reader's own branch first, each level as it arrives.
        // Wait for the level that holds the sibling — on the rail's tree as it is NOW, re-read on
        // every frame (NodePageAreas), never on child keys read from an earlier one.
        var railArea = splitter.Areas[0].Area!.ToString()!;
        var rail = await stream.TreeSnapshots(railArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(r => r.OfType<NavLinkControl>().Any(l => l.Url?.ToString() == $"/{SiblingPath}"),
                cancellationToken: ct);
        var menu = Assert.Single(rail!.OfType<NavMenuControl>());
        Assert.Equal(Space, menu.Skins.OfType<NavMenuSkin>().Single().SearchNamespace);
        Assert.Contains($"/{SiblingPath}", rail!.OfType<NavLinkControl>().Select(l => l.Url?.ToString()));

        // The second pane is the page itself: the typed node's header is in there, not in the rail.
        var page = await stream.TreeSnapshots(splitter.Areas[1].Area!.ToString()!)
            .Should().Within(TestTimeouts.Convergence)
            .Match(p => p.OfType<StackControl>().Any(IsHeader), "the page pane renders the typed node's header",
                cancellationToken: ct);
        Assert.Empty(page!.OfType<NavMenuControl>());
    }

    /// <summary>
    /// The trap behind MeshWeaver#6548, forced deterministically: a page whose index root has nothing
    /// to list renders WITHOUT a rail; a child created under it makes the index answer, and the page
    /// re-renders as a splitter. A sub-area key read from the first frame names an area of the OLD
    /// tree, so a wait on it after the re-shape never sees the page again — the shape that hung
    /// FutuRe's Overview test on CI. The live walk (<see cref="NodePageAreas.TreeSnapshots"/>) follows
    /// the page into its new pane.
    /// </summary>
    [Fact]
    public async Task AKeyReadFromTheFirstFrame_IsLostWhenTheRailReshapesThePage_TheLiveWalkFollowsThePage()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(ReshapeRootPath), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea));

        // Before: nothing to index, so the page is the area's root — no splitter. Read a child key
        // off this frame exactly as the old helpers did, and prove it resolves to the header NOW
        // (the positive control: the old pattern works as long as the page keeps its shape).
        var first = await stream.GetControlStream(MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(c => c is IContainerControl and not SplitterControl, "the page paints without a rail", cancellationToken: ct);
        var staleKeys = ((IContainerControl)first!).Areas.Select(a => a.Area?.ToString()).OfType<string>().ToImmutableList();
        var headerKey = await staleKeys
            .Select(key => stream.TreeSnapshots(key)
                .Where(tree => tree.OfType<StackControl>().Any(IsHeader))
                .Select(_ => key))
            .Merge()
            .Should().Within(TestTimeouts.Convergence)
            .Match(_ => true, "one of the first frame's child areas carries the header", cancellationToken: ct);

        // The forced re-shape: a child makes the index root non-empty, and the page re-renders.
        await NodeFactory.CreateNode(new MeshNode("Child", ReshapeRootPath)
            {
                Name = "Child",
                NodeType = WidgetType,
                Content = new RailWidget { Label = "c" },
            })
            .Should().Within(TestTimeouts.WriteConvergence).Emit(cancellationToken: ct);
        await stream.GetControlStream(MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(c => c is SplitterControl { Areas.Count: 2 }, "the index answers and the page re-shapes", cancellationToken: ct);

        // Negative control: the old pattern — wait on the key read from the first frame — no longer
        // reaches the header. The area belongs to a tree the page has left.
        await stream.GetControlStream(headerKey!)
            .Where(c => c is StackControl s && IsHeader(s))
            .Should().NotEmit(TestTimeouts.Quick,
                "the first frame's child area is not where the page lives after the re-shape", ct);

        // The helper: the same question asked of the page's CURRENT tree finds the header in the
        // splitter's page pane.
        var tree = await stream.TreeSnapshots(MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(t => t.OfType<SplitterControl>().Any() && t.OfType<StackControl>().Any(IsHeader),
                "the live walk follows the page into the splitter's page pane", cancellationToken: ct);
        Assert.Single(tree!.OfType<StackControl>(), IsHeader);
    }

    private static bool IsHeader(StackControl stack)
        => stack.DataContext?.Contains(MeshNodeLayoutAreas.HeaderDataId) == true;

    [Fact]
    public async Task OnTheRootsOwnPage_TheRailHeadingCarriesTheRootsName()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(RootPath), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea));
        var root = await stream.GetControlStream(MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(c => c is SplitterControl { Areas.Count: 2 }, cancellationToken: ct);
        var railArea = ((SplitterControl)root!).Areas[0].Area!.ToString()!;
        var rail = await stream.TreeSnapshots(railArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(r => r.OfType<NavLinkControl>().Any(l => l.Title?.ToString() == RootName),
                cancellationToken: ct);
        Assert.Contains(rail!.OfType<NavLinkControl>(), l => l.Title?.ToString() == RootName);
    }

    [Fact]
    public async Task AnEmbeddedTypedPage_GetsNoRail()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = Open("?showHeader=false");

        var root = await stream.GetControlStream(MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(TestTimeouts.Convergence).Match(c => c is not null, cancellationToken: ct);
        Assert.IsNotType<SplitterControl>(root);
        var all = await stream.TreeSnapshots(MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(t => t.Count > 1, "the embedded page renders its content below the root", cancellationToken: ct);
        Assert.Empty(all!.OfType<NavMenuControl>());
    }
}
