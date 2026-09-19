using System;
using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #1424's convergence window, asked of the path that actually takes the decision on a deployed
/// host (#4729).
///
/// <para><b>The defect.</b> <c>Arbitrate</c> holds a FRESH ROOT election for
/// <see cref="BuildNodeType.GrantSettleWindow"/> so every cluster's arbiter elects from the same
/// candidate set. It decided "is this the root?" by comparing the path of the node it was HANDED
/// against <c>Admin/Build</c> — and on the durable path it is never handed the Build node. #1424
/// moved the holder state onto the claim LOCK precisely so no whole-node flush could clobber it, so
/// <c>CommitGrant</c> hands the decision procedure <c>Admin/Build/_Claim</c>. That is not
/// <c>Admin/Build</c>, the guard was false on every pass, and the window was skipped on every host
/// with an <c>IStorageAdapter</c> — i.e. every real deployment, and the only topology where a
/// second cluster exists to converge with. Only <c>GrantOnMirror</c> — a host with no durable store
/// at all, hence one cluster and nothing to converge with — still paid it.</para>
///
/// <para><b>Why it sat.</b> The one test on the window
/// (<c>MeshWeaver.Hosting.Monolith.Test.BuildCoordinationTest.Arbitrate_FreshRootElection_WaitsOutTheSettleWindow</c>,
/// in MeshWeaver.Plugins) calls <c>Arbitrate</c> with a ROOT node it builds itself — a shape the
/// durable path cannot produce. It is a true statement about the mirror path and says nothing about
/// the other one, and nothing else asked. So every case here drives
/// <see cref="BuildNodeType.ArbitrateOnLock"/>, the expression <c>CommitGrant</c> runs, which builds
/// its decision input the way production does: the LOCK's row (or the row that would be INSERTED),
/// the lock's path, and the mirror's pending registrations.</para>
///
/// <para><b>Measured on the live protocol</b> before the fix:
/// <c>MeshWeaver.Hosting.Test.TheSecondHoldersCompletionStillPublishesItsGoTest</c> — TWO full root
/// elections on a monolith mesh that does register an <c>IStorageAdapter</c> — completed in
/// <b>0.878 s</b>, where a single window costs five.</para>
///
/// <para>Everything here is a decision over constructed state with no store, no cluster and no
/// wall-clock — the instant is a parameter — in the same family as
/// <see cref="BuildGrantPublicationTest"/>.</para>
/// </summary>
public class TheSettleWindowIsChargedOnTheDurablePathTest
{
    private static readonly JsonSerializerOptions Options = new();
    private static readonly DateTime T0 = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    private const string Candidate = "pod-7/2c9f";
    private const string ChunkPath = BuildNodeType.RootPath + "/Doc";

    /// <summary>A decision instant the oldest registration has NOT yet outlived.</summary>
    private static DateTime InsideTheWindow =>
        T0 + BuildNodeType.GrantSettleWindow - TimeSpan.FromSeconds(1);

    /// <summary>…and one it has.</summary>
    private static DateTime PastTheWindow =>
        T0 + BuildNodeType.GrantSettleWindow + TimeSpan.FromSeconds(1);

    /// <summary>One candidate, registered at <see cref="T0"/> — the simultaneous-boot shape.</summary>
    private static ImmutableDictionary<string, BuildClaimRequest> OneCandidate =>
        ImmutableDictionary<string, BuildClaimRequest>.Empty
            .Add(Candidate, new BuildClaimRequest("fp", T0));

    /// <summary>The lock row for a Build node, carrying <paramref name="state"/>.</summary>
    private static MeshNode LockRow(string buildPath, BuildState state) =>
        new(BuildNodeType.ClaimSegment, buildPath)
        {
            NodeType = BuildNodeType.NodeType,
            Name = "Build claim",
            Content = state,
        };

    /// <summary>The Build node itself, as the mirror path is handed it.</summary>
    private static MeshNode BuildNode(string path, BuildState state)
    {
        var split = path.LastIndexOf('/');
        return new MeshNode(path[(split + 1)..], path[..split])
        {
            NodeType = BuildNodeType.NodeType,
            Content = state,
        };
    }

