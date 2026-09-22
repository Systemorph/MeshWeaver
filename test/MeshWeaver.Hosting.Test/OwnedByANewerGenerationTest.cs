using System;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The mid-roll cross-stamp has ONE reader, and the bind path yields on exactly what the
/// census reports</b> (the other half of MeshWeaver#4632).
///
/// <para>On 2026-09-22 the memex control instance rolled from <c>3.0.0-ci.9106</c> to <c>9162</c>.
/// Within minutes of the new replica booting, its own <c>/health</c> read <i>"34 NodeType record(s)
/// were RE-KEYED to a framework this replica does not run AFTER it booted"</i>: every fresh
/// activation on the two DRAINING replicas found a record the new generation had stamped, took the
/// framework-stale heal, recompiled under the OLD identity and stamped that back — which the new
/// replica then healed forward again. Both generations flipped the same types Pending in turn, and
/// the roll's second replica sat in <i>"NodeType bake in progress"</i> for a quarter of an hour.
/// The census saw it and named it; the bind path had no notion of it and healed anyway.</para>
///
/// <para>The rule is pure and shared: a record is <b>owned by a newer generation</b> when its
/// build is foreign to this process AND it was stamped after this process started. The census
/// counts that set as <c>ForeignSinceBoot</c>; the bind path refuses to recompile it. These cases
/// hold the reader against the record shapes that incident, and the ordinary post-roll state
/// around it, actually take — with a control on each side, because a yield that fired on the
/// ordinary state would freeze every platform roll on the framework-stale overlay.</para>
/// </summary>
public class OwnedByANewerGenerationTest
{
    private const string Live = "sdc4cbaa0e03ddf01c781cabc070e5ef4";
    private const string Newer = "s6f66941d6aba8e4dd141454d38c9258d";
    private static readonly DateTimeOffset BootedAt = new(2026, 9, 22, 8, 55, 57, TimeSpan.Zero);

