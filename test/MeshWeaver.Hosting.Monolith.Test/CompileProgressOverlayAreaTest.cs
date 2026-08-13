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
/// Issue #1411 — <b>the compilation-in-progress overlay covered only <c>Overview</c></b>.
///
/// <para>While an instance's NodeType is building, its hub is activated against the overlay
/// configuration produced by
/// <see cref="NodeTypeEnrichmentHelpers.CreateCompilationInProgressConfiguration"/>. That
/// registered ONE named renderer, so every other area fell through to
/// <c>LayoutDefinition.BuildNotFoundControl</c> and answered
/// <c>"**Area not found** — No renderer is registered for area `KeyMetrics`"</c>. The silent park
/// the overlay set out to remove was not removed, it was RELOCATED: a user who deep-links any
/// non-Overview area during a framework-bump sweep is told the area does not exist, when the truth
/// is that its code is still building.</para>
///
/// <para>Both directions are asserted here, because a catch-all that swallowed the genuine miss
/// would be a worse bug than the one it fixes:</para>
/// <list type="number">
///   <item>an area of an OVERLAID instance that nothing else registers renders the compile-progress
///     page, not the placeholder;</item>
///   <item>an area that genuinely has no renderer, on a hub with no overlay, still renders the
///     placeholder;</item>
///   <item>and the two are told apart by a CONSUMER — <see cref="AreaFrameClassifier"/> on the
///     frame's well-known id — not by a human reading the prose. That is the coupling the issue
///     calls out: converting a terminal-looking frame into a transient one breaks every waiter
///     that treated "not the placeholder" as "the content arrived", so the distinction has to be
///     mechanically available in the same change.</item>
/// </list>
///
/// <para>The overlay is applied here as a STATIC node configuration rather than by stalling a real
/// Roslyn compile: the mesh composes it with <c>DefaultNodeHubConfiguration</c> exactly as
/// <c>MeshNodeHubFactory</c> does for the real activation path, so the layout under test is the
/// production one — without a timing-dependent compile to keep in flight.</para>
/// </summary>
public class CompileProgressOverlayAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The NodeType the overlaid instance is waiting on — held at <c>Compiling</c>.</summary>
    private const string PendingTypePath = "overlay/PendingType";

    /// <summary>The instance whose hub carries the compilation-in-progress overlay.</summary>
    private const string OverlaidPath = "overlay/Overlaid";

    /// <summary>A plain node hub — no overlay — used for the "genuinely missing" direction.</summary>
    private const string PlainPath = "overlay/Plain";

    /// <summary>An area no configuration on either hub registers.</summary>
    private const string TypeOwnedArea = "KeyMetrics";

    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(90);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(180);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddMeshNodes(MeshNode.FromPath(OverlaidPath) with
            {
                Name = "Overlaid",
                State = MeshNodeState.Active,
                // The production overlay, verbatim. The mesh composes DefaultNodeHubConfiguration
                // underneath it (MeshNodeHubFactory), which is what puts the standard named areas
                // — and therefore the HasNamedRenderer guard the catch-all depends on — in play.
                HubConfiguration = NodeTypeEnrichmentHelpers
                    .CreateCompilationInProgressConfiguration(PendingTypePath, OverlaidPath)
            })
            .AddMeshNodes(MeshNode.FromPath(PlainPath) with
            {
                Name = "Plain",
                State = MeshNodeState.Active,
                HubConfiguration = config => config
            });

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// The defect: an area of an instance whose type is mid-compile answered "this area does not
    /// exist". It must answer "this area's code is still building" — the same live progress page
    /// the instance's Overview already served.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AreaOfAnInstanceMidCompile_RendersProgress_NotAreaNotFound()
    {
        await CreateCompilingType();

        var control = await Render(OverlaidPath, TypeOwnedArea);

        AreaFrameClassifier.IsAreaNotFound(control).Should().BeFalse(
            "an area whose renderer has not been registered YET, because the instance's NodeType is "
            + "still compiling, must not be reported as an area that does not exist");
        AreaFrameClassifier.IsCompileProgress(control).Should().BeTrue(
            "every area of an overlaid instance serves the same live compile-progress page its "
            + "Overview serves — the catch-all is what makes the overlay cover the whole hub");
        AreaFrameClassifier.IsTransientFrame(control).Should().BeTrue(
            "the frame must announce itself as one that will be replaced without anyone acting");
    }

    /// <summary>
    /// The regression guard for what already worked: Overview keeps its own named renderer, and
    /// the catch-all must not have replaced it (two renderers for one area are
    /// last-wins-DESTRUCTIVE — the 2026-07-03 eternal-spinner RCA).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task OverviewOfAnInstanceMidCompile_StillRendersProgress()
    {
        await CreateCompilingType();

        var control = await Render(OverlaidPath, MeshNodeLayoutAreas.OverviewArea);

        AreaFrameClassifier.IsCompileProgress(control).Should().BeTrue(
            "the named Overview renderer must survive the catch-all's registration");
    }

    /// <summary>
    /// The other direction, and the reason the catch-all is guarded by <c>HasNamedRenderer</c>
    /// rather than being a bare <c>_ =&gt; true</c>: a hub WITHOUT the overlay must still tell the
    /// truth about an area nothing renders. A fix that made every missing area look like a
    /// compiling one would trade a false "does not exist" for a wait that never ends.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AreaThatGenuinelyHasNoRenderer_StillReportsAreaNotFound()
    {
        var control = await Render(PlainPath, TypeOwnedArea);

        AreaFrameClassifier.IsAreaNotFound(control).Should().BeTrue(
            "no overlay is in play here, so the area really has no renderer and must say so");
        AreaFrameClassifier.IsCompileProgress(control).Should().BeFalse(
            "nothing is compiling — presenting a permanent miss as a build in progress would turn "
            + "a clear verdict into a wait that never ends");
        AreaFrameClassifier.IsTransientFrame(control).Should().BeFalse(
            "a missing area is a verdict, not a promise");
    }

    /// <summary>
    /// The coupling the issue names: the two frames must be distinguishable BY A CONSUMER. Both
    /// are rendered here and each classifier is asserted against BOTH frames, so a marker that
    /// silently stopped being emitted (or started matching everything) fails here rather than
    /// months later in whichever waiter latched the wrong one.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task CompilingAndMissing_AreMechanicallyDistinguishable()
    {
        await CreateCompilingType();

        var compiling = await Render(OverlaidPath, TypeOwnedArea);
        var missing = await Render(PlainPath, TypeOwnedArea);

        AreaFrameClassifier.IsCompileProgress(compiling).Should().BeTrue();
        AreaFrameClassifier.IsCompileProgress(missing).Should().BeFalse();
        AreaFrameClassifier.IsAreaNotFound(missing).Should().BeTrue();
        AreaFrameClassifier.IsAreaNotFound(compiling).Should().BeFalse();
    }

    /// <summary>
    /// The NodeType the overlaid instance waits on, parked at <see cref="CompilationStatus.Compiling"/>:
    /// no sources, no configuration and no <c>RequestedReleaseAt</c>, so nothing kicks a compile and
    /// the state is stable for the length of the test.
    /// </summary>
    private async Task CreateCompilingType()
    {
        // Each [Fact] gets its own mesh (ShareMeshAcrossTests is off), so this is always a create.
        await MeshService.CreateNode(MeshNode.FromPath(PendingTypePath) with
        {
            Name = "PendingType",
            NodeType = "NodeType",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { CompilationStatus = CompilationStatus.Compiling }
        }).Should().Emit();
    }

    /// <summary>
    /// Renders one area through the layout client — the exact path the GUI drives — and waits for
    /// the first frame that carries a control for it.
    /// </summary>
    private async Task<UiControl?> Render(string addressPath, string area)
    {
        var client = GetClient();
        var reference = new LayoutAreaReference(area);
        var stream = client.GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(new Address(addressPath), reference);
        try
        {
            var control = await stream
                .GetControlStream(reference.Area!)
                .Should().Within(60.Seconds())
                .Match(c => c is not null,
                    $"{area} on {addressPath} must produce SOME frame — a hub that produces none is "
                    + "the eternal spinner both placeholders exist to prevent");
            Output.WriteLine($"{addressPath}/{area} → {control?.GetType().Name} (Id={control?.Id})");
            return control;
        }
        finally
        {
            stream.Dispose();
        }
    }
}
