using System.Data.Common;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Data;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Classification + bounded reactive retry for TRANSIENT storage-connect faults surfacing on a
/// query provider's observable (issue #2521: a single timed-out Npgsql connector open —
/// <c>PoolingDataSource.OpenNewConnector → NpgsqlConnector.RawOpen → TimeoutException</c> —
/// failed the entire layout-area render, in recurring bursts, while warm pooled connections kept
/// serving).
///
/// <para><b>Why the retry lives HERE, in the caller's reactive chain.</b> The database adapters run
/// their I/O leaves inside capped <c>IIoPool</c>s (<c>pg:{adapter}</c> / <c>pg-read:{adapter}</c>).
/// A retry INSIDE such a leaf would hold the pooled slot across every backoff wait — stacking
/// waiters inside the very pool whose cap is the serialization guarantee. Composing the retry on
/// the provider's cold observable instead means each resubscription re-enters the pool from
/// OUTSIDE: the failed attempt's slot is long released, and the backoff wait costs nothing but an
/// <see cref="Observable.Timer(TimeSpan, IScheduler)"/>. Same shape as
/// <c>MeshWeaver.Layout.AreaStreamRetry</c>: exponential backoff on a scheduler timer, a
/// <c>shouldRetry</c> predicate selecting ONLY the transient class, and the LAST error surfaced to
/// the consumer's <c>OnError</c> once the bound is spent — never an unbounded resubscribe (the
/// 2026-06-14 storm), never a swallowed fault.</para>
///
/// <para><b>Why the predicate is typed on <see cref="DbException"/>.</b> The storage adapters live
/// in provider packages (the plugins repo), so this assembly cannot name <c>NpgsqlException</c>.
/// It does not need to: every ADO.NET driver derives its faults from the BCL
/// <see cref="DbException"/>, which carries <see cref="DbException.SqlState"/> — enough to match
/// exactly the transient connect/timeout class (client-side connect timeouts arrive as a
/// <see cref="DbException"/> wrapping a <see cref="TimeoutException"/> /
/// <see cref="System.Net.Sockets.SocketException"/>/<see cref="IOException"/>; server-side refusals
/// carry a connection-class SQLSTATE). A real query/schema error (<c>42P01</c>, <c>23505</c>, a
/// syntax error) is NOT matched and propagates unchanged, as does every non-database fault —
/// retrying those would only mask a defect.</para>
/// </summary>
public static class TransientStorageFaults
{
    /// <summary>Maximum resubscriptions after the initial attempt before the fault is surfaced.</summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>First backoff step; doubles each retry (250 → 500 → 1000 ms).</summary>
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// True when <paramref name="ex"/> is a TRANSIENT database connect/timeout fault worth a
    /// bounded retry.
    ///
    /// <para>🚨 The RULE itself lives in <see cref="StorageFaults.IsTransientConnectFault"/>, one
    /// assembly down, and this is a thin forward to it — deliberately, not for tidiness. The layout
    /// render path needs the SAME answer to decide what an area shows once this retry's budget is
    /// spent (#2876), and it sits in <c>MeshWeaver.Layout</c>, which cannot see this assembly. Two
    /// copies of the rule would drift silently: a fault this layer retries but the renderer reports
    /// as a defect, or an outage the renderer excuses that this layer never retried. What stays
    /// HERE is the retry POLICY (<see cref="DefaultMaxRetries"/>, the backoff, the
    /// pre-first-emission contract) — the part that is genuinely about the query fan-in.</para>
    ///
    /// <para>The matched class: a <see cref="DbException"/> whose <see cref="DbException.SqlState"/>
    /// is in the connection class, or one wrapping a network-level <see cref="TimeoutException"/> /
    /// <see cref="System.Net.Sockets.SocketException"/> / <see cref="IOException"/>. A real
    /// query/schema error (<c>42P01</c>, <c>23505</c>) is NOT matched — retrying those would only
    /// mask a defect — nor is a timeout WITHOUT a database exception in the chain, which is a
    /// hub/request timeout with its own policy (<c>AreaErrorClassifier.IsTransientHubFailure</c>).</para>
    /// </summary>
    /// <param name="ex">The exception to classify; may be null.</param>
    public static bool IsTransientConnectFault(Exception? ex)
        => StorageFaults.IsTransientConnectFault(ex);

