using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// The build protocol's coordination contract (<c>Doc/Architecture/BuildCoordination.md</c>):
/// the root materializes once, claims are granted by the node's own hub (never taken), a
/// non-holder's writes land on nothing, completing a build publishes a GO that later builds
/// never revoke, and releasing a claim hands the node to the next queued candidate.
/// </summary>
public class BuildCoordinationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(15);

    // The protocol-bake test drives a full cold Roslyn kickoff compile plus a protocol sweep —
    // the same budget reasoning as NodeTypeReleaseTest's override: align the base class's dispose
    // watchdog with the longest [Fact] timeout in the class so it cannot kill the test before its
    // own declared budget.
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(240);

    [Fact(Timeout = 60_000)]
    public async Task ClaimQueue_Go_And_HolderGuard()
    {
        var hub = Mesh;
        var workspace = hub.GetWorkspace();

        // Materialize once — a second Ensure must resolve to the same node, not a second create.
        var first = await hub.EnsureBuildNode().FirstAsync().ToTask();
        var second = await hub.EnsureBuildNode().FirstAsync().ToTask();
        first.Path.Should().Be(BuildNodeType.RootPath);
        second.Path.Should().Be(first.Path);

        // Candidate A registers and is granted by the arbiter (nobody holds the node).
        await hub.RequestBuildClaim("holder-a", "fp-a").FirstAsync().ToTask();
        var grantedA = await hub.ObserveBuildClaim("holder-a")
            .FirstAsync().Timeout(WaitBudget).ToTask();
        grantedA.FrameworkVersion.Should().Be("fp-a");
        grantedA.Status.Should().Be(BuildStatus.Planning);

        // Candidate B registers while A holds — it must QUEUE, not preempt.
        await hub.RequestBuildClaim("holder-b", "fp-b").FirstAsync().ToTask();
        var whileAHolds = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(n => n?.ContentAs<BuildState>(hub.JsonSerializerOptions))
            .Where(s => s?.RequestedClaims?.ContainsKey("holder-b") == true)
            .FirstAsync().Timeout(WaitBudget).ToTask();
        whileAHolds!.ClaimedBy.Should().Be("holder-a");

        // A non-holder's guarded write is a no-op — a superseded builder cannot corrupt state.
        await hub.UpdateBuildAsHolder("holder-b", s => s with { Error = "must not land" })
            .FirstAsync().ToTask();
        var afterGuardedWrite = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Where(n => n is not null).FirstAsync().ToTask();
        afterGuardedWrite.ContentAs<BuildState>(hub.JsonSerializerOptions)!
            .Error.Should().BeNull();

        // A completes: GO for fp-a lands, the claim is released, and the arbiter grants B.
        await hub.CompleteBuild("holder-a", new BuildGo("fp-a", DateTime.UtcNow))
            .FirstAsync().ToTask();
        var goA = await hub.ObserveBuildGo("fp-a").FirstAsync().Timeout(WaitBudget).ToTask();
        goA.FrameworkVersion.Should().Be("fp-a");
        var grantedB = await hub.ObserveBuildClaim("holder-b")
            .FirstAsync().Timeout(WaitBudget).ToTask();
        grantedB.FrameworkVersion.Should().Be("fp-b");

        // B completes: fp-b's GO is ADDED — fp-a's GO must survive. Old-image silos stay ready.
        await hub.CompleteBuild("holder-b", new BuildGo("fp-b", DateTime.UtcNow))
            .FirstAsync().ToTask();
        var final = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(n => n?.ContentAs<BuildState>(hub.JsonSerializerOptions))
            .Where(s => s?.Ready?.ContainsKey("fp-b") == true)
            .FirstAsync().Timeout(WaitBudget).ToTask();
        final!.Ready!.Should().ContainKey("fp-a");
        final.Ready.Should().ContainKey("fp-b");
        final.ClaimedBy.Should().BeNull();
        final.Status.Should().Be(BuildStatus.Ready);
    }

    /// <summary>
    /// The executor path end-to-end: a protocol-driven sweep claims the root, opens a chunk per
    /// partition with its own <c>_Activity</c>, records the release paths the build produced, and
    /// publishes the GO — including when the share already holds every build (that re-publish IS
    /// the crashed-before-GO healing path).
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task ProtocolDrivenBake_PublishesGo_AndChunkReportsReleases()
    {
        var workspace = Mesh.GetWorkspace();
        const string partition = "TestBuildProto";
        const string typeId = "Sample";
        var typePath = $"{partition}/{typeId}";

        await NodeFactory.CreateNode(new MeshNode(typeId, partition)
        {
            Name = "Sample Type",
            NodeType = MeshNode.NodeTypePath,
            Content = new NodeTypeDefinition
            {
                Description = "Sample for the protocol bake test.",
                Configuration = "config => config.AddDefaultLayoutAreas()"
            }
        }).Should().Emit();

        // The kickoff compile (per-NodeType hub watcher) settles first — cold Roslyn on a 2-core
        // CI runner can take 60-90s.
        _ = await workspace.GetMeshNodeStream(typePath)
            .Should().Within(90.Seconds())
            .Match(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestReleasePath));

        var outcomes = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, buildProtocol: true)
            .ToList()
            .Timeout(TimeSpan.FromSeconds(120))
            .ToTask();
        (outcomes.Count > 0).Should().Be(true);

        var root = await workspace.GetMeshNodeStream(BuildNodeType.RootPath)
            .Select(n => n?.ContentAs<BuildState>(Mesh.JsonSerializerOptions))
            .Where(s => s?.Ready is { Count: > 0 })
            .FirstAsync().Timeout(WaitBudget).ToTask();
        root!.Status.Should().Be(BuildStatus.Ready);
        root.ClaimedBy.Should().BeNull();

        var chunk = await workspace.GetMeshNodeStream($"{BuildNodeType.RootPath}/{partition}")
            .Select(n => n?.ContentAs<BuildState>(Mesh.JsonSerializerOptions))
            .Where(s => s is { Status: BuildStatus.Ready })
            .FirstAsync().Timeout(WaitBudget).ToTask();
        // The sweep may have re-baked the type (a host without an assembly store re-compiles by
        // design), so the chunk's written path can be a NEWER release than the kickoff one — what
        // is contractual is that the paths it reports are release nodes of this chunk's types.
        chunk!.WrittenPaths.Should().NotBeNull();
        (chunk.WrittenPaths!.Count > 0).Should().Be(true);
        chunk.WrittenPaths.All(p => p.StartsWith($"{typePath}/Release/", StringComparison.Ordinal))
            .Should().Be(true);
        chunk.ActivityPath.Should().NotBeNull();

        var activity = await workspace.GetMeshNodeStream(chunk.ActivityPath!)
            .Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions))
            .Where(l => l is not null && l.Status.IsTerminal())
            .FirstAsync().Timeout(WaitBudget).ToTask();
        activity!.Status.Should().Be(ActivityStatus.Succeeded);
    }

    // ---- Arbitrate as a pure decision procedure: the staleness rules without wall-clock ----

    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode BuildNode(BuildState state) =>
        new("Build", "Admin") { NodeType = BuildNodeType.NodeType, Content = state };

    [Fact]
    public void Arbitrate_GrantsEarliestRequest_WhenUnclaimed()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var node = BuildNode(new BuildState
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("late", new BuildClaimRequest("fp-late", t0.AddSeconds(5)))
                .Add("early", new BuildClaimRequest("fp-early", t0)),
        });

        var result = BuildNodeType.Arbitrate(node, Options, t0.AddSeconds(10))
            .ContentAs<BuildState>(Options)!;

        result.ClaimedBy.Should().Be("early");
        result.FrameworkVersion.Should().Be("fp-early");
        result.Status.Should().Be(BuildStatus.Planning);
        result.RequestedClaims.Should().NotContainKey("early");
        result.RequestedClaims.Should().ContainKey("late");
    }

    [Fact]
    public void Arbitrate_LeavesLiveClaimAlone()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var node = BuildNode(new BuildState
        {
            ClaimedBy = "current",
            ClaimedAt = t0,
            HeartbeatAt = t0,
            Status = BuildStatus.Building,
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("challenger", new BuildClaimRequest("fp-x", t0.AddSeconds(1))),
        });

        // Heartbeat is fresh: the challenger stays queued, the node is returned UNCHANGED
        // (same reference — a redundant arbitration tick must write nothing).
        var result = BuildNodeType.Arbitrate(
            node, Options, t0 + BuildNodeType.ClaimStaleAfter - TimeSpan.FromSeconds(1));
        result.Should().BeSameAs(node);
    }

    [Fact]
    public void Arbitrate_StealsStaleClaim_ForNextCandidate()
    {
        var t0 = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);
        var node = BuildNode(new BuildState
        {
            ClaimedBy = "dead-builder",
            ClaimedAt = t0,
            HeartbeatAt = t0,
            Status = BuildStatus.Building,
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("successor", new BuildClaimRequest("fp-next", t0.AddMinutes(1))),
        });

        var result = BuildNodeType.Arbitrate(
                node, Options, t0 + BuildNodeType.ClaimStaleAfter + TimeSpan.FromSeconds(1))
            .ContentAs<BuildState>(Options)!;

        result.ClaimedBy.Should().Be("successor");
        result.FrameworkVersion.Should().Be("fp-next");
    }

    [Fact]
    public void Arbitrate_NoPendingClaims_IsUntouched()
    {
        var node = BuildNode(new BuildState { Status = BuildStatus.Ready });
        BuildNodeType.Arbitrate(node, Options, DateTime.UtcNow).Should().BeSameAs(node);
    }
}
