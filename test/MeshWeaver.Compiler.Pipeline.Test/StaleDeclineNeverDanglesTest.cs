using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
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
/// 🚨 <b>A stale-source decline must never leave a DANGLING record</b> (memex.systemorph.com,
/// 2026-09-08). The decline-before-writing branch (#2813) leaves "the live build's coordinates in
/// place" so the build that is serving keeps serving. After a pod restart those coordinates named
/// a <c>local</c> collection path in a dead pod's <c>/tmp</c>: no process could load them, the
/// branch's own comment claimed "the caller compiles" (it did not), and both replicas degraded
/// every read of <c>Crm/Client</c> content for hours.
///
/// <para>Written RED first: against the pre-fix seeder the stale arm below leaves the record
/// byte-for-byte as it was — coordinates still naming bytes the store does not hold, status still
/// <c>Ok</c>, nothing dispatched — which is exactly the assertion
/// <c>LeavingHubAdoptionSweepTest.ABundleWhoseFingerprintDisagrees…</c> makes for a record whose
/// build DOES resolve. The two tests differ by one thing: whether the store answers for the
/// claimed build.</para>
///
/// <para>Pure pins on the decision first (<see cref="PrebuiltAssemblySeeder.AfterStaleDecline"/>),
/// then the real-mesh arm: a live type whose record claims a build at a version the store never
/// held, seeded with a bundle whose fingerprint disagrees — the record must lose the dead
/// coordinates and a compile must be dispatched (<c>Pending</c> through the one door).</para>
/// </summary>
public class StaleDeclineNeverDanglesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string DeadMvid = "dead0000dead0000";
    private static readonly string FixtureLiveFingerprint =
        NodeTypeSourceFingerprint.Compute([], "type/FixtureLiveSources");

    private static NodeTypeDefinition ClaimingABuild() => new()
    {
        CompilationStatus = CompilationStatus.Ok,
        LastCompiledVersion = 5,
        LatestAssemblyCollection = FileSystemAssemblyStore.FileSystemCollectionName,
        LatestAssemblyPath = "Type_Stale/v5-dead.dll",
        LatestAssemblyMvid = DeadMvid,
        CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion,
        CurrentSourceFingerprint = FixtureLiveFingerprint,
    };

    // ── the pure decision ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABuildThatResolvesHere_IsLeftAlone()
        => PrebuiltAssemblySeeder.AfterStaleDecline(ClaimingABuild(), liveBuildResolvesHere: true, canCompileLocally: true, "m")
            .Should().BeNull("the live build is serving — the #2813 rule stands");

    [Fact]
    public void ABuildThatDoesNotResolveHere_IsClearedAndACompileIsDispatched()
    {
        var written = PrebuiltAssemblySeeder.AfterStaleDecline(ClaimingABuild(), liveBuildResolvesHere: false, canCompileLocally: true, "m");
        written.Should().NotBeNull("a record naming bytes nobody can load must not stand");
        written!.CompilationStatus.Should().Be(CompilationStatus.Pending, "through the one door");
        written.DispatchedBuildInputs.Should().NotBeNull("the door records what the dispatch is for (#3390)");
        written.LatestAssemblyCollection.Should().BeNull();
        written.LatestAssemblyPath.Should().BeNull();
        written.LatestAssemblyMvid.Should().BeNull();
        written.CompiledSources.Should().BeNull("a snapshot of a build that no longer exists is an unearned claim");
    }

    [Fact]
    public void ACompileAlreadyInFlight_IsNotDispatchedAgain()
    {
        // Once per decline, never per activation: a second decline of the same record is a no-op.
        foreach (var status in new[] { CompilationStatus.Pending, CompilationStatus.Compiling })
            PrebuiltAssemblySeeder.AfterStaleDecline(
                    ClaimingABuild() with { CompilationStatus = status }, liveBuildResolvesHere: false, canCompileLocally: true, "m")
                .Should().BeNull($"a record already {status} is left to the compile it carries");
    }

    [Fact]
    public void ARequirePrebuiltMesh_LeavesTheRecord_TheCallerSaysSoLoudly()
        => PrebuiltAssemblySeeder.AfterStaleDecline(ClaimingABuild(), liveBuildResolvesHere: false, canCompileLocally: false, "m")
            .Should().BeNull("clearing would leave the type with no build and no path to one; the caller logs Critical");

    [Fact]
    public void ARecordClaimingNoBuild_HasNothingToDangle()
        => PrebuiltAssemblySeeder.AfterStaleDecline(
                ClaimingABuild() with { LatestAssemblyCollection = null, LatestAssemblyPath = null },
                liveBuildResolvesHere: false, canCompileLocally: true, "m")
            .Should().BeNull();

    // ── the real-mesh arm ─────────────────────────────────────────────────────────────────────

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private IMessageHub SweepHub(string id) =>
        Mesh.GetHostedHub(
            new Address("adoption-sweep", id),
            c => c.AddData().WithGraphTypes(),
            HostedHubCreation.Always)
        ?? throw new InvalidOperationException("HostedHubCreation.Always always yields a hub");

    private static byte[] BundleBytes() =>
        File.ReadAllBytes(typeof(StaleDeclineNeverDanglesTest).Assembly.Location);

    [Fact]
    public async Task ADeclinedBundle_OverARecordWhoseBuildTheStoreDoesNotHold_ClearsItAndDispatchesACompile()
    {
        const string typePath = "type/DanglingStaleType";
        var typeNode = MeshNode.FromPath(typePath) with
        {
            Name = "DanglingStaleType",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
            Content = ClaimingABuild(),
        };
        await MeshService.CreateNode(typeNode).Should().Within(20.Seconds()).Emit();
        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                        && string.Equals(d.LatestAssemblyMvid, DeadMvid, StringComparison.Ordinal)
                        && string.Equals(d.CurrentSourceFingerprint, FixtureLiveFingerprint, StringComparison.Ordinal));

        // The store of this mesh has never held version 5 of this type — exactly the restarted-pod
        // shape: the record claims a build, the process has no bytes for it.
        var store = Mesh.ServiceProvider.GetRequiredService<IAssemblyStore>();
        var path = await store.TryGetAssemblyPath(typePath, 5).Should().Within(10.Seconds()).Emit();
        path.Should().BeNull("the precondition: the claimed build does not resolve here");

        var outcome = await PrebuiltAssemblySeeder.SeedDetailed(
                SweepHub("sweep"), typePath, BundleBytes(), pdbBytes: null,
                frameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid, logger: null,
                dependencies: null, sourceFingerprint: FixtureLiveFingerprint + "-stale")
            .Should().Within(20.Seconds()).Emit("a decline completes like any other");
        outcome.Should().Be(PrebuiltAssemblySeeder.SeedOutcome.DeclinedStaleSourcesCompileDispatched,
            "the bytes were declined on their fingerprint AND the dead build was cleared with a compile dispatched");

        await Mesh.GetMeshNodeStream(typePath).Should().Within(20.Seconds())
            .Match(n => n?.Content is NodeTypeDefinition d
                        && !string.Equals(d.LatestAssemblyMvid, DeadMvid, StringComparison.Ordinal)
                        && d.CompilationStatus != CompilationStatus.Ok || (n?.Content is NodeTypeDefinition e
                        && !string.Equals(e.LatestAssemblyMvid, DeadMvid, StringComparison.Ordinal)),
                "the record no longer names the dead build — it is Pending, or a fresh compile has replaced it");
    }
}
