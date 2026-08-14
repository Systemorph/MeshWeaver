using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The ROUTING TAIL's half of the answer-once contract across the packaging boundary (issue #1485).
///
/// <para><b>Why fixing the routers alone is not enough.</b> A hub's route handler is
/// <c>IRoutingService.DeliverMessage(delivery.Package(hub.JsonSerializerOptions))</c>
/// (<c>MeshBuilder</c>), and whatever that handler returns comes back to <c>MessageService</c>'s
/// not-on-target tail, which reports any <c>Failed</c> delivery the sender was not already told
/// about. So the delivery <c>ReportFailure</c> inspects there is the PACKAGED one — payload
/// <see cref="RawJson"/> — and its <c>[CanBeIgnored]</c> / <c>DeliveryFailure</c> test could not
/// match either. Suppressing the NACK in the routers while leaving this site would simply move the
/// same storm one level up.</para>
///
/// <para>The one path where this is reached in production is the router branch that returns
/// <c>Failed</c> SYNCHRONOUSLY — <c>OrleansRoutingService</c>'s shutdown short-circuit, i.e. every
/// pod shutdown. The route handler below returns exactly that shape (packaged, then
/// <c>Failed(…, ShuttingDown)</c>) so the seam is exercised without a cluster.</para>
/// </summary>
public class RoutingTailAnswerOnceAfterPackagingTest(ITestOutputHelper output) : TestBase(output)
{
    private static readonly Address RouterAddress = new("mesh", "routing-tail");
    private static readonly Address SenderAddress = new("client", "routing-tail-sender");
    private static readonly Address UnreachableTarget = new("unreachable", "node");

    [Fact(Timeout = 60_000)]
    public async Task PackagedFireAndForgetAndNacks_AreNotAnswered_ByTheRoutingTail()
    {
        // ReplaySubject: the assertion subscribes after the deliveries are submitted and must still
        // see everything the tail answered.
        var nacks = new ReplaySubject<string>();
        var answeredSoFar = nacks.Scan(ImmutableList<string>.Empty, (answered, id) => answered.Add(id));

        var router = ServiceProvider.CreateMessageHub(RouterAddress, conf => conf
            .WithPostingIdentity(PostingIdentity.System)
            .WithRoutes(routes => routes.WithHandler(delivery =>
            {
                var targetWithoutHost = delivery.Target is not null ? delivery.Target with { Host = null } : null;
                if (delivery.State != MessageDeliveryState.Submitted
                    || targetWithoutHost is null
                    || targetWithoutHost.Equals(routes.Hub.Address)
                    // Anything not aimed at the unreachable address (notably the NACK we post back
                    // to the sender) falls through to ordinary hosted-hub routing.
                    || targetWithoutHost.Type != UnreachableTarget.Type)
                    return Observable.Return(delivery);

                // 🚨 EXACTLY what OrleansRoutingService's shutdown branch hands back: the delivery
                // packaged the way MeshBuilder packages it, then Failed. The payload type the tail's
                // guard used to test is gone by construction.
                return Observable.Return(
                    delivery.Package(routes.Hub.JsonSerializerOptions)
                        .Failed($"Host is shutting down, cannot route to {targetWithoutHost}",
                            ErrorType.ShuttingDown));
            })));

        // The sender lives under the router so the DeliveryFailure the tail posts (ResponseFor →
        // target = delivery.Sender) reaches a real handler rather than vanishing.
        router.GetHostedHub(SenderAddress, c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<DeliveryFailure>((_, d) =>
            {
                nacks.OnNext(d.Message.Delivery.Id);
                return d.Processed();
            }));

        IMessageDelivery Submit<TMessage>(TMessage message)
        {
            var delivery = new MessageDelivery<TMessage>(SenderAddress, UnreachableTarget, message,
                router.JsonSerializerOptions);
            router.DeliverMessage(delivery);
            return delivery;
        }

        // Suppressed first, the positive control LAST: one action block, one queue, so the fold at
        // the moment the control is answered already contains anything the two before it produced.
        var heartBeat = Submit(new HeartBeatEvent());
        var failure = Submit(new DeliveryFailure(
            new MessageDelivery<string>(SenderAddress, UnreachableTarget, "inner", router.JsonSerializerOptions),
            "inner failure"));
        var control = Submit("ordinary-payload");

        var answered = await answeredSoFar.Should().Within(30.Seconds())
            .Match(a => a.Contains(control.Id),
                "an ordinary request that fails routing must still be reported to its sender");

        answered.Should().ContainSingle(
            "the routing tail must apply the same answer-once contract as the router it reports for — "
            + "otherwise every suppressed NACK the router declines is simply re-posted here, and a pod "
            + "shutdown still produces the failure storm").Subject
            .Should().Be(control.Id);
        answered.Should().NotContain(heartBeat.Id);
        answered.Should().NotContain(failure.Id);
    }

    /// <summary>
    /// 🚨 The stamp has to survive the JSON wire, because a participant proxy re-serializes the whole
    /// envelope (<c>SignalRConnectionHub.DeliverMessage</c> / the gRPC registry) and the routers on
    /// the far side apply the same contract to what comes back. This is also WHY
    /// <see cref="AnswerPolicy.MayAnswer"/> checks the key's PRESENCE and never its value: an
    /// <c>object</c>-valued delivery property is stamped as a <see cref="bool"/> and arrives as a
    /// <c>JsonElement</c>, so a value comparison would quietly stop matching after exactly one hop —
    /// the round-trip <c>MessageStormBreaker.ResolvePayloadKey</c> documents for the sibling
    /// <c>DiagnosticKey</c> stamp.
    /// </summary>
    [Fact]
    public void TheSuppressionStamp_SurvivesTheJsonWire()
    {
        var hub = ServiceProvider.CreateMessageHub(RouterAddress, conf => conf
            .WithPostingIdentity(PostingIdentity.System));

        var packaged = new MessageDelivery<HeartBeatEvent>(SenderAddress, UnreachableTarget,
                new HeartBeatEvent(), hub.JsonSerializerOptions)
            .Package(hub.JsonSerializerOptions);
        packaged.MayAnswer().Should().BeFalse("the payload was [CanBeIgnored] before it was erased");

        // Exactly the participant-proxy hop: serialize the whole envelope, deserialize it back.
        var json = JsonSerializer.Serialize(packaged, hub.JsonSerializerOptions);
        var roundTripped = JsonSerializer.Deserialize<IMessageDelivery>(json, hub.JsonSerializerOptions);

        roundTripped.Should().NotBeNull();
        roundTripped!.MayAnswer().Should().BeFalse(
            "the far side's routers apply the same answer-once contract, and by then BOTH the payload "
            + "type and the value's CLR type are gone — only the property key remains");
    }
}
