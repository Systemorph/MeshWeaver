using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Orleans.Test;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// <b>A late guarded delete never removes a RE-CREATED node</b> (MeshWeaver#6856, the follow-up).
/// Delete A (version-guarded, issued on silo 1) passes its owner's check and is paused at its store.
/// Meanwhile another hub on silo 0 deletes the node and creates it again — a new incarnation, which
/// starts over at version 1. Then A's held storage delete runs. It compared only
/// <c>version &lt;= confirmed</c>, which the new node satisfies, and removed it. Now it also requires the
/// incarnation it read (<see cref="MeshNode.CreatedDate"/>), refuses, and the re-created node survives.
/// <para>Negative control (recorded): on origin/main <c>cc8f557be4</c> the held delete answers
/// <c>true</c> and the re-created node is gone.</para>
/// </summary>
public class ALateDeleteNeverRemovesARecreatedNodeTest(ALateDeleteNeverRemovesARecreatedNodeTest.Cluster mesh)
    : IClassFixture<ALateDeleteNeverRemovesARecreatedNodeTest.Cluster>
{
    private const int OwnerSilo = 0;
    private const int DeleterSilo = 1;

    [Fact(Timeout = 180_000)]
    public async Task AHeldDelete_IsRefusedByTheNodeCreatedInItsPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = mesh.Silo(OwnerSilo);
        var access = services.GetRequiredService<AccessService>();
        var meshService = services.GetRequiredService<IMeshService>();
        var ns = $"recreate-{Guid.NewGuid():N}";
        var path = $"{ns}/Doc";
        await access.RunAsSystem(() => meshService.CreateNode(
                new MeshNode("Doc", ns) { Name = "original", NodeType = "Markdown", State = MeshNodeState.Active }))
            .Should().Within(TestTimeouts.Convergence).Emit("the original is created on silo 0", ct);
        await access.RunAsSystem(() => mesh.Hub(OwnerSilo).NodeOperationIssuingHub()
                .Observe(new PingRequest(), o => o.WithTarget(new Address(path))))
            .Should().Within(TestTimeouts.Convergence).Emit("its owner answers on silo 0", ct);
        var original = mesh.Stored(path)!;

        // Delete A: version-guarded, on silo 1, paused at silo 1's store after the owner's check.
        using var hold = mesh.Storage(DeleterSilo).HoldDeletes(path);
        var deleter = mesh.Hub(DeleterSilo);
        var deleteA = mesh.Silo(DeleterSilo).GetRequiredService<AccessService>().RunAsSystem(() =>
                deleter.NodeOperationIssuingHub().Observe(
                    new DeleteNodeRequest(path) { ExpectedVersion = original.Version },
                    o => o.WithTarget(deleter.NodeOperationTarget())))
            .Replay(1);
        using var connection = deleteA.Connect();
        await hold.Arrivals.Should().Within(TestTimeouts.Convergence)
            .Emit("delete A passed its owner's check and is held at silo 1's store", ct);

        // Another hub deletes the node and creates it again — a new incarnation, at version 1 again.
        (await access.RunAsSystem(() => mesh.Hub(OwnerSilo).NodeOperationIssuingHub()
                    .Observe(new DeleteNodeRequest(path), o => o.WithTarget(mesh.Hub(OwnerSilo).NodeOperationTarget())))
                .Should().Within(TestTimeouts.Convergence).Emit("delete B removes the node", ct))
            .Message.Success.Should().BeTrue();
        // Delete B tore the owner down; its teardown fences A's seal (the row is gone) and then lifts the
        // owner's version guard and row guard. Only then is the path writable again: wait for that, then
        // create ONCE — any failure of the create fails the test.
        var ownerRegistry = services.GetRequiredService<RecentlyDeletedRegistry>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Where(_ => !ownerRegistry.IsVersionGuardedDeletionInFlight(path))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the old owner's teardown fence settles and its version guard is lifted", ct);
        ownerRegistry.IsUnderActiveDeletion(path, out _).Should().BeFalse("the old owner's row guard is lifted with it");
        await access.RunAsSystem(() => meshService.CreateNode(
                new MeshNode("Doc", ns) { Name = "re-created", NodeType = "Markdown", State = MeshNodeState.Active }))
            .Should().Within(TestTimeouts.Convergence).Emit("the node is created again on silo 0", ct);
        var recreated = mesh.Stored(path);
        recreated.Should().NotBeNull();
        recreated!.Name.Should().Be("re-created");
        recreated.Version.Should().BeLessThanOrEqualTo(original.Version,
            "the precondition: a re-created node starts over, so 'version <= confirmed' alone accepts it");
        recreated.CreatedDate.Should().NotBe(original.CreatedDate, "a new incarnation");

        // Delete A's held storage delete now runs — and the store refuses it.
        hold.Release();
        (await mesh.Storage(DeleterSilo).ConditionalDeleteResults(path).Should().Within(TestTimeouts.Convergence)
                .Emit("delete A's storage delete reached the store", ct))
            .Should().BeFalse("it confirmed another incarnation; the node created in its place is not its to remove");
        // The store's refusal is recorded by the adapter BEFORE the delete pipeline handles it: the
        // completed operation is delete A's answer, which must name the conflict.
        var responseA = (await deleteA.Should().Within(TestTimeouts.Convergence)
            .Emit("delete A completes with an answer", ct)).Message;
        responseA.Success.Should().BeFalse("the node at the path is not the incarnation delete A confirmed");
        responseA.RejectionReason.Should().Be(NodeDeletionRejectionReason.Conflict, responseA.Error ?? "the stored row is another incarnation");
        mesh.Stored(path).Should().NotBeNull("the re-created node survives");
        mesh.Stored(path)!.Name.Should().Be("re-created");
    }

    /// <summary>Its own cluster.</summary>
    public class Cluster : FaultInjectionCluster;
}
