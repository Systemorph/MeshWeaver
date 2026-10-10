using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Systemorph/MeshWeaver#1198 - a recursive delete's commit stage failed a leaf for 'no progress' while the leaf
/// was merely QUEUED behind a write lane that kept serving other writers.
///
/// <para><b>Production shape</b> (memex 2026-10-09): <c>[DeleteNode:commit] ... made no progress for 25s - 0 of 1
/// planned path(s) removed ... pg:Postgres(cap 1) 219 waiting, 1 in flight ... 1325 admission(s) waited >= 1 s
/// during this stage</c>. The cap-1 <c>pg:</c> write pool is one process-wide gate; a leaf's removal is one write on
/// it, so it cannot remove anything before its turn, and the commit's watchdog - which counts only this delete's
/// own removals - read the wait as a stall although the lane was advancing the whole time.</para>
///
/// <para><b>The repro.</b> A one-path delete whose store write is admitted through a cap-1 <c>pg:</c> pool that is
/// already loaded with <see cref="Holders"/> unrelated writes of <see cref="HoldEach"/> each - about 7 s of queue
/// against a 4 s stage budget. The lane never stops: a slot is granted every 600 ms. Before the change the
/// watchdog fires at 4 s; after it, the lane's advance credits the watchdog and the delete completes in its turn.
/// The sibling test <c>DeleteCommitTimeoutSeparatesStarvedFromStuckTest</c> pins the other half: a leaf that is
/// granted and then hangs is still failed at one budget, naming the pool.</para>
/// </summary>
public class DeleteCommitQueuedBehindAMovingLaneTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string NodeId = "commit-queued-lane";

    /// <summary>A pg: name, so the pool gets the same cap 1 as the real Postgres write pool.</summary>
    private const string LaneName = "pg:1198-moving-lane";

    private const int Holders = 12;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan HoldEach = TimeSpan.FromMilliseconds(600);

    private readonly LatentDeleteStorageAdapter storage = new(new InMemoryStorageAdapter());

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageAdapter>(storage);
            services.AddSingleton(new MeshOperationOptions { Timeout = Budget });
            return services;
        }));

    [Fact(Timeout = 120000)]
    public async Task ADeleteQueuedBehindAMovingWriteLane_IsNotFailedForWaitingItsTurn()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>();
        var lane = registry.Get(LaneName);

        await NodeFactory.CreateNode(
                new MeshNode(NodeId, TestPartition) { Name = NodeId, NodeType = "Markdown" })
            .Should().Within(TestTimeouts.Convergence).Emit(
                cancellationToken: TestContext.Current.CancellationToken);
        var path = $"{TestPartition}/{NodeId}";
        storage.LatencyRoot = path;
        storage.DeleteLane = lane;

        // Unrelated writers: each holds the single slot for HoldEach, reactively (a timer, never a sleep),
        // so the lane keeps GRANTING slots while the delete's own write waits for its turn.
        var holders = new IDisposable[Holders];
        for (var i = 0; i < Holders; i++)
            holders[i] = lane
                .InvokeObservable(ct => Observable.Timer(HoldEach).Select(tick => 0))
                .Subscribe(_ => { }, _ => { });
        try
        {
            SpinWait.SpinUntil(() => lane.CurrentlyWaiting >= Holders - 1, TimeSpan.FromSeconds(10));
            lane.CurrentlyWaiting.Should().BeGreaterThanOrEqualTo(Holders - 1,
                "the lane must be loaded before the delete runs - otherwise this measures an idle pool");

            var startedAt = DateTime.UtcNow;
            var deleted = await NodeFactory.DeleteNode(path).Should().Within(TestTimeouts.WriteConvergence).Emit(
                "a leaf queued behind a lane that keeps serving other writers is waiting its turn, not stuck",
                cancellationToken: TestContext.Current.CancellationToken);
            var elapsed = DateTime.UtcNow - startedAt;

            deleted.Should().BeTrue();
            elapsed.Should().BeGreaterThan(Budget,
                "the test only discriminates if the delete really waited longer than the stage budget - "
                + $"it took {elapsed.TotalSeconds:0.0}s against {Budget.TotalSeconds:0}s");
            (await storage.Inner.Exists(path).Should().Within(TestTimeouts.Convergence).Emit(
                    cancellationToken: TestContext.Current.CancellationToken))
                .Should().BeFalse("the node must be gone");
        }
        finally
        {
            foreach (var holder in holders)
                holder.Dispose();
        }
    }
}
