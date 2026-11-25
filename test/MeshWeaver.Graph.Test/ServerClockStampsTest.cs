using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>THE OWNER'S CLOCK STAMPS A RECORD — A CALLER CANNOT BACKDATE IT.</b>
/// A person or service that creates a node (single or bulk) gets <c>createdDate</c> and
/// <c>lastModified</c> from the owning hub's clock; carried values are ignored. Only the platform
/// (a GitSync import, a repair) preserves a carried stamp, so history survives an import. An
/// append-only consumer (TimeTracking's newest save wins by createdDate) relies on this.
/// </summary>
public class ServerClockStampsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly DateTimeOffset Backdated = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string NewId() => "sc" + Guid.NewGuid().ToString("N")[..8];

    private static MeshNode Page(string id, string ns, bool backdate) => new(id, ns)
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        CreatedDate = backdate ? Backdated : default,
        LastModified = backdate ? Backdated : default,
        Content = new MarkdownContent { Content = "# page" },
    };

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    [Fact(Timeout = 180000)]
    public async Task PersonCreate_WithBackdatedStamps_GetsTheServerTime()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var created = await MeshService.CreateNode(Page(NewId(), TestPartition, backdate: true)).Take(1)
            .Should().Within(60.Seconds()).Emit("the create lands",
                cancellationToken: TestContext.Current.CancellationToken);

        created.CreatedDate.Should().BeAfter(before, "a person cannot backdate createdDate");
        created.LastModified.Should().BeAfter(before, "a person cannot backdate lastModified");
    }

    [Fact(Timeout = 180000)]
    public async Task PersonUpsertCreate_WithBackdatedStamps_GetsTheServerTime()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var created = await MeshService.CreateOrUpdateNode(Page(NewId(), TestPartition, backdate: true)).Take(1)
            .Should().Within(60.Seconds()).Emit("the upsert lands",
                cancellationToken: TestContext.Current.CancellationToken);

        created.CreatedDate.Should().BeAfter(before);
        created.LastModified.Should().BeAfter(before);
    }

    [Fact(Timeout = 180000)]
    public async Task PersonBulkCreate_WithBackdatedStamps_GetsTheServerTime()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var batch = Enumerable.Range(0, 2).Select(_ => Page(NewId(), TestPartition, true)).ToImmutableList();
        var delivery = await ObserveNodeOperation(new CreateNodesRequest(batch)).FirstAsync()
            .Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);

        delivery.Message.Success.Should().BeTrue(delivery.Message.Error?.ToString());
        foreach (var n in delivery.Message.Created)
        {
            n.CreatedDate.Should().BeAfter(before);
            n.LastModified.Should().BeAfter(before);
        }
    }

    [Fact(Timeout = 180000)]
    public async Task Update_CannotChangeCreatedDate()
    {
        var created = await MeshService.CreateNode(Page(NewId(), TestPartition, false)).Take(1)
            .Should().Within(60.Seconds()).Emit("the create lands",
                cancellationToken: TestContext.Current.CancellationToken);

        await MeshService.UpdateNode(created with { CreatedDate = Backdated, LastModified = Backdated, Name = "renamed" }).Take(1)
            .Should().Within(60.Seconds()).Emit("the update lands",
                cancellationToken: TestContext.Current.CancellationToken);

        var after = await Mesh.GetWorkspace().GetMeshNodeStream(created.Path)
            .Where(n => n is not null && n.Name == "renamed")
            .Should().Within(60.Seconds()).Emit("renamed is live",
                cancellationToken: TestContext.Current.CancellationToken);
        after.CreatedDate.Should().Be(created.CreatedDate);
        after.LastModified.Should().BeAfter(Backdated.AddYears(1));
    }

    /// <summary>
    /// Restore-from-version writes the historical node back through UpdateNode (VersionPlugin). For a
    /// LIVE node the update pipeline keeps the live createdDate; a restore never re-stamps it.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Restore_OfALiveNode_KeepsTheOriginalCreatedDate()
    {
        var created = await MeshService.CreateNode(Page(NewId(), TestPartition, false)).Take(1)
            .Should().Within(60.Seconds()).Emit("the create lands",
                cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await MeshService.UpdateNode(created with { Version = 0, Name = "restored" }).Take(1)
            .Should().Within(60.Seconds()).Emit("the restore lands",
                cancellationToken: TestContext.Current.CancellationToken);
        var after = await Mesh.GetWorkspace().GetMeshNodeStream(created.Path)
            .Where(n => n is not null && n.Name == "restored")
            .Should().Within(60.Seconds()).Emit("restored is live",
                cancellationToken: TestContext.Current.CancellationToken);
        after.CreatedDate.Should().Be(created.CreatedDate);
    }

    /// <summary>
    /// A restore (UpdateNode) of a DELETED node does not fall into the create handler: it is refused
    /// "Node not found", so a restore can never re-stamp a node with the server clock.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task Restore_OfADeletedNode_IsRefused_NotRecreatedWithANewStamp()
    {
        var created = await MeshService.CreateNode(Page(NewId(), TestPartition, false)).Take(1)
            .Should().Within(60.Seconds()).Emit("the create lands",
                cancellationToken: TestContext.Current.CancellationToken);
        await MeshService.DeleteNode(created.Path).Take(1)
            .Should().Within(60.Seconds()).Emit("deleted",
                cancellationToken: TestContext.Current.CancellationToken);

        var message = await MeshService.UpdateNode(created with { Version = 0 })
            .Take(1).Select(_ => (string?)null)
            .Catch<string?, Exception>(e => Observable.Return<string?>(e.Message))
            .Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);
        message.Should().NotBeNull("the restore of a deleted node is refused, not re-created");
        message!.Should().Contain("not found");
    }

    /// <summary>Negative control: the platform (an import) keeps the carried history.</summary>
    [Fact(Timeout = 180000)]
    public async Task SystemCreate_PreservesCarriedStamps()
    {
        var delivery = await Access
            .RunAsSystem(() => ObserveNodeOperation(new CreateNodeRequest(Page(NewId(), TestPartition, true))))
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);

        delivery.Message.Success.Should().BeTrue(delivery.Message.Error?.ToString());
        delivery.Message.Node!.CreatedDate.Should().Be(Backdated);
        delivery.Message.Node.LastModified.Should().Be(Backdated);
    }
}
