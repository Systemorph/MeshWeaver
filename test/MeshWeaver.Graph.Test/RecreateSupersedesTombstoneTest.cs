using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// End-to-end shape of #3008 on a real Monolith mesh: delete a node, recreate it at the same path,
/// and assert — at the moment <c>Created</c> is published on the <see cref="IMeshChangeFeed"/> — that
/// the mesh's delete tombstone (<see cref="IAddressTombstones"/>) no longer reports the address as
/// gone for good, so a fresh subscriber sees the recreated node instead of the tombstone's terminal
/// "will not reactivate".
///
/// <para>Before the fix the tombstone was cleared only inside a LIVE per-node hub's change handler:
/// after the delete had disposed that hub nothing cleared it on the recreate, and the assertion at
/// <c>Created</c> depended on whether the dying hub's handler happened to still be subscribed (the
/// 2-in-4 failure of the Plugins-side <c>WorkspaceCacheEvictionTest.NewSubscriber_AfterRecreate…</c>).
/// The deterministic, hub-free half of this pin is
/// <c>MeshWeaver.Hosting.Test.TombstoneSupersededBeforeCreatedTest</c>.</para>
/// </summary>
public class RecreateSupersedesTombstoneTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 30000)]
    public async Task Recreate_SupersedesTheTombstone_BeforeCreatedIsPublished_AndAFreshSubscriberSeesTheNewNode()
    {
        const string id = "tombstone-recreate";
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(
            new MeshNode(id, TestPartition) { Name = "First", NodeType = "Markdown" }).Should().Emit();

        // Warm a per-node hub for the path — the delete then has a hub to dispose, which is the
        // still-disposing owner the issue's reader was routed to.
        var stream1 = GetClient(c => c.AddData()).GetWorkspace().GetMeshNodeStream(path);
        await stream1.Select(n => n?.Name).Should().Match(n => n == "First");

        var tombstones = Mesh.ServiceProvider.GetRequiredService<IAddressTombstones>();
        var feed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var tombstonedAtDeleted = new ReplaySubject<bool>();
        var tombstonedAtCreated = new ReplaySubject<bool>();
        using var feedSub = feed.Subscribe(ev =>
        {
            if (ev.Path != path) return;
            if (ev.Kind == MeshChangeKind.Deleted) tombstonedAtDeleted.OnNext(tombstones.IsDeleted(path));
            if (ev.Kind == MeshChangeKind.Created) tombstonedAtCreated.OnNext(tombstones.IsDeleted(path));
        });

        await NodeFactory.DeleteNode(path).Should().Emit();
        var atDeleted = await tombstonedAtDeleted.Should().Within(5.Seconds()).Emit();
        atDeleted.Should().BeTrue("the delete marks the tombstone synchronously, before Deleted is published");

        await NodeFactory.CreateNode(
            new MeshNode(id, TestPartition) { Name = "Second", NodeType = "Markdown" }).Should().Emit();
        var atCreated = await tombstonedAtCreated.Should().Within(5.Seconds()).Emit();
        atCreated.Should().BeFalse(
            "the recreate's durable commit supersedes the tombstone BEFORE its Created is published — "
            + "a reader acting on Created must never be told the address will not reactivate");
        tombstones.IsDeleted(path).Should().BeFalse();

        // The issue's reader: a brand-new subscriber after Created was observed.
        var fresh = await GetClient(c => c.AddData()).GetWorkspace().GetMeshNodeStream(path)
            .Select(n => n?.Name)
            .Where(n => n != null)
            .Should().Within(10.Seconds()).Emit();
        fresh.Should().Be("Second", "a fresh subscriber after delete+recreate sees the recreated node");
    }
}
