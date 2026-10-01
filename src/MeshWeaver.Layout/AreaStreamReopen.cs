using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace MeshWeaver.Layout;

/// <summary>
/// Re-OPENS a layout area's stream after a deadline miss, paced — policy
/// <c>area-view-reopens-on-deadline-miss</c> (issues #5599 and #5714).
///
/// <para><b>Why a re-open and not a retry.</b> An area stream is a <c>SynchronizationStream</c>,
/// whose store is a <c>ReplaySubject</c>: once the owner's <c>SubscribeRequest</c> comes back with
/// the transport's deadline miss, the stream latches that fault and replays it to every later
/// subscriber. <see cref="AreaStreamRetry.RetryAreaWithBackoff{T}"/> re-subscribes its SOURCE, so
/// pointed at that stream it spends all its attempts on the replayed error in a few seconds and the
/// view shows "Area unavailable" until a reload — although the owner, which was only slow, would
/// have served the next frame. A recoverable miss needs a FRESH stream, i.e. a new
/// <c>SubscribeRequest</c>, which is what the <c>open</c> factory produces on every call.</para>
///
/// <para><b>Why this cannot storm.</b> Three bounds, all structural:</para>
/// <list type="bullet">
///   <item>Only a deadline miss re-opens (<see cref="AreaErrorClassifier.IsDeadlineMiss"/> by
///   default). Every other error — NotFound, Unavailable, an initialisation failure, a real
///   exception — propagates on the first occurrence, exactly as before.</item>
///   <item>At most ONE open per <see cref="DefaultMinReopenInterval"/>, measured from the previous
///   OPEN, on the injected scheduler. A stream that replays its fault instantly therefore waits out
///   the rest of the interval before the next open; it never re-opens in a loop.</item>
///   <item>Against a HUNG owner each open itself waits the transport deadline before it fails, so
///   the realised rate is one <c>SubscribeRequest</c> per deadline per open view — the cost the
///   decision accepted.</item>
/// </list>
///
/// <para>No deadline is changed by this: the transport's 30 s response deadline still decides when
/// an open has failed. This only decides what happens next.</para>
///
/// <para>🚨 This does NOT reverse the router's verdict. <c>RoutingGrain.ClassifyDeliveryException</c>
/// still reports a bare timeout as terminal (<c>ErrorType.Failed</c>), so a consumer with UNBOUNDED
/// recovery machinery (<c>SynchronizationStream</c>'s resubscribe latch, <c>MeshNodeStreamCache</c>)
/// still tears down. The re-open is the view's own, paced, decision — the "timeouts are terminal"
/// rule stays true for every other path.</para>
/// </summary>
public static class AreaStreamReopen
{
    /// <summary>
    /// The minimum gap between two opens of the same view's stream: 30 s, the transport's own
    /// response deadline, so a view never asks a slow owner more often than the owner is given to
    /// answer one request.
    /// </summary>
    public static readonly TimeSpan DefaultMinReopenInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Subscribes to <c>open()</c>; when that stream fails with a deadline miss, calls
    /// <paramref name="open"/> again for a FRESH stream — no sooner than
    /// <paramref name="minInterval"/> after the previous open — and continues with it. Any other
    /// error propagates.
    /// </summary>
    /// <typeparam name="T">The element type of the area stream.</typeparam>
    /// <param name="open">Opens a NEW stream (a new subscription to the owner) on each call. Must not
    /// hand back a stream that has already faulted — that is the defect this exists to avoid.</param>
    /// <param name="isDeadlineMiss">Selects the errors that re-open. Defaults to
    /// <see cref="AreaErrorClassifier.IsDeadlineMiss"/>.</param>
    /// <param name="minInterval">Minimum gap between two opens. Defaults to
    /// <see cref="DefaultMinReopenInterval"/>.</param>
    /// <param name="scheduler">Clock and timer for the pacing (inject a <c>TestScheduler</c> in
    /// tests). Defaults to <see cref="DefaultScheduler.Instance"/>.</param>
    /// <param name="onReopen">Called with the deadline miss and the wait before the re-open, so a
    /// caller can log it. Optional.</param>
    /// <returns>The values of the current stream; errors only for a non-deadline failure.</returns>
    public static IObservable<T> ReopenOnDeadlineMiss<T>(
        Func<IObservable<T>> open,
        Func<Exception, bool>? isDeadlineMiss = null,
        TimeSpan? minInterval = null,
        IScheduler? scheduler = null,
        Action<Exception, TimeSpan>? onReopen = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        var reopens = isDeadlineMiss ?? AreaErrorClassifier.IsDeadlineMiss;
        var interval = minInterval ?? DefaultMinReopenInterval;
        var sched = scheduler ?? DefaultScheduler.Instance;

        return Observable.Defer(() =>
        {
            // Per SUBSCRIPTION, never shared: two views of one area pace independently. Read and
            // written only inside the serialized RetryWhen chain (an open, then its error, then the
            // next open), so no two writers race.
            var lastOpen = DateTimeOffset.MinValue;
            return Observable
                .Defer(() =>
                {
                    lastOpen = sched.Now;
                    return open();
                })
                .RetryWhen(errors => errors.SelectMany(error =>
                {
                    if (!reopens(error))
                        return Observable.Throw<long>(error);
                    var wait = lastOpen + interval - sched.Now;
                    if (wait < TimeSpan.Zero)
                        wait = TimeSpan.Zero;
                    onReopen?.Invoke(error, wait);
                    // A miss that arrived a whole interval after its open (the normal case: the
                    // open itself waited out the deadline) re-opens NOW, not one scheduler hop
                    // later; only an early miss — a replayed fault — waits out the remainder.
                    return wait == TimeSpan.Zero
                        ? Observable.Return(0L)
                        : Observable.Timer(wait, sched);
                }));
        });
    }
}
