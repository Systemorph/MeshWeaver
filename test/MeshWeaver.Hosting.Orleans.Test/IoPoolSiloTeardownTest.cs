using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
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
/// Autofac's <c>LifetimeScope</c> threw in production. Every wait is bounded — the defect class
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

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries) entries.Add(new Entry(logLevel, formatter(state, exception)));
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
}
