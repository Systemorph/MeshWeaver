using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Tests for the controlled I/O pool (<see cref="IoPool"/>) — the primitive that
/// pushes genuinely-async / sync-blocking leaf work off the hub schedulers onto a
/// bounded slice of the shared ThreadPool. These prove the two properties the
/// pool exists for: it caps concurrency, and it runs off the calling thread.
/// No <c>Task.Delay</c>-to-wait — every wait is a condition wait
/// (<see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/> / <see cref="ManualResetEventSlim"/>).
/// </summary>
public class IoPoolTest
{
    private static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Timeout10 = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Invoke_caps_in_flight_at_the_pool_bound()
    {
        const int cap = 3;
        const int total = 20;
        using var pool = new IoPool(cap);
        var release = new TaskCompletionSource();
        var current = 0;
        var max = 0;
        var maxLock = new object();

        Task<int> Run() => pool.Invoke(async ct =>
        {
            var c = Interlocked.Increment(ref current);
            lock (maxLock) { if (c > max) max = c; }
            await release.Task;          // hold the slot until the test releases
            Interlocked.Decrement(ref current);
            return c;
        }).ToTask();

        var tasks = Enumerable.Range(0, total).Select(_ => Run()).ToArray();

        // Exactly `cap` bodies should be admitted concurrently; the 4th's
        // WaitAsync cannot return until a slot frees.
        SpinWait.SpinUntil(() => Volatile.Read(ref current) == cap, Timeout5)
            .Should().BeTrue("the pool should admit exactly the cap concurrently");
        pool.CurrentInFlight.Should().Be(cap);

        release.SetResult();
        await Task.WhenAll(tasks);

        max.Should().Be(cap, "in-flight concurrency must never exceed the configured cap");
        pool.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public void Invoke_runs_the_leaf_on_the_threadpool_not_the_subscriber()
    {
        using var pool = new IoPool(2);
        AssertLeafRunsOffSubscriber(io => pool.Invoke(io));
    }

    [Fact]
    public async Task InvokeBlocking_caps_concurrency_and_runs_off_thread()
    {
        const int cap = 3;
        const int total = 12;
        using var pool = new IoPool(cap);
        using var release = new ManualResetEventSlim(false);
        var current = 0;
        var max = 0;
        var maxLock = new object();
        var callingThread = Environment.CurrentManagedThreadId;
        var ranOffThread = true;

        var tasks = Enumerable.Range(0, total).Select(_ =>
            pool.InvokeBlocking(ct =>
            {
                if (Environment.CurrentManagedThreadId == callingThread) ranOffThread = false;
                var c = Interlocked.Increment(ref current);
                lock (maxLock) { if (c > max) max = c; }
                release.Wait(ct);        // blocks a real scheduler thread
                Interlocked.Decrement(ref current);
                return c;
            }).ToTask()).ToArray();

        SpinWait.SpinUntil(() => Volatile.Read(ref current) == cap, Timeout5)
            .Should().BeTrue("the dedicated scheduler should admit exactly the cap concurrently");
        pool.CurrentInFlight.Should().Be(cap);

        release.Set();
        await Task.WhenAll(tasks).WaitAsync(Timeout10, TestContext.Current.CancellationToken);

        max.Should().Be(cap, "the limited-concurrency scheduler must never exceed the cap");
        ranOffThread.Should().BeTrue();
        pool.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public void Invoke_is_cold_no_work_runs_until_subscribe()
    {
        using var pool = new IoPool(2);
        var ran = false;

        // Building the observable must NOT run the body or take a slot.
        _ = pool.Invoke<int>(_ => { ran = true; return Task.FromResult(1); });

        ran.Should().BeFalse("Invoke returns a cold observable — work runs on Subscribe");
        pool.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public async Task Invoke_releases_the_slot_on_exception()
    {
        using var pool = new IoPool(1);

        Func<Task> faulting = () =>
            pool.Invoke<int>(_ => throw new InvalidOperationException("boom")).ToTask();
        await faulting.Should().ThrowAsync<InvalidOperationException>();

        pool.CurrentInFlight.Should().Be(0, "the finally must release the slot even on error");

        // The single slot is free again, so a follow-up op runs.
        var ok = await pool.Invoke(_ => Task.FromResult(42)).ToTask();
        ok.Should().Be(42);
    }

    [Fact]
    public void Invoke_releases_the_slot_when_subscription_disposed_before_completion()
    {
        using var pool = new IoPool(1);
        using var entered = new ManualResetEventSlim(false);

        var sub = pool.Invoke(async ct =>
        {
            entered.Set();
            await Task.Delay(System.Threading.Timeout.Infinite, ct); // cancellable hold
            return 0;
        }).Subscribe(_ => { }, _ => { });

        entered.Wait(Timeout5, TestContext.Current.CancellationToken).Should().BeTrue();
        pool.CurrentInFlight.Should().Be(1);

        sub.Dispose(); // cancels ct → Task.Delay throws → finally releases the slot

        SpinWait.SpinUntil(() => pool.CurrentInFlight == 0, Timeout5)
            .Should().BeTrue("disposing the subscription must release the held slot");
    }

    // The teardown-SIGSEGV fix: Drain must CANCEL every in-flight leaf (a live change-feed
    // subscription never completes on its own, so a WAIT-only drain — the old WhenDrained —
    // would time out and let the caller unload the node ALCs while the leaf still runs on a
    // ThreadPool thread → native use-after-unload) AND JOIN synchronously, so the instant
    // Drain returns no pool thread is executing any ALC-compiled code.
    [Fact]
    public async Task Drain_cancels_in_flight_leaves_and_joins_synchronously()
    {
        using var pool = new IoPool(2);
        using var entered = new ManualResetEventSlim(false);
        var cancelled = false;

        pool.Invoke(async ct =>
        {
            entered.Set();
            try { await Task.Delay(System.Threading.Timeout.Infinite, ct); } // never completes on its own
            catch (OperationCanceledException) { cancelled = true; throw; }
            return 0;
        }).Subscribe(_ => { }, _ => { });

        entered.Wait(Timeout5, TestContext.Current.CancellationToken).Should().BeTrue();
        pool.CurrentInFlight.Should().Be(1);

        pool.Drain(); // cancel the leaf + JOIN — returns only once it has unwound

        pool.CurrentInFlight.Should().Be(0,
            "Drain joins synchronously — no spin: when it returns every in-flight leaf has unwound");
        cancelled.Should().BeTrue("Drain cancels in-flight leaves so a never-completing one actually stops");

        // Drain is TERMINAL (it cancels the pool token) — new work issued after Drain is
        // cancelled immediately; there is no in-flight leaf left to reference an unloading ALC.
        Func<Task> afterDrain = () =>
            pool.Invoke(_ => Task.FromResult(7)).ToTask(TestContext.Current.CancellationToken);
        await afterDrain.Should().ThrowAsync<OperationCanceledException>();

        // Idempotent: a second Drain is a safe no-op join.
        pool.Drain();
        pool.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public void Unbounded_fallback_runs_the_leaf_on_the_threadpool()
    {
        AssertLeafRunsOffSubscriber(io => IoPool.Unbounded.Invoke(io));
        IoPool.Unbounded.CurrentInFlight.Should().Be(0);
    }

    // 🚨 The IO boundary must CARRY the caller's identity. The AccessContext (the
    // identity baton) rides an AsyncLocal; a write done INSIDE ioPool.Invoke — a
    // compile/activity create, a thread-execution tool call — must run under the
    // SAME identity the caller had on its thread. If the SubscribeOn(TaskPool) hop
    // wipes the AsyncLocal, the pooled body sees null → the write posts context-null
    // → RLS denies it (the "Create outside the boundary" / activity-access-denied
    // bug). These pin that the pool preserves the caller's AsyncLocal into the body.
    [Fact]
    public async Task Invoke_carries_caller_AsyncLocal_into_the_pooled_body()
    {
        using var pool = new IoPool(2);
        var baton = new AsyncLocal<string?> { Value = "owner-identity" };

        string? observed = null;
        await pool.Invoke(_ => { observed = baton.Value; return Task.FromResult(0); })
            .ToTask(TestContext.Current.CancellationToken);

        observed.Should().Be("owner-identity",
            "the caller's AsyncLocal (the AccessContext baton) must flow into the pooled body — " +
            "the IO boundary must carry identity, not wipe it on the ThreadPool thread");
    }

    [Fact]
    public async Task InvokeBlocking_carries_caller_AsyncLocal_into_the_pooled_body()
    {
        using var pool = new IoPool(2);
        var baton = new AsyncLocal<string?> { Value = "owner-identity" };

        string? observed = null;
        await pool.InvokeBlocking(_ => { observed = baton.Value; return 0; })
            .ToTask(TestContext.Current.CancellationToken);

        observed.Should().Be("owner-identity",
            "InvokeBlocking must also carry the caller's identity into the blocking body");
    }

    [Fact]
    public async Task Unbounded_fallback_carries_caller_AsyncLocal_into_the_pooled_body()
    {
        var baton = new AsyncLocal<string?> { Value = "owner-identity" };

        string? observed = null;
        await IoPool.Unbounded.Invoke(_ => { observed = baton.Value; return Task.FromResult(0); })
            .ToTask(TestContext.Current.CancellationToken);

        observed.Should().Be("owner-identity",
            "the Unbounded fallback must carry the caller's identity into the pooled body too");
    }

    // Subscribes from a dedicated (non-ThreadPool) thread and asserts the leaf
    // body runs on a ThreadPool thread distinct from the subscriber — i.e. the
    // pool genuinely escaped the calling scheduler. Robust by construction: a
    // dedicated Thread is never a ThreadPool thread, so checking
    // IsThreadPoolThread on the body avoids the flaky "different thread id" guess
    // (the ThreadPool can otherwise reuse an awaiting caller's thread).
    private static void AssertLeafRunsOffSubscriber(
        Func<Func<CancellationToken, Task<int>>, IObservable<int>> invoke)
    {
        var subscriberThread = -1;
        var bodyThread = -1;
        var bodyOnThreadPool = false;
        using var done = new ManualResetEventSlim(false);

        var t = new Thread(() =>
        {
            subscriberThread = Environment.CurrentManagedThreadId;
            invoke(_ =>
            {
                bodyThread = Environment.CurrentManagedThreadId;
                bodyOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                return Task.FromResult(0);
            }).Subscribe(_ => done.Set(), _ => done.Set());
        }) { IsBackground = true };
        t.Start();

        done.Wait(Timeout5).Should().BeTrue("the leaf should complete");
        bodyOnThreadPool.Should().BeTrue("the leaf must run on the ThreadPool, not the subscriber's thread");
        bodyThread.Should().NotBe(subscriberThread);
    }
}
