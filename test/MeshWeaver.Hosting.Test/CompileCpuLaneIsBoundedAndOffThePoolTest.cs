using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The CPU lane (<see cref="IoPoolNames.CompileCpu"/>) runs its blocking leaves on DEDICATED threads —
/// never ThreadPool workers — and never more of them at once than its cap
/// (<c>Doc/Architecture/CompileOffTheThreadPool</c>).
///
/// <para>Both halves matter. A NodeType emit on the ThreadPool holds a worker the grain turns need
/// (the roll-time routing starvation); on an UNBOUNDED dedicated thread (the #5327 shape) a roll's
/// hundreds of distinct-type compiles become hundreds of threads. The lane is the bound without the
/// pool.</para>
///
/// <para>The leaves park on a volatile flag under a bounded <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>
/// and are released in a <c>finally</c>, so the cap is observed while it is being tested — a leaf
/// that started beyond it would be counted by the leaves themselves, not inferred from timing.</para>
/// </summary>
public class CompileCpuLaneIsBoundedAndOffThePoolTest
{
    private const int Cap = 2;
    private const int Leaves = 6;

    private sealed class Probe
    {
        public volatile int Release;
        public int Running;
        public int MaxRunning;
        public int OnPool;
        public int Ran;
    }

    private static IObservable<int> ParkedLeaf(IIoPool pool, Probe probe) =>
        pool.InvokeBlocking(_ =>
        {
            if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref probe.OnPool);
            var now = Interlocked.Increment(ref probe.Running);
            int seen;
            while ((seen = Volatile.Read(ref probe.MaxRunning)) < now
                   && Interlocked.CompareExchange(ref probe.MaxRunning, now, seen) != seen) { }
            SpinWait.SpinUntil(() => probe.Release != 0, TimeSpan.FromSeconds(20));
            Interlocked.Decrement(ref probe.Running);
            return Interlocked.Increment(ref probe.Ran);
        });

    private static async Task<Probe> RunParked(IIoPool pool, CancellationToken ct)
    {
        var probe = new Probe();
        try
        {
            var all = Enumerable.Range(0, Leaves).Select(_ => ParkedLeaf(pool, probe).Await(ct)).ToArray();

            SpinWait.SpinUntil(() => Volatile.Read(ref probe.Running) == Cap, TimeSpan.FromSeconds(10))
                .Should().BeTrue($"the lane must start {Cap} leaves while the rest queue");
            // Negative "nothing happened" check, bounded: with Cap leaves parked, a further leaf can
            // only start if the cap is broken.
            SpinWait.SpinUntil(() => Volatile.Read(ref probe.Running) > Cap, TimeSpan.FromMilliseconds(300))
                .Should().BeFalse($"no more than {Cap} leaves may run at once");

            probe.Release = 1;
            await Task.WhenAll(all);
        }
        finally
        {
            probe.Release = 1;
        }
        return probe;
    }

    [Fact(Timeout = 60_000)]
    public async Task TheCpuLane_RunsEveryLeafOffThePool_AndNeverMoreThanItsCap()
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions { CompileCpu = Cap });
        var probe = await RunParked(registry.Get(IoPoolNames.CompileCpu), TestContext.Current.CancellationToken);

        probe.Ran.Should().Be(Leaves, "every queued leaf runs once a slot frees");
        probe.MaxRunning.Should().Be(Cap, "the cap is the bound — reached, never exceeded");
        probe.OnPool.Should().Be(0,
            "a CPU-lane leaf must never occupy a ThreadPool worker the grain turns and routing legs need");
    }

    /// <summary>
    /// NEGATIVE CONTROL — a pool that BORROWS ThreadPool workers, same cap, same leaves: bounded, but ON
    /// the ThreadPool. This is what every pool did before its blocking leaves moved off the pool
    /// (<see cref="BlockingLeavesStayOffTheThreadPoolTest"/>); without the control the zero above could
    /// be an instrument that counts nothing.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Control_ABorrowingPool_BoundsTheLeaves_ButRunsThemOnThePool()
    {
        using var pool = new IoPool(Cap, IoPool.DefaultDrainTimeout, IoPool.DefaultDrainGrace, dedicatedThreads: false);
        var probe = await RunParked(pool, TestContext.Current.CancellationToken);

        probe.MaxRunning.Should().Be(Cap);
        probe.OnPool.Should().Be(Leaves, "control: a borrowing pool runs every blocking leaf on a pool worker");
    }
}
