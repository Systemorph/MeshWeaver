using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Runs route legs OFF the routing grain's turn (issue #1028) while keeping the mesh's
/// send order intact PER DESTINATION: two deliveries handed in for the same address are
/// subscribed strictly one after the other, in arrival order. Legs for different addresses
/// never wait on each other.
///
/// <para><b>Why FIFO per destination is a correctness requirement, not a nicety.</b> The data-sync
/// protocol is a DELTA protocol with a receive-side monotonicity guard: a frame whose owner
/// version is below the mirror's current version is discarded as stale
/// (<c>SynchronizationStream.UpdateStream</c>), and nothing ever re-sends it. That guard is only
/// sound while frames arrive in the order the owner produced them. Handing each leg to the I/O
/// pool independently — <c>pool.SubscribeThroughPool(leg)</c> per delivery, which hops every
/// subscribe onto its own thread-pool thread — discards that order, so two frames of ONE stream
/// can swap: the later one lands first, and the earlier one (carrying the layout area's actual
/// content) is then dropped as "stale" FOREVER. The subscriber stays on the "Building layout…"
/// base frame and its wait dies on its own timeout, with nothing logged above Debug on either
/// side. That is the same visible failure the wrong-clock bug produced (see
/// <c>SynchronizationStream.OwnerVersion</c>'s remarks) — reached here through arrival order
/// instead of through a wrong version stamp.</para>
///
/// <para>The routing grain's turn is the LAST point at which the order is still authoritative
/// (<c>[StatelessWorker(1)]</c>, non-reentrant — one turn per silo), which is why the FIFO is
/// claimed there and drained here.</para>
///
/// <para>Nothing runs on the turn and nothing is awaited: each leg is still subscribed through
/// the mesh's drainable routing <see cref="IIoPool"/>, so teardown can cancel + join it. The
/// queue is a plain lock-protected <see cref="ImmutableQueue{T}"/> — a synchronous data-structure
/// guard, never an async gate — and a destination's entry is removed the moment its queue drains,
/// so a silo that has served millions of short-lived <c>portal/{user}</c> addresses holds none of
/// them.</para>
///
/// <para><b>Head-of-line, deliberately.</b> One in-flight post PER DESTINATION is the point: a FIFO
/// channel cannot overtake itself. It costs nothing on the healthy path (a post's only await is
/// <c>IMemoryStreamQueueGrain.Enqueue</c> — sub-millisecond, and already bounded by Orleans' own
/// 30 s response timeout under <c>RoutingGrain.StreamPostTimeout</c>), and destinations never wait
/// on each other, so a stalled subscriber slows only its own channel. That backlog is exactly what
/// <c>RoutingGrain.ReportSaturation</c> already reports at <c>Critical</c>.</para>
/// </summary>
internal sealed class OrderedRouteDispatcher(IIoPool pool, ILogger logger)
{
    private readonly record struct QueuedLeg(IObservable<Unit> Leg, Action OnCompleted);

    private sealed class DestinationQueue
    {
        public ImmutableQueue<QueuedLeg> Pending = ImmutableQueue<QueuedLeg>.Empty;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, DestinationQueue> queues = new(StringComparer.Ordinal);

    /// <summary>
    /// Destinations that currently hold a queue. Diagnostics / tests only — a healthy silo drains
    /// to zero, so a non-zero steady state means a leg is not completing.
    /// </summary>
    internal int ActiveDestinations
    {
        get { lock (gate) return queues.Count; }
    }

    /// <summary>
    /// One lock acquisition, both numbers — the pair that tells the two shapes of a routing backlog
    /// apart, which the in-flight COUNT alone provably cannot (see
    /// <c>RoutingGrain.ReportSaturation</c>).
    ///
    /// <para><b>Why the count is ambiguous.</b> A leg's in-flight slot is claimed at ENQUEUE, not at
    /// subscribe. So N destinations holding one executing leg each, and ONE wedged destination with
    /// N-1 legs stacked behind its head, produce the IDENTICAL in-flight count. Those are opposite
    /// situations: the first is breadth (nothing waits on anything), the second is head-of-line
    /// blocking on a channel that cannot overtake itself. <c>Deepest</c> is what separates them —
    /// above 1 it means legs are waiting on a leg, not merely on the CPU.</para>
    /// </summary>
    /// <returns>
    /// <c>Destinations</c>: how many addresses currently hold a queue.
    /// <c>Deepest</c>: the largest number of legs queued behind the one executing leg of any single
    /// destination (0 when every destination is down to its in-flight leg).
    /// </returns>
    internal (int Destinations, int Deepest) QueueSnapshot()
    {
        lock (gate)
        {
            var deepest = 0;
            foreach (var queue in queues.Values)
            {
                var depth = 0;
                foreach (var _ in queue.Pending) depth++;
                if (depth > deepest) deepest = depth;
            }
            return (queues.Count, deepest);
        }
    }

    /// <summary>
    /// Enqueues one composed (cold) route leg for <paramref name="destination"/> and starts
    /// draining if this destination is idle. Returns immediately — the leg runs on the pool.
    /// </summary>
    /// <param name="destination">The target address path; the FIFO key.</param>
    /// <param name="leg">The cold route observable; its side effects run when the drain subscribes it.</param>
    /// <param name="onLegCompleted">Invoked once per leg after it terminates (success or fault),
    /// on the completing thread — the in-flight bookkeeping the caller owns.</param>
    public void Enqueue(string destination, IObservable<Unit> leg, Action onLegCompleted)
    {
        var queued = new QueuedLeg(leg, onLegCompleted);
        lock (gate)
        {
            if (queues.TryGetValue(destination, out var existing))
            {
                // An entry exists ⇒ a drain for this destination is running (or is about to be
                // started by its creator, which still holds the leg it enqueued). Appending is
                // therefore enough: that drain picks this leg up when the one ahead completes.
                existing.Pending = existing.Pending.Enqueue(queued);
                return;
            }
            queues[destination] = new DestinationQueue { Pending = ImmutableQueue.Create(queued) };
        }
        DrainNext(destination);
    }

    /// <summary>
    /// Subscribes the next leg for <paramref name="destination"/>, or removes the destination's
    /// entry when its queue is empty. Re-entered from each leg's terminal notification, so exactly
    /// one leg per destination is ever in flight. No recursion depth to worry about: every leg is
    /// subscribed through the pool, which hops to a thread-pool thread, so a leg can never complete
    /// inside its own subscribe call.
    /// </summary>
    private void DrainNext(string destination)
    {
        QueuedLeg next;
        lock (gate)
        {
            if (!queues.TryGetValue(destination, out var queue) || queue.Pending.IsEmpty)
            {
                queues.Remove(destination);
                return;
            }
            queue.Pending = queue.Pending.Dequeue(out next);
        }

        pool.SubscribeThroughPool(next.Leg)
            .Finally(() =>
            {
                next.OnCompleted();
                DrainNext(destination);
            })
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "[ROUTE] Ordered route leg faulted for {Destination}", destination));
    }
}
