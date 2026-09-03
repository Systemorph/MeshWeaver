using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// PUBLISHING a grant the election already decided — the second half of an arbitration pass, and
/// the half that wedged a build (#1193).
///
/// <para><b>The defect.</b> <c>ArbitrateDurably</c> reads the candidate set off this cluster's
/// mirror, then reads the claim LOCK, then commits with a compare-and-set, then publishes the grant
/// on the Build node. Two storage round-trips separate the election from the publication, and a
/// candidate is free to STAND DOWN inside that window: a follower that saw the GO calls
/// <c>WithdrawBuildClaim</c>, which removes its registration and then finds the lock still unheld,
/// so its release half is a no-op. The pass then writes the lock naming a process that is not
/// listening and never will be.</para>
///
/// <para><b>Why that is permanent rather than merely wrong.</b> The takeover rule defends a LIVE
/// holder by design (<c>#1355</c>: a stopped heartbeat on a running process means busy, not dead).
/// With cluster membership the claim is therefore never taken over at all; without it every later
/// candidate waits out <c>ClaimStaleAfter</c> and is handed the same dead claim again. No builder
/// is elected, no NodeType is baked, no pod reaches ready — the readiness stall #1440 fixed from
/// the candidate's side, arriving instead through the arbiter's own commit.</para>
///
/// <para><b>Measured</b>, on <c>MeshWeaver.Hosting.Monolith.Test.BuildCoordinationTest
/// .Follower_StandsDown_SoTheNextBuildCanStillBeClaimed</c>: <b>2 failures in 15 runs on a
/// completely idle machine</b> — so load was not the cause; the control ran without any. The state
/// dumped at the failure was <c>Admin/Build</c> carrying
/// <c>ClaimedBy=&lt;the follower&gt;, Status=Planning, RequestedClaims=[], Ready=[fp]</c>, i.e. a
/// build node locked to a process whose driver had already completed. Which of the two records is
/// left behind depends on how the two writers interleave — the mirror's projection (measured) or
/// the durable claim LOCK, when the stand-down read it a moment before this pass wrote it — and
/// refusing the publication closes both, because the arbiter is the lock's only writer and hands
/// it straight back.</para>
///
/// <para>Everything here is a decision over constructed state with no wall-clock and no
/// concurrency, in the same family as <c>BuildNodeType.Arbitrate</c>'s tests: the interleaving is
/// BUILT, never raced for.</para>
/// </summary>
public class BuildGrantPublicationTest
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The candidate the election picked — and, in most tests here, the one that withdrew.</summary>
    private const string Winner = "pod-7/2c9f";

    private static BuildState Granted() => new()
    {
        ClaimedBy = Winner,
        ClaimedAt = T0,
        HeartbeatAt = T0,
        FrameworkVersion = "fp",
        Status = BuildStatus.Planning,
    };

    private static MeshNode Mirror(BuildState state) =>
        new("Build", "Admin") { NodeType = BuildNodeType.NodeType, Content = state };

    private static MeshNode Lock(BuildState state) =>
        new("_Claim", "Admin/Build") { NodeType = BuildNodeType.NodeType, Content = state };

    // ── the decision: is this winner still a candidate? ─────────────────────────────────────────

    /// <summary>
    /// The mirror exactly as <c>WithdrawBuildClaim</c> leaves it — registration gone, nobody
    /// holding, the GO recorded. The election that produced the grant ran two round-trips ago, on a
    /// candidate set that still contained this follower. Publishing now is what locks the build to
    /// a process that has already finished with it.
    /// </summary>
    [Fact]
    public void AGrantIsNotPublished_ToACandidateThatStoodDownWhileThePassRan()
    {
        var mirror = Mirror(new BuildState
        {
            Status = BuildStatus.Ready,
            Ready = ImmutableDictionary<string, BuildGo>.Empty
                .Add("fp", new BuildGo("fp", T0)),
        });

        BuildNodeType.ApplyGrant(mirror, Granted(), Options).Should().BeSameAs(mirror);
    }

    /// <summary>
    /// Still a candidate when the pass commits: the grant lands, and the registration it consumed
    /// goes with it while every OTHER candidate stays queued.
    /// </summary>
    [Fact]
    public void AGrantIsPublished_WhileTheWinnerIsStillACandidate()
    {
        var mirror = Mirror(new BuildState
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add(Winner, new BuildClaimRequest("fp", T0))
                .Add("other-pod", new BuildClaimRequest("fp", T0.AddSeconds(1))),
        });

        var published = BuildNodeType.ApplyGrant(mirror, Granted(), Options)
            .ContentAs<BuildState>(Options)!;

        published.ClaimedBy.Should().Be(Winner);
        published.Status.Should().Be(BuildStatus.Planning);
        published.FrameworkVersion.Should().Be("fp");
        published.RequestedClaims.Should().NotContainKey(Winner);
        published.RequestedClaims.Should().ContainKey("other-pod");
    }

    /// <summary>
    /// 🚨 The guard that keeps the fix from becoming a worse bug. A holder that is already BUILDING
    /// has no registration left — its own grant consumed it — so a bare "is it still queued?" test
    /// would read a running bake as a candidate that stood down and pull the lock out from under
    /// it. What distinguishes them is that the mirror already NAMES this holder; the refusal
    /// applies only to a winner that is neither holder nor candidate.
    /// </summary>
    [Fact]
    public void AHolderMidBake_IsNotMistakenForACandidateThatStoodDown()
    {
        var mirror = Mirror(new BuildState
        {
            ClaimedBy = Winner,
            ClaimedByIdentity = "silo-a",
            ClaimedAt = T0,
            HeartbeatAt = T0.AddMinutes(1),
            FrameworkVersion = "fp",
            Status = BuildStatus.Building,
        });

        BuildNodeType.ApplyGrant(mirror, Granted(), Options)
            .ContentAs<BuildState>(Options)!.ClaimedBy
            .Should().Be(Winner);
    }

    /// <summary>A redundant pass over a mirror that already reflects the grant writes nothing.</summary>
    [Fact]
    public void ARedundantPass_OverAMirrorThatAlreadyReflectsTheGrant_WritesNothing()
    {
        var mirror = Mirror(Granted());

        BuildNodeType.ApplyGrant(mirror, Granted(), Options).Should().BeSameAs(mirror);
    }

    // ── the consequence: a refused publication must not leave the lock behind ────────────────────

    /// <summary>
    /// The half that actually frees the build. The compare-and-set already took the lock before the
    /// mirror refused, so the arbiter — the lock's only writer — has to put it back. Without this
    /// the mirror says "unclaimed" while the durable witness every cluster decides on still names
    /// the withdrawn follower, and no later candidate is ever granted.
    /// </summary>
    [Fact]
    public async Task ARefusedPublication_ReleasesTheLockItJustTook()
    {
        var storage = new InMemoryStorageAdapter();
        var claimPath = BuildNodeType.ClaimPath(BuildNodeType.RootPath);
        await storage.Write(Lock(Granted()), Options).Await();

        // The publication came back WITHOUT our winner as holder: it was refused.
        var refused = Mirror(new BuildState { Status = BuildStatus.Ready });

        await BuildNodeType
            .HandBackAStoodDownGrant(storage, Options, null, Granted(), claimPath, refused)
            .Await();

        (await storage.Read(claimPath, Options).Take(1).Await()).Should().BeNull();
    }

    /// <summary>The negative control: a grant that WAS published keeps the lock it won.</summary>
    [Fact]
    public async Task APublishedGrant_KeepsItsLock()
    {
        var storage = new InMemoryStorageAdapter();
        var claimPath = BuildNodeType.ClaimPath(BuildNodeType.RootPath);
        await storage.Write(Lock(Granted()), Options).Await();

        await BuildNodeType
            .HandBackAStoodDownGrant(storage, Options, null, Granted(), claimPath, Mirror(Granted()))
            .Await();

        var held = await storage.Read(claimPath, Options).Take(1).Await();
        held.Should().NotBeNull();
        held!.ContentAs<BuildState>(Options)!.ClaimedBy.Should().Be(Winner);
    }

    /// <summary>
    /// The hand-back is conditional, like every other write to the lock: a pass whose grant was
    /// superseded removes NOTHING. Deleting unconditionally here would turn a lost race into a
    /// second builder — the exact storm the lock exists to prevent.
    /// </summary>
    [Fact]
    public async Task ARefusedPublication_LeavesALockThatHasAlreadyMovedOn_Alone()
    {
        var storage = new InMemoryStorageAdapter();
        var claimPath = BuildNodeType.ClaimPath(BuildNodeType.RootPath);
        await storage.Write(
            Lock(Granted() with { ClaimedBy = "someone-else", FrameworkVersion = "fp-next" }),
            Options).Await();

        await BuildNodeType.HandBackAStoodDownGrant(
                storage, Options, null, Granted(), claimPath,
                Mirror(new BuildState { Status = BuildStatus.Ready }))
            .Await();

        var held = await storage.Read(claimPath, Options).Take(1).Await();
        held.Should().NotBeNull();
        held!.ContentAs<BuildState>(Options)!.ClaimedBy.Should().Be("someone-else");
    }
}
