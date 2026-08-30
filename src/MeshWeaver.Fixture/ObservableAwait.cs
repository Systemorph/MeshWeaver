using System.Reactive.Linq;

namespace MeshWeaver.Fixture;

/// <summary>
/// The ONE way a test waits for an observable — and the reason <c>.ToTask()</c> does not appear in
/// this repository (maintainer, 2026-08-30: <i>"no ToTask ever"</i>).
///
/// <para>🚨 Rx's own <c>ToTask()</c> completes its <c>TaskCompletionSource</c> WITHOUT
/// <c>RunContinuationsAsynchronously</c>, so the awaiting test resumes INLINE on whichever thread
/// signalled — the hub's disposal thread, a grain's turn scheduler — still inside Rx's trampoline
/// (<c>Producer.SubscribeRaw</c>), and every later <c>await</c> in the method inherits that
/// scheduler. An assertion is usually the last thing a test awaits before it tears the mesh down,
/// which is how #2301 and #2377 happened. The bridge is therefore not a style question in tests: it
/// changes what the test measures. Awaiting the observable DIRECTLY (<c>await source</c>) is the
/// same defect wearing different clothes — Rx's awaiter is an <c>AsyncSubject</c> and resumes
/// inline too.</para>
///
/// <para>🚨 <b>This is a faithful <c>ToTask</c>, not a first-notification wait.</b> That
/// distinction is the whole correctness of a 1,500-call-site sweep: <c>ToTask()</c> yields the
/// source's LAST value and FAULTS on an empty sequence. A helper that settled on the first
/// notification and returned <c>default</c> for an empty one would silently change 462 call sites
/// that do not reduce to a single element (<c>Take</c>, <c>ToList</c>, <c>FirstOrDefaultAsync</c>,
/// <c>DefaultIfEmpty</c>, or no reducer at all) — a null flowing into an assertion where an
/// exception used to be raised, and a FIRST value where the test asserted the LAST. So the
/// semantics below are copied deliberately; only the continuation scheduling differs.</para>
/// </summary>
public static class ObservableAwait
{
    /// <summary>
    /// Awaits the source the way <c>.ToTask()</c> did — last value, faulting on an empty sequence —
    /// but queues the continuation instead of resuming the caller on the signalling thread.
    /// </summary>
    /// <param name="source">The source. Must terminate; a never-ending one hangs exactly as the
    /// bridge it replaces did, so keep the <c>.Timeout(...)</c> the call site already has.</param>
    /// <param name="cancellationToken">Cancels the WAIT, not the source.</param>
    public static Task<T> Await<T>(
        this IObservable<T> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // 🚨 RunContinuationsAsynchronously IS THE FIX — see the type remarks. Without it this is
        // Rx's ToTask() with extra steps.
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasValue = false;
        T last = default!;

        var subscription = source.Subscribe(
            value => { last = value; hasValue = true; },
            error => completion.TrySetException(error),
            () =>
            {
                if (hasValue)
                    completion.TrySetResult(last);
                else
                    // The same fault Rx raises, with the same type, so a test that relied on the
                    // empty-source throw keeps failing for the same reason.
                    completion.TrySetException(
                        new InvalidOperationException("Sequence contains no elements"));
            });

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<T>)state!).TrySetCanceled(), completion);
            _ = completion.Task.ContinueWith(
                (_, state) =>
                {
                    var (reg, sub) = ((CancellationTokenRegistration, IDisposable))state!;
                    reg.Dispose();
                    sub.Dispose();
                },
                (registration, subscription),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _ = completion.Task.ContinueWith(
                (_, state) => ((IDisposable)state!).Dispose(),
                subscription,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return completion.Task;
    }
}
