using System;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Issue #5703: a routing saturation report filed Critical — "a leaked slot or a starved silo" — for
/// a leg that was only WAITING. Production, 2026-10-05: oldest leg 124 863 ms, deepest per-channel
/// queue 61 on one stream channel. A stream-routed leg was tracked from the moment it was ENQUEUED,
/// so a leg queued behind sixty healthy two-second round trips of its own stream carried an age of
/// two minutes before its own timeouts had even started, and the level rule (age against
/// <see cref="RoutingSaturationReport.LegSelfBound"/>, a bound on the time since a leg got its turn) read the queue as a
/// leak.
///
/// <para>Driven through the real pieces the routing grain wires together — <see cref="IoPool"/>,
/// <see cref="OrderedRouteDispatcher"/> and <see cref="RoutingQuiescence"/>, wired exactly as
/// <c>RoutingGrain.RouteMessage</c>'s stream branch wires them — with no cluster and no clock of its
/// own: every comparison is between two stamps the code took itself, forced apart by spinning until
/// the monotonic clock moves.</para>
/// </summary>
public class AQueuedLegIsNotALeakTest
{
    private const string Channel = "cache/L1PlUIhTo0WIzSJqWuyx1Q";
    private const string Stream = "HOTBn-NpckOVgw-t3NcqWg";

    private static void UntilTheMonotonicClockMoves()
    {
        var before = Stopwatch.GetTimestamp();
        Assert.True(SpinWait.SpinUntil(() => Stopwatch.GetTimestamp() != before, TestTimeouts.Quick),
            "the monotonic clock must advance, or the two stamps under comparison are equal by construction");
    }

    /// <summary>
    /// How long the head leg is held RUNNING before it lands — the stand-in for the sixty two-second
    /// round trips of production. A spin on the monotonic clock, never a sleep: what is forced is a
    /// measured gap between two stamps, and it has to dominate the scheduling noise between two
    /// consecutive readings, or a test of "measured from acceptance vs measured from start" cannot
    /// tell the two apart (the first draft of this test, with a one-tick gap, passed against the
    /// unfixed reading).
    /// </summary>
    private static readonly TimeSpan HeadRun = TimeSpan.FromMilliseconds(200);

    private static void SpinFor(TimeSpan gap)
    {
        var from = Stopwatch.GetTimestamp();
        Assert.True(SpinWait.SpinUntil(() => Stopwatch.GetElapsedTime(from) >= gap, TestTimeouts.Quick),
            "the monotonic clock must advance by the forced gap");
    }

    /// <summary>A leg that counts its start and terminates when <paramref name="gate"/> fires.</summary>
    private static IObservable<Unit> GatedLeg(int[] started, int index, IObservable<Unit> gate) =>
        Observable.Create<Unit>(observer =>
        {
            Volatile.Write(ref started[index], 1);
            return gate.Take(1).Subscribe(observer);
        });

    /// <summary>The stream branch of <c>RoutingGrain.RouteMessage</c>, minus the grain.</summary>
    private static void Route(
        RoutingQuiescence quiescence, OrderedRouteDispatcher dispatcher, int id, IObservable<Unit> leg)
    {
        var slot = quiescence.TrackQueued($"stream-routed → {Channel} (delivery {id:000})");
        dispatcher.Enqueue(Channel, Stream, leg, () => slot.MarkDispatched(), slot.Dispose);
    }

