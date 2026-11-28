using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A bundle from an OLDER release whose source paths the newer release deleted is JUDGED,
/// not deferred forever.</b> The #4280 path witness
/// (<see cref="AdoptionIsNotJudgedFromAPartialSourceSetTest"/>) reads a bundle path absent from the
/// live set as "the install is still writing it". Across a module-version move that reading is
/// wrong: the newer release removed or renamed the file, it never lands, and the deferral never
/// ends.
///
/// <para><b>The measurement.</b> The company instance, 2026-11-28. <c>Crm/Counterparty</c> on
/// source module version 4.2 served the 4.0.10 bundle (<c>buildProvenance: AdoptedVerified</c>,
/// adopted fingerprint <c>d3950799c2dfeecf</c>, live <c>736dd53405dfbdeb</c>) through two governed
/// provisions. 4.2 renamed <c>Crm/Source/StoredEnums</c> and <c>Crm/Source/TestTable</c> to
/// <c>CrmStoredEnums</c> / <c>CrmTestTable</c>; the 4.0.10 bundle's
/// <c>adoptedSourcePaths</c> still named the old two, so the seeder's pre-write decline deferred,
/// every release request's non-forced compile re-adopted 4.0.10, the owner's stamp deferred, and
/// the 4.2 content field (<c>person</c>) never reached the schema.</para>
/// </summary>
public class AdoptionIsJudgedPastPathsANewerReleaseDeletedTest
{
    private const string BundleFingerprint = "d3950799c2dfeecf";
    private const string LiveFingerprint = "736dd53405dfbdeb";

    private static readonly DateTimeOffset StampedAt = new(2026, 11, 28, 12, 24, 11, TimeSpan.Zero);

    /// <summary>What the 4.0.10 bundle was built from (trimmed to the paths that matter).</summary>
    private static readonly ImmutableList<string> BundlePaths =
    [
        "Crm/Counterparty/Source/CounterpartyLayoutAreas",
        "Crm/Source/Records",
        "Crm/Source/StoredEnums",
        "Crm/Source/TestTable",
    ];

    /// <summary>The 4.2 live set: the two renamed, one added.</summary>
    private static readonly ImmutableDictionary<string, long> LiveSet = Snapshot(
        "Crm/Counterparty/Source/CounterpartyLayoutAreas",
        "Crm/Source/AccessModel",
        "Crm/Source/CrmStoredEnums",
        "Crm/Source/CrmTestTable",
        "Crm/Source/Records");

    private static ImmutableDictionary<string, long> Snapshot(params string[] paths) =>
        ImmutableDictionary.CreateRange(
            paths.Select((p, i) => new KeyValuePair<string, long>(p, 639_314_072_700_000_000 + i)));

