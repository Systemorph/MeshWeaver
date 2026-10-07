using System.Collections.Concurrent;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A blocking lane KEEPS its threads between bursts and lets them go only when its pool is disposed
/// (#4654, <c>Doc/Architecture/CollectibleThreadStaticHandleReuse</c>).
///
/// <para>The defect this pins: the lane used to start a fresh OS thread whenever a leaf reached an
/// idle lane and let it EXIT the moment its queue drained — one thread created and destroyed per
/// burst. A thread exit is the trigger of a CoreCLR defect that frees an unrelated GC-statics box in
/// a LIVE collectible context, and the createdump of the 2026-10-07 09:53Z memex-cloud SIGSEGV
/// named a crashing thread with no managed frames whose id sat near the newest ~18,000 tids the
/// process had consumed in 54 minutes. Whatever else exits, this pool's lanes should not, once per
/// burst.</para>
///
/// <para>Deterministic, not a timing: each burst waits for the lane to go IDLE before the next one
/// starts — "idle" is either the lane's thread parked (kept) or no live lane thread at all (the old
/// exit-per-burst shape) — so the old code cannot reuse a thread by catching it mid-drain, and the
/// distinct-thread count is exact in both shapes. Negative control: revert
/// <c>LimitedConcurrencyLevelTaskScheduler.LaneLoop</c> to the exit-on-empty drain and the first fact
/// sees one thread per burst.</para>
/// </summary>
public class BlockingLaneThreadsAreKeptTest
{
    private const int Bursts = 20;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static bool LaneIsIdle(IoPool pool) =>
        pool.CurrentInFlight == 0
        && (pool.LiveBlockingThreads == 0 || pool.ParkedBlockingThreads == pool.LiveBlockingThreads);

    [Fact(Timeout = 60_000)]
    public async Task SequentialBursts_RunOnOneKeptThread_NotOneThreadPerBurst()
    {
        using var pool = new IoPool(2);
        var threads = new ConcurrentDictionary<int, Thread>();
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < Bursts; i++)
        {
            await pool.InvokeBlocking(_ =>
            {
                threads.TryAdd(Environment.CurrentManagedThreadId, Thread.CurrentThread);
                return 0;
            }).Await(ct);

            SpinWait.SpinUntil(() => LaneIsIdle(pool), Bound)
                .Should().BeTrue("the lane must go idle after each single-leaf burst");
        }

        threads.Should().HaveCount(1,
            "a burst arriving at an idle lane must wake the lane's kept thread — a thread created and "
            + "destroyed per burst is the exit storm that triggers the collectible thread-static defect");
        pool.LiveBlockingThreads.Should().Be(1, "the kept thread is still alive, parked");
        threads.Values.Single().IsAlive.Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task AWarmLane_StillRunsUpToItsCapConcurrently()
    {
        const int cap = 3;
        using var pool = new IoPool(cap);
        var ct = TestContext.Current.CancellationToken;

        // Warm: park `cap` threads.
        await RunConcurrent(pool, cap, ct);
        SpinWait.SpinUntil(() => LaneIsIdle(pool) && pool.ParkedBlockingThreads == cap, Bound)
            .Should().BeTrue($"after a {cap}-wide burst the lane keeps {cap} threads parked");

        // A second burst of `cap` parked leaves must wake ALL of them — not pulse one thread `cap`
        // times and run the leaves serially.
        var maxRunning = await RunConcurrent(pool, cap, ct);
        maxRunning.Should().Be(cap);
        pool.LiveBlockingThreads.Should().Be(cap, "waking parked threads starts no new ones");
    }

    [Fact(Timeout = 60_000)]
    public async Task Disposal_ReleasesTheKeptThreads()
    {
        var pool = new IoPool(2);
        var ct = TestContext.Current.CancellationToken;
        var threads = new ConcurrentDictionary<int, Thread>();
        var release = new Probe();
        try
        {
            var both = Enumerable.Range(0, 2).Select(_ => pool.InvokeBlocking(_ =>
            {
                threads.TryAdd(Environment.CurrentManagedThreadId, Thread.CurrentThread);
                Interlocked.Increment(ref release.Running);
                SpinWait.SpinUntil(() => release.Go != 0, Bound);
                return 0;
            }).Await(ct)).ToArray();
            SpinWait.SpinUntil(() => Volatile.Read(ref release.Running) == 2, Bound).Should().BeTrue();
            release.Go = 1;
            await Task.WhenAll(both);
        }
        finally
        {
            release.Go = 1;
        }

        SpinWait.SpinUntil(() => LaneIsIdle(pool) && pool.ParkedBlockingThreads == 2, Bound)
            .Should().BeTrue("both lane threads are kept, parked, before disposal");

        pool.Dispose();
        await pool.Disposed.Timeout(Bound).FirstAsync().Await(ct);

        SpinWait.SpinUntil(() => threads.Values.All(t => !t.IsAlive), Bound)
            .Should().BeTrue("disposal must let the kept lane threads exit — a pool must not leak threads past its mesh");
        pool.LiveBlockingThreads.Should().Be(0);
    }

    private sealed class Probe
    {
        public volatile int Go;
        public int Running;
    }

    private static async Task<int> RunConcurrent(IoPool pool, int width, CancellationToken ct)
    {
        var probe = new Probe();
        var max = 0;
        try
        {
            var all = Enumerable.Range(0, width).Select(_ => pool.InvokeBlocking(_ =>
            {
                var now = Interlocked.Increment(ref probe.Running);
                int seen;
                while ((seen = Volatile.Read(ref max)) < now
                       && Interlocked.CompareExchange(ref max, now, seen) != seen) { }
                SpinWait.SpinUntil(() => probe.Go != 0, Bound);
                Interlocked.Decrement(ref probe.Running);
                return 0;
            }).Await(ct)).ToArray();

            SpinWait.SpinUntil(() => Volatile.Read(ref probe.Running) == width, Bound)
                .Should().BeTrue($"all {width} leaves must run at once under a cap of {width}");
            probe.Go = 1;
            await Task.WhenAll(all);
        }
        finally
        {
            probe.Go = 1;
        }
        return max;
    }
}
