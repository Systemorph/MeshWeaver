using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Disposables;
using MeshWeaver.Mesh.Threading;

namespace MeshWeaver.Hosting.Persistence.Query;

/// <summary>
/// Delivers a merge's frames to its observer one at a time, in the order the merge produced them,
/// and NEVER while the merge's own gate is held.
///
/// <para>🚨 <b>Why it exists — a lock held across foreign code is half a deadlock.</b>
/// <see cref="MeshQuery"/>'s provider merge mutates its state under a gate, and it used to call
/// <c>observer.OnNext</c> inside that gate too. The observer is not inert: a create's
/// <c>CreatableTypesCreationValidator</c> answers off a query frame and continues the create
/// SYNCHRONOUSLY into the storage write, whose change notification is delivered through the
/// storage feed's own serializing <c>Observable.Merge</c> gate (<c>PersistenceService.Changes</c>
/// in production, <c>FaultInjectingStorageAdapter.Changes</c> in the fault-injection rig). A second
/// thread already delivering a change through that feed holds the feed's gate and needs the
/// merge's gate for a live query on the same store. Each thread then holds the lock the other one
/// waits for: the write never answers, and every later write through the feed parks behind it
/// (measured: a case-only rename's staging creates stalled at stage <c>write</c> on CI, MeshWeaver
/// #7117; reproduced locally with the two thread stacks in that exact shape).</para>
///
/// <para>So the merge keeps its gate for its STATE only: under the gate it enqueues here, and after
/// leaving the gate it calls <see cref="Drain"/>. One thread at a time drains; a thread that finds a
/// drain already running leaves its frame for that drain and returns at once — so a thread arriving
/// while the observer is busy is never blocked by it. Order is the gate's order, because every
/// enqueue happens under the gate. A frame produced re-entrantly by the observer itself is queued and
/// delivered after the one being handled, instead of nested inside it (Rx forbids the nesting).</para>
///
/// <para>🚨 <b>A drainer delivers only what was queued when it took the drain — never the stream's
/// future</b> (MeshWeaver#7277). "Whoever drains delivers everything" made the drain a thread
/// HIJACK: the thread that happened to deliver a frame went on delivering every frame any other
/// thread queued while it was busy, for as long as they kept coming. The thread that most often takes
/// the first drain is the Query pool's SETUP LEAF (<c>IoPool.SubscribeThroughPool</c>): a provider that
/// answers its Initial synchronously completes the merged Initial inside <c>source.Subscribe</c>, so the
/// setup leaf delivers it — by design — and then, under the old rule, went on delivering the LIVE
/// change feed while still counted as an in-flight leaf holding a Query-pool permit. A consumer that
/// writes in response to a frame feeds its own change straight back into that queue, so the leaf
/// could hold its permit indefinitely, and a silo stop's 30 s join expired over it:
/// <c>Did NOT report: Query=1 [Using`2[… MeshQuery+QueryMembers]]</c>. Now a drain delivers at most
/// the frames queued up to the moment it took the drain (its own frame included) and hands the rest
/// to a separate batch on the mesh's Query <see cref="IIoPool"/>, which delivers the next batch the same
/// way. Order is unchanged — the draining flag stays taken across the hand-off — and no producer's
/// thread outlives its own frame.</para>
///
/// <para>🚨 <b>The handed-off batch is TRACKED pool work, never a bare scheduler hop</b> (review of
/// #7844). A batch runs the consumer, and the consumer may use the service scope. On a bare
/// <c>TaskPoolScheduler</c> it ran outside every teardown count: a silo stop could complete the pooled
/// subscription and let <c>IoPool.Disposed</c> fire while a handed-off consumer was still running —
/// the use-after-unload shape (#613) the pool exists to prevent. So each batch is one
/// <see cref="IIoPool.InvokeBlocking{T}"/> leaf: counted in flight, waited for by the pool's teardown,
/// and named in its residual if it outlives the join. A batch that has not started when the
/// subscription ends is cancelled with it (<see cref="Dispose"/>), and one the pool's drain refuses is
/// never started.</para>
///
/// <para>The one lock here guards the queue and the draining flag only and is never held while the
/// observer runs. Instance state of one subscription; nothing static.</para>
/// </summary>
/// <typeparam name="TItem">The frame type.</typeparam>
/// <param name="observer">The merge's consumer.</param>
/// <param name="handOffPool">The mesh-owned pool a batch queued by OTHER threads during a drain is
/// delivered on, as a tracked blocking leaf.</param>
/// <param name="reportHandOffFault">Where a fault of a HANDED-OFF batch is reported — an exception the
/// observer threw, which has no caller to fault on a pool lane.</param>
internal sealed class GateFreeEmitter<TItem>(
    IObserver<TItem> observer,
    IIoPool handOffPool,
    Action<Exception> reportHandOffFault) : IDisposable
{
    private readonly object queueLock = new();
    private ImmutableQueue<Notification<TItem>> pending = ImmutableQueue<Notification<TItem>>.Empty;
    // Sequence numbers of the last frame queued and the last frame delivered; a drain's batch ends at
    // the `enqueued` it read when it took the drain.
    private long enqueued;
    private long delivered;
    private bool draining;
    private bool disposed;
    // The handed-off batch in flight or queued on the pool — at most one, because `draining` is held
    // across the hand-off. Disposing it cancels a batch that has not started.
    private readonly SerialDisposable handedOff = new();

    /// <summary>Queues a frame. Call under the merge's gate.</summary>
    public void OnNext(TItem value) => Enqueue(Notification.CreateOnNext(value));

    /// <summary>Queues the error terminal. Call under the merge's gate.</summary>
    public void OnError(Exception error) => Enqueue(Notification.CreateOnError<TItem>(error));

    /// <summary>Queues the completion terminal. Call under the merge's gate.</summary>
    public void OnCompleted() => Enqueue(Notification.CreateOnCompleted<TItem>());

    private void Enqueue(Notification<TItem> notification)
    {
        lock (queueLock)
        {
            pending = pending.Enqueue(notification);
            enqueued++;
        }
    }

    /// <summary>
    /// Delivers the frames queued so far, unless another thread is already doing so — then that
    /// drain delivers this caller's frames too, and this call returns at once. Frames other threads
    /// queue while this drain runs are handed off to the pool, never delivered on this thread. Call
    /// with the merge's gate RELEASED.
    /// </summary>
    public void Drain()
    {
        long limit;
        lock (queueLock)
        {
            if (draining || disposed)
                return;
            draining = true;
            limit = enqueued;
        }
        if (DeliverBatch(limit))
            HandOff();
    }

    /// <summary>
    /// Ends the subscription's delivery: a handed-off batch that has not started is cancelled, and
    /// nothing queued afterwards is delivered. A batch already running finishes its frames — the pool
    /// tracks it until it does.
    /// </summary>
    public void Dispose()
    {
        lock (queueLock)
        {
            disposed = true;
            pending = ImmutableQueue<Notification<TItem>>.Empty;
        }
        handedOff.Dispose();
    }

    // Delivers up to `limit`; answers whether frames queued after that remain and must be handed off
    // (`draining` then stays taken for the hand-off).
    private bool DeliverBatch(long limit)
    {
        try
        {
            while (true)
            {
                Notification<TItem> next;
                lock (queueLock)
                {
                    if (pending.IsEmpty || disposed)
                    {
                        draining = false;
                        return false;
                    }
                    if (delivered >= limit)
                        return true;
                    pending = pending.Dequeue(out next);
                    delivered++;
                }
                next.Accept(observer);
            }
        }
        catch
        {
            // Not swallowed: an observer that throws faults the caller exactly as before. The flag
            // is reset so the next producer can deliver what is still queued.
            lock (queueLock)
                draining = false;
            throw;
        }
    }

    // Queued by other threads (or re-entrantly by the observer) after the drain began: the next batch
    // runs as a tracked leaf of the pool, never on this thread. Subscribed OUTSIDE queueLock.
    private void HandOff()
        => handedOff.Disposable = handOffPool
            .InvokeBlocking(_ =>
            {
                long limit;
                lock (queueLock)
                    limit = enqueued;
                return DeliverBatch(limit);
            })
            .Subscribe(
                more =>
                {
                    if (more)
                        HandOff();
                },
                fault =>
                {
                    // A batch the pool refused or cancelled (its drain, or this subscription ending)
                    // delivered nothing: the subscriber is terminated by the pooled subscription's own
                    // teardown, and the flag is left taken so nothing is delivered after it. Anything
                    // else is the observer's own exception, reported — it has no caller to fault.
                    if (fault is OperationCanceledException)
                        return;
                    reportHandOffFault(fault);
                });
}
