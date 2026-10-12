using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// A click on an area the owner holds NO control for must not report success.
///
/// <para><c>LayoutAreaHost.OnClick</c> used to answer <see cref="UserActionAccepted"/> for a click
/// whose area had no control, so the sender's pending state cleared as if the action had run and the
/// click was dropped with nothing said to anyone. It now answers a <see cref="DeliveryFailure"/>
/// (<see cref="ErrorType.Rejected"/>, the localized <c>click.noControl</c> sentence naming the area),
/// which reaches the sender's <c>onRefused</c> arm through <c>SubmitUserAction</c> — the very call the
/// Blazor view makes — plus one Warning on the owner.</para>
///
/// <para>Two controls keep the measurement honest: a click on the rendered button of the SAME stream
/// is still accepted and runs (so the refusal is about the missing area, not about the stream), and a
/// click on a control that declares no click action is still accepted as the no-op it is.</para>
/// </summary>
public class ClickOnAnAreaWithNoControlIsRefusedTest : HubTestBase
{
    private const string Area = "NoControlClick";
    private const string ButtonArea = Area + "/Button";
    private const string LabelArea = Area + "/Label";
    private const string MissingArea = Area + "/NotRendered";

    private readonly AsyncSubject<bool> clickRan = new();
    private int clickRuns;

    /// <summary>Every log record. INSTANCE state, never static (NoStaticState).</summary>
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> logRecords = new();

    /// <inheritdoc />
    public ClickOnAnAreaWithNoControlIsRefusedTest(ITestOutputHelper output)
        : base(output)
    {
        Services.AddLogging(logging =>
            logging.Services.AddSingleton<ILoggerProvider>(new QueueLoggerProvider(logRecords)));
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(
                Area,
                Controls.Stack
                    .WithView(
                        Controls.Button("Run").WithClickAction(_ =>
                        {
                            Interlocked.Increment(ref clickRuns);
                            clickRan.OnNext(true);
                            clickRan.OnCompleted();
                            return Task.CompletedTask;
                        }),
                        "Button")
                    .WithView(Controls.Markdown("label"), "Label")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private async Task<ISynchronizationStream<JsonElement>> RenderedStream()
    {
        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        await stream.GetControlStream(ButtonArea).Should().Within(10.Seconds()).Match(
            control => control is not null,
            "the page must be rendered before anything on it is clicked",
            cancellationToken: TestContext.Current.CancellationToken);
        return stream;
    }

    /// <summary>The outcome of one submitted click: <c>null</c> = accepted, otherwise the refusal sentence.</summary>
    private static async Task<string?> Submit(ISynchronizationStream stream, string area)
    {
        var outcome = new AsyncSubject<string?>();
        stream.SubmitUserAction(
            new ClickedEvent(area, stream.StreamId),
            actingUser: null,
            onRefused: sentence => { outcome.OnNext(sentence); outcome.OnCompleted(); },
            onAccepted: () => { outcome.OnNext(null); outcome.OnCompleted(); });
        return await outcome.Should().Within(10.Seconds()).Emit(
            "every submitted click is answered — accepted or refused — never left pending",
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [HubFact]
    public async Task ClickOnAnAreaWithNoControl_IsRefusedNamingTheArea_NotAccepted()
    {
        var stream = await RenderedStream();

        var refusal = await Submit(stream, MissingArea);

        refusal.Should().NotBeNull(
            "a click on an area with no control did not run, so the sender must not be told it was accepted");
        refusal.Should().Be(
            LocalizationCatalog.Get("click.noControl", locale: null, MissingArea),
            "the sentence a person reads comes from the catalog, never a literal");
        refusal.Should().Contain(MissingArea, "the refusal names which click did not run");
        clickRuns.Should().Be(0, "no action ran");

        logRecords.Where(r => r.Message.Contains("REFUSING click") && r.Message.Contains(MissingArea))
            .Should().ContainSingle("the refusal is logged exactly once on the owner")
            .Which.Level.Should().Be(LogLevel.Warning);
    }

    [HubFact]
    public async Task NegativeControl_ClickOnTheRenderedButtonOfTheSameStream_IsAcceptedAndRuns()
    {
        var stream = await RenderedStream();

        var refusal = await Submit(stream, ButtonArea);

        refusal.Should().BeNull("a click on a rendered control with an action is accepted once it ran");
        await clickRan.Should().Within(10.Seconds()).Emit(
            "the button's action ran", cancellationToken: TestContext.Current.CancellationToken);
        logRecords.Should().NotContain(r => r.Message.Contains("REFUSING click"),
            "nothing was refused on this stream");
    }

    [HubFact]
    public async Task ClickOnARenderedControlWithoutAnAction_IsStillAcceptedAsANoOp()
    {
        var stream = await RenderedStream();

        var refusal = await Submit(stream, LabelArea);

        refusal.Should().BeNull(
            "a rendered control that declares no click action has nothing to run; the click is the no-op it always was");
        clickRuns.Should().Be(0);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        clickRan.Dispose();
        await base.DisposeAsync();
    }

    /// <summary>Captures every record into the owning test's instance queue.</summary>
    private sealed class QueueLoggerProvider(ConcurrentQueue<(LogLevel, string)> sink) : ILoggerProvider
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
