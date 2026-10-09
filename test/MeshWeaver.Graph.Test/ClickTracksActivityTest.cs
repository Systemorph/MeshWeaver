using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// A click that starts an ACTIVITY keeps its control busy over that activity
/// (<c>ctx.TrackActivity</c>, <c>Doc/GUI/ButtonPendingState</c>): the activity's latest message is the
/// status line, Cancel patches the activity's <c>RequestedStatus</c> (the activity control plane —
/// never a verb message) and the click settles only when the activity itself reports Cancelled.
/// <para>Negative control: before Cancel is pressed the activity's <c>RequestedStatus</c> is unset, so
/// the patch observed afterwards can only have come from the click's Cancel.</para>
/// </summary>
public class ClickTracksActivityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PageType = "ClickActivityProbeType";
    private const string Page = "ClickActivityProbe";
    private const string Area = "Probe";
    private const string StartArea = Area + "/start";

    private readonly string activityPath = $"{TestPartition}/_Activity/click{Guid.NewGuid():N}";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddMeshNodes(
            new MeshNode(PageType)
            {
                HubConfiguration = config => config.AddLayout(layout => layout.WithView(Area, (_, _) =>
                    Controls.Stack.WithView(
                        Controls.Button("Run").WithClickAction(ctx =>
                        {
                            ctx.TrackActivity(activityPath);
                            return Task.CompletedTask;
                        }),
                        "start")))
            },
            new MeshNode(Page) { NodeType = PageType });

    [Fact(Timeout = 120000)]
    public async Task TheClickShowsTheActivityAndCancelsItThroughItsControlPlane()
    {
        var ct = TestContext.Current.CancellationToken;
        var mesh = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var segments = activityPath.Split('/');
        await mesh.CreateNode(new MeshNode(segments[^1], string.Join('/', segments[..^1]))
        {
            NodeType = ActivityNodeType.NodeType,
            Name = "probe run",
            MainNode = TestPartition,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.Import)
            {
                Id = segments[^1], Status = ActivityStatus.Running,
                Messages = [new LogMessage("Copying 3 of 8", LogLevel.Information)],
            },
        }).Timeout(TestTimeouts.Convergence).Await(ct);

        var stream = GetClient().GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            new Address(Page), new LayoutAreaReference(Area));
        await stream.GetControlStream(StartArea).Should().Within(TestTimeouts.Convergence)
            .Match(c => c is ButtonControl, "the page renders its button", cancellationToken: ct);

        var answer = new ReplaySubject<string?>(1);
        stream.SubmitUserAction(new ClickedEvent(StartArea, stream.StreamId), actingUser: null,
            onRefused: answer.OnNext, onAccepted: () => answer.OnNext(null));
        (await answer.Should().Within(TestTimeouts.Convergence).Emit("the click is answered", ct)).Should().BeNull();

        var progress = stream.GetDataStream<ClickProgress>(new JsonPointerReference(ClickProgress.PointerFor(StartArea)));
        var busy = await progress.Should().Within(TestTimeouts.Convergence).Match(
            p => p is { Running: true, Status: "Copying 3 of 8" },
            "the control stays busy over the activity and shows its latest message", ct);
        busy!.ActivityPath.Should().Be(activityPath);

        var activity = Mesh.GetMeshNodeStream(activityPath).Select(n => n?.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions));
        (await activity.Should().Within(TestTimeouts.Convergence).Match(a => a is not null, "the activity exists", ct))!
            .RequestedStatus.Should().BeNull("negative control: nobody asked to cancel yet");

        stream.UpdatePointer(true, ClickProgress.CancelPointerFor(StartArea), new JsonPointerReference("requested"));

        await activity.Should().Within(TestTimeouts.Convergence).Match(
            a => a is { RequestedStatus: ActivityStatus.Cancelled },
            "Cancel patches the activity's RequestedStatus through its control plane", ct);
        await progress.Should().Within(TestTimeouts.Convergence).Match(
            p => p is { Running: true, Cancelling: true },
            "the click keeps watching the activity until it reports the cancel", ct);

        // What the activity's owner does when it honours the request.
        await Mesh.GetMeshNodeStream(activityPath).Update(node => node with
        {
            Content = node.ContentAs<ActivityLog>(Mesh.JsonSerializerOptions)! with { Status = ActivityStatus.Cancelled },
        }).Take(1).Timeout(TestTimeouts.Convergence).Await(ct);

        await progress.Should().Within(TestTimeouts.Convergence).Match(
            p => p is { Running: false, Summary: "Cancelled" },
            "the activity's Cancelled status settles the click and re-enables the control", ct);
    }
}
