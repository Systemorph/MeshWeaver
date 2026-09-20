using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 <b>An owner that DEACTIVATES must tell the mirrors it is about to orphan</b> — issue #3986,
/// the route the refusal machinery made legible and nothing fixed.
///
/// <para><b>What was measured in production.</b> memex-cloud, 2026-09-18 16:47:13–16:47:25Z:
/// <b>four</b> <c>ClickedEvent</c>s on area <c>Catalog/Categories/Cat-Education</c>, all for the SAME
/// stream <c>b98AWu3uVUS05xCcA9XQeA</c> from the SAME sender
/// <c>sync/b98AWu3uVUS05xCcA9XQeA~portal/ASZ-lU6…</c>, every one refused by the <c>Store</c> owner
/// because it had no <c>sync/{id}</c> for that stream. A person clicked four times over twelve
/// seconds and nothing happened. That is NOT the client-side release ordering this issue was filed
/// on (<c>1594bb31e4</c>): a circuit whose stream has been released posts nothing at all — one
/// look at <c>UserActionSubmission.SubmitUserAction</c>'s <c>HubIfHeld</c> arm says so — let alone
/// four times over twelve seconds. The sender was LIVE and the OWNER's half was gone.</para>
///
/// <para><b>Why the subscriber was never told, and it is an ordering defect, not a missing retry.</b>
/// A stream's end has two announcers and BOTH were keyed to the wrong event for a deactivating owner:
/// <list type="number">
///   <item><description><c>JsonSynchronizationStream</c>'s per-stream <c>StreamEndedEvent</c> is
///     deliberately suppressed once the OWNING hub is disposing — "a hub must speak only for itself,
///     and never while it is dying", because a dying owner reaching up the tree resurrects the
///     Orleans activation it is retiring. It delegates that case, in terms, to the second
///     announcer.</description></item>
///   <item><description><c>Workspace</c>'s <c>RecycleAnnouncement</c> — which posts through a carrier
///     that outlives the owner, AFTER its <c>DisposalCompleted</c>, and therefore has neither problem
///     — was hung on <c>MessageHub.HandleDispose</c>, i.e. on a message-routed <c>DisposeRequest</c>.
///     <c>MessageHubGrain.OnDeactivateAsync</c> calls <c>hub.Dispose()</c> DIRECTLY and posts no such
///     request, and that is "the largest single source of direct <c>Dispose()</c> in the mesh"
///     (#4888).</description></item>
/// </list>
/// So on the commonest teardown there is, neither announcer spoke. The mirror kept replaying its last
/// snapshot — the page still rendered, which is why nobody reported a blank screen — and every user
/// action it sent afterwards was refused <i>"NO sync hub for this stream was EVER registered on the
/// current activation"</i> and discarded. The fix moves the announcement to the teardown itself; see
/// <c>MessageHub.AnnounceRecycleUnlessAnAncestorIsTakingUsWithIt</c> and
/// <c>RecycleAnnouncementTest</c> for the seam's own contract in both directions.</para>
///
/// <para><b>The fixture is the production shape, not a simulation of it.</b> <c>host/1</c> is reached
/// through <c>RouteAddressToHostedHub</c> with <c>HostedHubCreation.Always</c>, so disposing it and
/// then addressing it again is exactly an Orleans deactivate-then-reactivate: a new activation with
/// no <c>sync/{id}</c> for a stream a live subscriber still holds. The mesh carries a
/// <see cref="RouterCarrier"/> because production's <c>MeshBuilder</c> does — it is what gives the
/// announcement a non-router hub that outlives the owner.</para>
/// </summary>
public class OwnerDeactivationTellsItsLiveSubscribersTest : HubTestBase
{
    private const string Area = "DeactivationRecovery";
    private const string ButtonArea = Area + "/Button";

    /// <summary>
    /// The mesh's designated non-router spokesman. Production gets one from <c>MeshBuilder</c>
    /// (<c>router.NodeOperationExecutionHub()</c>); the plumbing fixture has to name its own, or the
    /// announcement correctly declines to speak through the router and this test would measure the
    /// absence of a carrier rather than the presence of a goodbye.
    /// </summary>
    private static readonly Address CarrierAddress = new("carrier", "1");

    /// <summary>
    /// 🚨 REPLAY-backed, never a bare <see cref="Subject{T}"/>. Both the refusal and the
    /// re-subscribe line are written from a hub's own turn and can land before an assertion window
    /// opens; a bare subject drops them and the test passes having observed nothing.
    /// </summary>
    private readonly ReplaySubject<(LogLevel Level, string Message)> logRecords = new();

    /// <summary>Completed by the layout area's own click action — the proof the action RAN.</summary>
    private readonly AsyncSubject<Unit> clicked = new();

