using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>An owner activation that refuses a user action for a stream it does not serve must tell the
/// subscriber its stream has ENDED</b> — issue #8052.
///
/// <para><b>What was measured in production.</b> memex-cloud, 2026-11-09 13:58Z, during a roll. A
/// circuit on a new replica held a live layout stream whose owner activation lived on a replica that
/// was leaving. The leaving replica's router refused the goodbye (<c>Host is shutting down, cannot
/// route to</c> the circuit, 13:58:24Z), the owner re-activated on another replica (13:58:26Z), and
/// the person's click at 13:58:28Z reached that NEW activation, which had no <c>sync/{id}</c> for the
/// stream: <i>"REFUSING ClickedEvent … NO sync hub for this stream was EVER registered on the current
/// activation"</i>. That click could not run — it is the residue the refusal exists to make visible.
/// The defect is what came next: the new activation KNEW the subscriber was bound to an activation
/// that no longer exists and said nothing, so the mirror stayed stranded and every later action on the
/// page was refused the same way until the subscriber's next heartbeat (45 s by default) happened to
/// name the stream.</para>
///
/// <para><b>The fixture.</b> <c>host/1</c> is reached through <c>RouteAddressToHostedHub</c> with
/// <c>HostedHubCreation.Always</c>, so disposing it and addressing it again is an Orleans
/// deactivate-then-reactivate. The goodbye is made undeliverable the way a leaving host makes it
/// undeliverable: its carrier starts going down the instant it is chosen, so the announcement is
/// declined — and the test reads that decision from the log rather than assuming it. The
/// subscriber's heartbeat is set beyond the test's reach, so the only thing that can tell the mirror
/// is the refusal itself.</para>
/// </summary>
public class RefusalTellsAStrandedSubscriberTest : HubTestBase
{
    private const string Area = "StrandedRecovery";
    private const string ButtonArea = Area + "/Button";
    private const string NeverRegistered = "NO sync hub for this stream was EVER registered";

    /// <summary>The mesh's non-router spokesman — see <see cref="OwnerDeactivationTellsItsLiveSubscribersTest"/>.</summary>
    private static readonly Address CarrierAddress = new("carrier", "1");

    /// <summary>
    /// How long the owner holds a stream message whose <c>sync/{id}</c> is not registered before
    /// refusing it (<c>SyncStreamOptions.SyncHubRegistrationGrace</c>). A framework option, so the
    /// refusal arrives deterministically and the test does not spend the 5 s default.
    /// </summary>
    private static readonly TimeSpan OwnerHoldsAnUnroutableAction = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The subscriber's heartbeat cadence, deliberately beyond any wait in this test: a heartbeat
    /// naming the stream is answered with a <c>StreamEndedEvent</c> by an owner that does not serve
    /// it (#6047), and that answer would recover the mirror on its own and mask the refusal's.
    /// </summary>
    private static readonly TimeSpan HeartbeatOutOfReach = TimeSpan.FromHours(1);

    /// <summary>REPLAY-backed: refusal and re-subscribe lines can land before a wait is armed.</summary>
    private readonly ReplaySubject<(LogLevel Level, string Message)> logRecords = new();

    /// <summary>Completed by the layout area's own click action — the proof an action RAN.</summary>
    private readonly AsyncSubject<Unit> clicked = new();

    /// <summary>Completed with the sentence the FIRST click's refusal carried to its sender.</summary>
    private readonly AsyncSubject<string> firstClickRefused = new();

    /// <summary>How many activations have rendered <see cref="Area"/>; every later one waits on <see cref="reActivationRenders"/>.</summary>
    private int renders;

    /// <summary>
    /// Holds the re-activation's render open, so the re-hydrated base frame (no button yet) is
    /// observable before the content patch lands — see the sibling test's note on PR #7013.
    /// </summary>
    private readonly AsyncSubject<Unit> reActivationRenders = new();

    /// <inheritdoc />
    public RefusalTellsAStrandedSubscriberTest(ITestOutputHelper output)
        : base(output)
        => Services.AddLogging(logging =>
        {
            logging.Services.AddSingleton<ILoggerProvider>(new SubjectLoggerProvider(logRecords));
            // The re-subscribe line is Information and the test tree floors every category at
            // Warning; a provider-scoped filter lets this sink see it without moving any level.
            logging.AddFilter<SubjectLoggerProvider>(null, LogLevel.Trace);
        });

