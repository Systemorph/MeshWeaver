using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// What <c>RoutingGrain</c>'s in-flight route count does and does NOT mean — issues #1172 / #1284.
///
/// <para><b>The claim under test.</b> #1172 reads "64 route dispatches are in flight … and not
/// terminating" as proof that "a delivery leg is not completing", and reads the number 64 itself as
/// Orleans' <c>NonReentrancyQueueSize</c> limit. Both are wrong, and this test pins why:</para>
///
/// <list type="number">
///   <item><b>64 is not a bound.</b> It is <c>RoutingSaturationReport.SaturationThreshold</c>, a MeshWeaver
///     constant, and it gates a LOG LINE only — the routing <see cref="IIoPool"/> is capped at 256
///     and nothing refuses, queues or throttles at 64. The reason prod always printed exactly 64 is
///     that the report latches on the single increment that crosses the line.</item>
///   <item><b>The count cannot tell "stuck" from "busy".</b> A slot is claimed at DISPATCH — for the
///     stream branch at ENQUEUE, before the leg is subscribed at all — so 64 independent legs all
///     progressing normally and 1 wedged destination with 63 legs stacked behind it produce the
///     IDENTICAL number. This test builds both and asserts the counts are equal.</item>
/// </list>
///
/// <para><b>What DOES tell them apart</b> is <c>OrderedRouteDispatcher.QueueSnapshot()</c>:
/// <c>(64 channels, deepest 0)</c> is breadth — every leg is executing, nothing waits on
/// anything. <c>(1 channel, deepest 63)</c> is head-of-line blocking — 63 legs are waiting on a
/// leg. The saturation report now carries that pair, so the next occurrence is diagnosable from the
/// log line instead of from a profiler on a live pod.</para>
///
/// <para>🚨 <b>A channel is (destination, stream), not a destination — issue #5009.</b>
/// <see cref="OneMultiplexerDestination_ManyStreams_IsBreadthNotHeadOfLine"/> is the third shape,
/// and the one memex-cloud actually reported for ~1.5 h: 64 legs to ONE cache hub, 62 of them
/// queued, which read as head-of-line blocking because the channel key was the address. They were
/// 64 INDEPENDENT streams that had no reason to wait on each other, and after #5009 that shape
/// reports as breadth over one destination.</para>
///
/// <para>Deterministic and cluster-free: legs are <see cref="Subject{T}"/>s, so "in flight" and
/// "completed" are decided by the test, never by a timer.</para>
/// </summary>
public class RoutingBackpressureShapeTest
{
    /// <summary>
    /// The bounded poll window. <see cref="TestTimeouts.Quick"/> rather than a literal: these legs are
    /// in-memory <see cref="Subject{T}"/>s with no mesh and no cluster, which is exactly the "local
    /// settle" that bound names, and it scales with the runner instead of guessing at it.
    /// </summary>
    private static readonly TimeSpan Budget = TestTimeouts.Quick;

    /// <summary>Mirrors <c>RoutingSaturationReport.SaturationThreshold</c> — the value every prod report printed.</summary>
    private const int Legs = 64;

    /// <summary>
    /// Drives <paramref name="legs"/> legs through the dispatcher, one per destination returned by
    /// <paramref name="destinationOf"/>, and returns the in-flight count plus the queue snapshot at
    /// the moment they are all enqueued. Bookkeeping is byte-for-byte what <c>RoutingGrain</c> does:
    /// increment at enqueue, decrement in the leg-completed callback.
    /// </summary>
    private static (int InFlight, int Channels, int Destinations, int Deepest, Subject<Unit>[] Legs, Func<int> Completed)
        Enqueue(int legs, Func<int, string> destinationOf, OrderedRouteDispatcher dispatcher,
            Func<int, string?>? streamOf = null)
    {
        var subjects = Enumerable.Range(0, legs).Select(_ => new Subject<Unit>()).ToArray();
        var inFlight = 0;
        var completed = 0;

        foreach (var (subject, i) in subjects.Select((s, i) => (s, i)))
        {
            Interlocked.Increment(ref inFlight);
            dispatcher.Enqueue(
                destinationOf(i),
                streamOf?.Invoke(i),
                subject.AsObservable(),
                () =>
                {
                    Interlocked.Decrement(ref inFlight);
                    Interlocked.Increment(ref completed);
                });
        }

        var (channels, destinations, deepest, _) = dispatcher.QueueSnapshot();
        return (Volatile.Read(ref inFlight), channels, destinations, deepest, subjects,
            () => Volatile.Read(ref completed));
    }

    /// <summary>
    /// 🚨 THE REGRESSION GUARD for the misreading that produced #1172 and #1284. Two opposite
    /// situations — total breadth and total head-of-line blocking — must yield the SAME in-flight
    /// count, and must be separated by the queue snapshot the report now carries.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void SameInFlightCount_MeansBothBusyAndBlocked_AndOnlyTheQueueSnapshotTellsThemApart()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = new IoPool(256);   // the Routing pool's real cap — never the binding constraint at 64

        // ── Shape A: 64 destinations, one leg each. Every leg is executing; nothing waits on anything.
        var spread = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var a = Enqueue(Legs, i => $"portal/user-{i}", spread);

        Assert.True(SpinWait.SpinUntil(() => spread.QueueSnapshot().Channels == Legs, Budget),
            "all 64 destinations must have claimed a queue entry");
        var aSnapshot = spread.QueueSnapshot();

