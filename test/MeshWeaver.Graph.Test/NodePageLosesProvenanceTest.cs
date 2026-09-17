using System.Linq;
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
/// <para>ONE variable between the halves below: HOW the custom landing page is registered. Same
/// mesh, same node, same viewer, same page content, same area. The half that registers it the way
/// the whole fleet registers landing pages today — a bare <c>WithView(OverviewArea, …)</c> —
/// renders a page with NO provenance strip; the half that registers it through
/// <c>WithNodePage</c> renders the same page WITH one, supplied by the framework.</para>
///
/// <para>🚨 <b>The absence is asserted STRUCTURALLY, not on a timer.</b> The framework composes the
/// strip and the page into ONE control — <c>Stack(NodeMeta, page)</c>, emitted together — so there
/// is no window in which the page is on screen and the strip is still in flight. The assertion is
/// therefore on the landing area's ROOT control: does its area list carry a
/// <see cref="MeshNodeLayoutAreas.NodeMetaArea"/> child? That is decidable from one rendered value.
/// A "nothing emitted within N seconds" wait would be weaker AND wrong here: a negative window
/// always spends its whole budget, and <c>TestTimeouts.Quick</c> is CI-scaled to 36 s, which alone
/// exceeds the 30 s <c>methodTimeout</c> — the first version of this file passed locally and timed
/// out on every CI runner for exactly that reason.</para>
///
/// <para>🚨 <b>Why it is a measurement and not a tautology.</b> "No strip" is also what you would
/// observe if the page never rendered — a mistyped path, a missing permission, a hub that never
/// started. So every half asserts the page's OWN body is present in the same rendered tree it
/// claims the strip is missing from. The page is proven to be on screen at the moment the strip is
/// proven not to be.</para>
///
/// <para>The instrument is the strip's stable area id, never the rendered text. Asserting on the
/// word "Created:" would assert that a German reader sees an English page, and would keep passing
/// after somebody translated it.</para>
/// </summary>
public abstract class NodePageProvenanceExperimentBase(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The node whose landing page every half renders. Plain — no NodeType, so it takes
    /// the default chain and the only thing that differs is the registration under test.</summary>
    protected const string Node = "ProvenanceProbe";

    /// <summary>The area id of the custom page's own body — the positive signal that says "this
    /// page rendered", so no negative assertion can pass on an empty screen.</summary>
    protected const string PageBodyArea = "ProbeBody";

    /// <summary>
    /// The custom landing page, identical in every half. A node type's designed overview in
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

    /// <summary>
    /// The landing area's root control, once it has rendered. ONE bounded wait per test — and it
    /// completes as soon as the page is on screen rather than spending a budget.
    /// </summary>
    protected async Task<IContainerControl> LandingPageRoot()
    {
        var root = await Control(MeshNodeLayoutAreas.OverviewArea)
            .Where(c => c is IContainerControl)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence);
        return (IContainerControl)root!;
    }

    /// <summary>Reads one of <paramref name="container"/>'s child areas by its rendered key.</summary>
    protected async Task<IContainerControl> Child(NamedAreaControl area)
    {
        var control = await Control((string)area.Area)
            .Where(c => c is IContainerControl)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence);
        return (IContainerControl)control!;
    }

    /// <summary>
    /// Whether <paramref name="area"/> is the provenance strip. Matched on the rendered KEY's last
    /// segment, which is what <see cref="MeshNodeLayoutAreas.NodeMetaArea"/> guarantees — nested
    /// containers render into <c>{parentArea}/{childId}</c>, so the depth varies and the suffix
    /// does not.
    /// </summary>
    protected static bool IsProvenanceStrip(NamedAreaControl area) =>
        area.Area is string key
        && key.EndsWith('/' + MeshNodeLayoutAreas.NodeMetaArea, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="area"/> is the probe page's own body.</summary>
    protected static bool IsPageBody(NamedAreaControl area) =>
        area.Area is string key && key.EndsWith('/' + PageBodyArea, StringComparison.Ordinal);
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
        var root = await LandingPageRoot();

        // The page IS on screen — its own body is a direct child of the landing area's root,
        // because nothing wrapped it.
        Assert.Contains(root.Areas, IsPageBody);

        // …and the provenance line is not, anywhere in that tree.
        Assert.DoesNotContain(root.Areas, IsProvenanceStrip);
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
    public async Task ThePageRenders_BelowAProvenanceStrip()
    {
        var root = await LandingPageRoot();

        Assert.Contains(root.Areas, IsProvenanceStrip);

        // The page is intact BELOW the strip — the framework composed, it did not replace. Found
        // by taking the non-strip sibling's own rendered key rather than a positional guess.
        var pageArea = Assert.Single(root.Areas.Where(a => !IsProvenanceStrip(a)));
        var page = await Child(pageArea);
        Assert.Contains(page.Areas, IsPageBody);
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
        var root = await LandingPageRoot();

        Assert.Contains(root.Areas, IsPageBody);
        Assert.DoesNotContain(root.Areas, IsProvenanceStrip);
    }
}
