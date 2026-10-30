using System;
using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A request the mesh ACCEPTED while live and finishes routing after its own teardown began is
/// ANSWERED — never dropped while its sender waits.</b>
///
/// <para><b>The defect.</b> The mesh's router hands a delivery for a not-yet-activated address to a
/// per-address activation queue and routes it asynchronously (path resolution, hub creation). When
/// that route reached its end after the mesh had passed <see cref="MessageHubRunLevel.DisposeHostedHubs"/>,
/// every branch declined to answer: the queue's own teardown guard carried only correlated REPLIES
/// (#4023) and dropped the rest, and the NotFound / route-failure NACKs were each guarded by
/// <c>Mesh.RunLevel &lt; DisposeHostedHubs</c> — "the recipients are gone". A request a LIVE hub sent
/// through <c>hub.Observe</c> is not that: its sender holds a callback for it and sits out its whole
/// quiesce budget. Measured on a Plugins PR (portal-hosts leg 4/4, <c>HeldNodeTypeBundleRetryTest</c>):
/// a startup write's <c>SubscribeRequest@Plugins/_Policy</c> was ENQUEUED on the mesh while it was
/// <c>Started</c>, ROUTED 26 ms later — after <c>Mesh.Dispose()</c> — and then neither handled nor
/// refused; the teardown check failed a passing test on the orphaned callback.</para>
///
/// <para><b>Deterministic, not raced.</b> Every edge is a fence:
/// <list type="number">
///   <item>The activation queue for the target is HELD: its head is a delivery to a recipient
///     registered with <see cref="IRoutingService.RegisterStream"/> whose answer the test withholds,
///     so everything behind it waits in arrival order (#1145).</item>
///   <item>The sender's real <c>Observe</c> posts the request while the mesh is <c>Started</c>; a
///     marker the sender posts NEXT, to a live hub, arrives only after the mesh has handed the request
///     to that queue (one mesh queue, FIFO).</item>
///   <item>The sender is parked, so its ShutdownRequest queues behind the park and it stays below
///     <c>DisposeHostedHubs</c>; the mesh is disposed and waited past the mark.</item>
///   <item>Only then is the queue's head answered, so the request's route runs in teardown.</item>
/// </list></para>
///
/// <para><b>Control.</b> <see cref="ARequestInALiveMesh_GetsItsOrdinaryAnswer"/> pins that a request
/// over the same router still reaches its handler and gets the handler's answer when nothing is
/// shutting down. <see cref="ReplyRoutedDuringMeshTeardownReachesItsWaiterTest"/> keeps pinning that
/// traffic nobody waits for is still refused, unanswered, during teardown — the storm guard.</para>
/// </summary>
public class RequestRoutedDuringMeshTeardownIsAnsweredTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private record ParkTurn;

    /// <summary>A fire-and-forget marker that proves the mesh has routed everything posted before it.</summary>
    public record Marker;

    /// <summary>A request whose handler answers <see cref="Pong"/> — the route's live behaviour.</summary>
    public record Ping : IRequest<Pong>;

    /// <summary>The handler's answer.</summary>
    public record Pong;

    private static readonly Address SenderAddress = new("request-teardown-sender", "1");
    private static readonly Address ResponderAddress = new("request-teardown-responder", "1");

    private sealed class Flag
    {
        public int Value;
    }

    [Fact(Timeout = 120_000)]
    public async Task ARequestWhoseRouteEndsInDisposeHostedHubs_IsAnsweredShuttingDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var routing = Mesh.ServiceProvider.GetRequiredService<IRoutingService>();
        var releaseTurn = new Flag();
        var turnEntered = new AsyncSubject<Unit>();
        var sender = CreateSender(releaseTurn, turnEntered);
        var markerArrived = new AsyncSubject<Unit>();
        CreateResponder(markerArrived);

        // A REAL node with no hub of its own, so a delivery to it takes the activation queue.
        var targetPath = $"{TestPartition}/request-teardown-target-{Guid.NewGuid():N}";
        var target = new Address(targetPath);
        await NodeFactory.CreateNode(MeshNode.FromPath(targetPath) with
            {
                Name = "held activation",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit();
        Mesh.GetHostedHub(target, HostedHubCreation.Never).Should().BeNull(
            "the target must have no live hub, or the request would bypass the activation queue");

        // The queue's head: the router hands it to this registration and waits for its answer,
        // which the test withholds until the mesh is past the mark.
        var headEntered = new AsyncSubject<Unit>();
        var releaseHead = new AsyncSubject<Unit>();
        var pingsAtTarget = 0;
        using var registration = routing.RegisterStream(target, (AsyncDelivery)((d, _) =>
        {
            if (d.Message is RawJson raw && raw.Content.Contains(nameof(Ping), StringComparison.Ordinal))
                Interlocked.Increment(ref pingsAtTarget);
            headEntered.OnNext(Unit.Default);
            headEntered.OnCompleted();
            return releaseHead.Select(_ => d.Processed());
        }));

        var outcome = new ConcurrentQueue<string>();
        try
        {
            routing.DeliverMessage(new MessageDelivery<Marker>(
                        new Marker(), new PostOptions(Mesh.Address).WithTarget(target), Mesh.JsonSerializerOptions)
                    .Package(Mesh.JsonSerializerOptions))
                .Subscribe(_ => { }, ex => outcome.Enqueue($"head-route-error:{ex.Message}"));
            await headEntered.Should().Within(TestTimeouts.Convergence).Emit(
                "the activation queue's head must be held before the request is queued behind it",
                cancellationToken: ct);

            sender.Observe(new Ping(), o => o.WithTarget(target))!
                .Subscribe(
                    d => outcome.Enqueue($"answer:{d.Message.GetType().Name}"),
                    ex => outcome.Enqueue(ex is DeliveryFailureException dfe
                        ? $"nack:{dfe.Failure?.ErrorType}:{dfe.Message}"
                        : $"error:{ex.GetType().Name}:{ex.Message}"));
            sender.Post(new Marker(), o => o.WithTarget(ResponderAddress));
            await markerArrived.Should().Within(TestTimeouts.Convergence).Emit(
                "the marker leaves the sender and crosses the mesh AFTER the request, so its arrival proves the "
                + "mesh has handed the request to the held activation queue while it was still live",
                cancellationToken: ct);
            outcome.Should().BeEmpty("the request is queued behind the held head and has not been answered yet");

            sender.Post(new ParkTurn(), o => o.WithTarget(SenderAddress));
            await turnEntered.Should().Within(20.Seconds()).Emit(
                "the parked turn must hold the sender's action block before the mesh is torn down — it keeps "
                + "the sender alive and waiting while the mesh is past the mark",
                cancellationToken: ct);

            Mesh.Dispose();
            await Observable.Interval(25.Milliseconds()).StartWith(0L)
                .Where(_ => Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs
                            && sender.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
                .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
            Output.WriteLine($"[fence] mesh={Mesh.RunLevel} sender={sender.RunLevel}");

            // The request's route now runs with the mesh in DisposeHostedHubs and its sender alive.
            releaseHead.OnNext(Unit.Default);
            releaseHead.OnCompleted();
            Volatile.Write(ref releaseTurn.Value, 1);
            await TheMeshIsDead(ct);

            foreach (var o in outcome)
                Output.WriteLine($"[outcome] {o}");
            Volatile.Read(ref pingsAtTarget).Should().Be(0,
                "nothing may be delivered or activated for a new request once the mesh is past the mark");
            outcome.Should().ContainSingle(
                "the request's sender was alive and waiting on it; whatever the mesh does with a request it "
                + "finishes routing during its own teardown, the sender must get exactly one terminal answer");
            outcome.Should().ContainSingle(o => o.StartsWith($"nack:{ErrorType.ShuttingDown}:"),
                "the router refused the request because the MESH is shutting down — a transient, typed "
                + "ShuttingDown NACK delivered to the live sender. A drop leaves the callback to be cancelled "
                + "at the end of the sender's quiesce budget (HubDisposedBeforeResponseException) — the "
                + "orphaned SubscribeRequest the Plugins teardown check reported");
        }
        finally
        {
            releaseHead.OnCompleted();
            Volatile.Write(ref releaseTurn.Value, 1);
        }
    }

    /// <summary>
    /// 🚨 A request whose route ALREADY REACHED its recipient when the activation queue is torn down is
    /// NOT refused by that teardown. The route handed it over (the side effect happened), so a
    /// "cannot route" refusal would be false, and a second terminal answer if the recipient answers
    /// too. The queue's head is an awaited request from a stream-registered sender (the kind of sender
    /// that is still reachable when the queue is disposed, in the mesh's ShutDown phase). Its route
    /// delivered it to the target's registration, which holds the route's verdict open. Then the mesh
    /// is disposed.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARequestAlreadyHandedToItsRecipient_IsNotRefusedByTheQueueTeardown()
    {
        var ct = TestContext.Current.CancellationToken;
        var routing = Mesh.ServiceProvider.GetRequiredService<IRoutingService>();

        var targetPath = $"{TestPartition}/request-teardown-inflight-{Guid.NewGuid():N}";
        var target = new Address(targetPath);
        await NodeFactory.CreateNode(MeshNode.FromPath(targetPath) with
            {
                Name = "in-flight activation",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(TestTimeouts.Convergence).Emit();
        Mesh.GetHostedHub(target, HostedHubCreation.Never).Should().BeNull(
            "the target must have no live hub, or the request would bypass the activation queue");

        var senderAddress = new Address("request-teardown-stream-sender", Guid.NewGuid().ToString("N"));
        var atSender = new ConcurrentQueue<string>();
        using var senderRegistration = routing.RegisterStream(senderAddress, (SyncDelivery)(d =>
        {
            atSender.Enqueue(d.Message is DeliveryFailure f
                ? $"nack:{f.ErrorType}:{f.Message}"
                : $"{d.Message.GetType().Name}");
            return d.Processed();
        }));

        var headEntered = new AsyncSubject<Unit>();
        var releaseHead = new AsyncSubject<Unit>();
        var pingsAtTarget = 0;
        using var targetRegistration = routing.RegisterStream(target, (AsyncDelivery)((d, _) =>
        {
            if (d.Message is RawJson raw && raw.Content.Contains(nameof(Ping), StringComparison.Ordinal))
                Interlocked.Increment(ref pingsAtTarget);
            headEntered.OnNext(Unit.Default);
            headEntered.OnCompleted();
            return releaseHead.Select(_ => d.Processed());
        }));

        try
        {
            routing.DeliverMessage(new MessageDelivery<Ping>(
                        new Ping(),
                        new PostOptions(senderAddress).WithTarget(target).AcceptingProgress(),
                        Mesh.JsonSerializerOptions)
                    .Package(Mesh.JsonSerializerOptions))
                .Subscribe(_ => { }, ex => atSender.Enqueue($"route-error:{ex.Message}"));
            await headEntered.Should().Within(TestTimeouts.Convergence).Emit(
                "the request's route must have handed it to the target's registration before the teardown",
                cancellationToken: ct);
            Volatile.Read(ref pingsAtTarget).Should().Be(1, "the request reached its recipient");

            Mesh.Dispose();
            await TheMeshIsDead(ct);

            foreach (var o in atSender)
                Output.WriteLine($"[at-sender] {o}");
            atSender.Should().NotContain(o => o.StartsWith($"nack:{ErrorType.ShuttingDown}:"),
                "the request was already handed to its recipient; the queue's teardown cut only the wait for the "
                + "route's verdict, so refusing it as 'cannot route' would be false, and a second terminal answer "
                + "whenever the recipient answers too");
        }
        finally
        {
            releaseHead.OnCompleted();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ARequestInALiveMesh_GetsItsOrdinaryAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = CreateSender(new Flag { Value = 1 }, new AsyncSubject<Unit>());
        CreateResponder(new AsyncSubject<Unit>());

        var answer = await sender.Observe(new Ping(), o => o.WithTarget(ResponderAddress))!
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        answer.Message.Should().BeOfType<Pong>(
            "with nothing shutting down the request is routed to its handler and answered by it — the "
            + "teardown refusal must not touch the live path");
    }

    private IMessageHub CreateSender(Flag releaseTurn, AsyncSubject<Unit> turnEntered)
    {
        // 🚨 No hand-woven gate: the turn → test signal is an AsyncSubject the parked turn completes;
        // the release INTO the parked turn is a volatile flag polled under a bounded SpinUntil and
        // written in the test's `finally`.
        var sender = Mesh.GetHostedHub(SenderAddress, c => c
                .WithTypes(typeof(ParkTurn), typeof(Marker), typeof(Ping), typeof(Pong))
                .WithHandler<ParkTurn>((_, d) =>
                {
                    turnEntered.OnNext(Unit.Default);
                    turnEntered.OnCompleted();
                    SpinWait.SpinUntil(() => Volatile.Read(ref releaseTurn.Value) == 1, 60.Seconds());
                    return d.Processed();
                }),
            HostedHubCreation.Always);
        sender.Should().NotBeNull();
        return sender!;
    }

    private void CreateResponder(AsyncSubject<Unit> markerArrived)
    {
        var responder = Mesh.GetHostedHub(ResponderAddress, c => c
                .WithTypes(typeof(Marker), typeof(Ping), typeof(Pong))
                .WithHandler<Ping>((h, d) =>
                {
                    h.Post(new Pong(), o => o.ResponseFor(d));
                    return d.Processed();
                })
                .WithHandler<Marker>((_, d) =>
                {
                    markerArrived.OnNext(Unit.Default);
                    markerArrived.OnCompleted();
                    return d.Processed();
                }),
            HostedHubCreation.Always);
        responder.Should().NotBeNull();
    }

    /// <summary>
    /// The causal judgment point: once the mesh is Dead every hosted hub has disposed, so the sender's
    /// callback has either been answered or cancelled. The bound is a liveness guard on that
    /// precondition, not a budget for the answer.
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
            $"the mesh must finish disposing once the sender's turn is released; it is still at {Mesh.RunLevel}");
    }
}
