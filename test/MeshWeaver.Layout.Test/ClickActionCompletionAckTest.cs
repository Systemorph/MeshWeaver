using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the owner-side half of the framework button's PENDING state (<c>Doc/GUI/ButtonPendingState</c>):
/// the <see cref="UserActionAccepted"/> receipt for a click is answered when the click action is DONE —
/// its returned observable completed — and a failing action answers a <see cref="DeliveryFailure"/>
/// carrying the error, so the client can restore the button AND say why. Before this, the receipt was
/// posted the moment the action was invoked, whatever it returned, so a pending indicator would have
/// cleared before the work it stood for had happened, and a later fault reached nobody.
/// </summary>
public class ClickActionCompletionAckTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "ClickAck";
    private const string ReactiveArea = Area + "/Reactive";
    private const string FailingArea = Area + "/Failing";
    private const string ThrowingArea = Area + "/Throwing";
    private const string NavigatingArea = Area + "/Navigating";
    private const string ReRenderingArea = Area + "/ReRendering";
    private const string AcceptedTarget = "/Hosting/Actions/a1/Execution";

    private readonly Subject<Unit> reactiveWork = new();
    private readonly Subject<Unit> failingWork = new();
    private readonly AsyncSubject<Unit> failingInvoked = new();
    private readonly Subject<Unit> reRenderingWork = new();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(
                Area,
                Controls.Stack
                    .WithView(Controls.Button("Approve").WithReactiveClickAction(_ => reactiveWork.AsObservable()), "Reactive")
                    .WithView(Controls.Button("Fail").WithReactiveClickAction(_ => Observable.Defer(() =>
                    {
                        // Subscribed FIRST, then signalled: the test errors the work only once the
                        // owner is listening, so the refusal can only come from the completion arm.
                        var work = failingWork.AsObservable();
                        return Observable.Create<Unit>(observer =>
                        {
                            var subscription = work.Subscribe(observer);
                            failingInvoked.OnNext(Unit.Default);
                            failingInvoked.OnCompleted();
                            return subscription;
                        });
                    })), "Failing")
                    .WithView(Controls.Button("Throw").WithClickAction(ThrowSynchronously), "Throwing")
                    .WithView(Controls.Button("Go")
                        .WithReactiveClickAction(_ => Observable.Return(Unit.Default))
                        .WithNavigateOnAccepted(AcceptedTarget), "Navigating")
                    .WithView(Controls.Button("Approve and hide")
                        .WithReactiveClickAction(ctx =>
                        {
                            // What an approval does to its own page: the write re-renders the area
                            // that held the button (here: its parent), BEFORE the write's confirmation arrives.
                            ctx.Host.UpdateArea(Area, Controls.Markdown("approved"));
                            return reRenderingWork.AsObservable();
                        }), "ReRendering")));

    private static Task ThrowSynchronously(UiActionContext _) =>
        throw new InvalidOperationException("synchronous click failure");

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [HubFact]
    public async Task ReceiptWaitsForTheReactiveClickActionToComplete()
    {
        var stream = await OpenStream(ReactiveArea);
        var accepted = new AsyncSubject<Unit>();
        var refused = new AsyncSubject<string>();

        stream.SubmitUserAction(
            new ClickedEvent(ReactiveArea, stream.StreamId),
            actingUser: null,
            onRefused: sentence => { refused.OnNext(sentence); refused.OnCompleted(); },
            onAccepted: () => { accepted.OnNext(Unit.Default); accepted.OnCompleted(); });

        await accepted.Should().NotEmit(500.Milliseconds(),
            "the click action's observable is still open, so the button it belongs to must stay pending",
            TestContext.Current.CancellationToken);

        reactiveWork.OnNext(Unit.Default);
        reactiveWork.OnCompleted();

        await accepted.Should().Within(10.Seconds()).Emit(
            "completing the action's observable is what DONE means, and it must release the pending state",
            TestContext.Current.CancellationToken);
        await refused.Should().NotEmit(200.Milliseconds(),
            "an accepted click is never also refused", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 🚨 The click action's subscription belongs to the HOST, not to the clicked area: a click whose
    /// own effect re-renders (clears) the area that held the button must still be answered when its
    /// work completes. Owned by the area, the re-render would dispose it and strand the button in
    /// its pending state forever (review of MeshWeaver#5783).
    /// </summary>
    [HubFact]
    public async Task AClickWhoseActionClearsItsOwnAreaIsStillAnswered()
    {
        var stream = await OpenStream(ReRenderingArea);
        var accepted = new AsyncSubject<Unit>();

        stream.SubmitUserAction(
            new ClickedEvent(ReRenderingArea, stream.StreamId),
            actingUser: null,
            onRefused: null,
            onAccepted: () => { accepted.OnNext(Unit.Default); accepted.OnCompleted(); });

        await stream.GetControlStream(Area).Should().Within(10.Seconds()).Match(
            c => c is MarkdownControl,
            "the click's own effect re-rendered the page and cleared the button's area", TestContext.Current.CancellationToken);

        reRenderingWork.OnNext(Unit.Default);
        reRenderingWork.OnCompleted();

        await accepted.Should().Within(10.Seconds()).Emit(
            "the re-render must not have released the pending click — its completion still answers",
            TestContext.Current.CancellationToken);
    }

    [HubFact]
    public async Task AFailingClickActionIsRefusedWithItsError()
    {
        var stream = await OpenStream(FailingArea);
        var accepted = new AsyncSubject<Unit>();
        var refused = new AsyncSubject<string>();

        stream.SubmitUserAction(
            new ClickedEvent(FailingArea, stream.StreamId),
            actingUser: null,
            onRefused: sentence => { refused.OnNext(sentence); refused.OnCompleted(); },
            onAccepted: () => { accepted.OnNext(Unit.Default); accepted.OnCompleted(); });

        await failingInvoked.Should().Within(10.Seconds()).Emit(
            "the owner subscribes to the click action's work", TestContext.Current.CancellationToken);
        await accepted.Should().NotEmit(300.Milliseconds(),
            "the work has neither completed nor failed yet", TestContext.Current.CancellationToken);
        failingWork.OnError(new InvalidOperationException("approval write was rejected"));

        var sentence = await refused.Should().Within(10.Seconds()).Emit(
            "a failed click must restore the button with the reason, never silently",
            TestContext.Current.CancellationToken);
        sentence.Should().Contain("approval write was rejected");
        await accepted.Should().NotEmit(200.Milliseconds(),
            "a refused click is never also accepted", TestContext.Current.CancellationToken);
    }

    [HubFact]
    public async Task ASynchronouslyThrowingClickActionIsRefusedWithItsError()
    {
        var stream = await OpenStream(ThrowingArea);
        var refused = new AsyncSubject<string>();

        stream.SubmitUserAction(
            new ClickedEvent(ThrowingArea, stream.StreamId),
            actingUser: null,
            onRefused: sentence => { refused.OnNext(sentence); refused.OnCompleted(); },
            onAccepted: null);

        var sentence = await refused.Should().Within(10.Seconds()).Emit(
            "a click action that throws before returning must still answer the pending button",
            TestContext.Current.CancellationToken);
        sentence.Should().Contain("synchronous click failure");
    }

    [HubFact]
    public async Task NavigateOnAcceptedTravelsToTheClientWithTheButton()
    {
        var stream = await OpenStream(NavigatingArea);

        var control = await stream.GetControlStream(NavigatingArea).Should().Within(10.Seconds()).Match(
            c => c is ButtonControl,
            "the button is rendered", TestContext.Current.CancellationToken);

        var button = (ButtonControl)control!;
        button.IsClickable.Should().BeTrue();
        button.NavigateOnAccepted.Should().Be(AcceptedTarget,
            "the client navigates to this target the moment the click is acknowledged");
    }

    private async Task<ISynchronizationStream<JsonElement>> OpenStream(string controlArea)
    {
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));

        await stream.GetControlStream(controlArea).Should().Within(10.Seconds()).Match(
            control => control is not null,
            "the owner-side LayoutAreaHost and its stream-scoped click handler must exist",
            cancellationToken: TestContext.Current.CancellationToken);
        return stream;
    }
}
