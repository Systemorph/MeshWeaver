using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using Memex.Portal.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>An instrument nobody arms is the same silence one step later.</b> Everything here drives the
/// REAL <see cref="ServiceDefaults.AddServiceDefaults"/> composition over a real host and waits for
/// the heartbeat's own lines to arrive on a real logger — for the reason
/// <see cref="HealthCensusTest"/> states: a restatement of the wiring would agree with itself while
/// the deployment published nothing.
///
/// <para>The reading this exists for is MeshWeaver#4234. A portal logged nothing for the final 90 s
/// of an e2e shard; the page held <c>"Subscribing to {path}…"</c> and the report read the silence as
/// a layout subscription that never completed. The run's own Playwright trace then showed six
/// ordinary static-file GETs and the Blazor reconnect's <c>POST /_blazor/negotiate</c> hanging in the
/// same window — nothing mesh-shaped at all — so the failing unit was the whole HTTP server. Inside
/// the process, nothing could say which. This line can.</para>
/// </summary>
public class ProcessLivenessHeartbeatTest
{
    /// <summary>
    /// The cadence under test — a DRIVER INPUT, not a wait bound (every wait below is
    /// <see cref="TestTimeouts"/>). Short so two ticks land promptly; the production default is
    /// <see cref="ProcessLiveness.DefaultPeriod"/>.
    /// </summary>
    private const string FastCadenceSeconds = "0.05";

    [Fact]
    public async Task AddServiceDefaults_ArmsTheHeartbeat_AndItKeepsTicking()
    {
        var lines = new ReplaySubject<string>();
        using var host = Build(FastCadenceSeconds, lines);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var beats = lines.Where(l => l.Contains("[LIVENESS] tick=", StringComparison.Ordinal));

        var first = await beats.Take(1).Should().Within(TestTimeouts.Quick)
            .Emit("AddServiceDefaults must ARM the process-liveness heartbeat. No [LIVENESS] line "
                  + "at all means the host publishes nothing, and a silent window in its log can "
                  + "then never be told apart from a suspended process (#4234).");
        first.Should().Contain("gap=first");

        var second = await beats.Skip(1).Take(1).Should().Within(TestTimeouts.Quick)
            .Emit("the heartbeat stopped after one line. A single tick proves the thread started, "
                  + "not that it keeps running — and it is the CONTINUING line whose absence is the "
                  + "reading.");

        // The second tick is the first one that can carry a gap, and the gap is the field that
        // makes a resumed stop self-reporting.
        second.Should().Contain("gap=");
        second.Should().NotContain("gap=first");
        second.Should().Contain("gen2=");
        second.Should().Contain("gcPause=");
        second.Should().Contain("poolPending=");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 🚨 The negative half, and it is not merely "off means off": a host with the heartbeat
    /// switched off must SAY SO, so that "configured off" and "this build has no heartbeat" are
    /// different bytes in the log. Same rule as the <c>/health</c> census tag one layer up.
    /// </summary>
    [Fact]
    public async Task WithTheCadenceSetToZero_NothingTicks_ButTheLogSaysItIsOff()
    {
        var lines = new ReplaySubject<string>();
        using var host = Build("0", lines);
        await host.StartAsync(TestContext.Current.CancellationToken);

        await lines.Where(l => l.Contains("[LIVENESS] disabled", StringComparison.Ordinal))
            .Take(1).Should().Within(TestTimeouts.Quick)
            .Emit("a heartbeat switched off must announce it — otherwise its absence is "
                  + "indistinguishable from a build that never had one.");

        await lines.Where(l => l.Contains("[LIVENESS] tick=", StringComparison.Ordinal))
            .Should().NotEmit(TestTimeouts.Quick / 10,
                "the cadence was set to 0, so no tick may be published.");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IHost Build(string cadenceSeconds, IObserver<string> sink)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [ProcessLiveness.PeriodSecondsConfigKey] = cadenceSeconds,
                // 🚨 INFORMATION, deliberately not Debug or Trace. test/appsettings.json pins
                // `MeshWeaver: Warning` for the whole tree, so without this the heartbeat's lines
                // are filtered and every assertion below would fail for the wrong reason. Setting
                // it to Information rather than lower ALSO pins the contract: a heartbeat quietly
                // demoted to Debug would stop appearing here — and would stop appearing in the
                // portal logs the reading is taken from, which run MeshWeaver.* at Information.
                ["Logging:LogLevel:MeshWeaver.Hosting"] = "Information",
            });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new CapturingLoggerProvider(sink));
        builder.AddServiceDefaults();
        return builder.Build();
    }

    /// <summary>Collects formatted log messages onto an observer, from whichever thread wrote them.</summary>
    private sealed class CapturingLoggerProvider(IObserver<string> sink) : ILoggerProvider
    {
        private readonly object gate = new();

        public ILogger CreateLogger(string categoryName) => new Collector(sink, gate);

        public void Dispose() { }

        private sealed class Collector(IObserver<string> sink, object gate) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                lock (gate)
                    sink.OnNext(message);
            }
        }
    }
}
