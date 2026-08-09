using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro for the DROPPED ROUTING FAILURE: a delivery that ROUTING failed while it was
/// NOT on this hub's target used to be returned unreported, so no <see cref="DeliveryFailure"/> was
/// ever posted and the requester's <c>hub.Observe(...)</c> waited indefinitely.
///
/// <para><b>Where the hole was.</b> <c>MessageService.NotifyAsync</c> inspected
/// <see cref="MessageDeliveryState.Failed"/> only on the pre-routing, ON-TARGET path. After
/// <c>hierarchicalRouting.RouteMessageAsync</c> returned, the not-on-target branch fell through to a
/// bare <c>Observable.Return(delivery)</c> with no state check at all — and routing has five sites
/// that fail a delivery there without posting a NACK of their own (the two "no route while
/// disposing" arms and the parent-disposing check in <c>HierarchicalRouting</c>, plus both RunLevel
/// checks in <c>RouteConfiguration</c>). Every one of them stranded its caller.</para>
///
/// <para><b>Why it matters beyond a test.</b> This is the teardown shape captured in #981: a hosted
/// hub that quiesces AFTER its parent has reached <c>DisposeHostedHubs</c> still posts to that
/// parent, routing fails the delivery with "parent hub disposing", and the reply can never come.
/// The pending callback then outlives the quiescing budget — which is how the defect was ever
/// noticed, not what it is. It violates the framework's flat invariant: never a silent hang, every
/// error reaches a graceful sink.</para>
///
/// <para><b>What is driven here.</b> A route handler on the client hub — the same public seam
/// (<c>RouteConfiguration.WithHandler</c>) the SignalR, gRPC, Data and mesh routers register on —
/// fails a not-on-target delivery exactly the way those five framework sites do. No disposal is
/// involved, so there is nothing to time and no race to lose.</para>
/// </summary>
public class RoutingFailureReportedTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>Routing fails this one as a DISPOSAL RACE — the sender is owed a transient NACK.</summary>
    private record StrandedRequest : IRequest<Ack>;

    /// <summary>Routing fails this one as a ROUTING LOOP — a different, terminal verdict.</summary>
    private record LoopedRequest : IRequest<Ack>;

    /// <summary>Routing fails this one AFTER answering the sender itself — must not be NACKed twice.</summary>
    private record AnsweredRequest : IRequest<Ack>;

    /// <summary>Handled by the client itself; used as a FIFO barrier on the client's turn loop.</summary>
    private record BarrierRequest : IRequest<Ack>;

    private record Ack;

    private static readonly Address ServerAddress = new("host", "1");

    private int deliveryFailuresSeen;

    /// <summary>
    /// The host WOULD answer every one of these requests. So a passing NACK assertion can only come
    /// from the routing failure — never from the request quietly getting through.
    /// </summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(StrandedRequest), typeof(LoopedRequest), typeof(AnsweredRequest), typeof(Ack))
            .WithHandler<StrandedRequest>(AnswerWithAck)
            .WithHandler<LoopedRequest>(AnswerWithAck)
            .WithHandler<AnsweredRequest>(AnswerWithAck);

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithTypes(typeof(StrandedRequest), typeof(LoopedRequest), typeof(AnsweredRequest),
                typeof(BarrierRequest), typeof(Ack))
            .WithHandler<BarrierRequest>(AnswerWithAck)
            .WithRoutes(routes => routes.WithHandler(FailInRouting));

    private static IMessageDelivery AnswerWithAck(IMessageHub hub, IMessageDelivery delivery)
    {
        hub.Post(new Ack(), o => o.ResponseFor(delivery));
        return delivery.Processed();
    }

    /// <summary>
    /// Stands in for the framework's own routing-stage failures. Runs inside
    /// <c>HierarchicalRouting</c>'s handler fold, so what it returns is exactly what those five
    /// sites return — and it also SEES every inbound delivery (route handlers run before any
    /// message handler), which is how the NACKs are counted without perturbing the response
    /// machinery.
    /// </summary>
    private IObservable<IMessageDelivery> FailInRouting(IMessageDelivery delivery)
    {
        if (delivery.Message is DeliveryFailure)
            Interlocked.Increment(ref deliveryFailuresSeen);

        return Observable.Return(delivery.Message switch
        {
            // Classified + unanswered: MessageService owes the sender the NACK.
            StrandedRequest => delivery.Failed("routing gave up: the target's host is disposing",
                ErrorType.ShuttingDown),
            LoopedRequest => delivery.Failed("routing gave up: no hub found for target",
                ErrorType.RoutingLoop),
            // Already answered: the site posted its own NACK, so MessageService must stay quiet.
            AnsweredRequest => AnswerAndFail(delivery),
            _ => delivery
        });
    }

    private IMessageDelivery AnswerAndFail(IMessageDelivery delivery)
    {
        Hub.Post(
            new DeliveryFailure(delivery) { ErrorType = ErrorType.NotFound, Message = "no node at that address" },
            o => o.ResponseFor(delivery));
        return delivery.FailedAndNacked("no node at that address");
    }

    private IMessageHub Hub => hub ??= GetClient();
    private IMessageHub? hub;

    /// <summary>
    /// The core pin. WITHOUT the fix this task never completes — the delivery is failed, dropped,
    /// and nothing ever resolves the callback — so the test dies on its timeout with no explanation,
    /// which is precisely the production symptom.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task RoutingFailure_OffTarget_ReachesTheSender()
    {
        var failure = await Nack(new StrandedRequest());

        failure.Failure!.ErrorType.Should().Be(ErrorType.ShuttingDown,
            "a disposal race must reach the sender as TRANSIENT — the address may reactivate, and "
            + "consumers with their own recovery machinery (SynchronizationStream's resubscribe "
            + "latch) ride ShuttingDown out instead of tearing down");
        failure.Failure.Message.Should().Contain("routing gave up",
            "the NACK must carry the failing site's own reason — a generic message sends the next "
            + "investigator hunting the wrong layer");
    }

    /// <summary>
    /// The verdict is the one the FAILING SITE decided on, carried on the delivery — not a constant
    /// baked into the reporting code, and not re-derived downstream by pattern-matching the failure
    /// text (which drifts the moment someone rewords a string).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task TheFailingSitesVerdict_IsTheOneReported()
    {
        var failure = await Nack(new LoopedRequest());

        failure.Failure!.ErrorType.Should().Be(ErrorType.RoutingLoop,
            "the classification travels with the delivery from the site that knows the condition");
    }

    /// <summary>
    /// The anti-storm half. The routing services answer their own "no node at this address" before
    /// failing the delivery, and that path is HOT — every message to a missing or undeployed node
    /// takes it. Reporting it again would double every NotFound in the mesh, i.e. trade a hang for
    /// a traffic multiplier on a known storm-prone path.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task AnAlreadyAnsweredRoutingFailure_IsNotNackedTwice()
    {
        var failure = await Nack(new AnsweredRequest());
        failure.Failure!.ErrorType.Should().Be(ErrorType.NotFound);

        // FIFO barrier, not a sleep. Both NACKs would be posted from the SAME routing turn, so a
        // second one is already queued behind the first by the time its OnError fires. A round-trip
        // the client answers ITSELF therefore cannot complete until that queued turn has drained.
        await Hub.Observe<Ack>(new BarrierRequest(), o => o.WithTarget(Hub.Address))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        Volatile.Read(ref deliveryFailuresSeen).Should().Be(1,
            "the failing site already answered the sender; a second NACK on this path multiplies "
            + "every NotFound in the mesh");
    }

    private async Task<DeliveryFailureException> Nack(IRequest<Ack> request)
    {
        var response = Hub
            .Observe<Ack>(request, o => o.WithTarget(ServerAddress))
            .FirstAsync()
            .ToTask(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<DeliveryFailureException>(() => response);
        Output.WriteLine($"{request.GetType().Name}: errorType={failure.Failure?.ErrorType} message={failure.Failure?.Message}");
        failure.Failure.Should().NotBeNull();
        return failure;
    }
}
