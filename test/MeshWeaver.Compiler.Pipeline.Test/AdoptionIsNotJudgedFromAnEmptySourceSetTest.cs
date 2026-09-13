using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A NodeType activated BEFORE the sources it declares have landed adopts NOTHING and parks
/// red</b> (MeshWeaver#4208) — the pure judgement, pinned with no mesh and no timing.
///
/// <para><b>The measurement.</b> MeshWeaver.Reinsurance#204, run 34766381762 attempt 2, gate shard
/// 1/1, 2026-09-13. <c>Reinsurance/AggregateSection</c> declares the same three source queries
/// every Reinsurance NodeType declares — <c>namespace:Source scope:subtree</c>,
/// <c>shared=@Reinsurance/Source</c>, <c>shared=@Reinsurance/SampleData/Source</c> — and it sorts
/// FIRST in its namespace, ahead of the <c>Reinsurance/Source/*</c> nodes the last two read:</para>
///
/// <code>
/// 16:16:50.758 bake-seed  Prebuilt assembly ADOPTED … (framework s5c186d0c…, module version 1.0.21)
/// 16:16:50.760 CompileWatcher [AdoptedSourceStamp] …: the source moved past the adopted build
///                            (bundle fingerprint 2c12e765f7b3f3e9, live e3b0c44298fc…)
/// 16:16:55.310 MeshNodeCompilationService  Failed to compile assembly …
///              MISSING SOURCES: 3 of 3 declared source queries … matched NO nodes on this mesh
///              CS0246 (line 68): 'Premium' could not be found …
/// 16:16:55.383 CompileWatcher  NodeType … PARKED after compile failure
/// </code>
///
/// <para><c>e3b0c44298fc1c14</c> is <see cref="NodeTypeSourceFingerprint.EmptySourceSet"/> — the
/// value the fold returns when it is handed nothing. "The source moved past the adopted build" was
/// therefore a conclusion drawn from a set nobody had established. The refusal dispatched a
/// compile; that compile ran against no sources at all and Roslyn's completely genuine-looking
/// <c>CS0246</c>s parked the type. <c>AmountType</c> and every later sibling, with the identical
/// queries, adopted green once the 26 source nodes had landed milliseconds later.</para>
///
/// <para><b>The rule.</b> An EMPTY fold is a well-formed 16-character value, so every
/// <c>is { Length: &gt; 0 }</c> guard accepts it — but it establishes NOTHING, exactly like the
/// absent fingerprint #2813 already guards against. The judgement is DEFERRED, never refused: the
/// record is returned untouched, so the one-shot stamp request is left STANDING and the sources
/// watcher's next publication (a live synced query — no timer, no poll) judges it. That is the
/// reactive wait, and <see cref="WhenTheSourcesLand_TheStandingRequestConverges"/> is its
/// convergence.</para>
/// </summary>
public class AdoptionIsNotJudgedFromAnEmptySourceSetTest
{
    private const string TypePath = "Reinsurance/AggregateSection";

    /// <summary>The fingerprint the bake recorded for the bytes — the real value from the run.</summary>
    private const string BundleFingerprint = "2c12e765f7b3f3e9";

    private static readonly DateTimeOffset StampedAt =
        new(2026, 9, 13, 16, 16, 50, TimeSpan.Zero);

    /// <summary>The source set as it is once the import has landed: the bundle's own sources.</summary>
    private static readonly ImmutableDictionary<string, long> LiveSources =
        ImmutableDictionary.CreateRange(new Dictionary<string, long>
        {
            ["Reinsurance/Source/Premium"] = 638_000_000_000_000_000,
            ["Reinsurance/Source/ReinsuranceSection"] = 638_000_000_000_000_001,
        });

    /// <summary>The record exactly as <c>PrebuiltAssemblySeeder</c> leaves it: Ok, the adopted
    /// bytes' coordinates, the bundle's fingerprint and module version, and the one-shot stamp
    /// request for the owner to judge. <paramref name="liveFingerprint"/> and
    /// <paramref name="liveSources"/> are what the owner's sources watcher has published SO FAR.</summary>
    private static NodeTypeDefinition JustSeeded(
        string? liveFingerprint,
        ImmutableDictionary<string, long> liveSources,
        string? currentModuleVersion = "1.0.21") =>
        new()
        {
            CompilationStatus = CompilationStatus.Ok,
            LatestAssemblyCollection = "assemblies",
            LatestAssemblyPath = "Reinsurance_AggregateSection/v0-s5c186d0c-9f44cb41a463.dll",
            LatestAssemblyMvid = "6d1f0c4a7b2e40a9b3c8e51d2f7a9b04",
            CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            AdoptedSourceFingerprint = BundleFingerprint,
            AdoptedModuleVersion = "1.0.21",
            CurrentModuleVersion = currentModuleVersion,
            CurrentSourceFingerprint = liveFingerprint,
            CurrentSourceVersions = liveSources,
            // A fresh install has compiled nothing here — the owner stamps this, and only after
            // it has judged the adoption.
            CompiledSources = null,
            BuildProvenance = BuildProvenance.Compiled,
            RequestedSourceStampAt = StampedAt,
        };

