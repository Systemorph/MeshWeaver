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
/// to ONE pool leaf, which owns the drain from then on. Order is unchanged — the draining flag stays
/// taken across the hand-off — and no producer's thread outlives its own frame.</para>
///
/// <para>🚨 <b>The hand-off is ONE tracked pool leaf that owns the drain loop with its token</b>
/// (reviews of #7844). The leaf runs the consumer, and the consumer may use the service scope, so it is
/// an <see cref="IIoPool.InvokeBlocking{T}"/> leaf: counted in flight, waited for by the pool's
/// teardown, and named in its residual if it outlives the join — never a bare scheduler hop, which let
/// <c>IoPool.Disposed</c> fire under a running consumer. It delivers until the queue is empty and
/// checks its token between frames, so a drain stops it at the next frame boundary instead of letting
/// it run through a backlog. Exactly one such leaf exists at a time (the draining flag), so there is no
/// chain of re-subscriptions to order: its subscription goes into a slot published BEFORE it is
/// subscribed, and a late assignment can only dispose the slot it belongs to.</para>
///
/// <para>A hand-off the pool CANCELS (its drain, or a pool already gone) terminates the consumer with
/// that cancellation and releases the queue — not every owner sits under a pooled subscription that
/// would terminate it (autocomplete does not), and a consumer that hears nothing waits forever. A
/// hand-off ended by the subscription itself (<see cref="Dispose"/>) owes the consumer silence.</para>
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
    private enum BatchEnd { Empty, More, Cancelled }

    private readonly object queueLock = new();
    private ImmutableQueue<Notification<TItem>> pending = ImmutableQueue<Notification<TItem>>.Empty;
    // Sequence numbers of the last frame queued and the last frame delivered; a drain's batch ends at
    // the `enqueued` it read when it took the drain.
    private long enqueued;
    private long delivered;
    private bool draining;
    // Set by Dispose (the subscription ended) or by a cancelled hand-off (the consumer was terminated):
    // nothing is delivered after it.
    private bool ended;
    // The slot of the ONE hand-off leaf; replacing it disposes the previous leaf's slot, which by then
    // has released the drain.
    private readonly SerialDisposable handedOff = new();

    /// <summary>
    /// Test seam (InternalsVisibleTo): runs on the handing-off thread after the hand-off leaf has been
    /// SUBSCRIBED and before its subscription is assigned to its slot. Null in production. A test parks
    /// here to hold that assignment back while the leaf completes and a later hand-off begins — the
    /// ordering a late assignment must not be able to break.
    /// </summary>
    internal Action? OnHandOffSubscribed { get; set; }

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
            if (draining || ended)
                return;
            draining = true;
            limit = enqueued;
        }
        if (DeliverBatch(limit, CancellationToken.None) == BatchEnd.More)
            HandOff();
    }

    /// <summary>
    /// Ends the subscription's delivery: a hand-off that has not started is cancelled, and nothing
    /// queued afterwards is delivered. A hand-off already running stops at its next frame boundary —
    /// the pool tracks it until it does.
    /// </summary>
    public void Dispose()
    {
        lock (queueLock)
        {
            ended = true;
            pending = ImmutableQueue<Notification<TItem>>.Empty;
        }
        handedOff.Dispose();
    }

    // Delivers up to `limit`, checking `ct` between frames. Empty releases the drain; More and
    // Cancelled keep it taken for the caller to hand off or terminate.
    private BatchEnd DeliverBatch(long limit, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                Notification<TItem> next;
                lock (queueLock)
                {
                    // 🚨 Order matters: ended, THEN cancellation, THEN emptiness (review of #7844). A
                    // cancel that lands while the leaf runs its LAST frame finds the queue empty; read
                    // as Empty it would release the drain silently and the consumer would get no
                    // terminal — autocomplete has no pooled subscription to give it one.
                    if (ended)
                    {
                        draining = false;
                        return BatchEnd.Empty;
                    }
                    if (ct.IsCancellationRequested)
                        return BatchEnd.Cancelled;
                    if (pending.IsEmpty)
                    {
                        draining = false;
                        return BatchEnd.Empty;
                    }
                    if (delivered >= limit)
                        return BatchEnd.More;
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

    // The ONE hand-off leaf: owns the drain until the queue is empty, batch by batch, with the pool's
    // token checked between frames. Its slot is published BEFORE it is subscribed.
    private void HandOff()
    {
        var slot = new SingleAssignmentDisposable();
        handedOff.Disposable = slot;
        var subscription = handOffPool
            .InvokeBlocking(ct =>
            {
                while (true)
                {
                    long limit;
                    lock (queueLock)
                        limit = enqueued;
                    switch (DeliverBatch(limit, ct))
                    {
                        case BatchEnd.Empty:
                            return System.Reactive.Unit.Default;
                        case BatchEnd.Cancelled:
                            throw new OperationCanceledException(ct);
                    }
                }
            })
            .Subscribe(
                _ => { },
                fault =>
                {
                    if (fault is OperationCanceledException)
                        TerminateCancelled(fault);
                    else
                        // The observer's own exception: it has no caller to fault on a pool lane.
                        reportHandOffFault(fault);
                });
        OnHandOffSubscribed?.Invoke();
        slot.Disposable = subscription;
    }

    // The pool cancelled the hand-off (its drain, or a pool already gone) while the drain was taken:
    // tell the consumer, and release the queue. Never after the subscription ended — that owes silence.
    private void TerminateCancelled(Exception cancellation)
    {
        lock (queueLock)
        {
            if (ended)
                return;
            ended = true;
            draining = false;
            pending = ImmutableQueue<Notification<TItem>>.Empty;
        }
        observer.OnError(cancellation);
    }
}
