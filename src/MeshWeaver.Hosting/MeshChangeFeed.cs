using System.Reactive;
using System.Reactive.Concurrency;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// In-process implementation of <see cref="IMeshChangeFeed"/> — the mesh-wide
/// create/update/delete event bus. In Orleans, <c>OrleansMeshChangeFeed</c> wraps
/// this and adds the cross-silo broadcast.
///
/// <para>🚨 <b>Fan-out runs on ONE dedicated dispatch loop, never on the publisher's
/// thread</b> (issue #899). <see cref="Publish"/> only enqueues; the subscriber
/// graph is walked by the single <c>mesh-change-feed</c> thread. This is a
/// correctness requirement, not a performance choice:</para>
///
/// <para>A publisher is always some hub's action block, and it typically calls
/// <c>Publish</c> from deep inside a <c>SelectMany</c> continuation that is itself
/// executing INSIDE another operator's gate — e.g. the whole
/// <c>HandleDeleteNodeRequest</c> pipeline runs inside the
/// <c>Observable.CombineLatest</c> lock of <c>PermissionEvaluator</c>'s effective-
/// permission fold (the fold emits synchronously during <c>Subscribe</c> because its
/// sources are cached <c>ReplaySubject</c>s). A synchronous fan-out from there takes
/// the gates of every subscriber's chain — including the SHARED synced-query
/// <c>Merge</c> gates that other hubs' folds also sit behind — while still holding
/// its own. Two hubs publishing concurrently therefore acquired
/// {permission-fold gate, synced-query merge gate} in opposite orders and
/// deadlocked: both action blocks parked forever, the recursive delete could
/// neither succeed nor fail, and no <c>DeleteNodeResponse</c> was ever posted
/// (issue #899 — a recursive space delete wedging ~2 in 12 runs).</para>
///
/// <para>Handing the fan-out to its own serial loop breaks the cycle by
/// construction: a publisher never acquires a foreign gate, so no lock-order
/// inversion is possible no matter what subscribers do. Ordering is preserved —
/// the loop is single-threaded and FIFO — and this mirrors what
/// <c>OrleansMeshChangeFeed</c> already does for the cross-silo half (a
/// <c>Subject</c> + serial queue), so local and cross-silo delivery now have the
/// same "enqueue, never run on the caller's turn" semantics.</para>
///
/// <para>What callers may still rely on: the event is enqueued only AFTER the
/// storage commit that produced it (see <c>StorageAdapterChangeFeedExtensions</c>),
/// and events are delivered to subscribers in publish order. What they may NOT
/// rely on: a subscriber having finished processing by the time <c>Publish</c>
/// returns. Nothing in the mesh does — the delete path invalidates
/// <c>IMeshNodeStreamCache</c> and disposes the per-node hub explicitly, and
/// cross-silo delivery was already asynchronous.</para>
/// </summary>
public class InProcessMeshChangeFeed : IMeshChangeFeed, IDisposable
{
    /// <summary>
    /// The bus itself: enqueue-and-dispatch fan-out with per-subscriber isolation. Safe under
    /// concurrent publish from ANY hub action block (two per-node hubs deleting at the same
    /// time is the normal case in a recursive delete).
    /// </summary>
    private readonly DispatchedChangeFeed<MeshChangeEvent> _feed;

    /// <summary>
    /// The single dispatch thread. A dedicated <see cref="EventLoopScheduler"/> rather than
    /// the thread pool: change-feed delivery drives cache invalidation and synced-query
    /// folds, and must not queue behind (or be starved by) unrelated pool work. There is
    /// exactly ONE of these per mesh, so the dedicated thread is affordable here — storage
    /// adapters (one feed per partition schema) use the pool-backed default instead.
    /// </summary>
    private readonly EventLoopScheduler _dispatcher =
        new(start => new Thread(start) { IsBackground = true, Name = "mesh-change-feed" });

    private bool _disposed;

    /// <summary>
    /// Creates the feed and starts its serial dispatch loop.
    /// </summary>
    /// <param name="logger">Optional logger; a subscriber that throws is reported here.</param>
    public InProcessMeshChangeFeed(ILogger<InProcessMeshChangeFeed>? logger = null)
    {
        // 🚨 The dispatch + isolation semantics live in DispatchedChangeFeed — the ONE
        // fan-out primitive shared with every storage adapter, so "publishers only enqueue"
        // is a property of the mesh, not a property of this one class.
        _feed = new DispatchedChangeFeed<MeshChangeEvent>(logger, "mesh-change-feed", _dispatcher);
    }

    /// <summary>
    /// Enqueues a mesh change event for delivery to all local subscribers (no-op once
    /// disposed). Returns immediately: subscribers run on the feed's dispatch loop, never
    /// on the calling hub's thread — see the type doc for why that is mandatory.
    /// </summary>
    /// <param name="change">The change event to publish.</param>
    public void Publish(MeshChangeEvent change)
    {
        if (!_disposed)
            _feed.OnNext(change);
    }

    /// <summary>
    /// Publishes locally without re-broadcasting to Orleans streams.
    /// Used by PathCacheInvalidatorGrain to relay cross-silo events
    /// to local subscribers without creating an infinite loop.
    /// </summary>
    public void PublishLocal(MeshChangeEvent change)
    {
        if (!_disposed)
            _feed.OnNext(change);
    }

    /// <summary>
    /// Subscribes a handler to mesh change events, optionally filtered by change kind.
    /// The handler is invoked on the feed's dispatch loop, in publish order.
    /// </summary>
    /// <param name="handler">The callback invoked for each matching change event.</param>
    /// <param name="filter">When set, only events of this kind are delivered; otherwise all events are delivered.</param>
    /// <returns>A disposable that ends the subscription when disposed.</returns>
    public IDisposable Subscribe(Action<MeshChangeEvent> handler, MeshChangeKind? filter = null)
    {
        if (filter == null)
            return _feed.Subscribe(Observer.Create(handler));

        var kind = filter.Value;
        return _feed.Subscribe(Observer.Create<MeshChangeEvent>(e =>
        {
            if (e.Kind == kind)
                handler(e);
        }));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Stop the feed BEFORE releasing the dispatch thread so the loop cannot schedule
        // onto a disposed scheduler.
        _feed.Dispose();
        _dispatcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
