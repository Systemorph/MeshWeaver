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
//
// 🚨 EDITING THIS FILE? The Plugins copy is a separate file and this comment cannot make anyone open
// it — #3345: #3291 rewrote this twin to the no-forced-teardown contract, left the Plugins one
// asserting the contract it had just deleted, and the pin bump a day later produced a 55-second
// VERDICT_TIMEOUT that was filed as a core regression and bisected across five commits. What holds
// the two together now is Plugins' TeardownTwinParityTest, which compares this body against its own
// below the namespace line, at MW_PLATFORM_REF. It reddens in the PIN BUMP, not here — so a change
// to this file is not done until the Plugins copy carries it too.
// See Doc/Architecture/CrossRepoPairGate → "Shape 7's worst form".
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
/// (so no response can arrive inside the 2s window), THEN dispose the owner — whatever verdict
/// the owner produces is necessarily LATE, and the write must still reach durable storage and
/// the caller must still see it as its own success. Since the teardown lets accepted work finish
/// (Doc/Architecture/TeardownLayers), the parked turn releases itself when the owner starts
/// shutting down and the queued merge then RUNS — so the late verdict here is normally the
/// commit's own ack, dispatched to the armed watch; the OwnerDisposing NACK and its re-enqueue
/// remain the safety net for a merge the owner could not run at all (a post refused by an
/// already-closing sync hub, a store that completes before the echo). Both paths end in the same
/// ground truth this test asserts. Without the late watch either verdict is dropped, the caller is
/// never settled, and durable storage stays at the pre-write state.</para>
///
/// <para>🚨 The two assertions are in this order ON PURPOSE — #3477. The CALLER'S TERMINAL is the
/// only wait here with a contract of its own (<c>UpdateRemote</c> settles every write inside its
/// own bound and says why), so it is waited on FIRST and durable storage is checked second. The old
/// order polled storage for a hand-written 45 s — below what one re-enqueue may legitimately cost,
/// and inside a region that is silent by design — so the failure could only ever read "The
/// operation has timed out.", with the write's own diagnosis discarded unread. The new order also
/// makes the claim STRONGER: the owner acks only after its durable flush, so a write reported
/// committed while no store holds it fails on the storage assertion and says so.</para>
///
/// <para>🚨 Since #2661 the caller is NOT completed at the 2 s bound — a bound expiring is not
/// a commit, so the write's terminal is the owner's verdict wherever it arrives. Here that
/// verdict is the re-enqueued attempt's ack, chained back to the original caller, so this test
/// now also pins that a late NACK's remedy is reported to the writer instead of being swallowed
/// into a log line.</para>
/// </summary>
public class LateNackReenqueueTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // 240_000 ms, not TestTimeouts.TestMilliseconds: an attribute argument must be a constant, so
    // the property cannot be written here. The value must still DOMINATE it — 216 s at the CI
    // factor (Convergence 108 s x OuterMargin 2) — or the xunit kill pre-empts the inner wait and
    // the failure cannot say what it was waiting for.
    //
    // 🚨 It was 90_000, which is BELOW TestTimeouts.Convergence on a runner (108 s), so every
    // internal wait in this test was killed anonymously before it could report. That is exactly the
    // defect TestTimeouts exists to prevent, and it is what the 2026-09-09 sighting of this test's
    // sibling looked like: "Test execution timed out after 90000 milliseconds", no assertion, no
    // named wait (#3477).
    [Fact(Timeout = 240_000)]
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
            .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct);

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
        // The parked turn ALSO observes the owner's shutdown: accepted work finishes its job when
        // the owner goes down, it does not sit on the block waiting to be killed. The owner's
        // teardown no longer force-tears a parked sync hub down (Doc/Architecture/TeardownLayers),
        // so a turn blind to the shutdown would hold the owner at DisposeHostedHubs — honestly
        // pending — until the test's own `finally`, and the caller would burn its verdict budget.
        var gateEntered = new AsyncSubject<Unit>();
        var releaseGate = 0;
        var owner = nodeHub!;
        primary.Update((Func<EntityStore?, ChangeItem<EntityStore>?>)(_ =>
        {
            gateEntered.OnNext(Unit.Default);
            gateEntered.OnCompleted();
            SpinWait.SpinUntil(
                () => Volatile.Read(ref releaseGate) == 1 || owner.IsShuttingDown,
                TimeSpan.FromSeconds(60));
            return null;
        }), _ => { });
        try
        {
            await gateEntered.Should().Within(TestTimeouts.Quick).Emit(
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
                .Should().Within(TestTimeouts.Quick).Emit();

            // Dispose the owner AFTER the caller's response bound has expired — its
            // OwnerDisposing NACK (posted from the ShutDown-phase disposal action) is
            // necessarily LATE. The armed late watch must consume it and re-enqueue the
            // ORIGINAL update against the fresh activation the re-posted patch brings up.
            nodeHub!.Dispose();
            Output.WriteLine($"[dispose] owner per-node hub disposal invoked for {path}");

            // 🚨 THE CALLER'S TERMINAL IS WAITED ON FIRST, and that ordering is the point.
            // #2661: the re-attempt's verdict is the CALLER's verdict. Chaining it back is what
            // makes "saved" mean the owner committed, on the late path as much as the early one —
            // and it is the only wait here with a CONTRACT of its own: UpdateRemote settles every
            // write inside its own bound and says why (VERDICT_TIMEOUT / OwnerUnreachable, carrying
            // the corr= trail of every attempt). TestTimeouts.Convergence derives from that bound
            // precisely so this assertion dominates it.
            //
            // 🚨 The old order asked durable storage FIRST, for a hand-written 45 s. Storage is a
            // PROXY with no bound of its own, 45 s is below what one re-enqueue may legitimately
            // cost before the framework itself gives up (BaseStateWaitBound 30 s, then
            // WriteVerdictBound 31 s measured from the RE-ATTEMPT's post — additive, not
            // alternatives), and it is not CI-scaled while every other wait in this test is. So the
            // window closed inside a region that is silent BY DESIGN and the failure could only ever
            // read "System.TimeoutException : The operation has timed out." — which is verbatim what
            // #3477 recorded, with the write's own diagnosis discarded unread.
            await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .Where(_ => callerTerminal is not null || callerError is not null)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            callerError.Should().BeNull("the re-enqueued attempt landed, so the caller must see a success");
            callerTerminal!.Name.Should().Be(marker,
                "the caller's terminal is the verdict of the attempt that actually committed");

            // Ground truth, and now a STRICTLY STRONGER claim than the old poll made. On the owner
            // the ack FOLLOWS the durable flush (PATCH_MERGE_STAMPED → PATCH_ECHO_SEEN →
            // IPostCommitFlush.Flush → ack), so the instant the caller holds a success the value is
            // already in the store. Without the late-NACK re-enqueue the store stays frozen at
            // 'initial' (the parked merge turn died with the sync hub; nobody re-applies) — and a
            // PHANTOM success, the write reported saved while no store anywhere holds it, fails
            // HERE and says so, instead of hiding inside an anonymous storage timeout.
            var persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
                .Where(n => n is not null && n.Name == marker)
                .FirstAsync().Timeout(TestTimeouts.Quick).Await(ct);
            persisted!.Name.Should().Be(marker,
                "a write whose owner NACKed OwnerDisposing must be re-enqueued and applied on "
                + "the fresh activation — never silently lost, and never reported to the caller as "
                + "committed while durable storage still holds the pre-write state");
        }
        finally
        {
            Volatile.Write(ref releaseGate, 1);
        }
    }
}