    /// <inheritdoc />
    public OwnerDeactivationTellsItsLiveSubscribersTest(ITestOutputHelper output)
        : base(output)
        => Services.AddLogging(logging =>
        {
            logging.Services.AddSingleton<ILoggerProvider>(new SubjectLoggerProvider(logRecords));
            // 🚨 THE PROVIDER FILTER IS LOAD-BEARING, and leaving it out cost this test a 36-second
            // FALSE RED. The test tree's appsettings.json floors every category at `Warning`, and
            // filtering happens in the LoggerFactory — BEFORE a provider is handed the record. So
            // <see cref="ReSubscribes"/>, which reads an Information line, observed nothing whatever
            // the framework did, and the wait below burnt its whole budget against a dark
            // instrument. A provider-scoped filter is the narrowest fix: this test's sink sees
            // everything, the console sink keeps the tree's floor, and no src-tree level moves.
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

    /// <summary>
    /// When set, the carrier starts going down the instant AFTER the announcement has chosen it —
    /// the interleaving in which the "is an ancestor taking us with it?" read at the top of
    /// <c>Dispose()</c> is already stale by the time the goodbye is due. Instance state, read lazily
    /// by the carrier lambda, so a test sets it before disposing the owner.
    /// </summary>
    private volatile bool carrierGoesDownOnceChosen;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf)
        => base.ConfigureMesh(conf)
            .Set(new RouterCarrier(router =>
            {
                var carrier = router.GetHostedHub(CarrierAddress, c => c);
                // Dispose() flips IsShuttingDown synchronously, so by construction the carrier is
                // healthy when it is RESOLVED and shutting down when the goodbye is DELIVERED —
                // no race, no wait.
                if (carrierGoesDownOnceChosen)
                    carrier?.Dispose();
                return carrier;
            }));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(Area, ClickArea()));

    /// <summary>
    /// How long THIS host holds a stream message whose <c>sync/{id}</c> is not registered before
    /// refusing it, read from the hub's own <see cref="SyncStreamOptions"/>.
    ///
    /// <para>🚨 Read rather than SET, and rather than written as a literal. The refusal this test
    /// asserts the absence of is written exactly one grace after the action arrives, so the negative
    /// window has to be that grace — and a number this test invented would either be shorter than the
    /// framework's (an assertion that cannot fail) or an unexplained guess about a machine's speed.
    /// Asking the option makes the window the framework's own, whatever it is configured to be.</para>
    /// </summary>
    private static TimeSpan OwnerHoldsAnUnroutableAction(IMessageHub host)
        => host.ServiceProvider.GetRequiredService<IOptions<SyncStreamOptions>>()
            .Value.SyncHubRegistrationGrace;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// The whole chain, in the order production takes it: a live mirror, an owner that DEACTIVATES
    /// (direct <c>Dispose()</c>, no <c>DisposeRequest</c>), and then a person's click.
    ///
    /// <para>Every wait is on the event that actually settles the step — the owner's own
    /// <c>DisposalCompleted</c>, the mirror's next emission, the click action's own signal — so
    /// nothing here is a sleep, a poll or a widened bound. Without the announcement the subscriber
    /// is told nothing: the mirror never re-hydrates, so the wait for its next emission is what reds
    /// first, and the click that follows is refused exactly as it was in production.</para>
    /// </summary>
    [HubFact]
    public async Task AClickAfterItsOwnerDeactivatedStillRuns()
    {
        var client = GetClient();
        var owner = GetHost();
        // Read while the owner is alive — after its disposal this resolves a NEW activation.
        var ownerHoldsAnUnroutableAction = OwnerHoldsAnUnroutableAction(owner);

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is not null,
                "the owner-side LayoutAreaHost and its stream-scoped action handlers must be live "
                + "before anything about losing them can be measured");
        var streamId = stream.StreamId;

        owner.GetHostedHub(SynchronizationAddress.Create(streamId), HostedHubCreation.Never)
            .Should().NotBeNull(
                "the owner hosts one sync/{id} sub-hub per subscriber — without it this test measures "
                + "nothing");

        // Armed BEFORE the deactivation: the mirror's NEXT emission is the re-hydration, and a
        // subscription taken afterwards could miss it. Replay-backed for the same reason the log
        // subject is.
        var reHydrated = stream.Skip(1).Replay(1);
        using var reHydration = reHydrated.Connect();