    private static NodeTypeDefinition Adopted(string? adoptedVersion, string? currentVersion) =>
        new()
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "local",
            LatestAssemblyPath = "Crm_Counterparty/v611-c004e001-9022624adb10.dll",
            LatestAssemblyMvid = "9a17fb7ad6d74057b241d39effc61a7b",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            AdoptedSourceFingerprint = BundleFingerprint,
            AdoptedSourcePaths = BundlePaths,
            AdoptedModuleVersion = adoptedVersion,
            CurrentModuleVersion = currentVersion,
            CurrentSourceFingerprint = LiveFingerprint,
            CurrentSourceVersions = LiveSet,
            BuildProvenance = BuildProvenance.Compiled,
            RequestedSourceStampAt = StampedAt,
        };

    [Fact]
    public void TheTwoRenamedPaths_AreAbsentFromTheLiveSet()
        => NodeTypeCompilationHelpers.SourcePathsNotYetLive(BundlePaths, LiveSet)
            .Should().Equal(["Crm/Source/StoredEnums", "Crm/Source/TestTable"]);

    /// <summary>🚨 THE regression test: before the fix this answered false and the stamp request
    /// stood forever while the 4.0.10 bytes served as verified.</summary>
    [Fact]
    public void AcrossAVersionMove_TheAdoptionIsJudgeable()
        => NodeTypeCompilationHelpers.CanJudgeAdoption(Adopted("4.0.10", "4.2"), LiveSet)
            .Should().BeTrue("4.2 deleted the paths the 4.0.10 bundle names; they will never land");

    [Fact]
    public void AcrossAVersionMove_TheMismatchIsJudgedAndTheLiveSourceIsCompiled()
    {
        var seeded = Adopted("4.0.10", "4.2");

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            seeded, LiveSet, canCompileLocally: true);

        result.Should().NotBeSameAs(seeded, "the judgement is no longer deferred");
        result.RequestedSourceStampAt.Should().BeNull("the one-shot request is answered");
        result.BuildProvenance.Should().NotBe(BuildProvenance.AdoptedVerified,
            "bytes built from other source are never recorded as verified");
        result.BuildProvenance.Should().BeOneOf(BuildProvenance.StaleAdopted, BuildProvenance.AdoptionRefused);
        result.CompilationStatus.Should().Be(CompilationStatus.Pending,
            "this mesh compiles, so the live 4.2 source is compiled");
    }

    [Fact]
    public void SameRelease_StillDefers_TheInstallRaceIsUnchanged()
    {
        var seeded = Adopted("4.2.2", "4.2.2");

        NodeTypeCompilationHelpers.CanJudgeAdoption(seeded, LiveSet).Should().BeFalse(
            "within one release an absent bundle path is still an arrival (#4280)");
        NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(seeded, LiveSet, canCompileLocally: true)
            .Should().BeSameAs(seeded);
    }

    [Theory]
    [InlineData(null, "4.2")]
    [InlineData("4.0.10", null)]
    [InlineData(null, null)]
    public void AnUnknownVersion_IsNoVersionMove_AndStillDefers(string? adopted, string? current)
        => NodeTypeCompilationHelpers.CanJudgeAdoption(
Adopted(adopted, current), LiveSet)
            .Should().BeFalse("an unprovable version claim never lifts the #4280 deferral");

    [Fact]
    public void AcrossAVersionMove_AnIncludeTheNewerReleaseDropped_IsJudgedToo()
        => NodeTypeCompilationHelpers.CanJudgeAdoption(
                BundleFingerprint, LiveFingerprint, LiveSet, BundlePaths,
                adoptedSourceIncludes: ["Crm/Source/Retired"], currentSourceIncludes: [],
                adoptedModuleVersion: "4.0.10", currentModuleVersion: "4.2")
            .Should().BeTrue("a resolved include closure without it means 4.2 dropped it");

    [Fact]
    public void AcrossAVersionMove_AnIncludeClosureNotComputedYet_StillDefers()
        => NodeTypeCompilationHelpers.CanJudgeAdoption(
                BundleFingerprint, LiveFingerprint, LiveSet, BundlePaths,
                adoptedSourceIncludes: ["Crm/Source/Retired"], currentSourceIncludes: null,
                adoptedModuleVersion: "4.0.10", currentModuleVersion: "4.2")
            .Should().BeFalse("an uncomputed closure is unknown, not absent");

    [Fact]
    public void ABundleNewerThanTheLiveSource_StillDefers()
        => NodeTypeCompilationHelpers.CanJudgeAdoption(Adopted("4.3.0", "4.2"), LiveSet)
            .Should().BeFalse("the live source is the older one: its missing paths may be the "
                              + "upgrade still landing");

    [Theory]
    [InlineData("4.0.10", "4.2", true)]
    [InlineData("4.2.1", "4.2.2", true)]
    [InlineData("4.2.2", "4.2.2", false)]
    [InlineData("4.2", "4.2.0", false)]
    [InlineData("4.2.2", "4.2.1", false)]
    [InlineData("4.0.10-ci.3", "4.2", true)]
    public void BundleIsAnOlderRelease_ComparesTheNumericCore(string adopted, string current, bool older)
        => NodeTypeCompilationHelpers.BundleIsAnOlderRelease(adopted, current).Should().Be(older);

    [Fact]
    public void TheSeedersPreWriteWitness_TakesTheVersionsToo()
    {
        NodeTypeCompilationHelpers.CanJudgeAdoption(
                BundleFingerprint, LiveFingerprint, LiveSet, BundlePaths,
                adoptedSourceIncludes: null, currentSourceIncludes: null,
                adoptedModuleVersion: "4.0.10", currentModuleVersion: "4.2")
            .Should().BeTrue("the seeder must DECLINE the stale bundle rather than re-adopt it");
        NodeTypeCompilationHelpers.CanJudgeAdoption(
                BundleFingerprint, LiveFingerprint, LiveSet, BundlePaths,
                adoptedSourceIncludes: null, currentSourceIncludes: null)
            .Should().BeFalse("without versions the #4280 rule applies unchanged");
    }
}
