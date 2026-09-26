using System.Collections.Immutable;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The NodeType bake gate may only ever stall a ROLL — it must never be able to take an
/// instance fully down</b> (#5544). Measured on memex.systemorph.com, 2026-09-25/26: after the V58
/// migration every portal pod failed its startup probe on
///
/// <code>
/// NodeType bake regressed on this image — refusing readiness so the rollout stalls with the
///   previous image still serving. 1 NodeType(s) regressed on this image: BinaryClickerV2/BinaryToggle
/// </code>
///
/// <para>— the NEW <c>ci.9332</c> pod, and then the OLD <c>ci.9218</c> pod when it restarted
/// ("2 regressed: BinaryClickerV2/BinaryToggle, Store/Review"). The gate's premise, "the previous
/// image is still serving", was false the moment the last old pod restarted, and nothing served
/// (nginx 503) until the gate was switched off by hand.</para>
///
/// <para>Two defects, one per test group below:</para>
/// <list type="number">
/// <item><b>A type that never built was read as working.</b> <c>BinaryToggle</c>'s source has
/// failed CS1929 since it was authored (#3883). Its record carries no assembly
/// (<see cref="BakeState.NeverBuilt"/>) and <see cref="NodeTypeBakeEntry.WasHealthy"/> counts that
/// as healthy, so every pod filed the failure as a regression. And the record could never learn
/// better: the Error stamp is a <see cref="MeshPublicationGate"/> publication, DISCARDED on a
/// refused pod — the failure refused the pod, the pod could not record the failure, the next pod
/// read "healthy" again. A regression now needs a WORKING build to regress from
/// (<see cref="NodeTypeBakeEntry.HadWorkingBuild"/>).</item>
/// <item><b>A restarted pod of the serving image refused itself.</b> A working build this very
/// platform build produced proves the image serves here; one a NEWER build produced means this is
/// the OLD image of a roll. Neither is evidence that this image broke anything
/// (<see cref="NodeTypeBakeEntry.IsRegressionBaselineFor"/>,
/// <see cref="NodeTypeBakeReport.ThisBuildHasServed"/>).</item>
/// </list>
///
/// <para>Every test drives the SHARED stamp (<see cref="DynamicTypePreWarmer.BaselineStamp"/>) into
/// the real <see cref="NodeTypeBakeGateState"/>, so the readiness verdict is asserted end to end
/// from a bake report, and <see cref="BuildProtocolDriver.IsGatingFailure"/> is asserted to agree.</para>
/// </summary>
public class TheBakeGateCannotTakeAnInstanceDownTest
{
    private const string Framework = "framework-1";
    private const string Old = "3.0.0-ci.9218";
    private const string New = "3.0.0-ci.9332";

    private static NodeTypeBakeEntry Entry(string path, BakeState state, string? producer = null) =>
        new(path, state) { ProducedByPlatformBuild = producer };

    private static NodeTypeBakeReport Report(string? live, params NodeTypeBakeEntry[] entries) =>
        new([.. entries], Framework) { LivePlatformVersion = live };

    /// <summary>Stamp one failed outcome off <paramref name="report"/> and feed it to an ARMED gate
    /// that then completes its sweep — the readiness verdict a pod with this report would reach.</summary>
    private static (NodeTypeBakeGateState Gate, PreWarmOutcome Outcome) Bake(
        NodeTypeBakeReport report, string failingType, PreWarmStatus status = PreWarmStatus.CompileError)
    {
        var outcome = DynamicTypePreWarmer.BaselineStamp(report)(
            new PreWarmOutcome(failingType, status, "CS1929: 'LayoutAreaHost' does not contain a definition for 'GetData'"));
        var gate = new NodeTypeBakeGateState { GatesReadiness = true };
        gate.MarkRunning("enumerating dynamic NodeTypes");
        gate.MarkOutcome(outcome);
        gate.MarkComplete("baked");
        return (gate, outcome);
    }

    // ---- 1. a type that never built cannot regress --------------------------------------------

    [Fact(Timeout = 60000)]
    public void ATypeThatNeverBuilt_OnAnEstablishedInstance_DoesNotRefuseReadiness()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The incident's shape on the NEW image: plenty built here before (so this is not a first
        // bake), and BinaryToggle — Ok on its record, no assembly — fails on this pod.
        var report = Report(New,
            Entry("Crm/Contact", BakeState.FrameworkStale, Old),
            Entry("BinaryClickerV2/BinaryToggle", BakeState.NeverBuilt));
        DynamicTypePreWarmer.IsFirstBake(report).Should().BeFalse("the negative control: this is an established instance");

        var (gate, outcome) = Bake(report, "BinaryClickerV2/BinaryToggle");

