#pragma warning disable CS1591

using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>Two silos whose meshes register the prebuilt-bundle retention lease, as every portal host does.</summary>
public class RetentionLeaseTwoSiloFixture : TwoSiloCacheUpdateFixture
{
    protected override Type SiloConfiguratorType => typeof(RetentionLeaseSiloConfigurator);
}

/// <summary>Adds the lease registration the retention pass's own registration carries.</summary>
public class RetentionLeaseSiloConfigurator : TwoSiloConfigurator
{
    protected override MeshBuilder ConfigureAdditional(MeshBuilder builder)
        => builder.ConfigureServices(services => services.AddPrebuiltBundleRetentionLease());
}

/// <summary>
/// 🚨 <b>#7382 — two replicas of one store's owner never sweep the shared prebuilt-bundle store at
/// once.</b>
///
/// <para>Every replica runs <see cref="PrebuiltBundleRetentionHostedService"/> over the SAME mounted
/// store. Measured on a three-replica owner: at boot all three swept together, one collected 787
/// identities and the other two aborted on directories vanishing under their walk — and nothing
/// stopped two armed passes from planning deletions against each other's half-removed state. Each
/// pass now runs under <see cref="PrebuiltBundleRetentionLease"/>, a mesh node both replicas claim by
/// a fold its single owning activation decides.</para>
///
/// <para>Two silos, so the two claims are two processes writing to one grain — the production shape.
/// The passes are controlled: a pass that takes the lease PARKS on a switch the test holds, so the
/// other replica's attempt is decided while the first is provably mid-sweep. The negative control
/// shows the second replica CAN take the lease — once the first's hold has lapsed — so "only one
/// sweeps" is the lease's verdict and not a rig in which the second replica could never win.</para>
/// </summary>
public class PrebuiltBundleRetentionLeaseAcrossSilosTest : IClassFixture<RetentionLeaseTwoSiloFixture>
{
    private static readonly TimeSpan Hold = PrebuiltBundleRetentionLease.MinimumHold;

    private readonly RetentionLeaseTwoSiloFixture fixture;

    public PrebuiltBundleRetentionLeaseAcrossSilosTest(RetentionLeaseTwoSiloFixture fixture)
    {
        this.fixture = fixture;
    }

