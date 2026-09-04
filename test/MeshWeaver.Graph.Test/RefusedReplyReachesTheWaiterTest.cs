using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#3303 — teardown drops the ANSWER, not just new work.</b> A correlated REPLY that routing
/// refuses because the parent has reached <see cref="MessageHubRunLevel.DisposeHostedHubs"/> must
/// still reach the caller parked on it.
///
/// <para><b>The defect.</b> The shutdown guard refuses every delivery it would have to hand to a
/// parent past that mark, and words its refusal as advice to the SENDER — <i>"The address may
/// reactivate (recycle / restart); retry to get the authoritative answer."</i> For a REQUEST that
/// is exact: the sender is the party waiting and re-probing is something it can do. For a REPLY it
/// is advice to nobody. A reply's sender is the RESPONDER — it is waiting for nothing and will not
/// re-send — while the party actually parked on the message, the request's originator, is never
/// told at all. So the reply is dropped and the caller burns its whole verdict budget for an answer
/// the owner believes it sent.</para>
///
/// <para><b>Why the earlier fix does not cover it.</b> #3196 made the owner's ack gate
/// claim-then-VERIFY: a post the transport refuses falls through to the late-verdict sink instead
/// of being discarded with the once-only gate latched behind it. But that seam reads the delivery
/// the POST returned, and the post here is ACCEPTED — the owner's own run level is still open. The
/// refusal happens a turn later, in routing, where <c>PostPatchVerdict</c> cannot see it, and
/// <c>RegisterOwnerDisposingNack</c> then finds the gate claimed and stands down as
/// "already answered". <c>RoutePatchVerdict</c>'s own remarks name this gap and place the fix
/// there: <i>"a correlated reply that HierarchicalRouting cannot forward is DROPPED with nobody
/// told, and the process still holds the registry that could take it"</i>.</para>
///
/// <para><b>Why this test is deterministic where the field detector is not.</b>
/// <c>NackReachesTheWaiterDuringTeardownTest</c> reaches the same state by RACING a released merge
/// turn against the mesh's own advance to <c>DisposeHostedHubs</c> — it caught this defect (it
/// dequeued #3293 from the merge queue and reddened #3296/#3297/#3299/#3300/#3305), but only on the
/// runs where the race lands, and it passed 15/15 locally with the defect fully present. Here the
/// state is HELD OPEN BY CONSTRUCTION: the owner's action block is parked on an accepted turn, so
/// the <c>ShutdownRequest</c> its own disposal posts queues BEHIND that turn and the owner cannot
/// leave <c>Started</c>; and the parent cannot leave <c>DisposeHostedHubs</c> until the owner
/// finishes disposing. Both halves of "owner still open, parent already past" are facts the test
/// fences on, not timings it hopes for.</para>
///
/// <para><b>Both controls are here, and they are what make the green mean something.</b> A live
/// waiter hub PROVES the route works before the teardown (positive anchor) and PROVES the reply
/// never travelled after it (negative control) — so the only thing that can answer the caller is
/// the in-process hand-over. Neither control depends on which layer performs it.</para>
/// </summary>
public class RefusedReplyReachesTheWaiterTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>A message whose handler parks the owner's single-threaded action block.</summary>
    private record ParkTurn;

    private static readonly Address ParentAddress = new("refused-reply-parent", "1");
    private static readonly Address OwnerAddress = new("refused-reply-owner", "1");

    /// <summary>The caller's address — a LIVE hub hosted by the mesh, so "the reply did not
    /// arrive" is a measured fact about the transport rather than a property of a dead slot.</summary>
    private static readonly Address WaiterAddress = new("refused-reply-waiter", "1");

    private const string RouteProbe = "route-probe";

    [Fact(Timeout = 120_000)]
    public async Task AReplyRefusedByAShuttingDownParent_StillReachesTheArmedWatch()
    {
        var ct = TestContext.Current.CancellationToken;

        // A live waiter. Everything it receives is recorded, which is what turns the assertions
        // below into a pair of controls rather than one hopeful wait.
        var arrived = new ConcurrentQueue<PatchDataResponse>();
        var waiter = Mesh.GetHostedHub(WaiterAddress, c => c
                .WithTypes(typeof(PatchDataResponse))
                .WithHandler<PatchDataResponse>((_, d) =>
                {
                    arrived.Enqueue(d.Message);
                    return d.Processed();
                }),
            HostedHubCreation.Always);
        waiter.Should().NotBeNull();

        // The waiter's registry entry, armed exactly as UpdateRemote arms it before posting a
        // cross-hub patch. This registry IS the seam a late owner verdict arrives on, and the
        // dispatch REMOVES the entry — so the verdict landing is directly observable.
        var registry = Mesh.ServiceProvider.GetRequiredService<LatePatchResponseRegistry>();
        var requestId = $"refused-reply-{Guid.NewGuid():N}";
        var verdict = new AsyncSubject<PatchDataResponse>();
        registry.Register(
            requestId,
            $"{TestPartition}/refused-reply",
            response => { verdict.OnNext(response); verdict.OnCompleted(); },
            _ => { });

        // 🚨 No hand-woven gate: the turn → test signal is an AsyncSubject the parked turn
        // completes; the release travels back INTO the deliberately parked turn, so it is a
        // volatile flag polled under a bounded SpinUntil and written in the `finally`.
        var turnEntered = new AsyncSubject<Unit>();
        var releaseTurn = 0;

        var parent = Mesh.GetHostedHub(ParentAddress, c => c, HostedHubCreation.Always);
        parent.Should().NotBeNull();
        var owner = parent!.GetHostedHub(OwnerAddress, c => c
                .WithTypes(typeof(ParkTurn), typeof(PatchDataResponse))
                .WithHandler<ParkTurn>((h, d) =>
                {
                    turnEntered.OnNext(Unit.Default);
                    turnEntered.OnCompleted();
                    SpinWait.SpinUntil(
                        () => Volatile.Read(ref releaseTurn) == 1, TimeSpan.FromSeconds(60));
                    // The owner's verdict, minted on a turn that outlived the START of teardown —
                    // the production shape exactly: the merge turn wakes when the owner begins
                    // shutting down, applies the patch and answers the writer. The post is
                    // ACCEPTED (this hub's own run level is still open); routing refuses it a turn
                    // later because the parent has moved on.
                    h.Post(
                        new PatchDataResponse(true, h.Version),
                        o => o.WithTarget(WaiterAddress).WithProperty(PostOptions.RequestId, requestId));
                    return d.Processed();
                }),
            HostedHubCreation.Always);
        owner.Should().NotBeNull();

        try
        {
            // POSITIVE CONTROL, before anything is torn down: this exact route carries this exact
            // message type from this owner to this waiter. Without it, "the reply never arrived"
            // below would be indistinguishable from "that address was never reachable".
            owner!.Post(
                new PatchDataResponse(true, 0L) { Error = RouteProbe },
                o => o.WithTarget(WaiterAddress).WithProperty(PostOptions.RequestId, "probe-nobody-armed"));
            await Observable.Interval(TimeSpan.FromMilliseconds(25)).StartWith(0L)
                .Where(_ => arrived.Any(r => r.Error == RouteProbe))
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Output.WriteLine("[probe] owner → waiter route is live while nothing is shutting down");
            registry.ArmedRequestIds.Should().Contain(requestId,
                "the probe carries a correlation id NOBODY armed, so it must travel as ordinary "
                + "traffic and leave the real watch alone");

            parent.Post(new ParkTurn(), o => o.WithTarget(OwnerAddress));
            await turnEntered.Should().Within(20.Seconds()).Emit(
                "the parked turn must be holding the owner's action block before the parent is torn "
                + "down — that park is what keeps the owner's own ShutdownRequest queued behind it");

            // Tear the PARENT down, not the owner. Its DisposeHostedHubs phase disposes the owner,
            // whose ShutdownRequest lands BEHIND the parked turn — so the owner stays below
            // DisposeHostedHubs — and the parent cannot advance past DisposeHostedHubs until the
            // owner reports it is done. The #3303 window is therefore open and pinned.
            parent.Dispose();
            Output.WriteLine("[dispose] parent disposal invoked");

            // 🚨 The fence, and it is a control as much as a wait: it is the exact precondition of
            // the refusal, so without it the reply below would route normally and the assertion
            // could pass without the defect's state ever existing.
            await Observable.Interval(TimeSpan.FromMilliseconds(25)).StartWith(0L)
                .Where(_ => parent.RunLevel >= MessageHubRunLevel.DisposeHostedHubs
                            && owner!.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Output.WriteLine(
                $"[fence] parent={parent.RunLevel} owner={owner!.RunLevel} — a reply posted now is "
                + "accepted by the owner and refused by routing");

            Volatile.Write(ref releaseTurn, 1);

            // THE ASSERTION.
            await verdict.Should().Within(TestTimeouts.Convergence).Emit(
                "the owner minted a verdict for a request a caller is armed and waiting on, and the "
                + "transport refused it because the PARENT is past DisposeHostedHubs. A refusal is "
                + "advice the SENDER can act on; for a reply the sender is the responder and the "
                + "party parked on the message is never told, so dropping it here is #3303 — the "
                + "caller then burns its whole WriteVerdictBound for an answer that was already "
                + "minted and posted");

            // Keyed on THIS request rather than on the count: the registry is mesh-wide, so a
            // total is a claim about every writer in the process and not about this one.
            registry.ArmedRequestIds.Should().NotContain(requestId,
                "the dispatch REMOVES the entry, so a consumed watch is the observable fact that "
                + "the verdict reached its waiter rather than merely being logged");

            // NEGATIVE CONTROL: the reply did NOT travel. The waiter is alive and provably
            // reachable (the probe above), so its silence here is the refusal — which is what makes
            // the emission above evidence of the hand-over and not of ordinary delivery.
            arrived.Should().OnlyContain(r => r.Error == RouteProbe,
                "the parent refused this reply, so nothing can have carried it to the waiter's hub; "
                + "if it arrived, the routing refusal did not happen and the assertion above proved "
                + "nothing about the hand-over");
            Output.WriteLine($"[control] waiter hub received {arrived.Count} message(s), all probes");
        }
        finally
        {
            // In a `finally` so a failing assertion above cannot strand the parked turn.
            Volatile.Write(ref releaseTurn, 1);
        }
    }
}