    private static BuildState? Decided(MeshNode? node) => node?.ContentAs<BuildState>(Options);

    // ── THE CONTROL ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE CONTROL, on the shape a rollout actually produces: no lock row yet — the very first
    /// election, at <c>expectedVersion 0</c> — and one registration a second old. Nothing is
    /// granted, because a registration from another cluster may still be propagating.
    ///
    /// <para>It fails on a tree where the root is recognised by the path of the node handed in: the
    /// decision input carries <c>Admin/Build/_Claim</c>, the guard reads false, and the candidate is
    /// granted on the spot. Note what is NOT asserted — that <c>Arbitrate</c> holds a root node,
    /// which it always did; that is the statement that made the defect invisible.</para>
    /// </summary>
    [Fact]
    public void AFreshRootElection_IsHeld_WhenTheDecisionArrivesOnTheLock()
        => BuildNodeType.ArbitrateOnLock(
                held: null,
                claimPath: BuildNodeType.ClaimPath(BuildNodeType.RootPath),
                heldState: new BuildState(),
                pending: OneCandidate,
                Options,
                InsideTheWindow)
            .Should().BeNull(
                "a fresh root election inside the settle window grants nothing — and the durable "
                + "path is the ONLY one where a second cluster exists to converge with");

    /// <summary>
    /// The same, with a lock row that already exists and holds nobody — a build that ran before and
    /// was released. The row is present, so the pass would commit against its version rather than
    /// inserting; the decision is the same one.
    /// </summary>
    [Fact]
    public void AnExistingButUnheldLockRow_IsHeldTheSameWay()
        => BuildNodeType.ArbitrateOnLock(
                held: LockRow(BuildNodeType.RootPath, new BuildState()),
                claimPath: BuildNodeType.ClaimPath(BuildNodeType.RootPath),
                heldState: new BuildState(),
                pending: OneCandidate,
                Options,
                InsideTheWindow)
            .Should().BeNull("the window is a property of the election, not of the row's history");

    /// <summary>
    /// 🚨 The OTHER side of the change, and the reason this is a window rather than a refusal: past
    /// it the very same election grants. A guard that could not be satisfied would trade #1424's
    /// duplicate bake for a build that is never claimed at all.
    /// </summary>
    [Fact]
    public void PastTheWindow_TheSameRootElectionGrants()
    {
        var granted = Decided(BuildNodeType.ArbitrateOnLock(
            held: null,
            claimPath: BuildNodeType.ClaimPath(BuildNodeType.RootPath),
            heldState: new BuildState(),
            pending: OneCandidate,
            Options,
            PastTheWindow));

        granted.Should().NotBeNull();
        granted!.ClaimedBy.Should().Be(Candidate);
        granted.FrameworkVersion.Should().Be("fp");
        granted.Status.Should().Be(BuildStatus.Planning);
    }

    // ── the two paths are ONE notion, not two ───────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The property the fix is FOR: the mirror path and the durable path answer the same
    /// question the same way about the same election, at the same instant. They differ only in
    /// which row carries the decision state, and that is exactly what stopped being a difference —
    /// <c>IsRootElection</c> is asked, and it derives the lock's spelling from
    /// <c>RootPath</c> through the same <c>ClaimPath</c> that minted the lock.
    ///
    /// <para>Two arbiters disagreeing about whether to wait is not a cosmetic split: the whole
    /// point of the window is that every arbiter elects from the same candidate set, so one that
    /// does not wait elects from a smaller one and #1424's two-winner race is back.</para>
    /// </summary>
    [Fact]
    public void TheMirrorAndTheLock_DecideTheSameRootElection()
    {
        var mirror = BuildNode(
            BuildNodeType.RootPath, new BuildState { RequestedClaims = OneCandidate });
        var claimPath = BuildNodeType.ClaimPath(BuildNodeType.RootPath);

        BuildNodeType.ArbitrateOnMirror(mirror, Options, InsideTheWindow)
            .Should().BeSameAs(mirror, "the mirror path holds a fresh root election");
        BuildNodeType.ArbitrateOnLock(
                null, claimPath, new BuildState(), OneCandidate, Options, InsideTheWindow)
            .Should().BeNull("…and so, now, does the durable one");

        Decided(BuildNodeType.ArbitrateOnMirror(mirror, Options, PastTheWindow))!
            .ClaimedBy.Should().Be(Candidate);
        Decided(BuildNodeType.ArbitrateOnLock(
                null, claimPath, new BuildState(), OneCandidate, Options, PastTheWindow))!
            .ClaimedBy.Should().Be(Candidate, "and they converge on the same winner");
    }

