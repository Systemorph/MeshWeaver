using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A move retires the SOURCE's per-node hub, exactly as a delete retires the deleted node's
/// hub — issue #7171.</b>
///
/// <para>The move's delete leg removed the source rows from storage and published the
/// <c>Deleted</c> events, but, unlike the delete commit, it neither invalidated the node-stream
/// cache nor disposed the hub activated under the source address. So that activation went on
/// serving under an address that names no node. It kept fanning frames out to its subscribers,
/// and the router, which resolves the address to "no node", could carry neither forward traffic
/// nor the terminal NACK that would have made it evict a closed circuit. Measured on memex on
/// 2026-10-26: after <c>BusinessPlan2026/Plyona</c> was moved to
/// <c>BusinessPlan2026/Background/Plyona</c>, the router logged hundreds of undeliverable NACKs a
/// minute to sender <c>BusinessPlan2026/Plyona</c>.</para>
///
/// <para><b>Control in the same test:</b> a sibling node's live hub, not part of the move, is
/// untouched, so the fix is not "dispose whatever is hosted".</para>
///
/// <para><b>Fails on unfixed code:</b> the source hub's <c>DisposalCompleted</c> never fires and its
/// run level stays live after the move has answered.</para>
/// </summary>
public class AMovedAwayAddressIsRetiredTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task TheSourceHub_IsDisposed_AndASiblingHub_IsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/moved-{suffix}";
        var target = $"{TestPartition}/landed-{suffix}";
        var sibling = $"{TestPartition}/bystander-{suffix}";

        await Create(source, ct);
        await Create(sibling, ct);

        // Activate both hubs the way a viewer does: a read.
        var sourceHub = await Activate(source, ct);
        var siblingHub = await Activate(sibling, ct);

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        moved.Success.Should().BeTrue(moved.Error ?? "nothing objects to moving this node");

        await sourceHub.DisposalCompleted.FirstOrDefaultAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the move removed the node at this address, so the hub serving it must go, as it "
                + "does on a delete; otherwise it fans frames out under an address that routes nowhere",
                cancellationToken: ct);
        sourceHub.RunLevel.Should().Be(MessageHubRunLevel.Dead);
        Mesh.GetHostedHub(new Address(source), HostedHubCreation.Never).Should().BeNull(
            "nothing is hosted at the moved-away address any more");

        // 🚨 The control: the bystander was not moved, and its hub stays exactly as it was.
        ((int)siblingHub.RunLevel).Should().BeLessThan((int)MessageHubRunLevel.DisposeHostedHubs,
            "only the moved address is retired");
        Mesh.GetHostedHub(new Address(sibling), HostedHubCreation.Never).Should().BeSameAs(siblingHub);

        (await ReadNode(target).Timeout(ReadNodeTimeout).FirstAsync().Await(ct)).Should().NotBeNull(
            "and the moved node is served at its new address");
    }

    private Task<MeshNode> Create(string path, CancellationToken ct)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => NodeFactory.CreateNode(MeshNode.FromPath(path) with
            {
                NodeType = "Markdown",
                Name = path,
                State = MeshNodeState.Active,
            }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
    }

    private async Task<IMessageHub> Activate(string path, CancellationToken ct)
    {
        (await ReadNode(path).Timeout(ReadNodeTimeout).FirstAsync().Await(ct)).Should().NotBeNull();
        var hub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        hub.Should().NotBeNull($"PRECONDITION: a read activates the hub at '{path}'");
        return hub!;
    }
}
