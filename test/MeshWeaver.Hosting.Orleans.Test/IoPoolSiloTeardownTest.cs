using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// 🚨 Pins the ORDERING that issues #1898 / #1899 broke, and the ninth member of the shutdown-race
/// family (#1573, #715, #967, #1330, #1334): <see cref="IoPoolSiloTeardown"/> must capture
/// everything its stop needs WHILE the container is alive, and resolve NOTHING once the stop runs.
///
/// <para>The stop is not guaranteed a live container. On the orderly path it has one — that is what
/// the previous "resolved lazily: the container is still alive during OnStop" comment relied on.
/// But when host STARTUP is aborted (a rollout replacing a pod that is still starting, #1897),
/// <c>Host.StartAsync</c> throws and <c>RunAsync</c>'s <c>finally</c> goes straight to
/// <c>host.DisposeAsync()</c>: no <c>StopAsync</c>, no <c>StoppedAsync</c>, and the root provider
/// is disposed while the already-started silo is still stopping. The observer then resolved from a
/// dead scope and threw <see cref="ObjectDisposedException"/> out of the one method whose whole job
/// is to make the release safe.</para>
///
/// <para>These tests use a REAL MS DI <c>ServiceProvider</c> and dispose it, because a disposed
/// DI provider throws the same <see cref="ObjectDisposedException"/> from <c>GetService</c> that
/// the service scope threw in production. Every wait is bounded — the defect class
/// here is shutdown HANGS, so an unbounded wait would fail the way the bug does.</para>
/// </summary>
public class IoPoolSiloTeardownTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private sealed record Entry(LogLevel Level, string Message);

    /// <summary>Captures the teardown's report lines, which are the only externally visible
    /// evidence of what the drain actually did.</summary>
    private sealed class CapturingLogger : ILogger<IoPoolSiloTeardown>
    {
        private readonly List<Entry> entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get { lock (entries) return entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        /// <summary>Runs after each entry is recorded, outside the lock — a test's hook into the
        /// moment a given line is written.</summary>
        public Action<LogLevel, string>? OnLog { get; set; }

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (entries) entries.Add(new Entry(logLevel, message));
            OnLog?.Invoke(logLevel, message);
        }
    }

    /// <summary>Counts resolutions so "the stop resolves NOTHING" can be asserted as itself,
    /// rather than only through the exception a disposed scope happens to throw.</summary>
    private sealed class CountingProvider(IServiceProvider inner) : IServiceProvider
    {
        private int resolutions;

        public int Resolutions => Volatile.Read(ref resolutions);

        public object? GetService(Type serviceType)
        {
            Interlocked.Increment(ref resolutions);
            return inner.GetService(serviceType);
        }
    }

    private static (IoPoolRegistry Registry, Microsoft.Extensions.DependencyInjection.ServiceProvider Provider) NewContainer()
    {
        // The registry is created OUTSIDE the container and registered as an INSTANCE, so MS DI
        // never owns it (it disposes only what it creates — an ImplementationInstance resolves
        // through a ConstantCallSite, which is not captured as a disposable). Disposing the
        // provider therefore leaves the registry alive, which is what makes these tests about the
        // teardown's ordering rather than about container disposal. Asserted, not assumed, below.
        var registry = new IoPoolRegistry();
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        return (registry, services.BuildServiceProvider());
    }

    /// <summary>
    /// 🚨 THE REGRESSION PIN (#1898, #1899). The container is disposed before the stop runs — the
    /// aborted-startup path — and the terminal drain must still happen: cancel the pooled leaf,
    /// JOIN it, and report the outcome. Against the previous shape this throws
    /// <see cref="ObjectDisposedException"/> at the first line of <c>OnStop</c>, so nothing is
    /// drained and nothing is logged.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Stop_JoinsPooledIo_EvenAfterTheContainerHasBeenDisposed()
    {
        var (registry, provider) = NewContainer();
        var logger = new CapturingLogger();
        var observer = (ILifecycleObserver)new IoPoolSiloTeardown(provider, logger);

        await observer.OnStart(TestContext.Current.CancellationToken);

        // One pooled leaf in flight that ends ONLY when it is cancelled — the shape the drain
        // exists for (a live change-feed leaf never completes on its own, so a wait-without-cancel
        // would burn the budget and then release over live work).
        // 🚨 No hand-woven gate: `entered` travels leaf → test, so it is an AsyncSubject the leaf
        // completes and the test awaits reactively; `unwound` is never WAITED on — it is read as
        // state after OnStop returns — so a volatile flag says exactly that and nothing more.
        var entered = new AsyncSubject<Unit>();
        var unwound = 0;
        using var leaf = registry.Get(IoPoolNames.FileSystem)
            .InvokeBlocking(ct =>
            {
                entered.OnNext(Unit.Default);
                entered.OnCompleted();
                ct.WaitHandle.WaitOne(Budget);
                Volatile.Write(ref unwound, 1);
                return 0;
            })
            .Subscribe(_ => { }, _ => { });

        await entered.Should().Within(Budget).Emit(
            "precondition: the pooled leaf must actually be running before the drain is asked to join it");

        // 🚨 The race, reproduced: host startup was aborted, so the root provider was disposed
        // WITHOUT any ordered shutdown, while the silo is still stopping behind it.
        await provider.DisposeAsync();

        registry.TotalInFlight.Should().Be(1,
            "precondition: MS DI must not have disposed a registry it did not create — otherwise "
            + "this test would be measuring container disposal instead of the teardown");

        Func<Task> stop = () => observer.OnStop(TestContext.Current.CancellationToken);
        await stop.Should().NotThrowAsync();

        Volatile.Read(ref unwound).Should().Be(1,
            "OnStop must not RETURN until the pooled leaf has actually unwound — the join is the "
            + "whole point, and returning over live work is the use-after-unload SIGSEGV (#613)");
        logger.Entries.Should().Contain(
            e => e.Message.Contains("draining pooled I/O", StringComparison.Ordinal),
            "the stop must reach the registry it captured at OnStart, with no DI resolution of its own");
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Information
                 && e.Message.Contains("pooled I/O joined", StringComparison.Ordinal),
            "a clean join must be REPORTED — a silent return is indistinguishable from a drain that never ran");
    }

    /// <summary>
    /// The ordering stated as itself: the only service resolution happens at start. Reading the
    /// count instead of the exception means this still fails if someone reintroduces a resolve in
    /// the stop and wraps it in a <c>catch</c> — which would make the race invisible rather than
    /// absent.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TheOnlyServiceResolution_HappensAtStart_NeverAtStop()
    {
        var (_, provider) = NewContainer();
        await using var _provider = provider;
        var counting = new CountingProvider(provider);
        var observer = (ILifecycleObserver)new IoPoolSiloTeardown(counting, new CapturingLogger());

        await observer.OnStart(TestContext.Current.CancellationToken);
        var afterStart = counting.Resolutions;
        afterStart.Should().BeGreaterThan(0,
            "the stop's dependencies are captured while the scope is provably alive");

        await observer.OnStop(TestContext.Current.CancellationToken);

        counting.Resolutions.Should().Be(afterStart,
            "OnStop must resolve NOTHING: on an aborted startup the container is already disposed "
            + "by the time it runs, and a catch around the resolve would hide the race instead of "
            + "removing it");
    }

    /// <summary>
    /// 🚨 A teardown that could not run must SAY so. Orleans only stops observers whose start
    /// completed, so this is not expected — but shutdown must never swallow its own failure: this
    /// error line is the only attribution a later use-after-unload crash would get.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Stop_WithoutStart_ReportsThatPooledIoWasNotDrained()
    {
        var (_, provider) = NewContainer();
        var logger = new CapturingLogger();
        var observer = (ILifecycleObserver)new IoPoolSiloTeardown(provider, logger);

        // No OnStart, and the container is already gone — the aborted-startup shape.
        await provider.DisposeAsync();

        Func<Task> stop = () => observer.OnStop(TestContext.Current.CancellationToken);
        await stop.Should().NotThrowAsync();

        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Error
                 && e.Message.Contains("without OnStart", StringComparison.Ordinal),
            "an unrun teardown must be reported as an error, not returned like a clean join");
        logger.Entries.Should().NotContain(
            e => e.Message.Contains("pooled I/O joined", StringComparison.Ordinal),
            "nothing was joined, so nothing may claim it was");
    }

    /// <summary>
    /// 🚨 THE REGRESSION PIN FOR #2480: when the join budget EXPIRES, the report must name the pool
    /// that is still holding a leaf AND where that leaf is.
    ///
    /// <para>This is the only path the production fault ever takes, and it named nothing for three
    /// weeks and 18 occurrences. The per-pool attribution added for #2480 is subscribed to each
    /// pool's own <c>Disposed</c> — and <c>IoPool.TryFinishDisposal</c>'s own remarks say a pool
    /// whose leaf never unwinds never fires it. So the residual attribution was published on the
    /// SUCCESS signal: the failing case was precisely the case that could not report. Meanwhile
    /// <c>IoPoolRegistry.Dispose</c> clears <c>_pools</c>, so by expiry there was nothing left for
    /// the caller to enumerate either.</para>
    ///
    /// <para>Against the previous shape the assertions below fail on a message that is the bare
    /// sentence with no pool and no site — exactly the 18 production lines.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task WhenTheJoinBudgetExpires_TheReportNamesThePoolAndTheLeaf()
    {
        // 🚨 The budget is shrunk ONLY so the expiry path is reachable at all: at the production
        // 30 s no test can observe this report under xunit's 30 s methodTimeout, which is how its
        // wording went unchecked. The default is unchanged — this is the same reason DrainTimeout
        // and DrainGrace are settable.
        var registry = new IoPoolRegistry(new IoPoolOptions
        {
            SiloJoinBudget = TimeSpan.FromMilliseconds(300)
        });
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        await using var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger();
        var observer = (ILifecycleObserver)new IoPoolSiloTeardown(provider, logger);
        await observer.OnStart(TestContext.Current.CancellationToken);

        // A leaf that IGNORES its cancellation token — the defect this issue is named for. `entered`
        // travels leaf → test, so it is an AsyncSubject the leaf completes and the test awaits
        // reactively; `release` travels test → the deliberately parked leaf, so it is a volatile int
        // under a bounded SpinUntil, written in a finally so a failed assertion cannot strand it.
        var entered = new AsyncSubject<Unit>();
        var release = 0;
        using var leaf = registry.Get(IoPoolNames.Query)
            .InvokeBlocking(ct =>
            {
                entered.OnNext(Unit.Default);
                entered.OnCompleted();
                // `ct` is deliberately never observed: that IS the fault under test.
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, Budget);
                return 0;
            })
            .Subscribe(_ => { }, _ => { });

        try
        {
            await entered.Should().Within(Budget).Emit(
                "precondition: the leaf must be running before the join is asked to wait for it");

            await observer.OnStop(TestContext.Current.CancellationToken);

            var expiry = logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Error
                     && e.Message.Contains("did not finish within", StringComparison.Ordinal),
                "the expiry must be reported as an error — the silo is releasing over live work")
                .Subject;

            expiry.Message.Should().Contain(IoPoolNames.Query,
                "the report must NAME the pool that is still holding a leaf. Before this fix the only "
                + "attribution rode each pool's Disposed, which a pool whose leaf never unwinds never "
                + "fires — so the one path this fault takes named nothing at all (#2480)");
            expiry.Message.Should().Contain(
                nameof(WhenTheJoinBudgetExpires_TheReportNamesThePoolAndTheLeaf),
                "and it must name WHERE the leaf is: a lambda's compiler-generated method name "
                + "carries its enclosing method, which is the pointer 'fix the leaf' needs");
            expiry.Message.Should().MatchRegex(@" — in flight \d+(\.\d+)? s, opened by .+, started on thread \d+",
                "per leaf the report also says how long it has been in flight, who opened it and the "
                + "thread it started on — a site shared by every caller named nothing more (#7277)");
            expiry.Message.Should().NotContain("every pool reported",
                "a pool IS still holding a leaf, so the report must not claim the opposite finding");
        }
        finally
        {
            // In a finally so a failing assertion above cannot leave the pool thread parked for the
            // whole Budget and take the shard's teardown with it.
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// 🚨 #7277: a SYNCHRONOUS leaf that outlives the join — a <c>SubscribeThroughPool</c> subscribe
    /// that blocks, the shape of the production <c>Query=1</c> — is reported with the STACK of the
    /// thread it holds, so the report names the frame it is stuck in. The expiry's site alone names
    /// only the source observable's type, which one query fan-out shares with every query in the
    /// process. On unfixed code no such Warning exists and the assertion below finds nothing.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task WhenTheJoinBudgetExpires_ABlockedSubscribeIsReportedWithTheFrameItIsStuckIn()
    {
        var registry = new IoPoolRegistry(new IoPoolOptions { SiloJoinBudget = TimeSpan.FromMilliseconds(300) });
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        await using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger();
        var observer = (ILifecycleObserver)new IoPoolSiloTeardown(provider, logger);
        await observer.OnStart(TestContext.Current.CancellationToken);

        // The subscribe itself blocks — no token reaches it, exactly like the MeshQuery fan-out's.
        var entered = new AsyncSubject<Unit>();
        var release = 0;
        var source = Observable.Create<int>(_ =>
        {
            ParkedInsideTheSubscribe(entered, () => Volatile.Read(ref release) == 1);
            return Disposable.Empty;
        });
        using var leaf = registry.Get(IoPoolNames.Query).SubscribeThroughPool(source).Subscribe(_ => { }, _ => { });

        try
        {
            await entered.Should().Within(Budget).Emit(
                "precondition: the subscribe must be running before the join is asked to wait for it");

            await observer.OnStop(TestContext.Current.CancellationToken);

            var stack = logger.Entries.Should().ContainSingle(
                    e => e.Level == LogLevel.Warning && e.Message.Contains("still holds managed thread", StringComparison.Ordinal),
                    "the leaf that outlived the join is reported ONCE with the stack of the thread it holds — "
                    + "the reading #7277 could not take from the expiry line")
                .Subject.Message;
            stack.Should().Contain(IoPoolNames.Query, "the warning names the leaf's pool")
                .And.Contain(nameof(ParkedInsideTheSubscribe),
                    "and the frame the subscribe is stuck in — the top of its thread's stack, which is what "
                    + "'fix the leaf' needs and what the site alone cannot give")
                .And.NotContain("could not be read");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// 🚨 #7277, the <c>Query=0</c> shape: the silo stop's <c>Dispose()</c> hands the pool token's
    /// cancel to the pool's canceller thread, and that cancel runs every pooled subscription's
    /// downstream teardown inline. One teardown callback that parks keeps the cancel from returning,
    /// so disposal never completes — while no LEAF is in flight. Before this fix the expiry then read
    /// <c>Did NOT report: Query=0.</c>, naming nothing (three production occurrences): the cancel's
    /// label was set only by <c>Drain()</c>'s own join, which the Dispose path never runs. The report
    /// must name the parked cancel AND carry the canceller thread's stack, whose top is the parked frame.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task WhenTheJoinExpiresOverAParkedCancel_TheReportNamesTheCancelAndTheFrameItIsParkedIn()
    {
        var registry = new IoPoolRegistry(new IoPoolOptions { SiloJoinBudget = TimeSpan.FromMilliseconds(300) });
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        await using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger();
        var observer = (ILifecycleObserver)new IoPoolSiloTeardown(provider, logger);
        await observer.OnStart(TestContext.Current.CancellationToken);

        // A live pooled subscription whose subscribe RETURNS (so no leaf is in flight) and whose
        // OnCompleted — run by the pool's cancel — parks. `subscribed` travels leaf → test; `release`
        // travels test → the deliberately parked callback, written in the finally.
        var subscribed = new AsyncSubject<Unit>();
        var release = 0;
        using var subscription = registry.Get(IoPoolNames.Query)
            .SubscribeThroughPool(Observable.Create<int>(_ =>
            {
                subscribed.OnNext(Unit.Default);
                subscribed.OnCompleted();
                return Disposable.Empty;
            }))
            .Subscribe(_ => { }, _ => { }, () => ParkedInsideATeardownCallback(() => Volatile.Read(ref release) == 1));

        try
        {
            await subscribed.Should().Within(Budget).Emit(
                "precondition: the pooled subscription is live, so the cancel has a callback to run");
            // `subscribed` fires INSIDE source.Subscribe, before the setup leaf's LeaveLeaf/ReleaseGate
            // decrement the in-flight count — so the leaf is still counted at that instant. Wait for the
            // setup leaf to finish rather than reading the count off the subscribe-entry signal.
            SpinWait.SpinUntil(() => registry.TotalInFlight == 0, Budget).Should().BeTrue(
                "precondition: the subscribe returned, so no LEAF is in flight — this is the Query=0 shape");

            await observer.OnStop(TestContext.Current.CancellationToken);

            var expiry = logger.Entries.Should().ContainSingle(
                    e => e.Level == LogLevel.Error && e.Message.Contains("did not finish within", StringComparison.Ordinal),
                    "the parked cancel holds disposal back, so the join expires")
                .Subject.Message;
            expiry.Should().NotContain($"{IoPoolNames.Query}=0.",
                "the production line named nothing: a residual must never read as an anonymous zero")
                .And.Contain($"{IoPoolNames.Query}=1 [",
                    "the parked cancel is one unit of work the pool has not let go of, and it is named")
                .And.Contain("cancel had not returned",
                    "the name says the pool token's cancel is still running a teardown callback");

            logger.Entries.Should().ContainSingle(
                    e => e.Level == LogLevel.Warning && e.Message.Contains("still holds managed thread", StringComparison.Ordinal),
                    "the canceller's thread is read like any synchronous leaf's")
                .Subject.Message.Should().Contain("IoPool-cancel")
                .And.Contain(nameof(ParkedInsideATeardownCallback),
                    "the top of the canceller's stack is the callback it is parked in — the frame to fix")
                .And.NotContain("could not be read");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>The teardown frame the report must name. Not inlined, so it is a frame of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ParkedInsideATeardownCallback(Func<bool> released) =>
        SpinWait.SpinUntil(released, Budget);

    /// <summary>
    /// 🚨 A leaf that RETURNS between the expiry's snapshot and the dump hands its thread back, and the
    /// stack read from that thread is then whatever it ran next (review of #7584). The thread id cannot
    /// tell the two apart; the leaf's ticket can. Here the leaf is released the moment the expiry is
    /// logged — after the snapshot, before the capture — and the report must say it returned rather
    /// than hand it a stack that is not its own.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ALeafThatReturnsBeforeTheDump_IsNotGivenTheStackOfWhatItsThreadRanNext()
    {
        var registry = new IoPoolRegistry(new IoPoolOptions { SiloJoinBudget = TimeSpan.FromMilliseconds(300) });
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        await using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger();
        var observer = (ILifecycleObserver)new IoPoolSiloTeardown(provider, logger);
        await observer.OnStart(TestContext.Current.CancellationToken);

        var entered = new AsyncSubject<Unit>();
        var release = 0;
        var source = Observable.Create<int>(_ =>
        {
            ParkedInsideTheSubscribe(entered, () => Volatile.Read(ref release) == 1);
            return Disposable.Empty;
        });
        using var leaf = registry.Get(IoPoolNames.Query).SubscribeThroughPool(source).Subscribe(_ => { }, _ => { });

        // The expiry line is written after the snapshot of the residual leaves and before the capture:
        // release the leaf there and let it retire, so the capture reads a thread the leaf has left.
        logger.OnLog = (level, message) =>
        {
            if (level != LogLevel.Error || !message.Contains("did not finish within", StringComparison.Ordinal))
                return;
            Volatile.Write(ref release, 1);
            SpinWait.SpinUntil(
                () => registry.UnreportedResiduals().All(r => r.SynchronousLeaves.Count == 0), Budget);
        };

        try
        {
            await entered.Should().Within(Budget).Emit(
                "precondition: the subscribe must be running before the join is asked to wait for it");

            await observer.OnStop(TestContext.Current.CancellationToken);

            logger.Entries.Should().NotContain(
                e => e.Message.Contains("still holds managed thread", StringComparison.Ordinal),
                "a leaf that returned before the dump must not be reported with its former thread's stack");
            logger.Entries.Should().ContainSingle(
                e => e.Level == LogLevel.Warning && e.Message.Contains("returned before its stack could be read", StringComparison.Ordinal),
                "it is reported as having returned — named, never silently dropped");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// 🚨 A host with no local DAC REFUSES the capture rather than letting ClrMD resolve one elsewhere —
    /// that copy would not be pinned, and its unload crashes every thread that ran it (review of #7584).
    /// </summary>
    [Fact]
    public void ACaptureWithoutTheLocalDac_IsRefusedBeforeAnythingIsDumped()
    {
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"no-dac-{Guid.NewGuid():N}", "libmscordaccore.so");
        var refused = Record.Exception(() =>
            new BlockedThreadStacks(missing).Capture([Environment.CurrentManagedThreadId]));
        refused.Should().BeOfType<InvalidOperationException>();
        refused!.Message.Should().Contain(missing, "the refusal names the DAC it looked for");
    }

    /// <summary>
    /// 🚨 The ticket check holds on the FAILED-capture path too. A capture that throws (here: no DAC to
    /// pin) used to report every residual leaf as "still holds managed thread … could not be read" —
    /// including one that had already returned, which is the same misattribution the ticket exists to
    /// prevent (measured on CI, where a Linux dump's DAC mismatch failed the capture).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task WhenTheCaptureFails_ALeafThatReturnedIsStillNotReportedAsHoldingItsThread()
    {
        var (messages, release) = await StopWithAFailingCapture(releaseAtExpiry: true, TestContext.Current.CancellationToken);
        try
        {
            messages.Should().NotContain(
                m => m.Contains("still holds managed thread", StringComparison.Ordinal),
                "a leaf that returned holds nothing, whether or not the capture worked");
            messages.Should().ContainSingle(
                m => m.Contains("returned before its stack could be read", StringComparison.Ordinal),
                "it is reported as having returned — named, never silently dropped");
        }
        finally
        {
            release();
        }
    }

    /// <summary>
    /// NEGATIVE CONTROL for the test above: with the same failing capture, a leaf that is STILL parked
    /// is reported as holding its thread, with the reason its stack could not be read.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task WhenTheCaptureFails_ALeafStillParkedIsReportedWithWhyItsStackIsMissing()
    {
        var (messages, release) = await StopWithAFailingCapture(releaseAtExpiry: false, TestContext.Current.CancellationToken);
        try
        {
            messages.Should().ContainSingle(
                m => m.Contains("still holds managed thread", StringComparison.Ordinal)
                     && m.Contains("could not be read", StringComparison.Ordinal),
                "a leaf still parked is reported, and the failed capture says why its stack is missing");
        }
        finally
        {
            release();
        }
    }

    private static async Task<(IReadOnlyList<string> Messages, Action Release)> StopWithAFailingCapture(bool releaseAtExpiry, CancellationToken ct)
    {
        var registry = new IoPoolRegistry(new IoPoolOptions { SiloJoinBudget = TimeSpan.FromMilliseconds(300) });
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger();
        var noDac = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"no-dac-{Guid.NewGuid():N}", "libmscordaccore.so");
        var observer = (ILifecycleObserver)new IoPoolSiloTeardown(provider, logger) { Stacks = new BlockedThreadStacks(noDac) };
        await observer.OnStart(ct);

        var entered = new AsyncSubject<Unit>();
        var release = 0;
        var source = Observable.Create<int>(_ =>
        {
            ParkedInsideTheSubscribe(entered, () => Volatile.Read(ref release) == 1);
            return Disposable.Empty;
        });
        var leaf = registry.Get(IoPoolNames.Query).SubscribeThroughPool(source).Subscribe(_ => { }, _ => { });
        void Release()
        {
            Volatile.Write(ref release, 1);
            leaf.Dispose();
            provider.Dispose();
        }

        if (releaseAtExpiry)
            logger.OnLog = (level, message) =>
            {
                if (level != LogLevel.Error || !message.Contains("did not finish within", StringComparison.Ordinal))
                    return;
                Volatile.Write(ref release, 1);
                SpinWait.SpinUntil(
                    () => registry.UnreportedResiduals().All(r => r.SynchronousLeaves.Count == 0), Budget);
            };

        try
        {
            await entered.Should().Within(Budget).Emit(
                "precondition: the subscribe must be running before the join is asked to wait for it");
            await observer.OnStop(ct);
        }
        catch
        {
            Release();
            throw;
        }
        return (logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray(), Release);
    }

    /// <summary>The blocking frame the report must name. Not inlined, so it is a frame of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ParkedInsideTheSubscribe(AsyncSubject<Unit> entered, Func<bool> released)
    {
        entered.OnNext(Unit.Default);
        entered.OnCompleted();
        SpinWait.SpinUntil(released, Budget);
    }
}
