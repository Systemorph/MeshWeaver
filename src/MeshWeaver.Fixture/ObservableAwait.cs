using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Fixture;

/// <summary>
/// The ONE way a test waits for an observable — and the reason `.ToTask()` does not appear in
/// this repository (maintainer, 2026-08-30: <i>"no ToTask ever"</i>).
///
/// <para>🚨 Rx's own <c>ToTask()</c> completes its <c>TaskCompletionSource</c> from INSIDE the
/// pipeline without <c>RunContinuationsAsynchronously</c>, so the awaiting test resumes INLINE on
/// whichever thread signalled — the hub's disposal thread, a grain's turn scheduler — still inside
/// Rx's trampoline (<c>Producer.SubscribeRaw</c>), and every later <c>await</c> in the method
/// inherits that scheduler. An assertion is usually the last thing a test awaits before it tears
/// the mesh down, which is how #2301 and #2377 happened. So the bridge is not merely a style
/// question in tests: it changes what the test measures.</para>
///
/// <para><c>ReactiveCompletion.ObserveCompletion</c> is the safe
/// bridge — it queues the continuation instead of resuming inline, and it keeps the error arm
/// attached so a LATE fault is reported rather than swallowed. This wrapper supplies the report
/// arm every test wants: the failure is raised on the test's own thread the next time it awaits,
/// never discarded.</para>
/// </summary>
public static class ObservableAwait
{
    /// <summary>
    /// Awaits the source's first notification. Use instead of <c>.ToTask()</c>:
    /// <c>await hub.Observe&lt;TResponse&gt;(request).FirstAsync().Await(ct)</c>.
    /// </summary>
    /// <param name="source">The signal. Must terminate — a never-ending stream leaks the
    /// subscription, exactly as it does with the bridge this replaces.</param>
    /// <param name="cancellationToken">Cancels the WAIT, not the source.</param>
    public static async Task<T> Await<T>(
        this IObservable<T> source,
        CancellationToken cancellationToken = default)
        // The `!` is honest here and nowhere else: every call site pairs this with FirstAsync() /
        // LastAsync(), which THROW on an empty source rather than completing — so the "completed
        // without a value" default that ObserveCompletion can return is unreachable, and the
        // caller keeps the non-null type `.ToTask()` used to give it.
        => (await source.ObserveCompletion(
            // A fault arriving after the wait settled is the case a Task cannot represent. It is
            // rethrown on a pool thread so the run FAILS with the original stack rather than
            // discarding it — the one thing ObserveCompletion's contract forbids.
            static ex => System.Threading.Tasks.Task.Run(
                () => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()),
            cancellationToken))!;
}
