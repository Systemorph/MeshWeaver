#pragma warning disable CS1591

using System.Collections.Immutable;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 Pins the RE-EVALUATION LANE (#1976) — the READ half of the generated-input content key, and
/// the demotion of the toolchain MVID from an invalidation unit to a trigger.
///
/// <para>The key was stamped on every compile from #1994 and compared by NOTHING: every production
/// call site passed the three-argument <c>FindMismatch</c>, so <c>CompiledDependencies.cs</c>'s
/// <c>continue</c> fired 100% of the time and the only four-argument callers in the repo were
/// assertions. The maintainer's 2026-08-30 audit named that exactly — <i>"a guard that cannot
/// fail"</i> — because a key that is written and never read looks, to anyone inspecting a node,
/// identical to a key that is being enforced.</para>
///
/// <para>🚨 <b>The safety property is the one that matters here, and it is asymmetric.</b> A false
/// MISMATCH costs one rebuild. A false MATCH carries stale bytes forward over live source — the
/// defect class that destroyed four client documents (#2813). So every test below that stages an
/// ABSENT or INCONCLUSIVE input asserts the REBUILD side, and there is no path on which an absence
/// reads as equality.</para>
/// </summary>
public class ContentKeyReevaluationTest
{
    private const string OldToolchain = "mvid:toolchain-old";
    private const string LiveToolchain = "mvid:toolchain-live";
    private const string Digest = "gaaaabbbbccccddddeeeeffff00001111";

    private static Func<string, string?> Resolver(params (string Name, string Id)[] pairs)
        => name => pairs.FirstOrDefault(p => p.Name == name).Id;

    /// <summary>A record as a PREVIOUS image stamped it: the old toolchain id, one bound module,
    /// and the content key of the compile input that produced the bytes.</summary>
    private static ImmutableSortedDictionary<string, string> StampedRecord(
        string toolchain = OldToolchain,
        string moduleId = "mvid:v1",
        string? digest = Digest)
        => CompiledDependencies.Compute(
            ["Custom.Module"], Resolver(("Custom.Module", moduleId)), toolchain, digest);

    private static readonly Func<string, string?> LiveIds = Resolver(("Custom.Module", "mvid:v1"));

    // ---- the live key ---------------------------------------------------------------------------

    [Fact]
    public void LiveContentKeyOf_ReproducesTheStampedKey_FromTheRecordsOwnEntries()
    {
        var record = StampedRecord();

        // The consumer has not compiled, so it cannot know the PRUNED reference set — it resolves
        // exactly the names the record already carries. Same input, same surfaces ⇒ same key.
        CompiledDependencies.LiveContentKeyOf(record, LiveIds, Digest)
            .Should().Be(record[CompiledDependencies.ContentKey]);
    }

    [Fact]
    public void LiveContentKeyOf_IsNullWhenThereIsNothingToCompare()
    {
        CompiledDependencies.LiveContentKeyOf(StampedRecord(), LiveIds, liveGeneratedInputDigest: null)
            .Should().BeNull("nothing was regenerated");
        CompiledDependencies.LiveContentKeyOf(StampedRecord(digest: null), LiveIds, Digest)
            .Should().BeNull("the record carries no content key to compare against");
    }

    [Fact]
    public void LiveContentKeyOf_MovesWhenAnAssemblyTheBuildBindsMoves()
    {
        var record = StampedRecord();

        CompiledDependencies.LiveContentKeyOf(
                record, Resolver(("Custom.Module", "mvid:v2")), Digest)
            .Should().NotBe(record[CompiledDependencies.ContentKey],
                "the pruned reference surfaces are folded into the key, so a module update moves it");
    }

    // ---- the demotion ---------------------------------------------------------------------------

    [Fact]
    public void Reevaluate_IdenticalGeneratedInput_CarriesTheBuildForward()
    {
        // The toolchain MVID moved — 383 commits/30d across the closure do this — but the compile
        // input this type would be handed is byte-for-byte what it was.
        var verdict = ContentKeyReevaluation.Reevaluate(
            StampedRecord(), LiveIds, LiveToolchain, Digest);

        verdict.Verdict.Should().Be(ReevaluationVerdict.CarryForward);
        verdict.Detail.Should().Contain("generated input did not");
    }

    [Fact]
    public void Reevaluate_MovedGeneratedInput_Rebuilds_AndNamesTheContentKey()
    {
        var verdict = ContentKeyReevaluation.Reevaluate(
            StampedRecord(), LiveIds, LiveToolchain, "gTHE-INPUT-MOVED");

        verdict.Verdict.Should().Be(ReevaluationVerdict.Rebuild);
        verdict.Detail.Should().Contain(CompiledDependencies.ContentKey);
    }

    /// <summary>
    /// 🚨 THE SAFETY PROPERTY. Three ways the comparison can fail to happen, and every one of them
    /// must land on "rebuild", never on "carry forward".
    /// </summary>
    [Theory]
    [InlineData(null, Digest)]          // the input could not be regenerated
    [InlineData(Digest, null)]          // the record predates the key (an adopted prebuilt, a cache hit)
    [InlineData(null, null)]            // neither
    public void Reevaluate_AnAbsentKeyIsNeverEquality(string? liveDigest, string? stampedDigest)
    {
        var record = StampedRecord(digest: stampedDigest);

        ContentKeyReevaluation.Reevaluate(record, LiveIds, LiveToolchain, liveDigest)
            .Verdict.Should().Be(ReevaluationVerdict.Inconclusive);

        // …and inconclusive means the metadata-only rule still governs, which INVALIDATES on the
        // toolchain move. Nothing is carried forward on an absence.
        CompiledDependencies.FindMismatchAfterReevaluation(
                record, LiveIds, LiveToolchain,
                CompiledDependencies.LiveContentKeyOf(record, LiveIds, liveDigest))
            .Should().Contain(CompiledDependencies.ToolchainKey,
                "an absent content key leaves the toolchain entry decisive");
    }

    [Fact]
    public void Reevaluate_ADriftedBindingRebuilds_EvenThoughTheToolchainWouldHaveDemoted()
    {
        // Same generated TEXT, but the module this type binds was updated. The content key folds
        // the live resolution of the record's own entries, so it moves — the demotion is confined
        // to the one entry the direct observation actually answers.
        var verdict = ContentKeyReevaluation.Reevaluate(
            StampedRecord(), Resolver(("Custom.Module", "mvid:v2")), OldToolchain, Digest);

        verdict.Verdict.Should().Be(ReevaluationVerdict.Rebuild);
    }

    [Fact]
    public void Reevaluate_ARecordWithoutTheToolchainEntry_IsNeverTrusted()
    {
        var handAssembled = ImmutableSortedDictionary<string, string>.Empty
            .Add("Custom.Module", "mvid:v1")
            .Add(CompiledDependencies.ContentKey, "iwhatever");

        ContentKeyReevaluation.Reevaluate(handAssembled, LiveIds, LiveToolchain, Digest)
            .Verdict.Should().Be(ReevaluationVerdict.Inconclusive);
    }

    [Fact]
    public void Reevaluate_NoRecordAndNoResolver_AreBothInconclusive()
    {
        ContentKeyReevaluation.Reevaluate(null, LiveIds, LiveToolchain, Digest)
            .Verdict.Should().Be(ReevaluationVerdict.Inconclusive);
        ContentKeyReevaluation.Reevaluate(StampedRecord(), null, LiveToolchain, Digest)
            .Verdict.Should().Be(ReevaluationVerdict.Inconclusive);
    }

    /// <summary>
    /// The metadata-only sites keep their EXACT previous semantics: with no live key,
    /// <see cref="CompiledDependencies.FindMismatchAfterReevaluation"/> and
    /// <see cref="CompiledDependencies.FindMismatch"/> are the same function.
    /// </summary>
    [Fact]
    public void FindMismatchAfterReevaluation_WithNoLiveKey_IsExactlyFindMismatch()
    {
        foreach (var record in new[]
                 {
                     StampedRecord(), StampedRecord(digest: null),
                     StampedRecord(toolchain: LiveToolchain),
                 })
        {
            CompiledDependencies.FindMismatchAfterReevaluation(
                    record, LiveIds, LiveToolchain, liveContentKey: null)
                .Should().Be(CompiledDependencies.FindMismatch(record, LiveIds, LiveToolchain));
        }
    }

    // ---- the restamp -----------------------------------------------------------------------------

    /// <summary>
    /// 🚨 The restamp is what makes a carry-forward DURABLE: after it, the metadata-only readers
    /// — <c>HasUsableBuild</c>, the bake probe, the prebuilt seeder, none of which can regenerate
    /// anything — answer correctly on their own. Without it the lane would have to re-regenerate
    /// on every activation to reach the same verdict, which is the cost it exists to remove.
    /// </summary>
    [Fact]
    public void RestampToolchain_MakesTheCarryForwardDurable_ForMetadataOnlyReaders()
    {
        var record = StampedRecord();

        // Before: the metadata-only rule invalidates on the toolchain move.
        CompiledDependencies.FindMismatch(record, LiveIds, LiveToolchain)
            .Should().Contain(CompiledDependencies.ToolchainKey);

        var restamped = CompiledDependencies.RestampToolchain(record, LiveToolchain);

        // After: it validates — and only the toolchain entry moved. The content key and every
        // assembly entry are the EVIDENCE; a restamp that rewrote them would assert something
        // nobody measured.
        CompiledDependencies.FindMismatch(restamped, LiveIds, LiveToolchain).Should().BeNull();
        restamped[CompiledDependencies.ContentKey]
            .Should().Be(record[CompiledDependencies.ContentKey]);
        restamped["Custom.Module"].Should().Be(record["Custom.Module"]);
        restamped.Keys.Should().Equal(record.Keys);

        // …and the trigger re-arms: a FURTHER toolchain move invalidates the restamped record
        // again, so the lane runs again rather than the build validating forever.
        CompiledDependencies.FindMismatch(restamped, LiveIds, "mvid:toolchain-later")
            .Should().Contain(CompiledDependencies.ToolchainKey);
    }

    // ---- the production decision sites -----------------------------------------------------------

    private static readonly MeshNode Node = new("T", "Test");

    private static NodeTypeDefinition Def(
        ImmutableSortedDictionary<string, string> record, string? framework = null)
        => new()
        {
            LatestAssemblyCollection = "local",
            LatestAssemblyPath = "x/v1-abc.dll",
            CompiledFrameworkVersion = framework ?? NodeTypeCompilationHelpers.FrameworkVersion,
            CompiledDependencies = record,
            LastCompiledVersion = 1,
        };

    private static NodeTypeCompilationHelpers.BuildGuards Guards(string? digest) =>
        new(ModulesHash: null, DependencyIdOf: LiveIds, ToolchainId: LiveToolchain,
            LiveGeneratedInputDigest: digest);

    /// <summary>
    /// 🚨 The lane's three outcomes AT A PRODUCTION SITE — <c>HasUsableBuild</c> /
    /// <c>HasStaleFrameworkBuild</c>, the pair every automatic re-drive keys off.
    /// </summary>
    [Fact]
    public void HasUsableBuild_TheContentKeyDecides_AndAnAbsentOneRecompiles()
    {
        var record = StampedRecord();

        // IDENTICAL input ⇒ the build is carried forward, no recompile.
        NodeTypeCompilationHelpers.HasUsableBuild(Node, Def(record), Guards(Digest))
            .Should().BeTrue("the regenerated compile input still hashes to the stamped key");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(Def(record), Guards(Digest))
            .Should().BeFalse("nothing re-drives a compile for a build that is provably current");

        // DIFFERENT input ⇒ recompile.
        NodeTypeCompilationHelpers.HasUsableBuild(Node, Def(record), Guards("gMOVED"))
            .Should().BeFalse("the generated input moved, so the bytes are stale");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(Def(record), Guards("gMOVED"))
            .Should().BeTrue("the stale twin must re-drive the compile for the same condition");

        // INCONCLUSIVE ⇒ recompile. The toolchain entry stays decisive; this is the pre-lane
        // behaviour, preserved exactly.
        NodeTypeCompilationHelpers.HasUsableBuild(Node, Def(record), Guards(null))
            .Should().BeFalse("an absent live key never reads as equality");
        NodeTypeCompilationHelpers.HasStaleFrameworkBuild(Def(record), Guards(null))
            .Should().BeTrue();
    }

    /// <summary>
    /// 🚨 The same three outcomes at the BAKE probe — where the demotion is what lets the store's
    /// bytes-win rule decide, instead of a stale record preempting it and rebaking the world on
    /// every body-only toolchain commit (#1976's measurement: 383 commits/30d across the closure).
    /// </summary>
    [Fact]
    public void ClassifyDetailed_TheContentKeyDecides_AndAnAbsentOneRebakes()
    {
        // The framework rolled and the share is already warm for the LIVE tag — the designed
        // pre-bake flow ("a platform release performs ZERO per-node compiles").
        var rolled = Def(StampedRecord(), framework: "s-the-previous-image");

        NodeTypeBakeStatus.ClassifyDetailed(
                rolled, storeHasBytes: true, NodeTypeCompilationHelpers.FrameworkVersion,
                LiveIds, LiveToolchain, Digest)
            .State.Should().Be(BakeState.Baked,
                "the toolchain moved but this type's generated input did not, so the bytes win");

        NodeTypeBakeStatus.ClassifyDetailed(
                rolled, storeHasBytes: true, NodeTypeCompilationHelpers.FrameworkVersion,
                LiveIds, LiveToolchain, "gMOVED")
            .State.Should().Be(BakeState.FrameworkStale,
                "the generated input moved — the bytes in the share are not what a compile "
                + "would produce now");

        NodeTypeBakeStatus.ClassifyDetailed(
                rolled, storeHasBytes: true, NodeTypeCompilationHelpers.FrameworkVersion,
                LiveIds, LiveToolchain)
            .State.Should().Be(BakeState.FrameworkStale,
                "without a regenerated key the metadata-only rule governs, exactly as before");
    }
}
