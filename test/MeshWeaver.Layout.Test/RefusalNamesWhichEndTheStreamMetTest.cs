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
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Regression guard for the residue of issue #3986 — <b>one refusal sentence must not answer for
/// three different causes</b>.
///
/// <para><c>DataExtensions.RefuseUserAction</c> wrote a single line naming all three ways a
/// <c>sync/{id}</c> can be absent — <i>"disposed circuit, released read stream, or never-created
/// sync hub"</i> — so the incident fingerprint could not tell a DESIGNED refusal from a LIVE defect.
/// #3986 was filed on the client-side release ordering, fixed (<c>1594bb31e4</c>), deployed, and
/// then REOPENED on 2026-09-14 by an occurrence the fix could not reach: an owner-side per-node hub
/// (<c>rbuergi/Requests/provision-pearl-20260914</c>) whose <c>sync/{id}</c> had never been
/// registered on the activation that received the click. Same sentence, same fingerprint, different
/// defect — and two triages spent re-deriving which one they were looking at.</para>
///
/// <para>The split is an ATTRIBUTION fix, not a behaviour change: the owner records, per stream id
/// and per activation, whether it ever registered a <c>sync/{id}</c> and whether an
/// <c>UnsubscribeRequest</c> reached it (<c>SyncStreamActivationLedger</c>), and the refusal names
/// which of the three it was. Nothing retries, nothing waits, no bound moves, and a lost action is
/// still lost — <c>Doc/Architecture/RefusingALostUserAction</c> says why that is deliberate.</para>
///
/// <para><b>Each cause is produced by its own real route</b>, and each test also asserts that the
/// OTHER two causes' sentences did NOT appear — pairwise, because "distinguishable" is exactly the
/// property a shared sentence lacked. <see cref="AnAcceptedActionIsStillNotDiscarded"/> is the
/// positive control a fix that simply refused everything more descriptively would have to fail.</para>
/// </summary>
public class RefusalNamesWhichEndTheStreamMetTest : HubTestBase
{
    private const string Area = "RefusalAttribution";
    private const string ButtonArea = Area + "/Button";

    /// <summary>
    /// The phrase each cause is recognised by — the load-bearing half of its own log TEMPLATE, so
    /// an accidental re-merge of two causes into one sentence reds here rather than silently
    /// re-conflating the fingerprint.
    /// </summary>
    private const string ReleasedBySubscriber = "the SUBSCRIBER RELEASED this stream";
    private const string ReapedByOwner = "this hub SERVED this stream on the current activation";
    private const string NeverRegistered = "NO sync hub for this stream was EVER registered";
    private const string NoRecord = "this hub holds NO RECORD of the stream";

    /// <summary>
    /// How long the owner holds a stream message whose <c>sync/{id}</c> is not registered before
    /// refusing it (<c>SyncStreamOptions.SyncHubRegistrationGrace</c>). A framework OPTION, not a
    /// test wait: it is what makes each refusal below arrive deterministically instead of racing.
    /// </summary>
    private static readonly TimeSpan OwnerHoldsAnUnroutableAction = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How many stream dispositions the owner's activation ledger keeps
    /// (<c>SyncStreamOptions.ActivationLedgerCapacity</c>). The framework default is 512; a test
    /// that has to reach the BOUNDED answer lowers it before the host is built, which is the only
    /// way to observe that branch without minting hundreds of streams. Instance state, and read
    /// lazily by <see cref="ConfigureHost"/> on the first <c>GetHost()</c>.
    /// </summary>
    private int ledgerCapacity = 512;

    /// <summary>
    /// 🚨 REPLAY-backed, never a bare <see cref="Subject{T}"/>. A refusal is written from the
    /// owner's own turn and can land before an assertion window opens — a bare subject drops it and
    /// the test passes having observed nothing, which is exactly how a sibling test in this family
    /// passed in a filtered run and failed in the full suite. Instance state, never static.
    /// </summary>
    private readonly ReplaySubject<(LogLevel Level, string Message)> logRecords = new();

    private readonly AsyncSubject<Unit> clicked = new();

    /// <inheritdoc />
    public RefusalNamesWhichEndTheStreamMetTest(ITestOutputHelper output)
        : base(output)
    {
        // Mesh-wide, in the constructor — an ILoggerProvider added to a HUB's own service
        // collection never reaches the ILoggerFactory the hub resolves, which is the mesh
        // singleton. Every assertion below filters by stream id, so the wider capture costs
        // nothing.
        Services.AddLogging(logging =>
            logging.Services.AddSingleton<ILoggerProvider>(new SubjectLoggerProvider(logRecords)));
    }

