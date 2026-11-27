using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #8413 — the sources watcher publishes ONE source generation per record:
/// <see cref="NodeTypeCompilationHelpers.PublishSourceSnapshot"/>.
///
/// <para>The defect: an emission whose content fingerprint is INCONCLUSIVE (the <c>@@</c>-include
/// closure could not be read) kept the previous fingerprint beside the NEW snapshot. A bundle baked
/// from the previous sources then matched that stale fingerprint, a pending adoption stamp was
/// judged <see cref="BuildProvenance.AdoptedVerified"/>, and <c>CompiledSources</c> became the new
/// snapshot — the record read "Up to date" over bytes built from the previous source, which is the
/// shape the issue reports. Red before the fix: the stamp is consumed and the record is not dirty.</para>
/// </summary>
public class AStampIsNeverJudgedAgainstAnUnmeasuredGenerationTest
{
    private const string FingerprintN = "aaaaaaaaaaaaaaaa";
    private const string FingerprintN1 = "bbbbbbbbbbbbbbbb";

    private static readonly ImmutableDictionary<string, long> SnapshotN =
        ImmutableDictionary<string, long>.Empty
            .Add("P/App/Source/code", 10)
            .Add("P/App/Run/Test/note", 11);

    private static readonly ImmutableDictionary<string, long> SnapshotN1 =
        SnapshotN.SetItem("P/App/Source/code", 20);

    /// <summary>The owner's record right after a bundle baked from N was offered and adopted:
    /// its own live pair is still N's, and the adoption asked for the stamp.</summary>
    private static NodeTypeDefinition AdoptedFromN() => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Ok,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "P_App_Run/v3-n.dll",
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        CurrentSourceVersions = SnapshotN,
        CurrentSourceFingerprint = FingerprintN,
        AdoptedSourceFingerprint = FingerprintN,
        RequestedSourceStampAt = DateTimeOffset.UtcNow,
        CompiledSources = null,
    };

    private static NodeTypeDefinition Stamp(NodeTypeDefinition d, IReadOnlyDictionary<string, long> live)
        => NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(d, live, canCompileLocally: true);

    [Fact]
    public void AMovedSnapshotWithAnInconclusiveFingerprint_LeavesTheStampStanding()
    {
        var published = NodeTypeCompilationHelpers.PublishSourceSnapshot(
            AdoptedFromN(), SnapshotN1, fingerprint: null, includes: null,
            moduleVersion: (null, false), Stamp);

        published.Should().NotBeNull("the snapshot moved, so the record is written");
        published!.CurrentSourceVersions.Should().Equal(SnapshotN1);
        published.CurrentSourceFingerprint.Should().BeNull(
            "N's fingerprint does not describe N+1's sources; nothing was measured for them");
        published.RequestedSourceStampAt.Should().NotBeNull(
            "the stamp waits for a conclusive measurement instead of being judged against N's");
        published.BuildProvenance.Should().NotBe(BuildProvenance.AdoptedVerified);
        published.IsDirty.Should().BeTrue(
            "bytes built from N must never read 'Up to date' over N+1's sources");
        NodeTypeCompilationHelpers.CanJudgeAdoption(published, published.CurrentSourceVersions!)
            .Should().BeFalse("every other judge defers on the same record too");
    }

    [Fact]
    public void TheNextConclusivePublication_RefusesTheStaleBundle()
    {
        var deferred = NodeTypeCompilationHelpers.PublishSourceSnapshot(
            AdoptedFromN(), SnapshotN1, fingerprint: null, includes: null,
            moduleVersion: (null, false), Stamp)!;

        var judged = NodeTypeCompilationHelpers.PublishSourceSnapshot(
            deferred, SnapshotN1, FingerprintN1, includes: ImmutableList<string>.Empty,
            moduleVersion: (null, false), Stamp);

        judged.Should().NotBeNull();
        judged!.RequestedSourceStampAt.Should().BeNull("a conclusive publication consumes the request");
        judged.CurrentSourceFingerprint.Should().Be(FingerprintN1);
        judged.BuildProvenance.Should().NotBe(BuildProvenance.AdoptedVerified,
            "N's bytes over N+1's sources are not a verified adoption");
        judged.IsDirty.Should().BeTrue("the record never claims N's bytes are current");
    }

    [Fact]
    public void Control_AConclusiveMatchingPublication_StillVerifies()
    {
        var published = NodeTypeCompilationHelpers.PublishSourceSnapshot(
            AdoptedFromN(), SnapshotN, FingerprintN, includes: ImmutableList<string>.Empty,
            moduleVersion: (null, false), Stamp);

        published.Should().NotBeNull("a pending stamp is always consumed");
        published!.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified);
        published.IsDirty.Should().BeFalse();
        published.RequestedSourceStampAt.Should().BeNull();
    }

    [Fact]
    public void Control_AnUnchangedSnapshotWithAnInconclusiveFingerprint_KeepsItsFingerprint()
    {
        var current = AdoptedFromN() with
        {
            RequestedSourceStampAt = null,
            CompiledSources = SnapshotN,
            BuildProvenance = BuildProvenance.AdoptedVerified,
        };

        NodeTypeCompilationHelpers.PublishSourceSnapshot(
                current, SnapshotN, fingerprint: null, includes: null,
                moduleVersion: (null, false), Stamp)
            .Should().BeNull("nothing moved and nothing was measured: no write, the fingerprint still describes the snapshot");
    }

    /// <summary>A record that already reads AdoptedVerified over a producer fingerprint the live
    /// source no longer has: the live shape of the Crm 4.2 incident, where the old build kept
    /// serving, IsDirty=false, and every release was "satisfied by the existing build".</summary>
    private static NodeTypeDefinition VerifiedThenMoved() => AdoptedFromN() with
    {
        RequestedSourceStampAt = null,
        CompiledSources = SnapshotN,
        BuildProvenance = BuildProvenance.AdoptedVerified,
        CurrentSourceFingerprint = FingerprintN1,
    };

    [Fact]
    public void AVerifiedAdoptionWhoseLiveFingerprintMoved_IsRejudgedAndRefused()
    {
        var current = VerifiedThenMoved();
        NodeTypeCompilationHelpers.ContradictsLiveSource(current).Should().BeTrue();

        var judged = NodeTypeCompilationHelpers.PublishSourceSnapshot(
            current, SnapshotN, FingerprintN1, includes: ImmutableList<string>.Empty,
            moduleVersion: (null, false), Stamp);

        judged.Should().NotBeNull("the contradiction is a write, never a no-op");
        judged!.BuildProvenance.Should().NotBe(BuildProvenance.AdoptedVerified);
        judged.IsDirty.Should().BeTrue("the record must not claim the old bytes are current");
        NodeTypeCompilationHelpers.ContradictsLiveSource(judged).Should().BeFalse();
    }

    [Fact]
    public void Control_AVerifiedAdoptionWhoseFingerprintMatches_IsNotContradictedAndWritesNothing()
    {
        var current = VerifiedThenMoved() with { CurrentSourceFingerprint = FingerprintN };
        NodeTypeCompilationHelpers.ContradictsLiveSource(current).Should().BeFalse();

        NodeTypeCompilationHelpers.PublishSourceSnapshot(
                current, SnapshotN, FingerprintN, includes: null,
                moduleVersion: (null, false), Stamp)
            .Should().BeNull("a matching verified adoption is left alone");
    }
}
