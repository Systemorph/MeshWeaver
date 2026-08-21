using System;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the bounded, throttled, fully-reactive retry that protects layout-area
/// subscriptions from the inexistent-address message storm (the prod wedge,
/// 2026-06-14): "wedging usually means uncaught exception and endless messages,
/// especially with inexistent addresses". The contract:
///   * a retryable (transient) error is retried at most <c>maxRetries</c> times with
///     exponential backoff, THEN the error is surfaced — never an unbounded resubscribe;
///   * a non-retryable error fails fast (no retry, no delay);
///   * a source that recovers before the budget emits its value normally.
/// All virtual-time via Rx <see cref="TestScheduler"/> — no Task.Delay, no wall clock.
/// </summary>
public class AreaStreamRetryTest
{
    private static readonly Exception Retryable = new TimeoutException("transient: area not addressable yet");
    private static readonly Exception NonRetryable = new InvalidOperationException("permanent failure");

    [Fact]
    public void RetryableError_RetriesBoundedThenGivesUp()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        // Cold source that errors (retryable) on every (re)subscription.
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return Observable.Throw<int>(Retryable, scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source
            .RetryAreaWithBackoff(shouldRetry: _ => true, maxRetries: 5,
                baseDelay: TimeSpan.FromTicks(10), scheduler: scheduler)
            .Subscribe(observer);

        scheduler.Start();

        // 1 initial subscription + 5 retries = 6, then it gives up. NOT unbounded.
        subscribeCount.Should().Be(6);
        // Terminal: exactly one OnError, no OnNext, no infinite spin.
        observer.Messages.Should().HaveCount(1);
        observer.Messages[0].Value.Kind.Should().Be(NotificationKind.OnError);
    }

    [Fact]
    public void NonRetryableError_FailsFast_NoRetry()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return Observable.Throw<int>(NonRetryable, scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source
            .RetryAreaWithBackoff(shouldRetry: _ => false, maxRetries: 5,
                baseDelay: TimeSpan.FromTicks(10), scheduler: scheduler)
            .Subscribe(observer);

        scheduler.Start();

        // shouldRetry=false → surfaced immediately, exactly one subscription.
        subscribeCount.Should().Be(1);
        observer.Messages.Should().HaveCount(1);
        observer.Messages[0].Value.Kind.Should().Be(NotificationKind.OnError);
    }

    [Fact]
    public void RecoversBeforeBudget_EmitsValue_NoError()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            // Fail the first two subscriptions, succeed on the third (within budget).
            return subscribeCount < 3
                ? Observable.Throw<int>(Retryable, scheduler)
                : Observable.Return(42, scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source
            .RetryAreaWithBackoff(shouldRetry: _ => true, maxRetries: 5,
                baseDelay: TimeSpan.FromTicks(10), scheduler: scheduler)
            .Subscribe(observer);

        scheduler.Start();

        subscribeCount.Should().Be(3);
        observer.Messages.Should().HaveCount(2); // OnNext(42) + OnCompleted
        observer.Messages[0].Value.Value.Should().Be(42);
        observer.Messages[1].Value.Kind.Should().Be(NotificationKind.OnCompleted);
    }

    // ————————————————————————— a RECYCLE is not an inexistent address (#1996)

    private static readonly Exception Recycling =
        new InvalidOperationException("Hub ThinkInStreams is shutting down — cannot register new response subject.");

    /// <summary>
    /// 🚨 THE MEASURED INCIDENT, replayed on virtual time.
    ///
    /// <para>Systemorph/MeshWeaver#1996: a package provision recycled the node hub at 20:35:25.492;
    /// every area stream on it failed at 20:35:25.586 with <c>"Hub … is shutting down"</c>; the hub
    /// was serving again at 20:35:35.552 — <b>10.06 s</b> later. Under the old policy the client
    /// spent 5 retries over 250·2ⁿ ms = 7.75 s and painted a terminal "Reload to retry" 2.2 s
    /// BEFORE the hub came back, and the page never repaired itself.</para>
    ///
    /// <para>With the announced recycle switching the retry from "count attempts" to "keep probing
    /// until it answers", the SAME timeline recovers — and the assertion is the recovery, not a
    /// bigger number.</para>
    /// </summary>
    [Fact]
    public void AnnouncedRecycle_KeepsProbingUntilTheHubAnswers_TheMeasured10Seconds()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var hubBackAt = TimeSpan.FromMilliseconds(10_060);   // the measured recycle → serving gap
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return scheduler.Clock < hubBackAt.Ticks
                ? Observable.Throw<int>(Recycling, scheduler)
                : Observable.Return(42, scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source
            .RetryAreaWithBackoff(
                shouldRetry: AreaErrorClassifier.ShouldRetryArea,
                maxRetries: AreaStreamRetry.DefaultMaxRetries,
                baseDelay: TimeSpan.FromMilliseconds(250),
                scheduler: scheduler)
            .Subscribe(observer);

        scheduler.Start();

        observer.Messages.Should().HaveCount(2, "the page must recover, not paint a terminal error");
        observer.Messages[0].Value.Value.Should().Be(42);
        observer.Messages[1].Value.Kind.Should().Be(NotificationKind.OnCompleted);
        subscribeCount.Should().BeGreaterThan(AreaStreamRetry.DefaultMaxRetries + 1,
            "an announced recycle must NOT be counted against the fixed attempt budget");
    }

