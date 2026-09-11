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
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#4023 — the mesh's router dropped a reply its recipient was still waiting for.</b>
///
/// <para><b>The defect.</b> <c>RoutingServiceBase.RouteInMesh</c> dropped EVERY delivery once the
/// mesh reached <see cref="MessageHubRunLevel.DisposeHostedHubs"/> — no NACK, no log, and a
/// <c>Forwarded</c> result — because "recipients are likely also disposing". A hosted hub whose
/// parent is in that phase has only just been ASKED to dispose: it is Quiescing, waiting for the
/// replies it is still owed. <c>NackReachesTheWaiterDuringTeardownTest</c> caught the consequence
/// intermittently (CI runs 34513634943 and 34581527040): the owner committed the patch and posted
/// its ack while the mesh was Quiescing, the mesh routed that ack one turn after its own
/// <c>DisposeHostedHubs</c> turn, and the ack vanished — the caller's hub then sat out its whole
/// quiesce budget for it and the writer reported <c>OwnerUnreachable</c> after 31 s.</para>
///
/// <para><b>Why this test is deterministic where that one is not.</b> That test reaches the state by
/// racing a released merge turn against the mesh's own phase changes (2 failures in 2000 stressed
/// local iterations). Here the state is HELD OPEN BY CONSTRUCTION: the recipient's action block is
/// parked on an accepted turn, so the <c>ShutdownRequest</c> its disposal posts queues behind it and
/// it stays below <c>DisposeHostedHubs</c>; and the mesh cannot leave <c>DisposeHostedHubs</c> until
/// that recipient has disposed. Both halves of "mesh past the mark, recipient still alive" are
/// fences, not timings. The reply is handed to the mesh's <see cref="IRoutingService"/> exactly as
/// the mesh's routing handler hands it (<c>MeshBuilder</c>: <c>DeliverMessage(delivery.Package(…))</c>).</para>
///
/// <para><b>Both controls, and why each is falsifiable.</b> The positive one fails on the unfixed
/// router (the reply is dropped). The negative one — the SAME payload without a correlation id — fails
/// on a fix that simply stops dropping: teardown must still refuse traffic nobody is waiting for,
/// which is what keeps the storm class the guard was written for closed. Both are judged once the
/// mesh is <c>Dead</c>, the instant after which nothing more can arrive — a cause, not a clock.</para>
/// </summary>
public class ReplyRoutedDuringMeshTeardownReachesItsWaiterTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>A message whose handler parks the recipient's single-threaded action block.</summary>
    private record ParkTurn;

    private static readonly Address WaiterAddress = new("reply-teardown-waiter", "1");
    private static readonly Address ResponderAddress = new("reply-teardown-responder", "1");

    private const string RouteProbe = "route-probe";
    private const string AnsweredMarker = "the-answer";
    private const string UncorrelatedMarker = "nobody-waits-for-this";

    [Fact(Timeout = 120_000)]
    public async Task AReplyTheMeshRoutesInDisposeHostedHubs_ReachesItsLiveRecipient()
    {
        var ct = TestContext.Current.CancellationToken;
        var (waiter, arrived, routing, releaseTurn, turnEntered) = CreateParkableWaiter();
        try
        {
            await ProveTheRouteIsLive(routing, arrived, ct);
            await ParkTheWaiterAndTearDownTheMesh(waiter!, turnEntered);

            // Sent in this order on purpose: the waiter's queue is FIFO, so if the uncorrelated message
            // were delivered it would be processed BEFORE the answer — its absence is then conclusive.
            Deliver(routing, Response(UncorrelatedMarker, requestId: null));
            Deliver(routing, Response(AnsweredMarker, requestId: $"reply-teardown-{Guid.NewGuid():N}"));

            Volatile.Write(ref releaseTurn.Value, 1);
            await TheMeshIsDead(ct);

            arrived.Should().Contain(r => r.Error == AnsweredMarker,
                "the reply carried a correlation id and was routed while the mesh was in DisposeHostedHubs "
                + "and its recipient was alive and below that mark; the router dropped every delivery at that "
                + "run level — on the assumption that recipients are disposing too — which is #4023: the "
                + "caller's hub then waits out its quiesce budget for an answer that was already minted");
            arrived.Should().NotContain(r => r.Error == UncorrelatedMarker,
                "a delivery nobody is waiting for must still be refused during teardown; only answers are "
                + "carried, so the storm class the teardown guard exists for stays closed");
        }
        finally
        {
            Volatile.Write(ref releaseTurn.Value, 1);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task TrafficNobodyWaitsFor_IsStillRefusedWhileTheMeshTearsDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var (waiter, arrived, routing, releaseTurn, turnEntered) = CreateParkableWaiter();
        try
        {
            await ProveTheRouteIsLive(routing, arrived, ct);
            await ParkTheWaiterAndTearDownTheMesh(waiter!, turnEntered);

            // Nothing is owed: only uncorrelated traffic crosses the router during teardown.
            Deliver(routing, Response(UncorrelatedMarker, requestId: null));

            Volatile.Write(ref releaseTurn.Value, 1);
            await TheMeshIsDead(ct);

            arrived.Should().NotContain(r => r.Error == UncorrelatedMarker,
                "an ordinary teardown with nothing owed must carry nothing: the router refuses traffic nobody "
                + "is waiting for once the mesh is in DisposeHostedHubs, and a change that delivered it would "
                + "reopen the teardown storm class");
            arrived.Should().OnlyContain(r => r.Error == RouteProbe,
                "the only delivery this waiter may ever have seen is the pre-teardown route probe");
        }
        finally
        {
            Volatile.Write(ref releaseTurn.Value, 1);
        }
    }

    /// <summary>A volatile flag the test writes and the parked turn polls — boxed so it can be shared.</summary>
    private sealed class Flag
    {
        public int Value;
    }

    private (IMessageHub? Waiter, ConcurrentQueue<PatchDataResponse> Arrived, IRoutingService Routing,
        Flag ReleaseTurn, AsyncSubject<Unit> TurnEntered) CreateParkableWaiter()
    {
        // Resolved BEFORE any teardown — a disposal path never resolves from DI.
        var routing = Mesh.ServiceProvider.GetRequiredService<IRoutingService>();
        var arrived = new ConcurrentQueue<PatchDataResponse>();
        var releaseTurn = new Flag();
        var turnEntered = new AsyncSubject<Unit>();

        // 🚨 No hand-woven gate: the turn → test signal is an AsyncSubject the parked turn completes;
        // the release travels back INTO the deliberately parked turn, so it is a volatile flag polled
        // under a bounded SpinUntil and written in each test's `finally`.
        var waiter = Mesh.GetHostedHub(WaiterAddress, c => c
                .WithTypes(typeof(ParkTurn), typeof(PatchDataResponse))
                .WithHandler<ParkTurn>((_, d) =>
                {
                    turnEntered.OnNext(Unit.Default);
                    turnEntered.OnCompleted();
                    SpinWait.SpinUntil(() => Volatile.Read(ref releaseTurn.Value) == 1, 60.Seconds());
                    return d.Processed();
                })
                .WithHandler<PatchDataResponse>((_, d) =>
                {
                    arrived.Enqueue(d.Message);
                    return d.Processed();
                }),
            HostedHubCreation.Always);
        waiter.Should().NotBeNull();
        return (waiter, arrived, routing, releaseTurn, turnEntered);
    }

    /// <summary>
    /// POSITIVE ANCHOR, before anything is torn down: this router carries this message type to this
    /// recipient. Without it, "the reply did not arrive" would not distinguish a teardown refusal from
    /// an address that was never reachable.
    /// </summary>
    private async Task ProveTheRouteIsLive(
        IRoutingService routing, ConcurrentQueue<PatchDataResponse> arrived, CancellationToken ct)
    {
        Deliver(routing, Response(RouteProbe, requestId: "probe-nobody-armed"));
        await Observable.Interval(25.Milliseconds()).StartWith(0L)
            .Where(_ => arrived.Any(r => r.Error == RouteProbe))
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        Output.WriteLine("[probe] the mesh router delivers to the waiter while nothing is shutting down");
    }

    private async Task ParkTheWaiterAndTearDownTheMesh(IMessageHub waiter, AsyncSubject<Unit> turnEntered)
    {
        waiter.Post(new ParkTurn(), o => o.WithTarget(WaiterAddress));
        await turnEntered.Should().Within(20.Seconds()).Emit(
            "the parked turn must hold the waiter's action block before the mesh is torn down — that park "
            + "is what keeps the waiter's own ShutdownRequest queued behind it");

        Mesh.Dispose();
        Output.WriteLine("[dispose] mesh disposal invoked");

        // 🚨 The fence, and a control as much as a wait: it IS the precondition of the defect. Without
        // it the deliveries below would route through a live mesh and prove nothing about teardown.
        await Observable.Interval(25.Milliseconds()).StartWith(0L)
            .Where(_ => Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs
                        && waiter.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"[fence] mesh={Mesh.RunLevel} waiter={waiter.RunLevel} — the router is past the mark, the recipient is alive");
    }

    /// <summary>
    /// The causal judgment point: once the mesh is Dead every hosted hub has disposed, so every
    /// delivery that was going to reach the waiter has reached it. The bound is a liveness guard on
    /// that precondition, not a budget for the answer.
    /// </summary>
    private async Task TheMeshIsDead(CancellationToken ct)
    {
        var dead = await Observable.Interval(25.Milliseconds()).StartWith(0L)
            .Where(_ => Mesh.RunLevel == MessageHubRunLevel.Dead)
            .Select(_ => true)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence, Observable.Return(false))
            .Await(ct);
        dead.Should().BeTrue(
            $"the mesh must finish disposing once the waiter's turn is released; it is still at {Mesh.RunLevel}. "
            + "That is a wedged teardown — a different defect from the one asserted here");
    }

    /// <summary>Hands a delivery to the mesh router exactly as the mesh's routing handler does.</summary>
    private void Deliver(IRoutingService routing, IMessageDelivery delivery)
        => routing.DeliverMessage(delivery.Package(Mesh.JsonSerializerOptions))
            .Subscribe(_ => { }, ex => Output.WriteLine($"[route] delivery errored: {ex.Message}"));

    private IMessageDelivery Response(string marker, string? requestId)
    {
        var options = new PostOptions(ResponderAddress).WithTarget(WaiterAddress);
        if (requestId is not null)
            options = options.WithProperty(PostOptions.RequestId, requestId);
        return new MessageDelivery<PatchDataResponse>(
            new PatchDataResponse(true, 0L) { Error = marker }, options, Mesh.JsonSerializerOptions);
    }
}
