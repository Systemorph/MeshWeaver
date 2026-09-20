using System;
using System.Collections.Generic;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The live half of <c>bake-report</c> — the one reading that is not taken at boot</b>
/// (MeshWeaver#4632).
///
/// <para>On 2026-09-17 a replica on another image re-stamped <c>Approvals/Desk</c> with its own
/// framework identity while two READY replicas ran the previous one; a customer letter rendered
/// <i>"No renderer is registered for area Approvals"</i> and every instrument was green:
/// <c>content-types</c> records only a degraded content READ, and <c>bake-report</c>'s plan and
/// outcome census had been taken before the stamp landed. Every case here holds the pure fold
/// against the record shapes that incident, and the ordinary states around it, actually take —
/// with a control on EACH side of the verdict, because a census that only ever asserts the quiet
/// case passes just as well when the instrument is gone.</para>
/// </summary>
public class LiveRecordCensusTest
{
    private const string Live = "s2902ab117d8b351f11b358de1755decd";
    private const string Other = "saec4a2dc1f075f1fff7cf076055e150e";
    private static readonly DateTimeOffset BootedAt = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset At = new(2026, 9, 17, 17, 20, 0, TimeSpan.Zero);

    /// <summary>A record exactly as the incident's <c>Approvals/Desk</c> read: Ok, adopted, stamped for another framework.</summary>
    private static NodeTypeDefinition Stamped(string framework, DateTimeOffset? succeededAt) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = framework,
        LatestAssemblyCollection = "nodetype-cache",
        LatestAssemblyPath = $"Approvals_Desk/v844-{framework[..8]}-6ad498520d75.dll",
        LastCompiledVersion = 844,
        LastCompileSucceededAt = succeededAt,
    };

    private static NodeTypeLiveRecordCensus Census(
        params (string Path, NodeTypeDefinition? Definition)[] records)
        => NodeTypeLiveRecordCensus.Of(records, Live, BootedAt, At);

    [Fact]
    public void AForeignRecordStampedAfterBoot_IsTheNeverBenignCount_AndDegrades()
    {
        var census = Census(("Approvals/Desk", Stamped(Other, BootedAt.AddHours(2))));

        census.Total.Should().Be(1);
        census.Foreign.Should().Be(1, "the record names a build keyed to a framework this replica does not run");
        census.ForeignSinceBoot.Should().Be(1,
            "the stamp landed AFTER this replica booted — a process on another image re-keyed the "
            + "type, which is the state no boot-time reading can see");
        census.IsClean.Should().BeFalse("this is the verdict that is never benign");
        census.ForeignDetail.Should().Be("saec4a2d×1 in Approvals/… (1 since boot)");
    }

    [Fact]
    public void AForeignRecordStampedBeforeBoot_IsCountedButClean()
    {
        // The control on the other side: the ordinary every-deploy state. The previous image's
        // records are foreign to a fresh replica until the bake or the first access rebuilds them,
        // and every adopt-only portal carries hundreds of these for its whole life.
        var census = Census(("Edu/Course", Stamped(Other, BootedAt.AddHours(-2))));

        census.Foreign.Should().Be(1, "it is still foreign, and it is still printed");
        census.ForeignSinceBoot.Should().Be(0);
        census.IsClean.Should().BeTrue(
            "degrading on the previous image's records would make the check one that cannot pass "
            + "on any adopt-only portal, which is how a detector stops being read");
        census.ForeignDetail.Should().Be("saec4a2d×1 in Edu/… (0 since boot)");
    }

    [Fact]
    public void ARecordForTheLiveFramework_OrNamingNoBuild_IsNotForeign()
    {
        var census = Census(
            ("Hosting/TriageItem", Stamped(Live, BootedAt.AddHours(5))),
            ("Doc/Fresh", new NodeTypeDefinition { Configuration = "config => config" }),
            ("Doc/Broken", new NodeTypeDefinition
            {
                Configuration = "config => config",
                CompilationStatus = CompilationStatus.Error,
                CompiledFrameworkVersion = Other,
                LatestAssemblyCollection = "nodetype-cache",
                LatestAssemblyPath = "Doc_Broken/v3-saec4a2d-x.dll",
                LastCompiledVersion = 3,
            }));

        census.Total.Should().Be(3);
        census.Foreign.Should().Be(0,
            "a build for THIS framework is usable here; a record naming no build claims nothing; and "
            + "an Error passes through as Error — it is 'correct the code', never a foreign build "
            + "(NodeTypeBuildIdentity.ReportedStatus folds only a successful build)");
        census.IsClean.Should().BeTrue();
        census.ForeignDetail.Should().BeEmpty();
    }

    [Fact]
    public void AForeignStampWithNoTime_IsForeign_ButNeverSinceBoot()
    {
        var census = Census(("Store/Catalog", Stamped(Other, succeededAt: null)));

        census.Foreign.Should().Be(1);
        census.ForeignSinceBoot.Should().Be(0,
            "an absent stamp time may not decide the never-benign count in either direction");
        census.IsClean.Should().BeTrue();
    }

    [Fact]
    public void AnUntypedRecord_IsInTheDenominator_AndDecidedAboutByNothing()
    {
        var census = Census(
            ("Crm/Offer", null),
            ("Approvals/Desk", Stamped(Other, BootedAt.AddMinutes(1))));

        census.Total.Should().Be(2, "the denominator counts what was enumerated, typed or not");
        census.Untyped.Should().Be(1);
        census.Foreign.Should().Be(1, "an untyped record is neither foreign nor clean — it is unmeasured");
        NodeTypeLiveRecordCensus.Describe(census).Should()
            .Contain("of which 1 could not be typed on this hub and were decided about by nothing");
    }

    [Fact]
    public void TheDetailIsBounded_AndNamesTheSinceBootGroupsFirst()
    {
        var records = new List<(string, NodeTypeDefinition?)>();
        for (var i = 0; i < 20; i++)
            records.Add(($"P{i:00}/Type", Stamped(Other, BootedAt.AddHours(-1))));
        records.Add(("Zulu/Type", Stamped(Other, BootedAt.AddHours(1))));

        var census = NodeTypeLiveRecordCensus.Of(records, Live, BootedAt, At);

        census.Foreign.Should().Be(21);
        census.ForeignSinceBoot.Should().Be(1);
        census.ForeignDetail.Should().StartWith("saec4a2d×1 in Zulu/… (1 since boot)",
            "the group that carries the never-benign count is named first, whatever its partition sorts as");
        census.ForeignDetail.Should().EndWith("; +9 more group(s)",
            "twelve groups are named and the rest are counted — a mesh whose every record is foreign "
            + "must not turn the health body into a listing");
    }

    // ── the sentence ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoCensus_SaysSoInWords_AndIsNeverCalledClean()
    {
        NodeTypeLiveRecordCensus.Describe(null).Should()
            .Contain("LIVE RECORD CENSUS: NONE taken on this replica")
            .And.Contain("absence of measurement, NOT a clean one",
                "'I measured nothing' and 'I measured, and it was clean' must be two different "
                + "printed sentences — this body is the whole publication");
    }

    [Fact]
    public void AReKeyedRecord_TheSentenceNamesThePartitionAndBothIdentities_NotTheNode()
    {
        var census = Census(("Approvals/Desk", Stamped(Other, BootedAt.AddHours(2))));

        var sentence = NodeTypeLiveRecordCensus.Describe(census);
        sentence.Should()
            .StartWith("🚨 LIVE RECORD CENSUS")
            .And.Contain("1 NodeType record(s) were RE-KEYED to a framework this replica does not run AFTER it booted")
            .And.Contain("saec4a2d×1 in Approvals/…", "the partition routes the finding to an owner")
            .And.Contain("this replica's framework is s2902ab1",
                "both identities, always — the pair is what makes a refusal checkable by hand against "
                + "the DLL name and the CD run that produced it")
            .And.Contain("Denominator: 1 dynamic NodeType record(s)")
            .And.NotContain("Approvals/Desk",
                "this body is PUBLIC and unauthenticated: a node title is the surface #3890 closed");
    }

    [Fact]
    public void ACleanCensus_AndAnOldForeignOne_AreTwoDifferentSentences()
    {
        NodeTypeLiveRecordCensus.Describe(Census(("Hosting/TriageItem", Stamped(Live, At)))).Should()
            .Contain("every record names a build for this replica's framework")
            .And.NotContain("🚨")
            .And.Contain("Denominator: 1 dynamic NodeType record(s)");

        NodeTypeLiveRecordCensus.Describe(Census(("Edu/Course", Stamped(Other, BootedAt.AddDays(-1))))).Should()
            .Contain("ALL stamped before this replica booted")
            .And.Contain("NONE was re-keyed since boot")
            .And.NotContain("🚨",
                "the previous image's records are the ordinary state and must not read as the incident");
    }

    // ── the registry, and the verdict the host prints ─────────────────────────────────────────────

    [Fact]
    public void TheRegistryFoldsTheLiveHalfIntoTheVerdict_AndAnAbsentLiveHalfDegradesNothing()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(new BakeReportReading(
            NodeTypeBakeReportRegistry.AdoptOnlyProbe, Live,
            Total: 218, Baked: 217, Pending: 1, ClassifiedFromLocalAdoption: 0, AdoptionStamps: 78,
            Summary: "framework=s2902ab1 total=218 baked=217 pending=1", At: At));

        registry.LiveRecords.Should().BeNull();
        NodeTypeBakeReportRegistry.IsCleanIncludingLive(registry.Latest, registry.LiveRecords).Should().BeTrue(
            "a replica still coming up has not emitted a live reading yet; degrading every boot on it "
            + "would teach readers to ignore the entry — the absence is printed instead");
        NodeTypeBakeReportRegistry.DescribeIncludingLive(registry.Latest, registry.LiveRecords).Should()
            .Contain("total=218 baked=217 pending=1", "the boot-time reading is still the first half")
            .And.Contain("LIVE RECORD CENSUS: NONE taken on this replica");

        registry.RecordLiveRecords(Census(("Approvals/Desk", Stamped(Other, BootedAt.AddHours(2)))));

        NodeTypeBakeReportRegistry.IsCleanIncludingLive(registry.Latest, registry.LiveRecords).Should().BeFalse(
            "the boot-time reading says 217 of 218 baked and the LIVE one says a record was re-keyed "
            + "after boot — the entry must degrade on the second, which is exactly the reading the "
            + "2026-09-17 replicas had no instrument for");
        NodeTypeBakeReportRegistry.IsClean(registry.Latest).Should().BeTrue(
            "the control: the boot-time half alone still reads clean, so the degradation is the live half's");
        NodeTypeBakeReportRegistry.DescribeIncludingLive(registry.Latest, registry.LiveRecords).Should()
            .Contain("🚨 LIVE RECORD CENSUS")
            .And.Contain("saec4a2d×1 in Approvals/… (1 since boot)");

        registry.RecordLiveRecords(Census(("Approvals/Desk", Stamped(Live, BootedAt.AddHours(3)))));
        NodeTypeBakeReportRegistry.IsCleanIncludingLive(registry.Latest, registry.LiveRecords).Should().BeTrue(
            "a later reading REPLACES the earlier one: the census is the catalog as it stands, not a history");
    }

    [Fact]
    public void AFaultedWatch_Degrades_AndSaysTheLastReadingIsFrozen()
    {
        var registry = new NodeTypeBakeReportRegistry();
        registry.Record(new BakeReportReading(
            NodeTypeBakeReportRegistry.AdoptOnlyProbe, Live,
            Total: 218, Baked: 217, Pending: 1, ClassifiedFromLocalAdoption: 0, AdoptionStamps: 78,
            Summary: "framework=s2902ab1 total=218 baked=217 pending=1", At: At));
        registry.RecordLiveRecords(Census(("Hosting/TriageItem", Stamped(Live, At))));
        NodeTypeBakeReportRegistry.IsCleanIncludingLive(registry.Latest, registry.LiveRecords, registry.LiveRecordsFault)
            .Should().BeTrue("the control: a clean reading from a live watch is clean");

        registry.RecordLiveRecordsFault("UnanchoredQueryException: the catalog query was refused");

        NodeTypeBakeReportRegistry.IsCleanIncludingLive(registry.Latest, registry.LiveRecords, registry.LiveRecordsFault)
            .Should().BeFalse(
                "an ARMED instrument that broke is not one that has not emitted yet — its last reading "
                + "is frozen, and a frozen reading read as current is the boot-time defect one level up");
        NodeTypeBakeReportRegistry.DescribeIncludingLive(registry.Latest, registry.LiveRecords, registry.LiveRecordsFault)
            .Should()
            .Contain("LIVE RECORD CENSUS WATCH FAULTED (UnanchoredQueryException: the catalog query was refused)")
            .And.Contain("FROZEN")
            .And.Contain("every record names a build for this replica's framework",
                "the last reading is still printed — it was true when taken — beside the fault that dates it");
    }
}