    private UiControl ClickArea()
        => Controls.Stack.WithView(
            Controls.Html("Run").WithClickAction(_ =>
            {
                clicked.OnNext(Unit.Default);
                clicked.OnCompleted();
                return Task.CompletedTask;
            }), "Button");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            .Set(new RouterCarrier(router =>
            {
                // Healthy when RESOLVED, shutting down when the goodbye is DELIVERED — the
                // announcement declines, exactly as a leaving host's refused router leaves the
                // subscriber untold.
                var carrier = router.GetHostedHub(CarrierAddress, c => c);
                carrier?.Dispose();
                return carrier;
            }));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services.Configure<SyncStreamOptions>(o =>
                o.SyncHubRegistrationGrace = OwnerHoldsAnUnroutableAction))
            .AddLayout(layout => layout.WithView<UiControl>(Area, (_, _) =>
                Interlocked.Increment(ref renders) == 1
                    ? Observable.Return(ClickArea())
                    : reActivationRenders.Select(_ => ClickArea())));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithServices(services => services.Configure<SyncStreamOptions>(o =>
            {
                o.HeartbeatInterval = HeartbeatOutOfReach;
                o.FirstHeartbeat = HeartbeatOutOfReach;
            }))
            .AddLayoutClient(d => d);

    /// <summary>
    /// A live mirror; an owner that deactivates with its goodbye declined; a click that misses (the
    /// residue, refused); and then the NEXT click, which must run on the new activation because the
    /// refusal told the subscriber its stream had ended.
    /// </summary>
    [HubFact]
    public async Task AfterARefusalForAStreamTheActivationNeverServed_TheNextClickRuns()
    {
        var client = GetClient();
        var owner = GetHost();

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is not null,
                "the owner-side LayoutAreaHost and its stream-scoped click handler must be live first");
        var streamId = stream.StreamId;

        // POSITIVE CONTROL for Renders: the first activation's snapshot carries the button.
        await stream.Select(item => Renders(item.Value, ButtonArea)).Should()
            .Within(TestTimeouts.Convergence).Match(rendered => rendered,
                "the first activation rendered the button, and Renders has to be able to see it");

        // Released in an implicit finally so a failing assertion cannot leave the render held.
        using var releaseTheHold = Disposable.Create(reActivationRenders.OnCompleted);

        // THE DEACTIVATION, with its goodbye undeliverable.
        owner.Dispose();
        await owner.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the owner activation has to be gone before a click can miss it");
        await logRecords
            .Where(r => r.Message.Contains("is itself shutting down", StringComparison.Ordinal)
                        && r.Message.Contains(CarrierAddress.ToString(), StringComparison.Ordinal))
            .Select(r => r.Message)
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the goodbye must have been DECLINED — otherwise the subscriber was told by the "
                + "announcement and this test measures nothing about the refusal");

        // Armed BEFORE the first click: the mirror's next emission is the re-hydration.
        var reHydrated = stream.Skip(1).Replay(1);
        using var reHydration = reHydrated.Connect();

        // The click that misses: the stale page still shows the button, the new activation has no
        // sync/{id} for this stream. Refused — the residue, and correctly so.
        stream.SubmitUserAction(new ClickedEvent(ButtonArea, streamId), onRefused: sentence =>
        {
            firstClickRefused.OnNext(sentence);
            firstClickRefused.OnCompleted();
        });
        await firstClickRefused.Should().Within(TestTimeouts.Convergence).Emit(
            "a click addressed to an activation that no longer exists cannot run, and its sender "
            + "is told so");
        await Refusals(streamId).Should().Within(TestTimeouts.Convergence).Match(
            line => line.Contains(NeverRegistered, StringComparison.Ordinal),
            "the owner that refused it had never served this stream on its current activation — the "
            + "production sentence of #8052");

        await ReSubscribes(streamId).Should().Within(TestTimeouts.Convergence).Emit(
            "THE DEFECT: the refusing activation KNOWS this subscriber is bound to a dead activation; "
            + "unless the refusal says the stream ended, nothing tells the mirror before its next "
            + "heartbeat, and every later action on the page is refused the same way");

        await reHydrated.Should().Within(TestTimeouts.Convergence).Match(
            item => !Renders(item.Value, ButtonArea),
            "being told ends in a mirror bound to the NEW activation, whose first frame is its base "
            + "shell, before the render lands");

        reActivationRenders.OnNext(Unit.Default);
        reActivationRenders.OnCompleted();

        await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is not null,
                "the person clicks the button the NEW activation rendered");

        stream.SubmitUserAction(new ClickedEvent(ButtonArea, streamId));

        await clicked.Should().Within(TestTimeouts.Convergence).Emit(
            "the next click lands on a live handler: the refusal of the first one is what re-bound "
            + "the mirror");
    }

    /// <summary>Whether the layout snapshot <paramref name="store"/> carries a control at <paramref name="area"/>.</summary>
    private static bool Renders(JsonElement store, string area)
        => store.ValueKind == JsonValueKind.Object
           && store.TryGetProperty(LayoutAreaReference.Areas, out var areas)
           && areas.ValueKind == JsonValueKind.Object
           // Area keys are stored JSON-SERIALIZED, quotes included.
           && areas.TryGetProperty(LayoutAreaReference.Encode(area), out var control)
           && control.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>Every <c>REFUSING …</c> line the owner wrote about <paramref name="streamId"/>.</summary>
    private IObservable<string> Refusals(string streamId)
        => logRecords
            .Where(r => r.Level == LogLevel.Error
                        && r.Message.StartsWith("REFUSING", StringComparison.Ordinal)
                        && r.Message.Contains(streamId, StringComparison.Ordinal))
            .Select(r => r.Message);

    /// <summary>
    /// <c>JsonSynchronizationStream.Resubscribe</c>'s Information line for a re-ask caused by the
    /// owner ending the server-side half — the observable consequence of having been TOLD.
    /// </summary>
    private IObservable<string> ReSubscribes(string streamId)
        => logRecords
            .Where(r => r.Message.Contains(streamId, StringComparison.Ordinal)
                        && r.Message.Contains("ended our server-side subscription",
                            StringComparison.Ordinal)
                        && r.Message.Contains("resubscribing", StringComparison.Ordinal))
            .Select(r => r.Message);

    /// <summary>Subjects are disposed AFTER the base teardown, which logs into them.</summary>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            clicked.Dispose();
            firstClickRefused.Dispose();
            reActivationRenders.Dispose();
            logRecords.Dispose();
        }
    }

    /// <summary>Publishes every record the mesh produced into the owning test's instance subject.</summary>
    private sealed class SubjectLoggerProvider(IObserver<(LogLevel, string)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SubjectLogger(sink);
        public void Dispose() { }

        private sealed class SubjectLogger(IObserver<(LogLevel, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => sink.OnNext((logLevel, formatter(state, exception)));
        }
    }
}
