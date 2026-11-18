using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Issue #2146 — the window <see cref="IoPool"/> still had on the ACQUIRE side after #2141 fixed
/// the exit side.
///
/// <para>Every entry point on the pool returns a COLD observable: the <c>_disposed</c> fast path is
/// read when the observable is BUILT, while <c>_poolCts.Token</c> and <c>_gate.WaitAsync</c> are
/// touched later, on SUBSCRIBE. Disposal landing anywhere in that interval released both primitives
/// under a caller still entitled to use them, so an <see cref="ObjectDisposedException"/> came out
/// of a reactive chain mid-teardown — precisely the class the pool's cancellation contract exists to
/// keep out of teardown. <c>IoPoolTest.A_leaf_issued_after_disposal_is_cancelled_not_ObjectDisposed</c>
/// looks like it covers this and does not: it only ever built the leaf AFTER disposal, where the
/// fast path answers.</para>
///
/// <para>The fix is an admission region entered before the first touch and left after the last, so
/// disposal cannot complete while anyone may still reach the gate. These tests pin both halves: the
/// deterministic build-then-subscribe sequence, and the genuine acquire-side interleave where the
/// permit is handed straight from the outgoing leaf to a waiter.</para>
/// </summary>
public class IoPoolDisposeAcquireRaceTest
{
    private static readonly TimeSpan Timeout10 = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 🚨 DETERMINISTIC, no threads: the leaf is minted while the pool is alive and subscribed after
    /// it is dead. Before the fix, <c>_poolCts.Token</c> / <c>_gate.WaitAsync</c> threw
    /// ObjectDisposedException here — the pool's own contract says a leaf that will not run is a
    /// CANCELLATION.
    /// </summary>
    [Fact]
    public async Task Invoke_built_before_disposal_and_subscribed_after_is_cancelled_not_ObjectDisposed()
    {
        var pool = new IoPool(2);
        var ran = false;
        // Built while the pool is alive — this is where the `_disposed` fast path is evaluated.
        var cold = pool.Invoke(_ => { ran = true; return Task.FromResult(1); });

        pool.Dispose();

        var fault = await Record.ExceptionAsync(
            () => cold.FirstAsync().Await(TestContext.Current.CancellationToken));

        fault.Should().NotBeNull("a leaf that will not run must terminate, never hang");
        fault.Should().BeAssignableTo<OperationCanceledException>(
            "the pool is gone — that is a cancellation, not a caller bug");
        fault.Should().NotBeOfType<ObjectDisposedException>();
        ran.Should().BeFalse("the work must not run on a disposed pool");
    }

    [Fact]
    public async Task InvokeStream_built_before_disposal_and_subscribed_after_is_cancelled_not_ObjectDisposed()
    {
        var pool = new IoPool(2);
        var cold = pool.InvokeStream(One);

        pool.Dispose();

        var fault = await Record.ExceptionAsync(
            () => cold.FirstAsync().Await(TestContext.Current.CancellationToken));

        fault.Should().NotBeNull("a leaf that will not run must terminate, never hang");
        fault.Should().NotBeOfType<ObjectDisposedException>();
    }

