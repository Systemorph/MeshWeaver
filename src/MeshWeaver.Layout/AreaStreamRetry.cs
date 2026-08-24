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
///   <item><b>Unbounded retry</b> (the prod wedge, 2026-06-14): resubscribing forever to
///   an <i>inexistent</i> address produced an endless <c>[ROUTE] NotFound</c> message storm
///   that burned a core and wedged the partition's hub. "Wedging usually means uncaught
///   exception and endless messages, especially with inexistent addresses."</item>
/// </list>
///
/// <para>So: retry a <b>bounded</b> number of times with <b>exponential backoff</b>
/// (<see cref="Observable.Timer(TimeSpan, IScheduler)"/> — never <c>Task.Delay</c>) for
/// errors the caller classifies as retryable, then <b>give up and surface the last error</b>
/// to the caller's <c>OnError</c> so the GUI can report a real failure instead of spinning.</para>
///
/// <para>🚨 <b>…except a RECYCLE, which is a third thing.</b> Systemorph/MeshWeaver#1996: a
/// per-node hub recycle is a normal lifecycle event — it happens on every package provision, right
/// after the content lands — and it announces itself
/// (<c>"Hub X is shutting down"</c>, <see cref="AreaErrorClassifier.IsHubRecycling"/>). That is not
/// the inexistent address the bound above exists for; it is the address telling you it is coming
/// back. Counting it against a fixed attempt budget killed the page: measured, the client spent its
/// five retries (250·2ⁿ ms = 7.75 s) and gave up <b>2.2 s before</b> the hub was serving again.</para>
///
/// <para><b>The bound was standing where an EVENT belongs.</b> What ends a recycle is the hub
/// ANSWERING, and that is observable — so once a stream has been told the hub is recycling, this
/// stops counting attempts and keeps probing on a CAPPED backoff until the address answers or a
/// last-resort wall-clock guard expires. Raising 5 → N would only move the cliff, which is why that
/// is not what this does: the exit condition changed, not the number.</para>
///
/// <para>The storm risk is untouched in both directions. The recycle policy is a strict SUBSET of
/// what was already retryable (<see cref="AreaErrorClassifier.IsHubRecycling"/> ⊂
/// <see cref="AreaErrorClassifier.IsTransientHubFailure"/>), so it can never widen WHAT is retried;
/// <c>"No node found at 'X'"</c> is not transient, is not retried, and still fails fast. And a
/// probe every <see cref="DefaultRecycleBackoffCap"/> at worst is nothing like the unthrottled
/// resubscribe loop that burned a core in 2026-06-14.</para>
/// </summary>
public static class AreaStreamRetry
{
    /// <summary>Default number of reactive retry attempts before giving up.</summary>
    public const int DefaultMaxRetries = 5;

