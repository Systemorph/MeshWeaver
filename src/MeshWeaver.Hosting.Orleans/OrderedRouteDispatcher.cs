using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Runs route legs OFF the routing grain's turn (issue #1028) while keeping the mesh's
/// send order intact PER ORDERED CHANNEL: two deliveries handed in for the same channel are
/// subscribed strictly one after the other, in arrival order. Legs on different channels
/// never wait on each other.
///
/// <para><b>Why FIFO is a correctness requirement, not a nicety.</b> The data-sync
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
/// <para>🚨 <b>THE CHANNEL IS (destination, payload identity), NOT the destination — issue #5009.</b>
/// The guard above is per MIRROR, i.e. per synchronization stream. The destination ADDRESS is not
/// that: a stream-routed address is a MULTIPLEXER. The node-stream cache hub
/// <c>cache/{meshId}</c> fronts one <c>sync/{streamId}</c> sub-hub per observed node —
/// <c>DataExtensions.RouteStreamMessage</c> receives every frame at the cache hub's OWN address and
/// forwards it internally by <c>StreamId</c> — so keying the FIFO on the address alone serialised
/// the ENTIRE data-sync traffic of a whole process into ONE channel with ONE in-flight
/// <c>IPodHubGrain.Deliver</c> grain call. Where that destination is on another silo the channel's
/// service rate is one network round trip per frame (plus Orleans' JsonCodec copy of the full
/// payload, both ways), which is far below the arrival rate of a busy portal's fan-out: measured on
/// memex-cloud, 62 of 64 in-flight legs stacked behind one <c>cache/…</c> address for ~1.5 h while
/// the peer pod — whose own cache channel is local and therefore fast — reported a deepest queue of
/// 0 and read as "load". Every one of those 62 legs was waiting on a stream it has no ordering
/// relationship with.</para>
///
/// <para>The identity is <see cref="MeshWeaver.Messaging.IDiagnosticKeyed.DiagnosticKey"/> — the
/// stream id for every <c>StreamMessage</c> — read O(1) off the ENVELOPE by
/// <see cref="MeshWeaver.Messaging.DeliveryIdentity"/>, with no payload cast and no
/// deserialization on the turn. Same stream ⇒ same channel ⇒ arrival order preserved EXACTLY.
/// Different streams ⇒ different channels ⇒ their round trips overlap. A delivery with no identity
/// (there is nothing to say it is independent of anything) keeps the destination-wide channel,
/// which is byte-for-byte the previous behaviour.</para>
///
/// <para>🚨 <b>This raises no bound.</b> The number of legs the silo may have in flight is still the
/// routing <see cref="IIoPool"/>'s cap, unchanged; the receiving <c>PodHubGrain</c> is still
/// non-reentrant and still serialises its own hand-offs. What changes is only that legs with no
/// ordering relationship no longer wait on each other — the pipelining a correct channel key allows
/// and an over-coarse one forbade.</para>
///
/// <para>🚨 <b>Two deltas the narrowing really does carry, named rather than left to be found.</b>
/// (1) Traffic to one destination that carries NO identity — lifecycle and control messages, a
/// <c>DisposeRequest</c>, a heartbeat, a correlated response — stays on the destination-wide channel
/// and is therefore no longer ordered against that destination's DATA frames. Nothing relies on
/// that pairing: the whole data-sync protocol IS keyed (<c>DataChangedEvent</c>,
/// <c>SubscribeRequest</c>, <c>UnsubscribeRequest</c>, <c>StreamErrorEvent</c>,
/// <c>StreamEndedEvent</c> and every <c>IUserAction</c> carry the stream id), responses correlate by
/// request id, and a frame racing its destination's teardown was already a race the router never
/// decided — a user ACTION, the one case where a drop at teardown matters, is protected by its
/// <c>UserActionAccepted</c> receipt and the registration grace, never by router ordering.
/// (2) The number of dictionary entries is now bounded by in-flight LEGS rather than by in-flight
/// DESTINATIONS. An entry exists only while a channel holds a leg, and every held leg already holds
/// a route slot, so the bound is the same quantity the saturation report prints — tens of bytes per
/// entry against a delivery each.</para>
///
/// <para>Nothing runs on the turn and nothing is awaited: each leg is still subscribed through
/// the mesh's drainable routing <see cref="IIoPool"/>, so teardown can cancel + join it. The
/// queue is a plain lock-protected <see cref="ImmutableQueue{T}"/> — a synchronous data-structure
/// guard, never an async gate — and a channel's entry is removed the moment its queue drains,
/// so a silo that has served millions of short-lived <c>portal/{user}</c> addresses holds none of
/// them.</para>
///
/// <para><b>Head-of-line WITHIN a channel, deliberately.</b> One in-flight post per channel is the
/// point: a FIFO channel cannot overtake itself. It costs nothing on the healthy path, and channels
/// never wait on each other, so a stalled subscriber slows only its own stream. That backlog is
/// exactly what <c>RoutingGrain.ReportSaturation</c> already reports at <c>Critical</c> — and after
/// this change a non-zero <c>Deepest</c> means legs are queued behind a leg of the SAME stream,
/// which is a far stronger statement than it used to be.</para>
/// </summary>
internal sealed class OrderedRouteDispatcher(IIoPool pool, ILogger logger)
{
    private readonly record struct QueuedLeg(IObservable<Unit> Leg, Action OnCompleted);