    private static async IAsyncEnumerable<int> One([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return 1;
    }

    [Fact]
    public async Task InvokeBlocking_built_before_disposal_and_subscribed_after_is_cancelled_not_ObjectDisposed()
    {
        var pool = new IoPool(2);
        var ran = false;
        var cold = pool.InvokeBlocking(_ => { ran = true; return 1; });

        pool.Dispose();

        var fault = await Record.ExceptionAsync(
            () => cold.FirstAsync().Await(TestContext.Current.CancellationToken));

        fault.Should().BeAssignableTo<OperationCanceledException>();
        fault.Should().NotBeOfType<ObjectDisposedException>();
        ran.Should().BeFalse("the work must not run on a disposed pool");
    }

    [Fact]
    public async Task SubscribeThroughPool_built_before_disposal_and_subscribed_after_is_cancelled_not_ObjectDisposed()
    {
        var pool = new IoPool(2);
        var cold = pool.SubscribeThroughPool(Observable.Return(1));

        pool.Dispose();

        var fault = await Record.ExceptionAsync(
            () => cold.FirstAsync().Await(TestContext.Current.CancellationToken));

        fault.Should().NotBeNull("the leg must TERMINATE — a silent hang is the one unacceptable outcome");
        fault.Should().NotBeOfType<ObjectDisposedException>();
    }

    /// <summary>
    /// 🚨 THE ACQUIRE-SIDE INTERLEAVE ITSELF (#2146). One permit, two leaves: A holds it parked on a
    /// TCS the test controls, B is queued on <c>WaitAsync</c>. Disposal lands, then A is released —
    /// so A's <c>Release()</c> can hand the permit straight to B, and for the instant before B reaches
    /// <c>Interlocked.Increment(ref _inFlight)</c> the pool used to see zero leaves and dispose the
    /// gate out from under a leaf that was holding a permit.
    ///
    /// <para>The hand-off races <c>SemaphoreSlim</c>'s own release-vs-cancel resolution, so the
    /// sweep runs the interleave repeatedly to cover both outcomes. Whichever way it falls the
    /// invariants are identical and absolute: no subscriber may ever see an ObjectDisposedException,
    /// and disposal must still complete reporting nothing stranded.</para>
    /// </summary>
    [Fact]
    public async Task Disposing_while_a_leaf_is_taking_the_gate_never_surfaces_ObjectDisposed()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var pool = new IoPool(maxConcurrency: 1);
            var disposed = pool.Disposed.FirstAsync().Await();

            var aEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var a = pool.Invoke(async _ =>
            {
                aEntered.TrySetResult();
                await releaseA.Task;
                return 1;
            }).Await();

            await aEntered.Task;                       // A holds the only permit

            // B queues on the gate. It cannot proceed until A releases (or the pool cancels it).
            var b = pool.Invoke(_ => Task.FromResult(2)).Await();

            pool.Dispose();                            // disposal begins while B is at the gate
            releaseA.TrySetResult();                   // A releases the permit — B may take it here

            var faultA = await Record.ExceptionAsync(() => a);
            var faultB = await Record.ExceptionAsync(() => b);

            faultA.Should().NotBeOfType<ObjectDisposedException>(
                "the outgoing leaf must never release a gate it disposed");
            faultB.Should().NotBeOfType<ObjectDisposedException>(
                $"attempt {attempt}: the incoming leaf took the permit before it was counted, "
                + "and the pool disposed the gate underneath it");

            // Whatever the interleave, disposal must still COMPLETE — the region must never leak.
            var leaked = await disposed.WaitAsync(Timeout10, TestContext.Current.CancellationToken);
            leaked.Should().Be(0);
            pool.CurrentInFlight.Should().Be(0);
        }
    }

    /// <summary>
    /// The region must not be held past the work it guards: a pool with a long-lived
    /// <c>SubscribeThroughPool</c> subscription still open must complete disposal — otherwise
    /// <see cref="IoPool.Disposed"/> would park silo shutdown behind every live change feed routed
    /// through the pool.
    ///
    /// <para>🚨 <b>The subscription is LIVE before the pool dies, deterministically.</b> This test used
    /// to subscribe and dispose back to back and then wait for the subscriber to receive a terminal.
    /// That waited on a signal the pool's contract no longer gives (#7968, "The drain runs no consumer
    /// code"): once the source is OPEN, the drain disposes the pool's own subscription to it and
    /// delivers NOTHING, recording an unowned leak instead. Only a leg cancelled BEFORE its source
    /// opened is terminated, from the setup leaf (pinned by <c>IoPoolLateDrainRegistrationTest</c>).
    /// So the old shape passed when the canceller beat the setup leaf and timed out when the setup
    /// leaf opened the source first — a ThreadPool dispatch order, i.e. a coin toss. Here the source
    /// is open and the setup leaf has released its slot before <c>Dispose()</c>, which is the one
    /// case the name describes.</para>
    /// </summary>
    [Fact]
    public async Task Disposal_completes_even_with_a_live_SubscribeThroughPool_subscription()
    {
        var pool = new IoPool(2);
        var disposed = pool.Disposed.FirstAsync().Await();

        var sourceOpen = 0;
        var sourceTornDown = 0;
        var consumerTerminals = 0;
        var source = Observable.Create<int>(_ =>
        {
            Volatile.Write(ref sourceOpen, 1);
            return () => Volatile.Write(ref sourceTornDown, 1);
        });

        // UNOWNED on purpose — nothing disposes it before the pool does; that is the subject.
        var sub = pool.SubscribeThroughPool(source)
            .Subscribe(
                _ => { },
                _ => Interlocked.Increment(ref consumerTerminals),
                () => Interlocked.Increment(ref consumerTerminals));
        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref sourceOpen) == 1, Timeout10)
                .Should().BeTrue("precondition: the setup leaf opened the source — the subscription is live");
            // `sourceOpen` is written INSIDE source.Subscribe, before the setup leaf hands the returned
            // subscription over and leaves its region; wait for it to release its slot.
            SpinWait.SpinUntil(() => pool.CurrentInFlight == 0, Timeout10)
                .Should().BeTrue("precondition: the setup leaf finished — only the subscription is left");

            pool.Dispose();

            var leaked = await disposed.WaitAsync(Timeout10, TestContext.Current.CancellationToken);
            leaked.Should().Be(0, "the admission region is released with the subscribe, not with the subscription");
            Volatile.Read(ref sourceTornDown).Should().Be(1,
                "the drain tears down the pool's OWN resource — its subscription to the source");
            Volatile.Read(ref consumerTerminals).Should().Be(0,
                "the pool's cancel delivers nothing to a consumer whose source is open (#7968)");
            pool.UnownedLeaks.Should().Be(1, "the subscription was still attached when the pool was cancelled");
        }
        finally
        {
            sub.Dispose();
        }
    }
}
