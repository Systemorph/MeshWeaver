using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
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

    /// <summary>
    /// 🚨 THE PRODUCTION SHAPE. <c>MeshOperations.Move</c> (the MCP and portal verb) targets the
    /// SOURCE node's own hub, so the hub executing the move IS the one the move leaves behind. That
    /// hub cannot be disposed from inside its own handler without losing the reply, so the reply is
    /// posted from the mesh hub first and the hub is retired afterwards. Both halves are asserted:
    /// the caller hears success, AND the source hub is gone.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AMoveExecutedOnTheSourceHub_StillAnswers_AndRetiresThatHub()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/self-{suffix}";
        var target = $"{TestPartition}/self-landed-{suffix}";

        await Create(source, ct);
        var sourceHub = await Activate(source, ct);

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target),
                o => o.WithTarget(new Address(source)))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the reply must reach the caller although the hub that executed the move is retired",
                cancellationToken: ct);
        moved.Success.Should().BeTrue(moved.Error ?? "nothing objects to moving this node");

        await sourceHub.DisposalCompleted.FirstOrDefaultAsync()
            .Should().Within(TestTimeouts.Convergence).Emit(
                "the move ran ON the source hub, and that hub serves a path the move removed; skipping "
                + "it because it is the executing hub leaves exactly the activation #7171 measured",
                cancellationToken: ct);
        Mesh.GetHostedHub(new Address(source), HostedHubCreation.Never).Should().BeNull();
        (await ReadNode(target).Timeout(ReadNodeTimeout).FirstAsync().Await(ct)).Should().NotBeNull();
    }

    /// <summary>
    /// The node-stream cache half. A live cache entry for the source, held by a subscriber, must be
    /// INVALIDATED by the move, as a delete invalidates it: the change feed alone does not evict a
    /// healthy entry, so without this the cache keeps serving the pre-move node at the old path.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AMove_InvalidatesTheSourcesCacheEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var source = $"{TestPartition}/cached-{suffix}";
        var target = $"{TestPartition}/cached-landed-{suffix}";
        var cache = (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

        await Create(source, ct);

        // Warm and HOLD a cache subscription, as a viewer's binding does.
        var held = cache.GetStream(source, Mesh.JsonSerializerOptions).Replay(1);
        using var holding = held.Connect();
        await held.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
            "PRECONDITION: the cache serves the source before the move", cancellationToken: ct);
        cache.IsReadStreamLive(source).Should().BeTrue("PRECONDITION: the entry is live and held");

        // Attached BEFORE the move: an eviction is a point event on a hot subject.
        var evictions = cache.ReadStreamEvictions.Where(e => e.Path == source).Replay();
        using var watching = evictions.Connect();

        var moved = await ObserveNodeOperation(new MoveNodeRequest(source, target))
            .Select(d => d.Message)
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        moved.Success.Should().BeTrue(moved.Error ?? "nothing objects to moving this node");

        var eviction = await evictions.FirstAsync().Should().Within(TestTimeouts.Convergence).Emit(
            "the move removed the node, so its cache entry is invalidated exactly as a delete's is",
            cancellationToken: ct);
        eviction.Reason.Should().Be("invalidate");
        cache.IsReadStreamLive(source).Should().BeFalse("nothing in the cache serves the moved-away path");
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
