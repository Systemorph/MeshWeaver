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
using MeshWeaver.Testing.FaultInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <b>A read of a recreated node that reaches the DELETED node's dying owner waits for the new owner —
/// it is never told the node will not come back.</b>
///
/// <para>The shape behind <c>InboxOpenApprovalsTest.APreferencesNodeDeletedAndRecreated_NeverEndsTheFeed</c>:
/// a node is deleted, which tears its owner down, and created again while that owner is still tearing
/// down. The store publishes the recreated row on its change feed INSIDE the commit, so a reader that
/// follows a listing opens the node's stream before the recreate has superseded the delete tombstone.
/// Its request is routed to the old owner, which is still hosted. That owner asks the tombstone whether
/// its address is gone for good; if it says yes, it answers "the node was deleted, so this address will
/// not reactivate", the read ends, and the reader (whose listing already said "exists") never asks
/// again. Measured on the inbox test in 2 of about 200 runs, with that exact answer in the log.</para>
///
/// <para>The verdict the dying owner must give is "shutting down, ask again": a recreate the storage
/// guard has ADMITTED is on its way, so the address is not gone for good
/// (<see cref="RecentlyDeletedRegistry.IsDeleted"/>). The reader rides the teardown out and is served
/// the recreate by the next activation.</para>
///
/// <para><b>The forced interleaving.</b> The owner hosts a child whose Quiescing drain owes a reply that
/// is never sent, so the delete leaves it tearing down (refusing, still hosted) for the child's quiesce
/// budget. <see cref="FaultInjectingStorageAdapter.HoldWrites"/> holds the recreate after the guard
/// admitted it and before the store commits it, so the read is opened in exactly the window the inbox
/// opened it in. The old answer arrives in milliseconds; the test holds the window open for a second.</para>
/// </summary>
public class AReadOfARecreateReachingTheDyingOwnerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Accepted and never answered — parks one pending response callback on its sender.</summary>
    private record HoldRequest : IRequest<HoldResponse>;

    private record HoldResponse;

    private FaultInjectingStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<FaultInjectingStorageAdapter>();

    private AccessService Access => Mesh.ServiceProvider.GetRequiredService<AccessService>();

    /// <summary>The injector goes in BEFORE the base adds in-memory persistence: it is the store of record.</summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.AddFaultInjectingStorage());

    [Fact(Timeout = 120_000)]
    public async Task AReadOpenedWhileTheRecreateIsAdmitted_RidesTheDyingOwnerOut_AndSeesTheRecreate()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/dyr{Guid.NewGuid().ToString("N")[..10]}";
        await Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = "Markdown", Name = "first", State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit("the node exists first", ct);
        await Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)).Where(n => n?.Name == "first")
            .Should().Within(TestTimeouts.Convergence).Emit("reading the node activates its owner", ct);
        var owner = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        owner.Should().NotBeNull("the premise: the read activated the node's owner");

        // Hold the owner in teardown: a child whose Quiescing drain owes a reply a healthy hub never sends.
        var holdEntered = new AsyncSubject<Unit>();
        var holder = Mesh.GetHostedHub(
            new Address("dying-owner-read-hold", Guid.NewGuid().ToString("N")[..8]),
            c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse))
                .WithHandler<HoldRequest>((_, d) =>
                {
                    holdEntered.OnNext(Unit.Default);
                    holdEntered.OnCompleted();
                    return d.Processed();
                }));
        var child = owner!.GetHostedHub(new Address($"{path}/_hold"), c => c.WithTypes(typeof(HoldRequest), typeof(HoldResponse)));
        using var held = child.Observe<HoldResponse>(new HoldRequest(), o => o.WithTarget(holder.Address))
            .Subscribe(_ => { }, ex => Output.WriteLine($"hold released: {ex.GetType().Name}"));
        await holdEntered.Should().Within(TestTimeouts.Convergence)
            .Emit("the child owes a reply before the delete", ct);

        await Access.RunAsSystem(() => NodeFactory.DeleteNode(path))
            .Should().Within(TestTimeouts.Convergence).Emit("the node is deleted", ct);
        (owner.RunLevel > MessageHubRunLevel.Started).Should().BeTrue("the premise: the delete is tearing the owner down");

        // The recreate is admitted by the guard and held before the store commits it.
        using var hold = Storage.HoldWrites(path);
        using var recreate = Access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = "Markdown", Name = "second", State = MeshNodeState.Active,
            }))
            .Subscribe(_ => { }, ex => Output.WriteLine($"recreate ended: {ex.Message}"));
        await hold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("the recreate was admitted and reached the store", ct);

        // The reader that saw the row: its request goes to the deleted node's owner, still hosted.
        var seen = new ReplaySubject<Notification<MeshNode>>();
        using var read = Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path)).Materialize().Subscribe(seen);
        await seen.Where(n => n.Kind == NotificationKind.OnError)
            .Select(n => n.Exception!.Message)
            .Should().NotEmit(1.Seconds(),
                "the recreate is on its way: the dying owner must answer 'ask again', never 'this address will not reactivate'");
        (owner.RunLevel < MessageHubRunLevel.Dead).Should().BeTrue(
            "the premise: the read was answered while the deleted owner was still tearing down");

        hold.Release();
        var first = await seen.Where(n => n.Kind == NotificationKind.OnError || n.Value is not null)
            .Select(n => n.Kind == NotificationKind.OnNext ? n.Value!.Name ?? string.Empty : $"FAILED: {n.Exception?.Message}")
            .Should().Within(TestTimeouts.Convergence).Emit("the reader is answered", ct);
        first.Should().Be("second", "the reader rides the old owner's teardown out and is served the recreate");
    }
}
