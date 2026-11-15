using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Systemorph/MeshWeaver#7459 — WHEN the prebuilt-bundle retention lease is released, and what a release
/// fault may do to the pass's outcome. Driven synchronously through subjects: every step is a call the
/// test makes, so the order is pinned and nothing waits.
///
/// <para>The defects: the lease was released on EVERY result of the pass (a pass that emitted and kept
/// working released under itself, so another replica could sweep beside it), an EMPTY pass never
/// released at all (it lapsed), and a release that faulted after a successful sweep replaced the sweep's
/// result with its own fault — the hosted service then recorded a pass fault and logged "nothing was
/// collected" over deletions that had completed.</para>
/// </summary>
public class PrebuiltBundleRetentionLeaseTerminalTest
{
    private sealed class CountingRelease
    {
        private readonly Exception? fault;
        public CountingRelease(Exception? fault = null) => this.fault = fault;
        public int Subscriptions { get; private set; }

        public IObservable<Unit> Release => Observable.Defer(() =>
        {
            Subscriptions++;
            return fault is null ? Observable.Return(Unit.Default) : Observable.Throw<Unit>(fault);
        });
    }

    private sealed class Outcome<T>
    {
        public ImmutableList<T> Values { get; private set; } = ImmutableList<T>.Empty;
        public Exception? Error { get; private set; }
        public bool Completed { get; private set; }
        public void Observe(IObservable<T> source)
            => source.Subscribe(v => Values = Values.Add(v), e => Error = e, () => Completed = true);
    }

    [Fact]
    public void APassThatEmitsAndKeepsWorking_HoldsTheLeaseUntilItCompletes()
    {
        var pass = new Subject<int>();
        var release = new CountingRelease();
        var faults = ImmutableList<Exception>.Empty;
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(pass, release.Release, ex => faults = faults.Add(ex)));

        pass.OnNext(1);
        release.Subscriptions.Should().Be(0,
            "a pass that has emitted and is still working still sweeps — releasing now lets another replica sweep beside it");
        pass.OnNext(2);
        release.Subscriptions.Should().Be(0);

        pass.OnCompleted();
        release.Subscriptions.Should().Be(1, "the lease is released ONCE, at the pass's successful terminal");
        outcome.Values.Should().Equal(1, 2);
        outcome.Completed.Should().BeTrue();
        faults.Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyPass_ReleasesTheLease()
    {
        var release = new CountingRelease();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(Observable.Empty<int>(), release.Release, _ => { }));