    /// <summary>
    /// The FIFO key: a destination address plus the payload identity of the thing the deliveries on
    /// it are ABOUT (<c>null</c> for a payload that exposes none — see the type's remarks).
    /// </summary>
    private readonly record struct Channel(string Destination, string? OrderingKey);

    private sealed class DestinationQueue
    {
        public ImmutableQueue<QueuedLeg> Pending = ImmutableQueue<QueuedLeg>.Empty;
    }

    private readonly object gate = new();
    private readonly Dictionary<Channel, DestinationQueue> queues = new();

    /// <summary>
    /// Channels that currently hold a queue. Diagnostics / tests only — a healthy silo drains
    /// to zero, so a non-zero steady state means a leg is not completing.
    /// </summary>
    internal int ActiveChannels
    {
        get { lock (gate) return queues.Count; }
    }

    /// <summary>
    /// One lock acquisition, all three numbers — the triple that tells the shapes of a routing
    /// backlog apart, which the in-flight COUNT alone provably cannot (see
    /// <c>RoutingGrain.ReportSaturation</c>).
    ///
    /// <para><b>Why the count is ambiguous.</b> A leg's in-flight slot is claimed at ENQUEUE, not at
    /// subscribe. So N channels holding one executing leg each, and ONE wedged channel with
    /// N-1 legs stacked behind its head, produce the IDENTICAL in-flight count. Those are opposite
    /// situations: the first is breadth (nothing waits on anything), the second is head-of-line
    /// blocking on a channel that cannot overtake itself. <c>Deepest</c> is what separates them — it
    /// counts legs QUEUED BEHIND a channel's executing leg, so any value &gt;= 1 already means a
    /// leg is waiting on a leg rather than merely on the CPU; only 0 is pure breadth.</para>
    ///
    /// <para><b>Why BOTH counts.</b> <c>Channels</c> is the concurrency the dispatcher actually has;
    /// <c>Destinations</c> is how many distinct addresses those channels serve. The pair is the one
    /// that names the #5009 shape: many channels over ONE destination is a busy multiplexer hub
    /// draining in parallel, whereas one channel over one destination with a deep queue is a single
    /// stream whose frames are genuinely stacking up.</para>
    /// </summary>
    /// <returns>
    /// <c>Channels</c>: how many (destination, identity) channels currently hold a queue.
    /// <c>Destinations</c>: how many distinct addresses those channels belong to.
    /// <c>Deepest</c>: the largest number of legs queued behind the one executing leg of any single
    /// channel (0 when every channel is down to its in-flight leg).
    /// <c>DeepestChannel</c>: WHICH channel that is — <c>destination [stream]</c>, or <c>null</c> when
    /// no leg is queued behind another. A non-zero depth with no name told the reader that one stream
    /// was stacking up and never which one, so the producer behind a 63-deep burst could not be found
    /// from the line that measured it.
    /// </returns>
    internal (int Channels, int Destinations, int Deepest, string? DeepestChannel) QueueSnapshot()
    {
        lock (gate)
        {
            var deepest = 0;
            string? deepestChannel = null;
            foreach (var (channel, queue) in queues)
            {
                var depth = 0;
                foreach (var _ in queue.Pending) depth++;
                if (depth <= deepest) continue;
                deepest = depth;
                deepestChannel = channel.OrderingKey is null
                    ? channel.Destination
                    : $"{channel.Destination} [{channel.OrderingKey}]";
            }
            // LINQ's own dedupe rather than a set of ours: this runs once per saturation episode (the
            // report latches) or from a test, never on the dispatch path, so there is no reason for
            // the diagnostic to introduce a collection shape a future reader has to reason about.
            // Ordinal by default, which is what an address path needs.
            var destinations = queues.Keys.Select(channel => channel.Destination).Distinct().Count();
            return (queues.Count, destinations, deepest, deepestChannel);
        }
    }

