using System.Text.Json;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The reactive half of package-root icon inheritance (#2075 item 2):
/// <see cref="MeshNodeExtensions.ObservePartitionRoot"/>, the seam that fetches the root the pure
/// resolver cannot fetch for itself.
///
/// <para>Against a REAL mesh, because the thing worth pinning is exactly what a hand-written double
/// would assume away: that a package root really does reach a page under it, and that a package root
/// itself opens no read at all.</para>
/// </summary>
public class PartitionRootStreamTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Package = "Chess";
    private const string ChildPath = $"{Package}/Rules";

    /// <summary>An authored package mark — inline svg, the form every store mark takes.</summary>
    private const string PackageMark =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\">"
        + "<rect width=\"24\" height=\"24\" rx=\"5\" fill=\"#1f6feb\"/>"
        + "<path d=\"M8 6h8v3H8z\" fill=\"#fff\"/></svg>";

    private const string UnmarkedPackage = "Draughts";

    /// <summary>
    /// 🚨 <see cref="TestTimeouts"/>, never a literal (#2819). A hand-written <c>30 s</c> is exactly
    /// the framework's own write bound, so a test carrying one gives up ONE SECOND before the mesh
    /// can name what went wrong — and it is a guess about a laptop, on a runner ~1.7× slower.
    ///
    /// <para>Two budgets because two different things are waited on, and both stay strictly below
    /// their test's <c>[Fact(Timeout = …)]</c> so an inner wait loses first and reports what it was
    /// waiting for.</para>
    /// </summary>
    private static TimeSpan ReadBudget => TestTimeouts.Quick;

    /// <summary>A layout-area render round-trip, which settles later than a node read.</summary>
    private static TimeSpan RenderBudget => TestTimeouts.Convergence;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(
                new MeshNode(Package) { Name = "Chess", NodeType = "Space", Icon = PackageMark },
                new MeshNode("Rules", Package) { Name = "Rules", NodeType = "Markdown" },
                // The control: same shape, no mark on the root.
                new MeshNode(UnmarkedPackage) { Name = "Draughts", NodeType = "Space" },
                new MeshNode("Rules", UnmarkedPackage) { Name = "Rules", NodeType = "Markdown" });

    /// <summary>
    /// 🚨 END TO END: the page under the package reads the package's node, and the pure resolver
    /// turns it into the package's mark where it used to produce the generic document glyph.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task APageUnderThePackage_ResolvesThePackagesMark()
    {
        var workspace = Mesh.GetWorkspace();

        var root = await workspace.ObservePartitionRoot(ChildPath)
            .Where(n => n is not null)
            .FirstAsync().Timeout(ReadBudget).Await(TestContext.Current.CancellationToken);
        root!.Path.Should().Be(Package);

        var child = await workspace.GetMeshNodeStream(ChildPath)
            .Where(n => n is not null)
            .FirstAsync().Timeout(ReadBudget).Await(TestContext.Current.CancellationToken);

        // Before: the generic glyph for its type. After: the package's mark.
        MeshNodeImageHelper.ResolveNodeIcon(child).Should().Be("/static/NodeTypeIcons/document.svg");
        MeshNodeImageHelper.ResolveNodeIcon(child, root).Should().Be(PackageMark);
    }

    /// <summary>
    /// It STARTS null, so the page it feeds renders on the node's own stream immediately — the root
    /// read can never delay, or gate, a page that does not depend on it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheStream_StartsWithNothing_SoItCannotGateThePage()
    {
        (await Mesh.GetWorkspace().ObservePartitionRoot(ChildPath)
                .FirstAsync().Timeout(ReadBudget).Await(TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    /// <summary>
    /// 🚨 A PACKAGE ROOT OPENS NO READ. Its own partition root is itself, so the stream is a
    /// constant that completes — never a point read of a node the resolver would then have to
    /// refuse anyway, and never a subscription per page for a value that cannot be used.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ForThePackageRootItself_NothingIsRead()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var emissions = await Mesh.GetWorkspace().ObservePartitionRoot(Package)
            .ToList()
            .FirstAsync().Timeout(ReadBudget).Await();

        emissions.Should().ContainSingle().Which.Should().BeNull();
    }

    /// <summary>
    /// 🚨 THE WIRING, RENDERED. The page header of a node under a MARKED package really does draw
    /// the package's mark, and the identical page under an UNMARKED one keeps its type glyph — the
    /// control arm, without which "the mark is somewhere in the payload" would say nothing about
    /// where it came from.
    ///
    /// <para>This is the transition the change makes: both of these headers used to resolve the same
    /// generic document glyph.</para>
    ///
    /// <para>🚨 It waits for the mark rather than reading the first frame, and that is a REAL
    /// property of the design, not test patience. The partition-root stream starts null so it can
    /// never gate the page, so the header paints its type glyph on the first frame and re-renders
    /// with the package's mark when the root arrives. Asserting on frame one measured the
    /// un-inherited render and reported the wiring as broken — which is exactly what it did on the
    /// first run of this test.</para>
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task TheNodePageHeader_DrawsThePackageMark()
    {
        var marked = await IconTile(ChildPath, MarkPathData, TestContext.Current.CancellationToken);
        marked.Should().Contain(MarkSignature, "the package's mark is what the header's tile draws");

        // The control: same page shape, no mark on its root — it settles on the type glyph, and the
        // other package's mark never reaches it.
        var unmarked = await IconTile($"{UnmarkedPackage}/Rules", DocumentGlyph, TestContext.Current.CancellationToken);
        unmarked.Should().Contain(DocumentGlyph);
        unmarked.Should().NotContain(MarkSignature);
    }

    private const string DocumentGlyph = "/static/NodeTypeIcons/document.svg";

    /// <summary>The mark's path data — the fragment to WAIT for.</summary>
    private const string MarkPathData = "M8 6h8v3H8z";

    /// <summary>The mark's inner markup — what an assertion pins. The tile's <see cref="ImageControl"/>
    /// carries the resolved icon as its SOURCE value, so the authored mark arrives intact.</summary>
    private const string MarkSignature =
        "<rect width=\"24\" height=\"24\" rx=\"5\" fill=\"#1f6feb\"/>"
        + "<path d=\"M8 6h8v3H8z\" fill=\"#fff\"/></svg>";

    /// <summary>The image box the node-page header's 56 px tile draws a picture or svg icon in
    /// (<c>IconTile.Of</c>) — what tells the tile's <see cref="ImageControl"/> from any other image
    /// on the page.</summary>
    private const string IconTileImageSize = "48px";

    /// <summary>
    /// The node's rendered Overview header icon tile, awaited until it carries
    /// <paramref name="awaitedFragment"/>.
    ///
    /// <para>The wait is on the page's WHOLE current tree (<see cref="NodePageAreas.TreeSnapshots"/>),
    /// re-resolved on every frame — never on a sub-area whose key was read from an earlier frame.
    /// The header's inner area names are generated, and the page RE-SHAPES when its index rail
    /// arrives (MeshWeaver#6548): it paints as the page alone, then re-renders as a splitter with
    /// the rail beside the page, and the first frame's child areas are gone for good. The tree is
    /// searched for the tile itself, so the assertion is still about the ICON and not about the
    /// payload at large; an area that renders nothing by design simply contributes nothing.</para>
    /// </summary>
    private async Task<string> IconTile(string nodePath, string awaitedFragment, CancellationToken cancellationToken)
    {
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(nodePath), reference);

        // The tile is an ImageControl (no longer markup): its Source is the resolved icon — a literal
        // on this hub-built header — and its box is the tile's image size.
        var tiles = await stream.TreeSnapshots(reference.Area!)
            .Select(tree => tree.OfType<ImageControl>()
                .Where(image => image.Size?.ToString() == IconTileImageSize)
                .Select(image => image.Source?.ToString() ?? "")
                .ToList())
            .Should().Within(RenderBudget).Match(
                found => found.Any(tile => tile.Contains(awaitedFragment)),
                $"the Overview of '{nodePath}' renders its icon tile with '{awaitedFragment}'", cancellationToken);
        return Assert.Single(tiles!);
    }
}
