using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>A three-silo cluster: two that drain and one survivor (#6865).</summary>
public class ThreeSiloDrainFixture : TwoSiloCacheUpdateFixture
{
    /// <inheritdoc />
    protected override short SiloCount => 3;
}

/// <summary>Shared steps for the #6865 drain tests.</summary>
internal static class DrainTestSteps
{
    public static IServiceProvider SiloServices(TestCluster cluster, int index)
        => ((InProcessSiloHandle)cluster.Silos[index]).SiloHost.Services;

    /// <summary>Creates a node and activates its hub on the given silo (prefer-local placement).</summary>
    public static async Task<string> NodeOn(IServiceProvider silo, string ns, string name, CancellationToken ct)
    {
        var hub = silo.GetRequiredService<IMessageHub>();
        var path = $"{ns}/{name}";
        var access = silo.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => silo.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode(name, ns) { Name = name, NodeType = "Markdown", State = MeshNodeState.Active }))
            .FirstAsync().Await(ct);
        await access.RunAsSystem(() => hub.NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence)
            .Emit($"{name} answers on its silo — the precondition", ct);
        hub.GetHostedHub(new Address(path), HostedHubCreation.Never)
            .Should().NotBeNull($"{name} must be activated on the silo that drains");
        return path;
    }

    /// <summary>True when the silo hosts the hub at <paramref name="path"/> right now.</summary>
    public static bool Hosts(IServiceProvider silo, string path) =>
        silo.GetRequiredService<IMessageHub>().GetHostedHub(new Address(path), HostedHubCreation.Never) is not null;

    /// <summary>Begins <paramref name="drainer"/>'s drain and waits until <paramref name="observer"/>'s roster knows it.</summary>
    public static async Task BeginDrainSeenBy(
        IServiceProvider drainer, SiloAddress drainerAddress, IEnumerable<IServiceProvider> observers, CancellationToken ct)
    {
        drainer.GetRequiredService<HostDrainSignal>().Begin().Should().BeTrue("this silo's drain begins now");
        foreach (var observer in observers)
            await observer.GetRequiredService<ClusterDrainRoster>().Changes
                .Where(set => set.Contains(drainerAddress))
                .Should().Within(TestTimeouts.CrossSilo)
                .Emit($"every peer learns that {drainerAddress} drains — the cluster-wide drain signal", ct);
    }

    /// <summary>
    /// How long the address must stay put to prove it is not handed off again: several drain-watch
    /// ticks, the period at which a draining singleton re-checks where it should be.
    /// </summary>
    public static TimeSpan SettledFor => TimeSpan.FromTicks(MessageHubGrain.DrainWatchPeriod.Ticks * 4);
}

/// <summary>
/// Issue #6865. During a roll in which TWO old pods drained at once, an instance singleton
/// (<c>RelocateOnDrain</c>, e.g. <c>Hosting/PlatformBuilds</c>) was torn down and re-created every
/// ~2.2 s for 24 minutes, alternating between the two draining pods; every copy began a fresh pass whose
/// writes died with the copy, so the rows it maintains went stale for the whole drain.
///
/// <para><b>Root cause.</b> The hand-off target came from Orleans membership, in which a draining pod is
/// still <c>Active</c>; and the target was spread by <c>string.GetHashCode</c>, randomised per process,
/// so the two draining pods had no reason to agree. <b>Contract:</b> a draining silo tells its peers
/// (<see cref="ClusterDrainRoster"/>), a hand-off never targets a draining silo, and the target is
/// ranked by a fixed hash.</para>
/// </summary>
public class TwoSilosDrainingAtOnceTest(ThreeSiloDrainFixture fixture) : IClassFixture<ThreeSiloDrainFixture>
{
    /// <summary>
    /// Silos B and C drain; silo A does not. Eight singletons on B — enough that, with draining peers
    /// counted as targets, some would almost surely be handed to C (each picks C or A: 1 in 256 that
    /// none does). Every one settles on A, is never placed on C, and is not handed off again.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task SingletonsOfADrainingSilo_SettleOnTheSurvivor_NeverOnTheOtherDrainingSilo()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().Be(3, "two silos drain and one survives");

        var siloA = DrainTestSteps.SiloServices(cluster, 0);
        var siloB = DrainTestSteps.SiloServices(cluster, 1);
        var siloC = DrainTestSteps.SiloServices(cluster, 2);
        var silos = new[] { siloA, siloB, siloC };

        var ns = $"drain-two-{Guid.NewGuid():N}";
        var singletons = new List<string>();
        for (var i = 0; i < 8; i++)
            singletons.Add(await DrainTestSteps.NodeOn(siloB, ns, $"Singleton{i}", ct));
        // What RelocateOnDrain registers, on EVERY silo — a hand-off lands the hub on another silo,
        // which must know it is a singleton too.
        foreach (var silo in silos)
        foreach (var path in singletons)
            silo.GetRequiredService<HostDrainSignal>().Relocate(new Address(path));