    /// <summary>
    /// Enqueues one composed (cold) route leg on the channel
    /// (<paramref name="destination"/>, <paramref name="orderingKey"/>) and starts draining if that
    /// channel is idle. Returns immediately — the leg runs on the pool.
    /// </summary>
    /// <param name="destination">The target address path; half the FIFO key.</param>
    /// <param name="orderingKey">
    /// The payload identity the order must be preserved WITHIN — <c>null</c> when the payload
    /// exposes none, which puts the leg on the destination-wide channel. Stated explicitly rather
    /// than defaulted, because a call site that silently inherits the coarse channel is exactly the
    /// over-serialisation of issue #5009.
    /// </param>
    /// <param name="leg">The cold route observable; its side effects run when the drain subscribes it.</param>
    /// <param name="onLegCompleted">Invoked once per leg after it terminates (success or fault),
    /// on the completing thread — the in-flight bookkeeping the caller owns.</param>
    public void Enqueue(string destination, string? orderingKey, IObservable<Unit> leg, Action onLegCompleted)
    {
        var channel = new Channel(destination, orderingKey);
        var queued = new QueuedLeg(leg, onLegCompleted);
        lock (gate)
        {
            if (queues.TryGetValue(channel, out var existing))
            {
                // An entry exists ⇒ a drain for this channel is running (or is about to be
                // started by its creator, which still holds the leg it enqueued). Appending is
                // therefore enough: that drain picks this leg up when the one ahead completes.
                existing.Pending = existing.Pending.Enqueue(queued);
                return;
            }
            queues[channel] = new DestinationQueue { Pending = ImmutableQueue.Create(queued) };
        }
        DrainNext(channel);
    }

    /// <summary>
    /// Subscribes the next leg for <paramref name="channel"/>, or removes the channel's
    /// entry when its queue is empty. Re-entered from each leg's terminal notification, so exactly
    /// one leg per channel is ever in flight. No recursion depth to worry about: every leg is
    /// subscribed through the pool, which hops to a thread-pool thread, so a leg can never complete
    /// inside its own subscribe call.
    /// </summary>
    private void DrainNext(Channel channel)
    {
        QueuedLeg next;
        lock (gate)
        {
            if (!queues.TryGetValue(channel, out var queue) || queue.Pending.IsEmpty)
            {
                queues.Remove(channel);
                return;
            }
            queue.Pending = queue.Pending.Dequeue(out next);
        }

        pool.SubscribeThroughPool(next.Leg)
            .Finally(() =>
            {
                next.OnCompleted();
                DrainNext(channel);
            })
            .Subscribe(
                _ => { },
                ex => logger.LogError(ex,
                    "[ROUTE] Ordered route leg faulted for {Destination} (stream {OrderingKey})",
                    channel.Destination, channel.OrderingKey ?? "(none)"));
    }
}
