using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
/// 🚨 <b>The include door of MeshWeaver#4280, on a real mesh</b> (first review finding on #4293):
/// every source the type's QUERIES resolve is present, and the one thing still landing is an
/// <c>@@</c>-include target — a node no query matches, so its arrival changes nothing the sources
/// watcher observes through its query.
///
/// <para><see cref="AdoptionIsNotJudgedFromAPartialSourceSetTest"/> pins the DECISION for includes.
/// This pins the two halves a pure test cannot: that the SEEDER adopts rather than declines when
/// only an include is missing, and that the standing request CONVERGES when the include lands —
/// which needs the owner to notice an arrival its source queries cannot see
/// (<c>NodeTypeCompilationHelpers.AdoptedIncludeArrivals</c>). Before that trigger existed, the
/// deferral was correct and the wait was unbounded: nothing re-ran the fingerprint until an
/// unrelated source edit or a restart.</para>
/// </summary>
public class AdoptionDeferredUntilIncludeLandsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/DeferredIncludeType";
    private const string IncludePath = "Snippets/DeferredIncludeGreeting";

    /// <summary>The type's own source — PRESENT from the start — which includes a snippet that is
    /// not on the mesh yet. The directive is authored as the snippet's mesh path; the include
    /// reader tries it anchored under the type first and falls back to the authored path.</summary>
    private static MeshNode SourceNode() =>
        new("fixture", $"{TypePath}/Source")
        {
            NodeType = "Code",
            Name = "fixture",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = "public static class DeferredIncludeFixture { public static int N() => 11; }\n\n@@" + IncludePath,
            },
        };

    private const string IncludeText = "public static class DeferredIncludeGreeting { public static string Hi() => \"hi\"; }";

    private static MeshNode IncludeNode() =>
        new("DeferredIncludeGreeting", "Snippets")
        {
            NodeType = "Code",
            Name = "DeferredIncludeGreeting",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Language = "csharp", Code = IncludeText },
        };

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private IMessageHub SeedHub(string id) =>
        Mesh.GetHostedHub(
            new Address("adoption-include-defer", id),
            c => c.AddData().WithGraphTypes(),
            HostedHubCreation.Always)
        ?? throw new InvalidOperationException("HostedHubCreation.Always always yields a hub");

    private static byte[] BundleBytes() =>
        File.ReadAllBytes(typeof(AdoptionDeferredUntilIncludeLandsTest).Assembly.Location);

    private static NodeTypeDefinition Definition(MeshNode node) =>
        (NodeTypeDefinition)node.Content!;

    [Fact]
    public async Task ABundleSeededBeforeItsIncludeLands_IsAdopted_AndVerifiesWhenTheIncludeArrives()
    {
        // ── 1. The NodeType and its ONE source, present; the include it names is not. ────────
        var typeNode = MeshNode.FromPath(TypePath) with
        {
            Name = "DeferredIncludeType",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = CompilationStatus.Ok,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
            },
        };
        await MeshService.CreateNode(typeNode).Should().Within(TestTimeouts.Convergence).Emit();
        await MeshService.CreateNode(SourceNode()).Should().Within(TestTimeouts.Convergence).Emit();

        // The fingerprint the bundle records — over the source AND the include it was compiled
        // with. It is the value the owner will compute once the include lands and cannot now.
        var bundleFingerprint = NodeTypeSourceFingerprint.Compute(
            [SourceNode()], TypePath,
            new Dictionary<string, string> { [IncludePath] = IncludeText });
        var withoutInclude = NodeTypeSourceFingerprint.Compute(
            [SourceNode()], TypePath, ImmutableDictionary<string, string>.Empty);
        bundleFingerprint.Should().NotBe(withoutInclude,
            "the premise: the include is in the bytes, so the fingerprints differ until it lands");

        // ── 2. Seed it, naming the include. ADOPTED — not declined on a disagreement. ───────
        var bytes = BundleBytes();
        var bundleMvid = ServedBuildIdentity.OfBytes(bytes);

        var outcome = await PrebuiltAssemblySeeder.SeedDetailed(
                SeedHub("include"), TypePath, bytes, pdbBytes: null,
                frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
                logger: null, dependencies: null, sourceFingerprint: bundleFingerprint,
                moduleVersion: null,
                sourcePaths: [SourceNode().Path],
                sourceIncludes: [IncludePath])
            .Should().Within(TestTimeouts.WriteConvergence).Emit("a seed completes either way");
        outcome.Should().Be(PrebuiltAssemblySeeder.SeedOutcome.Adopted,
            "every query-resolved path is live and the fingerprints disagree ONLY because an include "
            + "the bundle names has not landed — an arrival, not a move, so the pre-write decline "
            + "must not fire");

        // The seed's own subscribe activates the owner; its sources watcher then publishes the
        // query-resolved set — the one source — with the fingerprint over it and the includes it
        // resolved as present: none, because the include is ABSENT (an answer, not a stall). That
        // publication is the one that judges the standing request, and must DEFER it.
        var seeded = await Mesh.GetMeshNodeStream(TypePath).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, bundleMvid, StringComparison.Ordinal)
                        && d.CurrentSourceVersions is { Count: 1 }
                        && d.CurrentSourceIncludes is { Count: 0 },
                "the owner publishes the one source, its fingerprint, and an EMPTY include list — "
                + "the include is absent, and absence is an answer");

        // ── 3. Deferred: the request stands, nothing is dispatched, nothing polls. ──────────
        var afterSeed = Definition(seeded);
        afterSeed.AdoptedSourceIncludes.Should().Equal([IncludePath], "the seeder stamps the includes");
        afterSeed.RequestedSourceStampAt.Should().NotBeNull("the judgement is deferred, not spent");
        afterSeed.CompilationStatus.Should().NotBe(CompilationStatus.Pending,
            "no compile of a set that is short of an include it binds — that is the CS0246 park");
        afterSeed.BuildProvenance.Should().NotBe(BuildProvenance.AdoptionRefused);
        afterSeed.BuildProvenance.Should().NotBe(BuildProvenance.StaleAdopted,
            "a missing include must not be read as the source having MOVED");

        await Mesh.GetMeshNodeStream(TypePath)
            .Where(n => n?.Content is NodeTypeDefinition d
                        && (d.CompilationStatus is CompilationStatus.Pending
                                                 or CompilationStatus.Compiling
                            || d.BuildProvenance is BuildProvenance.AdoptionRefused
                                                   or BuildProvenance.StaleAdopted))
            .Should()
            .NotEmit(TestTimeouts.Quick, "the wait is reactive — nothing retries, nothing parks");

        // ── 4. The include lands. The declared SOURCE set does not change at all. ──────────
        await MeshService.CreateNode(IncludeNode()).Should().Within(TestTimeouts.Convergence).Emit();

        var judged = await Mesh.GetMeshNodeStream(TypePath).Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.Content is NodeTypeDefinition d && d.RequestedSourceStampAt is null,
                "an include arrival is invisible to the source queries; the owner watches the "
                + "adopted-but-absent includes themselves and re-runs the fingerprint when one lands");

        var final = Definition(judged);
        final.CurrentSourceIncludes.Should().Equal([IncludePath],
            "the owner now resolves the include as present, published beside the fingerprint");
        final.CurrentSourceVersions.Should().HaveCount(1,
            "the query-resolved set never changed — that is the whole point of this test");
        final.BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified,
            $"the fingerprints agree once the include is in the fold. Live: {final.CurrentSourceFingerprint}");
        final.CompilationStatus.Should().Be(CompilationStatus.Ok,
            $"no compile was ever needed. Error: {final.CompilationError ?? "(none)"}");
        final.LatestAssemblyMvid.Should().Be(bundleMvid, "the adopted bytes are still the ones serving");
    }
}
