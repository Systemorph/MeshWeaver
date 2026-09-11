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
