using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// 🚨 Closing a dialog ALWAYS clears its store entry — with or without a <c>CloseAction</c>.
/// A closable dialog without one used to stay in the store, so a later dialog of the same shape was
/// Equal to the stale entry, changed nothing on the client and showed nothing.
/// </summary>
public class ClosedDialogLeavesTheStoreTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "ClosedDialog";
    private const string OpenPlain = Area + "/OpenPlain";
    private const string OpenWithAction = Area + "/OpenWithAction";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(
                Area,
                Controls.Stack
                    .WithView(
                        Controls.Button("Open").WithClickAction(ctx =>
                        {
                            // The SAME shape every time: equal to the entry a close left behind.
                            ctx.Host.UpdateArea(DialogControl.DialogArea, Controls.Dialog("Body", "Title"));
                            return Task.CompletedTask;
                        }),
                        "OpenPlain")
                    .WithView(
                        Controls.Button("Open2").WithClickAction(ctx =>
                        {
                            ctx.Host.UpdateArea(DialogControl.DialogArea,
                                Controls.Dialog("Body2", "Title2").WithCloseAction(_ => { }));
                            return Task.CompletedTask;
                        }),
                        "OpenWithAction")));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    [HubFact]
    public async Task ADialogClosedWithoutACloseAction_IsClearedAndAnEqualOneRendersAgain()
        => await OpenCloseOpen(OpenPlain);

    [HubFact]
    public async Task ADialogClosedWithACloseAction_IsAlsoCleared()
        => await OpenCloseOpen(OpenWithAction);

    private async Task OpenCloseOpen(string openButton)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var stream = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(Area));
        await stream.GetControlStream(openButton).Should().Within(10.Seconds())
            .Match(c => c is not null, "the host must render", cancellationToken: ct);

        async Task Open()
        {
            await client.Observe<UserActionAccepted>(
                    new ClickedEvent(openButton, stream.StreamId), o => o.WithTarget(CreateHostAddress()))
                .Should().Within(10.Seconds()).Emit("the click is accepted", cancellationToken: ct);
            await stream.GetControlStream(DialogControl.DialogArea).Should().Within(10.Seconds())
                .Match(c => c is DialogControl, "the dialog must reach the store", cancellationToken: ct);
        }

        await Open();
        await client.Observe<UserActionAccepted>(
                new CloseDialogEvent(DialogControl.DialogArea, stream.StreamId, DialogCloseState.OK),
                o => o.WithTarget(CreateHostAddress()))
            .Should().Within(10.Seconds()).Emit("the close is accepted", cancellationToken: ct);
        await stream.GetControlStream(DialogControl.DialogArea).Should().Within(10.Seconds())
            .Match(c => c is null, "closing a dialog clears its store entry", cancellationToken: ct);

        await Open(); // an equal-shaped dialog renders again
    }
}
