using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
    private const string FailingArea = "FailingFirstFrame";
    private const string FailureText = "first-frame-failure-4399";

    /// <summary>How long the slow view takes to produce its first control — the slow generator IS the subject.
    /// Well past the DEFAULT threshold (<see cref="FirstFrameTiming.DefaultThreshold"/>, no key configured).</summary>
    private static readonly TimeSpan SlowCompose = FirstFrameTiming.DefaultThreshold + TimeSpan.FromMilliseconds(600);

    /// <summary>Every log record. INSTANCE state, never static (NoStaticState).</summary>
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> logRecords = new();

    /// <summary>Every log record as a signal, raised AFTER it was enqueued: the line is written by the
    /// stream's applied callback, which runs after the frame was already published, so seeing the frame
    /// says nothing about the line. Tests wait on this instead.</summary>
    private readonly ReplaySubject<(LogLevel Level, string Message)> logged = new();

    /// <inheritdoc />
    public SlowFirstFrameIsLoggedOnceTest(ITestOutputHelper output) : base(output)
    {
        // Its OWN provider with its own filter rule: the shared test sinks and the shared appsettings
        // ("MeshWeaver": "Warning") would otherwise cut the Debug and Information lines under test
        // before they reach it (TestLogLevel).
        Services.AddLogging(logging =>
        {
            logging.Services.AddSingleton<ILoggerProvider>(new QueueLoggerProvider(logRecords, Subject.Synchronize(logged)));
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
                .WithView(FastArea, Controls.Markdown("fast"))
                // A data feed faults while the view's first control is still pending: the error frame,
                // published through the error path's own update, is the first thing the reader sees.
                .WithView(FailingArea, (host, _) =>
                {
                    // The production shape: the fault arrives AFTER the subscription is live, on
                    // another thread (FailFeed → SurfaceRenderError), not during Subscribe.
                    host.SubscribeToDataStream("feed", Observable.Timer(TimeSpan.FromMilliseconds(50))
                        .SelectMany(_ => Observable.Throw<int>(new InvalidOperationException(FailureText))));
                    return Observable.Never<UiControl?>();
                }));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private async Task<ISynchronizationStream<JsonElement>> Render(string area, string text)
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(area));
        await stream.Select(ci => ci.Value.GetRawText()).Should().Within(20.Seconds()).Match(
            raw => raw.Contains(text, StringComparison.Ordinal),
            $"the {area} area renders",
            cancellationToken: TestContext.Current.CancellationToken);
        return stream;
    }

    private static bool IsFirstFrameLine((LogLevel Level, string Message) r, string area)
        => r.Message.Contains("FIRST FULL FRAME", StringComparison.Ordinal)
           && r.Message.Contains($"'{area}'", StringComparison.Ordinal);

    /// <summary>The first-full-frame line for <paramref name="area"/>, once the sink has it.</summary>
    private Task<(LogLevel Level, string Message)> FirstFrameLine(string area)
        => logged.Should().Within(20.Seconds()).Match(r => IsFirstFrameLine(r, area),
            $"the first full frame of {area} is reported", TestContext.Current.CancellationToken);

    private (LogLevel Level, string Message)[] FirstFrameLines(string area)
        => logRecords.Where(r => IsFirstFrameLine(r, area)).ToArray();

    /// <summary>
    /// A round trip through the owner stream's hub AFTER everything it has queued: a click on an area
    /// that has no control is answered (refused) by that hub, which handles updates serially — so once
    /// the answer is back, every earlier update's applied callback, and so its log line, has run.
    /// </summary>
    private static async Task Settle(ISynchronizationStream<JsonElement> stream)
    {
        var answered = new AsyncSubject<bool>();
        stream.SubmitUserAction(
            new ClickedEvent("settle/NotRendered", stream.StreamId),
            actingUser: null,
            onRefused: _ => { answered.OnNext(true); answered.OnCompleted(); },
            onAccepted: () => { answered.OnNext(true); answered.OnCompleted(); });
        await answered.Should().Within(20.Seconds()).Emit(
            "the owner stream's hub answers after every update queued before it",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [HubFact]
    public async Task ASlowFirstFrame_LogsExactlyOneInformationLine_WithTheThreeHops()
    {
        var stream = await Render(SlowArea, "slow-2");
        await FirstFrameLine(SlowArea);
        // The re-render's update is published before its applied callback runs: settle the owner's
        // queue past it before counting, so a duplicate line would already be in the sink.
        await Settle(stream);

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
        var stream = await Render(FastArea, "fast");
        await FirstFrameLine(FastArea);
        await Settle(stream);

        var lines = FirstFrameLines(FastArea);
        lines.Should().ContainSingle("the fast open is still reported, once, at Debug");
        lines.Single().Level.Should().Be(LogLevel.Debug,
            "a healthy open writes nothing at Information, so it costs Loki nothing");
    }

    /// <summary>
    /// A data feed that faults while the view's first control is still pending: the error frame is
    /// published through the error path's own update (<c>SurfaceRenderError</c>), and it is that open's
    /// first full frame — reported once, with the compose hop.
    /// </summary>
    [HubFact]
    public async Task AnErrorFrame_IsTheFirstFullFrame_AndIsReportedOnce()
    {
        var stream = await Render(FailingArea, FailureText);
        var (_, message) = await FirstFrameLine(FailingArea);
        await Settle(stream);

        FirstFrameLines(FailingArea).Should().ContainSingle("the error frame is reported once, as the first full frame");
        message.Should().NotContain("composed at -1 ms", "the error control is the composed first control");
    }

    /// <summary>Captures every record into the owning test's instance queue.</summary>
    internal sealed class QueueLoggerProvider(
        ConcurrentQueue<(LogLevel, string)> sink, IObserver<(LogLevel, string)> signal) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new QueueLogger(sink, signal);
        public void Dispose() { }

        private sealed class QueueLogger(
            ConcurrentQueue<(LogLevel, string)> sink, IObserver<(LogLevel, string)> signal) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var record = (logLevel, formatter(state, exception));
                sink.Enqueue(record);
                signal.OnNext(record);
            }
        }
    }
}
