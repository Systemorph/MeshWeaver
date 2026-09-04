using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh.Threading;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Tests for the controlled I/O pool (<see cref="IoPool"/>) — the primitive that
/// pushes genuinely-async / sync-blocking leaf work off the hub schedulers onto a
/// bounded slice of the shared ThreadPool. These prove the two properties the
/// pool exists for: it caps concurrency, and it runs off the calling thread.
/// No <c>Task.Delay</c>-to-wait — every wait is a condition wait.
///
/// <para>🚨 And no hand-woven concurrency gate, in either direction. A signal a POOL LEAF produces
/// and the test consumes is an <see cref="AsyncSubject{T}"/> the leaf completes, awaited through
/// the assertion helpers (never a blocking bridge). A release that travels the other way — INTO a
/// leaf this test deliberately parks, because "a leaf that ignores its cancellation token" IS the
/// subject of half these tests — is a volatile flag the leaf polls under a bounded
/// <see cref="SpinWait.SpinUntil(Func{bool}, TimeSpan)"/>, written in a <c>finally</c> so a failing
/// assertion cannot leave a pool thread parked into the next test.</para>
/// </summary>
public class IoPoolTest
{
    private static readonly TimeSpan Timeout5 = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Timeout10 = TimeSpan.FromSeconds(10);
    /// <summary>A drain grace for tests whose leaf can only end by cancellation.</summary>
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 🚨 The grace: a leaf that is going to finish is NOT cancelled. The drain waits for it,
    /// joins it, and reports nothing — the write it was doing landed. The predecessor cancelled
    /// first and joined second, so every in-flight write at teardown was aborted the instant the
    /// mesh decided to go down.
    /// </summary>
    [Fact]
    public async Task Drain_letsALeafThatFinishesWithinTheGrace_Complete_WithoutCancellingIt()
    {
        using var pool = new IoPool(2, IoPool.DefaultDrainTimeout, TimeSpan.FromSeconds(3));
        var entered = new AsyncSubject<Unit>();
        var cancelled = false;
        var completed = false;

        pool.Invoke(async ct =>
        {
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            try { await Task.Delay(400, ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            completed = true;
            return 0;
        }).Subscribe(_ => { }, _ => { });

        await entered.Should().Within(Timeout5).Emit();
        pool.CurrentInFlight.Should().Be(1);

        var residual = pool.Drain();

        residual.Should().Be(0);
        completed.Should().BeTrue("the leaf finished on its own inside the grace");
        cancelled.Should().BeFalse("a leaf that finishes is never cancelled — its work landed");
        pool.LeavesCancelledAfterGrace.Should().Be(0);
        pool.CancelledLeafSites.Should().BeEmpty();
        pool.CurrentInFlight.Should().Be(0);
    }

    /// <summary>
    /// The grace is a STALL bound, not a duration: every completion restarts it. Three leaves on a
    /// pool of one run back to back for longer than one grace in total, and none is cancelled,
    /// because each finished within one grace of the previous one.
    /// </summary>
    [Fact]
    public async Task Drain_restartsTheGraceOnEveryCompletion_SoABurstOfShortLeavesIsNeverCancelled()
    {
        var grace = TimeSpan.FromMilliseconds(500);
        using var pool = new IoPool(1, IoPool.DefaultDrainTimeout, grace);
        // Completed by WHICHEVER leaf the pool admits first — the ThreadPool decides the order, not
        // the order of subscription, so "the first leaf" is the first one running, never leaf 0.
        var firstAdmitted = new AsyncSubject<Unit>();
        var cancelledLeaves = 0;
        var completedLeaves = 0;

        for (var i = 0; i < 3; i++)
        {
            pool.Invoke(async ct =>
            {
                firstAdmitted.OnNext(Unit.Default);
                firstAdmitted.OnCompleted();
                try { await Task.Delay(300, ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelledLeaves); throw; }
                Interlocked.Increment(ref completedLeaves);
                return 0;
            }).Subscribe(_ => { }, _ => { });
        }

        await firstAdmitted.Should().Within(Timeout5).Emit();
        var sw = Stopwatch.StartNew();
        var residual = pool.Drain();
        sw.Stop();

        residual.Should().Be(0);
        Volatile.Read(ref completedLeaves).Should().Be(3, "every leaf finished on its own");
        Volatile.Read(ref cancelledLeaves).Should().Be(0,
            "the drain waited ~900 ms across three completions on a 500 ms grace — the clock restarts "
            + "on every completion, so work that keeps finishing is never cancelled");
        pool.LeavesCancelledAfterGrace.Should().Be(0);
        sw.Elapsed.Should().BeGreaterThan(grace,
            "the total wait must exceed one grace, or this test never exercised the restart");
    }

    // 🚨 PIN for the endemic teardown SIGSEGV (Hosting.Monolith.Test exit=139). Root cause: a
    // MeshQuery straggler whose SUBSCRIBE (initial emission → route → CreateHub → Autofac
    // BeginLifetimeScope) ran on an UNTRACKED TaskPoolScheduler.Default, so teardown's drain never
    // waited for it and the Autofac scope was disposed mid-construction → native use-after-free.
    // The fix routes the subscribe through IIoPool.SubscribeThroughPool, so it's TRACKED and Drain()
    // JOINS it. This test pins that guarantee DETERMINISTICALLY (no flaky ~50% CI repro): a subscribe
    // that is in-flight holds a slot, and Drain() BLOCKS until it releases — i.e. the scope can never
    // be torn down while a BeginLifetimeScope is running.
    /// <summary>
    /// 🚨 <c>Dispose()</c> must not RETURN OVER live work — but it must also not BLOCK.
    ///
    /// <para>It used to set <c>_disposed</c> and only then call <c>Drain()</c>, whose guard
    /// (<c>if (_disposed) return 0</c>) returned immediately on exactly that flag — so it joined
    /// NOTHING, and its promise ("when it returns, no pool thread is running, so the caller may
    /// safely unload the node ALCs") was unbacked on the path that unloads every ALC.</para>
    ///
    /// <para>The obvious repair — call Drain() first — is the wrong one, and CI proved it:
    /// <c>using var pool = new IoPool(8)</c> in an async test runs Dispose on a ThreadPool thread,
    /// so a 30 s synchronous join parks a pool thread while the very leaves it waits for need pool
    /// threads to observe cancellation and release their permits. On a 4-vCPU runner that starves
    /// into a deadlock (OrderedRouteDispatcherTest, hung for its full budget); an 18-core dev box
    /// hides it entirely. So Dispose CANCELS and returns, and the waiting lives on
    /// <see cref="IoPool.Disposed"/>.</para>
    /// </summary>
    [Fact]
    public async Task Dispose_cancels_without_blocking_and_reports_through_Disposed()
    {
        var pool = new IoPool(2);
        var entered = new AsyncSubject<Unit>();
        var observedCancellation = false;

        pool.InvokeBlocking(ct =>
        {
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            // Honours the token — so the pool's cancel is what ends it.
            ct.WaitHandle.WaitOne(Timeout10);
            observedCancellation = ct.IsCancellationRequested;
            return 0;
        }).Subscribe(_ => { }, _ => { });

        await entered.Should().Within(Timeout5).Emit();
        pool.CurrentInFlight.Should().Be(1);

        // Dispose must return PROMPTLY even with a leaf in flight — the whole point.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        pool.Dispose();
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "Dispose must not block on the leaf — blocking here is the starvation deadlock that "
            + "hung OrderedRouteDispatcherTest for its full 30 s budget on CI");

        // …and Disposed fires only once the leaf has actually unwound.
        var residual = await pool.Disposed.Timeout(Timeout10).FirstAsync().Await(TestContext.Current.CancellationToken);
        residual.Should().Be(0, "the leaf observed the cancel and unwound — the join is real");
        observedCancellation.Should().BeTrue("Dispose must CANCEL in-flight work, not merely stop accepting new work");
        pool.CurrentInFlight.Should().Be(0);
    }

    /// <summary>
    /// <see cref="IoPool.Disposed"/> must not fire while a leaf is still running — a subscriber
    /// that proceeds on it would unload ALCs over live code — and must replay to a late subscriber,
    /// since the silo participant may attach after disposal already finished.
    /// </summary>
    [Fact]
    public async Task Disposed_waits_for_the_last_leaf_and_replays_to_a_late_subscriber()
    {
        var pool = new IoPool(2);
        var entered = new AsyncSubject<Unit>();
        var release = 0;

        pool.InvokeBlocking(_ =>
            {
                entered.OnNext(Unit.Default);
                entered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Timeout10);
                return 0;
            })
            .Subscribe(_ => { }, _ => { });
        await entered.Should().Within(Timeout5).Emit();

        // An EARLY subscriber — the property under test is that IT is not notified prematurely,
        // which a late `pool.Disposed` read (AsyncSubject replays) could not distinguish.
        var fired = new AsyncSubject<Unit>();
        pool.Disposed.Subscribe(_ =>
        {
            fired.OnNext(Unit.Default);
            fired.OnCompleted();
        });

        try
        {
            pool.Dispose();
            await fired.Should().NotEmit(300.Milliseconds(),
                "Disposed must not fire while the leaf is still running");

            Volatile.Write(ref release, 1);
            await pool.Disposed.Timeout(Timeout10).FirstAsync().Await(TestContext.Current.CancellationToken);
            await fired.Should().Within(Timeout5).Emit();

            var late = -1;
            pool.Disposed.Subscribe(n => late = n);
            late.Should().Be(0, "AsyncSubject replays the terminal report to a late subscriber");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// The registry aggregate — what <c>IoPoolSiloTeardown</c> awaits. It must report only once
    /// EVERY pool has finished, so the silo never releases over live work in a pool nobody looked at.
    /// </summary>
    [Fact]
    public async Task Registry_Disposed_reports_only_after_every_pool_has_finished()
    {
        var registry = new IoPoolRegistry();
        var a = registry.Get("pool-a");
        var b = registry.Get("pool-b");
        var enteredA = new AsyncSubject<Unit>();
        var enteredB = new AsyncSubject<Unit>();
        var releaseA = 0;
        var releaseB = 0;

        a.InvokeBlocking(_ =>
        {
            enteredA.OnNext(Unit.Default);
            enteredA.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref releaseA) == 1, Timeout10);
            return 0;
        }).Subscribe(_ => { }, _ => { });
        b.InvokeBlocking(_ =>
        {
            enteredB.OnNext(Unit.Default);
            enteredB.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref releaseB) == 1, Timeout10);
            return 0;
        }).Subscribe(_ => { }, _ => { });

        try
        {
            await enteredA.Should().Within(Timeout5).Emit();
            await enteredB.Should().Within(Timeout5).Emit();
            registry.TotalInFlight.Should().Be(2);

            var fired = new AsyncSubject<Unit>();
            registry.Disposed.Subscribe(_ =>
            {
                fired.OnNext(Unit.Default);
                fired.OnCompleted();
            });
            registry.Dispose();

            Volatile.Write(ref releaseA, 1);
            await fired.Should().NotEmit(300.Milliseconds(),
                "one pool finishing is not every pool finishing");

            Volatile.Write(ref releaseB, 1);
            var total = await registry.Disposed.Timeout(Timeout10).FirstAsync().Await(TestContext.Current.CancellationToken);
            total.Should().Be(0, "both pools joined — the silo may release");
        }
        finally
        {
            Volatile.Write(ref releaseA, 1);
            Volatile.Write(ref releaseB, 1);
        }
    }

    /// <summary>
    /// 🚨 A pool obtained AFTER disposal began must be REFUSED, not created live.
    ///
    /// <para><c>Dispose()</c> snapshots <c>_pools</c> and clears it. Without a guard, a racing
    /// <c>Get(...)</c> re-populates the dictionary with a brand-new, fully live pool that nobody
    /// will ever cancel or join — work issued on it runs unsupervised straight through the ALC
    /// unload, which is the exact hole this teardown path exists to close. (Copilot review, #1887.)</para>
    /// </summary>
    [Fact]
    public async Task A_pool_requested_after_disposal_is_refused_not_created_live()
    {
        var registry = new IoPoolRegistry();
        registry.Get("before");          // one real pool, so disposal has something to do
        registry.Dispose();
        await registry.Disposed.Timeout(Timeout10).FirstAsync().Await(TestContext.Current.CancellationToken);

        // A name never seen before disposal — the case that used to mint a live pool.
        var late = registry.Get("after-disposal");
        var ran = false;
        var act = async () => await late.Invoke(_ => { ran = true; return Task.FromResult(1); })
            .FirstAsync().Await(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse("work must not run on a pool handed out after teardown began");
        registry.TotalInFlight.Should().Be(0,
            "a refused pool is not tracked — nothing can be in flight on it");
    }

    /// <summary>
    /// 🚨 A leaf issued AFTER disposal must be CANCELLED, never <see cref="ObjectDisposedException"/>.
    ///
    /// <para>This is what makes disposing the pool during SILO shutdown safe. The mesh is torn down
    /// afterwards (<c>MeshTeardownHostedService</c> runs in <c>StoppedAsync</c>), and hub disposal
    /// keeps pushing final writes at the pool. Disposal releases <c>_poolCts</c> and <c>_gate</c>,
    /// so the natural path would throw ObjectDisposedException out of a reactive chain during
    /// teardown — the "catastrophic teardown" class. Cancellation is also the honest answer: a
    /// DRAINED pool already cancels everything issued after it, and disposal is that same terminal
    /// state plus the release, so callers must see the same thing either way.</para>
    /// </summary>
    [Fact]
    public async Task A_leaf_issued_after_disposal_is_cancelled_not_ObjectDisposed()
    {
        var pool = new IoPool(2);
        pool.Dispose();

        var ran = false;
        var act = async () => await pool.Invoke(_ => { ran = true; return Task.FromResult(1); })
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<OperationCanceledException>())
            .Which.Should().NotBeOfType<ObjectDisposedException>();
        ran.Should().BeFalse("the work must not run on a disposed pool");

        var blocking = async () => await pool.InvokeBlocking(_ => 1)
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        await blocking.Should().ThrowAsync<OperationCanceledException>();

        var through = async () => await pool.SubscribeThroughPool(Observable.Return(1))
            .FirstAsync().Await(TestContext.Current.CancellationToken);
        await through.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SubscribeThroughPool_holds_a_slot_and_Drain_joins_the_in_flight_subscribe()
    {
        using var pool = new IoPool(4);
        var subscribeEntered = new AsyncSubject<Unit>();
        var releaseSubscribe = 0;
        var subscribeFinished = false;

        // A source whose SUBSCRIBE blocks — stands in for the initial-emission → CreateHub window.
        var source = Observable.Create<int>(obs =>
        {
            subscribeEntered.OnNext(Unit.Default);
            subscribeEntered.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref releaseSubscribe) == 1, Timeout5);
            subscribeFinished = true;
            obs.OnNext(1);
            return System.Reactive.Disposables.Disposable.Empty;
        });

        using var sub = pool.SubscribeThroughPool(source).Subscribe(_ => { });

        try
        {
            await subscribeEntered.Should().Within(Timeout5).Emit("the subscribe must run on the pool");
            Assert.True(SpinWait.SpinUntil(() => pool.CurrentInFlight == 1, Timeout5),
                "the subscribe must hold a pool slot (tracked) for its duration — else the drain can't see it");

            // Drain() must JOIN: block until the in-flight subscribe releases its slot.
            var drainDone = Task.Run(() => pool.Drain());
            // Confirm Drain does NOT complete while the subscribe still holds a slot (the owning scope must
            // not dispose yet) — a "wait to confirm nothing happened" negative check.
            var winner = await Task.WhenAny(drainDone, Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(drainDone, winner);

            Volatile.Write(ref releaseSubscribe, 1);

            await drainDone.WaitAsync(Timeout5);
            Assert.True(subscribeFinished, "Drain must have JOINED the in-flight subscribe before returning");
            Assert.Equal(0, pool.CurrentInFlight);
        }
        finally
        {
            Volatile.Write(ref releaseSubscribe, 1);
        }
    }

    [Fact]
    public async Task Invoke_caps_in_flight_at_the_pool_bound()
    {
        const int cap = 3;
        const int total = 20;
        using var pool = new IoPool(cap);
        // 🚨 AsyncSubject + Await(), never a TaskCompletionSource (MeshWeaver#2809). The TCS here
        // carried NO RunContinuationsAsynchronously, so a single SetResult() resumed all `cap`
        // parked leaves INLINE on the test thread — the inline-resumption defect
        // ObservableToTaskBridgeGuard exists for. ObservableAwait.Await() sets that flag itself.
        var release = new AsyncSubject<Unit>();
        var current = 0;
        var max = 0;
        var maxLock = new object();

        Task<int> Run() => pool.Invoke(async ct =>
        {
            var c = Interlocked.Increment(ref current);
            lock (maxLock) { if (c > max) max = c; }
            await release.Await();       // hold the slot until the test releases
            Interlocked.Decrement(ref current);
            return c;
        }).Await();

        var tasks = Enumerable.Range(0, total).Select(_ => Run()).ToArray();

        try
        {
            // Exactly `cap` bodies should be admitted concurrently; the 4th's
            // WaitAsync cannot return until a slot frees.
            SpinWait.SpinUntil(() => Volatile.Read(ref current) == cap, Timeout5)
                .Should().BeTrue("the pool should admit exactly the cap concurrently");
            pool.CurrentInFlight.Should().Be(cap);

            release.OnNext(Unit.Default);
            release.OnCompleted();
            await Task.WhenAll(tasks);

            max.Should().Be(cap, "in-flight concurrency must never exceed the configured cap");
            pool.CurrentInFlight.Should().Be(0);
        }
        finally
        {
            // 🚨 The release belongs in a finally: an assertion that throws above would otherwise
            // leave all 20 pooled leaves parked on a signal that never arrives, and the test host
            // would hang rather than report the assertion. Completing twice is a no-op.
            release.OnNext(Unit.Default);
            release.OnCompleted();
        }
    }

    [Fact]
    public async Task Invoke_runs_the_leaf_on_the_threadpool_not_the_subscriber()
    {
        using var pool = new IoPool(2);
        await AssertLeafRunsOffSubscriber(io => pool.Invoke(io));
    }

    [Fact]
    public async Task InvokeBlocking_caps_concurrency_and_runs_off_thread()
    {
        const int cap = 3;
        const int total = 12;
        using var pool = new IoPool(cap);
        var release = 0;
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
                // blocks a real scheduler thread — that occupancy IS what the cap is measured on
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref release) == 1 || ct.IsCancellationRequested, Timeout10);
                ct.ThrowIfCancellationRequested();
                Interlocked.Decrement(ref current);
                return c;
            }).Await()).ToArray();

        try
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref current) == cap, Timeout5)
                .Should().BeTrue("the dedicated scheduler should admit exactly the cap concurrently");
            pool.CurrentInFlight.Should().Be(cap);

            Volatile.Write(ref release, 1);
            await Task.WhenAll(tasks).WaitAsync(Timeout10, TestContext.Current.CancellationToken);

            max.Should().Be(cap, "the limited-concurrency scheduler must never exceed the cap");
            ranOffThread.Should().BeTrue();
            pool.CurrentInFlight.Should().Be(0);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    // 🚨 REPRO for the #613 family: Drain()'s contract says "0 means the join is REAL: no pool
    // thread is still running when this returns", and every teardown orchestrator
    // (MonolithMeshTestBase, MeshTeardownExtensions, HubTestBase) trusts that return value before
    // it disposes the Autofac scope and unloads the collectible node ALCs. But the join is
    // implemented by re-acquiring the SemaphoreSlim gate — and InvokeBlocking NEVER takes a gate
    // permit (it dispatches on the LimitedConcurrencyLevelTaskScheduler and only bumps _inFlight).
    // So for a blocking leaf the permits are free immediately, Drain returns 0, and teardown
    // proceeds over live work. Deterministic: the leaf below ignores its cancellation token, which
    // is EXACTLY the case Drain's non-zero return is documented to report.
    [Fact]
    public async Task Drain_joins_InvokeBlocking_leaves_too()
    {
        using var pool = new IoPool(2);
        var entered = new AsyncSubject<Unit>();
        var release = 0;

        pool.InvokeBlocking(_ =>
        {
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            // deliberately ignores the token — that is the case Drain's non-zero return reports
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Timeout10);
            return 0;
        }).Subscribe(_ => { }, _ => { });

        try
        {
            await entered.Should().Within(Timeout5).Emit();
            pool.CurrentInFlight.Should().Be(1, "a running blocking leaf must be visible as in-flight");

            // Drain on ANOTHER thread and assert it does NOT return while the leaf runs. That is the
            // contract — "0 means no pool thread is still running" — and it makes the repro fast: the
            // earlier shape let Drain sit out the leaf's full 10s hold to observe the same thing.
            // (Copilot review, #1334.)
            var drain = Task.Run(pool.Drain, TestContext.Current.CancellationToken);
            // await, never Task.Wait/.Result — CI's analyzers (xUnit1031) reject blocking task ops in a
            // test, and they are right: this test exists to prove a JOIN, so it must not itself block.
            var raced = await Task.WhenAny(drain, Task.Delay(300, TestContext.Current.CancellationToken));
            raced.Should().NotBeSameAs(drain,
                "Drain must block while a blocking leaf is still executing — a blocking leaf holds no "
                + "gate permit, so re-acquiring the gate joins nothing and Drain used to return 0 here");

            Volatile.Write(ref release, 1);

            var residual = await drain.WaitAsync(Timeout5, TestContext.Current.CancellationToken);
            residual.Should().Be(0, "the leaf unwound within the budget — nothing to report");
            pool.CurrentInFlight.Should().Be(0);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// 🚨 The blocking-idle signal must be driven by a DEDICATED counter. <c>_inFlight</c> is shared
    /// with <c>Invoke</c>/<c>InvokeStream</c>/<c>SubscribeThroughPool</c>, so 0↔1 transitions on it do
    /// not correspond to blocking work starting or stopping: a blocking leaf starting while an async
    /// leaf ran incremented the shared counter to 2, the Reset never fired, and Drain saw "idle" —
    /// re-introducing the very bug the join was added to fix. (Copilot review, #1334.)
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Drain_joinsABlockingLeaf_evenWhileAnAsyncLeafIsRunning()
    {
        using var pool = new IoPool(4);
        var asyncEntered = new AsyncSubject<Unit>();
        var blockingEntered = new AsyncSubject<Unit>();
        var release = 0;
        // Same rule as above (MeshWeaver#2809): the sanctioned bridge, not a hand-rolled gate. No
        // cancellation token on the Await — this test's premise is that the async leaf is STILL in
        // flight when the blocking join runs, so a ct-aware wait that unwound on the drain's cancel
        // would weaken exactly what it measures.
        var asyncGate = new AsyncSubject<int>();

        try
        {
            // An ASYNC leaf, in flight for the whole test — it holds the shared counter above zero.
            pool.Invoke(_ =>
            {
                asyncEntered.OnNext(Unit.Default);
                asyncEntered.OnCompleted();
                return asyncGate.Await();
            }).Subscribe(_ => { }, _ => { });
            await asyncEntered.Should().Within(Timeout5).Emit();

            // …and now a BLOCKING leaf on top of it.
            pool.InvokeBlocking(_ =>
            {
                blockingEntered.OnNext(Unit.Default);
                blockingEntered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Timeout10);
                return 0;
            }).Subscribe(_ => { }, _ => { });
            await blockingEntered.Should().Within(Timeout5).Emit();

            var drain = Task.Run(pool.Drain, TestContext.Current.CancellationToken);
            var raced = await Task.WhenAny(drain, Task.Delay(300, TestContext.Current.CancellationToken));
            raced.Should().NotBeSameAs(drain,
                "the blocking leaf is still running, and a concurrently-running ASYNC leaf must not make "
                + "the blocking join believe the pool is idle");

            Volatile.Write(ref release, 1);
            asyncGate.OnNext(0);
            asyncGate.OnCompleted();

            // Assert the VALUE, not just that it returned: both leaves unwound inside the budget, so a
            // non-zero residual would mean the join reported a survivor that had already finished — the
            // other direction of the shared-counter defect. (Copilot review, #1338.)
            var residual = await drain.WaitAsync(Timeout5, TestContext.Current.CancellationToken);
            residual.Should().Be(0,
                "both the blocking and the async leaf unwound within the budget — nothing to report");
            pool.CurrentInFlight.Should().Be(0);
        }
        finally
        {
            Volatile.Write(ref release, 1);
            asyncGate.OnNext(0);
            asyncGate.OnCompleted();
        }
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
            pool.Invoke<int>(_ => throw new InvalidOperationException("boom")).Await();
        await faulting.Should().ThrowAsync<InvalidOperationException>();

        pool.CurrentInFlight.Should().Be(0, "the finally must release the slot even on error");

        // The single slot is free again, so a follow-up op runs.
        var ok = await pool.Invoke(_ => Task.FromResult(42)).Await();
        ok.Should().Be(42);
    }

    [Fact]
    public async Task Invoke_releases_the_slot_when_subscription_disposed_before_completion()
    {
        using var pool = new IoPool(1);
        var entered = new AsyncSubject<Unit>();

        var sub = pool.Invoke(async ct =>
        {
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            await Task.Delay(System.Threading.Timeout.Infinite, ct); // cancellable hold
            return 0;
        }).Subscribe(_ => { }, _ => { });

        await entered.Should().Within(Timeout5).Emit();
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
        // A leaf that can only end by cancellation is WEDGED by definition, so this test spends the
        // grace deliberately short: the subject is the cancel + join, not the wait for a completion
        // that will never come.
        using var pool = new IoPool(2, IoPool.DefaultDrainTimeout, ShortGrace);
        var entered = new AsyncSubject<Unit>();
        var cancelled = false;

        pool.Invoke(async ct =>
        {
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            try { await Task.Delay(System.Threading.Timeout.Infinite, ct); } // never completes on its own
            catch (OperationCanceledException) { cancelled = true; throw; }
            return 0;
        }).Subscribe(_ => { }, _ => { });

        await entered.Should().Within(Timeout5).Emit();
        pool.CurrentInFlight.Should().Be(1);

        pool.Drain(); // grace expires (no progress) → cancel the leaf + JOIN — returns only once it has unwound

        pool.CurrentInFlight.Should().Be(0,
            "Drain joins synchronously — no spin: when it returns every in-flight leaf has unwound");
        cancelled.Should().BeTrue("Drain cancels in-flight leaves so a never-completing one actually stops");
        pool.LeavesCancelledAfterGrace.Should().Be(1,
            "the leaf outlived the grace with the pool making no progress — the drain must SAY it killed it");
        string.Join(" | ", pool.CancelledLeafSites).Should().Contain(nameof(Drain_cancels_in_flight_leaves_and_joins_synchronously),
            "the killed leaf is named by its site, so the report points at the work that did not finish");

        // Drain is TERMINAL (it cancels the pool token) — new work issued after Drain is
        // cancelled immediately; there is no in-flight leaf left to reference an unloading ALC.
        Func<Task> afterDrain = () =>
            pool.Invoke(_ => Task.FromResult(7)).Await(TestContext.Current.CancellationToken);
        await afterDrain.Should().ThrowAsync<OperationCanceledException>();

        // Idempotent: a second Drain is a safe no-op join.
        pool.Drain();
        pool.CurrentInFlight.Should().Be(0);
    }

    /// <summary>
    /// 🚨 A leg the DRAIN cancels must still TERMINATE — issues #1172 / #1284.
    ///
    /// <para>A cancelled subscribe is expected teardown, so it is right not to surface it as a
    /// fault. It is NOT right to surface nothing at all. Previously the
    /// <see cref="OperationCanceledException"/> from the gate wait was swallowed outright, and the
    /// drain's own registration merely DISPOSES the inner subscription — disposal emits nothing. So
    /// the observable returned by <see cref="IIoPool.SubscribeThroughPool{T}"/> terminated neither
    /// completed nor errored, and every <c>.Finally(...)</c> hanging off it never ran.</para>
    ///
    /// <para>That bookkeeping is not cosmetic: it is what releases a route's in-flight slot
    /// (<c>RoutingGrain.Dispatch</c>) and what advances the per-destination FIFO
    /// (<c>OrderedRouteDispatcher.DrainNext</c>). A leg cancelled by the drain therefore leaked its
    /// slot and stranded its destination's queue for good — a silent non-termination, the one
    /// failure mode this codebase never accepts.</para>
    ///
    /// <para>Deterministic: cap 1, leg A blocks INSIDE its subscribe holding the only permit, so
    /// leg B is parked on the gate when the drain cancels it. No sleeps — every wait is a condition
    /// wait.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Drain_terminates_a_SubscribeThroughPool_leg_it_cancels()
    {
        // Leg A holds the only permit inside a subscribe that never returns on its own — wedged by
        // construction — so the grace is spent short; the subject is what the cancel does to leg B.
        using var pool = new IoPool(1, IoPool.DefaultDrainTimeout, ShortGrace);
        var legAEntered = new AsyncSubject<Unit>();
        var releaseLegA = 0;
        // Whether leg A was RELEASED rather than timing out. If the wait ever timed out, leg A would
        // free the permit on its own and the "leg B is parked on the gate" premise would be false —
        // the test could then pass for the wrong reason. Asserted on the test thread below.
        var legAReleasedCleanly = 0;

        var legA = Observable.Create<int>(obs =>
        {
            legAEntered.OnNext(Unit.Default);
            legAEntered.OnCompleted();
            if (SpinWait.SpinUntil(() => Volatile.Read(ref releaseLegA) == 1, Timeout10))
                Interlocked.Exchange(ref legAReleasedCleanly, 1);
            obs.OnNext(1);
            return System.Reactive.Disposables.Disposable.Empty;
        });

        using var subA = pool.SubscribeThroughPool(legA).Subscribe(_ => { });
        try
        {
            await legAEntered.Should().Within(Timeout5)
                .Emit("leg A must be inside its subscribe, holding the only permit");

            // Leg B can never acquire the permit — it is parked on the gate when the drain cancels.
            var legBTerminated = new AsyncSubject<Unit>();
            // Written from the pool's subscribe thread, read from the test thread — Interlocked/Volatile,
            // not a plain bool: a stale read of `false` is the PASSING value here, so an unsynchronised
            // field could hide a real regression (leg B being subscribed despite the drain).
            var legBSubscribed = 0;
            using var subB = pool
                .SubscribeThroughPool(Observable.Create<int>(obs =>
                {
                    Interlocked.Exchange(ref legBSubscribed, 1);
                    obs.OnNext(2);
                    return System.Reactive.Disposables.Disposable.Empty;
                }))
                .Finally(() =>
                {
                    legBTerminated.OnNext(Unit.Default);
                    legBTerminated.OnCompleted();
                })
                .Subscribe(_ => { }, _ => { });

            Assert.True(SpinWait.SpinUntil(() => pool.CurrentInFlight == 1, Timeout5),
                "exactly leg A holds the permit; leg B must still be waiting on the gate");

            var drainDone = Task.Run(pool.Drain, TestContext.Current.CancellationToken);

            // 🚨 ESTABLISH the ordering the assertions below depend on — do not assume it.
            //
            // `Task.Run` only SCHEDULES Drain; it says nothing about whether Drain has reached its
            // `_poolCts.Cancel()`. Releasing leg A here — as this test used to — frees the only permit
            // while the cancel may still be queued behind a busy thread pool, and SemaphoreSlim then
            // legitimately hands that permit to the leg B waiter already queued on it. Leg B's
            // `ct.ThrowIfCancellationRequested()` guard passes (nothing is cancelled yet), it subscribes,
            // and the test fails on `legBSubscribed == 1` — reporting a product regression when the
            // product did exactly the right thing. Observed on a loaded CI runner (six shards on one
            // box); the assertion asserted an ordering the test never created.
            //
            // Leg B's TERMINATION is the observable proof that the cancel has happened: it is parked in
            // `_gate.WaitAsync(linked)`, so `_poolCts.Cancel()` is what completes that wait as cancelled
            // → OnCompleted → this `.Finally`. Waiting for it costs nothing when the pool is healthy and
            // is what makes "the drain cancels BEFORE the permit is granted" true by construction rather
            // than by scheduling luck. It does not deadlock: Drain blocks re-acquiring leg A's permit,
            // which is independent of leg B's cancellation continuation.
            await legBTerminated.Should().Within(Timeout5).Emit(
                "a leg the drain cancels MUST terminate (OnCompleted) so its .Finally runs — that callback "
                + "is what releases RoutingGrain's in-flight route slot and advances OrderedRouteDispatcher's "
                + "per-destination FIFO; swallowing the cancellation silently leaked both");

            Volatile.Write(ref releaseLegA, 1);
            await drainDone.WaitAsync(Timeout10, TestContext.Current.CancellationToken);

            Volatile.Read(ref legAReleasedCleanly).Should().Be(1,
                "leg A must have been RELEASED, not timed out — a timed-out wait would free the permit on "
                + "its own and leg B would no longer be parked on the gate, so the test would prove nothing");
            // 🚨 THE REGRESSION GUARD — and it is now asserted on a cancellation that PROVABLY preceded
            // the permit becoming free (see the wait above), so a pass means the drain refused leg B
            // rather than merely outrunning it. Before the termination fix this never fired at all and
            // the test hung to its timeout.
            Volatile.Read(ref legBSubscribed).Should().Be(0,
                "the drain cancels before the permit is granted, so leg B's source must never be subscribed");
        }
        finally
        {
            // 🚨 Leg A is a deliberately parked subscribe. Releasing it here means an assertion that
            // throws above cannot leave it holding a pool thread for the full Timeout10.
            Volatile.Write(ref releaseLegA, 1);
        }
    }

    /// <summary>
    /// 🚨 A leg the drain cancels AFTER its subscribe completed must ALSO terminate — issue #1789,
    /// the other half of <see cref="Drain_terminates_a_SubscribeThroughPool_leg_it_cancels"/>.
    ///
    /// <para>That test covers a leg cancelled BEFORE or DURING <c>source.Subscribe(observer)</c>:
    /// its gate wait throws <see cref="OperationCanceledException"/> and the error arm turns it into
    /// <c>OnCompleted</c>. A leg whose subscribe already RETURNED never reaches that arm. It is torn
    /// down by the drain's cancellation registration, which disposed the inner subscription and
    /// nothing else — and <b>disposing a subscription emits nothing</b>. No <c>OnCompleted</c>, no
    /// <c>OnError</c>, so the observable terminated in neither direction and every
    /// <c>.Finally(...)</c> hung off it never ran.</para>
    ///
    /// <para>That bookkeeping is <c>RoutingGrain.Dispatch</c>'s in-flight decrement and
    /// <c>OrderedRouteDispatcher.DrainNext</c>'s FIFO advance. Leaking it strands a destination's
    /// queue permanently and makes the <c>cleared after</c> line structurally impossible to emit
    /// (it requires in-flight to fall back below half the saturation threshold) — which is exactly
    /// what prod showed on 2026-08-17: two saturation Criticals ten minutes apart at identical
    /// depth, both deep inside the termination grace period, with no clear line in the window.</para>
    ///
    /// <para>Deterministic and bounded: the source's subscribe returns immediately, and the test
    /// waits for the pool's permit to be released before draining — so the leg is provably PAST the
    /// window the sibling test covers. Disposal of the inner subscription is asserted first, so a
    /// failure on termination cannot be confused with a drain that never ran.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Drain_terminates_a_SubscribeThroughPool_leg_that_already_subscribed()
    {
        using var pool = new IoPool(2);
        // Three signals the PRODUCT emits and the test consumes — nothing is parked here, so all
        // three are AsyncSubjects awaited through the assertion helpers.
        var subscribed = new AsyncSubject<Unit>();
        var innerDisposed = new AsyncSubject<Unit>();
        var terminated = new AsyncSubject<Unit>();

        // A LONG-LIVED source, the shape every real SubscribeThroughPool caller has: the subscribe
        // returns promptly (releasing the pool permit) and the subscription then stays open,
        // emitting on its own schedule. Nothing here emits — the leg is simply alive when the pool
        // drains, which is what host shutdown does to every live route.
        var source = Observable.Create<int>(obs =>
        {
            subscribed.OnNext(Unit.Default);
            subscribed.OnCompleted();
            return System.Reactive.Disposables.Disposable.Create(() =>
            {
                innerDisposed.OnNext(Unit.Default);
                innerDisposed.OnCompleted();
            });
        });

        using var sub = pool.SubscribeThroughPool(source)
            .Finally(() =>
            {
                terminated.OnNext(Unit.Default);
                terminated.OnCompleted();
            })
            .Subscribe(_ => { }, _ => { });

        await subscribed.Should().Within(Timeout5).Emit(
            "the source must have been subscribed before the drain — this test is about the window "
            + "AFTER the subscribe completed");
        Assert.True(SpinWait.SpinUntil(() => pool.CurrentInFlight == 0, Timeout5),
            "the setup leaf must have released its permit, so the leg is provably past the subscribe "
            + "window the sibling test covers (otherwise this would re-test that one)");

        var drainDone = Task.Run(pool.Drain, TestContext.Current.CancellationToken);

        await innerDisposed.Should().Within(Timeout5).Emit(
            "the drain must tear the live inner subscription down — asserted FIRST so a failure on "
            + "termination below is attributable to the missing terminal, not to a drain that never ran");
        // 🚨 THE REGRESSION GUARD. Before the fix this never fired: the drain disposed the inner
        // subscription and emitted nothing, so `.Finally` never ran and this waited out its budget.
        await terminated.Should().Within(Timeout5).Emit(
            "a leg the drain tears down AFTER its subscribe completed MUST terminate (OnCompleted) so "
            + "its .Finally runs — that callback releases RoutingGrain's in-flight route slot and "
            + "advances OrderedRouteDispatcher's per-destination FIFO; disposing the subscription "
            + "emits nothing, so without an explicit terminal both leak permanently (#1789)");

        var residual = await drainDone.WaitAsync(Timeout10, TestContext.Current.CancellationToken);
        residual.Should().Be(0,
            "a leg past its subscribe holds no gate permit, so terminating it must not change what "
            + "Drain joins or reports");
    }

    [Fact]
    public async Task Unbounded_fallback_runs_the_leaf_on_the_threadpool()
    {
        await AssertLeafRunsOffSubscriber(io => IoPool.Unbounded.Invoke(io));
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
            .Await(TestContext.Current.CancellationToken);

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
            .Await(TestContext.Current.CancellationToken);

        observed.Should().Be("owner-identity",
            "InvokeBlocking must also carry the caller's identity into the blocking body");
    }

    [Fact]
    public async Task Unbounded_fallback_carries_caller_AsyncLocal_into_the_pooled_body()
    {
        var baton = new AsyncLocal<string?> { Value = "owner-identity" };

        string? observed = null;
        await IoPool.Unbounded.Invoke(_ => { observed = baton.Value; return Task.FromResult(0); })
            .Await(TestContext.Current.CancellationToken);

        observed.Should().Be("owner-identity",
            "the Unbounded fallback must carry the caller's identity into the pooled body too");
    }

    // Subscribes from a dedicated (non-ThreadPool) thread and asserts the leaf
    // body runs on a ThreadPool thread distinct from the subscriber — i.e. the
    // pool genuinely escaped the calling scheduler. Robust by construction: a
    // dedicated Thread is never a ThreadPool thread, so checking
    // IsThreadPoolThread on the body avoids the flaky "different thread id" guess
    // (the ThreadPool can otherwise reuse an awaiting caller's thread).
    private static async Task AssertLeafRunsOffSubscriber(
        Func<Func<CancellationToken, Task<int>>, IObservable<int>> invoke)
    {
        var subscriberThread = -1;
        var bodyThread = -1;
        var bodyOnThreadPool = false;
        var done = new AsyncSubject<Unit>();

        void Done()
        {
            done.OnNext(Unit.Default);
            done.OnCompleted();
        }

        var t = new Thread(() =>
        {
            subscriberThread = Environment.CurrentManagedThreadId;
            invoke(_ =>
            {
                bodyThread = Environment.CurrentManagedThreadId;
                bodyOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
                return Task.FromResult(0);
            }).Subscribe(_ => Done(), _ => Done());
        }) { IsBackground = true };
        t.Start();

        await done.Should().Within(Timeout5).Emit("the leaf should complete");
        bodyOnThreadPool.Should().BeTrue("the leaf must run on the ThreadPool, not the subscriber's thread");
        bodyThread.Should().NotBe(subscriberThread);
    }
    /// <summary>
    /// 🚨 REPRO for #2394 — the SILENT teardown wedge.
    ///
    /// <para><see cref="CancellationTokenSource.Cancel()"/> runs every registered callback
    /// SYNCHRONOUSLY on the thread that calls it, and <see cref="IoPool.SubscribeThroughPool{T}"/>
    /// registers one per LIVE pooled subscription which performs that subscription's whole
    /// downstream teardown (<c>inner.Dispose()</c> then <c>observer.OnCompleted()</c>) — layout
    /// renders, query change feeds, routing dispatch. <see cref="IoPool.Drain"/> used to call
    /// <c>_poolCts.Cancel()</c> inline, so <c>IoPoolRegistry.DrainAll()</c> — i.e. the MESH
    /// TEARDOWN thread — executed arbitrary application teardown with nothing over it:
    /// <c>DrainTimeout</c> bounds only the gate join that comes AFTER the cancel, no watchdog
    /// covers the phase (the hub's own ends at <c>DisposalCompleted</c>, already observed), and
    /// nothing on the path logs. One teardown leg that blocked parked mesh teardown FOREVER —
    /// <c>MeshWeaver.Hosting.Monolith.Test</c> killed at its 8&#160;min wall-clock cap with no test
    /// named and not one trace line after <c>DISPOSE_INVOKED</c>.</para>
    ///
    /// <para>Deterministic: the teardown leg below simply does not return promptly, which is the
    /// only precondition the wedge ever needed.</para>
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Drain_runsPooledSubscriptionTeardown_offTheCallersThread()
    {
        using var pool = new IoPool(4);
        var source = new Subject<int>();
        var teardownEntered = new AsyncSubject<Unit>();
        var releaseTeardown = 0;
        var teardownThread = 0;

        using var subscription = pool.SubscribeThroughPool<int>(source).Subscribe(
            _ => { },
            () =>
            {
                teardownThread = Environment.CurrentManagedThreadId;
                teardownEntered.OnNext(Unit.Default);
                teardownEntered.OnCompleted();
                // The teardown leg simply does not return promptly — that is the whole
                // precondition of the wedge this test repros.
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseTeardown) == 1, Timeout10);
            });

        try
        {
            SpinWait.SpinUntil(() => source.HasObservers, Timeout5)
                .Should().BeTrue("the pooled subscribe must have completed before the drain");

            var drainThread = 0;
            var drain = Task.Run(
                () =>
                {
                    drainThread = Environment.CurrentManagedThreadId;
                    return pool.Drain();
                },
                TestContext.Current.CancellationToken);

            await teardownEntered.Should().Within(Timeout5)
                .Emit("Drain cancels the pool, which terminates every pooled subscription");

            teardownThread.Should().NotBe(drainThread,
                "a pooled subscription's downstream teardown must never run on the thread that called "
                + "Drain() — that thread is the mesh-teardown thread, and Cancel() executes registered "
                + "callbacks synchronously on whoever calls it (#2394)");

            Volatile.Write(ref releaseTeardown, 1);

            var residual = await drain.WaitAsync(Timeout10, TestContext.Current.CancellationToken);
            residual.Should().Be(0, "the teardown finished inside the budget — nothing to report");
        }
        finally
        {
            Volatile.Write(ref releaseTeardown, 1);
        }
    }

    /// <summary>
    /// The other half of #2394: <see cref="IoPool.Dispose"/> documents "DISPOSE MUST NOT BLOCK" —
    /// yet <c>_poolCts.Cancel()</c> IS a blocking call whenever a pooled subscription's teardown is
    /// slow, for the same reason as above. `using var pool = …` in an async method runs Dispose on
    /// a ThreadPool thread, so a blocking Dispose parks a pool thread while the work it is
    /// unwinding needs pool threads — the starvation deadlock the method's own comment describes.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Dispose_doesNotBlockOnASlowPooledSubscriptionTeardown()
    {
        var pool = new IoPool(4);
        var source = new Subject<int>();
        var teardownEntered = new AsyncSubject<Unit>();
        var releaseTeardown = 0;

        using var subscription = pool.SubscribeThroughPool<int>(source).Subscribe(
            _ => { },
            () =>
            {
                teardownEntered.OnNext(Unit.Default);
                teardownEntered.OnCompleted();
                // A SLOW teardown leg — the condition under which Dispose used to block.
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseTeardown) == 1, Timeout5);
            });

        try
        {
            SpinWait.SpinUntil(() => source.HasObservers, Timeout5)
                .Should().BeTrue("the pooled subscribe must have completed before disposal");

            var sw = Stopwatch.StartNew();
            pool.Dispose();
            sw.Stop();

            await teardownEntered.Should().Within(Timeout5)
                .Emit("disposal must still terminate the subscription");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
                "Dispose must return immediately — the WAIT belongs on Disposed, and running the "
                + "pooled subscriptions' teardown inline made Dispose block for as long as they took");
        }
        finally
        {
            Volatile.Write(ref releaseTeardown, 1);
        }
    }

    /// <summary>
    /// 🚨 A RESIDUAL MUST NAME ITS LEAF. A dirty teardown reporting an anonymous
    /// <c>AgentStore=1</c> tells you a pool did not drain and nothing about what to fix — measured
    /// 2026-08-30, three such teardowns in 20 loaded runs of MeshWeaver.AI.Test named three
    /// different pools and not one operation. #2480 added the pool NAME for this reason; this is
    /// the level it stopped at.
    /// </summary>
    [Fact]
    public async Task AResidualNamesTheLeafThatWouldNotUnwind()
    {
        var pool = new IoPool(2);
        var entered = new AsyncSubject<Unit>();
        var release = 0;

        // A leaf that ignores its token — exactly the defect the residual exists to report.
        pool.InvokeBlocking(_ =>
        {
            entered.OnNext(Unit.Default);
            entered.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Timeout10);
            return 0;
        }).Subscribe(_ => { }, _ => { });

        try
        {
            await entered.Should().Within(Timeout5).Emit();

            var sites = pool.PendingLeafSites;
            sites.Should().NotBeEmpty("a leaf is in flight, so the pool must be able to say WHICH");
            string.Join(" | ", sites).Should().Contain(nameof(AResidualNamesTheLeafThatWouldNotUnwind),
                "the lambda's compiler-generated name carries its ENCLOSING method — that is what turns "
                + "'AgentStore=1' into a pointer at the operation to fix");
        }
        finally
        {
            // 🚨 In a `finally`: an assertion that throws above must not leave the leaf holding a
            // pool thread for the full Timeout10, into the next test.
            Volatile.Write(ref release, 1);
            pool.Dispose();
        }
    }

    /// <summary>A pool that drains cleanly reports no sites — the diagnostic is not noise.</summary>
    [Fact]
    public async Task ACleanPool_HasNoPendingSites()
    {
        var pool = new IoPool(2);
        await pool.Invoke(_ => Task.FromResult(1)).FirstAsync().Await(TestContext.Current.CancellationToken);
        pool.PendingLeafSites.Should().BeEmpty("every leaf unwound — there is nothing to name");
        pool.Dispose();
    }
}
