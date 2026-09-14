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
/// 🚨 <b>A NodeType whose bundle names sources that have NOT ALL landed yet is not judged</b>
/// (MeshWeaver#4280) — the #4208 deferral one step further, pinned pure with no mesh and no timing.
///
/// <para><b>The measurement.</b> MeshWeaver.Reinsurance run 34804118498, gate shard 1/1, set
/// 3.0.0-ci.8547, 2026-09-14:</para>
///
/// <code>
/// 04:11:44.095  ── Ifrs17: installing 403 file(s)…
/// 04:11:45.778  bake-seed  Prebuilt assembly ADOPTED for Ifrs17/RawVariable … module version 1.0.7
/// 04:11:45.783  CompileWatcher  [AdoptedSourceStamp] Ifrs17/RawVariable: the source moved past the
///               adopted build (bundle fingerprint aa82137e45651a6c, live bebbfc488bd3c8bd) …
///               a compile of the live source is dispatched
/// 04:11:47.772  fail: … CS0246 'PeriodType' …
/// 04:11:48.008  fail: NodeType 'Ifrs17/RawVariable' PARKED after compile failure
/// 04:11:50.777  [PackageInstaller] warmed installed root Ifrs17        ← the install ends HERE
/// 04:12:03.974  info: Successfully compiled assembly for Ifrs17/RawVariable   ← it COMPILES
/// </code>
///
/// <para>The compile was dispatched 1.7 s into a 6.7 s install: its source discovery matched 8 of
/// the 14 committed files under <c>Ifrs17/Source/</c>, and the six it missed declare exactly the
/// names in the <c>CS0246</c>s. Every declared query had matched SOMETHING, so #4208's empty
/// witness said "judgeable"; the fingerprints disagreed — six files short, of course they did —
/// and the disagreement was read as "the source moved past these bytes". The affected set was a
/// different one on every run and included types the repo does not own: an install-order race,
/// not a property of any type.</para>
///
/// <para><b>The rule.</b> The bundle now carries the PATHS it was built from
/// (<see cref="NodeTypeDefinition.AdoptedSourcePaths"/>, the key set of the manifest's
/// <c>sourceVersions</c>). While ANY of them is absent from the live snapshot, the live set is
/// ARRIVING, not moved: the judgement is deferred exactly as the empty case is — the record is
/// returned untouched, the one-shot request stays standing, and the sources watcher's publication
/// that carries the last arrival judges it. Nothing is dispatched off a set still being written.</para>
/// </summary>
public class AdoptionIsNotJudgedFromAPartialSourceSetTest
{
    private const string TypePath = "Ifrs17/RawVariable";
    private const string BundleFingerprint = "aa82137e45651a6c";
    private const string PartialFingerprint = "bebbfc488bd3c8bd";

    private static readonly DateTimeOffset StampedAt =
        new(2026, 9, 14, 4, 11, 45, TimeSpan.Zero);

    /// <summary>The 14 paths the bundle was built from — what the bake's manifest records.</summary>
    private static readonly ImmutableList<string> BundlePaths =
    [
        "Ifrs17/RawVariable/Source/RawVariable",
        "Ifrs17/RawVariable/Test/RawVariableTests",
        "Ifrs17/RawVariable/Test/RawVariableTestsArea",
        "Ifrs17/Source/Arithmetics",
        "Ifrs17/Source/Consts",
        "Ifrs17/Source/DataCube",
        "Ifrs17/Source/Enums",
        "Ifrs17/Source/IScope",
        "Ifrs17/Source/IScopeRegistryFactory",
        "Ifrs17/Source/QueryReach",
        "Ifrs17/Source/ScopeConfiguration",
        "Ifrs17/Source/Scopes",
        "Ifrs17/Source/StorageExtensions",
        "Ifrs17/Source/Variables",
    ];

    /// <summary>The 8 the premature compile's discovery matched — the run's own printed list.</summary>
    private static readonly ImmutableDictionary<string, long> EightOfFourteen = Snapshot(
        "Ifrs17/RawVariable/Source/RawVariable",
        "Ifrs17/RawVariable/Test/RawVariableTests",
        "Ifrs17/Source/Arithmetics",
        "Ifrs17/Source/Consts",
        "Ifrs17/Source/DataCube",
        "Ifrs17/Source/IScopeRegistryFactory",
        "Ifrs17/Source/QueryReach",
        "Ifrs17/Source/ScopeConfiguration");

