using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The first-full-frame diagnostic (<see cref="FirstFrameTiming"/>, Systemorph/MeshWeaver.Plugins#4399):
/// an area open whose first full frame takes longer than <c>Layout:SlowFirstFrameThreshold</c> writes
/// EXACTLY ONE <c>Information</c> line naming the area and the owner's hops (base frame, compose,
/// delivery); a fast open writes NONE at <c>Information</c> (negative control), so a healthy page
/// costs Loki nothing; and a later re-render of the slow area writes no second line (one latch).
/// </summary>
public class SlowFirstFrameIsLoggedOnceTest : HubTestBase
{
    private const string SlowArea = "SlowFirstFrame";
    private const string FastArea = "FastFirstFrame";

    /// <summary>How long the slow view takes to produce its first control — the slow generator IS the subject.
    /// Well past the DEFAULT threshold (<see cref="FirstFrameTiming.DefaultThreshold"/>, no key configured).</summary>
    private static readonly TimeSpan SlowCompose = FirstFrameTiming.DefaultThreshold + TimeSpan.FromMilliseconds(600);

    /// <summary>Every log record. INSTANCE state, never static (NoStaticState).</summary>
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> logRecords = new();

    /// <inheritdoc />
    public SlowFirstFrameIsLoggedOnceTest(ITestOutputHelper output) : base(output)
    {
        // Its OWN provider with its own filter rule: the shared test sinks and the shared appsettings
        // ("MeshWeaver": "Warning") would otherwise cut the Debug and Information lines under test
        // before they reach it (TestLogLevel).
        Services.AddLogging(logging =>
        {
            logging.Services.AddSingleton<ILoggerProvider>(new QueueLoggerProvider(logRecords));
            logging.AddFilter<QueueLoggerProvider>(null, LogLevel.Trace);
        });
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout
                // A first control after SlowCompose, then a re-render: the second must not log again.
                .WithView(SlowArea, (_, _) => Observable.Timer(SlowCompose)
                    .Select(_ => (UiControl?)Controls.Markdown("slow-1"))
                    .Concat(Observable.Return<UiControl?>(Controls.Markdown("slow-2"))))
                .WithView(FastArea, Controls.Markdown("fast")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private async Task Render(string area, string text)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));
        await stream.Select(ci => ci.Value.GetRawText()).Should().Within(20.Seconds()).Match(
            raw => raw.Contains(text, StringComparison.Ordinal),
            $"the {area} area renders",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private (LogLevel Level, string Message)[] FirstFrameLines(string area)
        => logRecords.Where(r => r.Message.Contains("FIRST FULL FRAME", StringComparison.Ordinal)
                                 && r.Message.Contains($"'{area}'", StringComparison.Ordinal)).ToArray();

    [HubFact]
    public async Task ASlowFirstFrame_LogsExactlyOneInformationLine_WithTheThreeHops()
    {
        await Render(SlowArea, "slow-2");

        var lines = FirstFrameLines(SlowArea);
        lines.Should().ContainSingle("one latch: the first full frame is reported once, the re-render is not");
        var (level, message) = lines.Single();
        level.Should().Be(LogLevel.Information, "the first full frame took longer than the configured threshold");
        message.Should().Contain("base frame at", "the line names the base-frame hop");
        message.Should().Contain("first control composed at", "the line names the compose hop");
        message.Should().Contain("update queue applied it", "the line names the delivery hop");
        message.Should().NotContain("composed at -1 ms", "this area composed through the render pipeline, so the compose hop was passed");
    }

    [HubFact]
    public async Task NegativeControl_AFastFirstFrame_LogsNothingAtInformation()
    {
        await Render(FastArea, "fast");

        var lines = FirstFrameLines(FastArea);
        lines.Should().ContainSingle("the fast open is still reported, once, at Debug");
        lines.Single().Level.Should().Be(LogLevel.Debug,
            "a healthy open writes nothing at Information, so it costs Loki nothing");
    }

    /// <summary>Captures every record into the owning test's instance queue.</summary>
    internal sealed class QueueLoggerProvider(ConcurrentQueue<(LogLevel, string)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(sink);
        public void Dispose() { }

        private sealed class QueueLogger(ConcurrentQueue<(LogLevel, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
