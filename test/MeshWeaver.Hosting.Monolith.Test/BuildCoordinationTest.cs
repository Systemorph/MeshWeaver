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
