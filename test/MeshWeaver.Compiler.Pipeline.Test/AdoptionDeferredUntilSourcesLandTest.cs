using System;
using System.Collections.Immutable;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>The PRODUCTION path of MeshWeaver#4208</b>, on a real mesh: the seeder's pre-write decline
/// and the owner's stamp, driven by a real <c>PrebuiltAssemblySeeder.Seed</c> against a real node,
/// rather than by a hand-built record handed to a pure function.
///
/// <para><see cref="AdoptionIsNotJudgedFromAnEmptySourceSetTest"/> pins the DECISION. It cannot pin
/// the two things the decision is worth nothing without, and both were the defect: that the SEEDER
/// stops declining on a source set nobody established, and that the standing stamp request is
/// actually re-evaluated when a source node arrives. A pure test stays green while either of those
/// is broken.</para>
///
/// <para><b>The shape, exactly as the gate ran it</b> (MeshWeaver.Reinsurance#204, 2026-09-13):</para>
/// <list type="number">
///   <item>a NodeType exists and <b>no</b> source node matches its declared queries yet — the state
///     <c>Reinsurance/AggregateSection</c> was in, sorting first in its namespace ahead of the
///     <c>Reinsurance/Source/*</c> nodes two of its queries read;</item>
///   <item>a bundle is seeded whose recorded source fingerprint <b>disagrees</b> with anything this
///     mesh could compute — which, before #4208, declined before writing and (one step on) parked
///     the type on <c>CS0246</c>s about code nothing is wrong with;</item>
///   <item>the source node lands, the sources watcher publishes, and the STANDING request is
///     fulfilled — no timer, no poll, nothing re-driven by hand.</item>
/// </list>
/// </summary>
public class AdoptionDeferredUntilSourcesLandTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/DeferredAdoptionType";

    /// <summary>The source node this type declares and does not have YET. Its text is what both
    /// fingerprints are computed over, so the bundle below can be given the very value the owner
    /// will compute once it arrives — which is what makes the convergence assertion a real
    /// verification rather than a tautology.</summary>
    private static MeshNode SourceNode() =>
        new("fixture", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "fixture",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = "public static class DeferredAdoptionFixture { public static int N() => 11; }",
            },
        };

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private IMessageHub SeedHub(string id) =>
        Mesh.GetHostedHub(
            new Address("adoption-defer", id),
            c => c.AddData().WithGraphTypes(),
            HostedHubCreation.Always)
        ?? throw new InvalidOperationException("HostedHubCreation.Always always yields a hub");

    /// <summary>Real PE bytes with a real MVID — the test assembly. The seeder stamps
    /// <c>LatestAssemblyMvid</c> from the bytes it is handed, so this identity is the positive
    /// signal that an adoption LANDED.</summary>
    private static byte[] BundleBytes() =>
        File.ReadAllBytes(typeof(AdoptionDeferredUntilSourcesLandTest).Assembly.Location);

    private static NodeTypeDefinition Definition(MeshNode node) =>
        (NodeTypeDefinition)node.Content!;

    [Fact]
    public async Task ABundleSeededBeforeTheSourcesLand_IsAdopted_AndVerifiesWhenTheyArrive()
    {
        // ── 1. The NodeType, with NOT ONE of its declared source queries matching anything. ──
        var typeNode = MeshNode.FromPath(TypePath) with
        {
            Name = "DeferredAdoptionType",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = CompilationStatus.Ok,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                // What the owner's sources watcher publishes for a type whose queries match
                // nothing: the empty snapshot, and the fold of an empty compile input. Both are
                // stated rather than waited for, because "nothing matched" has no arrival to wait
                // on — which is precisely why the judgement below cannot be made from them.
                CurrentSourceVersions = ImmutableDictionary<string, long>.Empty,
                CurrentSourceFingerprint = NodeTypeSourceFingerprint.EmptySourceSet,
            },
        };
        await MeshService.CreateNode(typeNode).Should().Within(TestTimeouts.Convergence).Emit();
        await Mesh.GetMeshNodeStream(TypePath).Should().Within(TestTimeouts.Convergence)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.CurrentSourceFingerprint,
                            NodeTypeSourceFingerprint.EmptySourceSet, StringComparison.Ordinal));

        // The fingerprint the bundle records — the value the owner WILL compute once the source
        // node lands, and which it cannot compute now. It disagrees with the empty fold, so before
        // #4208 this is the input that made the seeder decline and the owner refuse.
        var bundleFingerprint = NodeTypeSourceFingerprint.Compute([SourceNode()], TypePath);
        bundleFingerprint.Should().NotBe(NodeTypeSourceFingerprint.EmptySourceSet,
            "the premise of the whole test: the bundle's fingerprint is NOT the one this mesh can "
            + "compute right now");

        // ── 2. Seed it. The bytes must be ADOPTED, not declined. ─────────────────────────────
        var bytes = BundleBytes();
        var bundleMvid = ServedBuildIdentity.OfBytes(bytes);
        bundleMvid.Should().NotBeNullOrEmpty("the test assembly is a real PE image with an MVID");

        // SeedDetailed rather than Seed: the boolean cannot say WHY, and "declined on stale
        // sources" and "nothing was written" are different failures with different fixes.
        var outcome = await PrebuiltAssemblySeeder.SeedDetailed(
                SeedHub("defer"), TypePath, bytes, pdbBytes: null,
                frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
                logger: null, dependencies: null, sourceFingerprint: bundleFingerprint)
            .Should().Within(TestTimeouts.WriteConvergence).Emit("a seed completes either way");
        outcome.Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted,
            "a fingerprint that disagrees with the EMPTY fold is not a disagreement about the "
            + "source — not one declared query has matched a node here — so the pre-write decline "
            + "must NOT fire (#4208). Declining reaches AfterStaleDecline, which on a record whose "
            + "build does not resolve dispatches the very compile that parked "
            + "Reinsurance/AggregateSection on CS0246s about correct code");

        var seeded = await Mesh.GetMeshNodeStream(TypePath).Should().Within(TestTimeouts.WriteConvergence)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, bundleMvid, StringComparison.Ordinal));

        // ── 3. The judgement is DEFERRED — the one-shot request is left standing. ────────────
        var afterSeed = Definition(seeded);
        afterSeed.RequestedSourceStampAt.Should().NotBeNull(
            "the stamp request is ONE-SHOT: answering it from a source set nobody established "
            + "spends the only chance to refuse and closes the question for good");
        afterSeed.CompilationStatus.Should().NotBe(CompilationStatus.Pending,
            "no compile is dispatched — there is nothing to compile, and compiling nothing is what "
            + "produced the parked type");
        afterSeed.BuildProvenance.Should().NotBe(BuildProvenance.AdoptionRefused,
            "a refusal here would be a verdict formed from an absence");

        // …and nothing re-drives it while the sources are still missing: a standing request must
        // not become a poll.
        await Mesh.GetMeshNodeStream(TypePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                        && (d.CompilationStatus is CompilationStatus.Pending
                                                 or CompilationStatus.Compiling
                            || d.BuildProvenance == BuildProvenance.AdoptionRefused))
            .Should()
            .NotEmit(TestTimeouts.Quick,
                "the wait is REACTIVE — the type sits on the adopted build until the sources "
                + "arrive; nothing retries, nothing polls, nothing parks");

        // ── 4. The sources land. The STANDING request converges, with nothing driven by hand. ─
        await MeshService.CreateNode(SourceNode()).Should().Within(TestTimeouts.Convergence).Emit();

        var judged = await Mesh.GetMeshNodeStream(TypePath).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && d.RequestedSourceStampAt is null,
                "the sources watcher's publication fulfils the request it left standing, in the "
                + "same write that establishes the value — no timer, no poll, no re-drive");

        var final = Definition(judged);
        final.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified,
            "the fingerprints now agree, because the bundle WAS built from this source — which is "
            + $"the verdict every later sibling got. Live fingerprint: {final.CurrentSourceFingerprint}");
        final.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"no compile was ever needed. Error: {final.CompilationError ?? "(none)"}");
        final.LatestAssemblyMvid.Should().Be(bundleMvid,
            "the adopted bytes are still the ones serving — nothing replaced them");
        final.IsDirty.Should().BeFalse("CompiledSources is stamped with the live set");
    }
}
