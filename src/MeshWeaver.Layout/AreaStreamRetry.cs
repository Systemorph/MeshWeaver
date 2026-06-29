using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace MeshWeaver.Layout;

/// <summary>
/// Bounded, throttled, fully-reactive retry for a layout-area subscription whose target
/// address may be <i>transiently</i> unaddressable — the per-node hub is still
/// bootstrapping, the NodeType is mid-compile, or the compile/import <c>_Activity</c> node
/// it embeds is not yet routable.
///
/// <para>The two failure modes this sits between:</para>
/// <list type="bullet">
///   <item><b>No retry</b> (the old GUI behaviour): a genuinely transient miss never
///   self-heals — the area stays blank until the user re-navigates.</item>
///   <item><b>Unbounded retry</b> (the atioz wedge, 2026-06-14): resubscribing forever to
///   an <i>inexistent</i> address produced an endless <c>[ROUTE] NotFound</c> message storm
///   that burned a core and wedged the partition's hub. "Wedging usually means uncaught
///   exception and endless messages, especially with inexistent addresses."</item>
/// </list>
///
/// <para>So: retry a <b>bounded</b> number of times with <b>exponential backoff</b>
/// (<see cref="Observable.Timer(TimeSpan, IScheduler)"/> — never <c>Task.Delay</c>) for
/// errors the caller classifies as retryable, then <b>give up and surface the last error</b>
/// to the caller's <c>OnError</c> so the GUI can report a real failure instead of spinning.</para>
/// </summary>
public static class AreaStreamRetry
{
    /// <summary>Default number of reactive retry attempts before giving up.</summary>
    public const int DefaultMaxRetries = 5;

    /// <summary>
    /// Wraps <paramref name="source"/> so that, on an error accepted by
    /// <paramref name="shouldRetry"/>, it resubscribes after an exponentially growing,
    /// scheduler-driven delay — at most <paramref name="maxRetries"/> times — then
    /// propagates the error. Non-retryable errors propagate immediately (no delay),
    /// preserving fast-path handling (e.g. a CompilationInProgress NACK that the caller
    /// swaps to the Progress view at once).
    /// </summary>
    /// <param name="source">The cold area/control stream.</param>
    /// <param name="shouldRetry">Predicate selecting which errors are worth retrying
    /// (transient hub miss / not-yet-routable). Return <c>false</c> to fail fast.</param>
    /// <param name="maxRetries">Maximum reactive retries before giving up.</param>
    /// <param name="baseDelay">First backoff step; doubles each attempt. Default 250 ms.</param>
    /// <param name="scheduler">Scheduler for the backoff timer (inject a TestScheduler in
    /// tests). Defaults to <see cref="DefaultScheduler.Instance"/>.</param>
    public static IObservable<T> RetryAreaWithBackoff<T>(
        this IObservable<T> source,
        Func<Exception, bool> shouldRetry,
        int maxRetries = DefaultMaxRetries,
        TimeSpan? baseDelay = null,
        IScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(shouldRetry);
        var sched = scheduler ?? DefaultScheduler.Instance;
        var stepMs = (baseDelay ?? TimeSpan.FromMilliseconds(250)).TotalMilliseconds;

        return source.RetryWhen(errors => errors
            .Select((error, index) => (Error: error, Attempt: index))
            .SelectMany(t =>
                // Give up (and surface the error) when the error isn't retryable OR the
                // attempt budget is spent. Attempt is 0-based, so `>= maxRetries` allows
                // exactly maxRetries retries (maxRetries+1 total subscriptions).
                !shouldRetry(t.Error) || t.Attempt >= maxRetries
                    ? Observable.Throw<long>(t.Error)
                    // Throttled, scheduler-driven backoff: step, 2·step, 4·step, …
                    : Observable.Timer(TimeSpan.FromMilliseconds(stepMs * (1L << t.Attempt)), sched)));
    }
}
