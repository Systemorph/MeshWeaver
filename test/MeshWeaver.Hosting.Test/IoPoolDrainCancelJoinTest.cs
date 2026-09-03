using System;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using MeshWeaver.Mesh.Threading;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The teardown residual that read <c>pools=[Query=1]</c> — with NO bracketed site — on
/// <c>MeshNodeLanguageServiceTest</c> from 2026-08-28 (core #2578, #2616) to 2026-09-03 (Plugins
/// #1260 attempt 1, shard 3), and cost two wrong fixes before it was named.
///
/// <para><b>What a residual with no site is.</b> <see cref="IoPool.Drain"/> reports three things:
/// gate permits it could not re-acquire, blocking leaves still running, and a CANCEL that did not
/// return. The first two always carry a site (every leaf registers one); the third never did. So
/// <c>Query=1</c> with nothing in brackets meant exactly one thing — <c>_poolCts.Cancel()</c> on the
/// Query pool never returned — and nobody could read that off it.</para>
///
/// <para><b>What parks the cancel.</b> <see cref="IIoPool.SubscribeThroughPool{T}"/> registers one
/// callback per live pooled subscription that runs that subscription's downstream teardown inline:
/// <c>inner.Dispose(); observer.OnCompleted();</c>. It also put the raw
/// <see cref="CancellationTokenRegistration"/> into the subscription's <c>CompositeDisposable</c>,
/// and <see cref="CancellationTokenRegistration.Dispose"/> BLOCKS until a callback that is executing
/// on another thread has finished. Rx's <c>Throttle</c> forwards from its timer INSIDE its gate and
/// <c>Take(1)</c> disposes upstream synchronously on completion — so a consumer shaped
/// <c>Query(...).Throttle(1 s).Take(1)</c> whose timer fires as the drain cancels holds the
/// operator gate while it waits in <c>Dispose()</c> for the drain callback, and the drain callback
/// waits in <c>Throttle.OnCompleted</c> for the operator gate. Two locks, two threads, no exit; the
/// drain reports the cancel residual after its budget, RSS flat the whole time (parked, not
/// computing — #2616's own measurement). The consumer in every occurrence was
/// <c>CompletionUsageIndex.EnsureFresh()</c>, whose 1 s <c>Throttle</c> lands on the test bodies
/// that take ~1 s (1018 ms and 1044 ms in the two traces that survived).</para>
///
/// <para>Deterministic here because the timer is a <see cref="TestScheduler"/> advanced from a
/// thread this test owns, and the two interleavings the deadlock needs — "the consumer is inside
/// its gate" and "the drain callback is executing" — are volatile flags polled under bounded
/// spins, the sanctioned shape for a release INTO a worker the test deliberately parks.</para>
/// </summary>
public class IoPoolDrainCancelJoinTest
{
    private static readonly TimeSpan Timeout10 = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainBudget = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 🚨 THE repro. Fails on the pre-fix pool with <c>Query=1</c> and no site after exactly one
    /// drain budget — the CI signature — and the two threads stay deadlocked for the life of the
    /// process, exactly as they did on the runner.
    /// </summary>
    [Fact]
    public void Drain_WhenAConsumerCompletesInsideItsOperatorGateAsTheDrainCancels_TheCancelReturns()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions { DrainTimeout = DrainBudget });
        var pool = registry.Get(IoPoolNames.Query);
        var scheduler = new TestScheduler();
        var feed = new Subject<int>();
        var subscribed = 0;
        var drainCallbackEntered = 0;
        var consumerInsideGate = 0;
        var consumerReturned = 0;

        // The change feed a query opens: subscribed through the pool (the tracked window), then
        // left open — a query's feed never completes on its own.
        var pooled = pool.SubscribeThroughPool(Observable.Create<int>(o =>
        {
            var d = feed.Subscribe(o);
            Volatile.Write(ref subscribed, 1);
            return d;
        }));

        // CompletionUsageIndex.EnsureFresh()'s exact operator shape, with the two interleavings
        // made explicit:
        //  • the drain's OnCompleted passes the first Do on the IoPool-cancel thread BEFORE it
        //    reaches Throttle's gate — that is "the drain callback is executing";
        //  • the second Do runs INSIDE Throttle's gate on the timer thread (Rx forwards from
        //    Propagate under `lock (_gate)`), and holds it until the callback is executing.
        // Take(1) then completes and disposes upstream — still under that gate — which is where
        // CancellationTokenRegistration.Dispose() used to wait for the very callback that is
        // waiting for the gate.
        using var subscription = pooled
            .Do(_ => { }, () => Volatile.Write(ref drainCallbackEntered, 1))
            .Throttle(TimeSpan.FromSeconds(1), scheduler)
            .Do(_ =>
            {
                Volatile.Write(ref consumerInsideGate, 1);
                SpinWait.SpinUntil(() => Volatile.Read(ref drainCallbackEntered) == 1, Timeout10);
            })
            .Take(1)
            .Subscribe(_ => { }, _ => { });

        SpinWait.SpinUntil(() => Volatile.Read(ref subscribed) == 1, Timeout10)
            .Should().BeTrue("precondition: the pooled subscribe ran and the drain registration exists");

        // The timer thread: the feed's Initial arrives, then virtual time reaches the throttle's
        // due time and Propagate forwards under the gate.
        var timer = new Thread(() =>
        {
            feed.OnNext(1);
            scheduler.AdvanceBy(TimeSpan.FromSeconds(1).Ticks);
            Volatile.Write(ref consumerReturned, 1);
        })
        {
            IsBackground = true,
            Name = "rx-throttle-timer",
        };
        timer.Start();

        SpinWait.SpinUntil(() => Volatile.Read(ref consumerInsideGate) == 1, Timeout10)
            .Should().BeTrue("precondition: the consumer is inside its operator gate, about to complete");

        // Teardown's phase 2: cancel + join every pool. On the pre-fix pool this returns after the
        // budget with the cancel thread and the timer thread parked on each other.
        var sw = Stopwatch.StartNew();
        var residual = registry.DrainAll(out var byPool);
        sw.Stop();

        residual.Should().Be(0,
            "a consumer unsubscribing from inside its operator gate must never be able to park the "
            + "pool's cancel: its unsubscribe must not wait for the drain callback, and the callback "
            + "then completes on its own. Residual reported: [{0}]",
            string.Join(", ", byPool));
        sw.Elapsed.Should().BeLessThan(DrainBudget,
            "the cancel join must return because the callbacks FINISHED, not because the budget "
            + "expired — a join that only returns on its budget is the anonymous 30 s teardown");
        SpinWait.SpinUntil(() => Volatile.Read(ref consumerReturned) == 1, Timeout10)
            .Should().BeTrue("the consumer's unsubscribe returned — it did not wait on the drain callback");
    }

    /// <summary>
    /// The diagnostic half: a cancel that does not return within the budget must be NAMED as the
    /// cancel join, not reported as an anonymous count. <c>Query=1</c> and <c>Query=1 [the cancel
    /// did not return]</c> are the difference between a week and an afternoon (#2616 → this file).
    /// The parked callback here is the subscriber's own <c>OnCompleted</c>, which the drain runs
    /// inline — the same channel the deadlock above travels, without the deadlock.
    /// </summary>
    [Fact]
    public void Drain_WhenTheCancelJoinExpires_TheResidualNamesTheCancelJoin()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions { DrainTimeout = DrainBudget });
        var pool = registry.Get(IoPoolNames.Query);
        var subscribed = 0;
        var release = 0;

        using var subscription = pool
            .SubscribeThroughPool(Observable.Create<int>(_ =>
            {
                Volatile.Write(ref subscribed, 1);
                return Disposable.Empty;
            }))
            .Subscribe(
                _ => { },
                _ => { },
                // The SUBJECT: a teardown that will not return. Bounded, and released in the
                // finally below so a failing assertion cannot leave the cancel thread parked.
                () => SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Timeout10));

        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref subscribed) == 1, Timeout10)
                .Should().BeTrue("precondition: the pooled subscribe ran and the drain registration exists");

            var residual = registry.DrainAll(out var byPool);

            residual.Should().Be(1, "the parked callback is exactly one residual");
            byPool.Should().ContainSingle().Which.Pool.Should().Be(IoPoolNames.Query);
            byPool[0].Sites.Should().ContainSingle(
                "no leaf held a permit, so the ONLY thing left to name is the cancel join itself")
                .Which.Should().Contain("cancel",
                    "the residual must say that the pool token's cancel did not return — an "
                    + "anonymous 'Query=1' sent two investigations into the wrong leaf");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }
}
