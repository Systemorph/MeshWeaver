#pragma warning disable CS1591

using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// MeshWeaver#4692 — a durable activity action must still be there after the activity's hub is gone.
///
/// <para><b>The defect this closes.</b> A control a script returns lives in the execution hub's
/// area dictionary, which is empty again once that hub unloads; the activity page of an old run
/// therefore cannot reopen what the run produced. An <see cref="ActivityActionReference"/> is
/// stored on the activity node instead, as plain data.</para>
///
/// <para><b>What is observed.</b> A real activity node on a real monolith mesh. The reference is
/// recorded while the activity runs, a later terminal write that knows nothing about actions lands
/// on top of it, and the activity's per-node hub is then RECYCLED — the next read re-creates the
/// hub from the stored node, which is the "execution hub unloaded" condition. The reference is
/// read back from that fresh activation.</para>
/// </summary>
public class ActivityActionReferenceDurabilityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Partition = "TestData";

    private static readonly ActivityActionReference OpenCard = new()
    {
        Id = "open-card",
        Label = "Open QR card",
        LabelKey = "sample.openCard",
        Target = "/a/6f1c2d3e/card",
        ResultPath = "TestData/Links/6f1c2d3e",
        HandlerKey = "store.coupon-journey",
        HandlerVersion = 1,
    };

    private async Task<string> ARunningActivity()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var id = "action" + Guid.NewGuid().ToString("N")[..8];
        await meshService.CreateNode(new MeshNode(id, $"{Partition}/_Activity")
        {
            Name = "durable action reference probe",
            NodeType = ActivityNodeType.NodeType,
            MainNode = Partition,
            State = MeshNodeState.Active,
            Content = new ActivityLog(ActivityCategory.DataUpdate)
            {
                Id = id,
                HubPath = Partition,
                Status = ActivityStatus.Running,
            },
        }).FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        return $"{Partition}/_Activity/{id}";
    }

    private async Task<ActivityLog> Read(string path)
    {
        var node = await RequestHub.GetMeshNode(path).Where(n => n is not null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await();
        return node!.ContentAs<ActivityLog>(RequestHub.JsonSerializerOptions)
               ?? throw new InvalidOperationException($"{path} did not read back as an ActivityLog.");
    }

    [Fact]
    public async Task ARecordedAction_SurvivesALaterTerminalWrite_AndTheActivityHubBeingRecycled()
    {
        var path = await ARunningActivity();

        await Mesh.RecordActivityAction(path, OpenCard)
            .LastAsync().Timeout(TestTimeouts.Convergence).Await();

        // The run finishes through the ordinary writer, which patches only the fields it owns.
        await ActivityLogAppender.Append(Mesh, path,
                [new LogMessage("done", Microsoft.Extensions.Logging.LogLevel.Information)],
                log => log with { Status = ActivityStatus.Succeeded, End = DateTime.UtcNow })
            .LastAsync().Timeout(TestTimeouts.Convergence).Await();

        (await Read(path)).Actions.Should().ContainSingle(
            "negative control for the recycle below: the action is on the node BEFORE the hub goes away");

        await RequestHub.RecycleNode(path, reason: "ActivityActionReferenceDurabilityTest: unload the activity hub")
            .Should().Within(TestTimeouts.Convergence).Emit("the activity hub is torn down");

        var reopened = await Read(path);

        reopened.Status.Should().Be(ActivityStatus.Succeeded, "the terminal write is intact");
        var action = reopened.Actions.Should().ContainSingle(
            "the reference is stored data — it needs neither the execution hub nor its kernel").Subject;
        action.Id.Should().Be(OpenCard.Id);
        action.Label.Should().Be(OpenCard.Label);
        action.LabelKey.Should().Be(OpenCard.LabelKey);
        action.Target.Should().Be(OpenCard.Target);
        action.ResultPath.Should().Be(OpenCard.ResultPath);
        action.HandlerKey.Should().Be(OpenCard.HandlerKey);
        action.HandlerVersion.Should().Be(1);
        action.Availability().Should().Be(ActivityActionAvailability.Available,
            "the structural verdict is the same in the run that wrote the reference and after it is re-read");
    }

    [Fact]
    public async Task RecordingTheSameActionAgain_ReplacesIt_OnTheStoredActivity()
    {
        var path = await ARunningActivity();

        await Mesh.RecordActivityAction(path, OpenCard)
            .LastAsync().Timeout(TestTimeouts.Convergence).Await();
        await Mesh.RecordActivityAction(path, OpenCard with { Target = "/a/6f1c2d3e/card?rev=2" })
            .LastAsync().Timeout(TestTimeouts.Convergence).Await();

        var actions = (await Read(path)).Actions;
        actions.Should().ContainSingle("a retried publication must not list its action twice");
        actions[0].Target.Should().Be("/a/6f1c2d3e/card?rev=2");
    }

    [Fact]
    public void AReferenceThatCouldNotBeOffered_IsRefusedAtTheWrite()
    {
        var offSite = Assert.Throws<ArgumentException>(() => Mesh.RecordActivityAction("TestData/_Activity/x",
            OpenCard with { Target = "https://evil.example/card" }));
        var foreignKind = Assert.Throws<ArgumentException>(() => Mesh.RecordActivityAction("TestData/_Activity/x",
            OpenCard with { Kind = "RunScript" }));

        offSite.Message.Should().Contain(ActivityActionAvailability.UnsafeTarget,
            "an absolute target is never persisted for a later reader to follow");
        foreignKind.Message.Should().Contain(ActivityActionAvailability.UnsupportedKind);
    }
}