    private static IServiceProvider Services(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    private IMessageHub SiloMeshHub(int index)
    {
        var services = Services(fixture.Cluster, index);
        PrebuiltBundleRetentionLease.RegisterContentType(services);
        return services.GetRequiredService<IMessageHub>();
    }

    /// <summary>
    /// Waits until <paramref name="hub"/>'s own view of the lease satisfies <paramref name="condition"/>.
    /// A claimant decides "held by another" on ITS mirror and then writes nothing, so a mirror that
    /// trails the owner yields a false skip — safe (a claim is only ever HELD once the owner committed
    /// it), but a test asserting what a replica sees must first wait for that replica to see it.
    /// </summary>
    private static Task<PrebuiltBundleRetentionLeaseState> Sees(
        IMessageHub hub, Func<PrebuiltBundleRetentionLeaseState, bool> condition, CancellationToken ct)
        => hub.GetWorkspace().GetMeshNodeStream(PrebuiltBundleRetentionLease.LeasePath)
            .Select(node => node.ContentAs<PrebuiltBundleRetentionLeaseState>(hub.JsonSerializerOptions))
            .Where(lease => lease is not null && condition(lease))
            .Select(lease => lease!)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>A release in these tests never faults; one that did is a broken precondition, not a result.</summary>
    private static void NoReleaseFault(Exception fault)
        => throw new InvalidOperationException("PRECONDITION: releasing the lease faulted", fault);

    /// <summary>
    /// Writes the lease LAPSED, as System — the state a replacement owner reloads when the holder's claim
    /// was acknowledged but never persisted (an Update is acked on its flush bound, before durability).
    /// </summary>
    private static Task<MeshNode> ReloadedLapsed(IMessageHub hub, CancellationToken ct)
        => hub.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(PrebuiltBundleRetentionLease.LeasePath)
                // What persisted BEFORE the lost claim: lapsed, one generation back.
                .Update(node => node with
                {
                    Content = PrebuiltBundleRetentionLease.Released(PrebuiltBundleRetentionLeaseState.Lapsed with
                    {
                        Generation = Math.Max(
                            (node.ContentAs<PrebuiltBundleRetentionLeaseState>(hub.JsonSerializerOptions)?.Generation ?? 0) - 1, 0),
                    }),
                })
                .Take(1))
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long ticks = start.UtcTicks;
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    }

    /// <summary>A clock whose every read returns the next scripted instant (the last one repeats).</summary>
    private sealed class SteppingClock(params DateTimeOffset[] reads) : TimeProvider
    {
        private int next = -1;
        public override DateTimeOffset GetUtcNow() => reads[Math.Min(Interlocked.Increment(ref next), reads.Length - 1)];
    }

    /// <summary>One replica's controlled pass: counts its entries, signals that it started, and parks until released.</summary>
    private sealed class ControlledPass(int replica)
    {
        private int entries;
        public int Entries => Volatile.Read(ref entries);
        public AsyncSubject<Unit> Started { get; } = new();
        public AsyncSubject<Unit> Release { get; } = new();
        public AsyncSubject<PrebuiltBundleRetentionLeaseVerdict> Skipped { get; } = new();
        /// <summary>Signalled when the pass's subscription ends — completed, faulted, or cut by the lease.</summary>
        public AsyncSubject<Unit> Ended { get; } = new();

        public IObservable<int> Run() => Observable.Defer(() =>
        {
            Interlocked.Increment(ref entries);
            Started.OnNext(Unit.Default);
            Started.OnCompleted();
            return Release.Select(_ => replica);
        }).Finally(() =>
        {
            Ended.OnNext(Unit.Default);
            Ended.OnCompleted();
        });

        public void OnSkipped(PrebuiltBundleRetentionLeaseVerdict verdict)
        {
            Skipped.OnNext(verdict);
            Skipped.OnCompleted();
        }
    }

    [Fact]
    public async Task TwoReplicasPassing_AtTheSameInstant_OnlyOneSweeps_AndTheOtherLearnsWho()
    {
        var ct = TestContext.Current.CancellationToken;
        fixture.Cluster.Silos.Count.Should().BeGreaterThanOrEqualTo(2, "the race is between two PROCESSES");
        var hubs = new[] { SiloMeshHub(0), SiloMeshHub(1) };
        // A fresh epoch per run: the lease node outlives one test in the shared cluster.
        var now = new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var passes = new[] { new ControlledPass(0), new ControlledPass(1) };
        var runs = new ReplaySubject<int>[] { new(), new() };
        var completed = new[] { new AsyncSubject<Unit>(), new AsyncSubject<Unit>() };
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            PrebuiltBundleRetentionLease
                .RunLeased(hubs[index], new ManualClock(now), Hold, $"silo-{index}", passes[index].Run, passes[index].OnSkipped, NoReleaseFault)
                .Subscribe(
                    value => runs[index].OnNext(value),
                    ex => { runs[index].OnError(ex); completed[index].OnError(ex); },
                    () => { runs[index].OnCompleted(); completed[index].OnNext(Unit.Default); completed[index].OnCompleted(); });
        }

        // Whichever replica lost is told so while the winner is still parked mid-pass.
        var loserIndex = await Observable.Merge(
                passes[0].Skipped.Select(_ => 0),
                passes[1].Skipped.Select(_ => 1))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        var winnerIndex = 1 - loserIndex;
        await passes[winnerIndex].Started.Timeout(TestTimeouts.Convergence).Await(ct);
        var lostTo = await passes[loserIndex].Skipped.Await(ct);
        await completed[loserIndex].Timeout(TestTimeouts.Convergence).Await(ct);

        passes[winnerIndex].Entries.Should().Be(1, "the replica that took the lease sweeps");
        passes[loserIndex].Entries.Should().Be(0,
            "a replica that does not hold the lease must not touch the shared store while another sweeps it");
        lostTo.Held.Should().BeFalse();
        lostTo.Lease.Should().NotBeNull("the loser reads the lease the owner settled");
        lostTo.Lease!.Until.Should().Be(now + Hold);
        lostTo.Lease.Holder.Should().NotBe(0);

        // The holder stamps its name in a second write, AFTER its claim landed and BEFORE its pass
        // starts — so with the winner provably mid-pass, a later attempt names it.
        await Sees(hubs[loserIndex], lease => lease.Replica == $"silo-{winnerIndex}", ct);
        var whileSweeping = await PrebuiltBundleRetentionLease
            .Take(hubs[loserIndex], now, Hold, PrebuiltBundleRetentionLease.NewNonce(), $"silo-{loserIndex}")
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        whileSweeping.Held.Should().BeFalse("the winner is still sweeping");
        whileSweeping.Lease!.Holder.Should().Be(lostTo.Lease.Holder, "the claim the loser lost to is still the claim");
        whileSweeping.Lease.Replica.Should().Be($"silo-{winnerIndex}",
            "a skipped pass names the replica that runs the one sweep");

        // The winner finishes and RELEASES: the loser's next attempt at the same instant takes the lease.
        passes[winnerIndex].Release.OnNext(Unit.Default);
        passes[winnerIndex].Release.OnCompleted();
        await completed[winnerIndex].Timeout(TestTimeouts.Convergence).Await(ct);

        await Sees(hubs[loserIndex], lease => lease.IsLapsed, ct);
        var next = new ControlledPass(loserIndex);
        next.Release.OnNext(Unit.Default);
        next.Release.OnCompleted();
        var nextResult = await PrebuiltBundleRetentionLease
            .RunLeased(hubs[loserIndex], new ManualClock(now), Hold, $"silo-{loserIndex}", next.Run, next.OnSkipped, NoReleaseFault)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        nextResult.Should().Be(loserIndex,
            "a finished pass releases its lease, so the next pass on ANY replica sweeps without waiting for a lapse");
        next.Entries.Should().Be(1);
    }

    /// <summary>
    /// NEGATIVE CONTROL — the second replica is not structurally unable to win. A lease taken on silo A
    /// and never released (a pass that died) refuses silo B within its hold and is taken by B once the
    /// hold has lapsed.
    /// </summary>
    [Fact]
    public async Task ALeaseLeftByADeadPass_RefusesTheOtherReplicaUntilItLapses_ThenItTakesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var hubA = SiloMeshHub(0);
        var hubB = SiloMeshHub(1);
        const int DeadPass = 1;
        const int AfterLapse = 3;
        var now = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var dead = await PrebuiltBundleRetentionLease.Take(hubA, now, Hold, DeadPass, "silo-0")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        dead.Held.Should().BeTrue("PRECONDITION: nobody holds the lease at a fresh epoch");

        await Sees(hubB, lease => lease.Holder == DeadPass && lease.Replica == "silo-0", ct);
        var within = await PrebuiltBundleRetentionLease.Take(hubB, now + Hold / 2, Hold, 2, "silo-1")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        within.Held.Should().BeFalse("the dead pass's hold has not lapsed");
        within.Lease!.Holder.Should().Be(DeadPass);
        within.Lease.Until.Should().Be(now + Hold,
            "a refused claimant must not land ANY part of its claim — the hold end stays the holder's");
        within.Lease.Replica.Should().Be("silo-0", "only the confirmed holder stamps its name");

        var lapsed = await PrebuiltBundleRetentionLease.Take(hubB, now + Hold + TimeSpan.FromSeconds(1), Hold, AfterLapse, "silo-1")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        lapsed.Held.Should().BeTrue(
            "a lapsed hold is free — the other replica takes it, so a pass that dies holding the lease "
            + "delays the next sweep by one hold, never forever");

        await Sees(hubA, lease => lease.Holder == AfterLapse && lease.Replica == "silo-1", ct);
        await PrebuiltBundleRetentionLease.Release(hubA, DeadPass).Timeout(TestTimeouts.Convergence).Await(ct);
        var afterStaleRelease = await PrebuiltBundleRetentionLease.Take(hubA, now + Hold + TimeSpan.FromSeconds(2), Hold, 4, "silo-0")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        afterStaleRelease.Held.Should().BeFalse(
            "a release by a token that no longer holds the lease must not free the new holder's lease");
        afterStaleRelease.Lease!.Holder.Should().Be(AfterLapse);
        afterStaleRelease.Lease.Replica.Should().Be("silo-1");

        // Leave the shared lease lapsed: the other tests of this class run on the same cluster, at
        // epochs this test's hold would otherwise outlast.
        await PrebuiltBundleRetentionLease.Release(hubB, AfterLapse).Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// 🚨 A pass QUEUED behind a running one (the schedule serialises ticks through <c>Concat</c>) dates
    /// its hold from when it STARTS, never from when its tick composed it. Composed at T and subscribed
    /// two holds later, a compose-time clock read would claim a hold ending at T + hold — already over,
    /// so another replica could take the lease while this pass sweeps under its fresh deadline.
    /// </summary>
    [Fact]
    public async Task AQueuedPass_DatesItsHoldFromWhenItStarts_NotFromWhenItWasQueued()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = SiloMeshHub(0);
        var queuedAt = new DateTimeOffset(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(queuedAt);
        const string Replica = "silo-queued";

        // Composed now (the tick)…
        var attempt = PrebuiltBundleRetentionLease.RunLeased(
            hub, clock, Hold, Replica,
            () => hub.GetWorkspace().GetMeshNodeStream(PrebuiltBundleRetentionLease.LeasePath)
                .Select(node => node.ContentAs<PrebuiltBundleRetentionLeaseState>(hub.JsonSerializerOptions))
                .Where(lease => lease is not null && lease.Replica == Replica)
                .Select(lease => lease!)
                .Take(1),
            verdict => throw new InvalidOperationException($"PRECONDITION: the lease is free at a fresh epoch, yet the pass skipped: {verdict}"),
            NoReleaseFault);

        // …and started two holds later, when the pass ahead of it finished.
        clock.Advance(Hold * 2);
        var startedAt = queuedAt + Hold * 2;
        var held = await attempt.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        held.Until.Should().Be(startedAt + Hold,
            "the hold must cover the pass from its START — a hold dated from the tick would already have ended");
        held.TakenAt.Should().Be(startedAt);

        // The finished pass released the lease; leave it lapsed for the other tests on this cluster.
        await Sees(hub, lease => lease.IsLapsed, ct);
    }

    /// <summary>
    /// 🚨 A claim that LANDED is not a claim that is HELD by the time its holder stamps it. The take is two
    /// writes; a claimant that pauses across its own hold's end between them can find another replica's
    /// claim there, and its stamp then (correctly) writes nothing — so the verdict must say NOT held, or
    /// two replicas sweep at once. The interleaving is driven deterministically, write by write, across
    /// two silos. NEGATIVE CONTROL first: while the claim is still its own, the same stamp says held.
    /// </summary>
    [Fact]
    public async Task AStampThatFindsAnotherReplicasClaim_IsNotHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var hubA = SiloMeshHub(0);
        var hubB = SiloMeshHub(1);
        const int Paused = 5;
        const int Taker = 6;
        var t = new DateTimeOffset(2500, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var claimA = PrebuiltBundleRetentionLease.ClaimFor(t + Hold, Paused);

        var landed = await PrebuiltBundleRetentionLease.Claim(hubA, t, claimA)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        landed!.Claim.Should().Be(claimA, "PRECONDITION: nobody holds the lease at a fresh epoch");

        var stillOurs = await PrebuiltBundleRetentionLease.Stamp(hubA, claimA, landed.Generation, "silo-0", t)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        stillOurs.Held.Should().BeTrue("NEGATIVE CONTROL: a stamp over its own claim holds the lease");

        // A pauses across its hold's end; B takes the lapsed lease. B must first see A's STAMPED claim — the
        // state A paused in. `Holder == Paused` alone also matches A's claim BEFORE its stamp reached B's
        // mirror (the holder ignores the stamped flag), and B would then claim from a base the owner has
        // already moved past.
        var stampedA = claimA | PrebuiltBundleRetentionLease.StampedFlag;
        await Sees(hubB, lease => lease.Claim == stampedA, ct);
        var taken = await PrebuiltBundleRetentionLease.Take(hubB, t + Hold + TimeSpan.FromSeconds(1), Hold, Taker, "silo-1")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        taken.Held.Should().BeTrue("PRECONDITION: A's hold has ended, so B takes the lease");

        // A resumes and runs its stamp — once it sees B's SETTLED lease, claim AND stamp. A stamp that finds
        // another claim writes nothing and reports A's own mirror; waiting for `Holder == Taker` alone let
        // that mirror still carry B's unstamped claim beside A's earlier replica name, which the assertions
        // below then read as "A's stamp landed over B's claim". Measured: red on a Linux build host and in
        // a host-shaped container, green on Actions — the mirror's lag, not the lease, decided it.
        var settledB = taken.Lease!.Claim;
        await Sees(hubA, lease => lease.Claim == settledB, ct);
        var resumed = await PrebuiltBundleRetentionLease.Stamp(hubA, claimA, landed.Generation, "silo-0", t)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        resumed.Held.Should().BeFalse(
            "the lease the stamp settled carries B's claim — A sweeping now would overlap B's sweep");
        resumed.Lease!.Holder.Should().Be(Taker);
        resumed.Lease.Replica.Should().Be("silo-1", "A's stamp must not land over B's claim");

        await PrebuiltBundleRetentionLease.Release(hubB, Taker).Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// 🚨 The pass deadline is ABSOLUTE — half the hold from the instant the claim was dated. Taking the
    /// lease spends from it: when acquisition has already used the half-hold (the clock read after the
    /// take is past it), the pass must not start at all — it would run under a hold that ends while it
    /// sweeps. NEGATIVE CONTROL: the same take with time to spare runs.
    /// </summary>
    [Fact]
    public async Task APassWhoseAcquisitionUsedHalfTheHold_DoesNotStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = SiloMeshHub(0);
        // A fresh epoch below the claim's range (seconds << NonceBits stays within a long until ~2514).
        var t = new DateTimeOffset(2505, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var late = new ControlledPass(0);
        late.Release.OnNext(Unit.Default);
        late.Release.OnCompleted();
        var refused = await PrebuiltBundleRetentionLease
            .RunLeased(hub, new SteppingClock(t, t + PrebuiltBundleRetentionLease.PassDeadline(Hold)), Hold, "silo-late",
                late.Run, verdict => throw new InvalidOperationException($"PRECONDITION: the lease is free at a fresh epoch: {verdict}"),
                NoReleaseFault)
            .Materialize()
            .Where(n => n.Kind != System.Reactive.NotificationKind.OnNext)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        refused.Exception.Should().BeOfType<OperationCanceledException>("acquisition used the whole half-hold");
        late.Entries.Should().Be(0, "a pass with no deadline left must not touch the store");
        // The skipped pass leaves its lease to lapse; free it here so the other tests' epochs are not held.
        var left = await Sees(hub, lease => lease.Replica == "silo-late", ct);
        await PrebuiltBundleRetentionLease.Release(hub, left.Holder).Timeout(TestTimeouts.Convergence).Await(ct);
        await Sees(hub, lease => lease.IsLapsed, ct);

        var onTime = new ControlledPass(0);
        onTime.Release.OnNext(Unit.Default);
        onTime.Release.OnCompleted();
        var ran = await PrebuiltBundleRetentionLease
            .RunLeased(hub, new SteppingClock(t + Hold, t + Hold + TimeSpan.FromMinutes(1)), Hold, "silo-ontime",
                onTime.Run, verdict => throw new InvalidOperationException($"PRECONDITION: the lease was released: {verdict}"),
                NoReleaseFault)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        ran.Should().Be(0, "NEGATIVE CONTROL: with the deadline still ahead the pass runs");
        onTime.Entries.Should().Be(1);
        await Sees(hub, lease => lease.IsLapsed, ct);
    }

    /// <summary>
    /// 🚨 #7459 — a holder whose claim is LOST under it stops sweeping. The claim and the stamp are
    /// acknowledged on commit, not on persistence; an owner that dies before they persist is replaced by
    /// one that reloads the LAPSED lease, and another replica then takes it while the first holder still
    /// sweeps. The lost persistence is reproduced deterministically by writing the lease lapsed while the
    /// holder is parked mid-pass; silo B then takes it. The holder's pass must end — cancelled, not
    /// faulted, so it does not release — and leave B's claim alone. NEGATIVE CONTROL: the first test of
    /// this class parks a holder while the other replica's take is refused, and that holder is NOT cut.
    /// </summary>
    [Fact]
    public async Task AHolderWhoseClaimIsLostUnderIt_StopsItsPass_AndLeavesTheNewHolderAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var hubA = SiloMeshHub(0);
        var hubB = SiloMeshHub(1);
        const int Taker = 9;
        var now = new DateTimeOffset(2508, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var holder = new ControlledPass(0);
        var outcome = new ReplaySubject<Notification<int>>();
        PrebuiltBundleRetentionLease
            .RunLeased(hubA, new ManualClock(now), Hold, "silo-0", holder.Run,
                verdict => throw new InvalidOperationException($"PRECONDITION: the lease is free at a fresh epoch: {verdict}"),
                NoReleaseFault)
            .Materialize()
            .Subscribe(outcome);
        await holder.Started.Timeout(TestTimeouts.Convergence).Await(ct);
        await Sees(hubB, lease => lease.Replica == "silo-0" && lease.Until == now + Hold, ct);

        // The owner lost the un-persisted claim: the lease reloads lapsed, and B takes it.
        await ReloadedLapsed(hubB, ct);
        await Sees(hubB, lease => lease.IsLapsed, ct);
        var taken = await PrebuiltBundleRetentionLease.Take(hubB, now, Hold, Taker, "silo-1")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        taken.Held.Should().BeTrue("PRECONDITION: the reloaded lease is lapsed, so the other replica takes it");

        var ended = await outcome
            .Where(n => n.Kind != NotificationKind.OnNext)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        ended.Exception.Should().BeOfType<OperationCanceledException>(
            "a holder whose claim is gone must stop — two replicas sweeping one store is what the lease exists to prevent");
        await holder.Ended.Timeout(TestTimeouts.Convergence).Await(ct);
        holder.Entries.Should().Be(1);

        // B's take settled its STAMPED claim; A's mirror reaches it, and nothing of A's lands over it.
        var after = await Sees(hubA, lease => lease.Claim == taken.Lease!.Claim, ct);
        after.Replica.Should().Be("silo-1", "the stopped pass neither releases nor stamps over the new holder");
        after.Holder.Should().Be(Taker);

        await PrebuiltBundleRetentionLease.Release(hubB, Taker).Timeout(TestTimeouts.Convergence).Await(ct);
        await Sees(hubA, lease => lease.IsLapsed, ct);
    }

    /// <summary>
    /// 🚨 #7459: the claim/generation commit guard is armed at the lease node's OWNER. A write that advances
    /// the generation without a new claim (what a partially refused take leaves) commits nothing, from any
    /// silo. NEGATIVE CONTROL: the take before it, the claim with the next generation, commits.
    /// </summary>
    [Fact]
    public async Task AGenerationWithoutItsClaim_IsRefusedAtTheOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var hubA = SiloMeshHub(0);
        var hubB = SiloMeshHub(1);
        const int Holder = 15;
        var now = new DateTimeOffset(2511, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var held = await PrebuiltBundleRetentionLease.Take(hubA, now, Hold, Holder, "silo-0")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        held.Held.Should().BeTrue("NEGATIVE CONTROL: a whole take commits");
        var lease = await Sees(hubB, l => l.Claim == held.Lease!.Claim, ct);

        var outcome = await hubB.ServiceProvider.GetService<AccessService>()
            .RunAsSystem(() => hubB.GetWorkspace().GetMeshNodeStream(PrebuiltBundleRetentionLease.LeasePath)
                .Update(node => node with { Content = lease with { Generation = lease.Generation + 1 } })
                .Take(1)
                .Select(_ => (Exception?)null)
                .Catch<Exception?, Exception>(fault => Observable.Return<Exception?>(fault)))
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        outcome.Should().NotBeNull("the owner refuses a generation that advances without a new claim");

        var after = await PrebuiltBundleRetentionLease.Stamp(hubA, held.Lease!.Claim, held.Lease.Generation, "silo-0", now)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        after.Held.Should().BeTrue("the holder's fence still holds: nothing of the refused write committed");
        after.Lease!.Generation.Should().Be(held.Lease.Generation);

        await PrebuiltBundleRetentionLease.Release(hubA, Holder).Timeout(TestTimeouts.Convergence).Await(ct);
        await Sees(hubB, l => l.IsLapsed, ct);
    }

    /// <summary>
    /// 🚨 #7459, the fence. The lease node's owner ACKNOWLEDGED A's claim and stamp, then died before they
    /// persisted. Its replacement reloads the lease as it was before A's take (lapsed, one generation back),
    /// and B takes it, landing the SAME generation A took. A's watch on the live lease starts only now, so
    /// the first state it sees is B's claim. Without a fence it cannot tell that state from a snapshot
    /// from before its own take: it waited for its claim to appear, which never happens, and A swept beside
    /// B. With the fence, a lease of A's generation (or a newer one) that does not carry A's claim stops
    /// A, and none of A's stale writes (stamp, release) touches B's lease. NEGATIVE CONTROL: before the
    /// loss, the same checks accept A as the holder.
    /// </summary>
    [Fact]
    public async Task AHolderWhoseAckedClaimDiedWithTheOwner_IsRefused_EvenWhenItFirstLooksAfterTheLoss()
    {
        var ct = TestContext.Current.CancellationToken;
        var hubA = SiloMeshHub(0);
        var hubB = SiloMeshHub(1);
        const int Stale = 13;
        const int Taker = 14;
        var now = new DateTimeOffset(2509, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var heldA = await PrebuiltBundleRetentionLease.Take(hubA, now, Hold, Stale, "silo-0")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        heldA.Held.Should().BeTrue("PRECONDITION: the lease is free at a fresh epoch");
        var claimA = heldA.Lease!.Claim;
        var generationA = heldA.Lease.Generation;
        var seenByB = await Sees(hubB, lease => lease.Claim == claimA, ct);
        PrebuiltBundleRetentionLease.IsHeldBy(seenByB, claimA, generationA).Should().BeTrue(
            "NEGATIVE CONTROL: while A's claim stands, the fence accepts A as the holder");

        // The owner lost the acknowledged claim: the lease reloads as it was before A's take, and B takes it.
        await ReloadedLapsed(hubB, ct);
        await Sees(hubB, lease => lease.IsLapsed, ct);
        var taken = await PrebuiltBundleRetentionLease.Take(hubB, now, Hold, Taker, "silo-1")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        taken.Held.Should().BeTrue("PRECONDITION: the reloaded lease is lapsed, so the other replica takes it");
        taken.Lease!.Generation.Should().Be(generationA,
            "the reload lost A's generation too, so B's take lands the same number: the fence compares the claim within it");
        await Sees(hubA, lease => lease.Claim == taken.Lease.Claim, ct);

        // A's watch starts AFTER the loss: the first state it sees is B's claim.
        var stopped = await PrebuiltBundleRetentionLease
            .Lost(hubA.GetWorkspace().GetMeshNodeStream(PrebuiltBundleRetentionLease.LeasePath)
                    .Select(node => node.ContentAs<PrebuiltBundleRetentionLeaseState>(hubA.JsonSerializerOptions)),
                claimA, generationA)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        stopped.Message.Should().Contain($"this pass took generation {generationA}",
            "a holder that first looks after its claim was lost must stop, not wait for a claim that never comes back");

        // None of A's stale writes may act on B's lease.
        var restamped = await PrebuiltBundleRetentionLease.Stamp(hubA, claimA, generationA, "silo-0", now)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        restamped.Held.Should().BeFalse("a stale holder's stamp finds another claim in its generation");
        await PrebuiltBundleRetentionLease.Release(hubA, claimA, generationA).Timeout(TestTimeouts.Convergence).Await(ct);
        var after = await Sees(hubB, lease => lease.Claim == taken.Lease.Claim, ct);
        after.Replica.Should().Be("silo-1", "the stale holder neither stamps nor releases over the new holder");
        after.Generation.Should().Be(generationA);

        await PrebuiltBundleRetentionLease.Release(hubB, Taker).Timeout(TestTimeouts.Convergence).Await(ct);
        await Sees(hubA, lease => lease.IsLapsed, ct);
    }

    /// <summary>
    /// The generation is always written, <c>0</c> included, so a stale claimant's base carries it and the
    /// owner REFUSES its generation instead of letting it land over a newer one. NEGATIVE CONTROL: the
    /// same merge on a shape that omits a default generation (the hub serializer's default) applies the
    /// stale generation, and the fence goes backwards.
    /// </summary>
    [Fact]
    public void AStaleClaimantsGeneration_IsRefused_NeverMovesTheFenceBack()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var staleBase = PrebuiltBundleRetentionLeaseState.Lapsed;
        var live = new PrebuiltBundleRetentionLeaseState(
            PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1), 41) | PrebuiltBundleRetentionLease.StampedFlag, "silo-0", t)
        { Generation = 3 };
        var stale = PrebuiltBundleRetentionLease.Taken(staleBase, PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(2), 42));
        stale.Generation.Should().Be(1, "PRECONDITION: a take bumps the generation it read");

        var (merged, refused) = MergeLosingClaim(staleBase, live, stale);
        refused.Should().Contain("generation", "the base carries generation 0, so the owner sees it changed on both sides");
        merged.Deserialize<PrebuiltBundleRetentionLeaseState>(Web)!.Generation.Should().Be(3);

        var (omitted, omittedRefused) = MergeLosingClaim(
            new OmittingGenerationShape(staleBase.Claim, 0),
            new OmittingGenerationShape(live.Claim, live.Generation),
            new OmittingGenerationShape(stale.Claim, stale.Generation));
        omittedRefused.Should().NotContain("generation", "NEGATIVE CONTROL: an omitted 0 gives the owner no conflict signal");
        omitted.Deserialize<OmittingGenerationShape>(Web)!.Generation.Should().Be(1, "…and the fence moved backwards");
    }

    /// <summary>
    /// 🚨 #7459: a take proposed from a STALE lapsed base (generation 0) against an owner whose lease is lapsed
    /// one generation later, exactly as <c>MeshNodePatchMerge</c> resolves it. The stale take proposes
    /// generation 1, which IS the live generation, so the generation leaf carries no conflict; the claim
    /// leaf must. A released lease names its generation in its lapsed claim, so the claim changed since the
    /// stale base and the take is refused WHOLE, and its re-run fold lands generation 2. NEGATIVE CONTROL: a
    /// release that wrote one constant lapsed claim gives the owner no conflict on either leaf, and the take
    /// lands at generation 1, a generation a lost take may already have handed to another holder.
    /// </summary>
    [Fact]
    public void AStaleLapsedBase_AgainstANewerReleasedLease_IsRefusedWhole_AndTheRerunTakesTheNextGeneration()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var staleBase = PrebuiltBundleRetentionLeaseState.Lapsed;
        var firstTake = PrebuiltBundleRetentionLease.Taken(staleBase, PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1), 51));
        var live = PrebuiltBundleRetentionLease.Released(firstTake with { Claim = firstTake.Claim | PrebuiltBundleRetentionLease.StampedFlag });
        live.Generation.Should().Be(1, "PRECONDITION: the owner's lease was taken once and released");
        live.IsLapsed.Should().BeTrue("PRECONDITION: …and nobody holds it");
        var stale = PrebuiltBundleRetentionLease.Taken(staleBase, PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(2), 52));
        stale.Generation.Should().Be(live.Generation, "PRECONDITION: the stale take proposes the live generation, so only the claim can conflict");

        var (merged, refused) = MergeLosingClaim(staleBase, live, stale);
        refused.Should().Contain("claim", "the released claim names generation 1, so it changed since the stale base");
        merged.Deserialize<PrebuiltBundleRetentionLeaseState>(Web).Should().Be(live, "nothing of the stale take may land");
        PrebuiltBundleRetentionLease.Taken(live, stale.Claim).Generation.Should().Be(2,
            "the re-run fold reads the live lease and takes the NEXT generation");

        var constantLapsed = live with { Claim = PrebuiltBundleRetentionLease.LapsedClaim };
        var (landed, landedRefused) = MergeLosingClaim(staleBase, constantLapsed, stale);
        landedRefused.Should().BeEmpty("NEGATIVE CONTROL: with one constant lapsed claim neither leaf conflicts");
        var wrong = landed.Deserialize<PrebuiltBundleRetentionLeaseState>(Web)!;
        wrong.Holder.Should().Be(52, "…so the stale take lands");
        wrong.Generation.Should().Be(1, "…at a generation that was already taken once");
    }

    /// <summary>
    /// 🚨 #7459, a PARTIAL refusal against a RELEASED lease. The owner keeps the leaves that did not conflict:
    /// a take from an expired base <c>(claimA, 1)</c> against the released <c>(-2, 1)</c> has its claim refused
    /// and its generation applied, so the merge produces <c>(-2, 2)</c>, a lapsed lease a generation ahead of
    /// any take. The commit guard refuses that result whole. NEGATIVE CONTROL: the merge itself does produce it.
    /// </summary>
    [Fact]
    public void APartiallyRefusedTake_AgainstAReleasedLease_CommitsNothing()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expired = PrebuiltBundleRetentionLease.Taken(PrebuiltBundleRetentionLeaseState.Lapsed, PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1), 61));
        var released = PrebuiltBundleRetentionLease.Released(expired);
        var take = PrebuiltBundleRetentionLease.Taken(expired, PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(3), 62));

        var (merged, refused) = MergeLosingClaim(expired, released, take);
        refused.Should().Contain("claim", "PRECONDITION: the claim changed on both sides");
        var partial = merged.Deserialize<PrebuiltBundleRetentionLeaseState>(Web)!;
        partial.Should().Be(released with { Generation = 2 },
            "NEGATIVE CONTROL: the generation, unchanged on the owner since the base, is applied without its claim");

        PrebuiltBundleRetentionLease.TransitionRefusal(released, partial).Should().NotBeNull(
            "a generation advances only with a new claim, so the partial result commits nothing");
        PrebuiltBundleRetentionLease.TransitionRefusal(released, PrebuiltBundleRetentionLease.Taken(released, take.Claim)).Should().BeNull(
            "the re-run take, the claim with the next generation, commits");
    }

    /// <summary>
    /// 🚨 #7459, a PARTIAL refusal against an ACTIVE lease: an expired base of the same generation (the owner
    /// lost that take and granted the generation to another holder) proposes the next generation; the claim is
    /// refused, the generation lands, and the valid holder's fence reads its own lease as lost. The commit guard
    /// refuses it. A stamp, a release and the take itself still commit.
    /// </summary>
    [Fact]
    public void APartiallyRefusedTake_AgainstAnActiveLease_CommitsNothing_AndKeepsTheHoldersFence()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lost = PrebuiltBundleRetentionLease.Taken(PrebuiltBundleRetentionLeaseState.Lapsed, PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1), 71));
        var holder = PrebuiltBundleRetentionLease.Taken(PrebuiltBundleRetentionLeaseState.Lapsed, PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(5), 72));
        var active = holder with { Claim = holder.Claim | PrebuiltBundleRetentionLease.StampedFlag, Replica = "silo-1", TakenAt = t };
        var take = PrebuiltBundleRetentionLease.Taken(lost, PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(3), 73));

        var (merged, refused) = MergeLosingClaim(lost, active, take);
        refused.Should().Contain("claim", "PRECONDITION: the claim changed on both sides");
        var partial = merged.Deserialize<PrebuiltBundleRetentionLeaseState>(Web)!;
        partial.Generation.Should().Be(2, "NEGATIVE CONTROL: the generation lands without its claim");
        PrebuiltBundleRetentionLease.IsHeldBy(partial, holder.Claim, holder.Generation).Should().BeFalse(
            "NEGATIVE CONTROL: …and the valid holder's fence would read its own lease as lost");

        PrebuiltBundleRetentionLease.TransitionRefusal(active, partial).Should().NotBeNull("the partial result commits nothing");
        PrebuiltBundleRetentionLease.TransitionRefusal(holder, active).Should().BeNull("a stamp keeps the claim and the generation");
        PrebuiltBundleRetentionLease.TransitionRefusal(active, PrebuiltBundleRetentionLease.Released(active)).Should().BeNull("a release keeps the generation");
        PrebuiltBundleRetentionLease.TransitionRefusal(active, take with { Generation = 1 }).Should().NotBeNull(
            "a new claim never lands in the generation it replaces");
    }

    /// <summary>The lease shape with a generation the serializer OMITS at <c>0</c> — the negative control above.</summary>
    private sealed record OmittingGenerationShape(long Claim, long Generation);

    /// <summary>
    /// 🚨 A stamp proposed from a STALE mirror — the stamper's base still carries its own claim while the
    /// owner already holds another replica's — exactly as <c>MeshNodePatchMerge</c> resolves it. NEGATIVE
    /// CONTROL first: a stamp that changes only the diagnostic strings carries no conflict on the claim,
    /// so it lands beside the other replica's claim while the stamper's own computed content still says
    /// it holds the lease — two sweeps.
    /// </summary>
    [Fact]
    public void AStaleStamp_ThatLeavesTheClaimAlone_LandsBesideAnotherReplicasClaim()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mine = PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1), 31);
        var staleBase = new PrebuiltBundleRetentionLeaseState(mine);
        var other = new PrebuiltBundleRetentionLeaseState(
            PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(2), 32) | PrebuiltBundleRetentionLease.StampedFlag, "silo-1", t.AddHours(1));
        var stringsOnly = staleBase with { Replica = "silo-0", TakenAt = t };

        var (live, refused) = MergeLosingClaim(staleBase, other, stringsOnly);

        refused.Should().NotContain("claim", "the stamp did not touch the claim, so nothing conflicts on it");
        live.Deserialize<PrebuiltBundleRetentionLeaseState>(Web)!.Holder.Should().Be(32);
        stringsOnly.Claim.Should().Be(mine, "…while the stamper's own computed content still reads its claim — held");
    }

    /// <summary>The fix: the stamp rewrites the claim (<see cref="PrebuiltBundleRetentionLease.StampedFlag"/>), so a stale stamp is refused WHOLE.</summary>
    [Fact]
    public void AStaleStamp_ThatRewritesTheClaim_IsRefusedWhole()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mine = PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1), 31);
        var staleBase = new PrebuiltBundleRetentionLeaseState(mine);
        var other = new PrebuiltBundleRetentionLeaseState(
            PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(2), 32) | PrebuiltBundleRetentionLease.StampedFlag, "silo-1", t.AddHours(1));
        var stamp = staleBase with { Claim = mine | PrebuiltBundleRetentionLease.StampedFlag, Replica = "silo-0", TakenAt = t };

        var (live, refused) = MergeLosingClaim(staleBase, other, stamp);

        refused.Should().Contain("claim", "the stamp's claim and the owner's both changed since the stale base");
        live.Deserialize<PrebuiltBundleRetentionLeaseState>(Web)!.Claim.Should().Be(other.Claim,
            "the other replica's claim stands, and the stamper's re-run fold reads it and does not sweep");
        new PrebuiltBundleRetentionLeaseState(mine | PrebuiltBundleRetentionLease.StampedFlag).Holder.Should().Be(31,
            "the stamped flag is not part of the holder's nonce");
    }

    [Fact]
    public void ThePassIsCutAtHalfItsHold_AndTheHoldCoversMostOfTheInterval()
    {
        PrebuiltBundleRetentionLease.HoldFor(TimeSpan.FromMinutes(5)).Should().Be(PrebuiltBundleRetentionLease.MinimumHold);
        PrebuiltBundleRetentionLease.HoldFor(TimeSpan.FromDays(1)).Should().Be(TimeSpan.FromHours(21.6));
        PrebuiltBundleRetentionLease.PassDeadline(TimeSpan.FromHours(2)).Should().Be(TimeSpan.FromHours(1));

        var until = new DateTimeOffset(2300, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var lease = new PrebuiltBundleRetentionLeaseState(PrebuiltBundleRetentionLease.ClaimFor(until, 12345));
        lease.Until.Should().Be(until, "the claim round-trips the hold's end");
        lease.Holder.Should().Be(12345, "…and the holder's nonce");
        PrebuiltBundleRetentionLeaseState.Lapsed.Until.Should().Be(DateTimeOffset.MinValue);
    }

    /// <summary>The lease shape a first cut used: a holder string beside a date — see the merge tests below.</summary>
    private sealed record SplitLeaseShape(string Holder, DateTimeOffset Until, DateTimeOffset? TakenAt = null, string? Replica = null);

    // The hub serializer's shape that matters here: camelCase, and default values OMITTED.
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    /// <summary>
    /// The owner's three-way merge of a LOSING claim — the writer's base is the lapsed lease, the owner
    /// already holds the winner's claim — exactly as <c>MeshNodePatchMerge</c> resolves it for a remote
    /// <c>Update</c>. Returns the owner's state after the merge and the leaves it refused.
    /// </summary>
    private static (System.Text.Json.Nodes.JsonObject Live, System.Collections.Immutable.ImmutableList<string> Refused) MergeLosingClaim<T>(
        T lapsed, T winner, T loser)
    {
        var baseNode = System.Text.Json.JsonSerializer.SerializeToNode(lapsed, Web)!.AsObject();
        var live = System.Text.Json.JsonSerializer.SerializeToNode(winner, Web)!.AsObject();
        var proposal = System.Text.Json.JsonSerializer.SerializeToNode(loser, Web)!.AsObject();
        // The writer ships the leaves it changed (RFC 7396) and its base at those leaves.
        var patch = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, value) in proposal)
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(value, baseNode[key]))
                patch[key] = value?.DeepClone();
        var refused = System.Collections.Immutable.ImmutableList<string>.Empty;
        MeshWeaver.Data.Serialization.MeshNodePatchMerge.Apply(
            live, patch, MeshWeaver.Data.Serialization.MeshNodePatchMerge.ExtractBaseValues(baseNode, patch),
            key => refused = refused.Add(key));
        return (live, refused);
    }

    /// <summary>
    /// 🚨 Deterministic root of the torn lease, and its NEGATIVE CONTROL: a holder string beside a date
    /// string is merged leaf by leaf, so a losing claim lands PART of itself — the owner keeps the
    /// winner's holder and takes the loser's other leaves. Measured live on two silos before the claim
    /// became one scalar (the owner held the winner's holder with the loser's hold end and replica).
    /// </summary>
    [Fact]
    public void ALosingClaim_OnASplitLeaseShape_IsMergedLeafByLeaf_AndTearsTheLease()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var winner = new SplitLeaseShape("aa11", t.AddHours(1), t, "silo-0");
        var loser = new SplitLeaseShape("bb22", t.AddHours(1.5), t.AddMinutes(30), "silo-1");

        var (live, _) = MergeLosingClaim(new SplitLeaseShape("", DateTimeOffset.MinValue), winner, loser);

        var merged = live.Deserialize<SplitLeaseShape>(Web)!;
        merged.Holder.Should().Be("aa11", "the holder strings overlap, so the loser's holder is refused");
        merged.Should().NotBe(winner,
            "…but the leaves the merge does not see as conflicting land anyway: the lease is torn");
    }

    /// <summary>
    /// The fix: the claim is one scalar, so a losing claim conflicts WHOLE — refused, nothing of it
    /// lands, and the writer's fold re-runs on the winner's state.
    /// </summary>
    [Fact]
    public void ALosingClaim_OnTheSingleScalarClaim_IsRefusedWhole()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var winner = new PrebuiltBundleRetentionLeaseState(
            PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1), 11), "silo-0", t);
        // A claimant writes ONLY the claim on top of the state it read (the lapsed lease).
        var loser = PrebuiltBundleRetentionLeaseState.Lapsed with
        {
            Claim = PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1.5), 22),
        };

        var (live, refused) = MergeLosingClaim(PrebuiltBundleRetentionLeaseState.Lapsed, winner, loser);

        refused.Should().Contain("claim", "a scalar changed on both sides is refused, which re-runs the loser's fold");
        live.Deserialize<PrebuiltBundleRetentionLeaseState>(Web).Should().Be(winner,
            "nothing of the losing claim may land");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the lapsed value: a lapsed claim of <c>0</c> — the default — is omitted
    /// from the writer's base by the hub serializer, the owner then has no conflict signal, and the
    /// losing claim LANDS over the winner's. Measured on two silos before the lapsed claim became
    /// <see cref="PrebuiltBundleRetentionLease.LapsedClaim"/>: both claimants settled as holders.
    /// </summary>
    [Fact]
    public void ALapsedClaimOfZero_IsOmittedFromTheBase_AndTheLosingClaimLands()
    {
        var t = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var zeroLapsed = new PrebuiltBundleRetentionLeaseState(0L);
        var winner = zeroLapsed with { Claim = PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1), 11) };
        var loser = zeroLapsed with { Claim = PrebuiltBundleRetentionLease.ClaimFor(t.AddHours(1.5), 22) };

        var (live, refused) = MergeLosingClaim(zeroLapsed, winner, loser);

        refused.Should().BeEmpty("the base carries no claim leaf, so there is nothing to refuse");
        live.Deserialize<PrebuiltBundleRetentionLeaseState>(Web)!.Holder.Should().Be(22,
            "the losing claim overwrote the winner's — the defect a non-default lapsed claim prevents");
    }
}
