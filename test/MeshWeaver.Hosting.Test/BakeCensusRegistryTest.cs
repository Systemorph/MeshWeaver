using System;
using MeshWeaver.Graph.Configuration;
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
    private static BakeReportReading Reading(
        int fromLocalAdoption, int stamps = 0, string ownership = "") =>
        new(
            NodeTypeBakeReportRegistry.AdoptOnlyProbe,
            "sb43f9287dbd6922a7937bd24be103937",
            Total: 209,
            Baked: 5,
            Pending: 204,
            ClassifiedFromLocalAdoption: fromLocalAdoption,
            AdoptionStamps: stamps,
            Summary: "framework=sb43f928 total=209 baked=5 pending=204 frameworkstale=201",
            At: DateTimeOffset.UnixEpoch)
        {
            Ownership = ownership,
        };

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

    /// <summary>
    /// 🚨 <b>The count resolves to an owner in the SENTENCE</b> (#4258). <c>/health</c> prints a
    /// description and nothing else, so an identity that reaches the registry and not the sentence
    /// is still dropped. <c>previouslybroken=1</c> is the one record anywhere that a NodeType is
    /// broken for good — the rollout gate skips it on purpose — and the RLS-filtered sweep a reader
    /// can run cannot name it.
    /// </summary>
    [Fact]
    public void TheSentenceResolvesTheCountToAPartition_WithoutNamingTheNode()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(Reading(
            fromLocalAdoption: 0, stamps: 78,
            ownership: "previouslybroken in BinaryClickerV2/…; frameworkstale in Edu/…, Store/…"));

        NodeTypeBakeReportRegistry.IsClean(registry.Latest).Should().BeTrue(
            "a permanently-broken type must NOT flip the verdict — the gate skips it so one "
            + "abandoned NodeType cannot freeze the platform's deploys, and the census tag is what "
            + "prints this reading although it is Healthy");

        NodeTypeBakeReportRegistry.Describe(registry.Latest).Should()
            .Contain("previouslybroken in BinaryClickerV2/…",
                "the whole point is that the number can be routed to an owner")
            .And.NotContain("BinaryToggle",
                "this body is PUBLIC and unauthenticated: the partition routes the finding, a node "
                + "title is the surface #3890 closed");
    }

    /// <summary>
    /// 🚨 <b>THE BRIDGE — the exact call the identity was dropped at</b> (#4258).
    ///
    /// <para>Every case above stages a <see cref="BakeReportReading"/> with <c>Ownership</c> already
    /// populated, so all of them would pass while the projection from the REPORT to the READING kept
    /// dropping the entries' paths — which is precisely the defect. This holds that projection
    /// directly: a report carrying a permanently-broken type must produce a reading that names its
    /// partition, and a sentence that still refuses the node's own title.</para>
    /// </summary>
    [Fact]
    public void TheReportToReadingBridge_CarriesTheIdentity_NotOnlyTheCounts()
    {
        var report = new NodeTypeBakeReport(
            [
                new NodeTypeBakeEntry("Doc/Architecture/Fine", BakeState.Baked),
                new NodeTypeBakeEntry("BinaryClickerV2/BinaryToggle", BakeState.PreviouslyBroken),
            ],
            "sd608997c1a94e2f8b3d5a6079e4c1b23");

        var reading = DynamicTypePreWarmer.ReadingOf(
            report, NodeTypeBakeReportRegistry.AdoptOnlyProbe, adoptionStamps: 78,
            at: DateTimeOffset.UnixEpoch);

        reading.Total.Should().Be(2, "the counts must keep working — this is a widening, not a swap");
        reading.Ownership.Should().Be(report.Ownership,
            "the reduction to a /health line is where every entry's TypePath was discarded, so a "
            + "guard that stages the reading instead of the report checks nothing about it");
        NodeTypeBakeReportRegistry.Describe(reading).Should()
            .Contain("previouslybroken in BinaryClickerV2/…")
            .And.NotContain("BinaryToggle",
                "and the bridge must not smuggle a node title onto a public body either");
    }

    /// <summary>
    /// The positive control for the case above: a reading with nothing outstanding carries no
    /// ownership clause at all, so that assertion is reading the reading and not a constant.
    /// </summary>
    [Fact]
    public void AReadingWithNothingOutstanding_CarriesNoOwnershipClause() =>
        NodeTypeBakeReportRegistry.Describe(Reading(fromLocalAdoption: 0, stamps: 78))
            .Should().NotContain("Non-baked types by partition",
                "an empty clause printed anyway would make the ownership assertion unfalsifiable");

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


    // ── the OUTCOME census (#4645): the post-bytes-win verdict, as opposed to the plan ──────────

    private static NodeTypeBakeReportRegistry Swept(params (string Path, PreWarmStatus Status)[] verdicts)
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(Reading(fromLocalAdoption: 0, stamps: 78));
        foreach (var (path, status) in verdicts)
            registry.RecordOutcome(new PreWarmOutcome(path, status));
        registry.RecordSettlement("completed");
        return registry;
    }

    /// <summary>
    /// 🚨 <b>The reading that must exist for the census to be worth anything.</b> A bake report can
    /// be published, clean and complete while the sweep that would say whether this replica can
    /// SERVE those types never ran at all. Those are two different facts, so they are two different
    /// printed sentences — and the absent one may never read as the clean one.
    /// </summary>
    [Fact]
    public void NoSweepOutcome_IsNotACleanReplica_AndSaysSoInWords()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(Reading(fromLocalAdoption: 0, stamps: 78));

        var reading = registry.Latest!;
        reading.OutcomesReached.Should().Be(0);
        reading.SweepSettlement.Should().BeEmpty();

        NodeTypeBakeReportRegistry.Describe(reading).Should()
            .Contain("OUTCOME CENSUS: NO sweep has reported an outcome on this replica")
            .And.Contain("a PLAN, not an outcome")
            .And.Contain("NOT a clean replica",
                "the whole publication is a sentence, so the refusal has to be IN the sentence");
    }

    /// <summary>
    /// The other half of the pair, and the one that makes the assertion above falsifiable: a sweep
    /// that ran and found everything servable prints a DIFFERENT sentence, with its denominator.
    /// </summary>
    [Fact]
    public void ASweepThatFoundEverythingServable_PrintsACleanReadingWithItsDenominator()
    {
        var sentence = NodeTypeBakeReportRegistry.Describe(
            Swept(("Edu/Course", PreWarmStatus.Compiled),
                  ("Store/Item", PreWarmStatus.AlreadyBaked)).Latest);

        sentence.Should()
            .Contain("OUTCOME CENSUS (sweep completed)")
            .And.Contain("every one of the 2 type(s) that reached a verdict has a usable assembly")
            .And.Contain("Denominator: 2 of 209 enumerated type(s) reached a verdict",
                "a zero that does not state what it is a zero OF could equally mean 'I could not "
                + "look' — the defect class this census belongs to")
            .And.NotContain("NO sweep has reported",
                "the positive control: the clean reading must not carry the absent one's words, or "
                + "the test above could never fail")
            .And.NotContain("NO usable assembly",
                "nor the indictment");
    }

    /// <summary>
    /// 🚨 <b>The verdict that is never benign</b>, and the reason the census exists: a type this
    /// replica has no assembly for renders the area-not-found frame on every page that asks for one
    /// — the symptom a maintainer reported on 2026-09-17 with no instrument that could name it.
    /// </summary>
    [Fact]
    public void TypesWithNoUsableAssembly_AreNamedByPartition_NeverByNode()
    {
        var registry = Swept(
            ("Approvals/Desk", PreWarmStatus.CompileError),
            ("Approvals/Workspace", PreWarmStatus.UpstreamFailed),
            ("Edu/Course", PreWarmStatus.Compiled));

        var reading = registry.Latest!;
        reading.NoUsableAssembly.Should().Be(2);
        reading.UsableHere.Should().Be(1);

        NodeTypeBakeReportRegistry.Describe(reading).Should()
            .Contain("2 NodeType(s) have NO usable assembly on this replica")
            .And.Contain("area-not-found frame",
                "the sentence has to connect the verdict to the symptom a human actually sees, or "
                + "nobody reading /health will link the two")
            .And.Contain("in Approvals")
            .And.NotContain("Approvals/Desk",
                "this body is PUBLIC and unauthenticated: the partition routes the finding to an "
                + "owner, the node's own title is the surface #3890 closed");
    }

    /// <summary>
    /// 🚨 <b>"I did not find out" is a third answer</b>, and folding it into either of the other two
    /// is the defect this census is built against. A timed-out type is not healthy and is not
    /// broken.
    /// </summary>
    [Fact]
    public void AnUnknownVerdict_IsNeitherUsableNorUnusable()
    {
        var reading = Swept(
            ("Edu/Course", PreWarmStatus.Compiled),
            ("Store/Item", PreWarmStatus.TimedOut),
            ("Lib/Shared", PreWarmStatus.UpstreamUnevaluated)).Latest!;

        reading.UsableHere.Should().Be(1);
        reading.NoUsableAssembly.Should().Be(0);
        reading.Unknown.Should().Be(2);

        NodeTypeBakeReportRegistry.Describe(reading).Should()
            .Contain("2 reached none (timed out, or waiting on something that did)",
                "an unmeasured type must be VISIBLE in the sentence, not absorbed into the clean "
                + "count — otherwise a sweep that measured almost nothing reads as a pass");
    }

    /// <summary>
    /// 🚨 <b>The cry-wolf control.</b> A type its own repository withdrew is not a defect on this
    /// replica, and counting it as one would make the census fire on every completed retirement —
    /// which is exactly how a detector stops being read. It is still COUNTED, so the denominator
    /// reconciles and a reader can tell a retirement wave from breakage.
    /// </summary>
    [Fact]
    public void AWithdrawnType_IsCountedButNotAsBreakage()
    {
        var reading = Swept(
            ("Crm/Mail", PreWarmStatus.Removed),
            ("Legacy/Thing", PreWarmStatus.Retired),
            ("Edu/Course", PreWarmStatus.Compiled)).Latest!;

        reading.NoUsableAssembly.Should().Be(0);
        reading.Withdrawn.Should().Be(2);
        reading.OutcomesReached.Should().Be(3);

        NodeTypeBakeReportRegistry.Describe(reading).Should()
            .Contain("2 were withdrawn by their own repository")
            .And.NotContain("NO usable assembly on this replica",
                "a retirement is not breakage, and a census that says it is will be ignored the "
                + "next time it is right");
    }

    /// <summary>
    /// 🚨 <b>THE EXHAUSTIVE CONTROL.</b> Every member of <see cref="PreWarmStatus"/> is classified,
    /// the four buckets partition the population exactly, and — the property that matters — a
    /// status nobody classified counts as NO USABLE ASSEMBLY rather than joining the healthy count.
    /// A new status added without a thought must surface as something to look at; an unclassified
    /// outcome reading as a pass is the failure this whole census is built against.
    /// </summary>
    [Fact]
    public void EveryStatusIsClassified_AndTheBucketsPartitionThePopulation()
    {
        var statuses = Enum.GetValues<PreWarmStatus>();
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(Reading(fromLocalAdoption: 0, stamps: 78));
        foreach (var status in statuses)
            registry.RecordOutcome(new PreWarmOutcome($"P{(int)status}/Type", status));

        var reading = registry.Latest!;
        reading.OutcomesReached.Should().Be(statuses.Length);
        (reading.UsableHere + reading.NoUsableAssembly + reading.Unknown + reading.Withdrawn)
            .Should().Be(statuses.Length,
                "the four buckets must partition the population — a status falling through every "
                + "arm would be counted nowhere and the denominator would stop reconciling");

        reading.UsableHere.Should().Be(2,
            "and ONLY Compiled and AlreadyBaked are usable: a gate that insisted on a fresh "
            + "compile would fail every pod that inherited a good cache, and anything else "
            + "counted usable would be a silent pass");
    }

    /// <summary>
    /// The census folds onto whichever reading the host prints, whenever the verdicts arrive — the
    /// sweep publishes its report before it emits a single outcome, so a census held in a second
    /// object would print a plan from one instant and an outcome from another.
    /// </summary>
    [Fact]
    public void TheCensusFoldsOntoTheReadingTheHostPrints_WhicheverOrderTheyArriveIn()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.RecordOutcome(new PreWarmOutcome("Approvals/Desk", PreWarmStatus.CompileError));
        registry.Latest.Should().BeNull(
            "no report has been published yet, so there is no reading to fold onto — and the "
            + "registry must not invent one");

        registry.Record(Reading(fromLocalAdoption: 0, stamps: 78));
        registry.Latest!.NoUsableAssembly.Should().Be(1,
            "a verdict that arrived before the report must not be lost: the host prints ONE "
            + "reading, and both halves have to be on it");

        registry.RecordOutcome(new PreWarmOutcome("Edu/Course", PreWarmStatus.Compiled));
        registry.Latest!.OutcomesReached.Should().Be(2);
        registry.Latest!.UsableHere.Should().Be(1);
    }

    /// <summary>
    /// A sweep that FAULTED is neither clean nor a verdict — the sentence names the settlement, so
    /// "it finished and found nothing" and "it died partway" cannot read the same.
    /// </summary>
    [Fact]
    public void AFaultedSweep_SaysSo_RatherThanReadingAsAFinishedOne()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(Reading(fromLocalAdoption: 0, stamps: 78));
        registry.RecordOutcome(new PreWarmOutcome("Edu/Course", PreWarmStatus.Compiled));
        registry.RecordSettlement("faulted");

        NodeTypeBakeReportRegistry.Describe(registry.Latest).Should()
            .Contain("OUTCOME CENSUS (sweep faulted)")
            .And.NotContain("(sweep completed)",
                "a sweep that died partway through its population has measured a PREFIX of it, and "
                + "printing that as a finished census is the same lie as a skipped gate painted "
                + "green");
    }

    [Fact]
    public void TheLogWarningAndTheHealthVerdict_ShareOneThreshold()
        => SourceDiscoveryRegistry.GapShareWarnPercent.Should().Be(50,
            "NodeTypeBatchBake's warning and SourceDiscoveryHealthCheck's Degraded both read this "
            + "constant, so they can never disagree about the same pass");
}
