using System.Collections.Immutable;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A consumed release request must LEAVE A RELEASE FOR THE BUILD IT ASKED FOR
/// (MeshWeaver.Plugins#781).
///
/// <para>Measured on memex-cloud, <c>Publish/Deck</c>, 2026-08-27: the node settled
/// <c>CompilationStatus.Ok</c> over current sources, <c>lastCompiledVersion 575</c>,
/// <c>latestAssemblyPath</c> naming v575's bytes — and <c>latestReleasePath</c> naming a release cut
/// the PREVIOUS MORNING, with <c>lastReleaseRequestHandledAt</c> equal to <c>requestedReleaseAt</c>,
/// i.e. the request spent. Nothing retried, nothing errored, and every single-field check reads
/// healthy; only comparing the release against the build reveals it. A consumer following
/// <c>LatestReleasePath</c> — the build protocol, the release-pinned activation, the GUI's
/// "→ release" link — therefore adopts the PREVIOUS build while the node reports Ok on the
/// current one. Silent wrongness, self-stabilising, discovered by somebody noticing that a merged
/// fix was not live.</para>
///
/// <para>The invariant, from the issue: <b><c>latestReleasePath</c> must never be older than
/// <c>lastCompiledVersion</c> while <c>requestedReleaseAt</c> has been consumed.</b> It is
/// answerable from the node alone: a release version is <c>{yyyyMMddHHmmss}-{hash}</c> and the hash
/// is minted from the build's durable assembly-store coordinates, so the mint and the check are the
/// same function applied twice.</para>
/// </summary>
public class ReleaseNamesTheBuildTest
{
    private const string Collection = "assemblies";
    private const string Bytes575 = "Publish_Deck/v575-s8929555-aadb349047af.dll";
    private const string Bytes569 = "Publish_Deck/v569-s8929555-0be1c4d21f77.dll";
    private const string SourcePath = "Publish/Deck/Source/Deck";

    /// <summary>A release path exactly as <c>TryCreateReleaseNode</c> mints it for
    /// <paramref name="contentPath"/> — same hash function, so this is the real shape.</summary>
    private static string ReleasePathFor(string contentPath, string minted) =>
        "Publish/Deck/Release/" + minted + "-"
        + NodeTypeBuildState.ReleaseVersionHash($"{Collection}/{contentPath}");

    private static MeshNode Node() => new("Deck", "Publish");

    /// <summary>The #781 shape: settled Ok, sources clean, a usable build of v575's bytes — and
    /// whatever release path the case under test wants to put beside it.</summary>
    private static NodeTypeDefinition Def(string? latestReleasePath) => new()
    {
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        LatestAssemblyCollection = Collection,
        LatestAssemblyPath = Bytes575,
        LastCompiledVersion = 575,
        CompiledSources = ImmutableDictionary<string, long>.Empty.Add(SourcePath, 42),
        CurrentSourceVersions = ImmutableDictionary<string, long>.Empty.Add(SourcePath, 42),
        LatestReleasePath = latestReleasePath,
    };

    [Fact]
    public void AReleaseMintedForTheseBytes_NamesThem()
        => NodeTypeBuildState
            .ReleaseNamesBuild(Def(ReleasePathFor(Bytes575, "20260827215301")))
            .Should().BeTrue(
                "the mint and the check are the same hash over the same durable coordinates — if "
                + "this ever disagrees the two have drifted and every verdict below is worthless");

    [Fact]
    public void AReleaseMintedForOTHERBytes_IsDetected()
        => NodeTypeBuildState
            .ReleaseNamesBuild(Def(ReleasePathFor(Bytes569, "20260826065548")))
            .Should().BeFalse(
                "this is the measured #781 state: build v575, release cut for v569's bytes the "
                + "previous morning");

    [Fact]
    public void NoReleaseAtAll_IsInconclusive_NotStale()
        => NodeTypeBuildState.ReleaseNamesBuild(Def(null)).Should().BeTrue(
            "absence is not staleness — a build ADOPTED from a prebuilt bundle has never had a "
            + "release, and #1707 slice 3 deliberately answers such a request from the build in "
            + "hand; calling it stale would put a compile back on every install");

    [Fact]
    public void NoDurableCoordinates_IsInconclusive()
        => NodeTypeBuildState
            .ReleaseNamesBuild(Def(ReleasePathFor(Bytes569, "20260826065548")) with
            {
                LatestAssemblyCollection = null
            })
            .Should().BeTrue(
                "the Null-store path mints from a process-local assembly location no other "
                + "process can reproduce — the check cannot tell, so it must not accuse");

    [Fact]
    public void AVersionThisMintDidNotProduce_IsInconclusive()
        => NodeTypeBuildState
            .ReleaseNamesBuild(Def("Publish/Deck/Release/hand-written-v1"))
            .Should().BeTrue("a foreign version shape is not evidence of anything");

    /// <summary>
    /// 🚨 THE DEFECT. A release request answered by the build in hand is consumed on the same
    /// commit path a dispatch would use — so it can never re-fire. Consuming it while the node's
    /// release names DIFFERENT bytes is what makes #781 permanent: the request is spent, the
    /// release stays older than the build, and a plain (unforced) re-request is absorbed the same
    /// way, so the state cannot be repaired by asking again.
    /// </summary>
    [Fact]
    public void ARequestIsNotSatisfiedWhileTheReleaseNamesOtherBytes()
        => NodeTypeCompilationHelpers
            .IsSatisfiedByCurrentBuild(
                Node(), Def(ReleasePathFor(Bytes569, "20260826065548")), guards: null)
            .Should().BeFalse(
                "the request asks for a RELEASE of this build; consuming it against a release "
                + "minted for other bytes produces nothing and can never be re-asked");

    [Fact]
    public void ARequestISSatisfiedWhenTheReleaseNamesTheBuild()
        => NodeTypeCompilationHelpers
            .IsSatisfiedByCurrentBuild(
                Node(), Def(ReleasePathFor(Bytes575, "20260827215301")), guards: null)
            .Should().BeTrue(
                "#1707 slice 3 stands where it is honest: the release the request asks for "
                + "already exists and names these exact bytes");

    [Fact]
    public void AnAdoptedBuildWithNoReleaseIsStillSatisfied()
        => NodeTypeCompilationHelpers
            .IsSatisfiedByCurrentBuild(Node(), Def(null), guards: null)
            .Should().BeTrue(
                "the install path adopts prebuilt bytes and then requests a release; answering "
                + "that from the build in hand is #1707 slice 3's whole point, and this test is "
                + "what stops the #781 fix from quietly putting the compile back");

    [Fact]
    public void AForcedRequestAlwaysCompiles()
        => NodeTypeCompilationHelpers
            .IsSatisfiedByCurrentBuild(
                Node(),
                Def(ReleasePathFor(Bytes575, "20260827215301")) with { RequestedReleaseForce = true },
                guards: null)
            .Should().BeFalse("force is the user's escape hatch and outranks every optimisation");

    [Fact]
    public void ADirtyDefinitionIsNeverSatisfied()
        => NodeTypeCompilationHelpers
            .IsSatisfiedByCurrentBuild(
                Node(),
                Def(ReleasePathFor(Bytes575, "20260827215301")) with
                {
                    CurrentSourceVersions = ImmutableDictionary<string, long>.Empty.Add(SourcePath, 43)
                },
                guards: null)
            .Should().BeFalse("the sources moved since the build — this request must compile them");
}
