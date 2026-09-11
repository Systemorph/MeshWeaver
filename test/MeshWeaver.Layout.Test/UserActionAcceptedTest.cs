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
    private readonly AsyncSubject<string> invoked = new();

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(
                Area,
                Controls.Stack.WithView(
                    Controls.Button("Run").WithClickAction(_ =>
                    {
                        invoked.OnNext(ButtonArea);
                        invoked.OnCompleted();
                    }),
                    "Button")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [HubFact]
    public async Task ClickReceipt_IsPostedOnlyAfterTheStreamScopedActionWasInvoked()
    {
        var client = GetClient();
        var reference = new LayoutAreaReference(Area);
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        await stream.GetControlStream(ButtonArea).Should().Within(10.Seconds()).Match(
            control => control is ButtonControl,
            "the owner-side LayoutAreaHost and its stream-scoped ClickedEvent handler must exist");

        var receipt = await client
            .Observe<UserActionAccepted>(
                new ClickedEvent(ButtonArea, stream.StreamId),
                options => options.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit(
                "an accepted click must answer the callback the sender uses as its teardown drain");

        receipt.Message.Should().BeOfType<UserActionAccepted>();
        await invoked.Should().Within(10.Seconds()).Emit(
            "the receipt cannot precede invocation of the stream-scoped action it acknowledges");
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
