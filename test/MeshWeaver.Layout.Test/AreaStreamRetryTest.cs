using System;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the bounded, throttled, fully-reactive retry that protects layout-area
/// subscriptions from the inexistent-address message storm (the atioz wedge,
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
}
