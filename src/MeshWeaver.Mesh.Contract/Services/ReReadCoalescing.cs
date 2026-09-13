using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// THE coalescer for the re-read that a durable change notification triggers when it arrives
/// without its entity. Every source whose notification can outrun row visibility delivers <c>Entity = null</c>
/// by contract — PostgreSQL's LISTEN/NOTIFY, the Cosmos change feed, the Snowflake poller — so a
/// consumer that needs the row re-reads it. There are two such consumers, the per-node hub's
/// own-node reconcile in <c>MeshDataSource</c> and the process-wide
/// <c>StorageChangeFeedRelay</c>, and they MUST coalesce through this one operator with this one
/// window: a notification storm on one path collapses to at most one read per quiet window, and
/// the reads on that path are serialised, so a bulk import cannot turn a notification storm into a
/// read storm (#223, #4139 — the relay once read ahead of the coalescer and 200 notifications cost
/// 201 reads).
///
/// <para>🚨 <see cref="Observable.Throttle{TSource}(IObservable{TSource}, TimeSpan, IScheduler)"/>,
/// deliberately NOT <c>Sample</c>: <c>Sample</c> arms a PERIODIC timer per subscription and there
/// is one of these subscriptions per live node hub (and one per path a burst touches in the relay),
/// so an idle mesh would pay one timer tick per subscription per window for nothing. <c>Throttle</c>
/// arms a one-shot timer only while a burst is in flight — an idle subscription costs nothing — and
/// it always emits the LAST trigger of a burst, so the read that CONVERGES a mirror can never be the
/// one that gets dropped.</para>
///
/// <para><c>Concat</c>, deliberately NOT <c>Switch</c> or <c>Merge</c>: one read at a time per
/// subscription, so a second burst can never interleave its adoption with the first's, and the read
/// that runs after the last trigger observes the durable state as of AFTER that trigger.</para>
/// </summary>
public static class ReReadCoalescing
{
    /// <summary>
    /// The quiet window a burst of triggers on one path must fall silent for before the re-read
    /// runs. One constant for every caller — a second coalescer with a second window is how the
    /// read-storm guard (<c>CrossProcessChangeFeedTest.AnEntitylessBurst…</c>, which measures this
    /// window by name) comes back with a different number.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Coalesces <paramref name="triggers"/> into at most one <paramref name="reRead"/> per
    /// <see cref="Window"/> of quiet, always running one for the LAST trigger of a burst, with the
    /// reads serialised. <paramref name="reRead"/> must own its failure handling (a
    /// <c>Catch</c> that logs and returns empty): a faulted inner observable would otherwise
    /// terminate the serialised queue and the subscription would stop reconciling for good.
    /// </summary>
    /// <param name="triggers">One emission per entity-less notification for ONE path.</param>
    /// <param name="reRead">The authoritative storage read for the last trigger of a burst.</param>
    /// <param name="scheduler">The timer scheduler — <see cref="Scheduler.Default"/> unless a test
    /// drives virtual time.</param>
    public static IObservable<TResult> CoalesceReReads<TTrigger, TResult>(
        this IObservable<TTrigger> triggers,
        Func<TTrigger, IObservable<TResult>> reRead,
        IScheduler? scheduler = null)
        => triggers
            .Throttle(Window, scheduler ?? Scheduler.Default)
            .Select(reRead)
            .Concat();
}
