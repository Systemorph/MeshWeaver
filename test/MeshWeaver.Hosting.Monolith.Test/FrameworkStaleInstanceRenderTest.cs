using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Repro for the framework-redeploy stale-overlay bug the user hit on
/// <c>rbuergi/CatBond/AtlanticBond</c>: after a binary rebuild changes the
/// framework version, opening a dynamic-NodeType <b>instance</b> renders the
/// framework-stale overlay ("Built against a previous framework version")
/// instead of the instance's compiled layout area — even though the NodeType
/// itself self-heals to <c>Ok</c>.
///
/// <para><b>Root cause:</b> <c>NodeTypeEnrichmentHelpers.TriggerRecompileAndRetry</c>
/// waits on a <c>settled</c> predicate that also matches an IN-FLIGHT
/// (<c>Pending</c>/<c>Compiling</c>) NodeType state — the pre-recompile node
/// still carries <c>HubConfiguration</c> + <c>LatestAssemblyPath</c>, so the
/// recursion fires on the stale node, sees <c>HasUsableBuild=false</c>, hits
/// <c>MaxRecompileAttempts</c>, and lands on the overlay. The NodeType still
/// eventually recompiles (so a NodeType-only assertion passes — which is why
/// <c>OrleansCompileActivityAccessTest.FrameworkStaleAssembly_SelfHealsOnInstanceActivation</c>
/// went green and missed this), but the triggering <b>instance</b> is left
/// bound to the overlay config.</para>
///
/// <para>This test gives the dynamic type a distinctive compiled "Overview"
/// area — an <see cref="HtmlControl"/> carrying a unique marker — so the
/// compiled config is unambiguously distinguishable from the overlay (a
/// <c>StackControl</c> with markdown). It forces the framework-stale shape,
/// activates an instance, and requires the instance to render the COMPILED
/// area. Before the fix the instance is stuck on the overlay, so the wait for
/// the marker control times out (the user's exact symptom).</para>
/// </summary>
public class FrameworkStaleInstanceRenderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override bool ShareMeshAcrossTests => true;

    // This repro inherently drives TWO real Roslyn compiles (the baseline build,
    // then the self-heal recompile triggered by instance activation) plus a
    // layout render — comfortably past the 30s/60s defaults. Widen the watchdog
    // to match, like NodeOperations.DeletionTests does.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(90);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(180);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddGraph();

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 180_000)]
    public async Task FrameworkStaleInstance_RendersCompiledArea_NotOverlay()
    {
        var typeId = $"StaleArea{Guid.NewGuid():N}";
        var nodeTypePath = $"type/{typeId}";
        var marker = $"FRESH_COMPILED_MARKER_{typeId}";

        // Dynamic type whose compiled config registers a DISTINCTIVE "Overview"
        // area (an HtmlControl carrying the marker). Mirrors the CatBond shape:
        // a named render method passed to layout.WithView (known to compile).
        var source = $$"""
            using System;
            using System.Reactive.Linq;
            using MeshWeaver.Layout;
            using MeshWeaver.Layout.Composition;

            public record {{typeId}} { public string Title { get; init; } = string.Empty; }

            public static class {{typeId}}Areas
            {
                public static LayoutDefinition AddMarkerArea(this LayoutDefinition layout) =>
                    layout.WithView("Overview", RenderOverview);

                private static IObservable<UiControl?> RenderOverview(LayoutAreaHost host, RenderingContext ctx) =>
                    Observable.Return<UiControl?>(Controls.Html("{{marker}}"));
            }
            """;

        // 1. Create the NodeType + source and trigger the first compile. The
        //    Configuration wires the compiled marker area as the default Overview.
        var typeNode = MeshNode.FromPath(nodeTypePath) with
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = $"config => config.WithContentType<{typeId}>()"
                    + ".AddLayout(layout => layout.WithDefaultArea(\"Overview\").AddMarkerArea())"
            }
        };
        await MeshService.CreateNode(typeNode).Should().Emit();
        await MeshService.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
        {
            NodeType = "Code",
            Name = "code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = source, Language = "csharp" }
        }).Should().Emit();

        await Mesh.Observe(new GetCompilationPathRequest(), o => o.WithTarget(new Address(nodeTypePath)))
            .Should().Within(90.Seconds()).Emit();

        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath)
                && !string.IsNullOrEmpty(d.CompiledFrameworkVersion));
        Output.WriteLine("Baseline compile Ok with a usable build.");

        // 2. Force the framework-stale shape: stamp a bogus CompiledFrameworkVersion
        //    while leaving Status=Ok and the assembly fields intact — exactly what
        //    a binary redeploy leaves behind. Status stays Ok, so nothing
        //    auto-recompiles until an instance activation triggers the self-heal.
        var bogusFv = $"STALE-{Guid.NewGuid():N}";
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Update(curr => curr.Content is NodeTypeDefinition d
                ? curr with { Content = d with { CompiledFrameworkVersion = bogusFv } }
                : curr)
            .Should().Emit();
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(20.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d && d.CompiledFrameworkVersion == bogusFv);
        Output.WriteLine($"Forced framework-stale (bogus framework version {bogusFv}).");

        // 3. Activate an instance and render its Overview through the layout
        //    client — the exact path the GUI drives.
        var instancePath = $"{nodeTypePath}/Inst";
        await MeshService.CreateNode(MeshNode.FromPath(instancePath) with
        {
            Name = "Inst",
            NodeType = nodeTypePath,
            State = MeshNodeState.Active,
            Content = JsonSerializer.SerializeToElement(new { Title = "instance" })
        }).Should().Emit();

        var client = GetClient();
        var reference = new LayoutAreaReference("Overview");
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(instancePath), reference);

        // The instance MUST bind the COMPILED Overview — the HtmlControl carrying
        // the marker — after the framework-stale self-heal, NOT the overlay (a
        // StackControl with the "Built against a previous framework version"
        // markdown). Before the fix the self-heal recursion re-snapped the STALE
        // Ok node (the Ok→Pending flip is a cross-hub patch that hadn't
        // round-tripped), capped at MaxRecompileAttempts ~5ms later, and froze the
        // instance on the overlay — so this wait for the marker timed out (the
        // rbuergi/CatBond/AtlanticBond "not building type" symptom).
        var control = await stream
            .GetControlStream(reference.Area!)
            .Should().Within(90.Seconds())
            .Match(c => c is HtmlControl h && h.Data?.ToString()?.Contains(marker) == true);

        var html = control.Should().BeOfType<HtmlControl>(
            "the instance must bind the compiled Overview after the framework-stale "
            + "self-heal, not the framework-stale overlay").Which;
        html.Data.ToString().Should().Contain(marker,
            "the rendered Overview must be the compiled marker area, proving the instance "
            + "picked up the healed assembly rather than the overlay/default config");
    }
}
