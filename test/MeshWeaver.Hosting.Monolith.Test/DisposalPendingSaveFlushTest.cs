using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
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
/// 🚨 Deterministic repro of the acked-write-lost-on-teardown defect behind the
/// <c>TwoSiloRecycleConvergenceTest</c> flake (main run 30068597014, shard 2): the
/// post-recycle patch raced the owner hub's <c>Dispose()</c> into the Quiescing window
/// (the ShuttingDown NACK gate only starts at <c>RunLevel &gt;= DisposeHostedHubs</c>),
/// was applied and ACKED (<c>PatchDataResponse</c> Success) — and then durably LOST:
/// the per-node hub's persistence sampler (<c>MeshDataSource.SubscribeToOwnDeletion</c>)
/// debounces updates with <c>Sample(200 ms)</c>, and <c>DisposeImpl</c> disposed that
/// subscription with the save still pending. The store never advanced past the
/// pre-recycle version, so the test's <c>WaitForPersistedBeyond</c> timed out.
///
/// <para>This test forces the exact interleaving without Orleans: write through the
/// canonical own-node <c>stream.Update</c>, observe the own stream carry the write
/// (the sampler — an earlier subscriber of the same stream — has necessarily seen it),
/// then dispose the per-node hub INSIDE the 200 ms sample window. On main the pending
/// save is dropped both ways the race can land (sampler disposed before the window
/// elapses, or its <c>RunLevel &gt; Started</c> branch discarding the sampled value), so
/// the marker never reaches storage — RED. With the final-flush dispose action
/// (<c>FlushPendingOwnSave</c>, the update-path twin of <c>MeshNodeTypeSource</c>'s
/// create/delete <c>FlushPendingWrites</c>) the write becomes durable — GREEN.</para>
/// </summary>
public class DisposalPendingSaveFlushTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60_000)]
    public async Task WriteAckedInsideSampleWindow_SurvivesHubDisposal()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/flush-node";
        await NodeFactory.CreateNode(
                new MeshNode("flush-node", TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        // Activate the per-node hub (lazy) and grab its workspace — same shape as
        // WorkspaceUpdateMeshNodePropagationTest.
        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("the per-node hub must be activated by the GetDataRequest above");
        var workspace = nodeHub!.GetWorkspace();

        // Baseline: the CREATE is durable before we race the UPDATE against disposal,
        // so the final read can only be satisfied by the update itself.
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);

        // The update — canonical own-node stream.Update, the same primitive a cross-hub
        // PatchDataRequest commits through. Await the ack emission (the write is applied).
        var marker = $"updated-{Guid.NewGuid():N}"[..16];
        await workspace.GetMeshNodeStream()
            .Update(n => n with { Name = marker })
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);

        // Confirm the own stream carries the write. The persistence sampler subscribed to
        // this same stream at hub init — emissions fan out to subscribers in subscription
        // order on the emission walk, so once WE see the marker the sampler has tracked it
        // and is now holding the save in its 200 ms Sample buffer.
        await workspace.GetMeshNodeStream()
            .Where(n => n is not null && n.Name == marker)
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);

        // Dispose the per-node hub IMMEDIATELY — inside the sample window. This is the
        // idle-recycle / owner-teardown path: the pending debounced save must not be
        // dropped on the floor.
        nodeHub!.Dispose();
        Output.WriteLine($"[repro] disposed {path} with save for '{marker}' pending in the Sample window");

        // The acked write MUST become durable. storage.Read does not activate hubs, so
        // nothing can resurrect the value except the persistence pipeline itself.
        var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null && n.Name == marker)
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);

        persisted!.Name.Should().Be(marker,
            "a write that was applied and acknowledged before hub disposal must survive the "
            + "teardown — the dispose-time flush persists the pending sampled state instead of "
            + "dropping it (the TwoSiloRecycleConvergenceTest post-recycle orphan, run 30068597014)");
    }
}
