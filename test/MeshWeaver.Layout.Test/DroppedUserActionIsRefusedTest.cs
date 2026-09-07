using System;
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Regression guard for issue #3566 — <b>a user's action must never be discarded silently</b>.
///
/// <para>A <see cref="ClickedEvent"/> whose <c>sync/{id}</c> sub-hub is gone (a disposed circuit, a
/// released read stream, a reaped sync hub) used to be dropped with a Warning that reads exactly
/// like routine data-sync churn — and the <c>WithClickAction</c> body was never invoked. Measured on
/// MeshWeaver.Education run 34042620439: a Store <i>Install</i> click, a navigation 20 ms later, the
/// circuit disposed at +175 ms, the drop logged 5 s after that, <c>InstallPackage</c> never called,
/// and a 300-second probe then waiting for a package that was never going to exist.</para>
///
/// <para>The fix REFUSES the action instead of dropping it: a <see cref="DeliveryFailure"/> back to
/// the sender (which a live portal hub surfaces as the standard error modal via
/// <c>PortalErrorReporting</c>) plus one <see cref="LogLevel.Error"/> line naming the action and the
/// area. Data-sync traffic keeps the historical silent drop — that is the control below, and it is
/// what makes this a measurement of the user-action class rather than of "everything now NACKs".</para>
/// </summary>
public class DroppedUserActionIsRefusedTest : HubTestBase
{
    /// <summary>Short enough that the hold expires inside a test, long enough not to race CI.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Every log record the host produced. INSTANCE state, never static — process-wide static
    /// collections bleed across tests (see NoStaticState).
    /// </summary>
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> logRecords = new();

    private readonly Subject<DeliveryFailure> clientFailures = new();

    /// <inheritdoc />
    public DroppedUserActionIsRefusedTest(ITestOutputHelper output)
        : base(output)
    {
        Services.AddLogging(logging =>
            logging.Services.AddSingleton<ILoggerProvider>(new QueueLoggerProvider(logRecords)));
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services =>
                services.Configure<SyncStreamOptions>(o => o.SyncHubRegistrationGrace = Grace))
            .AddData()
            .WithTypes(typeof(ClickedEvent), typeof(DataChangedEvent));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(ClickedEvent), typeof(DataChangedEvent))
            .WithHandler<DeliveryFailure>((_, d) =>
            {
                clientFailures.OnNext(d.Message);
                return d.Processed();
            });

    [HubFact]
    public async Task ClickOnAGoneStream_IsRefusedToTheSender_NotSilentlyDropped()
    {
        var client = GetClient();
        var streamId = "gone-" + Guid.NewGuid().AsString();
        const string area = "Store/Install";

        // The click reaches the owner AFTER the circuit that produced it has released its stream:
        // no sync/{id} sub-hub exists, and none will ever register.
        client.Post(new ClickedEvent(area, streamId), o => o.WithTarget(CreateHostAddress()));

        var failure = await clientFailures.Should().Within(30.Seconds()).Emit(
            "a click the framework cannot deliver must be REFUSED to its sender, not discarded — "
            + "the sender is what surfaces it to the person who clicked");

        failure.ErrorType.Should().Be(ErrorType.Rejected,
            "the action was refused: it is neither a routing NotFound (which PortalErrorReporting "
            + "swallows as benign churn) nor a transient ShuttingDown the caller should retry into");
        failure.Message.Should().Be(
            LocalizationCatalog.Get("error.userActionNotRun", locale: null, area),
            "the sentence a person reads is resolved from the catalog off the acting user's locale, "
            + "never a hard-coded literal");
        failure.Message.Should().Contain(area, "the refusal names WHICH action did not run");

        logRecords.Should().Contain(
            r => r.Level == LogLevel.Error && r.Message.Contains(area) && r.Message.Contains(streamId),
            "the operator-facing record of a LOST USER ACTION is an Error naming the area and the "
            + "stream — not the Warning that data-sync churn shares");
    }

    [HubFact]
    public async Task DataSyncFrameOnAGoneStream_KeepsTheHistoricalSilentDrop()
    {
        var client = GetClient();
        var streamId = "gone-" + Guid.NewGuid().AsString();

        client.Post(
            new DataChangedEvent(streamId, 1, new RawJson("{}"), ChangeType.Full, null),
            o => o.WithTarget(CreateHostAddress()));

        // The control. A data frame for a subscriber that has gone away wants no audience: the only
        // party that could have used it left with its view. NACKing every one of them would be pure
        // teardown noise — the reason the framework deliberately never did.
        await clientFailures.Should().NotEmit(
            Grace + TimeSpan.FromSeconds(2),
            "data-sync traffic keeps the historical silent drop — only a USER ACTION is refused");
    }

    [HubFact]
    public Task TheThreeUserActionEventsAreMarked()
    {
        // Cheap, but it is the wiring the refusal turns on: a fourth user-action event added later
        // without the marker would silently rejoin the churn class.
        new ClickedEvent("a", "s").Should().BeAssignableTo<IUserAction>();
        new BlurEvent("a", "s").Should().BeAssignableTo<IUserAction>();
        new CloseDialogEvent("a", "s", DialogCloseState.OK).Should().BeAssignableTo<IUserAction>();
        ((IUserAction)new ClickedEvent("some/area", "s")).ActionArea.Should().Be("some/area");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        clientFailures.Dispose();
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
