using System;
using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Issue #781 — the release POST-CONDITION as a pure verdict: once a release request has been
/// CONSUMED, <see cref="NodeTypeDefinition.LatestReleasePath"/> must never name a build older than
/// <see cref="NodeTypeDefinition.LastCompiledVersion"/>.
///
/// <para>The production state it exists to catch (<c>Publish/Deck</c>, 2026-08-27):
/// <c>lastCompiledVersion 575</c>, <c>compilationStatus Ok</c>, sources current, an assembly built
/// — and <c>latestReleasePath</c> pointing at a release cut the previous day, with
/// <c>lastReleaseRequestHandledAt == requestedReleaseAt</c> marking the trigger spent. Healthy from
/// every angle; every instance binding yesterday's bytes.</para>
///
/// <para>These cases pin the two halves that keep the check honest: it FIRES on every way the build
/// can have moved past the standing release, and it stays SILENT wherever the answer is
/// inconclusive or the request was never consumed — a check that cried wolf on a first build or an
/// adopted bundle would be turned off, and the settle path re-cuts a release on its verdict. The
/// end-to-end behaviour (a refused attributed create, and the release restored from the bytes in
/// hand) is pinned on a real mesh by <c>ReleasePostConditionAtSettleTest</c> in
/// MeshWeaver.Plugins.</para>
/// </summary>
public class ReleasePostConditionTest
{
    private static readonly DateTimeOffset Requested = new(2026, 8, 27, 21, 52, 59, TimeSpan.Zero);

    private static ImmutableDictionary<string, long> Sources(long version) =>
        ImmutableDictionary<string, long>.Empty.Add("Publish/Deck/Source/Deck", version);

    /// <summary>The node as the compile watcher observed it at dispatch: a consumed request, and a
    /// release cut for the PREVIOUS build (store version 569).</summary>
    private static NodeTypeDefinition Consumed() => new()
    {
        Configuration = "config => config",
        CompilationStatus = CompilationStatus.Compiling,
        RequestedReleaseAt = Requested,
        LastReleaseRequestHandledAt = Requested,
        LastCompiledVersion = 569,
        LatestAssemblyCollection = "assemblies",
        LatestAssemblyPath = "Publish_Deck/v569-s8929555-aadb349047af.dll",
        LatestReleasePath = "Publish/Deck/Release/20260826065548-neI3XM25",
        CompiledSources = Sources(569),
    };

    /// <summary>The compile that just succeeded — store version 575, new bytes.</summary>
    private static NodeCompilationResult Built(
        long? version = 575, string? contentPath = "Publish_Deck/v575-s8929555-aadb349047af.dll",
        string? collection = "assemblies", long sourceVersion = 569)
        => new(
            AssemblyLocation: "/cache/Publish_Deck/Deck.dll",
            NodeTypeConfigurations: [],
            CompiledSources: Sources(sourceVersion),
            Collection: collection,
            ContentPath: contentPath,
            Version: version);

    [Fact]
    public void AConsumedRequest_WhoseReleaseNamesAnEarlierBuild_IsAViolation()
    {
        var violation = ReleasePostCondition.Violation(Consumed(), Built(), newReleasePath: null);

        violation.Should().NotBeNull(
            "this is the incident: the request is spent, the compile produced build 575, and the "
            + "only release on the node was cut for 569 — nothing will ever revisit it");
        violation.Should().Contain("20260826065548-neI3XM25", "the verdict must NAME the stale release");
        violation.Should().Contain("575", "…and the build it is stale against");
    }

    [Fact]
    public void AReleaseCutOnThisSettle_HoldsTheInvariant()
        => ReleasePostCondition.Violation(
                Consumed(), Built(), newReleasePath: "Publish/Deck/Release/20260827215301-abcd1234")
            .Should().BeNull("a release for these exact bytes just landed");

    [Fact]
    public void ARequestStillStanding_IsNotThisChecksBusiness()
    {
        // handled < requested: the trigger has NOT been consumed. The release watcher's own
        // contract is that it re-fires on the next settled emission — pre-empting that here would
        // cut a release for a build the pending request is about to supersede.
        var standing = Consumed() with { LastReleaseRequestHandledAt = Requested.AddSeconds(-30) };

        ReleasePostCondition.Violation(standing, Built(), newReleasePath: null).Should().BeNull();
    }

    [Fact]
    public void ABuildNobodyAskedToRelease_IsNotAViolation()
    {
        // A first-build kickoff / an adopted bundle: no request was ever made, so the absence of a
        // matching release is inconclusive — never evidence of a lost one (#3010's rule, kept).
        var never = Consumed() with { RequestedReleaseAt = null, LastReleaseRequestHandledAt = null };

        ReleasePostCondition.Violation(never, Built(), newReleasePath: null).Should().BeNull();
    }

    [Fact]
    public void AConsumedRequest_WithNoReleaseAtAll_IsAViolation()
    {
        var noRelease = Consumed() with { LatestReleasePath = null };

        ReleasePostCondition.Violation(noRelease, Built(), newReleasePath: null)
            .Should().Contain("NO release",
                "the request asked for a release and the compile succeeded — an empty "
                + "latestReleasePath is the same defect with nothing to compare against");
    }

    [Fact]
    public void TheSameBuild_SettlingAgain_IsNotAViolation()
    {
        // An idempotent settle (same store version, same coordinates, same sources): the standing
        // release genuinely names these bytes, so there is nothing to restore and nothing to shout
        // about. This is the case that keeps the check from re-cutting a release on every compile.
        var same = Built(version: 569, contentPath: "Publish_Deck/v569-s8929555-aadb349047af.dll");

        ReleasePostCondition.Violation(Consumed(), same, newReleasePath: null).Should().BeNull();
    }

    [Fact]
    public void AMovedAssemblyPath_IsEnoughEvidence_EvenWithoutAStoreVersion()
    {
        // A producer that reports coordinates but no integer store version still moved the bytes.
        var moved = Built(version: null);

        ReleasePostCondition.Violation(Consumed(), moved, newReleasePath: null)
            .Should().Contain("assembly path");
    }

    [Fact]
    public void AChangedSourceSnapshot_IsEnoughEvidence_OnAProducerWithNoStore()
    {
        // No store at all (Null-store hosts): the compiled-source snapshot is the identity that is
        // always present, and a release cut from other sources is a release of another build.
        var storeless = Built(version: null, contentPath: null, collection: null, sourceVersion: 999);

        ReleasePostCondition.Violation(Consumed(), storeless, newReleasePath: null)
            .Should().Contain("compiled-source snapshot");
    }

    [Fact]
    public void NoIdentityFactAtAll_IsInconclusive_NeverAViolation()
    {
        // Nothing the result carries can distinguish the builds — never invent a violation from an
        // absence: the remedy is a WRITE (a re-cut release node), so a false positive is a write.
        var blind = Built(version: null, contentPath: null, collection: null, sourceVersion: 569);

        ReleasePostCondition.Violation(Consumed(), blind, newReleasePath: null).Should().BeNull();
    }

    [Fact]
    public void AnUnreadableDefinition_IsNotJudged()
        => ReleasePostCondition.Violation(before: null, Built(), newReleasePath: null)
            .Should().BeNull("a definition the settle could not read says nothing about releases");
}
