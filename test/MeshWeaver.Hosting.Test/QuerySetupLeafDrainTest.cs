using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#7277 — a silo stop's 30 s IoPool join expired over ONE in-flight Query leaf, the setup
/// leaf of <see cref="IoPool.SubscribeThroughPool{T}"/> over the <c>MeshQuery</c> fan-out. That leaf is
/// retired only when the synchronous <c>source.Subscribe</c> returns, and a merge whose providers answer
/// their Initial synchronously delivers the merged Initial inside that call. The merge's
/// <see cref="GateFreeEmitter{TItem}"/> then made the delivering thread the queue's DRAINER for every
/// frame queued while it was busy — so the setup leaf went on delivering the LIVE change feed, still
/// counted in flight and holding a Query-pool permit, for as long as frames kept arriving. A consumer
/// that writes in response to a frame feeds its own change straight back into that queue.
///
/// <para>The release INTO the parked consumer is a volatile flag polled under a bounded
/// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>, written in a <c>finally</c> so a failing
/// assertion cannot strand a pool thread.</para>
/// </summary>
public class QuerySetupLeafDrainTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A live frame queued while the setup leaf delivers the Initial must not be delivered by the setup
    /// leaf: with that frame's consumer parked, the pool's setup leaf has to have LEFT (in-flight 0).
    /// Before the fix the setup leaf was the thread parked in the live frame, in flight indefinitely.
    /// </summary>
    [Fact]
    public async Task ALiveFrameQueuedDuringTheInitial_IsNotDeliveredByTheSetupLeaf()
    {
        using var pool = new IoPool(2);
        GateFreeEmitter<int>? emitter = null;
        // The merge: a provider answers its Initial (0) synchronously, inside source.Subscribe.
        var source = Observable.Create<int>(observer =>
        {
            emitter = new GateFreeEmitter<int>(observer);
            emitter.OnNext(0);
            emitter.Drain();
            return Disposable.Empty;
        });

        var liveDelivered = new AsyncSubject<Unit>();
        var release = 0;
        var received = ImmutableList<int>.Empty;
        try
        {
            using var subscription = pool.SubscribeThroughPool(source).Subscribe(value =>
            {
                ImmutableInterlocked.Update(ref received, r => r.Add(value));
                if (value == 0)
                {
                    // The consumer reacts to the Initial with a write whose change comes back into
                    // this live query (1) — queued while this drain is still running.
                    emitter!.OnNext(1);
                    emitter.Drain();
                    return;
                }
                liveDelivered.OnNext(Unit.Default);
                liveDelivered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(10));
            });

            await liveDelivered.Should().Within(Bound).Emit("the live frame reaches the consumer");

            SpinWait.SpinUntil(() => pool.CurrentInFlight == 0, Bound).Should().BeTrue(
                "the setup leaf delivers its own Initial and returns; the live frame, parked in its consumer, "
                + "must be on another thread — not on the Query pool's setup leaf, which would then hold its "
                + "permit for as long as live frames keep arriving (MeshWeaver#7277)");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        received.Should().Equal(0, 1);
    }

    /// <summary>
    /// The hand-off keeps the emitter's contract: frames are delivered in queue order, one at a time,
    /// none lost, the frames queued during a drain included.
    /// </summary>
    [Fact]
    public async Task FramesQueuedDuringADrain_AreAllDeliveredInOrder()
    {
        var received = ImmutableList<(int Value, int Thread)>.Empty;
        var all = new AsyncSubject<Unit>();
        GateFreeEmitter<int>? emitter = null;
        var observer = Observer.Create<int>(value =>
        {
            ImmutableInterlocked.Update(ref received, r => r.Add((value, Environment.CurrentManagedThreadId)));
            if (value == 0)
            {
                emitter!.OnNext(1);
                emitter.OnNext(2);
                emitter.Drain();   // a drain is running: returns at once
                emitter.OnNext(3);
                emitter.Drain();
            }
            if (value == 3)
            {
                all.OnNext(Unit.Default);
                all.OnCompleted();
            }
        });
        emitter = new GateFreeEmitter<int>(observer);

        var drainingThread = Environment.CurrentManagedThreadId;
        emitter.OnNext(0);
        emitter.Drain();

        await all.Should().Within(Bound).Emit("every queued frame is delivered");
        received.Select(r => r.Value).Should().Equal(0, 1, 2, 3);
        received[0].Thread.Should().Be(drainingThread, "a drainer delivers the frame it queued");
    }
}
