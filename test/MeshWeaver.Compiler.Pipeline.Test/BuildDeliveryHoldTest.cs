using System.Collections.Generic;
using System.Collections.Immutable;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 A NodeType never ERRORS because of delivery (MeshWeaver#3583, measured 2026-09-09): the
/// pure decisions, pinned with no mesh.
///
/// <para><b>The measurement.</b> Plugins#1555 — a one-line CSS change — was synced into a live
/// portal 35 minutes after merging, while the only bundle for that portal's framework identity was
/// baked from the OLD sources. The record ended <c>compilationStatus: Error</c>,
/// <c>buildProvenance: AdoptionRefused</c>, with a working assembly still named on it
/// (<c>Essentials_Email/v331-…</c>), and every page of the type rendered <i>"the platform refused
/// to run it"</i> for the afternoon.</para>
///
/// <para><b>The rule (the portal owner, 2026-09-09).</b> Adoption is a MODULE-VERSION
/// COMPATIBILITY check, not a fingerprint match: same MAJOR ⇒ the last build keeps serving
/// (stale-but-serving, naming both versions, a bundle pending); a MAJOR bump ⇒ the build is
/// refused, and even then the type reports "incompatible, awaiting bundle", never a dead page;
/// a matching bundle ⇒ adopts and a person is told.</para>
/// </summary>
public class BuildDeliveryHoldTest
{
    private static readonly ImmutableDictionary<string, long> LiveSources =
        ImmutableDictionary.CreateRange(new Dictionary<string, long>
        {
            ["Essentials/Email/Source/EmailLayoutAreas"] = 638_000_000_000_000_000,
        });

    /// <summary>The record exactly as the incident left it before the judgement: an adopted,
    /// working build on the coordinates, the bundle's fingerprint, and a live fingerprint that
    /// has moved past it.</summary>
    private static NodeTypeDefinition SourceMovedPast(
        string? adoptedVersion, string? currentVersion, BuildProvenance provenance = BuildProvenance.AdoptedVerified) =>
        new()
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "Essentials_Email/v331-s414bfb2-cb41a4639f44.dll",
            LatestAssemblyMvid = "3f73419cac5047d3bbce89206f7e377f",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            AdoptedSourceFingerprint = "572183a89fca620b",
            CurrentSourceFingerprint = "4ed23561cfd142d1",
            AdoptedModuleVersion = adoptedVersion,
            CurrentModuleVersion = currentVersion,
            CurrentSourceVersions = LiveSources,
            CompiledSources = LiveSources,
            BuildProvenance = provenance,
            RequestedSourceStampAt = System.DateTimeOffset.UtcNow,
        };

    // ── The compatibility rule ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.2.2", "1.2.3", ModuleVersionVerdict.Compatible)]
    [InlineData("1.2.2", "1.3", ModuleVersionVerdict.Compatible)]
    [InlineData("v1.9.0", "1.10.0-ci.12", ModuleVersionVerdict.Compatible)]
    [InlineData("1.2.2", "2.0.0", ModuleVersionVerdict.Incompatible)]
    [InlineData("2.0.0", "1.9.9", ModuleVersionVerdict.Incompatible)]
    [InlineData(null, "1.2.3", ModuleVersionVerdict.Unknown)]
    [InlineData("1.2.2", null, ModuleVersionVerdict.Unknown)]
    [InlineData("main", "1.2.3", ModuleVersionVerdict.Unknown)]
    [InlineData("", "", ModuleVersionVerdict.Unknown)]
    public void TheRule_IsTheMajor_AndUnknownNeverRefuses(string? adopted, string? current, ModuleVersionVerdict expected)
        => ModuleVersionCompatibility.Classify(adopted, current).Should().Be(expected);

    // ── Case 1: same major → the build keeps serving ────────────────────────────────────────

    [Fact]
    public void SameMajor_KeepsServing_AsStaleAdopted_OnAMeshThatWillNotCompile()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            SourceMovedPast("1.2.2", "1.2.3"), LiveSources, canCompileLocally: false);

        result.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted,
            "a fingerprint that differs says the source MOVED; same MAJOR says the build may stay");
        result.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "there IS a usable build and no compile failed — this is the state that used to be Error");
        result.LatestAssemblyPath.Should().Be("Essentials_Email/v331-s414bfb2-cb41a4639f44.dll",
            "the last build this mesh holds keeps serving");
        result.LatestAssemblyMvid.Should().Be("3f73419cac5047d3bbce89206f7e377f");
        result.IsDirty.Should().BeTrue("the build IS behind the source — say so");
        result.RequestedSourceStampAt.Should().BeNull("the request is consumed on every path");
        NodeTypeExecutionGate.Evaluate(result).Should().Be(BuildExecutionVerdict.Permitted,
            "stale-but-compatible bytes over newer source is the accepted trade-off; a dead page is not");
    }

    [Fact]
    public void SameMajor_OnAMeshThatCompiles_KeepsServing_WhileTheLiveSourceIsCompiled()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            SourceMovedPast("1.2.2", "1.2.3"), LiveSources, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);
        result.CompilationStatus.Should().Be(CompilationStatus.Pending,
            "a mesh that compiles module content still compiles the live source — the build serves meanwhile");
        result.LatestAssemblyPath.Should().NotBeNull("the coordinates are KEPT while the compile runs");
    }

    [Fact]
    public void UnknownVersions_KeepServing_NothingWasCompared()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            SourceMovedPast(adoptedVersion: null, currentVersion: null), LiveSources, canCompileLocally: false);
        result.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted,
            "a legacy bundle records no version and a test partition root has none: UNKNOWN never refuses");
        result.CompilationStatus.Should().Be(CompilationStatus.Ok);
    }

    // ── Case 2: major bump → refused, but NOT an error ──────────────────────────────────────

    [Fact]
    public void MajorBump_RefusesTheBuild_WithoutErroring_OnAMeshThatWillNotCompile()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            SourceMovedPast("1.2.2", "2.0.0"), LiveSources, canCompileLocally: false);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused,
            "a MAJOR bump is a declared incompatibility — the one refusal that remains");
        NodeTypeExecutionGate.Evaluate(result).Should().Be(BuildExecutionVerdict.Refused,
            "the incompatible bytes are not run");
        result.CompilationStatus.Should().Be(CompilationStatus.Unavailable,
            "not Error: nothing is known to be wrong with the source, a bundle is awaited — and the "
            + "readiness gate reads Error as a regression that freezes self-update");
        result.CompilationError.Should().Contain("Incompatible build, awaiting bundle")
            .And.Contain("1.2.2").And.Contain("2.0.0").And.Contain("MeshWeaver#3583");
        result.LatestAssemblyPath.Should().NotBeNull(
            "kept, so the page can name the build it refused; the gate is what stops it running");
    }

    [Fact]
    public void MajorBump_OnAMeshThatCompiles_StillDrivesARealCompile_AsBefore()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            SourceMovedPast("1.2.2", "2.0.0"), LiveSources, canCompileLocally: true);
        result.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused);
        result.CompilationStatus.Should().Be(CompilationStatus.Pending, "#2813's cure is unchanged here");
        result.LatestAssemblyPath.Should().BeNull("#2813: on a mesh that can rebuild, stale bytes stop running");
    }

    // ── Case 3: the bundle matches → adopted, and a person is told ──────────────────────────

    [Fact]
    public void AMatchingBundle_LiftsTheHold_AndIsTheReadinessEvent()
    {
        var held = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            SourceMovedPast("1.2.2", "1.2.3"), LiveSources, canCompileLocally: false);
        held.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);

        // The rebaked bundle for this identity lands: Seed stamps the matching fingerprint and
        // the new version, and asks the owner to judge it.
        var rebaked = held with
        {
            AdoptedSourceFingerprint = "4ed23561cfd142d1",
            AdoptedModuleVersion = "1.2.3",
            RequestedSourceStampAt = System.DateTimeOffset.UtcNow,
        };
        var adopted = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(rebaked, LiveSources, canCompileLocally: false);

        adopted.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified);
        adopted.IsDirty.Should().BeFalse("bytes and source agree again");
        BuildDeliveryHold.EventOf(held, adopted).Should().Be(BuildDeliveryHold.DeliveryEvent.Adopted,
            "requirement 3: 'Essentials 1.2.3 adopted' is the signal a person can act on");
        BuildDeliveryHold.EventOf(adopted, adopted).Should().BeNull("no transition, no notification");
        BuildDeliveryHold.EventOf(SourceMovedPast("1.2.2", "1.2.3"), held)
            .Should().Be(BuildDeliveryHold.DeliveryEvent.HeldStale, "entering the hold is announced once");
    }

    // ── The compile-watcher gate's settle ────────────────────────────────────────────────────

    [Fact]
    public void AGateOnATypeWithAUsableBuild_Holds_InsteadOfParkingDead()
    {
        var pending = SourceMovedPast("1.2.2", "1.2.3") with { CompilationStatus = CompilationStatus.Pending };
        var settled = BuildDeliveryHold.Settle(pending, hasUsableBuild: true, "the gate's reason");

        settled.Should().NotBeNull();
        settled!.CompilationStatus.Should().Be(CompilationStatus.Ok);
        settled.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);
        settled.CompilationError.Should().BeNull("an Ok record carries no error text");
        settled.LatestAssemblyMvid.Should().Be("3f73419cac5047d3bbce89206f7e377f", "no coordinates were touched");
        settled.DispatchedBuildInputs.Should().BeNull("terminal ⇒ nothing in flight (#3390)");
    }

    [Fact]
    public void AGateOnARefusedBuild_ReportsIncompatible_NeverError()
    {
        var pending = SourceMovedPast("1.2.2", "2.0.0", BuildProvenance.AdoptionRefused)
            with { CompilationStatus = CompilationStatus.Pending };
        var settled = BuildDeliveryHold.Settle(pending, hasUsableBuild: true, "the gate's reason");
        settled.Should().NotBeNull();
        settled!.CompilationStatus.Should().Be(CompilationStatus.Unavailable);
        settled.CompilationError.Should().Contain("Incompatible build, awaiting bundle").And.Contain("the gate's reason");
        settled.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused);
    }

    [Fact]
    public void AGateOnATypeWithNothingToServe_StillParks_AnHonestError()
        => BuildDeliveryHold.Settle(
                SourceMovedPast("1.2.2", "1.2.3") with
                {
                    CompilationStatus = CompilationStatus.Pending,
                    LatestAssemblyCollection = null, LatestAssemblyPath = null, LatestAssemblyMvid = null,
                },
                hasUsableBuild: false, "the gate's reason")
            .Should().BeNull("there is no build to serve — the named Error park is the truth");

    [Fact]
    public void TheServingNotice_NamesBothSides_AndWhatIsAwaited()
    {
        var notice = BuildDeliveryHold.ServingNotice(SourceMovedPast("1.2.2", "1.2.3"));
        notice.Should().Contain("1.2.2").And.Contain("1.2.3")
            .And.Contain("572183a89fca").And.Contain("4ed23561cfd1")
            .And.Contain("waiting for a bundle")
            .And.Contain(NodeTypeCompilationHelpers.FrameworkVersion);
    }
}
