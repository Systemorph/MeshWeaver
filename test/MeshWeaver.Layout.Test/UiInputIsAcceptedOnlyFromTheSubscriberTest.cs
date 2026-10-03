using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// A layout area is rendered once per subscriber, and its input is accepted only from that
/// subscriber: a click, a blur, a dialog dismissal or an edited value addressed to the stream is
/// handled only when the delivery carries the identity the stream was subscribed under. Any other
/// identity — a person, the platform identity, or none — is answered with a
/// <see cref="ErrorType.Forbidden"/> <see cref="DeliveryFailure"/> and nothing runs.
///
/// <para>Every case ends on a POSITIVE terminal. The refusal is an answer, and the subscriber's own
/// input — sent afterwards on the same route, so it is handled after the refused one — is the
/// control: once it has been accepted, "the action ran exactly once" and "the refused value is not
/// in the data" are read off settled state, with no window spent waiting for nothing to happen.</para>
/// </summary>
public class UiInputIsAcceptedOnlyFromTheSubscriberTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string Area = "SubscriberInput";
    private const string ButtonArea = Area + "/Button";
    private const string BlurArea = Area + "/Blur";
    private const string DialogArea = Area + "/Dialog";
    private const string EditorArea = "SubscriberInputEditor";
    private const string DataId = "subscriberInputForm";

    private static readonly AccessContext Subscriber = new() { ObjectId = "subscriber-of-the-stream", Name = "Subscriber" };
    private static readonly AccessContext Other = new() { ObjectId = "another-identity", Name = "Other" };

    private readonly AsyncSubject<LayoutAreaHost> owner = new();
    private int clicks;
    private int blurs;
    private int closes;

    /// <summary>The edited record: two fields, so one render names both writes.</summary>
    public record Form
    {
        /// <summary>Written by the refused edit.</summary>
        public double X { get; init; }
        /// <summary>Written by the subscriber's own edit.</summary>
        public double Y { get; init; }
    }

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout
                .WithView(Area, (host, _) =>
                {
                    owner.OnNext(host);
                    owner.OnCompleted();
                    return Controls.Stack
                        .WithView(
                            Controls.Button("Run").WithClickAction(_ => { Interlocked.Increment(ref clicks); }),
                            "Button")
                        .WithView(
                            Controls.Text("draft").WithBlurAction(_ => Count(ref blurs)),
                            "Blur")
                        .WithView(
                            Controls.Dialog("Body").WithCloseAction(_ => { Interlocked.Increment(ref closes); }),
                            "Dialog");
                })
                .WithView(EditorArea, (host, _) => Controls.Stack
                    .WithView(host.Hub.ServiceProvider.Edit(Observable.Return(new Form()), DataId))
                    .WithView((h, _) => h.Stream.GetDataStream<Form>(DataId)
                        .Select(f => (UiControl)Controls.Markdown($"{f.X}|{f.Y}")))));

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    private static Task Count(ref int counter)
    {
        Interlocked.Increment(ref counter);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The three person-actions, each from an identity that is not the subscriber: refused, and the
    /// control's action does not run. The subscriber's own action on the same stream then runs.
    /// </summary>
    [Theory]
    [InlineData("click", "other")]
    [InlineData("click", "none")]
    [InlineData("click", "platform")]
    [InlineData("blur", "other")]
    [InlineData("blur", "none")]
    [InlineData("blur", "platform")]
    [InlineData("close", "other")]
    [InlineData("close", "none")]
    [InlineData("close", "platform")]
    public async Task AUserActionIsAcceptedOnlyFromTheSubscriber(string kind, string sender)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var stream = OpenAsSubscriber(client, Area);
        await stream.GetControlStream(ControlAreaOf(kind)).Should().Within(TestTimeouts.Convergence)
            .Match(control => control is not null, "the area is rendered for its subscriber", cancellationToken: ct);

        var refusal = await Answer(client, ActionOf(kind, stream.StreamId), CreateHostAddress(), sender)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("an action that is not the subscriber's is answered, not dropped", cancellationToken: ct);
        Assert.NotNull(refusal); // an action from an identity other than the stream's subscriber is refused
        refusal.ErrorType.Should().Be(ErrorType.Forbidden);
        refusal.Message.Should().NotBeNullOrEmpty("the sender is told its action did not run");

        // The same refusal reaches a view, which submits from its own end of the stream.
        if (sender == "other")
            (await Submit(stream, ActionOf(kind, stream.StreamId), Other)
                    .Should().Within(TestTimeouts.Convergence)
                    .Emit("a view's action that is not the subscriber's is answered", cancellationToken: ct))
                .Should().NotBeNullOrEmpty("the view is given the sentence to show");

        // The control, and the terminal the count is read after: the subscriber's own action, sent
        // the way a view sends it, is accepted — and its receipt follows the handler.
        var accepted = await Submit(stream, ActionOf(kind, stream.StreamId), Subscriber)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the subscriber's own action is answered", cancellationToken: ct);
        accepted.Should().BeNull("the subscriber's own action is accepted");
        InvocationsOf(kind).Should().Be(1,
            "the control's action runs for the subscriber's action and for no other");
    }

    /// <summary>
    /// The same rule holds when the action is posted on the stream's own hub rather than routed to
    /// it through the owner: the check sits on the stream, not on the route to it.
    /// </summary>
    [HubFact]
    public async Task AUserActionPostedOnTheStreamsOwnHubIsAcceptedOnlyFromTheSubscriber()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var stream = OpenAsSubscriber(client, Area);
        await stream.GetControlStream(ButtonArea).Should().Within(TestTimeouts.Convergence)
            .Match(control => control is not null, cancellationToken: ct);
        var host = await owner.Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        var streamHub = host.Stream.Hub;

        var refusal = await Answer(streamHub, new ClickedEvent(ButtonArea, host.Stream.ClientId), streamHub.Address, "other")
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        Assert.NotNull(refusal); // refused however the action reaches the stream
        refusal.ErrorType.Should().Be(ErrorType.Forbidden);

        var accepted = await Answer(streamHub, new ClickedEvent(ButtonArea, host.Stream.ClientId), streamHub.Address, "subscriber")
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        accepted.Should().BeNull("the subscriber's own action is accepted on the same hub");
        Volatile.Read(ref clicks).Should().Be(1);
    }

    /// <summary>
    /// An edited value is input too: a data change addressed to the stream is applied only when it
    /// carries the subscriber's identity, and the sender of a refused one is answered.
    /// </summary>
    [HubFact]
    public async Task AnEditedValueIsAcceptedOnlyFromTheSubscriber()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = GetClient();
        var failures = new ReplaySubject<DeliveryFailure>();
        using var registration = client.Register<DeliveryFailure>(delivery =>
        {
            failures.OnNext(delivery.Message);
            return delivery.Processed();
        });
        var stream = OpenAsSubscriber(client, EditorArea);

        var control = await stream.GetControlStream(EditorArea)
            .Should().Within(TestTimeouts.Convergence).Match(x => x is not null, cancellationToken: ct);
        var stack = control.Should().BeOfType<StackControl>().Subject;
        var editor = (await stream.GetControlStream(stack.Areas.First().Area.ToString()!)
                .Should().Within(TestTimeouts.Convergence).Match(x => x is not null, cancellationToken: ct))
            .Should().BeOfType<EditorControl>().Subject;
        var resultArea = stack.Areas.Last().Area.ToString()!;
        await stream.GetControlStream(resultArea)
            .Should().Within(TestTimeouts.Convergence).Match(x => x is MarkdownControl, cancellationToken: ct);

        // The wire message a client posts for an edit, under an identity that is not the subscriber's.
        var applied = stream.Current;
        Assert.NotNull(applied);
        client.Post(
            new PatchDataChangeRequest(
                stream.StreamId,
                applied.Version,
                new RawJson(JsonSerializer.Serialize(new[]
                {
                    new { op = "replace", path = $"{editor.DataContext}/x", value = 5 }
                })),
                ChangeType.Patch,
                stream.ClientId),
            o => o.WithTarget(CreateHostAddress()).WithAccessContext(Other));

        var refusal = await failures.Where(f => f.Delivery.Message is PatchDataChangeRequest)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the sender of an edit the stream does not accept is told so", cancellationToken: ct);
        refusal.ErrorType.Should().Be(ErrorType.Forbidden);

        // The control: the subscriber's own edit, through the client's stream, is applied — and the
        // render it produces shows the refused value was never written.
        stream.UpdatePointer(7, editor.DataContext, new JsonPointerReference("y"));
        var rendered = await stream.GetControlStream(resultArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(x => x is MarkdownControl markdown && $"{markdown.Markdown}".EndsWith("|7"),
                "the subscriber's own edit is applied", cancellationToken: ct);
        $"{rendered.Should().BeOfType<MarkdownControl>().Subject.Markdown}".Should().Be("0|7",
            "only the subscriber's edit reached the data");
    }

    private ISynchronizationStream<JsonElement> OpenAsSubscriber(IMessageHub client, string area)
    {
        // The subscriber's identity is ambient only while the stream is opened, as on a request.
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using (access.SwitchAccessContext(Subscriber))
            return client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
                CreateHostAddress(), new LayoutAreaReference(area));
    }

    /// <summary>The owner's answer to <paramref name="action"/>: <c>null</c> when accepted, the failure when refused.</summary>
    private static IObservable<DeliveryFailure?> Answer(
        IMessageHub poster, IUserAction action, Address target, string sender)
        => poster
            .Observe<UserActionAccepted>(action, options => sender switch
            {
                "subscriber" => options.WithTarget(target).WithAccessContext(Subscriber),
                "other" => options.WithTarget(target).WithAccessContext(Other),
                "none" => options.WithTarget(target).WithAccessContext(new AccessContext()),
                // No identity stated: the fixture's hubs post as the platform identity.
                _ => options.WithTarget(target),
            })
            .Select(_ => (DeliveryFailure?)null)
            .Catch((DeliveryFailureException refused) => Observable.Return<DeliveryFailure?>(refused.Failure));

    /// <summary>
    /// The answer through <c>SubmitUserAction</c> — the sender a view uses: <c>null</c> when
    /// accepted, the refusal sentence otherwise.
    /// </summary>
    private static IObservable<string?> Submit(
        ISynchronizationStream<JsonElement> stream, IUserAction action, AccessContext actingUser)
        => Observable.Create<string?>(observer => stream.SubmitUserAction(
            action,
            actingUser,
            onRefused: sentence =>
            {
                observer.OnNext(sentence);
                observer.OnCompleted();
            },
            onAccepted: () =>
            {
                observer.OnNext(null);
                observer.OnCompleted();
            }));

    private static string ControlAreaOf(string kind) => kind switch
    {
        "click" => ButtonArea,
        "blur" => BlurArea,
        _ => DialogArea,
    };

    private static IUserAction ActionOf(string kind, string streamId) => kind switch
    {
        "click" => new ClickedEvent(ButtonArea, streamId),
        "blur" => new BlurEvent(BlurArea, streamId),
        _ => new CloseDialogEvent(DialogArea, streamId, DialogCloseState.OK),
    };

    private int InvocationsOf(string kind) => kind switch
    {
        "click" => Volatile.Read(ref clicks),
        "blur" => Volatile.Read(ref blurs),
        _ => Volatile.Read(ref closes),
    };
}