    private UiControl ClickArea()
        => Controls.Stack.WithView(
            Controls.Html("Run").WithClickAction(_ =>
            {
                clicked.OnNext(Unit.Default);
                clicked.OnCompleted();
                return Task.CompletedTask;
            }), "Button");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services.Configure<SyncStreamOptions>(o =>
            {
                o.SyncHubRegistrationGrace = OwnerHoldsAnUnroutableAction;
                o.ActivationLedgerCapacity = ledgerCapacity;
            }))
            .AddLayout(layout => layout.WithView(Area, ClickArea()));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// The 2026-09-14 occurrence, and the reason this issue was reopened after its fix shipped: the
    /// owner activation that received the click had never served that stream. The client-side
    /// release ordering cannot reach it, so a sentence that reads like the release ordering sends
    /// the next reader to the wrong place.
    /// </summary>
    [HubFact]
    public async Task AStreamThisActivationNeverServed_IsNamedAsSuch()
    {
        var client = GetClient();
        var streamId = "never-" + Guid.NewGuid().AsString();

        client.Post(new ClickedEvent(ButtonArea, streamId), o => o.WithTarget(CreateHostAddress()));

        var refusal = await AwaitRefusalFor(streamId);

        refusal.Should().Contain(NeverRegistered,
            "the owner never registered a sync hub for this stream on this activation, and that is "
            + "a DIFFERENT defect from a subscriber releasing its own stream — an owner-side "
            + "per-node hub deactivating under a live subscriber is what reopened #3986");
        refusal.Should().NotContain(ReleasedBySubscriber);
        refusal.Should().NotContain(ReapedByOwner);
    }

    /// <summary>
    /// The DESIGNED refusal — the shape #3986 was filed on. The subscriber disposed its stream, the
    /// <c>UnsubscribeRequest</c> reached the owner, and a click that was still crossing lost the
    /// race. Nothing here is broken; the refusal exists so the loss is not silent.
    /// </summary>
    [HubFact]
    public async Task AStreamTheSubscriberReleased_IsNamedAsSuch()
    {
        var client = GetClient();
        var (stream, ownerSyncHub) = await SubscribedStreamWithLiveOwnerHandler(client);
        var streamId = stream.StreamId;

        stream.Dispose();
        await ownerSyncHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the release has to have REACHED the owner before anything can be asserted about how "
            + "the owner describes it — the sub-hub's own disposal is that receipt");

        client.Post(new ClickedEvent(ButtonArea, streamId), o => o.WithTarget(CreateHostAddress()));

        var refusal = await AwaitRefusalFor(streamId);

        refusal.Should().Contain(ReleasedBySubscriber,
            "an UnsubscribeRequest for this stream reached this hub, so the action raced a teardown "
            + "the client itself asked for — the designed refusal, not a defect to hunt");
        refusal.Should().NotContain(NeverRegistered);
        refusal.Should().NotContain(ReapedByOwner);
    }

    /// <summary>
    /// The third cause: this owner DID serve the stream on this activation and was never told to
    /// stop — the sub-hub went with an owner-side teardown (an idle release, a workspace eviction,
    /// <c>EvictClientSubscriptions</c>) while the subscriber was still attached. Disposing the
    /// owner's <c>sync/{id}</c> is the exact route <c>EvictClientSubscriptions</c> takes.
    /// </summary>
    [HubFact]
    public async Task AStreamTheOwnerReaped_IsNamedAsSuch()
    {
        var client = GetClient();
        var streamId = "reaped-" + Guid.NewGuid().AsString();
        var ownerSyncHub = await OwnerServedStreamWithNoClientSideStream(client, streamId);

        // 🚨 NOT a client-side stream disposal: that would post an UnsubscribeRequest and produce
        // the previous test's cause. The owner ends it on its own, which is why the subscriber is
        // told nothing and the next click still names a stream this hub once served.
        ownerSyncHub.Dispose();
        await ownerSyncHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the owner-side sub-hub must actually be gone before a click can miss it");

        client.Post(new ClickedEvent(ButtonArea, streamId), o => o.WithTarget(CreateHostAddress()));

        var refusal = await AwaitRefusalFor(streamId);

        refusal.Should().Contain(ReapedByOwner,
            "this hub served the stream and was never told to unsubscribe, so the OWNER side ended "
            + "it — the one cause of the three where the person is still on the page and the "
            + "platform, not the client, dropped the subscription");
        refusal.Should().NotContain(NeverRegistered);
        refusal.Should().NotContain(ReleasedBySubscriber);
    }

    /// <summary>
    /// 🚨 A stream id has a SECOND LIFE, and the refusal must judge the one that just ended.
    ///
    /// <para><c>JsonSynchronizationStream.Resubscribe</c> deliberately REUSES a stream id, and after
    /// an owner-side stream ends the same id can be served again on the same activation. A ledger
    /// that kept the first life's "the subscriber released it" flag would report the second life's
    /// owner-side reap as a subscriber release — the same conflation, one level down, and invisible
    /// because both sentences are plausible.</para>
    /// </summary>
    [HubFact]
    public async Task AStreamIdReusedAfterAReleaseIsJudgedOnItsSecondLife()
    {
        var client = GetClient();
        var streamId = "reused-" + Guid.NewGuid().AsString();

        var firstLife = await OwnerServedStreamWithNoClientSideStream(client, streamId);
        client.Post(new UnsubscribeRequest(streamId), o => o.WithTarget(CreateHostAddress()));
        await firstLife.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the release has to have been seen and acted on before the id can be re-served");

        // The SAME id, served again on the SAME activation — and this time the owner ends it.
        var secondLife = await OwnerServedStreamWithNoClientSideStream(client, streamId);
        secondLife.Dispose();
        await secondLife.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the second life's sub-hub must be gone before a click can miss it");

        client.Post(new ClickedEvent(ButtonArea, streamId), o => o.WithTarget(CreateHostAddress()));

        var refusal = await AwaitRefusalFor(streamId);

        refusal.Should().Contain(ReapedByOwner,
            "the SECOND life was ended by the owner; carrying the FIRST life's release forward "
            + "would put a designed refusal's sentence on a live defect");
        refusal.Should().NotContain(ReleasedBySubscriber);
    }

    /// <summary>
    /// 🚨 The BOUNDED answer, which is the safeguard the other three rest on: once the ledger has
    /// aged anything out, an absent stream id is no longer evidence of "never served here" — it is
    /// equally "served, and long since ended". It has to SAY that, with its own numbers, rather
    /// than printing the third cause's fingerprint on a full ledger.
    ///
    /// <para>The capacity is lowered to 1 so this is reachable with three streams instead of six
    /// hundred. Nothing else about the path differs — the same ledger, the same switch, the same
    /// real hubs.</para>
    /// </summary>
    [HubFact]
    public async Task AnAgedOutStreamSaysSoRatherThanClaimingItWasNeverServed()
    {
        // 🚨 BEFORE the first GetHost() — ConfigureHost reads this when the host is built.
        ledgerCapacity = 1;
        var client = GetClient();

        var aged = "aged-" + Guid.NewGuid().AsString();
        var agedHub = await OwnerServedStreamWithNoClientSideStream(client, aged);
        // It has to be GONE for the click to miss it — a live sub-hub simply receives the click,
        // which is the healthy path and measures nothing.
        agedHub.Dispose();
        await agedHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the sub-hub must be gone before a click can miss it");

        // Two more streams push its disposition past the capacity, so the ledger no longer holds
        // the record it would have answered from.
        await OwnerServedStreamWithNoClientSideStream(client, "aged-" + Guid.NewGuid().AsString());
        await OwnerServedStreamWithNoClientSideStream(client, "aged-" + Guid.NewGuid().AsString());

        client.Post(new ClickedEvent(ButtonArea, aged), o => o.WithTarget(CreateHostAddress()));

        var refusal = await AwaitRefusalFor(aged);

        refusal.Should().Contain(NoRecord,
            "the ledger has pruned this stream's disposition, so it can no longer tell 'never "
            + "served here' from 'served and long since ended' — and a diagnostic that cannot fail "
            + "to give an answer is not a diagnostic");
        refusal.Should().Contain("aged",
            "and it states its OWN numbers, so a reader knows how far from an answer it is");
        refusal.Should().NotContain(NeverRegistered,
            "which is the whole point: a FULL ledger must never masquerade as the cause that sends "
            + "the next reader hunting a reactivation that never happened");
    }

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. Every assertion above is satisfied by a system that refuses
    /// everything and merely describes the refusal better. This one is not: an action on a live
    /// stream must still RUN, and produce no refusal at all.
    /// </summary>
    [HubFact]
    public async Task AnAcceptedActionIsStillNotDiscarded()
    {
        var client = GetClient();
        var (stream, _) = await SubscribedStreamWithLiveOwnerHandler(client);

        stream.SubmitUserAction(new ClickedEvent(ButtonArea, stream.StreamId));

        await clicked.Should().Within(TestTimeouts.Convergence).Emit(
            "attributing a refusal must never cost a delivery — an accepted action still has to "
            + "invoke the ClickAction it names");

        await Refusals(stream.StreamId).Should().NotEmit(
            OwnerHoldsAnUnroutableAction,
            "and an action that RAN must produce no refusal line at all: a diagnostic that fires on "
            + "the healthy path would bury the three it exists to separate");
    }

    /// <summary>Every <c>REFUSING …</c> line the owner wrote about <paramref name="streamId"/>.</summary>
    private IObservable<string> Refusals(string streamId)
        => logRecords
            .Where(r => r.Level == LogLevel.Error
                        && r.Message.StartsWith("REFUSING", StringComparison.Ordinal)
                        && r.Message.Contains(streamId, StringComparison.Ordinal))
            .Select(r => r.Message);

    private Task<string> AwaitRefusalFor(string streamId)
        => Refusals(streamId).Should().Within(TestTimeouts.Convergence).Emit(
            "a user action the framework cannot deliver is refused with one Error line — without it "
            + "there is no fingerprint to split");

    /// <summary>
    /// A client stream whose owner-side <c>sync/{id}</c> sub-hub — the thing a release destroys —
    /// is live and holding the layout area's action handlers.
    /// </summary>
    private async Task<(ISynchronizationStream<JsonElement> Stream, IMessageHub OwnerSyncHub)>
        SubscribedStreamWithLiveOwnerHandler(IMessageHub client)
    {
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));

        await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is not null,
                "the owner-side LayoutAreaHost and its stream-scoped action handlers must be live "
                + "before anything about losing them can be measured");

        var ownerSyncHub = GetHost().GetHostedHub(
            SynchronizationAddress.Create(stream.StreamId), HostedHubCreation.Never);
        ownerSyncHub.Should().NotBeNull(
            "the owner hosts one sync/{id} sub-hub per subscriber — without it this test measures "
            + "nothing");

        return (stream, ownerSyncHub!);
    }

    /// <summary>
    /// An owner-side subscription with NO client-side stream behind it — a bare
    /// <c>SubscribeRequest</c>, which is the production entry point (<c>HandleSubscribeRequest</c> →
    /// <c>SubscribeToClient</c>).
    ///
    /// <para>🚨 Deliberately not <c>GetRemoteStream</c>: a client-side stream RE-SUBSCRIBES when the
    /// owner announces <c>StreamEndedEvent</c>, re-creating a <c>sync/{id}</c> under the SAME stream
    /// id — so the reap this test is about would be undone by a race, and the test would measure
    /// whichever won. With no client-side stream there is nothing to re-ask, and the window is
    /// closed by construction rather than by a wait.</para>
    /// </summary>
    private async Task<IMessageHub> OwnerServedStreamWithNoClientSideStream(
        IMessageHub client, string streamId)
    {
        var syncAddress = SynchronizationAddress.Create(streamId);
        var host = GetHost();
        var registered = host.ServiceProvider.GetRequiredService<HostedHubsCollection>()
            .HubAdded
            .Where(h => (h.Address with { Host = null }).Equals(syncAddress))
            .Replay(1);
        // Connected BEFORE the subscribe is posted — HubAdded is hot, and the sub-hub is created on
        // the owner's own turn, which can complete before any later subscriber attaches.
        using var connection = registered.Connect();

        client.Post(
            new SubscribeRequest(streamId, new LayoutAreaReference(Area)) { Subscriber = client.Address },
            o => o.WithTarget(CreateHostAddress()));

        return await registered.Should().Within(TestTimeouts.Convergence).Emit(
            "the owner must have SERVED this stream on this activation — that is the whole "
            + "difference between the reaped cause and the never-registered one");
    }

    /// <summary>
    /// 🚨 The subjects are disposed AFTER the base teardown, in a <c>finally</c>. The base drains
    /// and disposes the mesh, which LOGS while it does so — and every record goes through
    /// <see cref="SubjectLoggerProvider"/> into <see cref="logRecords"/>, whose
    /// <c>OnNext</c> throws once disposed. Disposing first turns an ordinary teardown into a fault
    /// inside a logger call.
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

    /// <summary>Publishes every record the host produced into the owning test's instance subject.</summary>
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