        // Sampled from before the drains begin: was any singleton ever activated on C?
        var everOnC = 0;
        using var sampler = Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Subscribe(_ =>
            {
                if (singletons.Any(p => DrainTestSteps.Hosts(siloC, p)))
                    Interlocked.Exchange(ref everOnC, 1);
            });

        // C begins to drain first, and every peer learns it; then B — the pod the singletons are on.
        await DrainTestSteps.BeginDrainSeenBy(siloC, cluster.Silos[2].SiloAddress, [siloA, siloB], ct);
        await DrainTestSteps.BeginDrainSeenBy(siloB, cluster.Silos[1].SiloAddress, [siloA, siloC], ct);

        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .Where(_ => singletons.All(p => !DrainTestSteps.Hosts(siloB, p)))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("every singleton leaves the draining silo B", ct);

        var accessA = siloA.GetRequiredService<AccessService>();
        var hubA = siloA.GetRequiredService<IMessageHub>();
        foreach (var path in singletons)
            await accessA.RunAsSystem(() => hubA.NodeOperationIssuingHub()
                    .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
                .Should().Within(TestTimeouts.CrossSilo)
                .Emit($"{path} answers after the hand-off", ct);

        // 🚨 Settled: for several drain-watch ticks every singleton stays on A — no further hand-off.
        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .Where(_ => singletons.Any(p => !DrainTestSteps.Hosts(siloA, p)))
            .Take(1)
            .Should().NotEmit(DrainTestSteps.SettledFor,
                "🚨 every singleton settles on A, the one silo that is not draining, and is NOT handed off again");

        Volatile.Read(ref everOnC).Should().Be(0,
            "🚨 no singleton was ever handed to C — a silo that announced its drain is never a hand-off target (#6865)");
        singletons.Should().AllSatisfy(p => DrainTestSteps.Hosts(siloB, p).Should().BeFalse("and none went back to B"));
    }
}

/// <summary>
/// The other half of #6865: when EVERY silo drains, there is nowhere to hand a singleton to. It must
/// STAY where it is and keep answering — the documented no-survivor behaviour — instead of moving onto
/// the other draining silo and being handed straight back, which is the ping-pong the incident logged.
/// </summary>
public class AllSilosDrainingKeepTheirSingletonTest(TwoSiloCacheUpdateFixture fixture) : IClassFixture<TwoSiloCacheUpdateFixture>
{
    /// <summary>Both silos drain; the singleton on B stays on B, is never placed on A, and answers.</summary>
    [Fact(Timeout = 180_000)]
    public async Task ASingleton_StaysPut_WhenEveryOtherSiloDrainsToo()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(170));
        var ct = deadline.Token;
        var cluster = fixture.Cluster;
        cluster.Silos.Count.Should().Be(2);

        var siloA = DrainTestSteps.SiloServices(cluster, 0);
        var siloB = DrainTestSteps.SiloServices(cluster, 1);
        var path = await DrainTestSteps.NodeOn(siloB, $"drain-all-{Guid.NewGuid():N}", "Singleton", ct);
        foreach (var silo in new[] { siloA, siloB })
            silo.GetRequiredService<HostDrainSignal>().Relocate(new Address(path));

        var everOnA = 0;
        using var sampler = Observable.Interval(TimeSpan.FromMilliseconds(50))
            .Subscribe(_ =>
            {
                if (DrainTestSteps.Hosts(siloA, path))
                    Interlocked.Exchange(ref everOnA, 1);
            });

        await DrainTestSteps.BeginDrainSeenBy(siloA, cluster.Silos[0].SiloAddress, [siloB], ct);
        await DrainTestSteps.BeginDrainSeenBy(siloB, cluster.Silos[1].SiloAddress, [siloA], ct);

        await Observable.Interval(TimeSpan.FromMilliseconds(100))
            .Where(_ => !DrainTestSteps.Hosts(siloB, path))
            .Take(1)
            .Should().NotEmit(DrainTestSteps.SettledFor,
                "🚨 with every other silo draining there is nowhere to go — the singleton stays on B");
        Volatile.Read(ref everOnA).Should().Be(0,
            "🚨 it was never handed to A, which is draining too — that hop is the ping-pong of #6865");

        var accessB = siloB.GetRequiredService<AccessService>();
        await accessB.RunAsSystem(() => siloB.GetRequiredService<IMessageHub>().NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("and it keeps answering from where it is", ct);
    }
}

/// <summary>The hand-off target is the same answer in every process (#6865).</summary>
public class DrainHandOffTargetTest
{
    /// <summary>
    /// Pinned value: a hash randomised per process (<c>string.GetHashCode</c>, what the spread used to
    /// be) could not produce the same number in every test run, so a pinned constant is what proves
    /// independent pods compute the same ranking.
    /// </summary>
    [Fact]
    public void TheScore_IsAFixedFunction_IdenticalInEveryProcess() =>
        DrainHandOffTarget.Score("Hosting/PlatformBuilds", "10.0.0.1:11111@1").Should().Be(PinnedScore);

    private const ulong PinnedScore = 116423680924464001UL;

