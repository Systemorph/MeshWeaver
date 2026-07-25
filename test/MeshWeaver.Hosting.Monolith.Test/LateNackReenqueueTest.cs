using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins hole 2 of the residual acked-write-loss behind <c>TwoSiloRecycleConvergenceTest</c>
/// (main run 30159928718 / PR-645 run 30160988085): the mirror's <c>UpdateRemote</c> bounds
/// its owner-response wait at ~2s and emits the optimistic snapshot on timeout — but the OLD
/// shape also KILLED the response subscription there, so an owner verdict arriving later
/// (above all the <see cref="MeshNodeErrorCode.OwnerDisposing"/> disposal NACK, which only
/// lands after the owner's phased teardown) was observed by NOBODY. The caller saw success;
/// the write was gone.
///
/// <para>The fix: the write stays armed in <c>LatePatchResponseRegistry</c> for
/// <c>LateResponseWatchBound</c> (30s); the cache hub's <c>PatchDataResponse</c> handler
/// dispatches the late verdict, and an OwnerDisposing NACK — the owner's explicit
/// "the patch NEVER applied" — re-enqueues the ORIGINAL update lambda against the fresh
/// activation (bounded re-enqueue budget, re-diffed against the freshest state).</para>
///
/// <para>The scripted interleaving: park the owner's merge turn behind a gated no-op turn
/// (so no response can arrive inside the 2s window), let the caller take its optimistic
/// terminal, THEN dispose the owner — the disposal NACK is necessarily LATE. The write must
/// still reach durable storage via the re-enqueue. Without Part 2 the late NACK is dropped
/// and the storage poll times out at the pre-write state.</para>
/// </summary>
public class LateNackReenqueueTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 90_000)]
    public async Task LateOwnerDisposingNack_AfterOptimisticEmit_ReenqueuesAndLands()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/late-nack-node";
        await NodeFactory.CreateNode(
                new MeshNode("late-nack-node", TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull();

        // Park the owner's merge executor (same gating pattern as OwnerDisposalNackTest):
        // the cross-hub write below is accepted by the owner's handler but its merge turn
        // provably cannot run — no ack can arrive inside the caller's 2s window.
        var primary = nodeHub!.GetWorkspace().DataContext
            .GetDataSourceForType(typeof(MeshNode))!
            .GetStreamForPartition(null)!;
        using var gateEntered = new ManualResetEventSlim(false);
        using var releaseGate = new ManualResetEventSlim(false);
        primary.Update((Func<EntityStore?, ChangeItem<EntityStore>?>)(_ =>
        {
            gateEntered.Set();
            releaseGate.Wait(TimeSpan.FromSeconds(60));
            return null;
        }), _ => { });
        gateEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
            "the gated turn must be running on the primary stream's executor before the write");

        try
        {
            // Cross-hub cache write — the production mirror path (UpdateRemote via the
            // per-path queue). With the owner's merge parked, the caller's terminal is the
            // OPTIMISTIC emit at ~2s carrying the locally-computed snapshot.
            var marker = $"post-nack-{Guid.NewGuid():N}"[..24];
            var workspace = Mesh.GetWorkspace();
            var optimistic = await workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = marker })
                .FirstAsync().Timeout(10.Seconds()).ToTask(ct);
            optimistic.Name.Should().Be(marker, "the caller's terminal is the optimistic snapshot");
            Output.WriteLine($"[write] optimistic terminal received with marker {marker}");

            // Fence: the patch handler has provably run on the owner (registered the
            // disposal NACK) before the dispose below.
            await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
                .Should().Within(10.Seconds()).Emit();

            // Dispose the owner AFTER the optimistic emit — its OwnerDisposing NACK
            // (posted from the ShutDown-phase disposal action) is necessarily LATE. The
            // armed late watch must consume it and re-enqueue the ORIGINAL update against
            // the fresh activation the re-posted patch brings up.
            nodeHub!.Dispose();
            Output.WriteLine($"[dispose] owner per-node hub disposal invoked for {path}");

            // Ground truth: the write eventually lands in durable storage. Without the
            // late-NACK re-enqueue the store stays frozen at 'initial' (the parked merge
            // turn died with the sync hub; nobody re-applies) and this poll times out —
            // the WaitForPersistedBeyond signature of the TwoSilo failure.
            var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
                .Where(n => n is not null && n.Name == marker)
                .FirstAsync().Timeout(45.Seconds()).ToTask(ct);
            persisted!.Name.Should().Be(marker,
                "an optimistically-acked write whose owner NACKed OwnerDisposing must be "
                + "re-enqueued and applied on the fresh activation — never silently lost");
        }
        finally
        {
            releaseGate.Set();
        }
    }
}
