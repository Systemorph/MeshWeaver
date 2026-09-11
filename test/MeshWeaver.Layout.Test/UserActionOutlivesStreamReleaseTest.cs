using System;
using System.Reactive;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Regression guard for issue #3986 — <b>a stream must not be released while a user action it
/// already accepted is still owed</b>.
///
/// <para>The owner-side <c>sync/{id}</c> sub-hub is the ONLY place a <see cref="ClickedEvent"/>,
/// <see cref="BlurEvent"/> or <see cref="CloseDialogEvent"/> is handled, and the only place the
/// <c>ClickAction</c> closure lives. The client's <c>UnsubscribeRequest</c> destroys it. Until this
/// change that request was registered on the STREAM, and
/// <c>SynchronizationStream.Dispose()</c> disposes its registrants SYNCHRONOUSLY and deliberately
/// BEFORE <c>Hub.Dispose()</c> (#1613) — so the release overtook every in-flight action with no
/// phase in between that could have waited, and the action was refused: it did not run and never
/// will. Measured in production 2026-09-10 on memex-cloud, area
/// <c>Catalog/Categories/Cat-Insurance</c>.</para>
///
/// <para>It is now registered on the stream's HUB, so it runs from <c>DisposeImpl</c> in the
/// ShutDown phase — strictly after <b>Quiescing</b>, whose whole job is draining this hub's pending
/// response callbacks. <c>SubmitUserAction</c> holds one of those until the owner acknowledges the
/// action, so the release orders behind it by construction. No timer, no grace, no retry.</para>
///
/// <para><b>Both directions are asserted, and they fail for opposite reasons.</b>
/// <see cref="AnAcceptedActionHoldsTheReleaseUntilTheOwnerAnswers"/> goes red on the defect — the
/// owner-side handler is destroyed while the action is owed. <see cref="AnOrdinaryReleaseIsPrompt"/>
/// goes red on the lazy "fix" of simply never releasing the stream, which would satisfy the first
/// test on its own.</para>
/// </summary>
public class UserActionOutlivesStreamReleaseTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "ReleaseOrdering";
    private const string ButtonArea = Area + "/Button";

    /// <summary>
    /// How long the OWNER holds a stream message whose <c>sync/{id}</c> is not registered before
    /// refusing it (<c>SyncStreamOptions.SyncHubRegistrationGrace</c>). This is a framework OPTION,
    /// not a test wait: it is what makes the "action still owed" window DETERMINISTIC instead of a
    /// race, and it is deliberately well under the hub's 2 s Quiescing budget so the drain ends by
    /// the owner answering rather than by the budget expiring.
    /// </summary>
    private static readonly TimeSpan OwnerHoldsAnUnroutableAction = TimeSpan.FromMilliseconds(400);

    private readonly AsyncSubject<Unit> clicked = new();

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
            .WithServices(services => services.Configure<SyncStreamOptions>(
                o => o.SyncHubRegistrationGrace = OwnerHoldsAnUnroutableAction))
            .AddLayout(layout => layout.WithView(Area, ClickArea()));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    [HubFact]
    public async Task AnAcceptedActionHoldsTheReleaseUntilTheOwnerAnswers()
    {
        var (stream, ownerSyncHub) = await SubscribedStreamWithLiveOwnerHandler();

        // An action the owner cannot acknowledge at once: no sync/{id} is registered for THIS
        // stream id, so the owner holds it for SyncHubRegistrationGrace and then refuses. That is
        // a real production shape — a reaped sync hub, or a released read stream, on a client that
        // is still there — and it is what makes the owed-work window deterministic.
        stream.SubmitUserAction(new ClickedEvent(ButtonArea, "no-sync-hub-for-this-stream-id"));

        stream.Dispose();

        await ownerSyncHub.DisposalCompleted.Should().NotEmit(
            OwnerHoldsAnUnroutableAction / 2,
            "releasing a stream must not destroy the owner-side handler while a user action it "
            + "accepted is still owed — a click discarded there did not run and never will (#3986)");

        await ownerSyncHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "and once the owner has answered, the release must go through: teardown lets owed work "
            + "FINISH, it never cancels it and never waits on a timer");
    }

    [HubFact]
    public async Task AnOrdinaryReleaseIsPrompt()
    {
        var (stream, ownerSyncHub) = await SubscribedStreamWithLiveOwnerHandler();

        // Nothing owed. This is the control that a fix which simply never releases the stream must
        // fail: it would satisfy the ordering test above on its own.
        stream.Dispose();

        await ownerSyncHub.DisposalCompleted.Should().Within(TestTimeouts.Quick).Emit(
            "a release with nothing owed must still reach the owner promptly — holding the "
            + "subscription open would leak a per-subscriber stream on every navigation");
    }

    [HubFact]
    public async Task AnActionOnALiveStreamStillRuns()
    {
        var (stream, _) = await SubscribedStreamWithLiveOwnerHandler();

        stream.SubmitUserAction(new ClickedEvent(ButtonArea, stream.StreamId));

        await clicked.Should().Within(TestTimeouts.Convergence).Emit(
            "the acknowledged submission path must still INVOKE the action — an ordering guarantee "
            + "that stopped delivering clicks would pass both tests above");
    }

    /// <summary>
    /// 🚨 The second thing a registered callback fixes, measured rather than reasoned. A refusal is
    /// a <c>DeliveryFailure</c> posted back to the SENDER, and the sender of a click is the
    /// stream's own <c>sync/{id}</c> hub — whose <c>ConfigureSynchronizationHub</c> carries a
    /// blanket <c>DeliveryFailure</c> handler that answers <c>OnError</c> for anything that is not
    /// a transient <c>ShuttingDown</c>. So a bare <c>Post</c> of a click that cannot be delivered
    /// FAULTS THE WHOLE MIRROR: every view bound to that stream dies over one lost click. (Probed
    /// on this fixture: the stream terminated with
    /// <c>DeliveryFailureException: Your last action (“ProbeArea/Button”) did not run …</c>.)
    /// <c>DroppedUserActionIsRefusedTest</c> could not see it — it posts from the client HUB, so
    /// the refusal never reaches a stream's handler.
    ///
    /// <para>Matched to the action it belongs to, the refusal becomes a sentence about that action
    /// instead of a page-level fault.</para>
    /// </summary>
    [HubFact]
    public async Task ARefusedActionSurfacesToTheCallerWithoutFaultingTheView()
    {
        var (stream, _) = await SubscribedStreamWithLiveOwnerHandler();

        // 🚨 REPLAY, not a bare Subject. The fault this asserts against travels the same path as
        // the refusal awaited below, so it can land BEFORE the NotEmit window opens — a bare
        // Subject drops it and the test passes having observed nothing. That is exactly how this
        // case passed in a filtered run and failed in the full suite before it was replay-backed.
        var faults = new ReplaySubject<Exception>(1);
        using var live = stream.Subscribe(_ => { }, faults.OnNext);

        var refusals = new ReplaySubject<string>(1);
        stream.SubmitUserAction(
            new ClickedEvent(ButtonArea, "no-sync-hub-for-this-stream-id"),
            onRefused: refusals.OnNext);

        var sentence = await refusals.Should().Within(TestTimeouts.Convergence).Emit(
            "a person whose action could not run must be told so — the refusal is the whole reason "
            + "IUserAction exists (#3566), and it is worth nothing if the caller never sees it");

        sentence.Should().Be(
            LocalizationCatalog.Get("error.userActionNotRun", locale: null, ButtonArea),
            "the sentence is the owner's, resolved from the catalog off the ACTING USER's locale — "
            + "never re-worded, never a literal, never an ambient culture");

        await faults.Should().NotEmit(
            OwnerHoldsAnUnroutableAction,
            "and one refused action must not fault the synchronization stream: a fault travels the "
            + "same path as the refusal that just arrived, and it would kill every view bound to "
            + "this mirror over a single lost click");
    }

    /// <summary>
    /// A client stream whose owner-side <c>sync/{id}</c> sub-hub — the thing a release destroys —
    /// is live and holding the layout area's action handlers.
    /// </summary>
    private async Task<(ISynchronizationStream<JsonElement> Stream, IMessageHub OwnerSyncHub)>
        SubscribedStreamWithLiveOwnerHandler()
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));

        await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is not null,
                "the owner-side LayoutAreaHost and its stream-scoped action handlers must be live "
                + "before anything about releasing them can be measured");

        var ownerSyncHub = GetHost().GetHostedHub(
            SynchronizationAddress.Create(stream.StreamId), HostedHubCreation.Never);
        ownerSyncHub.Should().NotBeNull(
            "the owner hosts one sync/{id} sub-hub per subscriber, and it is what the "
            + "UnsubscribeRequest disposes — without it this test measures nothing");

        return (stream, ownerSyncHub!);
    }
}
