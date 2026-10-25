using System.Collections.Immutable;
using System.Reactive;

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
/// <para>The one lock here guards the queue and the draining flag only and is never held while the
/// observer runs. Instance state of one subscription; nothing static.</para>
/// </summary>
/// <typeparam name="TItem">The frame type.</typeparam>
internal sealed class GateFreeEmitter<TItem>(IObserver<TItem> observer)
{
    private readonly object queueLock = new();
    private ImmutableQueue<Notification<TItem>> pending = ImmutableQueue<Notification<TItem>>.Empty;
    private bool draining;

    /// <summary>Queues a frame. Call under the merge's gate.</summary>
    public void OnNext(TItem value) => Enqueue(Notification.CreateOnNext(value));

    /// <summary>Queues the error terminal. Call under the merge's gate.</summary>
    public void OnError(Exception error) => Enqueue(Notification.CreateOnError<TItem>(error));

    /// <summary>Queues the completion terminal. Call under the merge's gate.</summary>
    public void OnCompleted() => Enqueue(Notification.CreateOnCompleted<TItem>());

    private void Enqueue(Notification<TItem> notification)
    {
        lock (queueLock)
            pending = pending.Enqueue(notification);
    }

    /// <summary>
    /// Delivers every queued frame, unless another thread is already doing so — then that thread
    /// delivers this caller's frames too, and this call returns at once. Call with the merge's gate
    /// RELEASED.
    /// </summary>
    public void Drain()
    {
        lock (queueLock)
        {
            if (draining)
                return;
            draining = true;
        }
        try
        {
            while (true)
            {
                Notification<TItem> next;
                lock (queueLock)
                {
                    if (pending.IsEmpty)
                    {
                        draining = false;
                        return;
                    }
                    pending = pending.Dequeue(out next);
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
}
