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
            .FirstAsync().Timeout(ReadBudget);
        root!.Path.Should().Be(Package);

        var child = await workspace.GetMeshNodeStream(ChildPath)
            .Where(n => n is not null)
            .FirstAsync().Timeout(ReadBudget);

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
        => (await Mesh.GetWorkspace().ObservePartitionRoot(ChildPath)
                .FirstAsync().Timeout(ReadBudget))
            .Should().BeNull();

    /// <summary>
    /// 🚨 A PACKAGE ROOT OPENS NO READ. Its own partition root is itself, so the stream is a
    /// constant that completes — never a point read of a node the resolver would then have to
    /// refuse anyway, and never a subscription per page for a value that cannot be used.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ForThePackageRootItself_NothingIsRead()
    {
        var emissions = await Mesh.GetWorkspace().ObservePartitionRoot(Package)
            .ToList()
            .FirstAsync().Timeout(ReadBudget);

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
        var marked = await IconTile(ChildPath, MarkPathData);
        marked.Should().Contain(MarkSignature, "the package's mark is what the header's tile draws");

        // The control: same page shape, no mark on its root — it settles on the type glyph, and the
        // other package's mark never reaches it.
        var unmarked = await IconTile($"{UnmarkedPackage}/Rules", DocumentGlyph);
        unmarked.Should().Contain(DocumentGlyph);
        unmarked.Should().NotContain(MarkSignature);
    }

    private const string DocumentGlyph = "/static/NodeTypeIcons/document.svg";

    /// <summary>The mark's path data — the fragment to WAIT for, because it reads identically in the
    /// rendered HTML and in the JSON the area store travels as (it carries no character JSON
    /// escapes).</summary>
    private const string MarkPathData = "M8 6h8v3H8z";

    /// <summary>The mark's INNER markup, which is what an assertion can pin. The whole
    /// <see cref="PackageMark"/> string cannot be: a raw-HTML surface has no scoped CSS to size a
    /// viewBox-only svg with, so <c>MeshNodeImageHelper.SizeInlineSvg</c> injects an explicit
    /// <c>style</c> into the OPENING tag on the way out. Everything after it is untouched.</summary>
    private const string MarkSignature =
        "<rect width=\"24\" height=\"24\" rx=\"5\" fill=\"#1f6feb\"/>"
        + "<path d=\"M8 6h8v3H8z\" fill=\"#fff\"/></svg>";

    /// <summary>The 56 px square the node-page header draws its icon in — the one HTML control whose
    /// content IS the resolved icon.</summary>
    private const string IconTileMarker = "width: 56px; height: 56px";

    /// <summary>
    /// The node's rendered Overview header icon tile, awaited until the area carries
    /// <paramref name="awaitedFragment"/>.
    ///
    /// <para>The wait is on the AREA STORE — one condition, one stream — rather than on a sub-area
    /// resolved by name, because the header's inner area names are generated and a re-render is free
    /// to renumber them. <paramref name="awaitedFragment"/> is therefore chosen to read identically
    /// in the rendered HTML and in the JSON the store travels as. Once the store carries the answer
    /// the tree is walked for the tile itself, so the assertion is still about the ICON and not about
    /// the payload at large.</para>
    /// </summary>
    private async Task<string> IconTile(string nodePath, string awaitedFragment)
    {
        var workspace = GetClient(c => c.AddData()).GetWorkspace();
        var reference = new LayoutAreaReference("Overview");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(nodePath), reference);

        await stream.Should().Within(RenderBudget).Match(
            frame => frame.Value.ToString().Contains(awaitedFragment),
            $"the Overview of '{nodePath}' renders '{awaitedFragment}'");

        var tiles = new List<string>();
        await Collect(reference.Area!, 0);
        return Assert.Single(tiles);

        async Task Collect(string area, int depth)
        {
            if (depth > 6)
                return;
            var control = await stream.GetControlStream(area)
                .Should().Within(ReadBudget).Match(c => c != null);
            if (control is HtmlControl html
                && (html.Data?.ToString() ?? "").Contains(IconTileMarker))
                tiles.Add(html.Data!.ToString()!);
            if (control is StackControl stack)
                foreach (var name in stack.Areas.Select(a => a.Area?.ToString())
                             .Where(n => !string.IsNullOrEmpty(n)))
                    await Collect(name!, depth + 1);
        }
    }
}
