using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// The two readings a routing saturation report was missing, and the control that proves the one it
/// printed could not answer the question it posed — issues #5018 / #5118.
///
/// <para><b>The claim under test.</b> The `[ROUTE]` back-pressure line names the pre-subscribe
/// ThreadPool wait as its leading hypothesis (<i>"INCLUDING the unbounded wait for a ThreadPool
/// thread before the leg's own timeouts start, so a CPU-starved silo raises this with nothing
/// stuck"</i>) and then printed <see cref="IIoPool.CurrentInFlight"/>, which cannot see that
/// population: <c>IoPool</c> increments it only once the gate has GRANTED a slot and releases it as
/// soon as <c>source.Subscribe</c> returns, so it counts the subscribe prologue and nothing
/// else.</para>
///
/// <list type="number">
///   <item><see cref="TwoOppositePoolStates_PrintTheSameSubscribingCount_AndOnlyWaitingTellsThemApart"/>
///     builds both states and asserts the printed number is the same — and inside the range every
///     production sample reported — while <see cref="IIoPool.CurrentlyWaiting"/> differs by the
///     whole population.</item>
///   <item><see cref="OldestInFlight_SeparatesLoadFromALeakedSlot_InOneSample"/> pins the reading
///     that answers load-vs-leak WITHOUT a second sample, which is what left #5003, #5014 and #5134
///     inconclusive.</item>
/// </list>
///
/// <para>Deterministic and cluster-free. Nothing sleeps: a leg that must be parked is parked on a
/// volatile int under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> and released
/// in a <c>finally</c>, and a distinct timestamp is forced by spinning until the monotonic clock
/// moves rather than by waiting a guessed interval.</para>
/// </summary>
public class RoutingSaturationNamesItsOwnVerdictTest
{
    /// <summary>Mirrors <c>RoutingGrain.SaturationThreshold</c> — the value every production report printed.</summary>
    private const int Legs = 64;

    // 🚨 WHY THE [Fact(Timeout = …)] BELOW IS A LITERAL, AND WHY IT IS NOT 30 s. Every wait inside
    // these tests is a TestTimeouts value, which is the one place that decides how long a machine is
    // given; an ATTRIBUTE argument cannot be a property, so the outer bound has to be a constant. It
    // is set to dominate the longest inner wait at the CI factor rather than to be a guess of its
    // own — and deliberately not 30 s, which is the framework's own write bound
    // (LateResponseWatchBound + VerdictBoundGrace): a test bounded there always gives up one second
    // before the framework can name what went wrong, so the failure reads as an anonymous timeout.

    /// <summary>
    /// The largest `routing pool subscribing` value any production sample of this log site carried.
    /// The point of the first test is that the STARVED shape also lands inside this range, so the
    /// field could never have distinguished the two.
    /// </summary>
    private const int LargestSubscribingValueSeenInProduction = 2;

    /// <summary>Spins until <see cref="Stopwatch.GetTimestamp"/> has advanced, so two stamps differ.</summary>
    private static void UntilTheMonotonicClockMoves()
    {
        var before = Stopwatch.GetTimestamp();
        Assert.True(
            SpinWait.SpinUntil(() => Stopwatch.GetTimestamp() != before, TestTimeouts.Quick),
            "the monotonic clock must advance — without a distinct timestamp the oldest-leg reading "
            + "has no defined answer, and a sleep would be a guess where a spin is a fact");
    }

    /// <summary>
    /// 🚨 THE CONTROL FOR THE MISREADING. A pool whose every leg is parked waiting for a slot, and a
    /// pool whose every leg is past its subscribe and awaiting its own I/O, are opposite diagnoses —
    /// a thread shortage and ordinary breadth. Both printed the same `routing pool subscribing`
    /// number for the entire life of the ticket, and only <see cref="IIoPool.CurrentlyWaiting"/>
    /// separates them.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void TwoOppositePoolStates_PrintTheSameSubscribingCount_AndOnlyWaitingTellsThemApart()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

        // ── Shape A — THE NAMED HYPOTHESIS: work accepted, nothing able to start.
        // One slot, held by a leg parked inside its own subscribe, and the rest stacked at the gate.
        using var starved = new IoPool(1);
        var release = 0;

