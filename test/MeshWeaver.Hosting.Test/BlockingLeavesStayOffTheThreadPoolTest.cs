using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Every pool's BLOCKING leaves (<see cref="IIoPool.InvokeBlocking{T}"/>) run on threads the pool
/// starts itself — never on ThreadPool workers — and never more of them at once than the pool's cap
/// (#5388, <c>Doc/Architecture/BlockingLeavesOffTheThreadPool</c>).
///
/// <para>The defect this pins: the limited-concurrency scheduler used to BORROW ThreadPool workers.
/// A blocking leaf (a network-volume read, a bundle read, a <c>git</c> wait) holds its worker for as
/// long as it blocks, the pool's minimum is <c>ProcessorCount</c>, and the caps are far above that
/// (<c>FileSystem</c> 256, <c>Http</c> 16) — so a burst could hold every worker the grain turns and
/// routing legs need, and Orleans reported ".NET Thread Pool execution stalled" while the silo waited
/// on the ThreadPool's slow thread injection. The cap alone never protected the pool; where the leaf
/// RUNS does.</para>
///
/// <para>Deterministic, not a timing: the leaves count themselves — how many run at once, and how
/// many found themselves on a ThreadPool worker — while parked on a volatile flag under a bounded
/// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>, released in a <c>finally</c>.</para>
/// </summary>
public class BlockingLeavesStayOffTheThreadPoolTest
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
        public string? ThreadName;
    }

    private static IObservable<int> ParkedLeaf(IIoPool pool, Probe probe) =>
        pool.InvokeBlocking(_ =>
        {
            if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref probe.OnPool);
            Interlocked.CompareExchange(ref probe.ThreadName, Thread.CurrentThread.Name ?? "", null);
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
                .Should().BeTrue($"the pool must start {Cap} leaves while the rest queue");
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

    /// <summary>The registry's pools that carry blocking leaves in production, plus the default cap.</summary>
    public static TheoryData<string> BlockingPools() => new()
    {
        IoPoolNames.FileSystem,
        IoPoolNames.Http,
        IoPoolNames.Process,
        IoPoolNames.Compile,
        IoPoolNames.Blob,
        "prebuilt:files",   // an unnamed resource class — takes IoPoolOptions.Default
    };

    [Theory(Timeout = 60_000)]
    [MemberData(nameof(BlockingPools))]
    public async Task EveryRegistryPool_RunsItsBlockingLeavesOffThePool_AndNeverMoreThanItsCap(string poolName)
    {
        using var registry = new IoPoolRegistry(new IoPoolOptions
        {
            FileSystem = Cap, Http = Cap, Process = Cap, Compile = Cap, Blob = Cap, Default = Cap,
        });
        var probe = await RunParked(registry.Get(poolName), TestContext.Current.CancellationToken);

        probe.Ran.Should().Be(Leaves, "every queued leaf runs once a slot frees");
        probe.MaxRunning.Should().Be(Cap, "the cap is the bound — reached, never exceeded");
        probe.OnPool.Should().Be(0,
            $"a blocking leaf of the '{poolName}' pool must never hold a ThreadPool worker — that pool "
            + "runs every grain turn and routing leg on the silo");
        probe.ThreadName.Should().Be(LimitedConcurrencyLevelTaskScheduler.BlockingThreadName,
            "an IO lane's threads carry their own name, so a dump tells them from the CPU lane");
    }

    /// <summary>
    /// A pool built directly (no registry) — the shape hosts outside the mesh use — gets the same
    /// placement by default.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADirectlyBuiltPool_RunsItsBlockingLeavesOffThePool()
    {
        using var pool = new IoPool(Cap);
        var probe = await RunParked(pool, TestContext.Current.CancellationToken);

        probe.MaxRunning.Should().Be(Cap);
        probe.OnPool.Should().Be(0, "the default constructor must not borrow ThreadPool workers");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the borrowing scheduler, same cap, same leaves: bounded, but every leaf on a
    /// ThreadPool worker. This is what every pool above did before the fix; without it the zeros above
    /// could come from an instrument that counts nothing.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Control_TheBorrowingScheduler_RunsEveryBlockingLeafOnThePool()
    {
        using var pool = new IoPool(Cap, IoPool.DefaultDrainTimeout, IoPool.DefaultDrainGrace, dedicatedThreads: false);
        var probe = await RunParked(pool, TestContext.Current.CancellationToken);

        probe.MaxRunning.Should().Be(Cap);
        probe.OnPool.Should().Be(Leaves, "control: a borrowing pool runs every blocking leaf on a pool worker");
    }
}
