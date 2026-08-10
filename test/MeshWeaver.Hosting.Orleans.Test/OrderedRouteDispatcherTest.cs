using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 The routing contract this pins: deliveries to the SAME destination are posted in the order
/// the routing grain received them.
///
/// <para><b>Why it is a correctness requirement.</b> Data sync is a DELTA protocol with a
/// receive-side monotonicity guard — <c>SynchronizationStream.UpdateStream</c> discards any frame
/// whose owner version is below the mirror's current version, and nothing re-sends it. Reorder two
/// frames of one stream and the earlier one (which may be the one carrying the layout area's actual
/// content) is dropped as "stale" FOREVER: the subscriber keeps the "Building layout…" base frame,
/// its wait dies on its own timeout, and NOTHING is logged above Debug on either side. Measured on
/// a single Orleans test-suite run before this fix: 854 stream-routed deliveries, 46 destinations
/// with out-of-order posts, 115 inverted pairs — which is exactly the "one random layout-area
/// subscribe times out per run" flake.</para>
///
/// <para><see cref="UnorderedPoolDispatch_StartsEveryLegConcurrently"/> is the negative control: it
/// pins the behaviour of the shape this replaced (one <c>SubscribeThroughPool</c> per delivery),
/// which starts every leg at once and therefore cannot preserve any order.</para>
/// </summary>
public class OrderedRouteDispatcherTest(ITestOutputHelper output) : TestBase(output)
{
    private const string Destination = "client/subscriber-1";
    private const string OtherDestination = "client/subscriber-2";

    /// <summary>A leg that announces its subscribe on <paramref name="starts"/> and terminates only
    /// when <paramref name="gate"/> fires — so "did the next leg start?" is directly observable.</summary>
    private static IObservable<Unit> GatedLeg(int id, IObserver<int> starts, IObservable<Unit> gate) =>
        Observable.Create<Unit>(observer =>
        {
            starts.OnNext(id);
            return gate.Take(1).Subscribe(observer);
        });

    [Fact(Timeout = 30_000)]
    public async Task SameDestination_SubscribesLegsInOrder_OneAtATime()
    {
        using var pool = new IoPool(8);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var starts = new ReplaySubject<int>();
        var gates = new[] { new Subject<Unit>(), new Subject<Unit>(), new Subject<Unit>() };

        for (var i = 0; i < gates.Length; i++)
            dispatcher.Enqueue(Destination, GatedLeg(i, starts, gates[i]), () => { });

        // Leg 0 starts…
        (await starts.Take(1).Timeout(10.Seconds()).ToTask()).Should().Be(0);

        // …and NOTHING else does while leg 0 is still in flight. No positive signal exists for
        // "the second leg did not start", so the bounded absence IS the assertion.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            starts.Skip(1).Take(1).Timeout(1.Seconds()).ToTask());

        for (var i = 0; i < gates.Length - 1; i++)
        {
            gates[i].OnNext(Unit.Default);
            gates[i].OnCompleted();
            (await starts.Skip(i + 1).Take(1).Timeout(10.Seconds()).ToTask()).Should().Be(i + 1,
                "each leg is subscribed only after the one ahead of it has completed, in arrival order");
        }

        gates[^1].OnCompleted();
        var order = await starts.Take(3).ToList().Timeout(10.Seconds()).ToTask();
        order.Should().Equal([0, 1, 2], "the destination's FIFO must preserve the routing grain's arrival order");
    }

    [Fact(Timeout = 30_000)]
    public async Task DifferentDestinations_NeverWaitOnEachOther()
    {
        using var pool = new IoPool(8);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var starts = new ReplaySubject<int>();
        var blocked = new Subject<Unit>();
        var free = new Subject<Unit>();

        dispatcher.Enqueue(Destination, GatedLeg(0, starts, blocked), () => { });
        dispatcher.Enqueue(OtherDestination, GatedLeg(1, starts, free), () => { });

        var seen = await starts.Take(2).ToList().Timeout(10.Seconds()).ToTask();
        seen.Should().Contain(1,
            "a stalled destination must not hold up any other destination's routing");
        seen.Should().HaveCount(2);

        blocked.OnCompleted();
        free.OnCompleted();
    }

    [Fact(Timeout = 30_000)]
    public async Task DrainedDestination_IsReleased_SoTheSiloHoldsNoPerAddressState()
    {
        using var pool = new IoPool(8);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var starts = new ReplaySubject<int>();
        var gate = new Subject<Unit>();
        var completed = new Subject<Unit>();

        dispatcher.Enqueue(Destination, GatedLeg(0, starts, gate), () => completed.OnNext(Unit.Default));
        await starts.Take(1).Timeout(10.Seconds()).ToTask();
        dispatcher.ActiveDestinations.Should().Be(1);

        var drained = completed.Take(1).Timeout(10.Seconds()).ToTask();
        gate.OnCompleted();
        await drained;

        // The entry is removed on the drain that follows the leg's completion — observe the
        // released state rather than asserting on a single instant.
        var released = await Observable.Interval(20.Milliseconds()).StartWith(0L)
            .Select(_ => dispatcher.ActiveDestinations)
            .Where(count => count == 0)
            .FirstAsync()
            .Timeout(10.Seconds())
            .ToTask();
        released.Should().Be(0,
            "a destination holds a FIFO entry only while it has work in flight — a silo that has "
            + "served millions of short-lived portal/{user} addresses must retain none of them");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the shape that was there before: every delivery handed to the pool as its
    /// own <c>SubscribeThroughPool</c> leg. All three legs start while none has completed, i.e. the
    /// pool imposes no order at all, which is what let two frames of one sync stream swap places.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task UnorderedPoolDispatch_StartsEveryLegConcurrently()
    {
        using var pool = new IoPool(8);
        var starts = new ReplaySubject<int>();
        var gates = new[] { new Subject<Unit>(), new Subject<Unit>(), new Subject<Unit>() };
        var subscriptions = new List<IDisposable>();

        for (var i = 0; i < gates.Length; i++)
            subscriptions.Add(pool.SubscribeThroughPool(GatedLeg(i, starts, gates[i]))
                .Subscribe(_ => { }, _ => { }));

        var seen = await starts.Take(3).ToList().Timeout(10.Seconds()).ToTask();
        seen.Should().HaveCount(3,
            "the unordered dispatch subscribes all three legs even though none has completed — "
            + "there is no per-destination ordering, which is the defect OrderedRouteDispatcher fixes");

        foreach (var gate in gates) gate.OnCompleted();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}
