using System;
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
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Issue #3986 — <b>a click the portal has ACCEPTED must reach the owner, whichever way its stream
/// is torn down</b>, pinned in the shape the ordering tests next door could not reach: the click is
/// still SITTING in the client-side <c>sync/{id}</c> hub's queue when the teardown begins, because
/// that hub is busy (a large patch, a binding update) and has not handed the click up yet.
///
/// <para>The busy queue is made deterministic rather than raced: the test parks the sync hub's turn
/// loop, submits the click behind the park through the real sender
/// (<see cref="UserActionSubmission.SubmitUserAction"/>), starts the teardown, and only then lets the
/// click leave. The owner-side click action records whether its own <c>sync/{id}</c> sub-hub had
/// already been told to go when it ran, so BOTH ways of losing the click are red — refused, and
/// run on a handler already being released — and neither needs a timed "nothing happened" window.</para>
///
/// <para><b>The two routes, and what they measured on <c>main</c>:</b>
/// <list type="bullet">
///   <item><see cref="AClickQueuedOnABusySyncHubRunsWhenItsStreamIsReleased"/> — the stream is
///     disposed directly (a navigation, a workspace eviction). GREEN on <c>main</c>: the release is
///     registered on the stream's hub (#4001) and so queues behind the click. Falsified by
///     registering the release on the stream again: <c>found "ran on an owner-side handler already
///     told to go"</c>.</item>
///   <item><see cref="AClickQueuedOnABusySyncHubRunsWhenTheClientHubIsDisposed"/> — the hub that
///     hosts the stream is disposed (the per-circuit portal hub on circuit close). RED on
///     <c>main</c>: <c>refused: Hub sync/… cannot route ClickedEvent to host/1 — its parent hub
///     client/… is shutting down (RunLevel=DisposeHostedHubs)</c>. The portal hub closed its door to
///     its hosted hubs in the very phase in which it asks them to go down, so the click they had
///     accepted never left. Fixed by <c>MessageHub.CarriesAcceptedWorkOfAHostedHub</c>, and each of
///     its two halves (the child's route-up, the parent's intake gate) reds this test on its own
///     when removed.</item>
/// </list></para>
/// </summary>
public class UserActionQueuedBehindABusySyncHubTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "BusySyncHub";
    private const string ButtonArea = Area + "/Button";

    /// <summary>What the owner saw when the click RAN: whether its own <c>sync/{id}</c> sub-hub
    /// had already been told to go.</summary>
    private readonly ReplaySubject<bool> ranOnAHandlerAlreadyToldToGo = new(1);

    private readonly ReplaySubject<string> refusals = new(1);

    // A release INTO the client sync hub's parked turn: a volatile int polled under a bounded
    // SpinWait.SpinUntil, written in a finally so a failing assertion cannot strand the turn.
    private int releasePark;

    private UiControl ClickArea()
        => Controls.Stack.WithView(
            Controls.Html("Run").WithClickAction(ctx =>
            {
                ranOnAHandlerAlreadyToldToGo.OnNext(ctx.Host.Stream.HubIfHeld() is not { IsDisposing: false });
                ranOnAHandlerAlreadyToldToGo.OnCompleted();
                return Task.CompletedTask;
            }), "Button");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddLayout(layout => layout.WithView(Area, ClickArea()));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// The stream is released directly while the click is still queued on its busy sync hub. The
    /// release must order behind the click, and must still reach the owner once the click has run —
    /// "never release" would satisfy the first half alone.
    /// </summary>
    [HubFact]
    public async Task AClickQueuedOnABusySyncHubRunsWhenItsStreamIsReleased()
    {
        var (_, stream, syncHub, ownerSyncHub) = await SubscribedStream();
        try
        {
            ParkTheSyncHub(syncHub, () => false);
            stream.SubmitUserAction(new ClickedEvent(ButtonArea, stream.StreamId), onRefused: refusals.OnNext);
            stream.Dispose();
        }
        finally
        {
            // The release has been handed to whatever route it takes — only NOW may the click leave.
            Volatile.Write(ref releasePark, 1);
        }

        (await Outcome()).Should().Be("ran",
            "a click the portal accepted must reach the owner-side handler before the stream's release does (#3986)");

        await ownerSyncHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "and the release must still reach the owner once the click has run — ordering, never "
            + "holding the owner-side stream open",
            cancellationToken: TestContext.Current.CancellationToken);

        await AssertTheSenderWasAnsweredNotCancelled(syncHub);
    }

    /// <summary>
    /// The hub that HOSTS the stream is disposed while the click is still queued on its busy sync
    /// hub — the per-circuit portal hub on circuit close. The click leaves the sync hub only once the
    /// client hub has entered <see cref="MessageHubRunLevel.DisposeHostedHubs"/>, the latest point at
    /// which a teardown can still find it queued.
    /// </summary>
    [HubFact]
    public async Task AClickQueuedOnABusySyncHubRunsWhenTheClientHubIsDisposed()
    {
        var (client, stream, syncHub, _) = await SubscribedStream();
        try
        {
            ParkTheSyncHub(syncHub, () => client.RunLevel >= MessageHubRunLevel.DisposeHostedHubs);
            stream.SubmitUserAction(new ClickedEvent(ButtonArea, stream.StreamId), onRefused: refusals.OnNext);
            client.Dispose();

            (await Outcome()).Should().Be("ran",
                "a click the portal accepted must reach the owner-side handler even when the portal hub "
                + "that hosts its stream is torn down while the click is still queued (#3986)");
        }
        finally
        {
            Volatile.Write(ref releasePark, 1);
        }

        await AssertTheSenderWasAnsweredNotCancelled(syncHub);
    }

    /// <summary>
    /// The click RAN, and the sender learned so from the owner — its drain ended on the receipt, not
    /// by cancelling a callback it had given up on. A cancelled callback is what tells a person
    /// "your action did not run" about an action that did.
    /// </summary>
    private async Task AssertTheSenderWasAnsweredNotCancelled(IMessageHub syncHub)
    {
        await syncHub.DisposalCompleted.Should().Within(TestTimeouts.Convergence).Emit(
            "the client-side sync hub must finish its teardown once the click's receipt has landed",
            cancellationToken: TestContext.Current.CancellationToken);
        ((MessageHub)syncHub).QuiescingTimedOut.Should().BeFalse(
            "the sender's drain must end because the owner ANSWERED the click — a drain that ran out of "
            + "budget cancels the receipt and reports a refusal for an action that did run. "
            + ((MessageHub)syncHub).QuiescingTimeoutDetail);
    }

    private async Task<string> Outcome()
        => await ranOnAHandlerAlreadyToldToGo
            .Select(toldToGo => toldToGo ? "ran on an owner-side handler already told to go" : "ran")
            .Merge(refusals.Select(sentence => "refused: " + sentence))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the click must either run or be refused — silence is the defect this issue is about",
                cancellationToken: TestContext.Current.CancellationToken);

    private void ParkTheSyncHub(IMessageHub syncHub, Func<bool> alsoReleaseWhen)
        => syncHub.InvokeAsync(() =>
            SpinWait.SpinUntil(
                () => Volatile.Read(ref releasePark) == 1 || alsoReleaseWhen(),
                TestTimeouts.Convergence));

    private async Task<(IMessageHub Client, ISynchronizationStream<JsonElement> Stream, IMessageHub SyncHub, IMessageHub OwnerSyncHub)>
        SubscribedStream()
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));

        await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is not null,
                "the owner-side LayoutAreaHost and its stream-scoped click handler must be live first",
                cancellationToken: TestContext.Current.CancellationToken);

        var syncHub = stream.HubIfHeld();
        syncHub.Should().NotBeNull("the client-side sync hub is the queue the click waits in");
        var ownerSyncHub = GetHost().GetHostedHub(
            SynchronizationAddress.Create(stream.StreamId), HostedHubCreation.Never);
        ownerSyncHub.Should().NotBeNull(
            "the owner hosts one sync/{id} sub-hub per subscriber — the handler the click needs");
        return (client, stream, syncHub!, ownerSyncHub!);
    }
}
