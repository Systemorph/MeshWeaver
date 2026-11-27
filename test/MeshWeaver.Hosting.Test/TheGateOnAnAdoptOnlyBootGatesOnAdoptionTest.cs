using System.Collections.Immutable;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>Bake at BUILD</b> (owner directive, 2026-11-27: "we must bake at build"). An instance that
/// compiles nothing at boot (<c>PreWarm:DynamicTypes</c> off) used to leave an armed
/// <c>PreWarm:GateReadiness</c> permanently green: the gate read state only the sweep wrote. The gate
/// now certifies the ADOPTION of the CI-built bundles — <see cref="NodeTypeBakeGateState.ApplyAdoption"/>.
/// Gate-on/sweep-off is a measured configuration, not a contradiction to work around.
/// </summary>
public class TheGateOnAnAdoptOnlyBootGatesOnAdoptionTest
{
    private static NodeTypeBakeEntry Entry(string path, BakeState state, string? producedBy = null) =>
        new(path, state) { ProducedByPlatformBuild = producedBy };

    private static NodeTypeBakeReport Report(params NodeTypeBakeEntry[] entries) =>
        new(entries.ToImmutableList(), "framework-1") { LivePlatformVersion = "4.1.200" };

    private static NodeTypeBakeGateState Armed(bool allowUnproven = false) =>
        new() { GatesReadiness = true, AllowUnprovenBake = allowUnproven };

    [Fact(Timeout = 60000)]
    public void AnArmedGate_IsNotReadyUntilTheAdoptionIsMeasured()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = Armed();
        gate.MarkRunning("adopting the prebuilt bundles CI built for this image");

        gate.ReadinessGranted.Should().BeFalse("a pod that has not measured its adoption has proven nothing");
        gate.Admission.Should().Be(MeshAdmission.Provisional);
    }

    [Fact(Timeout = 60000)]
    public void EveryTypeAdopted_PassesTheGate()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = Armed();
        gate.MarkRunning("adopting");

        var proven = gate.ApplyAdoption(
            Report(Entry("Crm/Contact", BakeState.Baked), Entry("Crm/Mail", BakeState.Baked)), TimeSpan.FromSeconds(3));

        proven.Should().BeTrue();
        gate.Phase.Should().Be(BakePhase.Complete);
        gate.ReadinessGranted.Should().BeTrue();
        gate.Admission.Should().Be(MeshAdmission.Admitted);
        gate.Detail.Should().Contain("adopted=2").And.Contain("nothing compiled");
    }

    [Fact(Timeout = 60000)]
    public void ATypeAServingImageHadButCiDidNotDeliver_RefusesReadiness_NamingIt()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = Armed();
        gate.MarkRunning("adopting");

        var proven = gate.ApplyAdoption(
            Report(
                Entry("Crm/Contact", BakeState.Baked),
                Entry("Crm/Mail", BakeState.FrameworkStale, producedBy: "4.1.100")),
            TimeSpan.FromSeconds(3));

        proven.Should().BeFalse();
        gate.Phase.Should().Be(BakePhase.Faulted, "nothing failed to compile - the proof is missing, so it is UNPROVEN, not regressed");
        gate.ReadinessGranted.Should().BeFalse();
        gate.Admission.Should().Be(MeshAdmission.Refused);
        gate.Detail.Should().Contain("Crm/Mail");
    }

    [Fact(Timeout = 60000)]
    public void TheOperatorHatchAndAServedBuild_RelaxAnUnprovenAdoption_ButNotTheRecordedPhase()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var uncovered = Report(
            Entry("Crm/Mail", BakeState.BytesMissing, producedBy: "4.1.100"), Entry("Crm/Contact", BakeState.Baked));

        var hatch = Armed(allowUnproven: true);
        hatch.ApplyAdoption(uncovered, TimeSpan.Zero);
        hatch.ReadinessGranted.Should().BeTrue("AllowUnprovenBake is the documented escape hatch");
        hatch.Phase.Should().Be(BakePhase.Faulted, "the hatch changes the verdict, never the recorded state");

        var restarted = Armed();
        restarted.ApplyAdoption(uncovered, TimeSpan.Zero);
        restarted.MarkServedBefore();
        restarted.ReadinessGranted.Should().BeTrue("a restart of an image that already served must never refuse itself");
    }

    [Fact(Timeout = 60000)]
    public void AnImageThatAlreadyServed_AFirstEverBake_AndAnAlreadyBrokenType_NeverHoldTheRoll()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var served = Armed();
        served.ApplyAdoption(
            Report(
                Entry("Crm/Mail", BakeState.FrameworkStale, producedBy: "4.1.100"),
                Entry("Crm/Contact", BakeState.Baked, producedBy: "4.1.200")),
            TimeSpan.Zero).Should().BeTrue("a working build this very platform build produced proves the image serves here");

        var first = Armed();
        first.ApplyAdoption(Report(Entry("A/B", BakeState.NeverBuilt), Entry("C/D", BakeState.NeverBuilt)), TimeSpan.Zero)
            .Should().BeTrue("a brand-new instance has no previous image to protect");

        var broken = Armed();
        broken.ApplyAdoption(Report(Entry("A/B", BakeState.PreviouslyBroken), Entry("C/D", BakeState.Baked)), TimeSpan.Zero)
            .Should().BeTrue("a type that was already broken is not made worse by this image");
    }

    [Fact(Timeout = 60000)]
    public void ANoTypeMesh_IsTriviallyAdopted()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var gate = Armed();
        gate.ApplyAdoption(NodeTypeBakeReport.Empty("framework-1"), TimeSpan.Zero).Should().BeTrue();
        gate.Phase.Should().Be(BakePhase.Complete);
    }
}
