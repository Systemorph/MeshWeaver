using System;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The two bake verdicts that used to exist only in a boot log (MeshWeaver#3703, #3704), and the
/// ONE property that makes publishing them worth anything: <b>"I measured nothing" must never read
/// the same as "I measured, and it was clean"</b>. Both directions are pinned here — a clean
/// reading, an absent one, and the reading that indicts — because a control that only ever asserts
/// the quiet case passes just as well when the instrument is gone.
/// </summary>
public class BakeCensusRegistryTest
{
    private static BakeReportReading Reading(int fromLocalAdoption, int stamps = 0) =>
        new(
            NodeTypeBakeReportRegistry.AdoptOnlyProbe,
            "sb43f9287dbd6922a7937bd24be103937",
            Total: 209,
            Baked: 5,
            Pending: 204,
            ClassifiedFromLocalAdoption: fromLocalAdoption,
            AdoptionStamps: stamps,
            Summary: "framework=sb43f928 total=209 baked=5 pending=204 frameworkstale=201",
            At: DateTimeOffset.UnixEpoch);

    [Fact]
    public void NoReportIsNotACleanReport_AndTheSentenceSaysSo()
    {
        new NodeTypeBakeReportRegistry().Latest.Should().BeNull();

        NodeTypeBakeReportRegistry.IsClean(null).Should().BeFalse(
            "a replica that published no bake report has measured NOTHING about its bake; calling "
            + "that clean is the missing-instrument-reads-as-a-pass bug #3703 is stuck in");

        NodeTypeBakeReportRegistry.Describe(null).Should()
            .Contain("NO bake report on this replica")
            .And.Contain("measured NOTHING")
            .And.Contain("NOT a clean bake",
                "the sentence has to REFUSE the clean reading in words, because /health prints a "
                + "description and nothing else");
    }

    [Fact]
    public void ACleanReport_IsClean_AndPublishesBothCountersInTheSameSentence()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(Reading(fromLocalAdoption: 0, stamps: 78));

        var reading = registry.Latest;
        reading.Should().NotBeNull();
        NodeTypeBakeReportRegistry.IsClean(reading).Should().BeTrue();

        var sentence = NodeTypeBakeReportRegistry.Describe(reading);
        sentence.Should()
            .Contain("total=209 baked=5 pending=204", "the numbers ARE the publication")
            .And.Contain("Adoption stamps held by this process: 78")
            .And.Contain("not comparable",
                "78 and 5 are different populations in different units — printing them side by "
                + "side without saying so would re-stage the misreading #3703 was filed as")
            .And.NotContain("PREDATED",
                "the positive control: a CLEAN reading must not carry the indictment, or the "
                + "assertion below could never fail");
    }

    [Fact]
    public void AReportWhoseSnapshotWasBehind_IsNotClean_AndNamesTheCount()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(Reading(fromLocalAdoption: 73, stamps: 78));

        NodeTypeBakeReportRegistry.IsClean(registry.Latest).Should().BeFalse();
        NodeTypeBakeReportRegistry.Describe(registry.Latest).Should()
            .Contain("PREDATED this replica's own prebuilt adoptions for 73 of 209 type(s)")
            .And.Contain("#3703");
    }

    [Fact]
    public void TheLastReadingWins_SoTheCheckAnswersAboutThisReplicaNow()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(Reading(fromLocalAdoption: 73));
        registry.Record(Reading(fromLocalAdoption: 0) with { Pass = NodeTypeBakeReportRegistry.CompilingSweep });

        registry.Latest!.Pass.Should().Be(NodeTypeBakeReportRegistry.CompilingSweep);
        NodeTypeBakeReportRegistry.IsClean(registry.Latest).Should().BeTrue();
    }

    private static SourceDiscoveryPass Pass(string query, double gapMs, int settled = 1236) =>
        new(query, settled, Chunks: 7, Items: settled, ElapsedMs: 2100,
            LargestGapMs: gapMs, WindowMs: 1000, At: DateTimeOffset.UnixEpoch);

    [Fact]
    public void NoDiscoveryPass_IsNotIndicted_ButTheSentenceStillSaysNothingWasMeasured()
    {
        var registry = new SourceDiscoveryRegistry();
        registry.Snapshot().Should().BeEmpty();
        registry.Widest.Should().BeNull();

        SourceDiscoveryRegistry.IsIndicted(null).Should().BeFalse(
            "a warm replica issues no discovery query at all, so an empty registry must not paint "
            + "every healthy portal Degraded — a check that can never pass is not a check");

        SourceDiscoveryRegistry.Describe(registry.Snapshot(), registry.Widest).Should()
            .Contain("NO source-discovery pass recorded on this replica")
            .And.Contain("absence of measurement, NOT a clean one",
                "the status is Healthy here, so the SENTENCE is the only thing that separates "
                + "'nothing ran' from 'it ran and was fine' — which is exactly what the census tag "
                + "exists to print");
    }

    [Fact]
    public void NarrowGaps_ExonerateTheCompletionRule_AndPointUpstream()
    {
        var registry = new SourceDiscoveryRegistry();
        registry.Record(Pass("nodeType:Code partitions:all", gapMs: 40));

        SourceDiscoveryRegistry.IsIndicted(registry.Widest).Should().BeFalse();
        SourceDiscoveryRegistry.Describe(registry.Snapshot(), registry.Widest).Should()
            .Contain("1 discovery query/queries measured")
            .And.Contain("settled at 1236 node(s) from 7 change(s)")
            .And.Contain("largest inter-chunk gap 40ms = 4% of the 1000ms completion window")
            .And.Contain("did NOT end any of these folds early")
            .And.Contain("would be upstream")
            .And.NotContain("mechanism #3704 names",
                "the positive control for the case below: a narrow-gap reading must not carry the "
                + "indictment, or that assertion could never fail");
    }

    [Fact]
    public void AGapReachingTheWindow_IndictsTheCompletionRule()
    {
        var registry = new SourceDiscoveryRegistry();
        registry.Record(Pass("nodeType:Code partitions:all", gapMs: 900, settled: 1145));

        SourceDiscoveryRegistry.IsIndicted(registry.Widest).Should().BeTrue(
            "900ms of 1000ms is 90% of the completion window, which is the mechanism #3704 names");
        SourceDiscoveryRegistry.Describe(registry.Snapshot(), registry.Widest).Should()
            .Contain("mechanism #3704 names")
            .And.Contain("settled at 1145 node(s)");
    }

    [Fact]
    public void ANarrowPassAfterAWideOne_DoesNotEraseTheEvidence()
    {
        var registry = new SourceDiscoveryRegistry();
        registry.Record(Pass("nodeType:Code partitions:all", gapMs: 900, settled: 1145));
        registry.Record(Pass("nodeType:Code partitions:all", gapMs: 30, settled: 1236));

        registry.Snapshot().Should().ContainSingle(
            "the per-query map holds the LAST reading for each distinct query")
            .Which.Settled.Should().Be(1236);

        SourceDiscoveryRegistry.IsIndicted(registry.Widest).Should().BeTrue(
            "the high-water reading only ever moves UP: a benign pass that follows a wide one must "
            + "not silently clear the evidence the next reader is looking for");
        registry.Widest!.Settled.Should().Be(1145);
    }

    [Fact]
    public void TheLogWarningAndTheHealthVerdict_ShareOneThreshold()
        => SourceDiscoveryRegistry.GapShareWarnPercent.Should().Be(50,
            "NodeTypeBatchBake's warning and SourceDiscoveryHealthCheck's Degraded both read this "
            + "constant, so they can never disagree about the same pass");
}