    /// <summary>A record exactly as the incident's re-keyed <c>Hosting/…</c> types read on the draining replica: Ok, adopted, stamped by the other image.</summary>
    private static NodeTypeDefinition Stamped(string framework, DateTimeOffset? succeededAt) => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = framework,
        LatestAssemblyCollection = "nodetype-cache",
        LatestAssemblyPath = $"Hosting_InstanceAction/v12-{framework[..8]}-0f0e0d0c0b0a.dll",
        LastCompiledVersion = 12,
        LastCompileSucceededAt = succeededAt,
    };

    [Fact]
    public void AForeignStampAfterBoot_IsOwnedByANewerGeneration_SoTheBindPathYields()
    {
        var record = Stamped(Newer, BootedAt.AddMinutes(7));

        NodeTypeBuildIdentity.OwnedByANewerGeneration(record, Live, BootedAt).Should().BeTrue(
            "the record names a build for a framework this process does not run, stamped after this "
            + "process started — a replica on another image owns the type mid-roll, and a heal from "
            + "here would re-key it backwards");
    }

    [Fact]
    public void AForeignStampBeforeBoot_IsTheOrdinaryPostRollState_AndIsStillHealed()
    {
        // The control on the other side: every platform roll leaves the previous image's builds
        // behind, stamped BEFORE the new process started. That is the state the compile watcher
        // has always healed, and a yield here would freeze the roll on the overlay.
        var record = Stamped(Newer, BootedAt.AddHours(-3));

        NodeTypeBuildIdentity.OwnedByANewerGeneration(record, Live, BootedAt).Should().BeFalse(
            "a foreign build stamped before this process started is the previous image's, not a "
            + "newer generation's — the ordinary post-roll heal must still run");
        NodeTypeBuildIdentity.ReportedStatus(record, Live).Should().Be(CompilationStatus.Foreign,
            "the control must be a genuinely FOREIGN record, or the case would pass over a build "
            + "this process could simply adopt");
    }

    [Fact]
    public void AForeignStampWithNoTime_IsNeverOwnedByANewerGeneration()
    {
        // An absent reading may not decide the never-benign side in either direction — the same
        // rule the census applies to its since-boot count.
        var record = Stamped(Newer, succeededAt: null);

        NodeTypeBuildIdentity.StampedAfter(record, BootedAt).Should().BeFalse();
        NodeTypeBuildIdentity.OwnedByANewerGeneration(record, Live, BootedAt).Should().BeFalse(
            "a stamp with no time is foreign of UNKNOWN age; yielding on it would leave a type "
            + "un-healed on the strength of a reading nobody took");
    }

    [Fact]
    public void ARecordForTheLiveFramework_IsNeverOwnedByAnother_WhateverItsStamp()
    {
        var justStamped = Stamped(Live, BootedAt.AddMinutes(30));

        NodeTypeBuildIdentity.StampedAfter(justStamped, BootedAt).Should().BeTrue(
            "the time half alone says 'after boot' — this process compiled it itself");
        NodeTypeBuildIdentity.OwnedByANewerGeneration(justStamped, Live, BootedAt).Should().BeFalse(
            "a build for THIS framework is this process's own, and the time half alone must never "
            + "turn it into someone else's");
    }

    [Fact]
    public void ANullOrUnbuiltRecord_IsOwnedByNobody()
    {
        NodeTypeBuildIdentity.OwnedByANewerGeneration(null, Live, BootedAt).Should().BeFalse();
        NodeTypeBuildIdentity.StampedAfter(null, BootedAt).Should().BeFalse();

        var pending = Stamped(Newer, BootedAt.AddMinutes(1)) with
        {
            CompilationStatus = CompilationStatus.Pending,
            LatestAssemblyPath = null,
            LatestAssemblyCollection = null,
        };
        NodeTypeBuildIdentity.OwnedByANewerGeneration(pending, Live, BootedAt).Should().BeFalse(
            "a record with no successful build claims nothing about a framework, so there is nothing to yield to");
    }

    /// <summary>
    /// 🚨 The census and the bind path must AGREE, record by record — one reader, two consumers.
    /// A census that named a cross-stamp the bind path then healed is the exact defect this
    /// closes, so the agreement is asserted over the incident's own mixed population.
    /// </summary>
    [Fact]
    public void TheCensusSinceBootCount_IsExactlyTheSetTheBindPathYieldsOn()
    {
        (string Path, NodeTypeDefinition? Definition)[] records =
        [
            ("Hosting/InstanceAction", Stamped(Newer, BootedAt.AddMinutes(7))),   // re-keyed mid-roll
            ("Store/Package", Stamped(Newer, BootedAt.AddMinutes(12))),           // re-keyed mid-roll
            ("UWDeepfield/Submission", Stamped(Newer, BootedAt.AddHours(-2))),    // previous image's
            ("Crm/Client", Stamped(Newer, succeededAt: null)),                    // unknown age
            ("Essentials/Note", Stamped(Live, BootedAt.AddMinutes(3))),          // this process's own
            ("BinaryClickerV2/BinaryToggle", null),                               // untyped
        ];

        var census = NodeTypeLiveRecordCensus.Of(records, Live, BootedAt, BootedAt.AddMinutes(20));
        var yielded = records
            .Where(r => NodeTypeBuildIdentity.OwnedByANewerGeneration(r.Definition, Live, BootedAt))
            .Select(r => r.Path)
            .ToList();

        Assert.Equal(["Hosting/InstanceAction", "Store/Package"], yielded);
        census.ForeignSinceBoot.Should().Be(yielded.Count,
            "what the census reports as re-keyed since boot is precisely what the bind path refuses to heal");
        census.Foreign.Should().Be(4, "the previous image's build and the undated one are foreign but not since-boot");
        census.IsClean.Should().BeFalse();
    }
}
