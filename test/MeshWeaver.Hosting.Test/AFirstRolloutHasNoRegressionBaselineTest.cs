using System.Collections.Immutable;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The FIRST rollout of a brand-new instance has nothing to protect</b> — measured on
/// <c>pearl.meshweaver.cloud</c>, 2026-09-15/16. A freshly provisioned portal served <c>503</c> at
/// the edge for nine hours with a RUNNING pod:
///
/// <code>
/// Health check nodetype_bake with status Unhealthy … 'NodeType bake regressed on this image —
///   refusing readiness so the rollout stalls with the previous image still serving.
///   2 NodeType(s) regressed on this image: GoogleMaps/Gallery, MyAi/Panel'
/// </code>
///
/// <para>Neither type ever built there: the plugin registry marks both packages
/// <c>preInstalled</c>, so their CONTENT arrives on every instance, while the modules they bind
/// (<c>MeshWeaver.Blazor.GoogleMaps</c> and friends) are store-delivered and had not landed. Every
/// entry in that instance's bake report was <see cref="BakeState.NeverBuilt"/>, and
/// <see cref="NodeTypeBakeEntry.WasHealthy"/> counts NeverBuilt as healthy — so a first-ever
/// compile failure was filed as a REGRESSION and the pod gated itself forever.</para>
///
/// <para>The gate's own sentence is the argument: it refuses readiness <i>"so the rollout stalls
/// with the previous image still serving"</i>. On a first rollout there is no previous image and
/// no previous pod. Refusing readiness protects nobody and yields an instance that can never
/// become ready — the failure mode is total, and at the edge it looks like a dead application
/// rather than a content problem.</para>
///
/// <para>So the baseline is emptied for that one case, and ONLY that case: a report in which every
/// type is NeverBuilt. One baked type means a working instance, where a new failure can genuinely
/// be this image's doing and still gates. Nothing is swallowed either — the outcome is recorded in
/// <see cref="NodeTypeBakeGateState.WithoutBaseline"/> and named in the health payload.</para>
/// </summary>
public class AFirstRolloutHasNoRegressionBaselineTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static NodeTypeBakeReport Report(params (string Path, BakeState State)[] entries) =>
        new(entries.Select(e => new NodeTypeBakeEntry(e.Path, e.State)).ToImmutableList(), "framework-1");

    [Fact(Timeout = 60000)]
    public void EveryTypeNeverBuilt_IsAFirstBake_SoNothingHasABaseline()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var report = Report(("GoogleMaps/Gallery", BakeState.NeverBuilt), ("MyAi/Panel", BakeState.NeverBuilt));

        report.Entries.Should().OnlyContain(e => e.State == BakeState.NeverBuilt);
        report.Entries.Should().OnlyContain(e => e.WasHealthy,
            "the ENTRY's own meaning is unchanged — the baseline is emptied by the sweep, not by redefining NeverBuilt");
    }

    [Fact(Timeout = 60000)]
    public void AFailureWithNoBaseline_DoesNotGate_AndIsNamed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome(
            "GoogleMaps/Gallery", PreWarmStatus.CompileError, "CS0246: MeshWeaver.Blazor.GoogleMaps")
        {
            WasHealthyBeforeBake = false,
        });

        watch.Should().BeFalse("there is no recovery to watch for — the type never built here");
        gate.Regressions.Should().BeEmpty("a type that never built cannot have regressed on this image");
        gate.WithoutBaseline.Keys.Should().Contain("GoogleMaps/Gallery");
        gate.Phase.Should().Be(BakePhase.Running);

        gate.MarkComplete("baked in 00:02:00 — compiled=40 alreadyBaked=0");

        gate.ReadinessGranted.Should().BeTrue("a fresh instance must be able to serve its own content");
        gate.Detail.Should().Contain("GoogleMaps/Gallery").And.Contain("no working build to regress from",
            "non-blocking must not mean invisible — this is the line that explains a Degraded new portal");
    }

    [Fact(Timeout = 60000)]
    public void OnAnEstablishedInstance_ACompileErrorStillGates()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The negative control. One baked type means a working instance: a failure there can be
        // this image's doing, and the gate keeps its full strictness.
        var report = Report(("Crm/Contact", BakeState.Baked), ("Crm/Mail", BakeState.NeverBuilt));
        report.Entries.Should().Contain(e => e.State == BakeState.Baked);

        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");

        var watch = gate.MarkOutcome(new PreWarmOutcome("Crm/Contact", PreWarmStatus.CompileError, "CS0246")
        {
            WasHealthyBeforeBake = true,
        });

        watch.Should().BeTrue();
        gate.Phase.Should().Be(BakePhase.Regressed);
        gate.WithoutBaseline.Should().BeEmpty();
        gate.ReadinessGranted.Should().BeFalse("a type that used to build and no longer does still holds the pod back");
    }
}
