using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging;
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

    // ── the WAKE-UP: a decision the arbiter is never woken to take is not a fix ─────────────────

    /// <summary>
    /// 🚨 The half the release above cannot supply for itself. <c>ReleaseStoodDownClaim</c> is right
    /// about the wedged state, and <see cref="TheArbiterReleasesAStoodDownHolder_EvenWithNobodyQueued"/>
    /// passes whether or not a pass is ever RUN on it — a correct decision procedure and a trigger
    /// that never delivers it read exactly alike from a unit test. This is the trigger, and the
    /// state it has to see is the measured one: <c>RequestedClaims</c> EMPTY, because the grant
    /// consumed the follower's registration on its way in.
    ///
    /// <para>The arbiter's own-stream trigger used to ask only "is anyone queued?", so the
    /// stand-down mark landed on a node it filtered away. On a host WITH a durable store the
    /// withdraw's flush still published on <c>IStorageAdapter.Changes</c> and the pass ran at once;
    /// on one without — a monolith test, a dev box, the fail-open <c>GrantOnMirror</c> path, where
    /// the mirror IS the claim — the only wake-ups left were a re-check some EARLIER trigger
    /// happened to schedule and the <c>HeartbeatInterval</c> tick two minutes out.</para>
    /// </summary>
    [Fact]
    public void AStandDownMarkWakesTheArbiter_WithNobodyQueued()
    {
        BuildNodeType.ArbitrationTrigger(Wedged()).Should().NotBeNull(
            "the wedged state is exactly what an arbitration pass is owed for, and nothing else is "
            + "going to wake one — there is nobody queued left to register");
    }

    /// <summary>
    /// …and the mark belongs to the KEY, not merely to the filter. It arrives on a node whose other
    /// trigger fields did NOT move — the grant had already emptied <c>RequestedClaims</c> and set
    /// <c>ClaimedBy</c> — so a key that omitted it would be swallowed by <c>DistinctUntilChanged</c>:
    /// the filter would pass and still nothing would fire.
    /// </summary>
    [Fact]
    public void TheStandDownMark_ChangesTheTriggerKey()
    {
        var queuedBehindTheWedge = Wedged() with
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("next-image", new BuildClaimRequest("fp-next", T0)),
        };

        BuildNodeType.ArbitrationTrigger(queuedBehindTheWedge with { StoodDown = null })
            .Should().NotBe(
                BuildNodeType.ArbitrationTrigger(queuedBehindTheWedge),
                "the two differ only in the mark, and they need different arbitration");
    }

    /// <summary>
    /// The negative control, and what makes this a trigger rather than a poll: a node with nobody
    /// queued and nothing to release wakes NOTHING. A key that fired on every emission would be a
    /// poll wearing a filter's clothes.
    /// </summary>
    [Fact]
    public void AQuietBuildNode_WakesNoArbitrationPass()
    {
        BuildNodeType.ArbitrationTrigger(Granted()).Should().BeNull();
        BuildNodeType.ArbitrationTrigger(new BuildState()).Should().BeNull();
        BuildNodeType.ArbitrationTrigger(null).Should().BeNull();
    }

    /// <summary>
    /// A holder MID-BAKE carrying a mark wakes nothing either — the same line
    /// <see cref="ARunningBake_IsNeverReleasedByAStandDownMark"/> draws, stated at the trigger so
    /// the pass is not even run. Waking on a state the pass would decline is how a trigger turns
    /// into a retry loop.
    /// </summary>
    [Fact]
    public void ARunningBakeCarryingAMark_WakesNoArbitrationPass()
    {
        BuildNodeType.ArbitrationTrigger(Wedged() with { Status = BuildStatus.Building })
            .Should().BeNull();
    }

    /// <summary>
    /// The pre-existing trigger is untouched: a pending registration still wakes a pass, and the
    /// key still tracks BOTH the candidate set and the holder — a candidate joining the queue and
    /// the holder releasing are each a state the election decides differently.
    /// </summary>
    [Fact]
    public void APendingRegistration_StillWakesTheArbiter_AndTheHolderIsPartOfTheKey()
    {
        var queued = new BuildState
        {
            ClaimedBy = Winner,
            Status = BuildStatus.Building,
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add("next-image", new BuildClaimRequest("fp-next", T0)),
        };

        BuildNodeType.ArbitrationTrigger(queued).Should().NotBeNull();
        BuildNodeType.ArbitrationTrigger(queued with { ClaimedBy = null })
            .Should().NotBe(
                BuildNodeType.ArbitrationTrigger(queued),
                "the holder releasing is what makes the queued candidate grantable");
    }

    // ── a mirror this build cannot READ ─────────────────────────────────────────────────────────

    /// <summary>
    /// A mirror whose content is PRESENT and cannot be materialised as <c>BuildState</c> — the
    /// third of the three shapes <c>.ContentAs&lt;T&gt;</c> exists for, modelled as JSON whose
    /// <c>RequestedClaims</c> is a string where a map belongs.
    ///
    /// <para>This is a PURE decision test: nothing here goes through <c>MeshNodeStreamCache</c>, so
    /// no degradation is logged and the untyped-content shard gate is not involved. A test that
    /// drove a real mesh would need the other fixture shape — a value of a different REGISTERED
    /// type — for exactly that reason; see <c>Doc/Architecture/ReadingCiSignals</c>.</para>
    /// </summary>
    private static MeshNode UnreadableMirror() =>
        new("Build", "Admin")
        {
            NodeType = BuildNodeType.NodeType,
            Content = JsonSerializer.Deserialize<JsonElement>(
                """{"RequestedClaims":"written-by-a-shape-this-build-cannot-read"}"""),
        };

    /// <summary>
    /// 🚨 The fixture is only a fixture if it is genuinely unreadable. Asserted first, because every
    /// arm below is vacuous the moment this stops being true — a JSON shape the record happens to
    /// tolerate would make them all pass against the very defect they exist to refuse.
    /// </summary>
    [Fact]
    public void TheUnreadableFixture_IsActuallyUnreadable()
    {
        var mirror = UnreadableMirror();

        mirror.Content.Should().NotBeNull("PRESENT is half the point — absent content is a different fact");
        mirror.ContentAs<BuildState>(Options).Should().BeNull(
            "'present and this build cannot read it' is the state under test; if the record "
            + "tolerates this JSON the arms below assert nothing at all");
    }

    /// <summary>
    /// 🚨 <b>#3623 — the write that WAS reachable.</b> <c>ApplyGrant</c> read the mirror through
    /// <c>ContentAs&lt;BuildState&gt;(options) ?? new BuildState()</c>, so an unreadable mirror
    /// became an EMPTY one. With a holder in the grant the candidate guard then happened to refuse —
    /// the right outcome for the wrong reason, and silently. With NO holder every guard falls
    /// through and the method WRITES that empty record onto the Build node: registrations, holder,
    /// status and stand-down marks all replaced by defaults, in one merge patch, with nothing said.
    ///
    /// <para>This arm is the discriminating one: it fails against the pre-#3623 implementation and
    /// passes after it.</para>
    /// </summary>
    [Fact]
    public void AGrantWithNoHolder_NeverOverwritesAMirrorThisBuildCannotRead()
    {
        var mirror = UnreadableMirror();

        BuildNodeType.ApplyGrant(mirror, Granted() with { ClaimedBy = null }, Options)
            .Should().BeSameAs(mirror,
                "a failed READ must never become a written DEFAULT — the same node returned is the "
                + "only outcome that leaves the record intact");
    }

    /// <summary>
    /// The ordinary holder-carrying grant is refused too, and now for the stated reason rather than
    /// by accident of an empty candidate map. The refusal is what
    /// <c>HandBackAStoodDownGrant</c> reads, so the lock the pass took is released rather than
    /// stranded — which is why this path refuses instead of throwing.
    /// </summary>
    [Fact]
    public void AGrantIsNotPublished_OverAMirrorThisBuildCannotRead()
    {
        var mirror = UnreadableMirror();

        BuildNodeType.ApplyGrant(mirror, Granted(), Options).Should().BeSameAs(mirror);
    }

    /// <summary>
    /// 🚨 And it SAYS so. A refusal nobody can see is the shape that made #3623 invisible in the
    /// first place: the write simply did not happen, nothing was logged, and the loss surfaced
    /// later as a field that "went missing". The record names the node and the holder it withheld.
    /// </summary>
    [Fact]
    public void TheRefusalIsRecorded_NamingTheNodeAndTheHolder()
    {
        var logger = new RecordingLogger();

        BuildNodeType.ApplyGrant(UnreadableMirror(), Granted(), Options, logger);

        var record = Assert.Single(logger.Records);
        record.Level.Should().Be(LogLevel.Error);
        record.Message.Should().Contain("REFUSING to publish the grant");
        record.Message.Should().Contain("Admin/Build");
        record.Message.Should().Contain(Winner);
    }

    /// <summary>
    /// 🚨 Non-vacuity for all four arms above. A readable mirror still publishes — otherwise
    /// "the grant was refused" would be satisfied by an <c>ApplyGrant</c> that never grants
    /// anything, and the whole arbitration would be dead with every test green.
    /// </summary>
    [Fact]
    public void AReadableMirror_StillPublishesAndLogsNothing()
    {
        var logger = new RecordingLogger();
        var mirror = Mirror(new BuildState
        {
            RequestedClaims = ImmutableDictionary<string, BuildClaimRequest>.Empty
                .Add(Winner, new BuildClaimRequest("fp", T0)),
        });

        var published = BuildNodeType.ApplyGrant(mirror, Granted(), Options, logger)
            .ContentAs<BuildState>(Options)!;

        published.ClaimedBy.Should().Be(Winner);
        logger.Records.Should().BeEmpty("a healthy publication is not a fault");
    }

    // ── the DURABLE half: the claim lock the compare-and-set commits against ────────────────────

    /// <summary>
    /// 🚨 #3623 on the row that matters most. <c>CommitGrant</c> read the LOCK through
    /// <c>held?.ContentAs&lt;BuildState&gt;(options) ?? new BuildState()</c>, which answered the same
    /// empty state for "there is no lock row yet" — correct, that is the INSERT case at
    /// <c>expectedVersion 0</c> — and for "the row is there and this build cannot read it". On the
    /// second reading the pass decides in a world where nobody holds the build, and then COMMITS
    /// it: the compare-and-set diffs against the REAL row's version, so it succeeds. The live
    /// holder is evicted from the one witness every cluster's arbiter reads.
    /// </summary>
    [Fact]
    public void AnUnreadableLockRow_RefusesTheWholePass()
    {
        var logger = new RecordingLogger();
        var lockRow = new MeshNode("_Claim", "Admin/Build")
        {
            NodeType = BuildNodeType.NodeType,
            Content = JsonSerializer.Deserialize<JsonElement>(
                """{"RequestedClaims":"written-by-a-shape-this-build-cannot-read"}"""),
        };
        lockRow.ContentAs<BuildState>(Options).Should().BeNull("the fixture must actually be unreadable");

        BuildNodeType.ReadLockStateOrRefuse(lockRow, Options, "Admin/Build/_Claim", logger)
            .Should().BeNull("refusing the pass is the only outcome that leaves the lock row intact");

        var record = Assert.Single(logger.Records);
        record.Level.Should().Be(LogLevel.Error);
        record.Message.Should().Contain("REFUSING to arbitrate");
        record.Message.Should().Contain("Admin/Build/_Claim");
    }

    /// <summary>
    /// 🚨 Non-vacuity, both halves. An ABSENT row is the insert case and must still yield a fresh
    /// state — refusing it would mean no build is ever claimed on a cluster that has not run one —
    /// and a READABLE row must come back as itself, holder and all.
    /// </summary>
    [Fact]
    public void AnAbsentLockRowInserts_AndAReadableOneIsReturnedUnchanged()
    {
        var logger = new RecordingLogger();

        BuildNodeType.ReadLockStateOrRefuse(null, Options, "Admin/Build/_Claim", logger)
            .Should().NotBeNull("no lock row yet IS the insert case — expectedVersion 0");

        var readable = BuildNodeType.ReadLockStateOrRefuse(
            Lock(Granted()), Options, "Admin/Build/_Claim", logger);

        readable.Should().NotBeNull();
        readable!.ClaimedBy.Should().Be(Winner);
        logger.Records.Should().BeEmpty("neither of these is a fault");
    }

    private sealed record LogRecord(LogLevel Level, string Message);

    /// <summary>Captures level and formatted message — enough to assert that the refusal is
    /// visible and names what it withheld.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<LogRecord> records = [];

        public IReadOnlyList<LogRecord> Records => records;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => records.Add(new LogRecord(logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
