using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A cross-hub write's base must be FRESH because the mirror PROVED it, never because the mirror
/// was thrown away — issue #1174.
///
/// <para><b>What was wrong.</b> A writer's base was kept current by destroying the mirror:
/// <c>Workspace.EvictForPath</c> fired on every change-feed <c>Created</c>/<c>Updated</c>/<c>Deleted</c>
/// and evicted unconditionally — including the mirror the writer itself had just used, on the
/// writer's own commit. The next write therefore resolved a brand-new mirror (a
/// <c>SubscribeRequest</c>, an initial-state round trip, a fresh pair of <c>sync/</c> hubs) and had
/// to get it hydrated inside <see cref="MeshNodeStreamHandle.BaseStateWaitBound"/> — 30 s — while
/// the one it replaced was being torn down on the same owner address. A node at version 6498 had
/// paid that thousands of times; a cold path pays it once. That is the hot-path concentration of
/// #1174's 414 production timeouts, which the issue thread carried for five weeks as an
/// unexplained correlation.</para>
///
/// <para><b>What is right.</b> <c>MeshChangeEvent</c> already carries the committed
/// <c>Version</c>. The workspace records it against the owner and KEEPS the mirror; this seam makes
/// the write wait for the mirror to REACH that version before diffing against it. The mirror is
/// live, so the owner's own fan-out delivers that version — no hydration, no round trip, and the
/// 30 s bound is never approached on a warm path.</para>
///
/// <para>🚨 <b>And it is NOT the liveness gate.</b> Skipping the eviction because the mirror still
/// LOOKS healthy was implemented, measured and reverted: liveness is not freshness, and it handed
/// writers a healthy-but-behind base — 2000 messages appended, 1975 recorded, one whole 25-message
/// batch lost in <c>StaticRepoImportActivityWriteCountTest</c>. Everything below is the opposite
/// test: the mirror must PROVE it carries the announced commit, and a mirror that cannot is
/// evicted by the writer itself.</para>
///
/// <para>Deterministic by construction: a <see cref="TestScheduler"/> supplies virtual time, so the
/// assertions are about ORDER and CAUSE, never wall-clock duration. No mesh, no cluster, no
/// sleep.</para>
/// </summary>
public class HotPathWriteBaseIsVersionAwareTest
{
    private const string Path = "rbuergi/_UserActivity/rbuergi";

    // 🚨 Every duration below is VIRTUAL time on a TestScheduler, placed RELATIVE to the two
    // production bounds the seam composes — never a literal, so a change to either bound moves the
    // scenarios with it instead of silently turning "inside the bound" into "outside it".
    private static TimeSpan CatchUpBound => MeshNodeStreamHandle.ConflictRebaseBound;
    private static TimeSpan BaseBound => MeshNodeStreamHandle.BaseStateWaitBound;

    /// <summary>A fan-out arrival comfortably INSIDE the catch-up bound.</summary>
    private static TimeSpan AMomentLater => CatchUpBound / 5;

    /// <summary>Long enough for every timer in the composition to have fired.</summary>
    private static TimeSpan PastEveryBound => BaseBound + CatchUpBound + BaseBound;

    /// <summary>What a subscriber actually observed — all three Rx terminations, separately.</summary>
    private sealed record Observed(List<MeshNode> Values, Exception? Error, bool Completed);

    private static MeshNode At(long version) => new(Path) { Version = version };

    /// <summary>
    /// The production composition of a FIRST attempt's base read, with the announced floor in
    /// place: <c>RebaseSource(BaseStateSource(mirror), refusedBaseVersion: 0, …,
    /// announcedVersion: v, …)</c> — exactly what <c>UpdateRemote</c> builds.
    /// </summary>
    private static Observed Run(
        Func<TestScheduler, IObservable<ChangeItem<MeshNode>>> mirror,
        long announcedVersion,
        TimeSpan runFor,
        List<long>? evictedAt = null)
    {
        var scheduler = new TestScheduler();
        var values = new List<MeshNode>();
        Exception? error = null;
        var completed = false;

        using var subscription = MeshNodeStreamHandle
            .RebaseSource(
                MeshNodeStreamHandle.BaseStateSource(mirror(scheduler), scheduler),
                refusedBaseVersion: 0,
                onStaleMirror: _ => throw new InvalidOperationException(
                    "the CONFLICT reporter must not fire on a first attempt — the floor here is the "
                    + "announced version, and the two reasons a base is too old must stay separable"),
                scheduler: scheduler,
                announcedVersion: announcedVersion,
                onMirrorBehindAnnounced: v => evictedAt?.Add(v))
            .Subscribe(values.Add, ex => error = ex, () => completed = true);

        scheduler.AdvanceBy(runFor.Ticks);
        return new Observed(values, error, completed);
    }

    private static ChangeItem<MeshNode> Carrying(long version) =>
        new(At(version), StreamId: Path, Version: version);