        // 🚨 A park that EXPIRED and a park that was RELEASED are different facts about this test's
        // own subject, so the spin's result may not be discarded — completing on both would make an
        // expired park read as a held one.
        //
        // 🚨 And faulting alone is NOT enough, which is the half that is easy to miss. The
        // subscriptions below are `Subscribe(_ => { }, _ => { })` and that error arm is deliberate:
        // 63 of these legs are cancelled at their gate wait when the pool drains, and the test must
        // not red on expected teardown. So an OnError from the parked leg would be swallowed by the
        // very arm that exists for the cancelled ones. The expiry is therefore ALSO recorded in a
        // flag an assertion can see.
        //
        // A volatile int and not the Exception itself: the write happens on a pool thread and the
        // read on the test thread, and an unsynchronised reference read may never observe it. This is
        // the same shape the house uses for every release into a deliberately parked worker.
        var parkExpired = 0;
        var parked = Observable.Create<Unit>(observer =>
        {
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.DefaultOuterBound))
            {
                Volatile.Write(ref parkExpired, 1);
                observer.OnError(new TimeoutException(
                    "the parked leg's release was never signalled, so it stopped holding the pool's "
                    + "only slot on its own — every reading taken here is about a pool that was no "
                    + "longer starved"));
                return Disposable.Empty;
            }
            observer.OnCompleted();
            return Disposable.Empty;
        });

        var starvedSubscriptions = new CompositeDisposable();
        try
        {
            starvedSubscriptions.Add(starved.SubscribeThroughPool(parked).Subscribe(_ => { }, _ => { }));
            Assert.True(SpinWait.SpinUntil(() => starved.CurrentInFlight == 1, TestTimeouts.DefaultOuterBound),
                "the head leg must take the pool's only slot and park inside its subscribe");

            for (var i = 0; i < Legs - 1; i++)
                starvedSubscriptions.Add(
                    starved.SubscribeThroughPool(new Subject<Unit>().AsObservable()).Subscribe(_ => { }, _ => { }));

            Assert.True(
                SpinWait.SpinUntil(() => starved.CurrentlyWaiting == Legs - 1, TestTimeouts.DefaultOuterBound),
                $"all {Legs - 1} remaining legs must be ACCEPTED and waiting for a slot — that is the "
                + "population the log line's own caveat describes");

            var starvedSubscribing = starved.CurrentInFlight;
            var starvedWaiting = starved.CurrentlyWaiting;

            // ── Shape B — ORDINARY BREADTH: every leg past its subscribe, awaiting its own I/O.
            // A Subject's Subscribe returns at once, so the prologue is over and the leg lives on.
            using var spread = new IoPool(256);   // the routing pool's real cap
            var legs = Enumerable.Range(0, Legs).Select(_ => new Subject<Unit>()).ToArray();
            var spreadSubscriptions = new CompositeDisposable(
                legs.Select(leg => spread.SubscribeThroughPool(leg.AsObservable()).Subscribe(_ => { }, _ => { })));
            try
            {
                Assert.True(
                    SpinWait.SpinUntil(() => spread.CurrentlyWaiting == 0 && spread.CurrentInFlight == 0,
                        TestTimeouts.DefaultOuterBound),
                    "every leg must get through its subscribe prologue and then simply live on — "
                    + "awaiting its own terminal, counted by neither pool gauge");

                // 1️⃣ The field the report printed is the SAME in both, and in both it is a value
                //    production actually printed while asserting nothing was stuck.
                starvedSubscribing.Should().BeLessThanOrEqualTo(LargestSubscribingValueSeenInProduction,
                    "a silo with 63 legs unable to start prints a `routing pool subscribing` inside the "
                    + "very range every production sample carried — so the field never had the power to "
                    + "confirm or refute the ThreadPool-starvation reading it was printed as evidence for");
                spread.CurrentInFlight.Should().BeLessThanOrEqualTo(LargestSubscribingValueSeenInProduction);

                // 2️⃣ …and the two situations are opposite.
                starvedWaiting.Should().Be(Legs - 1,
                    "accepted-and-not-yet-running is exactly what a thread shortage looks like");
                spread.CurrentlyWaiting.Should().Be(0,
                    "nothing is queued for a slot here — the legs are downstream of their subscribe, so "
                    + "the same in-flight route count means load rather than starvation");

                // 3️⃣ The discriminator, stated as the gauge's own XML doc states it.
                (starvedWaiting - spread.CurrentlyWaiting).Should().BeGreaterThanOrEqualTo(Legs - 1,
                    "`CurrentlyWaiting` is the only one of the two gauges that moves between these "
                    + "states, which is why the saturation report has to print it");

                // 4️⃣ …and every reading above is only about a starved pool if the park actually held.
                //    Checked LAST rather than first: the park is released in the finally below, so
                //    this is the point at which an expiry would already have happened.
                Volatile.Read(ref parkExpired).Should().Be(0,
                    "the starved shape means nothing if its head leg stopped holding the pool's only "
                    + "slot before the readings were taken");
            }
            finally
            {
                foreach (var leg in legs) leg.OnCompleted();
                spreadSubscriptions.Dispose();
            }
        }
        finally
        {
            // 🚨 In a finally so a failing assertion above cannot strand the parked pool thread.
            Volatile.Write(ref release, 1);
            starvedSubscriptions.Dispose();
        }
    }

    /// <summary>
    /// 🚨 THE READING THAT ANSWERS load-vs-leak FROM ONE SAMPLE. The log site's own advice — a later
    /// line with a higher episode proves this one drained — needs a SECOND sample, while the incident
    /// filer files per sample. An age needs none: every leg young is load, one leg old is a slot that
    /// is not coming back, and the label says which leg.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void OldestInFlight_SeparatesLoadFromALeakedSlot_InOneSample()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var quiescence = new RoutingQuiescence();

        quiescence.OldestInFlight().Should().BeNull(
            "an idle silo has no oldest leg — and null must be distinguishable from an age of zero, "
            + "because 'nothing is in flight' and 'something started just now' are different facts");

        // A FULL SATURATION BUDGET of legs rather than two or three, so the reading is exercised at
        // the size it is actually taken at.
        //
        // 🚨 WHAT THIS CASE DOES AND DOES NOT FALSIFY — measured on both sides, because the first
        // control written for it was VACUOUS and passing it proved nothing:
        //
        //  • Inverting the comparison (return the NEWEST in-flight leg) FAILS here — it reports
        //    `leg-63`. So the direction of the reading is pinned.
        //  • Returning the FIRST ENUMERATED entry, with no timestamp comparison at all, PASSES here.
        //    ConcurrentDictionary keyed by an incrementing long enumerates in insertion order in
        //    practice at this size, so enumeration order and accept order coincide and a black-box
        //    test cannot separate them. This case therefore does NOT establish independence from
        //    enumeration order, and no claim below should be read as establishing it — the
        //    comparison is there because the collection does not guarantee that order, not because
        //    a test caught it.
        var handles = new IDisposable[Legs];
        for (var i = 0; i < Legs; i++)
        {
            handles[i] = quiescence.Track($"stream-routed → cache/leg-{i} (delivery {i:000})");
            UntilTheMonotonicClockMoves();
        }

        // 1️⃣ It names the leg, not just a number — #2833's rule: a leg that cannot be named cannot
        //    be found, and the whole value of the age is that it points AT something.
        var oldest = quiescence.OldestInFlight();
        oldest.Should().NotBeNull();
        oldest!.Value.Label.Should().Be("stream-routed → cache/leg-0 (delivery 000)",
            "the leg accepted FIRST is the one a saturation report must name — reporting the newest "
            + "would make every episode look healthy, which is the inversion this pins");

        // 2️⃣ The age is a real elapsed time and it GROWS, which is what makes 'minutes old' mean
        //    a leaked slot rather than a formatting artefact.
        var firstReading = oldest.Value.Age;
        firstReading.Should().BeGreaterThan(TimeSpan.Zero);
        Assert.True(
            SpinWait.SpinUntil(() => quiescence.OldestInFlight()!.Value.Age > firstReading,
                TestTimeouts.DefaultOuterBound),
            "the oldest leg's age must advance with real elapsed time — a leg held for minutes is the "
            + "leaked-slot signature, so a static number would answer nothing");

        // 3️⃣ It follows the legs, so a silo that keeps working reports a YOUNG oldest leg — the
        //    load reading — rather than staying pinned to a leg that has landed.
        handles[0].Dispose();
        quiescence.OldestInFlight()!.Value.Label.Should().Be(
            "stream-routed → cache/leg-1 (delivery 001)",
            "once the oldest leg lands, the next one is the oldest — the reading is about what is "
            + "STILL in flight, which is the only thing a saturation report may claim");

        handles[0].Dispose();
        quiescence.OldestInFlight()!.Value.Label.Should().Be(
            "stream-routed → cache/leg-1 (delivery 001)",
            "a double dispose must not resurrect a leg or shift the reading");

        for (var i = 1; i < Legs; i++) handles[i].Dispose();
        quiescence.OldestInFlight().Should().BeNull(
            "back to the idle reading once every leg has landed");
    }
}
