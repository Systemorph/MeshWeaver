using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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

    /// <summary>The cache/portal hub every one of these destinations really is: a MULTIPLEXER that
    /// fronts one <c>sync/{streamId}</c> sub-hub per observed node (issue #5009).</summary>
    private const string Multiplexer = "cache/12xX8OXQIEmdwvf_ZZ8LjA";

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
            dispatcher.Enqueue(Destination, orderingKey: null, GatedLeg(i, starts, gates[i]), () => { });

        // Leg 0 starts…
        (await starts.Take(1).Timeout(10.Seconds()).Await(TestContext.Current.CancellationToken)).Should().Be(0);

        // …and NOTHING else does while leg 0 is still in flight. No positive signal exists for
        // "the second leg did not start", so the bounded absence IS the assertion.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            starts.Skip(1).Take(1).Timeout(1.Seconds()).Await(TestContext.Current.CancellationToken));

        for (var i = 0; i < gates.Length - 1; i++)
        {
            gates[i].OnNext(Unit.Default);
            gates[i].OnCompleted();
            (await starts.Skip(i + 1).Take(1).Timeout(10.Seconds()).Await(TestContext.Current.CancellationToken)).Should().Be(i + 1,
                "each leg is subscribed only after the one ahead of it has completed, in arrival order");
        }

        gates[^1].OnCompleted();
        var order = await starts.Take(3).ToList().Timeout(10.Seconds()).Await(TestContext.Current.CancellationToken);
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

        dispatcher.Enqueue(Destination, orderingKey: null, GatedLeg(0, starts, blocked), () => { });
        dispatcher.Enqueue(OtherDestination, orderingKey: null, GatedLeg(1, starts, free), () => { });

        var seen = await starts.Take(2).ToList().Timeout(10.Seconds()).Await(TestContext.Current.CancellationToken);
        seen.Should().Contain(1,
            "a stalled destination must not hold up any other destination's routing");
        seen.Should().HaveCount(2);

        blocked.OnCompleted();
        free.OnCompleted();
    }

    /// <summary>
    /// 🚨 ISSUE #5009 — THE BLOCKING SIDE OF THE CONTROL. Two frames of two DIFFERENT streams,
    /// handed in for the SAME destination, must both be subscribed even though the first never
    /// terminates.
    ///
    /// <para><b>Why this is the defect and not a nicety.</b> Every stream-routed address is a
    /// MULTIPLEXER. <c>cache/{meshId}</c> fronts one <c>sync/{streamId}</c> sub-hub per observed node
    /// — <c>DataExtensions.RouteStreamMessage</c> receives every frame at the cache hub's own address
    /// and forwards it internally by <c>StreamId</c> — so keying the FIFO on the address alone put a
    /// whole process's data-sync traffic on ONE channel with ONE in-flight
    /// <c>IPodHubGrain.Deliver</c> call. Where the destination is on another silo, that channel's
    /// service rate is one network round trip per frame, and memex-cloud measured the consequence:
    /// 62 of 64 in-flight legs queued behind one <c>cache/…</c> address, recurring for ~1.5 h, while
    /// the peer pod (local cache channel, therefore fast) reported deepest 0 and read as load.</para>
    ///
    /// <para>Before the fix this test times out on <c>starts.Take(2)</c>: leg 1 is never subscribed,
    /// because it is queued behind a leg of a stream it has no ordering relationship with.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task IndependentStreamsOnOneDestination_NeverWaitOnEachOther()
    {
        using var pool = new IoPool(8);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var starts = new ReplaySubject<int>();
        var blocked = new Subject<Unit>();
        var free = new Subject<Unit>();

        dispatcher.Enqueue(Multiplexer, "sync/stream-a", GatedLeg(0, starts, blocked), () => { });
        dispatcher.Enqueue(Multiplexer, "sync/stream-b", GatedLeg(1, starts, free), () => { });

        var seen = await starts.Take(2).ToList().Timeout(10.Seconds())
            .Await(TestContext.Current.CancellationToken);
        seen.Should().HaveCount(2,
            "two frames of two DIFFERENT streams share a destination HUB, not an ordering "
            + "relationship — a stalled leg of one stream must not hold up any other stream's "
            + "routing, exactly as a stalled destination must not hold up another destination's");
        seen.Should().Contain(1);

        var snapshot = dispatcher.QueueSnapshot();
        snapshot.Channels.Should().Be(2, "one channel per stream");
        snapshot.Destinations.Should().Be(1, "both channels front the same multiplexer hub");
        snapshot.Deepest.Should().Be(0,
            "nothing is queued behind anything — before #5009 this same traffic reported a deepest "
            + "per-destination queue of 1, i.e. head-of-line blocking, over two unrelated streams");

        blocked.OnCompleted();
        free.OnCompleted();
    }

    /// <summary>
    /// 🚨 THE CORRECTNESS SIDE OF THE CONTROL — the half a finer channel key could have broken.
    /// Frames of ONE stream, on a multiplexer destination that is now serving many channels, must
    /// STILL be subscribed strictly one at a time and in arrival order.
    ///
    /// <para>That is the whole reason the FIFO exists: the delta protocol's receive-side
    /// monotonicity guard (<c>SynchronizationStream.UpdateStream</c>) discards a frame whose owner
    /// version is below the mirror's current version and nothing re-sends it, so two frames of one
    /// stream swapping means the earlier one is dropped as stale FOREVER. Narrowing the channel from
    /// the destination to (destination, stream) is only sound because it narrows to exactly the
    /// domain that guard operates on — which is what this test asserts.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task FramesOfOneStream_OnAMultiplexer_StillSubscribeInOrderOneAtATime()
    {
        const string OneStream = "sync/the-only-stream";
        using var pool = new IoPool(8);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var starts = new ReplaySubject<int>();
        var gates = new[] { new Subject<Unit>(), new Subject<Unit>(), new Subject<Unit>() };

        for (var i = 0; i < gates.Length; i++)
            dispatcher.Enqueue(Multiplexer, OneStream, GatedLeg(i, starts, gates[i]), () => { });

        (await starts.Take(1).Timeout(10.Seconds()).Await(TestContext.Current.CancellationToken))
            .Should().Be(0);

        // No positive signal exists for "the second frame did not start", so the bounded absence IS
        // the assertion — and it is kept short on purpose (a negative assertion always spends its
        // whole window).
        await Assert.ThrowsAsync<TimeoutException>(() =>
            starts.Skip(1).Take(1).Timeout(1.Seconds()).Await(TestContext.Current.CancellationToken));

        dispatcher.QueueSnapshot().Deepest.Should().Be(gates.Length - 1,
            "frames of the SAME stream queue behind one another — and THIS is what a non-zero "
            + "deepest now means in the saturation report");

        for (var i = 0; i < gates.Length - 1; i++)
        {
            gates[i].OnNext(Unit.Default);
            gates[i].OnCompleted();
            (await starts.Skip(i + 1).Take(1).Timeout(10.Seconds())
                    .Await(TestContext.Current.CancellationToken))
                .Should().Be(i + 1, "the next frame of a stream is subscribed only after the one "
                    + "ahead of it has completed");
        }

        gates[^1].OnCompleted();
        var order = await starts.Take(3).ToList().Timeout(10.Seconds())
            .Await(TestContext.Current.CancellationToken);
        order.Should().Equal([0, 1, 2],
            "a stream's channel must preserve the routing grain's arrival order — the routing grain's "
            + "turn is the last point at which that order is authoritative");
    }

    /// <summary>
    /// The channel key is a (destination, stream) PAIR, never the two concatenated. A concatenation
    /// makes <c>("a", "b/c")</c> and <c>("a/b", "c")</c> the same channel for any single delimiter a
    /// mesh address may contain — and mesh addresses contain <c>/</c> and <c>~</c> — which would
    /// silently serialise two unrelated streams again, or (worse) let two frames of one stream
    /// overtake each other. Deliberately asserted rather than argued: the pairing is invisible from
    /// outside unless something reads it.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ChannelKeyIsAPair_NotAConcatenation()
    {
        using var pool = new IoPool(8);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var starts = new ReplaySubject<int>();
        var blocked = new Subject<Unit>();
        var free = new Subject<Unit>();

        dispatcher.Enqueue("cache/a", "b/c", GatedLeg(0, starts, blocked), () => { });
        dispatcher.Enqueue("cache/a/b", "c", GatedLeg(1, starts, free), () => { });

        var seen = await starts.Take(2).ToList().Timeout(10.Seconds())
            .Await(TestContext.Current.CancellationToken);
        seen.Should().HaveCount(2,
            "these are two different destinations and two different streams; only a key built by "
            + "concatenating them with '/' would collapse them into one channel");
        dispatcher.QueueSnapshot().Destinations.Should().Be(2);

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

        dispatcher.Enqueue(Destination, orderingKey: null, GatedLeg(0, starts, gate), () => completed.OnNext(Unit.Default));
        await starts.Take(1).Timeout(10.Seconds()).Await(TestContext.Current.CancellationToken);
        dispatcher.ActiveChannels.Should().Be(1);

        var drained = completed.Take(1).Timeout(10.Seconds()).Await(TestContext.Current.CancellationToken);
        gate.OnCompleted();
        await drained;

        // The entry is removed on the drain that follows the leg's completion — observe the
        // released state rather than asserting on a single instant.
        var released = await Observable.Interval(20.Milliseconds()).StartWith(0L)
            .Select(_ => dispatcher.ActiveChannels)
            .Where(count => count == 0)
            .FirstAsync()
            .Timeout(10.Seconds())
            .Await(TestContext.Current.CancellationToken);
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

        var seen = await starts.Take(3).ToList().Timeout(10.Seconds()).Await(TestContext.Current.CancellationToken);
        seen.Should().HaveCount(3,
            "the unordered dispatch subscribes all three legs even though none has completed — "
            + "there is no per-destination ordering, which is the defect OrderedRouteDispatcher fixes");

        foreach (var gate in gates) gate.OnCompleted();
        foreach (var subscription in subscriptions) subscription.Dispose();
    }
}
