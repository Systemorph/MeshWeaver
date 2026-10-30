using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A DELETED OWNER'S DISPOSE FLUSH NEVER OVERWRITES THE NODE CREATED IN ITS PLACE.</b>
///
/// <para><b>The failure.</b> A node is deleted, and the delete tears its owner down. While that owner
/// is still tearing down, the same path is created again — the Feedback hand-over does exactly this:
/// it deletes a second copy and writes a redirect at the same address. The recreate's
/// <c>Created</c> notification reached the dying owner's own-node change handler, which reset its
/// "my node was deleted" flag and then tried to adopt the recreate — an adoption a tearing-down hub
/// refuses (<c>HubDisposingException … is shutting down</c>). The flag stayed reset over an in-RAM
/// node that was still the PRE-delete one, so the owner's dispose-time flush found an "unsaved" own
/// state, no delete flag, and a tombstone the recreate had superseded — and wrote the deleted node
/// back over the recreate. Measured on MeshWeaver.Plugins'
/// <c>FeedbackOccupiedInboxTest.ARevisionWrittenWhileTheSettleIsPending_IsNeverDeleted_ItIsFoldedIn</c>:
/// the redirect was created and logged, and the store then held the deleted submission again.</para>
///
/// <para><b>The deterministic shape.</b> The owner hosts one child whose Quiescing drain holds an
/// un-answered callback (the device of <see cref="AWriteDuringItsOwnersRecycleLandsTest"/>), so once
/// the delete disposes the owner it stays in teardown — refusing, but still subscribed to the store's
/// change notifications — for the child's whole quiesce budget. The recreate lands inside that window
/// by construction; the owner's dispose flush runs after it.</para>
/// </summary>
public class ADyingOwnerNeverOverwritesTheRecreateTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Accepted and never answered — parks one pending response callback on its sender.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    [Fact(Timeout = 180_000)]
    public async Task ARecreateSeenWhileTheDeletedOwnerTearsDown_IsNotOverwrittenByItsDisposeFlush()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = "dying-owner-" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{TestPartition}/{id}";
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await NodeFactory.CreateNode(new MeshNode(id, TestPartition) { Name = "original", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit("the node exists first", cancellationToken: ct);

        // A revision from another hub: the owner holds it in RAM as its own, patch-driven state.
        var workspace = Mesh.GetWorkspace();
        await workspace.GetMeshNodeStream(path).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the node is readable", cancellationToken: ct);
        await workspace.GetMeshNodeStream(path).Update(n => n with { Name = "revised" }).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the revision is committed by the owner", cancellationToken: ct);
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions).Take(1))
            .Where(n => n?.Name == "revised")
            .Should().Within(TestTimeouts.Convergence).Emit("the revision is durable", cancellationToken: ct);

        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull("reading the node activated its owner");

        // Hold the owner in teardown: a child whose Quiescing drain owes a reply a healthy hub never sends.
        var holdEntered = new AsyncSubject<Unit>();
        var holder = Mesh.GetHostedHub(
            new Address("dying-owner-hold", Guid.NewGuid().ToString("N")[..8]),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse))
                .WithHandler<HoldRequest>((_, d) =>
                {
                    holdEntered.OnNext(Unit.Default);
                    holdEntered.OnCompleted();
                    return d.Processed();
                }));
        var child = owner!.GetHostedHub(
            new Address($"{path}/_hold"),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse)));
        using var held = child
            .Observe<HoldResponse>(new HoldRequest(), o => o.WithTarget(holder.Address))
            .Subscribe(
                d => Output.WriteLine($"hold answered unexpectedly: {d.Message}"),
                ex => Output.WriteLine($"hold released: {ex.GetType().Name}"));
        await holdEntered.Should().Within(TestTimeouts.Convergence).Emit(
            "the child holds an un-answered callback before the delete", cancellationToken: ct);

        // Every store write of the path from here on; the delete itself is not one.
        var writes = new ReplaySubject<MeshNode>();
        using var changes = storage.Changes
            .Where(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)
                        && c.Kind is DataChangeKind.Created or DataChangeKind.Updated)
            .Select(c => c.Entity as MeshNode)
            .Where(n => n is not null)
            .Subscribe(n => writes.OnNext(n!));

        (await NodeFactory.DeleteNode(path).Should().Within(TestTimeouts.Convergence)
                .Emit("the delete answers", cancellationToken: ct))
            .Should().BeTrue("the delete removes the node and tears its owner down");
        (owner.RunLevel > MessageHubRunLevel.Started).Should().BeTrue("the premise: the delete is tearing the owner down");

        await NodeFactory.CreateNode(new MeshNode(id, TestPartition) { Name = "recreated", NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit("the path is created again", cancellationToken: ct);
        (owner.RunLevel < MessageHubRunLevel.Dead).Should().BeTrue(
            "the premise: the recreate landed while the deleted owner was still tearing down");

        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => owner.RunLevel == MessageHubRunLevel.Dead)
            .Should().Within(TestTimeouts.Convergence).Emit("the deleted owner finishes its teardown", cancellationToken: ct);

        await writes.Where(n => n.Name != "recreated")
            .Should().NotEmit(5.Seconds(),
                "the deleted owner's in-RAM node predates the delete; its dispose flush must never write it "
                + "back over the node created in its place");
        var stored = await storage.Read(path, Mesh.JsonSerializerOptions).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("recreated", "the store holds the recreate, not the resurrected original");
    }
}
