using System;
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
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end pin for the overlay SELF-HEAL
/// (<c>NodeTypeEnrichmentHelpers.WithOverlaySelfHeal</c>) — the memex
/// 2026-07-25/26 incident: an instance hub that activates while its NodeType
/// cannot produce a usable build binds the compilation-error overlay ONCE and
/// keeps it for its whole grain lifetime — the re-enrichment short-circuit
/// (<c>node.HubConfiguration != null</c>) means the type compiling green
/// seconds later changes NOTHING for the already-activated instance. On memex
/// the Underwriting root served "This page can't be displayed" for ~14h until
/// an operator posted a manual DisposeRequest.
///
/// <para>The loop pinned here: instance binds the overlay (broken type,
/// settled Error) → the source is fixed and the type recompiles to Ok with a
/// usable build → the instance's background watcher posts the
/// self-<see cref="DisposeRequest"/> (the RecycleLayoutArea idiom) → the next
/// access re-activates and renders the COMPILED area. The test never posts a
/// manual recycle — before the fix, every probe kept hitting the still-alive
/// overlay hub and the final wait timed out (the exact prod symptom).</para>
/// </summary>
public class OverlaySelfHealInstanceRecycleTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Two real Roslyn compiles (the failing baseline, then the healing
    // rebuild) plus an activation round-trip per probe — past the 30s/60s
    // defaults, same rationale as FrameworkStaleInstanceRenderTest.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(120);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(240);

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [Fact(Timeout = 240_000)]
    public async Task OverlaidInstance_SelfRecycles_WhenTypeCompilesGreen()
    {
        var typeId = $"SelfHeal{Guid.NewGuid():N}";
        var nodeTypePath = $"{TestPartition}/{typeId}";
        var sourcePath = $"{nodeTypePath}/Source/code";
        var instancePath = $"{nodeTypePath}/Inst";
        var marker = $"SELF_HEALED_MARKER_{typeId}";
        var workspace = Mesh.GetWorkspace();

        // The FIXED source: a distinctive compiled Overview (HtmlControl with a
        // unique marker) so the healed page is unambiguously distinguishable
        // from the overlay (a StackControl with markdown).
        var goodSource = $$"""
            using MeshWeaver.Layout.Composition;
            public static class {{typeId}}Areas
            {
                public static UiControl Overview(LayoutAreaHost host, RenderingContext _)
                    => Controls.Html("<div id='marker'>{{marker}}</div>");
            }
            """;

        // 1. NodeType + a deliberately BROKEN source — the compile settles at
        //    Error (genuine failure, no usable build; the type also parks).
        await NodeFactory.CreateNode(new MeshNode(typeId, TestPartition)
        {
            Name = typeId,
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Overlay self-heal end-to-end regression.",
                Configuration = $"config => config.AddDefaultLayoutAreas().AddLayout(layout => layout.WithView(\"Overview\", {typeId}Areas.Overview))",
            }
        }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("code", $"{nodeTypePath}/Source")
        {
            Name = "Code",
            NodeType = "Code",
            Content = new CodeConfiguration { Code = "this is not valid C#;", Language = "csharp" }
        }).Should().Within(30.Seconds()).Emit();

        await TriggerRecompile(nodeTypePath, DateTimeOffset.UtcNow);

        // Error must be visible on the MESH HUB's workspace view — the same
        // stream activation reads — BEFORE the instance is created, so
        // enrichment deterministically hits the genuine-Error overlay branch
        // (not the wait-for-in-flight-compile path).
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(60.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Error);
        Output.WriteLine("NodeType settled at Error on the mesh hub view.");

        // 2. An instance of the broken type — activation binds the overlay
        //    (StackControl; the compiled area would be an HtmlControl). This is
        //    the stuck state the fix targets.
        await NodeFactory.CreateNode(new MeshNode("Inst", nodeTypePath)
        {
            Name = "Inst",
            NodeType = nodeTypePath,
            State = MeshNodeState.Active,
        }).Should().Within(30.Seconds()).Emit();

        var reference = new LayoutAreaReference("Overview");
        var overlayClient = GetClient(c => c.AddData());
        await overlayClient.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(instancePath), reference)
            .GetControlStream(reference.Area!)
            .Should().Within(60.Seconds()).Match(c => c is StackControl);
        Output.WriteLine("Instance is bound to the compilation-error overlay.");

        // 3. Fix the source. A source fix on a parked Error type auto-un-parks
        //    and recompiles (pinned in NodeTypeCompileParkTest.
        //    ParkedNodeType_SourceFix_AutoRecompilesAndUnparks) — wait for the
        //    healed terminal state: Ok WITH a usable build, on the SAME
        //    mesh-hub stream the instance's self-heal watcher subscribes.
        await workspace.GetMeshNodeStream(sourcePath).Update(curr =>
            curr with { Content = new CodeConfiguration { Code = goodSource, Language = "csharp" } })
            .Should().Within(30.Seconds()).Emit();
        await workspace.GetMeshNodeStream(nodeTypePath)
            .Should().Within(90.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyCollection)
                && !string.IsNullOrEmpty(d.LatestAssemblyPath));
        Output.WriteLine("NodeType healed to Ok with a usable build.");

        // 4. THE CONTRACT — no manual DisposeRequest anywhere in this test:
        //    the stuck instance must SELF-recycle (the overlay's watcher posts
        //    the self-DisposeRequest on the healed emission) and the next
        //    access must re-activate onto the COMPILED configuration. Probe
        //    with fresh clients — each probe is a new subscription, i.e. a new
        //    activation once the stuck hub disposed; a probe that lands before
        //    the recycle sees the overlay and times out its bounded window, so
        //    retry. Before the fix EVERY probe hit the still-alive overlay hub
        //    and this wait exhausted all attempts (the prod 14h symptom).
        string? healedHtml = null;
        for (var attempt = 0; attempt < 6 && healedHtml is null; attempt++)
        {
            var probeClient = GetClient(c => c.AddData());
            try
            {
                var control = await probeClient.GetWorkspace()
                    .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(instancePath), reference)
                    .GetControlStream(reference.Area!)
                    .Where(c => c is HtmlControl h && (h.Data?.ToString() ?? string.Empty).Contains(marker))
                    .Take(1)
                    .Timeout(15.Seconds())
                    .ToTask(TestContext.Current.CancellationToken);
                healedHtml = (control as HtmlControl)?.Data?.ToString();
            }
            catch (TimeoutException)
            {
                Output.WriteLine($"Probe {attempt}: still the overlay — self-recycle not landed yet, retrying.");
            }
        }

        healedHtml.Should().NotBeNull(
            "the overlaid instance must self-recycle once its NodeType compiles green — "
            + "no manual Recycle — and re-activate onto the compiled configuration");
        healedHtml!.Should().Contain(marker,
            "the re-activated instance must render the compiled Overview area, not the overlay");
    }

    /// <summary>
    /// Canonical explicit compile trigger — the RequestedReleaseAt + Force flip
    /// on the NodeType MeshNode (same shape as CodeEditRecompileTest's helper;
    /// the UI Compile button executes this exact write).
    /// </summary>
    private async Task TriggerRecompile(string nodeTypePath, DateTimeOffset triggerAt)
    {
        await Mesh.GetWorkspace().GetMeshNodeStream(nodeTypePath).Update(curr =>
        {
            if (curr?.Content is not NodeTypeDefinition def) return curr!;
            return curr with
            {
                Content = def with
                {
                    RequestedReleaseAt = triggerAt,
                    RequestedReleaseForce = true,
                }
            };
        }).Should().Within(30.Seconds()).Emit();
    }
}
