using System.Collections.Immutable;
using System.Reactive.Disposables;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// The change feed every <see cref="IStorageAdapter"/> publishes <see cref="IStorageAdapter.Changes"/>
/// through: a hot broadcast where ONE faulty subscriber cannot starve the others.
///
/// <para>🚨 <b>A storage adapter must NEVER publish through a plain
/// <see cref="System.Reactive.Subjects.Subject{T}"/>.</b> <c>Subject&lt;T&gt;.OnNext</c> delivers to
/// its observers SYNCHRONOUSLY, IN SUBSCRIPTION ORDER, on the caller's thread — so the first
/// observer that throws aborts delivery to every observer after it. Adapters then wrapped the
/// publish in <c>catch { /* best-effort */ }</c>, which turned that into silence.</para>
///
/// <para>The concrete failure on Postgres (CI 31083356138 and again on main at 0744169c): a synced
/// query being torn down disposes its buffer before the subscription feeding it, so a write landing
/// in that window throws <see cref="ObjectDisposedException"/> into the fan-out. Every OTHER live
/// subscriber — including the <c>$security-access</c> query the permission evaluator folds — never
/// saw that notification. Its <c>Replay(1)</c> cache stayed frozen at the pre-write snapshot, the
/// access fold never completed, and reads were evaluated against stale permissions until something
/// else happened to re-trigger a query. In tests that surfaced as PaywallRealGateShapeTests timing
/// out on its fold barrier; in a live portal it is a silently stale security cache.</para>
///
/// <para>The SAME defect was still live on the in-memory adapter — the feed behind every monolith
/// test mesh and every in-memory partition — and surfaced there as issue #1053: a LIVE children
/// query silently stopped re-emitting after a create that completed successfully. The subscriber
/// that throws is not hypothetical on any backend: every live synced query owns a
/// <c>persistence.Changes → changeBuffer</c> pipeline, and one-shot queries
/// (<c>IMeshService.QueryAsync</c>, autocomplete, path resolution) open and tear one down
/// constantly, so the disposal window is hit whenever a write lands during a teardown.</para>
///
/// <para>So: deliver to a SNAPSHOT of the observers, isolate each one, and never let a throw from
/// one reach another. A DISPOSED observer is dropped (provably dead — every later notification
/// would throw again); any OTHER throw is isolated but the observer stays subscribed, because
/// permanently disabling a live subscriber over a transient fault would just relocate this very
/// bug. Either way it is LOGGED: a swallowed fault on the security-fold path is precisely what
/// made this take three CI runs to see.</para>
/// </summary>
public sealed class IsolatedChangeFeed : IObservable<DataChangeNotification>, IObserver<DataChangeNotification>, IDisposable
{
    private readonly ILogger? _logger;
    private readonly string _adapter;
    private readonly object _gate = new();
    // Immutable so OnNext iterates a stable snapshot with no lock held — a subscriber that
    // subscribes or disposes DURING a publish can never mutate the list being walked.
    private ImmutableList<IObserver<DataChangeNotification>> _observers =
        ImmutableList<IObserver<DataChangeNotification>>.Empty;
    private bool _disposed;

    /// <summary>
    /// Creates a feed for one adapter.
    /// </summary>
    /// <param name="logger">
    /// Logger for isolated-fault warnings. Pass a real logger — a null one restores exactly the
    /// silence this class exists to end (see <c>PostgreSqlPartitionStorageProvider</c>, where the
    /// per-schema feeds were once constructed with a null logger).
    /// </param>
    /// <param name="adapter">
    /// Name of the owning adapter (a Postgres schema, <c>"path-router"</c>, <c>"in-memory"</c>, …),
    /// used to attribute a warning to the feed it came from.
    /// </param>
    public IsolatedChangeFeed(ILogger? logger, string adapter)
    {
        _logger = logger;
        _adapter = adapter;
    }