    /// <summary>
    /// The longest gap between two probes of a hub that announced it is RECYCLING. The backoff
    /// still doubles up to here (250 ms → 500 → 1 s → 2 s) and then holds, so a recovery that takes
    /// a while is polled at a steady, cheap rate rather than at an exponentially receding one that
    /// would answer late by construction.
    /// </summary>
    public static readonly TimeSpan DefaultRecycleBackoffCap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The LAST-RESORT wall-clock guard on waiting for a recycling hub — not the thing that decides
    /// when to stop waiting (that is the hub answering), only the thing that stops an unbounded
    /// wait if it never does.
    ///
    /// <para><b>Why 60 s, and why that is not "7.75 s plus a margin".</b> The measurement in
    /// Systemorph/MeshWeaver#1996 is a recycle→serving gap of <b>10.06 s</b>
    /// (20:35:25.492 → 20:35:35.552), and sizing a bound just above one observed sample is how you
    /// get the same dead page on a slower pod. The number here is instead the framework's own
    /// last-resort terminal for a hub that has not answered — <c>MessageHub.RequestTimeout</c>,
    /// 60 s by default, the budget every unbudgeted request already waits. So the client waits
    /// exactly as long for a recycling hub as the framework itself is willing to wait for any hub,
    /// and there is no new number to keep in sync with reality.</para>
    /// </summary>
    public static readonly TimeSpan DefaultRecycleRecoveryBudget = TimeSpan.FromSeconds(60);

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
    /// <param name="isRecycling">Selects the errors that say the target hub is COMING BACK
    /// (<see cref="AreaErrorClassifier.IsHubRecycling"/> by default). Seeing one LATCHES the
    /// recycle policy for the rest of this retry sequence: subsequent retryable errors are part of
    /// the same recovery — a recycling hub reports "shutting down", then "target hub was not
    /// found", then answers — so re-counting them against <paramref name="maxRetries"/> would give
    /// up in the middle of the very recovery the first error announced.</param>
    /// <param name="recycleBackoffCap">Ceiling on the gap between probes while recycling.</param>
    /// <param name="recycleRecoveryBudget">Last-resort wall-clock guard on the whole recovery.</param>
    public static IObservable<T> RetryAreaWithBackoff<T>(
        this IObservable<T> source,
        Func<Exception, bool> shouldRetry,
        int maxRetries = DefaultMaxRetries,
        TimeSpan? baseDelay = null,
        IScheduler? scheduler = null,
        Func<Exception, bool>? isRecycling = null,
        TimeSpan? recycleBackoffCap = null,
        TimeSpan? recycleRecoveryBudget = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(shouldRetry);
        var sched = scheduler ?? DefaultScheduler.Instance;
        var stepMs = (baseDelay ?? TimeSpan.FromMilliseconds(250)).TotalMilliseconds;
        var recycling = isRecycling ?? AreaErrorClassifier.IsHubRecycling;
        var capMs = (recycleBackoffCap ?? DefaultRecycleBackoffCap).TotalMilliseconds;
        var budgetMs = (recycleRecoveryBudget ?? DefaultRecycleRecoveryBudget).TotalMilliseconds;

        return source.RetryWhen(errors => errors
            // Scan, not Select((e, i) => …): the decision needs STATE — how much of the attempt
            // budget is spent, whether a recycle has been announced, and how long we have been
            // waiting for it — and that state must survive across errors of different kinds.
            .Scan(
                new RetryState(-1, -1, 0d, false, null, TimeSpan.Zero, false),
                (state, error) => Next(state, error, shouldRetry, recycling, maxRetries, stepMs,
                    capMs, budgetMs))
            .SelectMany(state => state.GiveUp
                ? Observable.Throw<long>(state.Error!)
                : Observable.Timer(state.Delay, sched)));
    }

    // The retry decision as PURE state transition — no scheduler, no clock, no source. What makes
    // "an announced recycle stops the attempt count and starts a capped probe" testable as an
    // assertion rather than as a timing observation.
    private sealed record RetryState(
        int Attempt, int RecycleAttempt, double RecycleWaitedMs, bool Recycling,
        Exception? Error, TimeSpan Delay, bool GiveUp);

    private static RetryState Next(
        RetryState state, Exception error, Func<Exception, bool> shouldRetry,
        Func<Exception, bool> recycling, int maxRetries, double stepMs, double capMs, double budgetMs)
    {
        if (!shouldRetry(error))
            return state with { Error = error, Delay = TimeSpan.Zero, GiveUp = true };

        // Once the hub has said it is coming back, every retryable error until it answers is part
        // of that recovery — see the isRecycling remark on why the latch, not the message, decides.
        var latched = state.Recycling || recycling(error);
        if (!latched)
        {
            var attempt = state.Attempt + 1;
            return attempt >= maxRetries
                ? state with { Attempt = attempt, Error = error, Delay = TimeSpan.Zero, GiveUp = true }
                : state with
                {
                    Attempt = attempt,
                    Error = error,
                    Delay = TimeSpan.FromMilliseconds(stepMs * (1L << attempt)),
                    GiveUp = false,
                };
        }

        var probe = state.RecycleAttempt + 1;
        // Shift capped at 20 so a very long recovery cannot overflow the shift; the Min against
        // capMs is what actually bounds the gap.
        var waitMs = Math.Min(stepMs * (1L << Math.Min(probe, 20)), capMs);
        var waited = state.RecycleWaitedMs + waitMs;
        return waited > budgetMs
            ? state with { Recycling = true, Error = error, Delay = TimeSpan.Zero, GiveUp = true }
            : state with
            {
                RecycleAttempt = probe,
                RecycleWaitedMs = waited,
                Recycling = true,
                Error = error,
                Delay = TimeSpan.FromMilliseconds(waitMs),
                GiveUp = false,
            };
    }
}
