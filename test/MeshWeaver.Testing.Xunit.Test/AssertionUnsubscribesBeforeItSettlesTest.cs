using Xunit;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Testing.Xunit.Test;

/// <summary>
/// The contract <c>ReactiveWait</c> owes every assertion built on it: <b>when the wait settles it
/// has already unsubscribed</b>. Tests measure things whose whole subject is "nobody is watching
/// this any more" — a refcounted cache entry, a released claim, an idle sweep that may only reap
/// an unwatched path — and such a test cannot be correct if the assertion it just awaited is
/// still attached to the subject it is about to measure.
///
/// <para>This was not hypothetical. <c>ReactiveWait</c> disposed as a CONTINUATION on the settled
/// task, and the continuation cannot get there first: the task is created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so <c>TrySetResult</c> queues
/// the awaiting test the instant it runs, while the unsubscribe was still travelling back up the
/// producer's stack (<c>Take(1)</c> disposes its upstream from inside <c>ForwardOnCompleted</c>,
/// i.e. AFTER it has pushed the value into the handler). MeshWeaver#3332's shard-4 red was exactly
/// that: <c>ChangeFeedResetReleasesUpstreamTest.ControlArm_ReleaseIfUnwatched_…</c> asked
/// <c>MeshNodeStreamCache.ReleaseIfUnwatched</c> to claim an entry whose only remaining subscriber
/// was the wait that had just returned, and the release refused it (run 33937167117).</para>
/// </summary>
public class AssertionUnsubscribesBeforeItSettlesTest
{
    /// <summary>
    /// How long the source's teardown holds the producer's thread, waiting to be told the awaiting
    /// test has already run. This is deliberate FAULT INJECTION, not a wait for propagation: the
    /// real window between "settle" and "unsubscribe" is a handful of instructions wide, so on a
    /// quiet machine the producer wins it nearly every time and a guard written without this would
    /// pass while the defect was present — a guard that cannot fail. Widening the window makes the
    /// ORDER observable. On correct code nothing is ever signalled, so this bound is also the
    /// (one-off) price the healthy path pays.
    /// </summary>
    private static readonly TimeSpan TeardownHold = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task AWaitThatHasSettled_HasAlreadyReleasedItsSubscription()
    {
        var relay = new Subject<int>();
        var subscribers = 0;
        var awaiterRan = 0;

        // A source that COUNTS its live subscribers, exactly like the refcounted shared views a
        // real test measures, and that holds the producer inside teardown long enough for a
        // prematurely-resumed awaiter to read the count while it is still 1.
        var counted = Observable.Create<int>(observer =>
        {
            Interlocked.Increment(ref subscribers);
            var inner = relay.Subscribe(observer);
            return Disposable.Create(() =>
            {
                SpinWait.SpinUntil(() => Volatile.Read(ref awaiterRan) == 1, TeardownHold);
                inner.Dispose();
                Interlocked.Decrement(ref subscribers);
            });
        });

        // Emit() subscribes synchronously before it hands back the task, so the source is live here.
        var waiting = counted.Should().Within(TimeSpan.FromSeconds(30)).Emit();
        Volatile.Read(ref subscribers).Should().Be(1,
            "precondition: the assertion subscribed when it was armed, so there is a refcount to release");

        // The value must arrive on a thread that is NOT the one the awaiter resumes on — the defect
        // IS the producer and the awaiter running concurrently. TaskPoolScheduler, not Task.Run:
        // this package's discipline is Rx end to end.
        TaskPoolScheduler.Default.Schedule(() => relay.OnNext(42));

        var value = await waiting;
        Volatile.Write(ref awaiterRan, 1);

        value.Should().Be(42, "the wait settles on the source's first value");
        Volatile.Read(ref subscribers).Should().Be(0,
            "the wait had already unsubscribed when it settled — an assertion whose subject is "
            + "'is anything still watching?' reads this, and reads it with no grace period");
    }
}
