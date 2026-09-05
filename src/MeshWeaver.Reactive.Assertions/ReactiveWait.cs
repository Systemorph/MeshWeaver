using System.Diagnostics;
using System.Reactive.Disposables;

namespace MeshWeaver.Reactive.Assertions;

/// <summary>
/// The wait every terminal assertion in this package is built on: subscribe, and settle a
/// <see cref="Task{T}"/> on the source's FIRST notification — its value, its completion, or its
/// fault — <b>without blocking, and without resuming the awaiting test on the thread that
/// signalled</b>.
///
/// <para>🚨 <b>Why this exists instead of Rx's own observable-to-<see cref="Task"/> bridge</b>
/// (maintainer ruling, 2026-08-30: <i>"no ToTask ever"</i>; the earlier "tests are the one
/// sanctioned place" exemption is RETRACTED). Rx's bridge completes its
/// <see cref="TaskCompletionSource{TResult}"/> from INSIDE the Rx pipeline, without
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so
/// <c>TrySetResult</c> resumes the awaiter <b>inline, on the signalling thread, still inside Rx's
/// trampoline</b> (<c>Producer.SubscribeRaw</c>). Everything the continuation then does inherits
/// that — a captured 558-frame stack in the reproduction shows it escaping the pipeline entirely,
/// and it is sticky: <c>await</c> captures <see cref="TaskScheduler"/>.<see cref="TaskScheduler.Current"/>
/// when there is no <see cref="SynchronizationContext"/>, so once one continuation lands on that
/// scheduler every later <c>await</c> in the same method schedules onto it too.</para>
///
/// <para>That is not a test-only concern. An assertion is the LAST thing a test awaits before it
/// asserts and tears down, so the scheduler it resumes on is the scheduler the rest of the test —
/// and the mesh teardown it triggers — runs on. Under xUnit the reach is wider still: the resumed
/// continuation carries the runner itself, which then starts SUBSEQUENT tests inside the
/// trampoline. A bridge written "only in a test" therefore changes how the code under test runs,
/// and a green test proves the wrong thing.</para>
///
/// <para><see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> below is the whole fix,
/// and it is load-bearing rather than tidiness: it is the line that queues the test's continuation
/// instead of running it on the producer's thread.</para>
///
/// <para>This mirrors <c>MeshWeaver.Messaging.ReactiveCompletion.ObserveCompletion</c>, which is
/// the same guarantee for the mesh proper. It is duplicated here on purpose: this package ships
/// standalone and depends only on <c>System.Reactive</c>, so it cannot reference the mesh.</para>
/// </summary>
internal static class ReactiveWait
{
    /// <summary>
    /// Subscribes to <paramref name="source"/> and returns a task that settles on its first
    /// notification: the value, its completion (with <c>default</c>), or its fault.
    ///
    /// <para><b>The wait unsubscribes BEFORE it settles</b>, so "this assertion has returned"
    /// implies "this assertion is no longer a subscriber". Tests assert on that: a subject whose
    /// point is that nobody is watching any more (a refcounted cache entry, a released claim)
    /// cannot be measured by a caller that is still attached. A late fault — one arriving after
    /// the task settled, which the task can no longer carry — is traced rather than orphaned, so
    /// it never reaches the finalizer as a <see cref="TaskScheduler.UnobservedTaskException"/>
    /// (xUnit v3 escalates those to a Catastrophic failure that poisons the NEXT test class).</para>
    /// </summary>
    /// <typeparam name="T">The source's element type.</typeparam>
    /// <param name="source">The (terminating) source to wait on.</param>
    /// <returns>The first value, or <c>default</c> if the source completed without one.</returns>
    public static Task<T?> First<T>(IObservable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 🚨 DISPOSE THE SUBSCRIPTION WHEN THE WAIT SETTLES. Rx's own bridge did; dropping the
        // IDisposable leaves a live subscriber on the asserted stream for the rest of the process.
        // Assertions in a class share subjects, so every settled assertion keeps consuming and a
        // later one starves — NodeTypeCompileParkTest.RecycleRetry, ~40% of main runs after #2748,
        // reported as "Last of N emission(s), none matched" at the full timeout.
        //
        // 🚨 AND IT DISPOSES *INSIDE THE HANDLER*, AHEAD OF THE SETTLE — never as a continuation
        // on `completion.Task`. A continuation cannot get there first, and the gap is not
        // theoretical:
        //
        //   * `RunContinuationsAsynchronously` QUEUES the awaiter the instant TrySetResult runs,
        //     so the test thread is already released while the producer thread is still unwinding;
        //   * the operator chain every call site builds ends `…Take(1).ToList().Timeout(…)`, and
        //     `Take(1)` disposes its upstream from inside `ForwardOnCompleted` — which runs AFTER
        //     it has pushed the value through `ToList` into the handler here. So the unsubscribe
        //     is structurally LAST, not merely late.
        //
        // A test that then measures "is anything still subscribed?" reads its own wait.
        // ChangeFeedResetReleasesUpstreamTest.ControlArm_ReleaseIfUnwatched_… did exactly that:
        // `MeshNodeStreamCache.ReleaseIfUnwatched` refuses an entry with a live subscriber
        // (`Entry.TryMarkIdleEvicted`), and the only subscriber left was this wait, so the release
        // returned false and the control arm failed on a state it had never observed
        // (run 33937167117, shard 4). Disposing here puts the unsubscribe on the producer's thread
        // ahead of the settle — where Rx's own First/Take put it — so "the wait settled" IMPLIES
        // "the wait unsubscribed", and an assertion may rely on that.
        //
        // A source that emits synchronously during Subscribe is covered too, and by construction:
        // the caller has no task to await until `First` returns, and `SingleAssignmentDisposable`
        // disposes the real subscription the moment it is assigned — before that return.
        //
        // The late-fault trace below therefore only covers faults arriving BEFORE disposal, which
        // is the honest trade: a fault from a stream nobody is asserting on any more is noise, and
        // keeping the subscription alive to catch it is what broke the suite.
        var subscription = new SingleAssignmentDisposable();
        subscription.Disposable = source.Subscribe(
            value =>
            {
                subscription.Dispose();
                completion.TrySetResult(value);
            },
            error =>
            {
                subscription.Dispose();
                // Before the task settled this IS the answer; after it, the task can no longer
                // carry it — so it goes to the trace rather than nowhere.
                if (completion.TrySetException(error))
                    return;
                Trace.TraceError(
                    "ReactiveWait: the asserted stream faulted AFTER the wait settled — reported, "
                    + "not orphaned: {0}: {1}", error.GetType().Name, error.Message);
            },
            () =>
            {
                subscription.Dispose();
                completion.TrySetResult(default);
            });

        return completion.Task;
    }
}
