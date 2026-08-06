using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// The storage adapter's in-process change feed: a hot broadcast where ONE faulty subscriber
/// cannot starve the others.
///
/// <para>This replaces a plain <see cref="System.Reactive.Subjects.Subject{T}"/>, which is
/// fan-out-hostile in exactly the way that matters here. <c>Subject&lt;T&gt;.OnNext</c> delivers to
/// its observers SYNCHRONOUSLY, IN SUBSCRIPTION ORDER, on the caller's thread — so the first
/// observer that throws aborts delivery to every observer after it. The adapter then wrapped the
/// publish in <c>catch { /* best-effort */ }</c>, which turned that into silence.</para>
///
/// <para>The concrete failure (CI 31083356138 and again on main at 0744169c): a synced query being
/// torn down disposes its buffer before the subscription feeding it, so a write landing in that
/// window throws <see cref="ObjectDisposedException"/> into the fan-out. Every OTHER live
/// subscriber — including the <c>$security-access</c> query the permission evaluator folds — never
/// saw that notification. Its <c>Replay(1)</c> cache stayed frozen at the pre-write snapshot, the
/// access fold never completed, and reads were evaluated against stale permissions until something
/// else happened to re-trigger a query. In tests that surfaced as PaywallRealGateShapeTests timing
/// out on its fold barrier; in a live portal it is a silently stale security cache.</para>
///
/// <para>So: deliver to a SNAPSHOT of the observers, isolate each one, and never let a throw from
/// one reach another. A DISPOSED observer is dropped (provably dead — every later notification
/// would throw again); any OTHER throw is isolated but the observer stays subscribed, because
/// permanently disabling a live subscriber over a transient fault would just relocate this very
/// bug. Either way it is LOGGED: a swallowed fault on the security-fold path is precisely what
/// made this take three CI runs to see.</para>
/// </summary>
internal sealed class IsolatedChangeFeed : IObservable<DataChangeNotification>, IObserver<DataChangeNotification>, IDisposable
{
    private readonly ILogger? _logger;
    private readonly string _adapter;
    private readonly object _gate = new();
    // Immutable so OnNext iterates a stable snapshot with no lock held — a subscriber that
    // subscribes or disposes DURING a publish can never mutate the list being walked.
    private ImmutableList<IObserver<DataChangeNotification>> _observers =
        ImmutableList<IObserver<DataChangeNotification>>.Empty;
    private bool _disposed;

    public IsolatedChangeFeed(ILogger? logger, string adapter)
    {
        _logger = logger;
        _adapter = adapter;
    }

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

    public void OnError(Exception error)
    {
        foreach (var observer in Volatile.Read(ref _observers))
        {
            try { observer.OnError(error); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Change-feed observer threw on OnError ({adapter}).", _adapter); }
        }
    }

    public void OnCompleted()
    {
        foreach (var observer in Volatile.Read(ref _observers))
        {
            try { observer.OnCompleted(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Change-feed observer threw on OnCompleted ({adapter}).", _adapter); }
        }
    }

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

    private sealed class Disposable : IDisposable
    {
        public static readonly IDisposable Empty = new Disposable();
        public void Dispose() { }
    }
}