    /// <summary>
    /// Wraps a COLD storage-backed observable so that an error accepted by
    /// <paramref name="shouldRetry"/> arriving BEFORE the first emission resubscribes the source
    /// after an exponentially growing, scheduler-driven delay — at most
    /// <paramref name="maxRetries"/> times — then surfaces the last error. Non-retryable errors,
    /// and any error after the stream has emitted, propagate immediately.
    ///
    /// <para><b>Why pre-first-emission only.</b> The providers' query observables are change
    /// feeds: one Initial frame, then live deltas. The fault this heals is the initial SQL read
    /// failing to obtain a connection — retrying THAT re-runs a read that produced nothing, which
    /// is free. Resubscribing after the Initial has been delivered would mint a SECOND Initial
    /// into a merge whose one-Initial-per-provider accounting has already closed; mid-stream
    /// faults therefore keep their existing semantics (propagate to the consumer, whose own layers
    /// — the area retry, the stream-cache breaker — own that recovery).</para>
    /// </summary>
    /// <param name="source">The cold provider observable; each resubscription re-runs the query.</param>
    /// <param name="shouldRetry">Selects retryable errors; defaults to <see cref="IsTransientConnectFault"/>.</param>
    /// <param name="maxRetries">Bounded retry budget; defaults to <see cref="DefaultMaxRetries"/>.</param>
    /// <param name="baseDelay">First backoff step (doubles per retry); defaults to <see cref="DefaultBaseDelay"/>.</param>
    /// <param name="scheduler">Backoff timer scheduler (inject a TestScheduler in tests).</param>
    /// <param name="onRetry">Diagnostic hook invoked per scheduled retry: (error, attempt, delay).</param>
    public static IObservable<T> RetryTransientConnect<T>(
        this IObservable<T> source,
        Func<Exception, bool>? shouldRetry = null,
        int maxRetries = DefaultMaxRetries,
        TimeSpan? baseDelay = null,
        IScheduler? scheduler = null,
        Action<Exception, int, TimeSpan>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var accept = shouldRetry ?? IsTransientConnectFault;
        var sched = scheduler ?? DefaultScheduler.Instance;
        var stepMs = (baseDelay ?? DefaultBaseDelay).TotalMilliseconds;

        return Observable.Defer(() =>
        {
            // Monotonic per consumer subscription, deliberately NOT reset per attempt: once
            // anything has been delivered downstream, no later fault may trigger a resubscribe
            // (see the pre-first-emission contract above).
            var emitted = false;
            return source
                .Do(_ => emitted = true)
                .RetryWhen(errors => errors
                    .Scan(
                        new RetryState(0, null, TimeSpan.Zero, false),
                        (state, error) => Next(state, error, emitted, accept, maxRetries, stepMs))
                    .SelectMany(state =>
                    {
                        if (state.GiveUp)
                            return Observable.Throw<long>(state.Error!);
                        onRetry?.Invoke(state.Error!, state.Attempt, state.Delay);
                        return Observable.Timer(state.Delay, sched);
                    }));
        });
    }

    // The retry decision as a pure state transition (mirrors AreaStreamRetry) — testable as an
    // assertion, not a timing observation.
    private sealed record RetryState(int Attempt, Exception? Error, TimeSpan Delay, bool GiveUp);

    private static RetryState Next(
        RetryState state, Exception error, bool emitted,
        Func<Exception, bool> shouldRetry, int maxRetries, double stepMs)
    {
        if (emitted || !shouldRetry(error) || state.Attempt >= maxRetries)
            return new RetryState(state.Attempt, error, TimeSpan.Zero, true);
        var attempt = state.Attempt + 1;
        return new RetryState(
            attempt, error, TimeSpan.FromMilliseconds(stepMs * (1L << (attempt - 1))), false);
    }
}
