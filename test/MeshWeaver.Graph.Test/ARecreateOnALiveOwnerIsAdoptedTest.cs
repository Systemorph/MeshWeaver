using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <b>A node deleted and created again while its owner is still live is a NEW incarnation, and the
/// owner adopts it even though its version restarted at 1.</b>
///
/// <para>The shape: the owner holds the node at version N, a Deleted and then a Created for the same
/// path reach its change handler, and the recreate is persisted at version 1. <c>AdoptPersisted</c>
/// is forward-only by version, so it dropped the recreate as a "lagged echo" and the owner went on
/// serving the deleted node (reads answered it, or answered "delete in progress"). The incarnation
/// marker the store already uses — <see cref="MeshNode.CreatedDate"/>, via
/// <see cref="StorageIncarnation"/> — separates the two: a lower version of the SAME incarnation is
/// a stale echo and stays refused; a lower version of a LATER incarnation is the recreate.</para>
/// </summary>
public class ARecreateOnALiveOwnerIsAdoptedTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private HostedHubsCollection Hosted => Mesh.ServiceProvider.GetRequiredService<HostedHubsCollection>();

    private async Task<(IMessageHub Hub, string Path)> LiveOwnerAtVersionThree(string id)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(
            new MeshNode(id, TestPartition) { Name = "First", NodeType = "Markdown" }).Should().Emit(cancellationToken: ct);
        await GetClient(c => c.AddData()).GetWorkspace().GetMeshNodeStream(path)
            .Should().Match(n => n?.Name == "First", cancellationToken: ct);

        var deadline = DateTime.UtcNow + TestTimeouts.Convergence;
        IMessageHub? hub = null;
        while (hub is null && DateTime.UtcNow < deadline)
        {
            hub = Hosted.Hubs.FirstOrDefault(h => ActivationRecycle.PathOf(h) == path
                                                  && h.RunLevel == MessageHubRunLevel.Started);
            if (hub is null) await Task.Delay(50, ct);
        }
        hub.Should().NotBeNull("the node has a live owner");

        var stream = hub!.GetWorkspace().GetMeshNodeStream();
        await stream.Update(n => n with { Name = "Second" }).Should().Emit(cancellationToken: ct);
        var third = await stream.Update(n => n with { Name = "Third" }).Should().Emit(cancellationToken: ct);
        third.Version.Should().BeGreaterThanOrEqualTo(3, "the owner holds the node well past version 1");
        return (hub!, path);
    }

    [Fact(Timeout = 120_000)]
    public async Task ADeletedThenCreatedFeedOnALiveOwner_AdoptsTheRecreateAtVersionOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hub, path) = await LiveOwnerAtVersionThree("recreate-live-feed");
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var id = path.Split('/')[^1];

        // Straight through the store: the live owner hears Deleted, then Created, on its change feed.
        await storage.Delete(path).Should().Emit(cancellationToken: ct);
        await Task.Delay(50, ct);
        var recreate = new MeshNode(id, TestPartition)
        {
            Name = "Recreated", NodeType = "Markdown", Version = 1,
            CreatedDate = DateTimeOffset.UtcNow.AddMinutes(1), State = MeshNodeState.Active,
        };
        await storage.Write(recreate, Mesh.JsonSerializerOptions).Should().Emit(cancellationToken: ct);

        var adopted = await hub.GetWorkspace().GetMeshNodeStream()
            .Where(n => n?.Name == "Recreated")
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the owner must serve the recreate, not the deleted node", cancellationToken: ct);
        adopted!.Version.Should().BeLessThan(3, "the recreate restarted the version counter");
    }

    [Fact(Timeout = 120_000)]
    public async Task AdoptPersisted_TakesALowerVersionOfALaterIncarnation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hub, _) = await LiveOwnerAtVersionThree("recreate-live-adopt");
        var stream = hub.GetWorkspace().GetMeshNodeStream();
        var current = await stream.Take(1).Should().Emit(cancellationToken: ct);

        var recreate = current! with
        {
            Name = "Recreated", Version = 1, CreatedDate = current.CreatedDate.AddSeconds(5),
        };
        var after = await stream.AdoptPersisted(recreate).Should().Emit(cancellationToken: ct);

        after.Name.Should().Be("Recreated");
        after.Version.Should().Be(1);
    }

    [Fact(Timeout = 120_000)]
    public async Task AdoptPersisted_StillRefusesALowerVersionOfTheSameIncarnation()
    {
        var ct = TestContext.Current.CancellationToken;
        var (hub, _) = await LiveOwnerAtVersionThree("recreate-live-stale");
        var stream = hub.GetWorkspace().GetMeshNodeStream();
        var current = await stream.Take(1).Should().Emit(cancellationToken: ct);

        var staleEcho = current! with { Name = "Lagged echo", Version = current.Version - 1 };
        var after = await stream.AdoptPersisted(staleEcho).Should().Emit(cancellationToken: ct);
        after.Name.Should().Be(current.Name, "a lower version of the same incarnation is a lagged echo");
        after.Version.Should().Be(current.Version);

        var olderIncarnation = current with
        {
            Name = "Pre-delete ghost", Version = 1, CreatedDate = current.CreatedDate.AddSeconds(-5),
        };
        after = await stream.AdoptPersisted(olderIncarnation).Should().Emit(cancellationToken: ct);
        after.Name.Should().Be(current.Name, "an EARLIER incarnation never replaces a later one");
        after.Version.Should().Be(current.Version);
    }
}
