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
/// Scenario guard for the recycle→reactivate-on-write flow behind
/// <c>TwoSiloRecycleConvergenceTest</c> (main runs 30068597014 / 30079395006): a
/// cross-hub <c>stream.Update</c> that lands on a COLD per-node hub (disposed →
/// reactivated by the write itself) must give read-your-write — a successful Update
/// completion implies the write is applied and reaches durable storage; it must
/// never be silently dropped by the cold-store window (merge turn racing the initial
/// load) while the ack watcher mistakes the load echo for the commit.
///
/// <para>The deterministic pin of the ack-identity mechanism is
/// <c>MeshWeaver.Data.Test.PatchAckWriteIdentityTest</c>; this test drives the same
/// window through the REAL pipeline: dispose the per-node hub, then immediately
/// write through the cross-hub cache path (PatchDataRequest → cold reactivation →
/// init-gate release → merge/load race) and require the store to advance.</para>
/// </summary>
public class ColdReactivationPatchReadYourWriteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 60_000)]
    public async Task UpdateAgainstDisposedHub_ReactivatesAndPersists()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = $"{TestPartition}/cold-patch-node";
        await NodeFactory.CreateNode(
                new MeshNode("cold-patch-node", TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        // Warm the per-node hub and wait until the CREATE is durable.
        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).ToTask(ct);

        // Recycle: dispose the per-node hub — the next write must reactivate it COLD.
        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull();
        nodeHub!.Dispose();
        Output.WriteLine($"[recycle] disposed per-node hub for {path}");

        // Post-recycle write through the cross-hub cache path (PatchDataRequest to a
        // cold hub — the init gate releases the patch into the merge/load race).
        // Resilient on the reactive tick, mirroring the TwoSilo test: the very first
        // attempt can race the disposing hub's ShuttingDown NACK window.
        var marker = $"post-recycle-{Guid.NewGuid():N}"[..24];
        var workspace = Mesh.GetWorkspace();
        await Observable.Interval(TimeSpan.FromMilliseconds(500)).StartWith(0L)
            .SelectMany(_ => workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = marker })
                .Take(1)
                .Select(_ => true)
                .Catch<bool, Exception>(_ => Observable.Return(false)))
            .Where(ok => ok)
            .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
        Output.WriteLine($"[write] update acked with marker {marker}");

        // Read-your-write: a successful cross-hub Update against the reactivated hub
        // must reach durable storage — the store may NEVER stay frozen at the
        // pre-recycle state (the acked-write-loss signature: WaitForPersistedBeyond
        // timing out at the pre-recycle version).
        var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null && n.Name == marker)
            .FirstAsync().Timeout(15.Seconds()).ToTask(ct);
        persisted!.Name.Should().Be(marker,
            "a write acknowledged against a cold-reactivated per-node hub must be applied "
            + "and durable — never dropped by the merge/load race with the ack watcher "
            + "taking the load echo for the commit (runs 30068597014 / 30079395006)");
    }
}