    /// <summary>
    /// A recycle announces itself ONCE and then reports whatever the reactivating hub reports —
    /// "target hub was not found", a timeout — so the policy LATCHES. Without the latch the retry
    /// falls back to counting attempts in the middle of the very recovery the first error
    /// announced, and gives up exactly as before.
    /// </summary>
    [Fact]
    public void RecycleLatches_SoTheFollowUpTransientsAreStillPartOfTheSameRecovery()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var hubBackAt = TimeSpan.FromMilliseconds(10_060);
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            if (scheduler.Clock >= hubBackAt.Ticks)
                return Observable.Return(42, scheduler);
            // Only the FIRST failure says "shutting down"; the rest are the ordinary transients a
            // reactivating hub produces.
            return Observable.Throw<int>(
                subscribeCount == 1
                    ? Recycling
                    : new InvalidOperationException("the target hub was not found"),
                scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source
            .RetryAreaWithBackoff(
                shouldRetry: AreaErrorClassifier.ShouldRetryArea,
                baseDelay: TimeSpan.FromMilliseconds(250),
                scheduler: scheduler)
            .Subscribe(observer);

        scheduler.Start();

        observer.Messages.Should().HaveCount(2, "the recovery is one event, whatever it reports mid-flight");
        observer.Messages[0].Value.Value.Should().Be(42);
    }

    /// <summary>
    /// The storm guard is untouched. A hub that never comes back is given the recovery budget and
    /// then FAILS — bounded, never an unbounded resubscribe — and the probes are capped at
    /// <see cref="AreaStreamRetry.DefaultRecycleBackoffCap"/>, so waiting longer never means
    /// hammering harder.
    /// </summary>
    [Fact]
    public void RecycleThatNeverReturns_StillGivesUp_WithinTheBudget_AndCappedProbes()
    {
        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return Observable.Throw<int>(Recycling, scheduler);
        });

        var observer = scheduler.CreateObserver<int>();
        source
            .RetryAreaWithBackoff(
                shouldRetry: AreaErrorClassifier.ShouldRetryArea,
                baseDelay: TimeSpan.FromMilliseconds(250),
                scheduler: scheduler)
            .Subscribe(observer);

        scheduler.Start();

        observer.Messages.Should().HaveCount(1);
        observer.Messages[0].Value.Kind.Should().Be(NotificationKind.OnError,
            "a hub that never comes back still fails — the guard is a guard, not a promise");
        var budget = AreaStreamRetry.DefaultRecycleRecoveryBudget;
        var cap = AreaStreamRetry.DefaultRecycleBackoffCap;
        TimeSpan.FromTicks(scheduler.Clock).Should().BeLessThan(budget + cap,
            "the whole recovery is bounded by the wall-clock guard");
        // Every probe costs at least the cap once the backoff has grown into it, so the count can
        // never approach the unthrottled resubscribe loop the bound exists for.
        subscribeCount.Should().BeLessThan((int)(budget.TotalMilliseconds / cap.TotalMilliseconds) + 8,
            "probes are capped in FREQUENCY, so waiting longer never means hammering harder");
    }

    /// <summary>
    /// 🚨 A ROUTING NotFound IS STILL NOT RETRIED AT ALL. The 2026-06-14 storm was an inexistent
    /// address, and the recycle policy is a strict subset of what was already retryable — it can
    /// never widen WHAT is retried, only how long an address that announced its own return is
    /// waited for.
    /// </summary>
    [Fact]
    public void RoutingNotFound_IsNotRecycling_AndStillFailsFast()
    {
        var gone = new InvalidOperationException("No node found at 'me/C/Ex'. Closest ancestor is 'me'");
        AreaErrorClassifier.IsHubRecycling(gone).Should().BeFalse("a gone address is not a recycle");
        AreaErrorClassifier.ShouldRetryArea(gone).Should().BeFalse("…and it was never retryable");
        AreaErrorClassifier.IsHubRecycling(Recycling).Should().BeTrue();
        AreaErrorClassifier.IsTransientHubFailure(Recycling).Should()
            .BeTrue("the recycle set is a SUBSET of the retryable set");

        var scheduler = new TestScheduler();
        var subscribeCount = 0;
        var source = Observable.Defer(() =>
        {
            subscribeCount++;
            return Observable.Throw<int>(gone, scheduler);
        });
        var observer = scheduler.CreateObserver<int>();
        source
            .RetryAreaWithBackoff(shouldRetry: AreaErrorClassifier.ShouldRetryArea, scheduler: scheduler)
            .Subscribe(observer);
        scheduler.Start();

        subscribeCount.Should().Be(1, "an inexistent address is not resubscribed even once");
        observer.Messages[0].Value.Kind.Should().Be(NotificationKind.OnError);
    }
}