        // ── Shape B: 64 legs, ONE destination. The head is executing; 63 are stacked behind it.
        var blocked = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var b = Enqueue(Legs, _ => "portal/one-slow-subscriber", blocked);

        Assert.True(SpinWait.SpinUntil(() => blocked.QueueSnapshot().Deepest == Legs - 1, Budget),
            "63 legs must be queued behind the one executing leg");
        var bSnapshot = blocked.QueueSnapshot();

        // 1️⃣ The number #1172 was filed on is IDENTICAL in both — so on its own it diagnoses nothing.
        a.InFlight.Should().Be(Legs);
        b.InFlight.Should().Be(Legs,
            "a leg holds its in-flight slot from ENQUEUE, so legs merely waiting behind another leg "
            + "count exactly like legs that are executing — which is why the count alone can never "
            + "support the claim that 'a delivery leg is not completing'");

        // 2️⃣ …and in shape A nothing is stuck at all: 64 in flight is a perfectly healthy silo.
        aSnapshot.Channels.Should().Be(Legs);
        aSnapshot.Destinations.Should().Be(Legs);
        aSnapshot.Deepest.Should().Be(0,
            "64 independent destinations each with one executing leg is BREADTH — no leg is waiting "
            + "on another, so reaching the reporting threshold here means load, not a wedge");

        // 3️⃣ The discriminator the saturation report now carries.
        bSnapshot.Channels.Should().Be(1);
        bSnapshot.Destinations.Should().Be(1);
        bSnapshot.Deepest.Should().Be(Legs - 1,
            "one destination with 63 legs stacked behind its head IS head-of-line blocking — this is "
            + "the shape the critical log line must be able to name, and the only one where 'a "
            + "delivery leg is not completing' is a true statement");

        // ── Both drain to zero once the legs finish: the FIFO is head-of-line, never a leak.
        foreach (var leg in a.Legs) leg.OnCompleted();
        foreach (var leg in b.Legs) leg.OnCompleted();

        Assert.True(SpinWait.SpinUntil(() => a.Completed() == Legs && b.Completed() == Legs, Budget),
            "every leg must terminate and release its slot — including the 63 that were queued, which "
            + "the dispatcher subscribes one at a time as the one ahead completes");
        Assert.True(SpinWait.SpinUntil(
                () => spread.ActiveChannels == 0 && blocked.ActiveChannels == 0, Budget),
            "a channel's entry is removed the moment its queue drains — a silo that has served "
            + "millions of short-lived portal/{user} addresses must hold none of them");
    }

    /// <summary>
    /// 🚨 THE #5009 SHAPE, on both sides of the fix. 64 legs to ONE stream-routed destination — a
    /// multiplexer hub, which every one of them is: <c>cache/{meshId}</c> fronts one
    /// <c>sync/{streamId}</c> sub-hub per observed node, and <c>portal/{userId}</c> the same for a
    /// viewer's areas.
    ///
    /// <para><b>Before:</b> the channel key was the ADDRESS, so those 64 independent streams shared
    /// one FIFO — 1 channel, deepest 63, one in-flight grain call at a time. On memex-cloud that was
    /// 62 of 64 legs stacked behind one <c>cache/…</c> address for ~1.5 h, reported (correctly, by
    /// the log line's own rule) as head-of-line blocking, while the peer pod — whose own cache
    /// channel is LOCAL and therefore fast — showed deepest 0 and read as load. Nothing was stuck:
    /// the channel key simply could not let unrelated streams overlap.</para>
    ///
    /// <para><b>After:</b> 64 channels over 1 destination, deepest 0 — breadth. Which is what the
    /// silo was actually doing, and is now what the log line says.</para>
    ///
    /// <para>This is a load control as much as a blocking control: the legs are all in flight and
    /// the dispatcher is at its busiest, and it must still drain to zero holding no per-stream
    /// state.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void OneMultiplexerDestination_ManyStreams_IsBreadthNotHeadOfLine()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = new IoPool(256);
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);

        var m = Enqueue(Legs, _ => "cache/12xX8OXQIEmdwvf_ZZ8LjA", dispatcher, i => $"sync/stream-{i}");

        Assert.True(SpinWait.SpinUntil(() => dispatcher.QueueSnapshot().Channels == Legs, Budget),
            "each of the 64 streams must hold its OWN channel — they share a destination hub, not an "
            + "ordering relationship");
        var snapshot = dispatcher.QueueSnapshot();

        m.InFlight.Should().Be(Legs);
        snapshot.Channels.Should().Be(Legs);
        snapshot.Destinations.Should().Be(1,
            "all 64 channels front the SAME multiplexer hub — that is the shape #5009 was filed on");
        snapshot.Deepest.Should().Be(0,
            "no leg is waiting on a leg: 64 frames of 64 DIFFERENT streams have no ordering "
            + "relationship, so serialising them was over-serialisation, never head-of-line blocking. "
            + "Before #5009 this identical traffic reported deepest 63");

        foreach (var leg in m.Legs) leg.OnCompleted();
        Assert.True(SpinWait.SpinUntil(() => m.Completed() == Legs, Budget),
            "every leg must terminate and release its slot");
        Assert.True(SpinWait.SpinUntil(() => dispatcher.ActiveChannels == 0, Budget),
            "a channel is held only while it has work in flight — a silo that has served millions of "
            + "short-lived streams must retain none of them");
    }
}
