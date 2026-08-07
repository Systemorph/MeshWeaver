using System.Collections.Immutable;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The mesh's ONE fan-out primitive for change feeds: a hot broadcast that (a) never runs a
/// subscriber on the publisher's thread and (b) never lets one faulty subscriber starve the
/// others. Every place that pushes change events to arbitrary subscribers uses this instead
/// of a bare <see cref="Subject{T}"/>.
///
/// <para><b>Why (a) — the lock-order inversion (issue #899).</b> A publisher is always some
/// hub's action block or an I/O leaf, and it typically calls <see cref="OnNext"/> from deep
/// inside a <c>SelectMany</c> continuation that is itself executing INSIDE another Rx
/// operator's gate. The proven case: the whole <c>HandleDeleteNodeRequest</c> pipeline runs
/// inside the <c>Observable.CombineLatest</c> lock of <c>PermissionEvaluator</c>'s
/// effective-permission fold, because that fold emits synchronously during <c>Subscribe</c>
/// (its sources are cached <c>ReplaySubject</c>s). A synchronous fan-out from there walks the
/// subscriber graph and takes every gate in it — including SHARED gates that other hubs' folds
/// also sit behind (<c>PersistenceService.Changes</c>'s <c>Merge</c>, the per-query
/// <c>Concat</c>, the process-wide <c>Replay(1)</c> in <c>IMeshNodeStreamCache</c>) — while
/// still holding its own. Two hubs doing that concurrently acquire
/// {own fold gate, shared gate} in opposite orders and deadlock: both action blocks park
/// forever and the operation can neither succeed nor fail — no response is ever posted.
///
/// <para>Handing the fan-out to a dispatch loop breaks the cycle BY CONSTRUCTION: a publisher
/// only enqueues, so it can never acquire a foreign gate while holding its own, no matter what
/// subscribers do.</para></para>
///
/// <para><b>Why (b) — the swallowed fault (issue #889).</b> <c>Subject&lt;T&gt;.OnNext</c>
/// delivers to its observers in subscription order and the first observer that throws aborts
/// delivery to every observer after it; publishers then wrapped the call in
/// <c>catch { /* never throw */ }</c>, turning that into silence. A synced query being torn
/// down disposes its buffer before the subscription feeding it, so a write landing in that
/// window threw <see cref="ObjectDisposedException"/> into the fan-out and every OTHER live
/// subscriber — including the <c>$security-access</c> query the permission evaluator folds —
/// never saw the notification, leaving a permanently stale security cache. So: deliver to a
/// SNAPSHOT of the observers, isolate each one, and LOG rather than swallow.</para>
///
/// <para><b>Ordering</b> is preserved — one FIFO dispatch chain. <b>What callers may rely
/// on:</b> the event is enqueued only after the commit that produced it, and subscribers see
/// events in publish order. <b>What they may NOT rely on:</b> a subscriber having finished (or
/// even started) by the time <see cref="OnNext"/> returns. Nothing in the mesh does — this is
/// the same contract cross-silo delivery has always had.</para>
///
/// <para><b>Scheduler choice.</b> The default is <see cref="Scheduler.Default"/>: the dispatch
/// chain is <c>Select(v =&gt; Observable.Return(v, scheduler)).Concat()</c>, which serialises on
/// the thread pool WITHOUT holding a thread — measured at ~0 extra threads for 64 concurrent
/// feeds, where <c>ObserveOn(Scheduler.Default)</c> costs one dedicated (long-running) thread
/// EACH. That matters because there is one feed per storage adapter and Postgres creates one
/// adapter per partition schema. Pass an <see cref="EventLoopScheduler"/> explicitly where a
/// dedicated, named dispatch thread is wanted and there is exactly one feed (the mesh-wide
/// change feed does this, so change delivery is never starved by unrelated pool work).</para>
/// </summary>
/// <typeparam name="T">The event type broadcast by this feed.</typeparam>
public sealed class DispatchedChangeFeed<T> : IObservable<T>, IObserver<T>, IDisposable
{
    private readonly ILogger? logger;
    private readonly string name;
    private readonly object gate = new();

    /// <summary>
    /// Immutable so the fan-out iterates a stable snapshot with NO lock held — a subscriber
    /// that subscribes or disposes DURING a publish can never disturb the walk, and the feed
    /// itself is therefore never a gate that a foreign thread can be blocked on.
    /// </summary>
    private ImmutableList<IObserver<T>> observers = ImmutableList<IObserver<T>>.Empty;

    /// <summary>
    /// Publisher-facing inbox. Wrapped in <see cref="Subject.Synchronize{T}(ISubject{T})"/>
    /// because ANY hub action block or I/O leaf may publish concurrently (two per-node hubs
    /// deleting at the same time is the normal case in a recursive delete) and a bare
    /// <see cref="Subject{T}"/> is not safe under concurrent <c>OnNext</c>.
    /// </summary>
    private readonly Subject<T> inboxCore = new();

    private readonly ISubject<T> inbox;
    private readonly IDisposable pump;
    private bool disposed;

