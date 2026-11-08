using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using MeshWeaver.Connection.Orleans;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 The <c>[POOLSTALL]</c> capture (MeshWeaver#5388) must NAME the frame the ThreadPool's workers are
/// held in. The reproduction parks real pool workers inside one known method — the shape of a stalled
/// pool: workers that do not return — and the capture, read from a real dump of this process, has to
/// return exactly those workers standing in that method, rendered as one group naming it. The negative control takes the same capture with
/// nothing parked and must not find the method at all, so the positive reading cannot be an artefact of
/// the reader.
/// </summary>
public class ThreadPoolWorkerStacksTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    /// <summary>How many pool workers the reproduction holds.</summary>
    private const int Held = 4;

    private int parked;
    private int left;
    private int release;

    [Fact]
    public void AStalledPool_TheCaptureNamesTheFrameItsWorkersAreHeldIn()
    {
        try
        {
            for (var i = 0; i < Held; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_ => HeldInTheParkedLeaf(), null);
            SpinWait.SpinUntil(() => Volatile.Read(ref parked) == Held, Budget)
                .Should().BeTrue("the reproduction needs {0} pool workers parked before it captures", Held);

            var stacks = new ThreadPoolWorkerStacks().CaptureWorkerStacks();

            var held = stacks
                .Where(stack => stack.Contains(nameof(HeldInTheParkedLeaf), StringComparison.Ordinal))
                .ToArray();
            held.Should().HaveCount(Held, "every parked worker is a ThreadPool worker standing in the parked leaf");

            // Only the captured parked-worker subset is rendered: the rest of this test process's pool
            // (idle workers, other tests' work) is not under the test's control, so group ORDER over the
            // whole capture is pinned with controlled inputs in ThreadPoolStallTest instead.
            var reading = ProcessLiveness.Read(
                new ProcessLivenessSample(1, TimeSpan.FromSeconds(10), 0, 0, 0, TimeSpan.Zero, 0, true, Held, 0, 1_000),
                new ProcessLivenessSample(2, TimeSpan.FromSeconds(20), 0, 0, 0, TimeSpan.Zero, 0, true, Held, 20_335, 1_000),
                TimeSpan.FromSeconds(10));
            var line = ThreadPoolStall.Describe(reading, held, null);
            var group = line.Split("  ×", StringSplitOptions.None)[1];
            group.Should().StartWith($"{Held}:", "the parked workers share one stack shape");
            group.Should().Contain(nameof(HeldInTheParkedLeaf), "the group names the frame that held the workers");
        }
        finally
        {
            // Released, and every parked worker seen to LEAVE the leaf, so the negative control
            // (or any later capture) can never find one of these still standing in it.
            Volatile.Write(ref release, 1);
            SpinWait.SpinUntil(() => Volatile.Read(ref left) == Volatile.Read(ref parked), Budget);
        }
    }

    /// <summary>How far below the outer caller the contended lock sits — deeper than the 12 frames a group is keyed by.</summary>
    private const int Depth = 20;

    /// <summary>The outer caller's frame as ClrMD renders it — precise, so no other frame can match it.</summary>
    private const string OuterFrame = nameof(ThreadPoolWorkerStacksTest) + "." + nameof(OuterCaller) + "(";

    private readonly object gate = new();
    private int holding;
    private int waiting;
    private int waitersDone;

    /// <summary>
    /// 🚨 The 2026-11-07 capture (MeshWeaver#5388) named the Rx gate ≥13 workers waited on and cut every
    /// stack at 12 frames, so neither the OUTER caller that led them there nor the thread holding the
    /// gate was in the line. This reproduces that shape in a real dump: pool workers enter
    /// <see cref="OuterCaller"/>, recurse <see cref="Depth"/> frames, and wait on a monitor that a
    /// dedicated NON-pool thread holds inside the same lock-site method. The line must name the outer
    /// caller (more than <see cref="ThreadPoolStall.FramesPerGroup"/> frames below the blocking frame, so
    /// a 12-frame line cannot contain it — asserted in the test, the structural negative control) and
    /// the holder thread standing in <see cref="HolderLeaf"/>, which is not a pool worker at all.
    /// </summary>
    [Fact]
    public void WorkersWaitingOnAMonitor_TheLineNamesTheirOuterCallerAndTheCandidateHolder()
    {
        var holder = new Thread(() => EnterTheGate(hold: true)) { IsBackground = true, Name = "p5388-holder" };
        try
        {
            holder.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref holding) == 1, Budget)
                .Should().BeTrue("the holder must own the gate before the workers queue");
            var contentionBefore = Monitor.LockContentionCount;
            for (var i = 0; i < Held; i++)
                ThreadPool.UnsafeQueueUserWorkItem(_ => OuterCaller(Depth), null);
            // Positive signal that each worker is IN the monitor wait, not just on its way to it: the
            // runtime counts a contended Enter. Process-wide, so ≥ — another contention only makes it earlier
            // than `waiting`, and `waiting` is required too.
            SpinWait.SpinUntil(() => Volatile.Read(ref waiting) == Held
                                     && Monitor.LockContentionCount - contentionBefore >= Held, Budget)
                .Should().BeTrue("the reproduction needs {0} pool workers contending for the gate", Held);

            var stacks = new ThreadPoolWorkerStacks().CaptureStacks();

            var waiters = stacks.Workers
                .Where(stack => stack.Contains(OuterFrame, StringComparison.Ordinal))
                .ToArray();
            waiters.Should().HaveCount(Held, "every queued worker is parked under the outer caller");
            var frames = waiters[0].Split(Environment.NewLine);
            Array.FindIndex(frames, frame => frame.Contains(OuterFrame, StringComparison.Ordinal))
                .Should().BeGreaterThan(ThreadPoolStall.FramesPerGroup,
                    "the outer caller must sit below the innermost {0} frames, or this test could pass on a 12-frame line",
                    ThreadPoolStall.FramesPerGroup);
            stacks.OtherThreads.Should().Contain(stack => stack.Contains(nameof(HolderLeaf), StringComparison.Ordinal),
                "the holder is a dedicated thread, so only the non-worker half of the capture can show it");

            var reading = ProcessLiveness.Read(
                new ProcessLivenessSample(1, TimeSpan.FromSeconds(10), 0, 0, 0, TimeSpan.Zero, 0, true, Held, 0, 1_000),
                new ProcessLivenessSample(2, TimeSpan.FromSeconds(20), 0, 0, 0, TimeSpan.Zero, 0, true, Held, 20_335, 1_000),
                TimeSpan.FromSeconds(10));
            var line = ThreadPoolStall.Describe(reading, waiters, null, stacks.OtherThreads);

            line.Should().Contain($"×{Held}:", "the waiting workers share one shape");
            line.Should().Contain(OuterFrame, "the line names the caller that led the workers to the gate");
            line.Should().Contain("LOCK waiting to enter a monitor at", "the group is recognised as waiting on a monitor");
            line.Should().Contain(nameof(EnterTheGate), "the lock site is the method that takes the gate");
            line.Should().Contain("candidate holder:", "a thread inside the lock site without waiting is named");
            line.Should().Contain(nameof(HolderLeaf), "the candidate holder's stack is printed in full");
        }
        finally
        {
            Volatile.Write(ref release, 1);
            SpinWait.SpinUntil(() => Volatile.Read(ref waitersDone) == Volatile.Read(ref waiting), Budget);
            holder.Join(Budget);
        }
    }

    /// <summary>The outermost frame of the waiters' own code — it appears ONCE, <see cref="Depth"/> frames below the gate.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OuterCaller(int depth) => DescendTowardsTheGate(depth);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DescendTowardsTheGate(int depth)
    {
        if (depth > 0)
        {
            DescendTowardsTheGate(depth - 1);
            return;
        }
        Interlocked.Increment(ref waiting);
        try
        {
            EnterTheGate(hold: false);
        }
        finally
        {
            Interlocked.Increment(ref waitersDone);
        }
    }

    /// <summary>The lock site: every thread takes the gate here; the holder then stays in <see cref="HolderLeaf"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnterTheGate(bool hold)
    {
        lock (gate)
        {
            if (hold)
                HolderLeaf();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void HolderLeaf()
    {
        Volatile.Write(ref holding, 1);
        SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Budget);
    }

    /// <summary>Negative control: with nothing parked, the same capture does not find the parked leaf.</summary>
    [Fact]
    public void AnUnheldPool_TheCaptureDoesNotFindTheParkedLeaf()
    {
        var stacks = new ThreadPoolWorkerStacks().CaptureWorkerStacks();

        stacks.Should().NotContain(stack => stack.Contains(nameof(HeldInTheParkedLeaf), StringComparison.Ordinal));
    }

    /// <summary>A capture that cannot pin the DAC fails, and the heartbeat line says the stacks could not be read.</summary>
    [Fact]
    public void ACaptureWithoutTheLocalDac_FailsAndTheLineSaysWhy()
    {
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"no-dac-{Guid.NewGuid():N}", "libmscordaccore.so");
        var reader = new ThreadPoolWorkerStacks { Stacks = new BlockedThreadStacks(missing) };

        var failure = Record.Exception(() => reader.CaptureWorkerStacks());

        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().Contain(missing);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void HeldInTheParkedLeaf()
    {
        Interlocked.Increment(ref parked);
        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Budget);
        }
        finally
        {
            Interlocked.Increment(ref left);
        }
    }
}