    // ── The constant is the incident's own measurement ───────────────────────────────────────

    /// <summary>
    /// 🚨 Pins <see cref="NodeTypeSourceFingerprint.EmptySourceSet"/> to BOTH things it has to be:
    /// what the fold actually returns for no input, and the literal value the gate log recorded.
    /// A constant that drifted from either would make every assertion below vacuous.
    /// </summary>
    [Fact]
    public void TheEmptyFold_IsWhatTheGateLogMeasured()
    {
        NodeTypeSourceFingerprint.EmptySourceSet.Should().Be(
            NodeTypeSourceFingerprint.Compute(Array.Empty<MeshNode>(), TypePath),
            "the constant is computed from the fold so it cannot drift away from it");
        NodeTypeSourceFingerprint.EmptySourceSet.Should().Be("e3b0c44298fc1c14",
            "that is the live fingerprint MeshWeaver.Reinsurance#204 recorded at 16:16:50.760");
    }

    /// <summary>
    /// 🚨 The reason the witness is the SNAPSHOT and not this constant. The tick snapshot counts
    /// every MATCHED node; the fingerprint is folded over
    /// <c>NodeCompileShaping.CollectCompileSources</c>, which drops executable cells and blank
    /// files. A type whose only matched node is one of those has an established source set AND the
    /// empty fold — reading the hash as the witness would defer its adoption indefinitely, trading
    /// #4208's park for a permanent hold.
    /// </summary>
    [Fact]
    public void AMatchedSourceTheFoldDROPS_IsStillAnEstablishedSourceSet()
    {
        var blank = new MeshNode("blank", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "blank",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Language = "csharp", Code = "   " },
        };
        NodeTypeSourceFingerprint.Compute([blank], TypePath).Should().Be(
            NodeTypeSourceFingerprint.EmptySourceSet,
            "a blank file contributes nothing to the compile input, so the fold is empty");

        var matched = ImmutableDictionary.CreateRange(
            new Dictionary<string, long> { [blank.Path] = 638_000_000_000_000_000 });

