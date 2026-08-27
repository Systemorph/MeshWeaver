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
/// 🚨 Issue #2346 — the residual half of #2305 / #2291, and the reason the symptom those issues name
/// came back on a tree that already contained their fix.
///
/// <para><b>What the user sees.</b> An agent round whose response cell reaches
/// <c>Status = Completed</c> while <c>Text</c> still reads <c>"Generating response..."</c>. On
/// 2026-08-27 that failed <c>OrleansAutoExecuteTest.AutoExecute_CreatesResponseCell_And_CompletesExecution</c>
/// on a PR whose whole diff is a workflow file plus markdown — <i>Expected "Generating response..."
/// to contain "Echo:"</i>, in 14.0 s against three 30 s budgets. Not a timeout: the round finished
/// and the content was wrong.</para>
///
/// <para><b>The mechanism.</b> <c>MeshNodeStreamCache</c> funnels every write to a path through one
/// per-path serial queue so each write diffs against the state its predecessor produced. #2305
/// delivered that by handing the predecessor's owner-ACKNOWLEDGED node to the successor
/// (<c>_pendingSelfWrites</c> → <c>MeshNodeStreamHandle.PatchBaseSource</c>). But the queue SLOT was
/// released by the caller's terminal — and for a busy owner that terminal is <c>UpdateRemote</c>'s
/// OPTIMISTIC emit at <c>UpdateResponseWaitBound</c> (~2 s), which carries no information about the
/// node's state at all. So on exactly the runs where the owner is slow (a loaded CI runner; a grain
/// still activating) the hand-off never happened, the successor fell back to the mirror — the node as
/// it stood BEFORE its predecessor's patch — and the owner three-way-merged that against live state
/// it had already moved past and REFUSED the conflicting leaf. The write's other leaves land, so the
/// owner still acks <c>Success</c> and nothing surfaces: one write, two verdicts.</para>
///
/// <para><b>Why this test is deterministic.</b> The interleaving that makes the production symptom
/// rare locally and common under CI load is CONSTRUCTED here, not raced for: the owner's merge turn
/// is parked behind a gated turn (the same device as <c>LateNackReenqueueTest</c>), so the first
/// write's ack provably cannot arrive inside its 2 s window. No cluster, no load, no sleep.</para>
/// </summary>
public class QueuedWriteAdvancesOnHandoffTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 120_000)]
    public async Task SecondQueuedWrite_WhenTheFirstsAckIsLate_DiffsAgainstTheFirstAndLands()
    {
        var ct = TestContext.Current.CancellationToken;
        const string id = "handoff-node";
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(
                new MeshNode(id, TestPartition) { Name = "initial", NodeType = "Markdown" })
            .Should().Emit();

        // Bring the owner up and WARM this hub's mirror of it. The warm mirror matters for the
        // control: with it, a queued write's base read emits synchronously inside Subscribe, so the
        // pre-fix dispatch below provably happens before the gate is released — the negative arm of
        // this test cannot accidentally pass by reading a base that is already correct.
        // 🚨 RequestHub, not Mesh — the root mesh hub is the ROUTER and must never be an END of a
        // delivery (RouterAsTestRequestOriginRatchetGuard / #2423).
        await RequestHub.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();
        var workspace = Mesh.GetWorkspace();
        var warm = await workspace.GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
        warm.Name.Should().Be("initial");

        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
            .Where(n => n is not null && n!.Name == "initial")
            .FirstAsync().Timeout(30.Seconds()).ToTask(ct);

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("the owner per-node hub must be live before its merge turn is parked");

        // Park the owner's merge executor: the write below is accepted by the owner's handler, but
        // its merge turn provably cannot run, so no PatchDataResponse can arrive inside the caller's
        // UpdateResponseWaitBound and the caller's terminal is the optimistic snapshot.
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
            "the gated turn must be running on the primary stream's executor before the writes");

        try
        {
            // WRITE 1 — its ack cannot arrive while the merge turn is parked, so this returns the
            // optimistic snapshot. Pre-fix, THAT is what released the queue slot.
            var first = await workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = "first" })
                .FirstAsync().Timeout(30.Seconds()).ToTask(ct);
            first.Name.Should().Be("first", "the caller's terminal is the optimistic snapshot");
            Output.WriteLine("[write 1] optimistic terminal received (owner's merge is parked)");

            // WRITE 2 — the successor. Subscribing is what enqueues it. Pre-fix the slot is already
            // free, so this dispatches HERE, synchronously, diffing against a mirror that predates
            // write 1. With the hand-off gate it waits for write 1's verdict instead.
            //
            // 🚨 TWO fields, and that is the whole point — it is what makes the loss SILENT and what
            // reproduces the production shape. `Name` collides with write 1 (a manufactured
            // conflict); `Description` does not. The owner refuses the colliding leaf and applies the
            // other, so the node DID change and it acks Success — no Conflict, no re-enqueue, no
            // error, nothing for the caller to observe. That is the agent round's response cell
            // exactly: `Status`/`Summary` land, `Text` is refused, and a Completed cell keeps
            // "Generating response...". A single-field write instead has EVERY leaf refused, which
            // trips the owner's nothing-landed backstop into a Conflict NACK and the existing
            // re-enqueue quietly rescues it — the reason this defect hides.
            MeshNode? secondTerminal = null;
            Exception? secondError = null;
            using var secondSub = workspace.GetMeshNodeStream(path)
                .Update(n => n with { Name = "second", Description = "successor-also-wrote-this" })
                .Subscribe(n => secondTerminal = n, ex => secondError = ex);
            Output.WriteLine("[write 2] enqueued");

            // Release: the owner drains the parked turn, then write 1's merge, then write 2's.
            releaseGate.Set();
            Output.WriteLine("[owner] merge executor released");

            // GROUND TRUTH — the store must end on the SUCCESSOR's value. Pre-fix write 2 shipped
            // base name="initial" while live had already moved to "first", so the owner refused the
            // leaf, kept "first", applied `Description`, and acked Success: this poll times out at
            // "first" with the description already written — one write, two verdicts — and the
            // caller was told nothing.
            //
            // The poll waits for BOTH halves so a partially-applied write can never read as a pass.
            MeshNode? persisted;
            try
            {
                persisted = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                    .SelectMany(_ => storage.Read(path, Mesh.JsonSerializerOptions))
                    .Where(n => n is not null && n!.Name == "second"
                                && n.Description == "successor-also-wrote-this")
                    .FirstAsync()
                    .Timeout(45.Seconds())
                    .ToTask(ct);
            }
            catch (TimeoutException ex)
            {
                var stored = await storage.Read(path, Mesh.JsonSerializerOptions)
                    .FirstAsync().Timeout(10.Seconds()).ToTask(ct);
                throw new TimeoutException(
                    $"The successor's write never landed whole. STORE-DUMP name='{stored?.Name ?? "(absent)"}' "
                    + $"description='{stored?.Description ?? "(null)"}' "
                    + $"version={stored?.Version.ToString() ?? "(n/a)"}. name='first' with the description "
                    + "written is the defect exactly: a queued write whose predecessor's ack was late must "
                    + "still diff against that predecessor, or the owner refuses the colliding leaf as a "
                    + "conflict that never happened, applies the rest, and acks Success (#2346 / #2305).",
                    ex);
            }

            persisted!.Name.Should().Be("second");
            persisted.Description.Should().Be("successor-also-wrote-this");
            secondError.Should().BeNull("the successor's write must not fault");
            secondTerminal.Should().NotBeNull("the successor's caller must receive its terminal");
            Output.WriteLine("[assert] store converged on the successor's value");
        }
        finally
        {
            releaseGate.Set();
        }
    }
}
