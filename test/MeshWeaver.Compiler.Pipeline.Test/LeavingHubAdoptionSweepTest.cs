using System;
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
/// Systemorph/MeshWeaver#3129 — the prebuilt-bundle adoption sweep on a hub that is LEAVING.
///
/// <para><b>What was measured.</b> A pod terminating under a 30-minute grace period kept running
/// the adoption sweep against NodeType nodes the whole deployment shares: each pass stamped a
/// stale bundle's coordinates over the live build's, the owner refused the adoption (#2813) and
/// cleared them, and the healthy pods' instances of the type sat on the fallback card for the
/// 120 s self-heal bound — per type, per roll. Every one of the 1424 refusals came from the pod
/// that was leaving.</para>
///
/// <para><b>The rule, and the controlled experiment that pins it.</b> A leaving hub does not seed:
/// <see cref="PrebuiltAssemblySeeder.Seed(IMessageHub, string, byte[], byte[], string, Microsoft.Extensions.Logging.ILogger, System.Collections.Generic.IReadOnlyDictionary{string, string}, string)"/>
/// answers "not adopted" and the shared record is byte-for-byte what it was. The control arm is
/// the SAME call on a live hub against the SAME node, which adopts — without it the leaving arm
/// would pass just as well against a seeder that had stopped writing altogether. The hub is
/// flipped the way #3109's test flips it: <c>Dispose()</c> begins teardown synchronously, so
/// <see cref="IMessageHub.IsShuttingDown"/> is true before the seed is even built.</para>
///
/// <para>The second test pins the seeder-side half of the #2813 refusal: a bundle whose source
/// fingerprint already disagrees with the owner's published live fingerprint is declined BEFORE
/// it writes, so the live build's coordinates are never replaced and never cleared — the clobber
/// #3129 measured is unreachable even on a healthy hub. Control arm: the same call with the
/// matching fingerprint writes.</para>
/// </summary>
public class LeavingHubAdoptionSweepTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string LiveAssemblyPath = "live/LeavingSweepType.dll";
    private const string LiveMvid = "11ee0000aaaa0000";
    /// <summary>
    /// 🚨 The fingerprint the OWNER will hold for this fixture type — computed with the product's
    /// own function over the type's live source set, which for a fixture type is EMPTY.
    ///
    /// <para>It used to be the literal <c>"live-fingerprint-fixture"</c>, on the reasoning that "a
    /// sourceless fixture type publishes nothing that could move it". That is false: a sourceless
    /// type publishes the fingerprint of the empty source set, and the owner's sources watcher
    /// writes it over whatever the fixture seeded — at a moment nothing in the test controls. When
    /// it landed mid-test the CONTROL arm's matching bundle stopped matching, the owner refused the
    /// adoption it was supposed to accept, cleared the coordinates and dispatched a fresh compile,
    /// and the final assertion waited 20 s for an MVID that was never going to arrive. Twice on the
    /// merge queue for PR #3143 (runs 33673071012 and 33669188031), where a queue red costs every
    /// PR behind it.</para>
    ///
    /// <para>Stating the value the product itself computes removes the race rather than narrowing
    /// it: the watcher's recompute now writes the SAME value, so there is no window in which the
    /// two arms mean something different. The two arms still differ by exactly one thing — whether
    /// the producer's fingerprint equals the live one — which is the whole subject of the test.</para>
    /// </summary>
    private static readonly string FixtureLiveFingerprint =
        NodeTypeSourceFingerprint.Compute([], "type/FixtureLiveSources");

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>A hub standing in for the sweep's hub — a child of the mesh so that disposing it
    /// flips ITS <see cref="IMessageHub.IsShuttingDown"/> while the mesh (and the node's owner)
    /// keep running. Carries a workspace, as every hub the seeder runs on does (the seeder
    /// writes through <c>hub.GetWorkspace()</c>).</summary>
    private IMessageHub SweepHub(string id) =>
        Mesh.GetHostedHub(
            new Address("adoption-sweep", id),
            c => c.AddData().WithGraphTypes(),
            HostedHubCreation.Always)
        ?? throw new InvalidOperationException("HostedHubCreation.Always always yields a hub");

    /// <summary>Real PE bytes with a real MVID — the test assembly itself. The seeder stamps
    /// <see cref="NodeTypeDefinition.LatestAssemblyMvid"/> from the bytes it is handed, so this
    /// identity is the positive signal that an adoption LANDED, independent of what the store
    /// answers for a location.</summary>
    private static byte[] BundleBytes() =>
        File.ReadAllBytes(typeof(LeavingHubAdoptionSweepTest).Assembly.Location);

    /// <summary>A NodeType that already serves a usable build compiled on this mesh — the record
    /// a sweep on another generation would clobber.</summary>
    private async Task CreateLiveType(string typePath)
    {
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = typePath[(typePath.LastIndexOf('/') + 1)..],
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                CompilationStatus = CompilationStatus.Ok,
                LatestAssemblyCollection = "assemblies",
                LatestAssemblyPath = LiveAssemblyPath,
                LatestAssemblyMvid = LiveMvid,
                CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
                CurrentSourceFingerprint = FixtureLiveFingerprint,
            },
        };
        await MeshService.CreateNode(typeNode).Should().Within(20.Seconds()).Emit();
        // Wait for BOTH fields, not just the MVID: the live fingerprint is what the seeder's
        // pre-write check and the owner's stamp check both read, so a test that proceeds before it
        // is observable is deciding against a record it has not established.
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, LiveMvid, StringComparison.Ordinal)
                        && string.Equals(d.CurrentSourceFingerprint, FixtureLiveFingerprint,
                            StringComparison.Ordinal));
    }

    private static IObservable<bool> Seed(IMessageHub hub, string typePath, byte[] bytes, string? fingerprint) =>
        PrebuiltAssemblySeeder.Seed(
            hub, typePath, bytes, pdbBytes: null,
            frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid,
            logger: null, dependencies: null, sourceFingerprint: fingerprint);

    /// <summary>True for any write the sweep could have made: a standing stamp request, a moved
    /// assembly identity, or a compile dispatched (Pending) — the three things #3129 saw.</summary>
    private static bool Touched(MeshNode? n) =>
        n?.Content is NodeTypeDefinition d
        && (d.RequestedSourceStampAt is not null
            || !string.Equals(d.LatestAssemblyMvid, LiveMvid, StringComparison.Ordinal)
            || d.CompilationStatus != CompilationStatus.Ok
            || d.BuildProvenance == BuildProvenance.AdoptionRefused);

    [Fact]
    public async Task ALeavingHub_LeavesTheSharedNodeTypeUntouched_AndALiveHubAdopts()
    {
        const string typePath = "type/LeavingSweepType";
        await CreateLiveType(typePath);
        var bytes = BundleBytes();
        var bundleMvid = ServedBuildIdentity.OfBytes(bytes);
        bundleMvid.Should().NotBeNullOrEmpty("the test assembly is a real PE image with an MVID")
            .And.NotBe(LiveMvid, "the adopted identity must be distinguishable from the live one");

        // THE LEAVING ARM — the hub the sweep runs on has begun shutting down.
        var leaving = SweepHub("leaving");
        leaving.Dispose();
        leaving.IsShuttingDown.Should().BeTrue("Dispose() begins this hub's teardown synchronously");
        leaving.IsLeaving().Should().BeTrue("a hub that is shutting down is leaving");

        var adopted = await Seed(leaving, typePath, bytes, fingerprint: null)
            .Should().Within(20.Seconds())
            .Emit("a leaving hub answers 'not adopted' — it never parks the seeding pass");
        adopted.Should().BeFalse("nothing is adopted on a hub that is leaving");

        await Mesh.GetMeshNodeStream(typePath).Where(Touched)
            .Should()
            .NotEmit(3.Seconds(), "a leaving hub must neither stamp, clear nor dispatch on a NodeType every "
                     + "generation shares — that write is what starved the healthy pods in #3129");

        // THE CONTROL ARM — the same call, the same node, a hub that stays.
        var live = SweepHub("live");
        live.IsLeaving().Should().BeFalse("the control hub is neither disposing nor hosted by a stopping process");

        var adoptedLive = await Seed(live, typePath, bytes, fingerprint: null)
            .Should().Within(20.Seconds()).Emit("a live hub's seed completes");
        adoptedLive.Should().BeTrue("on a live hub the same bundle IS adopted — without this arm the "
                                    + "leaving arm would prove nothing");
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, bundleMvid, StringComparison.Ordinal),
                "the live hub's adoption lands on the shared record");
    }

    [Fact]
    public async Task ABundleWhoseFingerprintDisagreesWithTheLiveSource_IsDeclinedBeforeItWrites()
    {
        const string typePath = "type/StaleBundleType";
        await CreateLiveType(typePath);
        var bytes = BundleBytes();
        var bundleMvid = ServedBuildIdentity.OfBytes(bytes);

        // The seeder decides on the owner's CURRENT snapshot of the node, and this IS that value:
        // the owner's sources watcher computes the same function over the same (empty) source set,
        // so its recompute writes the fingerprint back unchanged and cannot move under either arm.
        // See FixtureLiveFingerprint for what a hand-picked literal cost here.
        var liveFingerprint = FixtureLiveFingerprint;

        // THE STALE ARM — the producer names sources that are not the ones this mesh holds.
        var sweep = SweepHub("sweep");
        var adopted = await Seed(sweep, typePath, bytes, fingerprint: liveFingerprint + "-stale")
            .Should().Within(20.Seconds()).Emit("a decline completes like any other");
        adopted.Should().BeFalse("a bundle the owner would refuse is declined before it writes");

        await Mesh.GetMeshNodeStream(typePath).Where(Touched)
            .Should()
            .NotEmit(3.Seconds(), "declining before the write leaves the live build's coordinates in place — "
                     + "the stamp-then-refuse-then-clear sequence never starts");

        // THE CONTROL ARM — the same bundle, now naming the live sources: it writes.
        var adoptedLive = await Seed(sweep, typePath, bytes, fingerprint: liveFingerprint)
            .Should().Within(20.Seconds()).Emit("a matching fingerprint seeds");
        adoptedLive.Should().BeTrue("the fingerprint is the ONLY difference between the two arms");
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, bundleMvid, StringComparison.Ordinal),
                "the matching bundle's adoption lands on the shared record");
    }
}
