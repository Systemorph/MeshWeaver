using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 The reboot watchdog lives exactly as long as the hub it reads from (MeshWeaver#7496).
///
/// <para><b>The incident.</b> On a portal pod going down, the mesh hub's service scope was disposed
/// while the watchdog's interval was still armed. The next tick's first resolve
/// (<c>Evaluate</c> → <c>ServiceProvider.GetService</c>) threw <see cref="ObjectDisposedException"/>
/// from inside the selector — outside the per-pass Catch — so the loop ended as
/// <c>[RebootWatchdog] stopped on mesh/…</c> at Error, an incident on every such stop.</para>
///
/// <para>The hub is REAL (hosted on the monolith test mesh) and so is its container. Only the
/// disposed scope is injected, at the one resolve each pass makes: from the switch on, it answers the
/// incident's <see cref="ObjectDisposedException"/> (object name
/// <see cref="HubDisposingException.DisposedContainerObjectName"/>) while the hub and its interval
/// stay alive — the order the pod saw. The watchdog's log is read through a recording
/// <see cref="ILoggerFactory"/> the hub resolves.</para>
/// </summary>
public class RebootWatchdogEndsWithItsHubTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);


    /// <summary>How many times the watchdog resolved its options — once to arm, then once per pass.</summary>
    private int resolves;

    /// <summary>Once set, every resolve answers what a disposed <c>MeshServiceProvider</c> scope answers.</summary>
    private volatile bool scopeDisposed;

    private (IMessageHub Hub, RecordingLoggerFactory Log) ArmedHub(string id, bool otherFault = false)
    {
        var armed = false;
        var log = new RecordingLoggerFactory();
        // No critical namespaces: a pass reads nothing from the mesh and is Healthy, so the only fault
        // a pass can meet is the disposed scope this test arranges.
        var options = new InstanceRebootOptions { CriticalNamespaces = ImmutableArray<string>.Empty, WatchdogInterval = Tick };
        var hub = Mesh.GetHostedHub(new Address($"test/{id}"),
            c => c.WithServices(s => s
                // Transient on purpose: every resolve is counted, so the passes are observable.
                .AddTransient(_ =>
                {
                    System.Threading.Interlocked.Increment(ref resolves);
                    // A fault that is NOT a disposed scope, on every pass after the arming resolve.
                    if (otherFault && System.Threading.Volatile.Read(ref armed))
                        throw new InvalidOperationException("injected: a pass that fails for another reason");
                    System.Threading.Volatile.Write(ref armed, true);
                    // The incident's exception, verbatim in type and object name: the hub's scope (or a
                    // parent) was disposed while the watchdog's interval was still armed.
                    return scopeDisposed
                        ? throw new ObjectDisposedException(HubDisposingException.DisposedContainerObjectName,
                            "Instances cannot be resolved and nested scopes cannot be begun from this service scope "
                            + "as it (or one of its parent scopes) has already been disposed.")
                        : options;
                })
                .AddSingleton<ILoggerFactory>(log)));
        new RebootWatchdog().Arm(hub).Subscribe();
        return (hub, log);
    }

    private IObservable<int> Passes => Observable.Interval(Tick).Select(_ => System.Threading.Volatile.Read(ref resolves));

    /// <summary>
    /// The scope goes away under the armed watchdog: the loop ENDS, said once at Debug — never the
    /// Error "stopped" line, and never a Warning per tick for a container that cannot come back. On
    /// unfixed code the first tick after the dispose logged "[RebootWatchdog] stopped" at Error.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ADisposedScope_EndsTheWatchdogQuietly_NeverAsAnError()
    {
        var (hub, log) = ArmedHub("watchdog-scope");
        // Armed only once the hub is Started; disposing before that would test a watchdog that never ran.
        await hub.RunLevelChanged.Where(level => level >= MessageHubRunLevel.Started)
            .Should().Within(10.Seconds()).Emit("the test hub starts", TestContext.Current.CancellationToken);
        await Passes.Where(n => n >= 3)
            .Should().Within(10.Seconds()).Emit("the watchdog is running passes", TestContext.Current.CancellationToken);

        // The scope goes away under the armed watchdog. Disposing the hub's provider here would dispose
        // the hub with it (and the watchdog with the hub) — the incident is the OTHER order, a scope
        // (or its parent) gone while the hub and its interval are still alive — so the resolve seam
        // answers exactly what the disposed scope answered.
        scopeDisposed = true;

        var ended = await log.Lines.Where(l => l.Message.Contains("[RebootWatchdog]"))
            .Should().Within(10.Seconds()).Emit("the next tick meets the disposed scope", TestContext.Current.CancellationToken);
        ended.Level.Should().Be(LogLevel.Debug, $"a torn-down hub ends the watchdog quietly, got: {ended.Message}");
        ended.Message.Should().Contain("ended");
        var atEnd = System.Threading.Volatile.Read(ref resolves);
        await log.Lines.Where(l => l.Level >= LogLevel.Warning)
            .Should().NotEmit(20 * Tick, "the loop has ended: no Warning per tick and no Error after it", TestContext.Current.CancellationToken);
        System.Threading.Volatile.Read(ref resolves).Should().Be(atEnd, "an ended loop takes no further pass");
    }

    /// <summary>
    /// Classified, never swallowed: any OTHER fault of a pass still warns, and the next pass runs.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnyOtherPassFault_StillWarns_AndTheLoopGoesOn()
    {
        var (hub, log) = ArmedHub("watchdog-other-fault", otherFault: true);
        await hub.RunLevelChanged.Where(level => level >= MessageHubRunLevel.Started)
            .Should().Within(10.Seconds()).Emit("the test hub starts", TestContext.Current.CancellationToken);

        var warned = await log.Lines.Where(l => l.Message.Contains("[RebootWatchdog]"))
            .Should().Within(10.Seconds()).Emit("a faulted pass is reported", TestContext.Current.CancellationToken);
        warned.Level.Should().Be(LogLevel.Warning, $"got: {warned.Message}");
        var after = System.Threading.Volatile.Read(ref resolves);
        await Passes.Where(n => n > after + 2)
            .Should().Within(10.Seconds()).Emit("the loop goes on after a faulted pass", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Negative control: with the scope intact the watchdog keeps running and logs nothing at Warning
    /// or above — so the absence asserted above is the fix, not a watchdog that never ran.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnIntactScope_KeepsTheWatchdogRunning_Silently()
    {
        var (hub, log) = ArmedHub("watchdog-intact");
        await hub.RunLevelChanged.Where(level => level >= MessageHubRunLevel.Started)
            .Should().Within(10.Seconds()).Emit("the test hub starts", TestContext.Current.CancellationToken);

        await log.Lines.Where(l => l.Message.Contains("[RebootWatchdog]"))
            .Should().NotEmit(20 * Tick, "a healthy pass logs nothing; nothing ends the loop", TestContext.Current.CancellationToken);
        log.Resolves.Should().BeGreaterThan(0, "the watchdog resolved its logger from the hub, so it was armed");
    }

    private sealed record LogLine(LogLevel Level, string Message);

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly ReplaySubject<LogLine> lines = new();
        public int Resolves;

        public IObservable<LogLine> Lines => lines.AsObservable();

        public ILogger CreateLogger(string categoryName)
        {
            System.Threading.Interlocked.Increment(ref Resolves);
            return new Recorder(lines);
        }

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Recorder(ReplaySubject<LogLine> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => lines.OnNext(new LogLine(logLevel, formatter(state, exception)));
        }
    }
}
