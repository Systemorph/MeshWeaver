using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
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
///   <item><b>64 is not a bound.</b> It is <c>RoutingGrain.SaturationThreshold</c>, a MeshWeaver
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
/// <c>(64 destinations, deepest 0)</c> is breadth — every leg is executing, nothing waits on
/// anything. <c>(1 destination, deepest 63)</c> is head-of-line blocking — 63 legs are waiting on a
/// leg. The saturation report now carries that pair, so the next occurrence is diagnosable from the
/// log line instead of from a profiler on a live pod.</para>
///
/// <para>Deterministic and cluster-free: legs are <see cref="Subject{T}"/>s, so "in flight" and
/// "completed" are decided by the test, never by a timer.</para>
/// </summary>
public class RoutingBackpressureShapeTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Mirrors <c>RoutingGrain.SaturationThreshold</c> — the value every prod report printed.</summary>
    private const int Legs = 64;

    /// <summary>
    /// Drives <paramref name="legs"/> legs through the dispatcher, one per destination returned by
    /// <paramref name="destinationOf"/>, and returns the in-flight count plus the queue snapshot at
    /// the moment they are all enqueued. Bookkeeping is byte-for-byte what <c>RoutingGrain</c> does:
    /// increment at enqueue, decrement in the leg-completed callback.
    /// </summary>
    private static (int InFlight, int Destinations, int Deepest, Subject<Unit>[] Legs, Func<int> Completed)
        Enqueue(int legs, Func<int, string> destinationOf, OrderedRouteDispatcher dispatcher)
    {
        var subjects = Enumerable.Range(0, legs).Select(_ => new Subject<Unit>()).ToArray();
        var inFlight = 0;
        var completed = 0;

        foreach (var (subject, i) in subjects.Select((s, i) => (s, i)))
        {
            Interlocked.Increment(ref inFlight);
            dispatcher.Enqueue(
                destinationOf(i),
                subject.AsObservable(),
                () =>
                {
                    Interlocked.Decrement(ref inFlight);
                    Interlocked.Increment(ref completed);
                });
        }

        var (destinations, deepest) = dispatcher.QueueSnapshot();
        return (Volatile.Read(ref inFlight), destinations, deepest, subjects, () => Volatile.Read(ref completed));
    }

    /// <summary>
    /// 🚨 THE REGRESSION GUARD for the misreading that produced #1172 and #1284. Two opposite
    /// situations — total breadth and total head-of-line blocking — must yield the SAME in-flight
    /// count, and must be separated by the queue snapshot the report now carries.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public void SameInFlightCount_MeansBothBusyAndBlocked_AndOnlyTheQueueSnapshotTellsThemApart()
    {
        using var pool = new IoPool(256);   // the Routing pool's real cap — never the binding constraint at 64

        // ── Shape A: 64 destinations, one leg each. Every leg is executing; nothing waits on anything.
        var spread = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var a = Enqueue(Legs, i => $"portal/user-{i}", spread);

        Assert.True(SpinWait.SpinUntil(() => spread.QueueSnapshot().Destinations == Legs, Budget),
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
        aSnapshot.Destinations.Should().Be(Legs);
        aSnapshot.Deepest.Should().Be(0,
            "64 independent destinations each with one executing leg is BREADTH — no leg is waiting "
            + "on another, so reaching the reporting threshold here means load, not a wedge");

        // 3️⃣ The discriminator the saturation report now carries.
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
                () => spread.ActiveDestinations == 0 && blocked.ActiveDestinations == 0, Budget),
            "a destination's entry is removed the moment its queue drains — a silo that has served "
            + "millions of short-lived portal/{user} addresses must hold none of them");
    }
}