    /// <summary>
    /// 🚨 THE REGRESSION. The hot-path shape: the mirror is LIVE and replays the state it had
    /// BEFORE the commit the change feed just announced, then the owner's fan-out brings the
    /// announced version a moment later. The write must diff against the announced one.
    ///
    /// <para>Before #1174 this base read took the first emission it saw, whatever version it
    /// carried — which is precisely why the mirror had to be destroyed on every commit to keep it
    /// honest. With the floor in place the stale replay is skipped and the fan-out is waited for,
    /// so the mirror can stay.</para>
    /// </summary>
    [Fact]
    public void ALiveMirrorBehindTheAnnouncedCommit_WaitsForIt_InsteadOfDiffingTheStaleReplay()
    {
        // v6497 replays at once (the mirror's current state), v6498 — the announced commit —
        // arrives through the owner's fan-out a moment later, well inside the catch-up bound.
        var observed = Run(
            s => Observable.Return(Carrying(6497))
                .Concat(Observable.Timer(AMomentLater, s).Select(_ => Carrying(6498)))
                .Concat(Observable.Never<ChangeItem<MeshNode>>()),
            announcedVersion: 6498,
            runFor: PastEveryBound);

        observed.Error.Should().BeNull(
            "the announced version arrived through the live mirror's own subscription — no fresh "
            + "hydration was needed, and nothing is near the base-state bound");
        observed.Values.Should().ContainSingle().Which.Version.Should().Be(6498,
            "the base must be the version the change feed announced, not the stale replay that "
            + "preceded it. Taking the first emission is what made the unconditional eviction "
            + "load-bearing — and that eviction is #1174");
    }

    /// <summary>
    /// The warm case, and the one that matters for cost: a mirror that ALREADY carries the
    /// announced version hands it straight through. No filter delay, no second subscription, no
    /// round trip — which is the whole point of keeping the mirror instead of rebuilding it.
    /// </summary>
    [Fact]
    public void AMirrorAlreadyAtTheAnnouncedVersion_PassesItThroughImmediately()
    {
        var observed = Run(
            _ => Observable.Return(Carrying(6498)).Concat(Observable.Never<ChangeItem<MeshNode>>()),
            announcedVersion: 6498,
            runFor: AMomentLater);

        observed.Error.Should().BeNull();
        observed.Values.Should().ContainSingle().Which.Version.Should().Be(6498,
            "a mirror that has already caught up is the freshest state there is — the announced "
            + "floor is `>= announced`, never `> announced`, so the commit itself qualifies");
    }

    /// <summary>
    /// 🚨 NEVER PARKS, AND NEVER FAULTS ON THE 30 s BOUND. A mirror that never reaches the
    /// announced version is provably behind, so the write EVICTS it — the pre-#1174 behaviour,
    /// paid once on proof instead of once per commit — and proceeds on the state it has rather
    /// than running out <see cref="MeshNodeStreamHandle.BaseStateWaitBound"/>.
    ///
    /// <para>This is the arm that makes the change strictly no-worse than what it replaces: where
    /// the version gate cannot be satisfied it degrades to exactly the old shape, and the next
    /// acquire — this write's Conflict re-attempt, or the next write on the path — hydrates a
    /// fresh mirror.</para>
    /// </summary>
    [Fact]
    public void AMirrorThatNeverReachesTheAnnouncedVersion_EvictsAndProceeds()
    {
        var evictedAt = new List<long>();

        var observed = Run(
            _ => Observable.Return(Carrying(6497)).Concat(Observable.Never<ChangeItem<MeshNode>>()),
            announcedVersion: 6498,
            runFor: PastEveryBound,
            evictedAt: evictedAt);

        observed.Error.Should().BeNull(
            "a write whose mirror will not catch up must still be written, not failed — and above "
            + "all it must not sit out the BaseStateWaitBound, which is the fault #1174 is");
        observed.Values.Should().ContainSingle().Which.Version.Should().Be(6497,
            "the fallback is the state the mirror has — byte for byte the behaviour before the "
            + "floor existed. A base the owner has moved past is refused as a Conflict and the "
            + "re-attempt rebases, which is a retry, never a loss");
        evictedAt.Should().ContainSingle(
            "🚨 EXACTLY ONCE, naming the version it could not reach. The eviction is what restores "
            + "the old freshness barrier for the one mirror that needs it — and a fallback that "
            + "did NOT evict would leave a permanently-behind mirror in the cache, which is the "
            + "liveness-gate arm that lost writes")
            .Which.Should().Be(6498L, "…and it names the version the mirror could not reach");
    }

    /// <summary>
    /// 🚨 THE CONTROL, and it is not optional. With NO announced version — a path this workspace
    /// has heard no commit for, i.e. a cold write and every write on a build where the feed never
    /// reaches the workspace — the read must be byte-for-byte what it was before #1174: the first
    /// emission, no filter, no timer, no eviction. Without this arm the three above would also
    /// pass on a build that gated EVERY write, which would be a far worse bug than the one this
    /// closes.
    /// </summary>
    [Fact]
    public void WithNoAnnouncedVersion_TheReadIsUnchanged()
    {
        var evictedAt = new List<long>();

        var observed = Run(
            _ => Observable.Return(Carrying(3)).Concat(Observable.Never<ChangeItem<MeshNode>>()),
            announcedVersion: 0,
            runFor: PastEveryBound,
            evictedAt: evictedAt);

        observed.Error.Should().BeNull();
        observed.Values.Should().ContainSingle().Which.Version.Should().Be(3,
            "no announced version means no claim about freshness, so the ordinary write path gains "
            + "no filter and no second subscription");
        evictedAt.Should().BeEmpty("nothing was proven behind, so nothing may be evicted");
    }