    private static readonly ImmutableDictionary<string, long> AllFourteen = Snapshot([.. BundlePaths]);

    private static ImmutableDictionary<string, long> Snapshot(params string[] paths) =>
        ImmutableDictionary.CreateRange(
            paths.Select((p, i) => new KeyValuePair<string, long>(p, 638_000_000_000_000_000 + i)));

    /// <summary>The record exactly as the seeder leaves it — including, since #4280, the paths.</summary>
    private static NodeTypeDefinition JustSeeded(
        string? liveFingerprint,
        ImmutableDictionary<string, long> liveSources,
        ImmutableList<string>? adoptedPaths = null) =>
        new()
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "Ifrs17_RawVariable/v0-s1c1d62ad-aa82137e4565.dll",
            LatestAssemblyMvid = "0b3d8f2a5c7e4d1b9e6f2a8c4d7b1e35",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            AdoptedSourceFingerprint = BundleFingerprint,
            AdoptedSourcePaths = adoptedPaths ?? BundlePaths,
            AdoptedModuleVersion = "1.0.7",
            CurrentModuleVersion = "1.0.7",
            CurrentSourceFingerprint = liveFingerprint,
            CurrentSourceVersions = liveSources,
            CompiledSources = null,
            BuildProvenance = BuildProvenance.Compiled,
            RequestedSourceStampAt = StampedAt,
        };

    // ── The witness ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SixOfTheBundlesFourteenPaths_AreNotLiveYet()
    {
        var missing = NodeTypeCompilationHelpers.SourcePathsNotYetLive(BundlePaths, EightOfFourteen);

        missing.Should().Equal(
        [
            "Ifrs17/RawVariable/Test/RawVariableTestsArea",
            "Ifrs17/Source/Enums",
            "Ifrs17/Source/IScope",
            "Ifrs17/Source/Scopes",
            "Ifrs17/Source/StorageExtensions",
            "Ifrs17/Source/Variables",
        ], "these are the files whose declarations the CS0246s named — PeriodType and IScope live "
           + "in Enums.cs and IScope.cs, RawVariableTestsArea is the missing test sibling");
    }

    [Fact]
    public void PathsAreComparedOrdinalIgnoreCase_TheSameEquivalenceTheCompileFoldDeduplicatesOn()
    {
        var live = Snapshot("ifrs17/source/ENUMS");
        NodeTypeCompilationHelpers.SourcePathsNotYetLive(["Ifrs17/Source/Enums"], live)
            .Should().BeEmpty("a producer and a consumer that spell a path differently must not hold "
                              + "each other hostage");
    }

    [Fact]
    public void ALegacyBundleWithNoPaths_HasNothingToWaitFor()
    {
        NodeTypeCompilationHelpers.SourcePathsNotYetLive(null, EightOfFourteen).Should().BeEmpty();
        NodeTypeCompilationHelpers.SourcePathsNotYetLive([], EightOfFourteen).Should().BeEmpty();
        NodeTypeCompilationHelpers.CanJudgeAdoption(
                JustSeeded(PartialFingerprint, EightOfFourteen, adoptedPaths: null)
                    with { AdoptedSourcePaths = null },
                EightOfFourteen)
            .Should().BeTrue("with no paths recorded the two-witness rule applies unchanged — a "
                             + "matched, disagreeing set is judgeable, as it was before #4280");
    }

    // ── The defect ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE regression test. Before the fix this returned <c>StaleAdopted</c> with
    /// <c>CompilationStatus = Pending</c> — the compile of the 8-file set that failed CS0246 and
    /// parked the type.
    /// </summary>
    [Fact]
    public void JudgedWhileTheInstallIsStillWriting_TheAdoptionIsDeferredAndTheRequestStands()
    {
        var seeded = JustSeeded(PartialFingerprint, EightOfFourteen);

        NodeTypeCompilationHelpers.CanJudgeAdoption(seeded, EightOfFourteen).Should().BeFalse(
            "8 of the 14 paths the bundle was built from is an install in flight, not a measurement");

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            seeded, EightOfFourteen, canCompileLocally: true);

        result.Should().BeSameAs(seeded, "the judgement is DEFERRED: the record is returned untouched");
        result.RequestedSourceStampAt.Should().Be(StampedAt,
            "the stamp request is ONE-SHOT — answering it from a set still being written spends "
            + "the only chance to refuse on a verdict nobody measured");
        result.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "no compile is dispatched: compiling 8 of 14 files is what produced the CS0246s about "
            + "PeriodType and IScope, which are declared in the six still landing");
        result.BuildProvenance.Should().Be(BuildProvenance.Compiled, "no verdict at all is recorded");
        result.LatestAssemblyPath.Should().Be("Ifrs17_RawVariable/v0-s1c1d62ad-aa82137e4565.dll",
            "the adopted bytes keep serving while the sources are still arriving");
    }

    /// <summary>
    /// The other half of the reactive wait: the publication that carries the last arrival — the
    /// full 14-file set, hashing to the bundle's own fingerprint — fulfils the standing request.
    /// </summary>
    [Fact]
    public void WhenTheLastSourceLands_TheStandingRequestConverges()
    {
        var deferred = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            JustSeeded(PartialFingerprint, EightOfFourteen), EightOfFourteen, canCompileLocally: true);
        deferred.RequestedSourceStampAt.Should().NotBeNull("precondition: the request is standing");

        var published = deferred with
        {
            CurrentSourceVersions = AllFourteen,
            CurrentSourceFingerprint = BundleFingerprint,
        };

        var judged = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            published, AllFourteen, canCompileLocally: true);

        judged.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified,
            "these ARE the sources the bundle was built from — the verdict every sibling that "
            + "activated after its sources got, and this type gets once its own have landed");
        judged.RequestedSourceStampAt.Should().BeNull("answered, so the one-shot request is consumed");
        judged.CompilationStatus.Should().Be(CompilationStatus.Ok, "no compile was ever needed");
        judged.IsDirty.Should().BeFalse("CompiledSources is stamped with the live set");
    }

    // ── Negative controls: a COMPLETE set that disagrees is still a measurement ──────────────

    /// <summary>
    /// 🚨 The control that could have falsified the fix. All 14 paths present and the fingerprint
    /// still differs — the source genuinely MOVED past the bundle (an edit, not an arrival) — must
    /// still reach #3583's stale-but-serving row and dispatch the compile. Deferring on a short set
    /// must not become deferring on a real disagreement.
    /// </summary>
    [Fact]
    public void ACompleteSetThatDisagrees_IsStillStaleAdoptedAndCompiled()
    {
        var edited = JustSeeded("4ed23561cfd142d1", AllFourteen);

        NodeTypeCompilationHelpers.CanJudgeAdoption(edited, AllFourteen).Should().BeTrue(
            "every path the bundle names is live: the disagreement is a measurement");

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            edited, AllFourteen, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);
        result.CompilationStatus.Should().Be(CompilationStatus.Pending, "a real compile is dispatched");
        result.RequestedSourceStampAt.Should().BeNull("a judgement that RAN consumes the request");
    }

    /// <summary>A live set that holds MORE than the bundle names (a file added upstream) is
    /// complete with respect to the bundle — nothing is arriving that the bundle needs — so it is
    /// judged.</summary>
    [Fact]
    public void ALiveSetThatHasGrownPastTheBundle_IsJudged()
    {
        var grown = AllFourteen.Add("Ifrs17/Source/NewFile", 638_000_000_000_000_099);
        NodeTypeCompilationHelpers.CanJudgeAdoption(
                JustSeeded("4ed23561cfd142d1", grown), grown)
            .Should().BeTrue("the bundle's paths are all present; the extra one is the move");
    }

    /// <summary>🚨 Equality outranks the paths, as it outranks the empty witness: a live set that
    /// hashes to the bundle's fingerprint IS the bundle's set, whatever a path list says.</summary>
    [Fact]
    public void AnHonestFingerprintMatch_VerifiesRegardlessOfThePathList()
    {
        var odd = JustSeeded(BundleFingerprint, EightOfFourteen,
            adoptedPaths: BundlePaths.Add("Ifrs17/Source/SomethingTheFoldDropped"));

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            odd, EightOfFourteen, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified,
            "producer and consumer hashed the same compile input, and they agree");
    }

    // ── The include half of the witness (#4293's first review finding) ───────────────────────

    /// <summary>The bundle was compiled with two <c>@@</c> includes; the query-resolved set is
    /// complete, the includes are what is still landing.</summary>
    private static readonly ImmutableList<string> BundleIncludes =
        ["Ifrs17/Shared/Snippets/Header", "Ifrs17/Shared/Snippets/Usings"];

    [Fact]
    public void AnIncludeStillLanding_IsAnArrivalNotAMove()
    {
        NodeTypeCompilationHelpers.SourceIncludesNotYetLive(
                BundleIncludes, ["Ifrs17/Shared/Snippets/Usings"])
            .Should().Equal(["Ifrs17/Shared/Snippets/Header"],
                "an ABSENT include is an answer in the include reader — it contributes nothing to the "
                + "live fold — so the only way to know it is an arrival is to know the bundle had it");

        var seeded = JustSeeded("4ed23561cfd142d1", AllFourteen) with
        {
            AdoptedSourceIncludes = BundleIncludes,
            CurrentSourceIncludes = ["Ifrs17/Shared/Snippets/Usings"],
        };
        NodeTypeCompilationHelpers.CanJudgeAdoption(seeded, AllFourteen).Should().BeFalse(
            "every query-resolved path is live, and one @@ target the bundle folded over is not");

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            seeded, AllFourteen, canCompileLocally: true);
        result.Should().BeSameAs(seeded, "deferred: the record is returned untouched");
        result.CompilationStatus.Should().Be(CompilationStatus.Ok, "no compile is dispatched");
    }

    [Fact]
    public void AnIncludeListNotPublishedYet_IsNotJudged_ButNoIncludesRecordedJudgesAsBefore()
    {
        var unpublished = JustSeeded("4ed23561cfd142d1", AllFourteen) with
        {
            AdoptedSourceIncludes = BundleIncludes,
            CurrentSourceIncludes = null,
        };
        NodeTypeCompilationHelpers.CanJudgeAdoption(unpublished, AllFourteen).Should().BeFalse(
            "the fingerprint and the include list are written together, so 'not published' is "
            + "'not computed', never 'none present'");

        var legacy = JustSeeded("4ed23561cfd142d1", AllFourteen) with
        {
            AdoptedSourceIncludes = null,
            CurrentSourceIncludes = null,
        };
        NodeTypeCompilationHelpers.CanJudgeAdoption(legacy, AllFourteen).Should().BeTrue(
            "a producer that recorded no includes gets the path witness alone, as before");
    }

    [Fact]
    public void OnceTheIncludesArePresent_ACompleteSetThatDisagreesIsJudged()
    {
        var landed = JustSeeded("4ed23561cfd142d1", AllFourteen) with
        {
            AdoptedSourceIncludes = BundleIncludes,
            CurrentSourceIncludes = BundleIncludes.Add("Ifrs17/Shared/Snippets/Extra"),
        };
        NodeTypeCompilationHelpers.CanJudgeAdoption(landed, AllFourteen).Should().BeTrue(
            "every include the bundle folded over is present (an extra one is the move); the "
            + "disagreement is a measurement");
        NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(landed, AllFourteen, canCompileLocally: true)
            .BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);
    }

    // ── The second half: a verdict formed under a set that has moved is re-driven, not parked ─

    [Fact]
    public void SourcesMovedSince_IsTheParkRegistrysOwnEquality()
    {
        NodeTypeCompileParkRegistry.SourcesMovedSince(EightOfFourteen, AllFourteen).Should().BeTrue(
            "six files landed after Roslyn was handed the eight");
        NodeTypeCompileParkRegistry.SourcesMovedSince(AllFourteen, AllFourteen).Should().BeFalse(
            "a failure on the complete set is a verdict about the code, and parks as before");
        NodeTypeCompileParkRegistry.SourcesMovedSince(null, AllFourteen).Should().BeFalse(
            "a compile that never resolved a set is not comparable — no re-drive off a set nobody "
            + "established (#1216)");
        NodeTypeCompileParkRegistry.SourcesMovedSince(EightOfFourteen, null).Should().BeFalse(
            "an unseeded live set is 'not known yet', not 'different'");
        NodeTypeCompileParkRegistry.SourcesMovedSince(
                EightOfFourteen, EightOfFourteen.SetItem("Ifrs17/Source/Consts", 1))
            .Should().BeTrue("an EDIT during the compile moves the set too — the same equality "
                             + "ShouldRetryForSourceChange applies");
    }
}
