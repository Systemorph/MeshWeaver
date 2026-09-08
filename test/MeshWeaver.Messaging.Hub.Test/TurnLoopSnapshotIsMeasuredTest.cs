using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>The two fields that decide "wedged or queued" must MOVE</b> —
/// <see href="https://github.com/Systemorph/MeshWeaver/issues/3593">#3593</see>, and the reading
/// they exist to serve in <see href="https://github.com/Systemorph/MeshWeaver/issues/2543">#2543</see>.
///
/// <para><b>The defect this guards against returning.</b> <c>GetQueueSnapshot</c> printed
/// <c>exec=0</c> for the whole life of the turn loop — a hard-coded literal left behind by the
/// TPL-Dataflow pump, true of every hub in every state. Read as *"no drain is running"* it is the
/// single most load-bearing clause a stall report can carry, and it measured nothing. Two weeks of
/// #2543 were argued from a capture containing it. <c>drainsInFlight</c> replaced it; the whole
/// point of the replacement is that it is a live count, so a test that never observes it non-zero
/// would leave the fleet exactly where it was.</para>
///
/// <para><b>Why a POSITIVE control specifically.</b> A field frozen at <c>0</c> passes every
/// assertion that only ever sees an idle hub — that is precisely how the literal survived. So the
/// subject here is a hub whose handler is genuinely on the block, held there, while the snapshot is
/// taken from another thread. The negative control beside it is what stops the opposite regression
/// (a field frozen at <c>1</c>, or an <c>Executing</c> that is never cleared).</para>
///
/// <para><b>What is NOT claimed.</b> This says nothing about what occupies
/// <c>portal/nodeops</c> during a bake. It makes the instrument that would answer that question
/// trustworthy, and it calibrates the reading: the state built below is what
/// <c>drainsInFlight=1</c> + <c>Executing(T, N ms)</c> means — a THREAD blocked inside the handler,
/// with the queue behind it going nowhere until it returns.</para>
/// </summary>
public class TurnLoopSnapshotIsMeasuredTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Park : IRequest<Parked>;

    private record Parked;

    private record Trailer : IRequest<Trailed>;

    private record Trailed;

    /// <summary>
    /// 🚨 THE POSITIVE CONTROL. With a thread blocked inside the handler, the snapshot must say so:
    /// a drain body executing, the turn named, the drain latched — and a message posted behind it
    /// still sitting in the buffer, because <c>DrainLoop</c> advances only when a turn's observable
    /// terminates and nothing else ever restarts it.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSnapshot_ReportsTheBlockedTurn_WhileAHandlerHoldsTheBlock()
    {
        var entered = new AsyncSubject<Unit>();
        var released = 0;
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "turn-loop-snapshot"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Park>((h, d) =>
            {
                entered.OnNext(Unit.Default);
                entered.OnCompleted();
                // What a genuinely long turn does to this block. Bounded, and released from a
                // finally below so a failing assertion cannot strand the mesh's teardown.
                SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TestTimeouts.Convergence);
                return d.Processed();
            })
            .WithHandler<Trailer>((h, d) =>
            {
                h.Post(new Trailed(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        string snapshot;
        try
        {
            var parked = victim.Observe(new Park(), o => o.WithTarget(victim.Address));
            using var parkedSub = parked.Subscribe(_ => { }, _ => { });

            await entered.Should().Within(TestTimeouts.Convergence)
                .Emit("the handler must actually be on the block, or this test measures an idle hub");

            // Queued BEHIND the parked turn, from a hub that is not the victim, so the post itself
            // cannot be what is blocked.
            Mesh.Post(new Trailer(), o => o.WithTarget(victim.Address));

            snapshot = victim.GetTurnLoopSnapshot();
        }
        finally
        {
            Interlocked.Exchange(ref released, 1);
        }

        snapshot.Should().Contain("Executing(Park",
            "a handler IS on the block and the snapshot must name it — this is the field the whole "
            + "of #2543 was read from");
        snapshot.Should().NotContain("drainsInFlight=0",
            "a thread is blocked inside that handler right now, so the count of executing drain "
            + "bodies cannot be zero — reading zero here is the `exec=0` literal returning, and it "
            + "is what made the wedge/queue question unanswerable");
        snapshot.Should().Contain("draining=True",
            "the drain is latched for as long as the loop has not found its queue empty");
    }

    /// <summary>
    /// 🚨 THE NEGATIVE CONTROL, and the half that makes the other one mean anything. An idle hub
    /// must report NO executing turn and no drain body — otherwise the assertions above would pass
    /// against fields stuck at a constant, which is exactly the defect being guarded.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheSnapshot_ReportsAnIdleHub_WhenNoTurnIsInFlight()
    {
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "turn-loop-idle"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Trailer>((h, d) =>
            {
                h.Post(new Trailed(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        // Awaited to completion, so the hub is measurably back to idle rather than merely young.
        await victim.Observe(new Trailer(), o => o.WithTarget(victim.Address))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the hub must answer once, so what follows describes a hub that has finished work "
                + "rather than one that has not started any");

        var snapshot = victim.GetTurnLoopSnapshot();

        snapshot.Should().NotContain("Executing(",
            "no handler is on the block, so the snapshot must not name one — a snapshot that always "
            + "reports an executing turn would make the positive control above vacuous");
        snapshot.Should().Contain("drainsInFlight=0",
            "no drain body is running on an idle hub");
    }
}
