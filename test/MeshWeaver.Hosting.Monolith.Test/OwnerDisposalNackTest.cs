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
/// Pins hole 1 of the residual acked-write-loss behind <c>TwoSiloRecycleConvergenceTest</c>
/// (main run 30159928718 / PR-645 run 30160988085): an owner per-node hub disposed AFTER a
/// <c>PatchDataRequest</c> was delivered (handler ran, ack watcher + merge turn queued) but
/// BEFORE the merge turn committed used to die SILENTLY — the ack watcher (<c>postSub</c>)
/// was <c>hub.RegisterForDisposal</c>'d so disposal killed it before its own timeout could
/// fire, and the parked merge turn's <c>UpdateStreamRequest</c> was dropped by the sync hub's
/// shutting-down gate. No response was EVER posted; the sender waited to its full request
/// timeout while the write was gone.
///
/// <para>The fix (<c>DataExtensions.RegisterOwnerDisposingNack</c>): the patch handler
/// registers a disposal action that claims the once-only ack gate and posts an explicit
/// <c>PatchDataResponse</c> NACK with <see cref="MeshNodeErrorCode.OwnerDisposing"/> —
/// routed through the PARENT hub, because the disposing hub's own Post is gated closed in
/// the ShutDown phase where disposal actions run. This test parks the merge turn behind a
/// gated no-op turn on the primary data-source stream's executor (the
/// <c>TeardownHubCreationFreezeTest</c> gating pattern), disposes the owner, and requires
/// the NACK as the sender's terminal. Without the fix it fails with a silent timeout.</para>
/// </summary>
public class OwnerDisposalNackTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60_000)]
    public async Task DisposingOwner_BeforeMergeTurnRuns_NacksOwnerDisposing()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/disposal-nack-node";
        await NodeFactory.CreateNode(
                new MeshNode("disposal-nack-node", TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        // Warm the per-node hub and wait until the CREATE is durable, so the owner's
        // data-source streams are fully initialized before the gate goes in.
        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull();

        // Park the PRIMARY data-source stream's turn executor with a gated no-op turn:
        // the patch's merge turn queues BEHIND it on the same serialized executor, so it
        // provably cannot run before the hub is disposed. Test-side gating only — the
        // same ManualResetEventSlim pattern as TeardownHubCreationFreezeTest.
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
            "the gated turn must be running on the primary stream's executor before the patch is posted");

        try
        {
            // Post the patch with a pre-registered response observable, then FENCE on a
            // GetDataRequest response from the same single-threaded action block — when the
            // fence answers, the patch handler has run: ack watcher + disposal NACK are
            // registered and the merge turn is parked behind the gate.
            var nameKey = Mesh.JsonSerializerOptions.PropertyNamingPolicy?.ConvertName(nameof(MeshNode.Name))
                ?? nameof(MeshNode.Name);
            var patchJson = new System.Text.Json.Nodes.JsonObject { [nameKey] = "never-applied" }
                .ToJsonString(Mesh.JsonSerializerOptions);
            var respTask = Mesh.Observe(
                    new PatchDataRequest(new MeshNodeReference(), new RawJson(patchJson)),
                    o => o.WithTarget(new Address(path)))
                .FirstAsync().Timeout(10.Seconds()).ToTask(ct);
            await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
                .Should().Within(10.Seconds()).Emit();

            nodeHub!.Dispose();
            Output.WriteLine($"[dispose] owner per-node hub disposal invoked for {path}");

            // WITHOUT the disposal NACK this wait dies silently (postSub disposed with the
            // hub, merge turn dropped) and times out at 10s — the acked-write-loss
            // signature. WITH the fix, the ShutDown-phase disposal action posts the
            // OwnerDisposing NACK through the parent hub within the phased-teardown bound
            // (hosted-hub drain is capped at 5s even with the sync hub parked).
            var response = await respTask;
            var patchResponse = response.Message.Should().BeOfType<PatchDataResponse>().Subject;
            patchResponse.Success.Should().BeFalse(
                "the merge turn never ran — a disposing owner must never claim success");
            patchResponse.NodeError.Should().NotBeNull(
                "the disposal NACK carries a structured error so the mirror can classify it");
            patchResponse.NodeError!.Code.Should().Be(MeshNodeErrorCode.OwnerDisposing,
                "OwnerDisposing is the owner's explicit 'the patch was NOT applied — safe to retry' "
                + "verdict; any other code (or silence) loses the acked-at-mirror write");
        }
        finally
        {
            releaseGate.Set();
        }
    }
}