    /// <summary>
    /// Attaches <paramref name="observer"/> to the feed. Subscribing (or disposing) during a
    /// publish is safe — <see cref="OnNext"/> walks a snapshot taken before delivery starts.
    /// </summary>
    /// <param name="observer">The observer to receive every subsequent notification.</param>
    /// <returns>A subscription handle; disposing it detaches the observer.</returns>
    public IDisposable Subscribe(IObserver<DataChangeNotification> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            if (_disposed)
                return Disposable.Empty;
            _observers = _observers.Add(observer);
        }
        return new Subscription(this, observer);
    }

    /// <summary>
    /// Publishes <paramref name="value"/> to every attached observer, isolating each one: a throw
    /// from any observer never reaches another, and never reaches the publishing write.
    /// </summary>
    /// <param name="value">The change notification to broadcast.</param>
    public void OnNext(DataChangeNotification value)
    {
        // Snapshot OUTSIDE the delivery loop: an observer is free to subscribe or dispose while we
        // are publishing, and neither may disturb this fan-out.
        var observers = Volatile.Read(ref _observers);
        foreach (var observer in observers)
        {
            try
            {
                observer.OnNext(value);
            }
            catch (ObjectDisposedException ex)
            {
                // PROVABLY DEAD, so drop it: the subscriber's sink is disposed and every later
                // notification would throw again. This is the shape the disposal-order race
                // produced (a CompositeDisposable killing changeBuffer while its feed was live).
                Remove(observer);
                _logger?.LogWarning(ex,
                    "Change-feed observer {Observer} was disposed while still subscribed ({Adapter}); dropped from the feed "
                    + "at {Path}. Delivery to the other {Remaining} observer(s) was NOT affected.",
                    observer.GetType().Name, _adapter, value.Path, observers.Count - 1);
            }
            catch (Exception ex)
            {
                // ISOLATED but KEPT. A transient throw must not permanently disable a live
                // subscriber — dropping it here would starve it of every future change, which is
                // the very failure this class exists to prevent, just moved. Only a disposed sink
                // (above) is provably unrecoverable. Warning, not Debug: a subscriber that missed
                // a change has a stale view of the mesh, and on the security-fold path that means
                // stale permissions.
                _logger?.LogWarning(ex,
                    "Change-feed observer {Observer} threw on {Path} ({Adapter}) and MISSED that notification; it remains "
                    + "subscribed. Delivery to the other {Remaining} observer(s) was NOT affected.",
                    observer.GetType().Name, value.Path, _adapter, observers.Count - 1);
            }
        }
    }

    /// <summary>Forwards a terminal fault to every observer, isolating each one.</summary>
    /// <param name="error">The fault to forward.</param>
    public void OnError(Exception error)
    {
        foreach (var observer in Volatile.Read(ref _observers))
        {
            try { observer.OnError(error); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Change-feed observer threw on OnError ({adapter}).", _adapter); }
        }
    }

    /// <summary>Forwards completion to every observer, isolating each one.</summary>
    public void OnCompleted()
    {
        foreach (var observer in Volatile.Read(ref _observers))
        {
            try { observer.OnCompleted(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Change-feed observer threw on OnCompleted ({adapter}).", _adapter); }
        }
    }

    /// <summary>
    /// Detaches every observer and refuses further subscriptions. Called when the owning adapter
    /// is disposed.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _observers = ImmutableList<IObserver<DataChangeNotification>>.Empty;
        }
    }

    private void Remove(IObserver<DataChangeNotification> observer)
    {
        lock (_gate)
        {
            _observers = _observers.Remove(observer);
        }
    }

    private sealed class Subscription(IsolatedChangeFeed feed, IObserver<DataChangeNotification> observer) : IDisposable
    {
        private IObserver<DataChangeNotification>? _observer = observer;

        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _observer, null);
            if (o is not null)
                feed.Remove(o);
        }
    }
}