        NodeTypeCompilationHelpers.CanJudgeAdoption(
                JustSeeded(NodeTypeSourceFingerprint.EmptySourceSet, matched), matched)
            .Should().BeTrue(
                "a declared query DID match a node here — the disagreement is a measurement, and "
                + "deferring on it would hold this type's adoption for ever");
    }

    // ── The defect ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE regression test. Before the fix this returned <c>StaleAdopted</c> with
    /// <c>CompilationStatus = Pending</c> and <c>CompiledSources = null</c> — a dispatched compile
    /// of a source set that does not exist, which is what parked the type.
    /// </summary>
    [Fact]
    public void ActivatedBeforeItsSourcesLanded_TheAdoptionIsNotJudgedAndTheRequestStands()
    {
        var seeded = JustSeeded(
            NodeTypeSourceFingerprint.EmptySourceSet, ImmutableDictionary<string, long>.Empty);

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            seeded, ImmutableDictionary<string, long>.Empty, canCompileLocally: true);

        result.RequestedSourceStampAt.Should().Be(StampedAt,
            "the stamp request is ONE-SHOT — answering it from a set nobody established spends the "
            + "only chance to refuse and closes the question for good");
        result.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "no compile is dispatched: there is nothing to compile, and compiling nothing is what "
            + "produced the CS0246s that parked Reinsurance/AggregateSection");
        result.BuildProvenance.Should().Be(BuildProvenance.Compiled,
            "the judgement is DEFERRED, so it records no verdict at all — not even Unverified");
        result.LatestAssemblyPath.Should().Be(
            "Reinsurance_AggregateSection/v0-s5c186d0c-9f44cb41a463.dll",
            "the adopted bytes keep serving while the sources are still arriving");
        result.IsDirty.Should().BeFalse(
            "CompiledSources and CurrentSourceVersions are both empty, so the install's release "
            + "request is satisfied by the adopted build instead of compiling it");
    }

    /// <summary>
    /// The other half of the reactive wait: when the 26 source nodes land, the sources watcher
    /// publishes them and fulfils the request it left standing — in the same write — and the
    /// adoption verifies. Nothing polls, nothing retries, and the type never entered a failed state.
    /// </summary>
    [Fact]
    public void WhenTheSourcesLand_TheStandingRequestConverges()
    {
        var deferred = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            JustSeeded(NodeTypeSourceFingerprint.EmptySourceSet, ImmutableDictionary<string, long>.Empty),
            ImmutableDictionary<string, long>.Empty,
            canCompileLocally: true);
        deferred.RequestedSourceStampAt.Should().NotBeNull("precondition: the request is standing");

        // The sources watcher's next publication: the full live set, and the fingerprint over it —
        // which is the one the bake recorded, because these ARE the sources it was built from.
        var published = deferred with
        {
            CurrentSourceVersions = LiveSources,
            CurrentSourceFingerprint = BundleFingerprint,
        };

        var judged = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            published, LiveSources, canCompileLocally: true);

        judged.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified,
            "the same standing request now answers the question it could not answer before — which "
            + "is exactly the verdict AmountType and every later sibling got");
        judged.RequestedSourceStampAt.Should().BeNull("answered, so the one-shot request is consumed");
        judged.CompilationStatus.Should().Be(CompilationStatus.Ok, "no compile was ever needed");
        judged.IsDirty.Should().BeFalse("CompiledSources is stamped with the live set");
    }

    // ── Negative controls: the mechanism still fires where it should ─────────────────────────

    /// <summary>
    /// 🚨 The control that could have falsified the fix. A live fingerprint that ESTABLISHES a
    /// different source set, with a MAJOR version bump, must still be refused — #2813 is a
    /// data-loss issue (four client documents, one unrecoverable) and deferring on an empty fold
    /// must not become deferring on a real disagreement.
    /// </summary>
    [Fact]
    public void ARealDisagreementWithAMajorBump_IsStillRefused()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            JustSeeded("4ed23561cfd142d1", LiveSources, currentModuleVersion: "2.0.0"),
            LiveSources, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused,
            "an established live source set that disagrees is a MEASUREMENT — #2813's refusal is "
            + "untouched");
        result.CompilationStatus.Should().Be(CompilationStatus.Pending, "a real compile is dispatched");
        result.LatestAssemblyPath.Should().BeNull("the rejected bytes stop serving");
    }

    /// <summary>The compatible-version half of the same control: a real disagreement still reaches
    /// #3583's stale-but-serving row and still dispatches the compile.</summary>
    [Fact]
    public void ARealDisagreementWithinTheSameMajor_IsStillStaleAdopted()
    {
        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            JustSeeded("4ed23561cfd142d1", LiveSources), LiveSources, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.StaleAdopted);
        result.CompilationStatus.Should().Be(CompilationStatus.Pending);
        result.RequestedSourceStampAt.Should().BeNull("a judgement that RAN consumes the request");
    }

    /// <summary>
    /// 🚨 Equality outranks the guard. A NodeType that genuinely compiles from no sources at all
    /// carries the empty fold on BOTH sides, and that is an honest match — demoting it to deferred
    /// (or to Unverified) would trade one wrong verdict for another.
    /// </summary>
    [Fact]
    public void ATypeThatCompilesFromNoSourcesAtAll_StillVerifies()
    {
        var sourceless = JustSeeded(
                NodeTypeSourceFingerprint.EmptySourceSet, ImmutableDictionary<string, long>.Empty)
            with
            { AdoptedSourceFingerprint = NodeTypeSourceFingerprint.EmptySourceSet };

        var result = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            sourceless, ImmutableDictionary<string, long>.Empty, canCompileLocally: true);

        result.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified,
            "producer and consumer both hashed nothing, and they agree");
        result.RequestedSourceStampAt.Should().BeNull("answered");
    }

    // ── The shared predicate ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one definition every writer asks, so no two of them can disagree about when the
    /// three-way is answerable. The third column is whether ANY declared source query matched a
    /// node — the snapshot, not the hash.
    /// </summary>
    [Theory]
    // A legacy bundle records no fingerprint: the verdict is Unverified and needs no live side.
    [InlineData(null, null, 0, true)]
    [InlineData(null, "4ed23561cfd142d1", 1, true)]
    // #2813 — the owner has not published a live fingerprint yet.
    [InlineData(BundleFingerprint, null, 0, false)]
    [InlineData(BundleFingerprint, "", 1, false)]
    // #4208 — it HAS published one, and not one declared query matched a node.
    [InlineData(BundleFingerprint, "e3b0c44298fc1c14", 0, false)]
    // …the same, with a live fingerprint that is NOT the empty fold — unreachable in practice, but
    // it pins that the witness is the snapshot rather than the hash.
    [InlineData(BundleFingerprint, "4ed23561cfd142d1", 0, false)]
    // A query DID match — answerable, whether the fingerprints agree or not, and INCLUDING the
    // case where the fold dropped every matched node (an executable cell, a blank file).
    [InlineData(BundleFingerprint, "4ed23561cfd142d1", 2, true)]
    [InlineData(BundleFingerprint, "e3b0c44298fc1c14", 1, true)]
    // Equality outranks the witness: a match is a match, empty==empty included.
    [InlineData(BundleFingerprint, BundleFingerprint, 0, true)]
    [InlineData("e3b0c44298fc1c14", "e3b0c44298fc1c14", 0, true)]
    public void CanJudgeAdoption_IsAnsweredByEvidence(
        string? adopted, string? live, int matchedSources, bool expected)
    {
        var snapshot = ImmutableDictionary.CreateRange(
            Enumerable.Range(0, matchedSources)
                .Select(i => new KeyValuePair<string, long>($"{TypePath}/Source/n{i}", 638L + i)));
        NodeTypeCompilationHelpers.CanJudgeAdoption(
            new NodeTypeDefinition
            {
                AdoptedSourceFingerprint = adopted,
                CurrentSourceFingerprint = live,
                CurrentSourceVersions = snapshot,
            },
            snapshot).Should().Be(expected);
    }
}