    /// <summary>
    /// 🚨 …and the two reasons a base can be too old stay SEPARABLE. A CONFLICT re-attempt whose
    /// mirror is already past the announced version is stale for the OTHER reason — the owner
    /// refused it — so the conflict reporter fires and the mirror is NOT evicted. Evicting there
    /// would be exactly the speculative churn this issue is about.
    /// </summary>
    [Fact]
    public void AConflictReattemptPastTheAnnouncedVersion_ReportsTheConflict_AndEvictsNothing()
    {
        var scheduler = new TestScheduler();
        var staleReported = new List<long>();
        var evictedAt = new List<long>();
        var values = new List<MeshNode>();

        // The owner refused v6500; the mirror sits at v6499 and never advances. The announced
        // floor (6498) is already satisfied, so only the conflict reason is binding.
        using var subscription = MeshNodeStreamHandle
            .RebaseSource(
                MeshNodeStreamHandle.BaseStateSource(
                    Observable.Return(Carrying(6499)).Concat(Observable.Never<ChangeItem<MeshNode>>()),
                    scheduler),
                refusedBaseVersion: 6500,
                onStaleMirror: staleReported.Add,
                scheduler: scheduler,
                announcedVersion: 6498,
                onMirrorBehindAnnounced: evictedAt.Add)
            .Subscribe(values.Add, _ => { });

        scheduler.AdvanceBy(PastEveryBound.Ticks);

        staleReported.Should().ContainSingle(
                "the owner refused this write at 6500 and the mirror never moved past it — that is "
                + "the #1910 diagnostic and it must keep firing")
            .Which.Should().Be(6500L, "…named by the version the owner refused");
        evictedAt.Should().BeEmpty(
            "the mirror is at 6499, i.e. PAST the announced 6498, so it is not behind the change "
            + "feed at all — evicting it would be the per-write churn #1174 removes");
        values.Should().ContainSingle().Which.Version.Should().Be(6499,
            "the re-attempt still proceeds on what the mirror has, exactly as before");
    }

    /// <summary>
    /// 🚨 A mirror that has carried NOTHING yet is HYDRATING, not behind — and must not be evicted
    /// for being slow. The announced floor is in force (the change feed spoke while the mirror was
    /// being built), the owner is slow, and the first snapshot lands after the catch-up bound but
    /// well inside the base-state bound.
    ///
    /// <para>Evicting here would throw away the one subscription that is about to deliver: the
    /// write in flight still waits for it (it holds the lease), and the NEXT write would hydrate
    /// yet another fresh mirror — the per-write churn this floor exists to remove, re-entered at
    /// exactly the moment the owner is slowest, which is #1174's production condition. So the
    /// fallback evicts only on PROOF: a node seen below the floor.</para>
    ///
    /// <para>A HOT source, because a live mirror is one: the fallback re-subscribes it, and a cold
    /// source would restart its own clock and model a mirror that does not exist.</para>
    /// </summary>
    [Fact]
    public void AMirrorStillHydrating_IsWaitedFor_AndNotEvictedForBeingSlow()
    {
        var scheduler = new TestScheduler();
        var evictedAt = new List<long>();
        var values = new List<MeshNode>();
        Exception? error = null;

        // First (and only) snapshot at twice the catch-up bound: past the floor's wait, inside the
        // base-state bound.
        var hydration = scheduler.CreateHotObservable(
            ReactiveTest.OnNext((CatchUpBound + CatchUpBound).Ticks, Carrying(6498)));

        using var subscription = MeshNodeStreamHandle
            .RebaseSource(
                MeshNodeStreamHandle.BaseStateSource(hydration, scheduler),
                refusedBaseVersion: 0,
                onStaleMirror: _ => throw new InvalidOperationException(
                    "the CONFLICT reporter must not fire on a first attempt"),
                scheduler: scheduler,
                announcedVersion: 6498,
                onMirrorBehindAnnounced: evictedAt.Add)
            .Subscribe(values.Add, ex => error = ex);

        scheduler.AdvanceBy(PastEveryBound.Ticks);

        error.Should().BeNull(
            "the snapshot arrived inside the base-state bound, so the write has a base");
        values.Should().ContainSingle().Which.Version.Should().Be(6498,
            "the fallback keeps waiting on the SAME mirror and takes its first snapshot");
        evictedAt.Should().BeEmpty(
            "🚨 nothing below the floor was ever seen — the mirror was hydrating, not behind. "
            + "Evicting it is speculation, and speculative eviction is the churn #1174 removes");
    }
}
