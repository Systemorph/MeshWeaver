using System.Diagnostics;

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
    /// <para>The subscription deliberately OUTLIVES the returned task. Unsubscribing on settle is
    /// what Rx's bridge does and what loses a fault arriving just after a timeout fired — an
    /// unobserved exception, surfaced on the finalizer as
    /// <see cref="TaskScheduler.UnobservedTaskException"/>, which xUnit v3 escalates to a
    /// Catastrophic failure that poisons the NEXT test class. Here the error arm stays attached
    /// and a late fault is traced instead. Every source this is pointed at terminates (each call
    /// site ends in <c>FirstAsync</c>, <c>ToList</c>, <c>IgnoreElements</c> or <c>Timeout</c>), so
    /// the subscription releases itself.</para>
    /// </summary>
    /// <typeparam name="T">The source's element type.</typeparam>
    /// <param name="source">The (terminating) source to wait on.</param>
    /// <returns>The first value, or <c>default</c> if the source completed without one.</returns>
    public static Task<T?> First<T>(IObservable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var completion = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);

        source.Subscribe(
            value => completion.TrySetResult(value),
            error =>
            {
                // Before the task settled this IS the answer; after it, the task can no longer
                // carry it — so it goes to the trace rather than nowhere.
                if (completion.TrySetException(error))
                    return;
                Trace.TraceError(
                    "ReactiveWait: the asserted stream faulted AFTER the wait settled — reported, "
                    + "not orphaned: {0}: {1}", error.GetType().Name, error.Message);
            },
            () => completion.TrySetResult(default));

        return completion.Task;
    }
}