    /// <summary>The order the candidates arrive in never changes the pick.</summary>
    [Fact]
    public void TheChoice_DoesNotDependOnCandidateOrder()
    {
        var silos = Enumerable.Range(1, 5)
            .Select(i => SiloAddress.FromParsableString($"10.0.0.{i}:11111@1"))
            .ToArray();
        foreach (var key in new[] { "Hosting/PlatformBuilds", "a/b", "x" })
        {
            var forward = DrainHandOffTarget.Choose(key, silos);
            var backward = DrainHandOffTarget.Choose(key, silos.Reverse());
            forward.Should().NotBeNull();
            backward.Should().Be(forward, $"every process ranks the candidates for '{key}' the same way");
        }
        DrainHandOffTarget.Choose("k", []).Should().BeNull("no candidate, no target — the address stays put");
    }
}

/// <summary>
/// #6875 review: a drain notice whose activation lands on the WRONG silo (a stale directory entry, or a
/// placement that did not honour the hint) must still reach the recipient — with membership held
/// unchanged, so no re-announcement can rescue it. The activation migrates to the silo its key names
/// and records the carried notice there.
/// </summary>
public class AMisplacedDrainNoticeReachesItsRecipientTest(TwoSiloCacheUpdateFixture fixture)
    : IClassFixture<TwoSiloCacheUpdateFixture>
{
    /// <summary>The notice for silo A is placed on silo B; A's roster still records it.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ANoticeThatLandsOnTheWrongSilo_IsCarriedToTheRecipient()
    {
        var ct = TestContext.Current.CancellationToken;
        var cluster = fixture.Cluster;
        var siloA = DrainTestSteps.SiloServices(cluster, 0);
        var siloB = DrainTestSteps.SiloServices(cluster, 1);
        var recipient = cluster.Silos[0].SiloAddress;
        var draining = SiloAddress.FromParsableString("10.9.9.9:11111@1");

        // Placed on B: the hint names B, not the recipient its key names — the misplacement under test.
        var grain = siloB.GetRequiredService<IGrainFactory>()
            .GetGrain<MeshWeaver.Connection.Orleans.ISiloDrainNoticeGrain>(recipient.ToParsableString());
        var hintKey = global::Orleans.Runtime.Placement.IPlacementDirector.PlacementHintKey;
        bool recordedOnTheSpot;
        RequestContext.Set(hintKey, cluster.Silos[1].SiloAddress);
        try
        {
            recordedOnTheSpot = await grain.Draining(draining.ToParsableString());
        }
        finally
        {
            RequestContext.Remove(hintKey);
        }
        recordedOnTheSpot.Should().BeFalse("the precondition: the notice landed on silo B, not on its recipient A");

        await siloA.GetRequiredService<ClusterDrainRoster>().Changes
            .Where(set => set.Contains(draining))
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("🚨 the misplaced notice is carried to silo A and recorded there — membership never changed, "
                  + "so nothing else would ever tell A", ct);
        siloB.GetRequiredService<ClusterDrainRoster>().IsDraining(draining)
            .Should().BeFalse("it is recorded on the recipient, not on the silo it landed on");
    }
}

/// <summary>
/// #6875 review: <see cref="ClusterDrainRoster.Changes"/> must serialise its initial emission with the
/// change stream — an observer is never called concurrently, and never receives an older snapshot after
/// a newer one, even when drain notices arrive while it is subscribing.
/// </summary>
public class ClusterDrainRosterChangesTest
{
    /// <summary>Notices race the subscription; every observer sees serial, never-shrinking snapshots.</summary>
    [Fact]
    public void Changes_AreSerialAndInOrder_WhileNoticesRaceTheSubscription()
    {
        var silos = Enumerable.Range(1, 40)
            .Select(i => SiloAddress.FromParsableString($"10.1.0.{i}:11111@1"))
            .ToArray();
        var overlaps = 0;
        var regressions = 0;
        for (var round = 0; round < 300; round++)
        {
            using var roster = new ClusterDrainRoster(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ClusterDrainRoster>.Instance);
            var inFlight = 0;
            var lastSize = -1;
            IDisposable? subscription = null;
            Parallel.Invoke(
                () => subscription = roster.Changes.Subscribe(set =>
                {
                    if (Interlocked.Increment(ref inFlight) > 1)
                        Interlocked.Increment(ref overlaps);
                    // Widen the window an overlapping call would need.
                    Thread.SpinWait(200);
                    if (set.Count < Volatile.Read(ref lastSize))
                        Interlocked.Increment(ref regressions);
                    Volatile.Write(ref lastSize, set.Count);
                    Interlocked.Decrement(ref inFlight);
                }),
                () =>
                {
                    foreach (var silo in silos)
                        roster.MarkDraining(silo);
                });
            subscription!.Dispose();
            Volatile.Read(ref lastSize).Should().Be(silos.Length, "the last snapshot an observer sees is the complete roster");
        }
        overlaps.Should().Be(0, "an observer of the roster is never called concurrently");
        regressions.Should().Be(0, "an observer never receives an older (smaller) snapshot after a newer one");
    }
}
