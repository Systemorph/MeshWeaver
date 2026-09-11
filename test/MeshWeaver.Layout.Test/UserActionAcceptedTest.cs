using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Pins the owner-side half of issue #3986. A user action is an observed request: the owner-side
/// <c>sync/{id}</c> handler invokes the action and only then answers
/// <see cref="UserActionAccepted"/>. The Blazor sender can therefore let the ordinary hub quiesce
/// drain this receipt instead of releasing the stream while the click is still in flight.
/// </summary>
public class UserActionAcceptedTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "ActionReceipt";
    private const string ButtonArea = Area + "/Button";
    private const string BlurArea = Area + "/Blur";
    private const string DialogArea = Area + "/Dialog";
    private readonly AsyncSubject<long> clickInvoked = new();
    private readonly AsyncSubject<long> blurInvoked = new();
    private readonly AsyncSubject<long> closeInvoked = new();
    private long ordering;

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(
                Area,
                Controls.Stack.WithView(
                    Controls.Button("Run").WithClickAction(_ =>
                    {
                        Signal(clickInvoked);
                    }),
                    "Button")
                    .WithView(
                        Controls.Text("draft").WithBlurAction(_ => Signal(blurInvoked)),
                        "Blur")
                    .WithView(
                        Controls.Dialog("Body").WithCloseAction(_ => Signal(closeInvoked)),
                        "Dialog")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [HubFact]
    public async Task ClickReceipt_IsPostedOnlyAfterTheStreamScopedActionWasInvoked()
        => await ReceiptFollowsInvocation(
            ButtonArea,
            streamId => new ClickedEvent(ButtonArea, streamId),
            clickInvoked);

    [HubFact]
    public async Task BlurReceipt_IsPostedOnlyAfterTheStreamScopedActionWasInvoked()
        => await ReceiptFollowsInvocation(
            BlurArea,
            streamId => new BlurEvent(BlurArea, streamId),
            blurInvoked);

    [HubFact]
    public async Task DialogCloseReceipt_IsPostedOnlyAfterTheQueuedCloseActionWasInvoked()
        => await ReceiptFollowsInvocation(
            DialogArea,
            streamId => new CloseDialogEvent(DialogArea, streamId, DialogCloseState.OK),
            closeInvoked);

    private async Task ReceiptFollowsInvocation(
        string controlArea,
        Func<string, IUserAction> action,
        AsyncSubject<long> invoked)
    {
        var client = GetClient();
        var reference = new LayoutAreaReference(Area);
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        await stream.GetControlStream(controlArea).Should().Within(10.Seconds()).Match(
            control => control is not null,
            "the owner-side LayoutAreaHost and its stream-scoped action handler must exist");

        long receiptOrder = 0;
        var receipt = await client
            .Observe<UserActionAccepted>(
                action(stream.StreamId),
                options => options.WithTarget(CreateHostAddress()))
            .Do(_ => receiptOrder = Interlocked.Increment(ref ordering))
            .Should().Within(10.Seconds()).Emit(
                "an accepted user action must answer the callback the sender uses as its teardown drain");

        receipt.Message.Should().BeOfType<UserActionAccepted>();
        var invocationOrder = await invoked.Should().Within(10.Seconds()).Emit(
            "the receipt cannot precede invocation of the stream-scoped action it acknowledges");
        receiptOrder.Should().BeGreaterThan(invocationOrder,
            "the owner must not release the sender's quiesce drain before its handler accepts the action");
    }

    private Task Signal(AsyncSubject<long> invoked)
    {
        invoked.OnNext(Interlocked.Increment(ref ordering));
        invoked.OnCompleted();
        return Task.CompletedTask;
    }

    [Fact]
    public void EveryUserActionCarriesTheSameReceiptContract()
    {
        new ClickedEvent("a", "s").Should().BeAssignableTo<IRequest<UserActionAccepted>>();
        new BlurEvent("a", "s").Should().BeAssignableTo<IRequest<UserActionAccepted>>();
        new CloseDialogEvent("a", "s", DialogCloseState.OK)
            .Should().BeAssignableTo<IRequest<UserActionAccepted>>();
    }
}