    [Fact(Timeout = 120_000)]
    public void ALegWaitingBehindItsOwnStream_IsAWait_AndTheRunningLegIsMeasuredFromItsStart()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = new IoPool(8);
        using var quiescence = new RoutingQuiescence();
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var gates = new[] { new Subject<Unit>(), new Subject<Unit>(), new Subject<Unit>() };
        var started = new int[gates.Length];
        try
        {
            // Three frames of ONE stream, accepted together: one dispatched, two queued behind it.
            for (var i = 0; i < gates.Length; i++)
                Route(quiescence, dispatcher, i, GatedLeg(started, i, gates[i]));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started[0]) == 1, TestTimeouts.Quick),
                "the head leg must start");
            SpinFor(HeadRun);

            // The head lands; the next frame only NOW starts its own timeouts.
            var headLanded = Stopwatch.GetTimestamp();
            gates[0].OnNext(Unit.Default);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started[1]) == 1, TestTimeouts.Quick),
                "the second leg starts once the head has terminated — one leg per channel");
            UntilTheMonotonicClockMoves();

            var dispatched = quiescence.OldestInFlight();
            var queued = quiescence.OldestQueued();
            var sample = quiescence.InFlightSample().Labels;
            var sinceHeadLanded = Stopwatch.GetElapsedTime(headLanded);

            dispatched.Should().NotBeNull();
            dispatched!.Value.Label.Should().Be($"stream-routed → {Channel} (delivery 001)",
                "the dispatched leg is the one that just got its turn — the head has landed");
            queued.Should().NotBeNull();
            queued!.Value.Label.Should().Be($"stream-routed → {Channel} (delivery 002)",
                "the third frame is still waiting behind the second, and is reported AS waiting");

            // 🚨 The defect: leg 001 was accepted at the same moment as leg 002, before the head ran.
            // Measured from acceptance its age includes the head's whole run; measured from its START
            // it does not. The level rule bounds the time since dispatch, so it must be fed the second.
            dispatched.Value.Age.Should().BeLessThanOrEqualTo(sinceHeadLanded,
                "a leg's age for the leak verdict runs from its DISPATCH; leg 001 was dispatched only after the "
                + "head landed, so its age cannot exceed the time since then — measured from acceptance it "
                + "would carry the head's whole run as well, which is exactly what read a two-minute queue "
                + "as a leaked slot");
            queued.Value.Age.Should().BeGreaterThanOrEqualTo(HeadRun,
                "leg 002 has waited since it was accepted, before the head ran — the wait is real and is "
                + "reported, it just decides nothing");

            sample.Should().Contain(l => l.StartsWith($"stream-routed → {Channel} (delivery 002)")
                    && l.EndsWith("(queued, not dispatched)"),
                "the shutdown residual must be able to say a leg was never dispatched, not just how long it was held");
        }
        finally
        {
            foreach (var gate in gates) gate.OnNext(Unit.Default);
        }
    }

    /// <summary>
    /// The control that keeps the fix from becoming a blind spot: a head leg that never terminates is
    /// still the oldest dispatched leg, its age still grows from its own dispatch, and the level rule
    /// still reaches Critical on it — nothing queued behind it can mask it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void AHeadLegThatNeverLands_IsStillTheOldestRunningLeg()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var pool = new IoPool(8);
        using var quiescence = new RoutingQuiescence();
        var dispatcher = new OrderedRouteDispatcher(pool, NullLogger.Instance);
        var stuck = new Subject<Unit>();
        var behind = new Subject<Unit>();
        var started = new int[2];
        try
        {
            Route(quiescence, dispatcher, 0, GatedLeg(started, 0, stuck));
            Route(quiescence, dispatcher, 1, GatedLeg(started, 1, behind));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref started[0]) == 1, TestTimeouts.Quick),
                "the head leg must start");
            UntilTheMonotonicClockMoves();

            var first = quiescence.OldestInFlight();
            first!.Value.Label.Should().Be($"stream-routed → {Channel} (delivery 000)");
            Assert.True(
                SpinWait.SpinUntil(() => quiescence.OldestInFlight()!.Value.Age > first.Value.Age, TestTimeouts.Quick),
                "a stuck head's age since dispatch grows — it is the leak reading, and it stays visible");
            Volatile.Read(ref started[1]).Should().Be(0, "nothing overtakes a stuck head on its own channel");
            RoutingSaturationReport.SaturationLevel(RoutingSaturationReport.LegSelfBound)
                .Should().Be(LogLevel.Critical, "a dispatched leg past its own bounds is still the one Critical");
        }
        finally
        {
            stuck.OnNext(Unit.Default);
            behind.OnNext(Unit.Default);
        }
    }
}