    // ── what the window must NOT reach ──────────────────────────────────────────────────────────

    /// <summary>
    /// A CHUNK election is never held, on either path — the pre-existing rule, and the one the fix
    /// could most easily have broken by over-matching. The root election already decided THE
    /// builder, chunk claims arrive pre-arbitrated by it, and 37 chunks × 5 s of settle would put
    /// minutes of pure waiting into a bake measured at ~1m42s.
    /// </summary>
    [Fact]
    public void AChunkElection_IsNeverHeld_OnEitherPath()
    {
        var justRegistered = T0.AddMilliseconds(1);

        Decided(BuildNodeType.ArbitrateOnLock(
                null, BuildNodeType.ClaimPath(ChunkPath), new BuildState(), OneCandidate,
                Options, justRegistered))!
            .ClaimedBy.Should().Be(Candidate, "a chunk lock is not the root's");

        Decided(BuildNodeType.ArbitrateOnMirror(
                BuildNode(ChunkPath, new BuildState { RequestedClaims = OneCandidate }),
                Options, justRegistered))!
            .ClaimedBy.Should().Be(Candidate, "and neither is the chunk node");
    }

    /// <summary>
    /// A TAKEOVER is not a fresh election and is never held, however new the successor's
    /// registration — #1355's immediate-takeover property, which a window on the durable path must
    /// not quietly re-introduce a delay into. The successor queue accumulated while the holder was
    /// alive and is long converged; there is nothing left to wait for.
    /// </summary>
    [Fact]
    public void ATakeoverIsNeverHeld_HoweverFreshTheSuccessorsRegistration()
    {
        var abandoned = new BuildState
        {
            ClaimedBy = "the-holder-that-went-away",
            Status = BuildStatus.Building,
            ClaimedAt = T0 - TimeSpan.FromHours(1),
            HeartbeatAt = T0 - TimeSpan.FromHours(1),
        };

        Decided(BuildNodeType.ArbitrateOnLock(
                LockRow(BuildNodeType.RootPath, abandoned),
                BuildNodeType.ClaimPath(BuildNodeType.RootPath),
                abandoned,
                OneCandidate,
                Options,
                InsideTheWindow))!
            .ClaimedBy.Should().Be(
                Candidate, "the window gates FRESH elections; a stale claim is taken over at once");
    }

    // ── the predicate itself ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The root has exactly TWO spellings, because the decision state has two homes — and both are
    /// derived from <c>RootPath</c>, never written out a second time. Everything below the root is
    /// a chunk and is excluded, lock or node.
    /// </summary>
    [Theory]
    [InlineData("Admin/Build", true)]
    [InlineData("Admin/Build/_Claim", true)]
    [InlineData("admin/build/_claim", true)]
    [InlineData("Admin/Build/Doc", false)]
    [InlineData("Admin/Build/Doc/_Claim", false)]
    [InlineData("Admin/BuildSomethingElse", false)]
    [InlineData(null, false)]
    public void IsRootElection_RecognisesBothSpellingsOfTheRoot_AndNothingElse(
        string? path, bool expected)
        => BuildNodeType.IsRootElection(path).Should().Be(expected);

    /// <summary>
    /// 🚨 And the lock spelling is DERIVED, so the two cannot drift: whatever <c>ClaimPath</c>
    /// makes of the root is a root election by construction. A literal <c>"Admin/Build/_Claim"</c>
    /// here — or a <c>_Claim</c> suffix test — would be a second notion free to come apart from the
    /// first, which is the shape of the defect this file exists for.
    /// </summary>
    [Fact]
    public void TheRootsLockSpelling_IsDerivedFromTheRootItself()
        => BuildNodeType.IsRootElection(BuildNodeType.ClaimPath(BuildNodeType.RootPath))
            .Should().BeTrue();
}
