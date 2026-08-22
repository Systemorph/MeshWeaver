using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// The ONE bridge from a reactive source to the <c>Task&lt;string&gt;</c> an agent tool must hand
/// back to <c>AIFunctionFactory</c>.
///
/// <para><b>Why a shared bridge and not a <see cref="TaskCompletionSource{TResult}"/> per tool.</b>
/// Every hand-rolled bridge in this folder had at least one terminal that never settled the task
/// (#1956). A tool call runs INSIDE the round's leaf on the bounded <c>IoPoolNames.Ai</c> pool and
/// holds one gate permit for its whole duration, so a task that never settles is not a slow tool —
/// it is a permit held through <c>IoPool.Drain()</c>, a Stop button that does nothing, and a
/// teardown that proceeds over live code. #1863 / #1908 fixed exactly that in
/// <see cref="DelegationTool"/> and wrote the invariant into <c>ControlledIoPooling.md</c>:
/// <i>a tool call runs inside the leaf, so its Task must observe the token</i>.</para>
///
/// <para><b>The three terminals — every one of them settles.</b></para>
/// <list type="number">
///   <item><b>A value.</b> The first emission is the answer; the subscription is disposed at once
///     (nothing keeps reading a node stream whose hub may be going away).</item>
///   <item><b>An error.</b> Formatted into an answer — on an agent-facing tool the failure text IS
///     the result the model must see.</item>
///   <item><b>An EMPTY completion.</b> 🚨 The terminal every hand-rolled bridge missed. A
///     2-argument <c>Subscribe(onNext, onError)</c> never fires for a source that completes
///     without emitting, so the task stays pending forever. And an empty completion is REACHABLE:
///     <c>MeshNodeStreamCache</c> completes its per-path subjects on eviction and on dispose, a
///     <c>.Where(...)</c>-filtered stream completes when nothing passes the filter, and a
///     <c>.Take(1)</c> over a permission evaluator completes empty if the evaluator does.
///     <c>Timeout</c> does NOT cover this: it passes an empty <c>OnCompleted</c> straight through.
///     A <c>yield break</c> is a silence; the empty answer is an ANSWER — so the caller supplies
///     one and it is always sent.</item>
/// </list>
///
/// <para><b>And cancellation is a fourth.</b> The token is registered before the source is
/// subscribed, and firing it both cancels the task and DISPOSES the subscription, so the work the
/// tool started actually stops rather than running on unobserved.</para>
/// </summary>
public static class ToolTask
{
    /// <summary>
    /// Bridges <paramref name="source"/> to a task that settles on the first emission, on an error,
    /// on an empty completion, or on <paramref name="cancellationToken"/> — whichever happens
    /// first — and disposes the subscription in every one of those cases.
    /// </summary>
    /// <typeparam name="T">Element type of the source.</typeparam>
    /// <param name="source">The reactive operation backing the tool call. Only its FIRST emission
    /// is used; the subscription is disposed as soon as the task settles.</param>
    /// <param name="cancellationToken">The round's token, as handed to the tool by
    /// <c>AIFunctionFactory</c>. Cancelling it cancels the returned task and disposes the
    /// subscription.</param>
    /// <param name="onNext">Formats the first emission into the tool's answer.</param>
    /// <param name="onError">Formats a fault into the tool's answer. Also covers a throw from
    /// <paramref name="onNext"/>, which Rx routes down the error channel.</param>
    /// <param name="onEmpty">The answer for a source that completes without emitting. Never
    /// omit it: silence is what leaves the round parked.</param>
    /// <returns>A task that always settles.</returns>
    public static Task<string> Bridge<T>(
        IObservable<T> source,
        CancellationToken cancellationToken,
        Func<T, string> onNext,
        Func<Exception, string> onError,
        Func<string> onEmpty)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onNext);
        ArgumentNullException.ThrowIfNull(onError);
        ArgumentNullException.ThrowIfNull(onEmpty);

        // RunContinuationsAsynchronously: without it the caller's continuation resumes INLINE on
        // whichever thread settled us — for a hub-backed source that is the hub's response-dispatch
        // thread, and running an agent round's continuation there is how a hub scheduler gets
        // occupied by work that then waits on the same hub.
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Everything the wait holds: the cancellation registration and the subscription. Whichever
        // terminal fires first releases both — a bare TrySet* would settle the caller and leave the
        // subscription live against a hub that may already be tearing down.
        var pending = new CompositeDisposable();
        void Settle(Func<bool> set)
        {
            if (set())
                pending.Dispose();
        }

        // Registered FIRST so an already-cancelled token settles before the source is subscribed;
        // CompositeDisposable disposes anything added after its own disposal, so the subscription
        // below is torn down immediately in that case.
        pending.Add(cancellationToken.Register(() => Settle(() => tcs.TrySetCanceled(cancellationToken))));

        // Take(1) → Select → DefaultIfEmpty is what turns the missing terminal into a real one: the
        // null that reaches onNext below can ONLY come from DefaultIfEmpty, because onNext never
        // returns null. A throw from the caller's formatter lands on the error channel.
        pending.Add(source
            .Take(1)
            .Select(value => (string?)onNext(value))
            .DefaultIfEmpty()
            .Subscribe(
                answer => Settle(() => tcs.TrySetResult(answer ?? onEmpty())),
                error => Settle(() => tcs.TrySetResult(onError(error)))));

        return tcs.Task;
    }
}
