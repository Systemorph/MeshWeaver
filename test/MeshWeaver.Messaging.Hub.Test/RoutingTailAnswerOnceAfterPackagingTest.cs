using System.Collections.Concurrent;
using System.Reactive.Linq;
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
        var nackedDeliveryIds = new ConcurrentQueue<string>();
        var controlAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlId = string.Empty;

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
                nackedDeliveryIds.Enqueue(d.Message.Delivery.Id);
                if (d.Message.Delivery.Id == controlId)
                    controlAnswered.TrySetResult();
                return d.Processed();
            }));

        IMessageDelivery Submit<TMessage>(TMessage message)
        {
            var delivery = new MessageDelivery<TMessage>(SenderAddress, UnreachableTarget, message,
                router.JsonSerializerOptions);
            router.DeliverMessage(delivery);
            return delivery;
        }

        // Suppressed first, the positive control LAST: one action block, one queue, so the control's
        // NACK arriving proves the two before it were fully processed.
        var heartBeat = Submit(new HeartBeatEvent());
        var failure = Submit(new DeliveryFailure(
            new MessageDelivery<string>(SenderAddress, UnreachableTarget, "inner", router.JsonSerializerOptions),
            "inner failure"));
        var control = new MessageDelivery<string>(SenderAddress, UnreachableTarget, "ordinary-payload",
            router.JsonSerializerOptions);
        controlId = control.Id;
        router.DeliverMessage(control);

        await controlAnswered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var answered = nackedDeliveryIds.Should().ContainSingle(
            "the routing tail must apply the same answer-once contract as the router it reports for — "
            + "otherwise every suppressed NACK the router declines is simply re-posted here, and a pod "
            + "shutdown still produces the failure storm").Subject;
        answered.Should().Be(control.Id);
        heartBeat.Id.Should().NotBe(control.Id);
        failure.Id.Should().NotBe(control.Id);
    }
}
