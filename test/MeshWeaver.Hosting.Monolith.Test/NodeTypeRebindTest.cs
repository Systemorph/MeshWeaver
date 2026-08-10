using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Repro for issue #1104 — <b>a hub activated on the wrong NodeType stays wrong</b>.
///
/// <para>A per-node hub binds its <c>HubConfiguration</c> from the node's NodeType EXACTLY ONCE,
/// at activation, and is then PINNED by address in <c>HostedHubsCollection</c>. Nothing re-reads
/// the NodeType afterwards, so if the node's type ARRIVES or CHANGES while the hub is alive, the
/// hub keeps serving the configuration it was born with — the mesh DEFAULT configuration when the
/// node had no type at activation — for the rest of its lifetime.</para>
///
/// <para>That is the plugin gate's <c>Store/Catalog</c> RED: "No renderer is registered for area
/// <c>Tests</c> on hub <c>Store</c>", whose available-areas list was item-for-item
/// <c>ConfigureDefaultNodeHub</c>'s set and contained not one area from the installed package's
/// configuration. The install provisions the partition BEFORE writing the root, so any routed
/// touch inside that window activates the root's hub against a type-less node; the later retype
/// changes the row and nothing recycles the hub. Fixing the RESOLUTION path (#1055 — a synthesized
/// partition root is no longer CACHED) cannot help a hub a bad resolution has ALREADY activated:
/// not cached is not not-pinned.</para>
///
/// <para>The test reproduces the mechanism WITHOUT the installer and without Roslyn: a node is
/// created with no NodeType, its hub is activated (Ping), the node is then retyped to a static
/// NodeType that declares a distinctive layout area, and the area is rendered through the layout
/// client — the exact path the GUI drives. Before the fix the hub still serves the mesh default,
/// the marker area does not exist on it, and the render never produces the marker control.</para>
/// </summary>
public class NodeTypeRebindTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string MarkerTypePath = "type/RebindMarker";
    private const string MarkerArea = "RebindMarkerArea";
    private const string Marker = "REBOUND_ON_THE_REAL_NODETYPE";

    private const string OtherTypePath = "type/RebindOther";
    private const string OtherArea = "RebindOtherArea";

    // The happy path is fast (the recycle is one change-feed hop), but a REGRESSION here is a
    // wait that runs out — so the per-test watchdogs must outlast the render budget below, or the
    // failure reads as a harness abort instead of the assertion it is.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(90);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(180);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            // STATIC NodeTypes (no Roslyn, no compile lifecycle) whose configurations each declare
            // one distinctive area. The marker area's presence on the instance hub is the
            // unambiguous proof that the hub bound THAT type rather than the mesh default (or the
            // type it activated with).
            .AddMeshNodes(MeshNode.FromPath(MarkerTypePath) with
            {
                Name = "RebindMarker",
                State = MeshNodeState.Active,
                HubConfiguration = config => config.AddLayout(layout =>
                    layout.WithView(MarkerArea, RenderMarker))
            })
            .AddMeshNodes(MeshNode.FromPath(OtherTypePath) with
            {
                Name = "RebindOther",
                State = MeshNodeState.Active,
                HubConfiguration = config => config.AddLayout(layout =>
                    layout.WithView(OtherArea, RenderOther))
            });

    // NAMED areas (not predicate views) on purpose: a named area shows up in the "Available named
    // areas" list the layout host prints when an area is missing, so a red run reproduces the
    // issue's decisive evidence verbatim — the list is item for item ConfigureDefaultNodeHub's set,
    // with not one area of the type the node actually carries.
    private static IObservable<UiControl?> RenderMarker(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(Controls.Html(Marker));

    private static IObservable<UiControl?> RenderOther(LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<UiControl?>(Controls.Html("the type this hub activated with"));

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// #1104's exact shape: the hub activates against a node with NO NodeType — what a
    /// freshly-provisioned partition root looks like inside the install window, and what
    /// <c>PathResolutionService.SynthesizePartitionRoot</c>'s fabricated placeholder always is —
    /// so it binds the mesh DEFAULT configuration. The type then arrives.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task NodeTypeArrivesAfterActivation_HubRebindsToIt()
        => AssertRebinds(activationNodeType: null);

    /// <summary>
    /// The installer's placeholder dance, generalised: the hub activates on one type and the node
    /// is retyped to another. Same pin, same consequence — the hub serves the FIRST type's areas
    /// forever. (In the Store's case type #1 is the <c>Space</c> placeholder written before the
    /// package's own type has compiled.)
    /// </summary>
    [Fact(Timeout = 120_000)]
    public Task NodeTypeChangedAfterActivation_HubRebindsToIt()
        => AssertRebinds(activationNodeType: OtherTypePath);

    private async Task AssertRebinds(string? activationNodeType)
    {
        var instancePath = $"type/Rebind{Guid.NewGuid():N}";

        // 1. The node as it looks at activation time. Content is required (a node with neither
        //    NodeType nor Content is rejected), the NodeType is the variable under test.
        await MeshService.CreateNode(MeshNode.FromPath(instancePath) with
        {
            Name = "Rebind",
            NodeType = activationNodeType,
            State = MeshNodeState.Active,
            Content = JsonSerializer.SerializeToElement(new { title = "instance" }),
        }).Should().Emit();

        // 2. ACTIVATE its hub in that state. Ping is answered by every hub, so the response is
        //    proof the hub exists — and from here on it is PINNED by address: routing
        //    short-circuits on GetHostedHub and never resolves the path again.
        await Mesh.Observe(new PingRequest(), o => o.WithTarget(new Address(instancePath)))
            .Should().Within(60.Seconds()).Emit();
        Output.WriteLine(
            $"Activated {instancePath} as NodeType '{activationNodeType ?? "(none)"}'.");

        // 3. The node acquires its real type — the installer's retype, a user changing a node's
        //    type, a repo import landing the typed row.
        await Mesh.GetWorkspace().GetMeshNodeStream(instancePath)
            .Update(node => node with { NodeType = MarkerTypePath })
            .Should().Emit();
        Output.WriteLine($"Retyped {instancePath} to {MarkerTypePath}.");

        // 4. The hub MUST now serve the type's area. Before the fix it still serves whatever it
        //    was born with, the area is not registered on it, and this render never yields the
        //    marker — "No renderer is registered for area '{MarkerArea}' on hub '{instancePath}'".
        var client = GetClient();
        var reference = new LayoutAreaReference(MarkerArea);
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(instancePath), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Should().Within(60.Seconds())
            .Match(c => c is HtmlControl h && h.Data?.ToString()?.Contains(Marker) == true);

        control.Should().BeOfType<HtmlControl>(
            "the hub must rebind to the node's real NodeType once it arrives, instead of "
            + "staying pinned on the configuration it activated with")
            .Which.Data!.ToString().Should().Contain(Marker);

        // Asserted LAST, so a red run is never ambiguous about which half broke: by now the retype
        // has demonstrably reached the hub, and this confirms it is what the mesh reads back too.
        await Mesh.GetWorkspace().GetMeshNodeStream(instancePath)
            .Should().Within(30.Seconds())
            .Match(n => n.NodeType == MarkerTypePath);
    }
}
