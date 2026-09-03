using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

// Core twin of MeshWeaver.Plugins/src/MeshWeaver.Hosting.Monolith.Test/LateNackReenqueueTest.cs (ported 2026-09-02):
// core's own CI cannot run the Plugins-hosted suite, so the 2026-09-02 regression of the owner-disposing
// NACK (PR #3070) reached main unseen. Keep the two in step.
namespace MeshWeaver.Graph.Test;

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
/// (so no response can arrive inside the 2s window), THEN dispose the owner — the disposal
/// NACK is necessarily LATE. The write must still reach durable storage via the re-enqueue.
/// Without Part 2 the late NACK is dropped and the storage poll times out at the pre-write
/// state.</para>
///
/// <para>🚨 Since #2661 the caller is NOT completed at the 2 s bound — a bound expiring is not
/// a commit, so the write's terminal is the owner's verdict wherever it arrives. Here that
/// verdict is the re-enqueued attempt's ack, chained back to the original caller, so this test
/// now also pins that a late NACK's remedy is reported to the writer instead of being swallowed
/// into a log line.</para>
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

        await RequestHub.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).Await(ct);

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull();

        // Park the owner's merge executor (same gating pattern as OwnerDisposalNackTest):
        // the cross-hub write below is accepted by the owner's handler but its merge turn
        // provably cannot run — no ack can arrive inside the caller's 2s window.
        var primary = nodeHub!.GetWorkspace().DataContext
            .GetDataSourceForType(typeof(MeshNode))!
            .GetStreamForPartition(null)!;
        // 🚨 No hand-woven gate. The turn → test signal is an AsyncSubject the parked turn
        // completes; the release travels back INTO that deliberately parked executor turn, so it
        // is a volatile flag polled under a bounded SpinUntil and written in the `finally` below.
        var gateEntered = new AsyncSubject<Unit>();
        var releaseGate = 0;
        primary.Update((Func<EntityStore?, ChangeItem<EntityStore>?>)(_ =>
        {
            gateEntered.OnNext(Unit.Default);
            gateEntered.OnCompleted();
            SpinWait.SpinUntil(() => Volatile.Read(ref releaseGate) == 1, TimeSpan.FromSeconds(60));
            return null;
        }), _ => { });
        try
        {
            await gateEntered.Should().Within(10.Seconds()).Emit(
                "the gated turn must be running on the primary stream's executor before the write");

            // Cross-hub cache write — the production mirror path (UpdateRemote via the
            // per-path queue). With the owner's merge parked, no verdict can arrive inside the
            // caller's response bound, so the caller is NOT settled here (#2661): it stays open
            // on the late watch, and the terminal it eventually gets is the re-enqueued
            // attempt's. Subscribe rather than await — awaiting a verdict the parked owner
            // cannot give is what would hang.
            var marker = $"post-nack-{Guid.NewGuid():N}"[..24];
            var workspace = Mesh.GetWorkspace();
            MeshNode? callerTerminal = null;
            Exception? callerError = null;
            using var writeSub = workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = marker })
                .Subscribe(n => callerTerminal = n, ex => callerError = ex);
            Output.WriteLine($"[write] patch posted with marker {marker}; owner merge is parked");

            // Fence on the patch actually being in flight before the dispose below — the armed
            // late watch is that fact, and it is the same fact the disposal NACK will land on.
            var registry = Mesh.ServiceProvider.GetRequiredService<LatePatchResponseRegistry>();
            await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
                .Where(_ => registry.ArmedCount > 0)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

            // Fence: the patch handler has provably run on the owner (registered the
            // disposal NACK) before the dispose below.
            await RequestHub.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
                .Should().Within(10.Seconds()).Emit();

            // Dispose the owner AFTER the caller's response bound has expired — its
            // OwnerDisposing NACK (posted from the ShutDown-phase disposal action) is
            // necessarily LATE. The armed late watch must consume it and re-enqueue the
            // ORIGINAL update against the fresh activation the re-posted patch brings up.
            nodeHub!.Dispose();
            Output.WriteLine($"[dispose] owner per-node hub disposal invoked for {path}");

            // Ground truth: the write eventually lands in durable storage. Without the
            // late-NACK re-enqueue the store stays frozen at 'initial' (the parked merge
            // turn died with the sync hub; nobody re-applies) and this poll times out —
            // the WaitForPersistedBeyond signature of the TwoSilo failure.
            var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
                .Where(n => n is not null && n.Name == marker)
                .FirstAsync().Timeout(45.Seconds()).Await(ct);
            persisted!.Name.Should().Be(marker,
                "a write whose owner NACKed OwnerDisposing must be re-enqueued and applied on "
                + "the fresh activation — never silently lost");

            // #2661: the re-attempt's verdict is the CALLER's verdict. Chaining it back is what
            // makes "saved" mean the owner committed, on the late path as much as the early one.
            await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .Where(_ => callerTerminal is not null || callerError is not null)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            callerError.Should().BeNull("the re-enqueued attempt landed, so the caller must see a success");
            callerTerminal!.Name.Should().Be(marker,
                "the caller's terminal is the verdict of the attempt that actually committed");
        }
        finally
        {
            Volatile.Write(ref releaseGate, 1);
        }
    }
}