    /// <summary>
    /// Creates the feed and wires its serial dispatch chain.
    /// </summary>
    /// <param name="logger">Optional logger; a subscriber that throws is reported here.</param>
    /// <param name="name">
    /// Short identifier of the publishing component (adapter/schema name), used in log
    /// messages so a faulty subscriber can be attributed to the feed it starved.
    /// </param>
    /// <param name="dispatcher">
    /// Scheduler the fan-out runs on. Defaults to <see cref="Scheduler.Default"/> (thread
    /// pool, no dedicated thread). NEVER pass a scheduler that runs inline on the caller —
    /// that reinstates the inversion this type exists to prevent.
    /// </param>
    public DispatchedChangeFeed(ILogger? logger, string name, IScheduler? dispatcher = null)
    {
        this.logger = logger;
        this.name = name;
        var scheduler = dispatcher ?? Scheduler.Default;
        inbox = Subject.Synchronize(inboxCore);
        // 🚨 Select(Return(v, scheduler)).Concat() — NOT ObserveOn(scheduler). Both preserve
        // FIFO and both leave the publisher's thread, but Rx's ObserveOn prefers
        // ISchedulerLongRunning and therefore parks ONE DEDICATED THREAD per live
        // subscription; Concat over per-item Return(scheduler) uses a plain scheduled work
        // item, so N feeds cost O(1) threads instead of O(N). Concat subscribes the next item
        // only after the previous one completed, which IS the ordering guarantee.
        pump = inboxCore
            .Select(change => Observable.Return(change, scheduler))
            .Concat()
            .Subscribe(
                FanOut,
                ex => logger?.LogError(ex,
                    "Change-feed dispatch loop faulted ({Adapter}) — change events stopped.", name));
    }

    /// <summary>
    /// Enqueues an event for delivery to every subscriber and returns IMMEDIATELY (no-op once
    /// disposed). Subscribers run on the dispatch chain, never on the calling thread — see the
    /// type doc for why that is a correctness requirement, not a performance choice.
    /// </summary>
    public void OnNext(T value)
    {
        if (!disposed)
            inbox.OnNext(value);
    }

    /// <summary>
    /// Reports a terminal fault to every subscriber, isolated per subscriber. Delivered
    /// directly (not through the dispatch chain) so a fault is never queued behind a backlog.
    /// </summary>
    public void OnError(Exception error)
    {
        foreach (var observer in Volatile.Read(ref observers))
        {
            try { observer.OnError(error); }
            catch (Exception ex) { logger?.LogWarning(ex, "Change-feed observer threw on OnError ({Adapter}).", name); }
        }
    }

    /// <summary>Completes every subscriber, isolated per subscriber.</summary>
    public void OnCompleted()
    {
        foreach (var observer in Volatile.Read(ref observers))
        {
            try { observer.OnCompleted(); }
            catch (Exception ex) { logger?.LogWarning(ex, "Change-feed observer threw on OnCompleted ({Adapter}).", name); }
        }
    }

    /// <summary>
    /// Subscribes <paramref name="observer"/>. It is invoked on the dispatch chain, in publish
    /// order, and its exceptions cannot reach any other subscriber.
    /// </summary>
    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (gate)
        {
            if (disposed)
                return NullDisposable.Instance;
            observers = observers.Add(observer);
        }
        return new Subscription(this, observer);
    }

    /// <summary>Stops the dispatch chain and drops every subscriber.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            observers = ImmutableList<IObserver<T>>.Empty;
        }
        // Only the pump is disposed. Disposing `inboxCore` would make a publisher that raced
        // past the `disposed` check throw ObjectDisposedException back into its own write —
        // and "fix" that with a swallow. With the pump gone and the observer list empty, a
        // late OnNext is already a no-op, and the subject dies with this object.
        pump.Dispose();
    }

    private void FanOut(T value)
    {
        // Snapshot OUTSIDE the delivery loop: an observer is free to subscribe or dispose
        // while we are publishing, and neither may disturb this fan-out.
        var snapshot = Volatile.Read(ref observers);
        foreach (var observer in snapshot)
        {
            try
            {
                observer.OnNext(value);
            }
            catch (ObjectDisposedException ex)
            {
                // PROVABLY DEAD, so drop it: the subscriber's sink is disposed and every later
                // notification would throw again. This is the shape the disposal-order race
                // produces (a CompositeDisposable killing a change buffer while its feed is live).
                Remove(observer);
                logger?.LogWarning(ex,
                    "Change-feed observer {Observer} was disposed while still subscribed ({Adapter}); dropped from the feed. "
                    + "Delivery to the other {Remaining} observer(s) was NOT affected.",
                    observer.GetType().Name, name, snapshot.Count - 1);
            }
            catch (Exception ex)
            {
                // ISOLATED but KEPT. A transient throw must not permanently disable a live
                // subscriber — dropping it here would starve it of every future change, which is
                // the very failure this class exists to prevent, just moved. Only a disposed sink
                // (above) is provably unrecoverable. Warning, not Debug: a subscriber that missed
                // a change has a stale view of the mesh, and on the security-fold path that means
                // stale permissions.
                logger?.LogWarning(ex,
                    "Change-feed observer {Observer} threw ({Adapter}) and MISSED that notification; it remains subscribed. "
                    + "Delivery to the other {Remaining} observer(s) was NOT affected.",
                    observer.GetType().Name, name, snapshot.Count - 1);
            }
        }
    }

    private void Remove(IObserver<T> observer)
    {
        lock (gate)
        {
            observers = observers.Remove(observer);
        }
    }

    private sealed class Subscription(DispatchedChangeFeed<T> feed, IObserver<T> observer) : IDisposable
    {
        private IObserver<T>? current = observer;

        public void Dispose()
        {
            var o = Interlocked.Exchange(ref current, null);
            if (o is not null)
                feed.Remove(o);
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly IDisposable Instance = new NullDisposable();
        public void Dispose() { }
    }
}