        release.Subscriptions.Should().Be(1, "a pass that completed with no result has ended — it must not leave the lease to lapse");
        outcome.Completed.Should().BeTrue();
        outcome.Values.Should().BeEmpty();
    }

    [Fact]
    public void AConsumerThatStopsAtTheFirstResult_StillFindsTheLeaseReleased()
    {
        var release = new CountingRelease();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(Observable.Return(7), release.Release, _ => { }).Take(1));

        outcome.Values.Should().Equal(7);
        release.Subscriptions.Should().Be(1, "results are delivered only after the release was attempted");
    }

    [Fact]
    public void AReleaseThatFaults_AfterASuccessfulSweep_LeavesTheSweepRecorded_AndReportsTheReleaseOnItsOwn()
    {
        var releaseFault = new InvalidOperationException("release failed");
        var release = new CountingRelease(releaseFault);
        var releaseFaults = ImmutableList<Exception>.Empty;
        var records = ImmutableList<string>.Empty;
        var passFaults = ImmutableList<Exception>.Empty;
        var cancellations = 0;
        var ended = false;

        PrebuiltBundleRetentionHostedService.GuardedPass(
                PrebuiltBundleRetentionLease.ReleasedAtTerminal(
                    Observable.Return("collected 3"), release.Release, ex => releaseFaults = releaseFaults.Add(ex)),
                result => records = records.Add(result),
                ex => passFaults = passFaults.Add(ex),
                _ => cancellations++)
            .Subscribe(_ => { }, _ => { }, () => ended = true);

        ended.Should().BeTrue();
        records.Should().Equal(new[] { "collected 3" }, "the deletions completed — the sweep's result must reach the status");
        passFaults.Should().BeEmpty("a release fault is not a pass fault, and 'nothing was collected' would be false");
        releaseFaults.Should().ContainSingle().Which.Should().BeSameAs(releaseFault, "the release failure is reported on its own");
        cancellations.Should().Be(0);
    }

    [Fact]
    public void AFaultedPass_ReleasesOnce_AndSurfacesItsOwnFault_EvenWhenTheReleaseFaultsToo()
    {
        var passFault = new InvalidOperationException("reference read failed");
        var releaseFault = new InvalidOperationException("release failed");
        var release = new CountingRelease(releaseFault);
        var releaseFaults = ImmutableList<Exception>.Empty;
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(
            Observable.Throw<int>(passFault), release.Release, ex => releaseFaults = releaseFaults.Add(ex)));

        release.Subscriptions.Should().Be(1, "a faulted pass has ended — it releases");
        outcome.Error.Should().BeSameAs(passFault, "the pass's own fault is what the pass reports");
        releaseFaults.Should().ContainSingle().Which.Should().BeSameAs(releaseFault);
    }

    [Fact]
    public void ACancelledPass_LeavesTheLeaseToLapse()
    {
        var release = new CountingRelease();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.ReleasedAtTerminal(
            Observable.Throw<int>(new OperationCanceledException("deadline")), release.Release, _ => { }));

        release.Subscriptions.Should().Be(0,
            "a cancelled pass may still be winding down its leaf — handing the store over under it would overlap two sweeps");
        outcome.Error.Should().BeOfType<OperationCanceledException>();
    }

    // ── Lost: when the holder must stop (review of #7515) ─────────────────────────────────────────────

    private static readonly DateTimeOffset ClaimedAt = new(2508, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HoldSpan = TimeSpan.FromHours(1);
    private static readonly long OurClaim = PrebuiltBundleRetentionLease.ClaimFor(ClaimedAt + HoldSpan, 5) | PrebuiltBundleRetentionLease.StampedFlag;

    private const long OurGeneration = 7;
    private static readonly PrebuiltBundleRetentionLeaseState Ours = new(OurClaim) { Generation = OurGeneration };

    private static PrebuiltBundleRetentionLeaseState Claimed(DateTimeOffset at, int nonce, long generation = 0)
        => new(PrebuiltBundleRetentionLease.ClaimFor(at + HoldSpan, nonce) | PrebuiltBundleRetentionLease.StampedFlag) { Generation = generation };

    /// <summary>
    /// 🚨 #7459, the fence. The owner acknowledged this pass's claim, died before it persisted, reloaded the
    /// lease as it was one generation back, and granted it to another pass, which landed the SAME generation.
    /// The holder's watch first looks only now. Before the fence it waited for its own claim to appear, which
    /// never happens, and swept beside the new holder.
    /// </summary>
    [Fact]
    public void AClaimLostBeforeTheWatchFirstLooks_ToATakeOfTheSameGeneration_IsALoss()
    {
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var outcome = new Outcome<OperationCanceledException>();
        outcome.Observe(PrebuiltBundleRetentionLease.Lost(live, OurClaim, OurGeneration));

        live.OnNext(Claimed(ClaimedAt, 9, OurGeneration));

        outcome.Values.Should().ContainSingle(
            "another claim in this pass's own generation means its take was superseded — it must stop");
        outcome.Completed.Should().BeTrue();
    }

    [Fact]
    public void ANewerGeneration_IsALoss_WhateverItsClaim()
    {
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var outcome = new Outcome<OperationCanceledException>();
        outcome.Observe(PrebuiltBundleRetentionLease.Lost(live, OurClaim, OurGeneration));

        live.OnNext(PrebuiltBundleRetentionLeaseState.Lapsed with { Generation = OurGeneration + 1 });

        outcome.Values.Should().ContainSingle("a newer generation exists, so this holder is stale");
    }

    [Fact]
    public void AnOlderGeneration_IsWaitedPast_AndTheSupersedingTakeIsStillALoss()
    {
        // NEGATIVE CONTROL: a snapshot from before the take (the previous holder's ended claim, the lease
        // lapsed one generation back, which is also what a reload of an unpersisted claim looks like) is
        // not a loss: nobody can sweep on it without taking a newer generation.
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var outcome = new Outcome<OperationCanceledException>();
        outcome.Observe(PrebuiltBundleRetentionLease.Lost(live, OurClaim, OurGeneration));

        live.OnNext(Claimed(ClaimedAt - HoldSpan - TimeSpan.FromMinutes(1), 3, OurGeneration - 1));
        live.OnNext(PrebuiltBundleRetentionLeaseState.Lapsed with { Generation = OurGeneration - 1 });
        outcome.Values.Should().BeEmpty("older generations are snapshots from before this take");

        live.OnNext(Claimed(ClaimedAt, 9, OurGeneration));
        outcome.Values.Should().ContainSingle("the take that superseded this one is still seen");
    }

    [Fact]
    public void TheFence_HoldsOnlyTheClaimInItsOwnGeneration()
    {
        PrebuiltBundleRetentionLease.IsHeldBy(Ours, OurClaim, OurGeneration).Should().BeTrue("NEGATIVE CONTROL: the holder itself");
        PrebuiltBundleRetentionLease.IsHeldBy(Ours with { Claim = OurClaim & ~PrebuiltBundleRetentionLease.StampedFlag }, OurClaim, OurGeneration)
            .Should().BeTrue("…before its stamp landed");
        PrebuiltBundleRetentionLease.IsHeldBy(Ours with { Generation = OurGeneration + 1 }, OurClaim, OurGeneration).Should().BeFalse(
            "the SAME claim in a newer generation is not this holder's: a stale holder never stamps or releases it");
        PrebuiltBundleRetentionLease.IsHeldBy(Claimed(ClaimedAt, 9, OurGeneration), OurClaim, OurGeneration).Should().BeFalse();
        PrebuiltBundleRetentionLease.IsHeldBy(null, OurClaim, OurGeneration).Should().BeFalse();

        PrebuiltBundleRetentionLease.Taken(Ours with { Claim = PrebuiltBundleRetentionLease.LapsedClaim }, 123L << PrebuiltBundleRetentionLease.NonceBits | 4)
            .Generation.Should().Be(OurGeneration + 1, "every take bumps the generation");
    }

    [Fact]
    public void ASnapshotFromBeforeTheClaim_IsNotALoss_AndALaterLossStillIs()
    {
        // CONTROL: what the live lease may replay from before this claim landed — the previous holder's
        // ended claim, or lapsed — is not a loss; the claim then appears, and a loss after it counts.
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var outcome = new Outcome<OperationCanceledException>();
        outcome.Observe(PrebuiltBundleRetentionLease.Lost(live, OurClaim, OurGeneration));

        live.OnNext(Claimed(ClaimedAt - HoldSpan - TimeSpan.FromMinutes(1), 3));
        live.OnNext(PrebuiltBundleRetentionLeaseState.Lapsed);
        live.OnNext(Ours);
        outcome.Values.Should().BeEmpty("stale snapshots from before the claim, then the claim itself");

        live.OnNext(PrebuiltBundleRetentionLeaseState.Lapsed);
        outcome.Values.Should().ContainSingle("the claim was seen and is gone");
        outcome.Completed.Should().BeTrue();
    }

    [Fact]
    public void ALeaseStreamThatFaults_IsACancellation_KeepingTheFault()
    {
        var fault = new InvalidOperationException("lease stream failed");
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var outcome = new Outcome<OperationCanceledException>();
        outcome.Observe(PrebuiltBundleRetentionLease.Lost(live, OurClaim, OurGeneration));

        live.OnNext(Ours);
        live.OnError(fault);

        outcome.Error.Should().BeNull("a fault of the watch must not reach the pass as an ordinary fault, which releases under its leaf");
        outcome.Values.Should().ContainSingle().Which.InnerException.Should().BeSameAs(fault);
    }

    [Fact]
    public void ALeaseStreamThatCompletes_IsACancellation()
    {
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var outcome = new Outcome<OperationCanceledException>();
        outcome.Observe(PrebuiltBundleRetentionLease.Lost(live, OurClaim, OurGeneration));

        live.OnNext(Ours);
        live.OnCompleted();

        outcome.Values.Should().ContainSingle("a watch that ended can no longer see a loss — the guard must not vanish silently");
    }

    // ── Fenced: the pass starts only once its own claim is seen live (#7459) ──────────────────────────

    private sealed class CountingPass
    {
        public Subject<int> Results { get; } = new();
        public int Subscriptions { get; private set; }
        public IObservable<int> Pass => Observable.Defer(() =>
        {
            Subscriptions++;
            return Results;
        });
    }

    /// <summary>
    /// 🚨 #7459, the double drop. Two takes were acknowledged and lost together with the lease node's owner
    /// (this holder's at <c>OurGeneration</c> and the take before it), the replacement owner reloaded the lease
    /// two generations back, and another replica took it there, landing a generation BELOW this holder's. The
    /// fence reads that as a snapshot from before the take and waits past it, so the pass used to sweep from
    /// the verdict on until its deadline, beside the new holder. It must not start until its own claim is
    /// seen on the live lease.
    /// </summary>
    [Fact]
    public void AHolderWhoseFirstLookIsATakeBelowItsGeneration_NeverStartsItsPass()
    {
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var pass = new CountingPass();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.Fenced(live, OurClaim, OurGeneration, pass.Pass));

        live.OnNext(Claimed(ClaimedAt, 9, OurGeneration - 1));

        pass.Subscriptions.Should().Be(0,
            "the live lease names another pass, and this holder's own claim was never seen there — it must not sweep");
        outcome.Error.Should().BeNull("an older generation alone cannot tell a lost take from a stale snapshot, so it waits");

        live.OnCompleted();
        pass.Subscriptions.Should().Be(0);
        outcome.Error.Should().BeOfType<OperationCanceledException>(
            "a lease that can no longer be followed stops the holder before it starts, and leaves the lease to lapse");
    }

    [Fact]
    public void AHolderWhoseClaimIsSeen_RunsItsPass_AndALaterLossStillStopsIt()
    {
        // NEGATIVE CONTROL: the ordinary holder, whose own claim is what the live lease carries.
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var pass = new CountingPass();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.Fenced(live, OurClaim, OurGeneration, pass.Pass));

        live.OnNext(Ours);
        pass.Subscriptions.Should().Be(1, "the claim is seen: the pass starts");
        pass.Results.OnNext(1);
        outcome.Values.Should().Equal(1);

        live.OnNext(Claimed(ClaimedAt, 9, OurGeneration - 1));
        outcome.Error.Should().BeOfType<OperationCanceledException>("once seen, any state without the claim is a loss");
    }

    [Fact]
    public void AnOlderSnapshot_ThenTheClaim_StartsThePass_WhichCompletesNormally()
    {
        // CONTROL: a replayed snapshot from before the take delays the start, it never stops it.
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var pass = new CountingPass();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.Fenced(live, OurClaim, OurGeneration, pass.Pass));

        live.OnNext(PrebuiltBundleRetentionLeaseState.Lapsed with { Generation = OurGeneration - 1 });
        pass.Subscriptions.Should().Be(0);
        live.OnNext(Ours);
        pass.Subscriptions.Should().Be(1);

        pass.Results.OnNext(4);
        pass.Results.OnCompleted();
        outcome.Values.Should().Equal(4);
        outcome.Error.Should().BeNull();
        outcome.Completed.Should().BeTrue("the pass's own completion ends the watch");
    }

    [Fact]
    public void ALeaseStreamThatFaultsBeforeTheClaimIsSeen_IsACancellation_AndThePassNeverStarts()
    {
        var fault = new InvalidOperationException("lease stream failed");
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var pass = new CountingPass();
        var outcome = new Outcome<int>();
        outcome.Observe(PrebuiltBundleRetentionLease.Fenced(live, OurClaim, OurGeneration, pass.Pass));

        live.OnError(fault);

        pass.Subscriptions.Should().Be(0);
        outcome.Error.Should().BeOfType<OperationCanceledException>(
            "never an ordinary fault, which would release the lease").Which.InnerException.Should().BeSameAs(fault);
    }

    /// <summary>
    /// #7459, the live reading: a take and its stamp are two versions at ONE generation. Between them the
    /// lease carries the new claim beside the PREVIOUS holder's <c>Replica</c>, and the skip line named that
    /// replica as the holder. Built from the same pure transitions the lease writes (release, take, stamp).
    /// </summary>
    [Fact]
    public void ATakeNotYetStamped_NamesNoReplica_NotThePreviousHolder()
    {
        var takenAt = new DateTimeOffset(2026, 10, 31, 3, 58, 13, TimeSpan.Zero);
        var until = takenAt + PrebuiltBundleRetentionLease.HoldFor(TimeSpan.FromDays(1));
        var previousClaim = PrebuiltBundleRetentionLease.ClaimFor(takenAt.AddMinutes(-25), 4711);
        var previousHolder = new PrebuiltBundleRetentionLeaseState(
            previousClaim | PrebuiltBundleRetentionLease.StampedFlag, "replica-previous", takenAt.AddMinutes(-25))
        { Generation = 49 };
        var released = PrebuiltBundleRetentionLease.Released(previousHolder);

        var claim = PrebuiltBundleRetentionLease.ClaimFor(until, 179190359);
        var taken = PrebuiltBundleRetentionLease.Taken(released, claim);
        var stamped = taken with { Claim = claim | PrebuiltBundleRetentionLease.StampedFlag, Replica = "replica-new", TakenAt = takenAt };

        // The two versions of one take: same generation, claims exactly one StampedFlag apart.
        taken.Generation.Should().Be(50);
        stamped.Generation.Should().Be(taken.Generation);
        (stamped.Claim - taken.Claim).Should().Be(PrebuiltBundleRetentionLease.StampedFlag);
        taken.Replica.Should().Be("replica-previous", "a take writes only the claim and its generation");

        var beforeStamp = PrebuiltBundleRetentionLease.DescribeHolder(taken);
        taken.HolderReplica.Should().BeNull();
        beforeStamp.Should().NotContain("replica-previous", "the previous holder released; it does not hold this claim")
            .And.Contain("pass 179190359")
            .And.Contain("not stamped");

        stamped.HolderReplica.Should().Be("replica-new");
        PrebuiltBundleRetentionLease.DescribeHolder(stamped).Should().Contain("pass 179190359 on replica-new");

        // Negative control: a released lease keeps its last Replica but names no holder replica.
        released.Replica.Should().Be("replica-previous");
        released.HolderReplica.Should().BeNull();
    }

    /// <summary>The stamp's lease transitions, from the same pure functions the lease writes.</summary>
    private static class StampScenario
    {
        public static readonly DateTimeOffset At = new(2300, 1, 1, 1, 0, 1, TimeSpan.Zero);
        public static readonly long Claim = PrebuiltBundleRetentionLease.ClaimFor(At + HoldSpan, 3);

        /// <summary>A dead pass's lease, stamped, one generation before the take: its hold has ended at <see cref="At"/>.</summary>
        public static readonly PrebuiltBundleRetentionLeaseState DeadPass = new(
            PrebuiltBundleRetentionLease.ClaimFor(At.AddSeconds(-1), 1) | PrebuiltBundleRetentionLease.StampedFlag,
            "silo-0", At.AddHours(-1))
        { Generation = 9 };

        /// <summary>The take of <see cref="Claim"/> over <see cref="DeadPass"/>, as the owner committed it.</summary>
        public static readonly PrebuiltBundleRetentionLeaseState Taken = PrebuiltBundleRetentionLease.Taken(DeadPass, Claim);

        /// <summary>
        /// The stamping fold over the mirror AS IT STANDS — what <c>stream.Update</c> folds on when no queued
        /// predecessor hands it a base (after a claim the owner first refused as a conflict, then took).
        /// </summary>
        public static IObservable<PrebuiltBundleRetentionLeaseState?> FoldOnMirror(
            BehaviorSubject<PrebuiltBundleRetentionLeaseState?> mirror, Action onStamp)
            => Observable.Defer(() =>
            {
                onStamp();
                var current = mirror.Value;
                return Observable.Return(current is not null && PrebuiltBundleRetentionLease.IsHeldBy(current, Claim, Taken.Generation)
                    ? current with { Claim = Claim | PrebuiltBundleRetentionLease.StampedFlag, Replica = "silo-1", TakenAt = At }
                    : current);
            });
    }

    /// <summary>
    /// 🚨 #7382, measured on CI (#8316): the take of a lease whose dead holder's hold had ended COMMITTED,
    /// and the take still reported "not held". The stamp folded on this hub's mirror one write behind the
    /// claim — the dead pass's lease — wrote nothing and decided on it. The replica that won skipped its
    /// pass and left its unstamped claim standing for a whole hold. The stamp must wait past a lease of an
    /// older generation (a snapshot from before its take) and decide on the take's own echo.
    /// </summary>
    [Fact]
    public void AStampWhoseMirrorTrailsItsClaim_WaitsForTheClaim_AndHolds()
    {
        var mirror = new BehaviorSubject<PrebuiltBundleRetentionLeaseState?>(StampScenario.DeadPass);
        var stamps = 0;
        var outcome = new Outcome<PrebuiltBundleRetentionLeaseVerdict>();
        outcome.Observe(PrebuiltBundleRetentionLease.StampOnceSeen(
            mirror, () => StampScenario.FoldOnMirror(mirror, () => stamps++), StampScenario.Claim, StampScenario.Taken.Generation));

        var stampsBeforeTheEcho = stamps;

        mirror.OnNext(StampScenario.Taken);

        outcome.Values.Should().ContainSingle().Which.Held.Should().BeTrue(
            "the owner committed this claim — the replica that took the lease holds it and sweeps");
        outcome.Values[0].Lease!.HolderReplica.Should().Be("silo-1");
        outcome.Completed.Should().BeTrue();
        stampsBeforeTheEcho.Should().Be(0, "a lease one generation older than the take is a snapshot from before it — no verdict");
        stamps.Should().Be(1);
    }

    /// <summary>
    /// NEGATIVE CONTROL for the wait: a lease of the take's own generation carrying ANOTHER claim (the owner
    /// lost this take and granted the same generation again) is decided at once, and is not held.
    /// </summary>
    [Fact]
    public void AStampThatFindsAnotherClaimInItsGeneration_IsNotHeld_WithoutWaiting()
    {
        var other = PrebuiltBundleRetentionLease.Taken(
            StampScenario.DeadPass, PrebuiltBundleRetentionLease.ClaimFor(StampScenario.At + HoldSpan, 4));
        var mirror = new BehaviorSubject<PrebuiltBundleRetentionLeaseState?>(other);
        var stamps = 0;
        var outcome = new Outcome<PrebuiltBundleRetentionLeaseVerdict>();
        outcome.Observe(PrebuiltBundleRetentionLease.StampOnceSeen(
            mirror, () => StampScenario.FoldOnMirror(mirror, () => stamps++), StampScenario.Claim, StampScenario.Taken.Generation));

        stamps.Should().Be(1, "a lease of the take's own generation is not a snapshot from before it");
        outcome.Values.Should().ContainSingle().Which.Held.Should().BeFalse("another pass holds the lease");
        outcome.Values[0].Lease!.Holder.Should().Be(4);
    }

    /// <summary>NEGATIVE CONTROL: a NEWER generation (another replica took after a loss) decides at once, and is not held.</summary>
    [Fact]
    public void AStampThatFindsANewerGeneration_IsNotHeld_WithoutWaiting()
    {
        var newer = PrebuiltBundleRetentionLease.Taken(
            StampScenario.Taken, PrebuiltBundleRetentionLease.ClaimFor(StampScenario.At + HoldSpan + HoldSpan, 6));
        var mirror = new BehaviorSubject<PrebuiltBundleRetentionLeaseState?>(newer);
        var outcome = new Outcome<PrebuiltBundleRetentionLeaseVerdict>();
        outcome.Observe(PrebuiltBundleRetentionLease.StampOnceSeen(
            mirror, () => StampScenario.FoldOnMirror(mirror, () => { }), StampScenario.Claim, StampScenario.Taken.Generation));

        outcome.Values.Should().ContainSingle().Which.Held.Should().BeFalse();
        outcome.Values[0].Lease!.Generation.Should().Be(StampScenario.Taken.Generation + 1);
    }

    /// <summary>An unreadable lease is decided at once and never held: nobody sweeps on a lease it cannot read.</summary>
    [Fact]
    public void AStampOnAnUnreadableLease_IsNotHeld_WithoutWaiting()
    {
        var mirror = new BehaviorSubject<PrebuiltBundleRetentionLeaseState?>(null);
        var outcome = new Outcome<PrebuiltBundleRetentionLeaseVerdict>();
        outcome.Observe(PrebuiltBundleRetentionLease.StampOnceSeen(
            mirror, () => StampScenario.FoldOnMirror(mirror, () => { }), StampScenario.Claim, StampScenario.Taken.Generation));

        outcome.Values.Should().ContainSingle().Which.Held.Should().BeFalse();
        outcome.Values[0].Lease.Should().BeNull();
    }

    /// <summary>
    /// A lease stream that ends before carrying the take still stamps, on what the write path reads: the
    /// wait refuses to decide early, it never invents a verdict or swallows the attempt.
    /// </summary>
    [Fact]
    public void AStampWhoseLeaseStreamEndsFirst_StillStamps_AndDecidesOnWhatItReads()
    {
        var mirror = new BehaviorSubject<PrebuiltBundleRetentionLeaseState?>(StampScenario.DeadPass);
        var live = new Subject<PrebuiltBundleRetentionLeaseState?>();
        var stamps = 0;
        var outcome = new Outcome<PrebuiltBundleRetentionLeaseVerdict>();
        outcome.Observe(PrebuiltBundleRetentionLease.StampOnceSeen(
            live, () => StampScenario.FoldOnMirror(mirror, () => stamps++), StampScenario.Claim, StampScenario.Taken.Generation));

        live.OnNext(StampScenario.DeadPass);
        stamps.Should().Be(0);
        live.OnCompleted();

        stamps.Should().Be(1);
        outcome.Values.Should().ContainSingle().Which.Held.Should().BeFalse("what it read does not carry the claim");
        outcome.Completed.Should().BeTrue();
    }
}
