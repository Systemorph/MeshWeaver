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
/// <para>The frames handed off instead run as TRACKED pool leaves (review of #7844), so the pool's
/// teardown still waits for a consumer running in one.</para>
///
/// <para>The release INTO a parked consumer is a volatile flag polled under a bounded
/// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>, written in a <c>finally</c> so a failing
/// assertion cannot strand a pool thread.</para>
/// </summary>
public class QuerySetupLeafDrainTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ParkBound = TimeSpan.FromSeconds(10);

    private static readonly Action<Exception> Rethrow = fault => throw fault;

    /// <summary>
    /// The merge: a provider answers its Initial (0) synchronously, inside the pooled subscribe. The
    /// consumer reacts to the Initial with a write whose change comes back into this live query (1),
    /// queued while the drain delivering the Initial is still running — and parks inside that frame.
    /// </summary>
    private static (IDisposable Subscription, AsyncSubject<Unit> LiveDelivered) SubscribeParkingOnTheLiveFrame(
        IoPool pool, Func<bool> released)
    {
        GateFreeEmitter<int>? emitter = null;
        var source = Observable.Create<int>(observer =>
        {
            emitter = new GateFreeEmitter<int>(observer, pool, Rethrow);
            emitter.OnNext(0);
            emitter.Drain();
            return emitter;
        });
        var liveDelivered = new AsyncSubject<Unit>();
        var subscription = pool.SubscribeThroughPool(source).Subscribe(value =>
        {
            if (value == 0)
            {
                emitter!.OnNext(1);
                emitter.Drain();
                return;
            }
            liveDelivered.OnNext(Unit.Default);
            liveDelivered.OnCompleted();
            SpinWait.SpinUntil(released, ParkBound);
        }, _ => { });
        return (subscription, liveDelivered);
    }

    /// <summary>
    /// A live frame queued while the setup leaf delivers the Initial must not be delivered by the setup
    /// leaf. With that frame's consumer parked, exactly ONE leaf may be in flight — the handed-off
    /// batch — and the setup leaf must have left. Before the fix the setup leaf was the thread parked in
    /// the live frame, in flight for as long as frames kept coming.
    /// </summary>
    [Fact]
    public async Task ALiveFrameQueuedDuringTheInitial_IsNotDeliveredByTheSetupLeaf()
    {
        using var pool = new IoPool(2);
        var release = 0;
        try
        {
            var (subscription, liveDelivered) = SubscribeParkingOnTheLiveFrame(
                pool, () => Volatile.Read(ref release) == 1);
            using var _ = subscription;

            await liveDelivered.Should().Within(Bound).Emit("the live frame reaches the consumer");

            SpinWait.SpinUntil(() => pool.CurrentInFlight == 1
                    && pool.PendingLeafSites.All(site => site.Contains("GateFreeEmitter", StringComparison.Ordinal)),
                Bound).Should().BeTrue(
                "the setup leaf delivers its own Initial and returns; the live frame, parked in its consumer, "
                + "is a handed-off batch of its own — not the Query pool's setup leaf, which would then hold "
                + "its permit for as long as live frames keep arriving (MeshWeaver#7277)");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// Review of #7844: a handed-off batch runs the consumer, and the consumer may use the service
    /// scope. The pool's teardown must therefore WAIT for it — <see cref="IoPool.Disposed"/> may not
    /// fire while a handed-off consumer is still running. On an untracked scheduler hop it fired at once.
    /// </summary>
    [Fact]
    public async Task Teardown_WaitsForAHandedOffBatchStillRunningItsConsumer()
    {
        var pool = new IoPool(2);
        var release = 0;
        try
        {
            var (subscription, liveDelivered) = SubscribeParkingOnTheLiveFrame(
                pool, () => Volatile.Read(ref release) == 1);
            using var _ = subscription;
            await liveDelivered.Should().Within(Bound).Emit("the handed-off batch is running its consumer");

            var fired = new AsyncSubject<Unit>();
            pool.Disposed.Subscribe(_ =>
            {
                fired.OnNext(Unit.Default);
                fired.OnCompleted();
            });

            pool.Dispose();
            await fired.Should().NotEmit(TimeSpan.FromMilliseconds(500),
                "Disposed must not fire while a handed-off batch is still running its consumer");

            Volatile.Write(ref release, 1);
            await fired.Should().Within(ParkBound).Emit("the batch finished, so teardown completes");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// Review of #7844: a hand-off leaf can finish before the thread that subscribed it has assigned its
    /// subscription. A LATE assignment of that finished leaf must not dispose the hand-off that began
    /// after it — that cancelled the live leaf and left the queue with its drain taken, so no later frame
    /// was ever delivered. The seam holds the first hand-off's assignment back while its leaf completes
    /// and a second hand-off starts and parks; frame 4, queued in the second leaf, must still arrive.
    /// </summary>
    [Fact]
    public async Task ALateAssignmentOfAFinishedHandOff_NeverCancelsTheNextOne()
    {
        using var pool = new IoPool(2);
        var releaseA = 0;
        var releaseB = 0;
        var firstDrainReturned = 0;
        var oneDelivered = new AsyncSubject<Unit>();
        var threeEntered = new AsyncSubject<Unit>();
        var fourDelivered = new AsyncSubject<Unit>();
        GateFreeEmitter<int>? emitter = null;
        var observer = Observer.Create<int>(value =>
        {
            switch (value)
            {
                case 0:
                    emitter!.OnNext(1);
                    break;
                case 1:
                    oneDelivered.OnNext(Unit.Default);
                    oneDelivered.OnCompleted();
                    break;
                case 2:
                    emitter!.OnNext(3);
                    break;
                case 3:
                    threeEntered.OnNext(Unit.Default);
                    threeEntered.OnCompleted();
                    SpinWait.SpinUntil(() => Volatile.Read(ref releaseB) == 1, ParkBound);
                    emitter!.OnNext(4);
                    break;
                case 4:
                    fourDelivered.OnNext(Unit.Default);
                    fourDelivered.OnCompleted();
                    break;
            }
        });
        using var owned = emitter = new GateFreeEmitter<int>(observer, pool, Rethrow);
        var handOffs = 0;
        emitter.OnHandOffSubscribed = () =>
        {
            if (Interlocked.Increment(ref handOffs) == 1)
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseA) == 1, ParkBound);
        };
        try
        {
            // Hand-off A: its subscribing thread parks before assigning, while A's leaf delivers 1.
            new Thread(() =>
            {
                emitter.OnNext(0);
                emitter.Drain();
                Volatile.Write(ref firstDrainReturned, 1);
            }) { IsBackground = true }.Start();
            await oneDelivered.Should().Within(Bound).Emit("hand-off A delivers frame 1");
            SpinWait.SpinUntil(() => pool.CurrentInFlight == 0, Bound).Should().BeTrue(
                "hand-off A's leaf finished while its subscribing thread is still parked before the assignment");

            // Hand-off B: begins after A finished, and parks inside frame 3.
            emitter.OnNext(2);
            emitter.Drain();
            await threeEntered.Should().Within(Bound).Emit("hand-off B is running");

            // Now A's late assignment lands.
            Volatile.Write(ref releaseA, 1);
            SpinWait.SpinUntil(() => Volatile.Read(ref firstDrainReturned) == 1, Bound).Should().BeTrue();

            Volatile.Write(ref releaseB, 1);
            await fourDelivered.Should().Within(Bound).Emit(
                "a late assignment of hand-off A must not cancel hand-off B, which still owns the drain");
        }
        finally
        {
            Volatile.Write(ref releaseA, 1);
            Volatile.Write(ref releaseB, 1);
        }
    }

    /// <summary>
    /// Review of #7844: the hand-off leaf checks the pool's token between frames, and a hand-off the
    /// pool CANCELS terminates the consumer with that cancellation. Not every owner sits under a pooled
    /// subscription that would terminate it on teardown (autocomplete does not); a consumer that hears
    /// nothing waits forever. The frame queued behind the cancellation is not delivered — and with NO
    /// frame queued behind it (the cancel lands during the hand-off's LAST frame), the consumer is
    /// terminated all the same: an empty queue must not read as "done" once the token is cancelled.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AHandOffThePoolCancels_TerminatesTheConsumer_AndStopsAtTheNextFrame(bool frameQueuedBehind)
    {
        var pool = new IoPool(2);
        var release = 0;
        var oneEntered = new AsyncSubject<Unit>();
        var faulted = new AsyncSubject<Exception>();
        var received = ImmutableList<int>.Empty;
        GateFreeEmitter<int>? emitter = null;
        var observer = Observer.Create<int>(
            value =>
            {
                ImmutableInterlocked.Update(ref received, r => r.Add(value));
                if (value == 0)
                    emitter!.OnNext(1);
                if (value != 1)
                    return;
                oneEntered.OnNext(Unit.Default);
                oneEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, ParkBound);
            },
            error =>
            {
                faulted.OnNext(error);
                faulted.OnCompleted();
            });
        using var owned = emitter = new GateFreeEmitter<int>(observer, pool, Rethrow);
        try
        {
            emitter.OnNext(0);
            emitter.Drain();
            await oneEntered.Should().Within(Bound).Emit("the hand-off leaf is running its consumer");

            if (frameQueuedBehind)
                emitter.OnNext(2);
            pool.Dispose();
            // Dispose only WAKES the pool's canceller thread. Release the consumer once the pool
            // token is actually cancelled — the canceller exits after Cancel() has returned, and the
            // hand-off's linked token is cancelled inside that call — or frame 2 could be delivered
            // before the cancellation exists, and there would be nothing to terminate.
            SpinWait.SpinUntil(() => !pool.CancellerIsAlive, Bound).Should().BeTrue(
                "the pool's canceller has run the cancel");
            Volatile.Write(ref release, 1);

            var error = await faulted.Timeout(Bound).FirstAsync().Await(TestContext.Current.CancellationToken);
            error.Should().BeAssignableTo<OperationCanceledException>(
                "the consumer is told its hand-off was cancelled, rather than hearing nothing");
            received.Should().Equal(0, 1);
            await pool.Disposed.Timeout(ParkBound).FirstAsync().Await(TestContext.Current.CancellationToken);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// The hand-off keeps the emitter's contract: frames are delivered in queue order, one at a time,
    /// none lost, the frames queued during a drain included.
    /// </summary>
    [Fact]
    public async Task FramesQueuedDuringADrain_AreAllDeliveredInOrder()
    {
        using var pool = new IoPool(2);
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
        using var owned = emitter = new GateFreeEmitter<int>(observer, pool, Rethrow);

        var drainingThread = Environment.CurrentManagedThreadId;
        emitter.OnNext(0);
        emitter.Drain();

        await all.Should().Within(Bound).Emit("every queued frame is delivered");
        received.Select(r => r.Value).Should().Equal(0, 1, 2, 3);
        received[0].Thread.Should().Be(drainingThread, "a drainer delivers the frame it queued");
    }
}
