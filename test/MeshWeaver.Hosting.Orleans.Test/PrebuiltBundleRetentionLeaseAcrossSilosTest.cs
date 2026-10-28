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

        public IObservable<int> Run() => Observable.Defer(() =>
        {
            Interlocked.Increment(ref entries);
            Started.OnNext(Unit.Default);
            Started.OnCompleted();
            return Release.Select(_ => replica);
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
                .RunLeased(hubs[index], new ManualClock(now), Hold, $"silo-{index}", passes[index].Run, passes[index].OnSkipped)
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

        await Sees(hubs[loserIndex], lease => lease.Claim == PrebuiltBundleRetentionLease.LapsedClaim, ct);
        var next = new ControlledPass(loserIndex);
        next.Release.OnNext(Unit.Default);
        next.Release.OnCompleted();
        var nextResult = await PrebuiltBundleRetentionLease
            .RunLeased(hubs[loserIndex], new ManualClock(now), Hold, $"silo-{loserIndex}", next.Run, next.OnSkipped)
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
            verdict => throw new InvalidOperationException($"PRECONDITION: the lease is free at a fresh epoch, yet the pass skipped: {verdict}"));

        // …and started two holds later, when the pass ahead of it finished.
        clock.Advance(Hold * 2);
        var startedAt = queuedAt + Hold * 2;
        var held = await attempt.FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        held.Until.Should().Be(startedAt + Hold,
            "the hold must cover the pass from its START — a hold dated from the tick would already have ended");
        held.TakenAt.Should().Be(startedAt);

        // The finished pass released the lease; leave it lapsed for the other tests on this cluster.
        await Sees(hub, lease => lease.Claim == PrebuiltBundleRetentionLease.LapsedClaim, ct);
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

        var stillOurs = await PrebuiltBundleRetentionLease.Stamp(hubA, claimA, "silo-0", t)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        stillOurs.Held.Should().BeTrue("NEGATIVE CONTROL: a stamp over its own claim holds the lease");

        // A pauses across its hold's end; B takes the lapsed lease.
        await Sees(hubB, lease => lease.Holder == Paused, ct);
        var taken = await PrebuiltBundleRetentionLease.Take(hubB, t + Hold + TimeSpan.FromSeconds(1), Hold, Taker, "silo-1")
            .Timeout(TestTimeouts.Convergence).Await(ct);
        taken.Held.Should().BeTrue("PRECONDITION: A's hold has ended, so B takes the lease");

        // A resumes and runs its stamp.
        await Sees(hubA, lease => lease.Holder == Taker, ct);
        var resumed = await PrebuiltBundleRetentionLease.Stamp(hubA, claimA, "silo-0", t)
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
                late.Run, verdict => throw new InvalidOperationException($"PRECONDITION: the lease is free at a fresh epoch: {verdict}"))
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
        await Sees(hub, lease => lease.Claim == PrebuiltBundleRetentionLease.LapsedClaim, ct);

        var onTime = new ControlledPass(0);
        onTime.Release.OnNext(Unit.Default);
        onTime.Release.OnCompleted();
        var ran = await PrebuiltBundleRetentionLease
            .RunLeased(hub, new SteppingClock(t + Hold, t + Hold + TimeSpan.FromMinutes(1)), Hold, "silo-ontime",
                onTime.Run, verdict => throw new InvalidOperationException($"PRECONDITION: the lease was released: {verdict}"))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        ran.Should().Be(0, "NEGATIVE CONTROL: with the deadline still ahead the pass runs");
        onTime.Entries.Should().Be(1);
        await Sees(hub, lease => lease.Claim == PrebuiltBundleRetentionLease.LapsedClaim, ct);
    }

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
