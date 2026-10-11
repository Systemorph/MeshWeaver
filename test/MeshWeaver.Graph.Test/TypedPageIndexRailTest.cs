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
            new MeshNode("Beta", Space) { Name = "Beta", NodeType = WidgetType, Content = new RailWidget { Label = "b" } });

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
        // Wait for the level that holds the sibling.
        var railArea = splitter.Areas[0].Area!.ToString()!;
        var rail = await Observable.Interval(TestTimeouts.Quick / 20).StartWith(0L)
            .Select(_ => Observable.FromAsync(() => Collect(stream, railArea, ct)))
            .Concat()
            .Should().Within(TestTimeouts.Convergence)
            .Match(r => r.OfType<NavLinkControl>().Any(l => l.Url?.ToString() == $"/{SiblingPath}"),
                cancellationToken: ct);
        var menu = Assert.Single(rail.OfType<NavMenuControl>());
        Assert.Equal(Space, menu.Skins.OfType<NavMenuSkin>().Single().SearchNamespace);
        Assert.Contains($"/{SiblingPath}", rail.OfType<NavLinkControl>().Select(l => l.Url?.ToString()));

        // The second pane is the page itself: the typed node's header is in there, not in the rail.
        var page = await Collect(stream, splitter.Areas[1].Area!.ToString()!, ct);
        Assert.Contains(page.OfType<StackControl>(),
            s => s.DataContext?.Contains(MeshNodeLayoutAreas.HeaderDataId) == true);
        Assert.Empty(page.OfType<NavMenuControl>());
    }

    [Fact]
    public async Task AnEmbeddedTypedPage_GetsNoRail()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = Open("?showHeader=false");

        var root = await stream.GetControlStream(MeshNodeLayoutAreas.OverviewArea)
            .Should().Within(TestTimeouts.Convergence).Match(c => c is not null, cancellationToken: ct);
        Assert.IsNotType<SplitterControl>(root);
        var all = await Collect(stream, MeshNodeLayoutAreas.OverviewArea, ct);
        Assert.Empty(all.OfType<NavMenuControl>());
    }

    private static async Task<ImmutableList<UiControl>> Collect(
        ISynchronizationStream<JsonElement> stream, string areaName, System.Threading.CancellationToken ct, int depth = 0)
    {
        if (depth > 8)
            return ImmutableList<UiControl>.Empty;
        var control = await stream.GetControlStream(areaName)
            .Should().Within(TestTimeouts.Convergence).Match(c => c != null, cancellationToken: ct);
        if (control is null)
            return ImmutableList<UiControl>.Empty;
        var found = ImmutableList.Create(control);
        if (control is IContainerControl container)
            foreach (var area in container.Areas)
                if (area.Area?.ToString() is { Length: > 0 } name)
                    found = found.AddRange(await Collect(stream, name, ct, depth + 1));
        return found;
    }
}
