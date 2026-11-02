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