        outcome.WasHealthyBeforeBake.Should().BeTrue("#4496 stands: a never-built type is not called broken");
        outcome.HasRegressionBaseline.Should().BeFalse("no working build of this type is on record — there is nothing to regress FROM");
        BuildProtocolDriver.IsGatingFailure(outcome).Should().BeFalse("the GO and the gate read the same stamp and must agree");
        gate.Regressions.Should().BeEmpty();
        gate.WithoutBaseline.Keys.Should().Contain("BinaryClickerV2/BinaryToggle", "non-gating must not mean invisible");
        gate.ReadinessGranted.Should().BeTrue("one type that never compiled must not keep a whole image out of service");
        gate.Admission.Should().Be(MeshAdmission.Admitted,
            "an admitted pod RELEASES its held Error stamp, so the record finally reads Error — the loop that "
            + "kept BinaryToggle's record at Ok on every boot (#3883) is broken here");
    }

    [Fact(Timeout = 60000)]
    public void ATypeThatNeverBuilt_IsNotInTheRegressionBaseline_ButIsStillNotBroken()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var entry = Entry("BinaryClickerV2/BinaryToggle", BakeState.NeverBuilt);

        entry.WasHealthy.Should().BeTrue("the entry's own meaning is unchanged — a type nobody built is not known to be broken");
        entry.HadWorkingBuild.Should().BeFalse();
        entry.IsRegressionBaselineFor(New).Should().BeFalse();
        Entry("Kmu/Abandoned", BakeState.PreviouslyBroken).IsRegressionBaselineFor(New).Should().BeFalse();
    }

    // ---- 2. a restarted pod of the serving image does not refuse itself ------------------------

    [Fact(Timeout = 60000)]
    public void ARestartedPodOfTheServingImage_DoesNotRefuseItself()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The OLD ci.9218 pod restarting mid-roll: records it produced itself (so it has served
        // here), plus Store/Review, which the NEW image re-keyed and which the old image now cannot
        // follow. This is the pod the rollout falls back on; refusing it is the outage.
        var report = Report(Old,
            Entry("Crm/Contact", BakeState.Baked, Old),
            Entry("Store/Review", BakeState.FrameworkStale, New));
        report.ThisBuildHasServed.Should().BeTrue("a working build stamped by this very build proves a replica of it was admitted here");

        var (gate, outcome) = Bake(report, "Store/Review");

        outcome.HasRegressionBaseline.Should().BeFalse("this image IS the previous image — there is no other one to protect");
        BuildProtocolDriver.IsGatingFailure(outcome).Should().BeFalse();
        gate.ReadinessGranted.Should().BeTrue("the last serving pod of an instance must always be able to come back");
        report.GateRelevant.Should().BeEmpty("the witness-unreadable path must not refuse a serving image either");
    }

    [Fact(Timeout = 60000)]
    public void ABuildProducedByANewerImage_IsNotABaselineForTheOldOne()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Even with no record of its own left (the newer image re-keyed everything), the old image
        // is the OLD one: a type only a newer build could build does not regress on it.
        var report = Report(Old, Entry("Store/Review", BakeState.FrameworkStale, New));
        report.ThisBuildHasServed.Should().BeFalse();

        var (gate, outcome) = Bake(report, "Store/Review");

        outcome.HasRegressionBaseline.Should().BeFalse("only a newer build ever built it");
        gate.ReadinessGranted.Should().BeTrue();
    }

    [Fact(Timeout = 60000)]
    public void ABuildProducedByThisSameImage_IsNotEvidenceThatThisImageBrokeIt()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // This build produced the working build (bytes since lost): the image demonstrably CAN
        // build the type, so a failure now is content or environment, not an image regression.
        Entry("Crm/Contact", BakeState.BytesMissing, New).IsRegressionBaselineFor(New).Should().BeFalse();
    }

    // ---- the negative controls: a real regression still gates -----------------------------------

    [Fact(Timeout = 60000)]
    public void ATypeAnOlderImageBuilt_FailingOnANewImage_StillRefusesReadiness()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // The case the gate exists for: ci.9218 built Crm/Contact, ci.9332 cannot, and no ci.9332
        // replica has ever been admitted here. The old image is serving; stall the roll.
        var report = Report(New,
            Entry("Crm/Contact", BakeState.FrameworkStale, Old),
            Entry("Crm/Deal", BakeState.FrameworkStale, Old));
        report.ThisBuildHasServed.Should().BeFalse();

        var (gate, outcome) = Bake(report, "Crm/Contact");

        outcome.WasHealthyBeforeBake.Should().BeTrue();
        outcome.HasRegressionBaseline.Should().BeTrue();
        BuildProtocolDriver.IsGatingFailure(outcome).Should().BeTrue();
        gate.Phase.Should().Be(BakePhase.Regressed);
        gate.ReadinessGranted.Should().BeFalse("a type that an older image built and this one cannot is exactly a regression");
        report.GateRelevant.Select(e => e.TypePath).Should().Contain("Crm/Contact");
    }

    [Fact(Timeout = 60000)]
    public void AnUnknownProducer_KeepsTheStrictReading()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // A record from before CompiledPlatformVersion existed, or a process with no build stamp:
        // nothing establishes that this image produced it, so the gate stays strict.
        Entry("Crm/Contact", BakeState.FrameworkStale).IsRegressionBaselineFor(New).Should().BeTrue();
        Entry("Crm/Contact", BakeState.FrameworkStale, Old).IsRegressionBaselineFor(null).Should().BeTrue();

        var report = Report(null, Entry("Crm/Contact", BakeState.Baked, Old));
        report.ThisBuildHasServed.Should().BeFalse("an unknown live build can never match a producer");
    }

    [Fact(Timeout = 60000)]
    public void OnlyAWorkingBuild_ProvesThisImageServed()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // A NeverBuilt/PreviouslyBroken entry names no build, so a producer string on it (which the
        // probe never sets) must not count as this image having served.
        Report(New, Entry("X/Y", BakeState.NeverBuilt, New)).ThisBuildHasServed.Should().BeFalse();
        Report(New, Entry("X/Y", BakeState.Baked, New)).ThisBuildHasServed.Should().BeTrue();
    }
}