        // 🚨 THE ORLEANS DEACTIVATION, verbatim: MessageHubGrain.OnDeactivateAsync notes the cause
        // and calls Dispose(). No DisposeRequest is posted, and nothing tells this client.
        owner.Dispose();
        await owner.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the owner activation has to be GONE before a click can miss it — its own completion "
            + "signal is that receipt, and the announcement is delivered off it");

        await ReSubscribes(streamId).Should().Within(TestTimeouts.Convergence).Emit(
            "the owner's goodbye is the only thing that can reach a subscriber of a DEACTIVATED "
            + "owner: the per-stream StreamEndedEvent is suppressed while the owner is disposing, "
            + "the change-feed latch needs a WRITE and a deactivation is not one, and the recycle "
            + "re-arm needs a SubscribeRequest to be NACKed — nothing re-asks, so nothing is NACKed");

        await reHydrated.Should().Within(TestTimeouts.Convergence).Emit(
            "and being told has to end in a mirror bound to the NEW activation — a re-ask that "
            + "never lands leaves the same stale snapshot the person was already looking at");

        // The person is still on the page, and clicks.
        stream.SubmitUserAction(new ClickedEvent(ButtonArea, streamId));

        await clicked.Should().Within(TestTimeouts.Convergence).Emit(
            "THE DEFECT: four of these were thrown away on memex-cloud on 2026-09-18 because the "
            + "owner's new activation had no sync/{id} for a stream this subscriber still held");
        await Refusals(streamId).Should().NotEmit(
            ownerHoldsAnUnroutableAction,
            "and an action that RAN must produce no refusal line at all — the owner held it for the "
            + "registration grace and found a live handler, which is the whole difference between a "
            + "recovered mirror and a stranded one");
    }

    /// <summary>
    /// 🚨 The interleaving the announce-time guard cannot see (PR #4974 review): the subtree is
    /// frozen AFTER the owner read <c>IsShuttingDown</c> and BEFORE its goodbye is due. The guard's
    /// answer is final only at DELIVERY, so <c>Workspace</c> asks the carrier again there — a carrier
    /// that is itself shutting down means the tree is going, and the goodbye is declined.
    ///
    /// <para>Made deterministic rather than raced: the carrier is healthy when the announcement
    /// RESOLVES it and is disposed in that same call, so it is shutting down by construction when
    /// the owner's <c>DisposalCompleted</c> fires. The assertion reads the decision itself — the
    /// line exists only on the declining arm.</para>
    /// </summary>
    [HubFact]
    public async Task AGoodbyeIsDeclinedWhenItsCarrierStartedGoingDownAfterItWasChosen()
    {
        var client = GetClient();
        var owner = GetHost();

        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is not null,
                "there has to be a live client subscription for the owner to have anyone to tell");

        carrierGoesDownOnceChosen = true;
        owner.Dispose();
        await owner.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the goodbye is decided off the owner's own completion signal");

        await logRecords
            .Where(r => r.Message.Contains("is itself shutting down", StringComparison.Ordinal)
                        && r.Message.Contains(CarrierAddress.ToString(), StringComparison.Ordinal))
            .Select(r => r.Message)
            .Should().Within(TestTimeouts.Convergence).Emit(
                "a carrier that began shutting down between being chosen and the goodbye falling due "
                + "means the tree is going — announcing then tells a subscriber to re-ask for an "
                + "address that is not coming back, which is the resurrection the teardown silence "
                + "exists to prevent");
    }

    /// <summary>Every <c>REFUSING …</c> line the owner wrote about <paramref name="streamId"/>.</summary>
    private IObservable<string> Refusals(string streamId)
        => logRecords
            .Where(r => r.Level == LogLevel.Error
                        && r.Message.StartsWith("REFUSING", StringComparison.Ordinal)
                        && r.Message.Contains(streamId, StringComparison.Ordinal))
            .Select(r => r.Message);

    /// <summary>
    /// The subscriber announcing that it is re-asking because the owner ended its server-side half —
    /// <c>JsonSynchronizationStream.Resubscribe</c>'s own Information line. It is the observable
    /// consequence of having been TOLD, and it exists only on the goodbye's path.
    /// </summary>
    private IObservable<string> ReSubscribes(string streamId)
        => logRecords
            .Where(r => r.Message.Contains(streamId, StringComparison.Ordinal)
                        && r.Message.Contains("ended our server-side subscription",
                            StringComparison.Ordinal)
                        && r.Message.Contains("resubscribing", StringComparison.Ordinal))
            .Select(r => r.Message);

    /// <summary>
    /// 🚨 The subjects are disposed AFTER the base teardown, in a <c>finally</c>. The base drains and
    /// disposes the mesh, which LOGS while it does so — and every record goes through
    /// <see cref="SubjectLoggerProvider"/> into <see cref="logRecords"/>, whose <c>OnNext</c> throws
    /// once disposed. Disposing first turns an ordinary teardown into a fault inside a logger call.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            clicked.Dispose();
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
