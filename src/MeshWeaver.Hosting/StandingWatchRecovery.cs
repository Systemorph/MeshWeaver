using System.Reactive.Concurrency;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting;

/// <summary>
/// 🚨 <b>A STANDING watch must re-open after a transient fault of the infrastructure it reads —
/// bounded, and only for faults that are typed as transient</b> (MeshWeaver#6183).
///
/// <para><b>The defect this closes.</b> The pre-warmer's live NodeType-record census
/// (<see cref="DynamicTypePreWarmer.ObserveLiveRecordCensus"/>) is one subscription held for the
/// process's life. A single <c>Connection reset by peer</c> while the PostgreSQL provider streamed
/// the catalog's rows terminated it, and nothing ever opened it again: <c>/health</c>'s bake-report
/// said "no longer being watched for" until the replica restarted. Measured on memex-cloud,
/// <c>memex-portal-deployment-65fcf777bc-pt5dc</c>, 2026-10-05 23:30:44Z.</para>
///
/// <para><b>Why the watch, and not a layer below it.</b> The query fan-in deliberately does NOT
/// retry a fault after a provider's first emission (<c>TransientStorageFaults.RetryTransientConnect</c>
/// — a resubscribe there would mint a second Initial into a merge that has already closed its
/// accounting), and the synced-query cache deliberately evicts the faulted chain and leaves the
/// decision to "the next caller" (<c>MeshNodeStreamCache.EvictFaultedQuery</c>). A one-shot read has
/// a next caller. A standing watch has none — it IS the caller, so the re-open is its job, and
/// nobody else's.</para>
///
/// <para><b>What makes it typed rather than a blanket retry.</b> Only <see cref="IsTransient"/>
/// faults re-open: a database connect/IO fault (<see cref="StorageFaults.IsTransientConnectFault"/>,
/// <see cref="InfrastructureFault.IsTransient"/>) or the fan-in's own availability terminal
/// (<see cref="QueryProviderStalledException"/>, "no answer in time", never a verdict). A query or
/// schema error, a deserialization defect, a disposed mesh — anything else — terminates the watch
/// on the first occurrence exactly as before, because re-reading it would only repeat the same
/// defect on a timer.</para>
///
/// <para><b>What makes it bounded.</b> At most <c>maxConsecutive</c> re-opens WITHOUT an
/// intervening emission; an emission proves the watch is reading again and resets the count. When
/// the budget is spent the LAST fault surfaces unchanged — the caller latches and reports it, as it
/// always did. So a database that is genuinely down ends the watch loudly after a few minutes, and a
/// one-off reset costs one re-open.</para>
///
/// <para>No timer polls anything, nothing sleeps on a thread, and nothing re-subscribes while the
/// watch is healthy: the only scheduled work is one backoff delay after a classified fault.</para>
/// </summary>
public static class StandingWatchRecovery
{
    /// <summary>Re-opens allowed in a row without an emission in between.</summary>
    public const int DefaultMaxConsecutive = 5;

    /// <summary>
    /// Delay before re-open number <paramref name="consecutive"/> (1-based): 2 s, 5 s, 15 s, 30 s,
    /// then 60 s. Long enough that a database restart or failover can finish, short enough that the
    /// whole budget is spent in about two minutes — after which a real outage is reported, not hidden.
    /// </summary>
    /// <param name="consecutive">How many re-opens have been attempted in a row, including this one.</param>
    public static TimeSpan DefaultBackoff(int consecutive) => consecutive switch
    {
        <= 1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(15),
        4 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(60),
    };

    /// <summary>
    /// True when <paramref name="fault"/> is a transient fault of the infrastructure a standing read
    /// depends on — see the type remarks for the exact classes, and for why nothing else qualifies.
    /// </summary>
    /// <param name="fault">The fault that terminated the watch; may be null.</param>
    public static bool IsTransient(Exception? fault)
    {
        if (fault is null)
            return false;
        if (StorageFaults.IsTransientConnectFault(fault) || InfrastructureFault.IsTransient(fault))
            return true;
        // A database read that dies mid-stream surfaces as the driver's DbException wrapping the
        // transport fault (IOException → SocketException 104), which both predicates above match.
        // The fan-in's stall terminal is the other availability fault a standing read meets — on a
        // pod in a GC or thread-pool stall every query misses its Initial bound together.
        for (var e = fault; e is not null; e = e.InnerException)
            if (e is QueryProviderStalledException)
                return true;
        return false;
    }

    /// <summary>
    /// Opens <paramref name="open"/>, and re-opens it after a <see cref="IsTransient"/> fault, at
    /// most <paramref name="maxConsecutive"/> times in a row without an emission in between. Any other
    /// fault, or the budget spent, surfaces unchanged.
    /// </summary>
    /// <typeparam name="T">The watch's emission.</typeparam>
    /// <param name="open">Cold factory for one subscription of the watch. Re-invoked per re-open, so
    /// it must build a FRESH upstream (a cached chain that latched the fault would replay it).</param>
    /// <param name="maxConsecutive">Re-opens allowed in a row; values below 0 mean none.</param>
    /// <param name="backoff">Delay before re-open number n (1-based).</param>
    /// <param name="scheduler">Scheduler for the backoff delay.</param>
    /// <param name="onReopen">Told each fault that is about to be re-opened from, with the
    /// re-open's number and delay — the caller records it, so the gap is never silent.</param>
    /// <param name="isTransient">The classification; <see cref="IsTransient"/> when null.</param>
    /// <returns>The watch's emissions across every re-open.</returns>
    public static IObservable<T> ReopenOnTransientFault<T>(
        Func<IObservable<T>> open,
        int maxConsecutive,
        Func<int, TimeSpan> backoff,
        IScheduler scheduler,
        Action<Exception, int, TimeSpan>? onReopen = null,
        Func<Exception, bool>? isTransient = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(scheduler);
        var budget = Math.Max(0, maxConsecutive);
        var classify = isTransient ?? IsTransient;

        IObservable<T> Subscription(int consecutiveSoFar) => Observable.Defer(() =>
        {
            // Per subscription, never shared: whether THIS opening has emitted decides whether the
            // fault that ends it continues a run of failures or starts a new one.
            var emitted = false;
            return Observable.Defer(open)
                .Do(_ => emitted = true)
                .Catch((Exception fault) =>
                {
                    var consecutive = (emitted ? 0 : consecutiveSoFar) + 1;
                    if (!classify(fault) || consecutive > budget)
                        return Observable.Throw<T>(fault);
                    var wait = backoff(consecutive);
                    onReopen?.Invoke(fault, consecutive, wait);
                    return Observable.Timer(wait, scheduler).SelectMany(_ => Subscription(consecutive));
                });
        });

        return Subscription(0);
    }
}
