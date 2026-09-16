using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#4500 — "Provenance is opt-in per layout area", as a controlled experiment
/// against a real mesh and a real layout client.
///
/// <para>ONE variable between the two halves below: HOW the custom landing page is registered.
/// Same mesh, same node, same viewer, same page content, same area. The half that registers it the
/// way the whole fleet registers landing pages today — a bare <c>WithView(OverviewArea, …)</c> —
/// renders a page with NO provenance; the half that registers it through <c>WithNodePage</c>
/// renders the same page WITH it, supplied by the framework.</para>
///
/// <para>🚨 <b>Why the negative half is a measurement and not a tautology.</b> "The provenance strip
/// is absent" is also what you would observe if the area never rendered at all — a mistyped node
/// path, a permission the test viewer lacks, a hub that never started. So the negative half waits
/// for the page's OWN control first and only then asserts the strip's absence: the page is proven
/// to be on screen at the moment the strip is proven not to be. Without that, this file would pass
/// while measuring nothing, which is the failure mode AGENTS.md names as the dominant one.</para>
///
/// <para>The instrument is the strip's stable area id (<see cref="MeshNodeLayoutAreas.NodeMetaArea"/>),
/// not the rendered text. Asserting on the word "Created:" would assert that a German reader sees
/// an English page — and would keep passing after somebody translated it.</para>
/// </summary>
public abstract class NodePageProvenanceExperimentBase(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The node whose landing page both halves render. Plain — no NodeType, so it takes
    /// the default chain and the only thing that differs is the registration under test.</summary>
    protected const string Node = "ProvenanceProbe";

    /// <summary>The area id of the custom page's own body — the positive signal that says "this
    /// page rendered", so the negative assertion below cannot pass on an empty screen.</summary>
    protected const string PageBodyArea = "ProbeBody";

    /// <summary>
    /// The custom landing page, identical in both halves. A node type's designed overview in
    /// miniature: it draws its own content and never calls <c>BuildHeader</c> — which is exactly
    /// what 82 of the 92 landing pages in MeshWeaver.Plugins do (measured 2026-09-16).
    /// </summary>
    protected static IObservable<UiControl?> CustomPage(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(Controls.Stack
            .WithView(Controls.Markdown("the page's own content"), PageBodyArea));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>The rendered control at <paramref name="areaKey"/>, read through the layout client
    /// exactly as the portal reads it.</summary>
    protected IObservable<UiControl?> Control(string areaKey)
        => GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(Node), new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea))
            .GetControlStream(areaKey);

    /// <summary>The store key of the provenance strip on a page the framework composed it for —
    /// a direct named child of the composed frame, so the key is deterministic.</summary>
    protected const string StripKey =
        MeshNodeLayoutAreas.OverviewArea + "/" + MeshNodeLayoutAreas.NodeMetaArea;

    /// <summary>Waits until the custom page itself is on screen. This is the control that makes
    /// the strip assertions mean something.</summary>
    protected async Task ThePageHasRendered()
    {
        var root = await Control(MeshNodeLayoutAreas.OverviewArea)
            .Where(c => c is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence);
        Assert.NotNull(root);
    }
}

/// <summary>
/// 🚨 THE DEFECT, reproduced. A landing page registered the way the fleet registers them today
/// loses the provenance line, and nothing anywhere says so.
/// </summary>
public class ABareWithViewLosesProvenanceTest(ITestOutputHelper output)
    : NodePageProvenanceExperimentBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            // Registered AFTER AddDefaultLayoutAreas, so this replaces the framework's Overview —
            // the same last-writer-wins replacement a node type's own HubConfiguration performs.
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout =>
                layout.WithView(MeshNodeLayoutAreas.OverviewArea, CustomPage)))
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" });

    [HubFact]
    public async Task ThePageRenders_AndTheProvenanceStripIsGone()
    {
        await ThePageHasRendered();

        await Control(StripKey).Where(c => c is not null).Should().NotEmit(
            TestTimeouts.Quick,
            "a landing page registered with a bare WithView replaces the framework's Overview, "
            + "and the provenance line goes with the renderer it rode on (#4500)");
    }
}

/// <summary>
/// THE FIX, on the identical page. <c>WithNodePage</c> with no stated opinion composes the
/// provenance line above the page — so a landing page written by somebody who never thought about
/// provenance ships with it, instead of silently without.
/// </summary>
public class WithNodePageSuppliesProvenanceTest(ITestOutputHelper output)
    : NodePageProvenanceExperimentBase(output)
{
    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout =>
                layout.WithNodePage(MeshNodeLayoutAreas.OverviewArea, CustomPage)))
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" });

    [HubFact]
    public async Task ThePageRenders_AndCarriesTheProvenanceStrip()
    {
        await ThePageHasRendered();

        var strip = await Control(StripKey)
            .Where(c => c is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence);

        Assert.NotNull(strip);
    }
}

/// <summary>
/// The declared-decline half: a page that says it wants no provenance line gets none — the
/// framework composes nothing — but the DECISION is on the record. This is what separates a
/// weighed omission from a forgotten one, and it is the only reason "opt-out" is an improvement on
/// "opt-in" rather than a rename of it.
/// </summary>
public class ADeclinedNodePageCarriesNoStripTest(ITestOutputHelper output)
    : NodePageProvenanceExperimentBase(output)
{
    private const string Reason = "this probe page reports its own state instead";

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .ConfigureDefaultNodeHub(config => config.AddLayout(layout =>
                layout.WithNodePage(MeshNodeLayoutAreas.OverviewArea, CustomPage,
                    NodePageProvenance.Declined(Reason))))
            .AddMeshNodes(new MeshNode(Node) { Name = "Probe" });

    [HubFact]
    public async Task ThePageRenders_WithoutAStrip()
    {
        await ThePageHasRendered();

        await Control(StripKey).Where(c => c is not null).Should().NotEmit(
            TestTimeouts.Quick,
            "the page declined the line — and unlike the bare-WithView half, it said why");
    }
}
