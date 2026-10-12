using System.Runtime.CompilerServices;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A blocking lane thread that has PARKED holds no reference to the work it last ran
/// (<c>Doc/Architecture/ControlledIoPooling</c> → "A parked lane holds nothing it ran").
///
/// <para>The defect this pins, measured with a heap dump of <c>ModuleLiveSwapTest</c> on a Debug
/// build: the retired module generation's only root was the parked lane thread's
/// <c>LimitedConcurrencyLevelTaskScheduler.LaneLoop</c> frame — two JIT temporaries of the
/// PREVIOUS iteration (the dequeued <c>LinkedListNode&lt;Task&gt;</c> and the <c>Task</c> itself)
/// still in their stack slots while the thread waited for the next item. The task's result was the
/// live swap's plan, which names the generations it retires, so a collectible context stayed rooted
/// until the lane happened to run something else. Every "the old generation is collected" assertion
/// in <c>MeshWeaver.Compiler.Pipeline.Test</c> failed on a Debug build of unchanged main for exactly
/// this reason, and passed in Release, where the JIT reports those slots dead.</para>
///
/// <para>The fix runs each take-and-execute in a frame of its own that RETURNS before the next park,
/// so nothing it touched is on the stack of a parked lane. Negative control: inline
/// <c>RunOne</c> back into <c>LaneLoop</c> and this fails on a Debug build.</para>
/// </summary>
public class AParkedLaneHoldsNothingItRanTest
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact(Timeout = 60_000)]
    public async Task WhatALeafCaptured_IsCollectible_OnceTheLaneHasParked()
    {
        using var pool = new IoPool(1);
        var ct = TestContext.Current.CancellationToken;

        var captured = await RunALeafThatReturnsAPayload(pool, ct);
        // The helper completed this method INLINE, so its frames are still below this one on the
        // stack (a Debug JIT reports their slots live). Leave them before measuring.
        await Task.Yield();

        SpinWait.SpinUntil(() => pool.CurrentInFlight == 0 && pool.ParkedBlockingThreads == pool.LiveBlockingThreads
                                 && pool.LiveBlockingThreads == 1, Bound)
            .Should().BeTrue("the lane's one thread must be kept, PARKED, before the reference graph is measured");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        captured.IsAlive.Should().BeFalse(
            "a parked lane thread must not root the task it last ran — nor the result that task holds");
    }

    // The payload is the leaf's RESULT, as the swap plan was: a Task drops its delegate once it has
    // run, but keeps its result for as long as the Task itself is reachable. Its own frame (and its own
    // state machine, cleared on completion), so the test method holds nothing but the weak reference.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RunALeafThatReturnsAPayload(IoPool pool, CancellationToken ct) =>
        Weakly(await pool.InvokeBlocking(_ => new byte[1024]).Await(ct));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Weakly(object payload) => new(payload);
}
