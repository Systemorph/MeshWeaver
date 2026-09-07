using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // ── the OTHER order: the stand-down decided BEFORE the grant, applied AFTER it (#1193) ──────
    //
    // Everything above closes "the grant is published after the candidate stood down". This is its
    // mirror image, and #3131 could not close it because the publication was LEGITIMATE at the
    // moment it happened — the candidate was still registered. What arrives late is the STAND-DOWN,
    // and it arrives as a merge patch computed on a mirror that predates the grant.
    //
    // Measured on MeshWeaver.Plugins#1193 (Follower_StandsDown_SoTheNextBuildCanStillBeClaimed):
    //   FAIL: ClaimedBy=<the follower>  Status=Planning  RequestedClaims=[]  Ready=[fp-stand-down]
    //   PASS: ClaimedBy=<null>          Status=Planning  RequestedClaims=[]  Ready=[fp-stand-down]
    // — one emission in fifteen seconds, i.e. terminal rather than slow, and the whole difference
    // is a holder that was never cleared. It recurred on a core that CONTAINS #3131.

    /// <summary>The mirror a follower reads before the arbiter's grant reaches it: still queued, nobody holding.</summary>
    private static BuildState StillACandidate() => new()
    {
        RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
            .Add(Winner, new BuildClaimRequest("fp", T0)),
        Ready = ImmutableDictionary<string, BuildGo>.Empty.Add("fp", new BuildGo("fp", T0)),
    };

    /// <summary>
    /// 🚨 THE MECHANISM, driven through the REAL diff a cross-hub write ships. A candidate does not
    /// own the Build node, so <c>WithdrawBuildClaim</c>'s lambda runs on ITS OWN MIRROR and travels
    /// as an RFC 7396 merge patch of the fields it changed
    /// (<c>MeshNodeStreamHandle.ComputeMergePatchDiff</c>). On a mirror the grant has not reached,
    /// <c>grantedNotStarted</c> is false — so the patch carries the registration removal and NO
    /// <c>claimedBy</c> at all. By RFC 7396 an absent member leaves the owner's value untouched, so
    /// a grant committed in between survives the stand-down.
    ///
    /// <para>This is asserted on the PATCH rather than on an applied result on purpose: the patch is
    /// what actually travels, and applying it here would need a stand-in applier that could drift
    /// from the owner's.</para>
    /// </summary>
    [Fact]
    public void TheStandDownPatch_CannotCarryAClaimTheMirrorHasNotSeenYet()
    {
        var mirror = Mirror(StillACandidate());
        var stoodDown = BuildNodeType.StandDown(mirror, Winner, Options, T0);

        var patch = MeshNodeStreamHandle.ComputeMergePatchDiff(
            JsonSerializer.SerializeToNode(mirror, Options)!.AsObject(),
            JsonSerializer.SerializeToNode(stoodDown, Options)!.AsObject());

        var content = Member(patch, nameof(MeshNode.Content))!.AsObject();
        var registrations = Member(content, nameof(BuildState.RequestedClaims))!.AsObject();

        registrations.ContainsKey(Winner).Should().BeTrue();
        registrations[Winner].Should().BeNull("RFC 7396 spells a removal as an explicit null");
        Member(content, nameof(BuildState.ClaimedBy)).Should().BeNull(
            "the member is ABSENT from the patch — the lambda left it unchanged because THIS "
            + "mirror shows nobody holding — which is exactly why a grant that landed on the "
            + "owner in between is not undone by this patch");
        content.Any(kv => string.Equals(kv.Key, nameof(BuildState.StoodDown),
                StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue(
                "the fact is written unconditionally, so it is the half that is always there to be read");
    }

    /// <summary>
    /// A patch member by name, case-insensitively — the assertion is about which members the diff
    /// CONTAINS, and that must not turn red if a naming policy changes underneath it.
    /// </summary>
    private static JsonNode? Member(JsonObject o, string name) =>
        o.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// The wedge itself, constructed: the state the measured failure left behind. A holder that
    /// stood down, at <see cref="BuildStatus.Planning"/>, with nobody queued.
    /// </summary>
    private static BuildState Wedged() => new()
    {
        ClaimedBy = Winner,
        ClaimedByIdentity = "silo-7",
        ClaimedAt = T0,
        HeartbeatAt = T0,
        FrameworkVersion = "fp",
        Status = BuildStatus.Planning,
        StoodDown = ImmutableDictionary<string, DateTime>.Empty.Add(Winner, T0),
        Ready = ImmutableDictionary<string, BuildGo>.Empty.Add("fp", new BuildGo("fp", T0)),
    };

    /// <summary>
    /// 🚨 THE CONTROL, and the reason this needed a fix at all: the election ALONE cannot free the
    /// wedge, however long anyone waits. <c>Arbitrate</c> defends a live holder by design (#1355 —
    /// a stopped heartbeat on a running process means busy, not dead), and the follower's process
    /// really is alive; there is nobody queued to elect either. So the state above is terminal
    /// under the pre-fix pass, which is what "one emission in fifteen seconds" measured.
    /// </summary>
    [Fact]
    public void TheElectionAlone_NeverFreesAStoodDownHolder()
    {
        var wedged = Mirror(Wedged());

        BuildNodeType.Arbitrate(wedged, Options, T0.AddSeconds(1)).Should().BeSameAs(wedged);
    }

    /// <summary>
    /// …and the fix: the arbiter — the one writer that owns this node, so its lambda is serialised
    /// against fresh state — reads the recorded stand-down and releases the claim, in the same pass
    /// that elects whoever is next.
    /// </summary>
    [Fact]
    public void TheArbiterReleasesAStoodDownHolder_AndElectsTheNextCandidateInTheSamePass()
    {
        var wedged = Mirror(Wedged() with
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("next-image", new BuildClaimRequest("fp-next", T0)),
        });

        var after = BuildNodeType
            .ArbitrateOnMirror(wedged, Options, T0.Add(BuildNodeType.GrantSettleWindow).AddSeconds(1))
            .ContentAs<BuildState>(Options)!;

        after.ClaimedBy.Should().Be("next-image");
        after.FrameworkVersion.Should().Be("fp-next");
        after.StoodDown.Should().BeNull("the mark is CONSUMED by the release, never accumulated");
        after.Ready.Should().ContainKey("fp", "releasing a claim never touches the GO history");
    }

    /// <summary>
    /// With nobody queued the release still happens — the build has to be free BEFORE the next
    /// candidate registers, or that candidate is queued behind a holder that will never move.
    /// </summary>
    [Fact]
    public void TheArbiterReleasesAStoodDownHolder_EvenWithNobodyQueued()
    {
        var after = BuildNodeType
            .ArbitrateOnMirror(Mirror(Wedged()), Options, T0.AddSeconds(1))
            .ContentAs<BuildState>(Options)!;

        after.ClaimedBy.Should().BeNull();
        after.ClaimedByIdentity.Should().BeNull();
        after.ClaimedAt.Should().BeNull();
        after.HeartbeatAt.Should().BeNull();
        after.StoodDown.Should().BeNull();
    }

    /// <summary>
    /// 🚨 The guard that keeps THIS fix from becoming a worse bug, in the same family as
    /// <c>ARunningBakeIsNotMistakenForAStandDown</c> above. A holder that is BUILDING is mid-bake.
    /// A stand-down mark carrying its id — from an earlier life of the same process, or a
    /// stand-down that raced its own grant into a started build — must never pull the claim out
    /// from under a running compile: two builders is the storm the claim exists to prevent.
    /// </summary>
    [Fact]
    public void ARunningBake_IsNeverReleasedByAStandDownMark()
    {
        var building = Mirror(Wedged() with { Status = BuildStatus.Building });

        var after = BuildNodeType.ReleaseStoodDownClaim(building, Options, T0.AddSeconds(1))
            .ContentAs<BuildState>(Options)!;

        after.ClaimedBy.Should().Be(Winner);
        after.Status.Should().Be(BuildStatus.Building);
    }

    /// <summary>
    /// A mark for a candidate that was never granted anything is aged out on the claim's own
    /// budget, so the durable row cannot accumulate one per process that ever followed a build.
    /// </summary>
    [Fact]
    public void AStandDownMarkThatNothingConsumed_IsPrunedOnTheClaimsOwnBudget()
    {
        var stale = Mirror(new BuildState
        {
            StoodDown = ImmutableDictionary<string, DateTime>.Empty
                .Add("long-gone", T0)
                .Add("just-now", T0.Add(BuildNodeType.ClaimStaleAfter)),
        });

        var after = BuildNodeType
            .ReleaseStoodDownClaim(stale, Options, T0.Add(BuildNodeType.ClaimStaleAfter).AddSeconds(1))
            .ContentAs<BuildState>(Options)!;

        after.StoodDown.Should().ContainKey("just-now").And.NotContainKey("long-gone");
    }

    /// <summary>
    /// The negative control for the pruning pass: a node with no marks is returned UNCHANGED, so
    /// running the release on every arbitration tick costs a quiet build node nothing —
    /// <c>Update</c> no-ops on an unchanged node.
    /// </summary>
    [Fact]
    public void AQuietBuildNode_IsReturnedUnchangedByTheRelease()
    {
        var quiet = Mirror(Granted());

        BuildNodeType.ReleaseStoodDownClaim(quiet, Options, T0.AddDays(1)).Should().BeSameAs(quiet);
    }

    /// <summary>
    /// A candidate that REGISTERS again is not stood down, so its own mark goes with the
    /// registration — otherwise a holder id reused for the next build would be refused a grant by a
    /// mark left from the previous one.
    /// </summary>
    [Fact]
    public void AGrantIsRefused_ToAWinnerThatHasStoodDown()
    {
        // Still listed as a candidate — the half ApplyGrant's original guard reads — but the
        // stand-down fact is already there, and it is the one that decides.
        var mirror = Mirror(new BuildState
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add(Winner, new BuildClaimRequest("fp", T0)),
            StoodDown = ImmutableDictionary<string, DateTime>.Empty.Add(Winner, T0),
        });

        BuildNodeType.ApplyGrant(mirror, Granted(), Options).Should().BeSameAs(mirror);
    }
}
