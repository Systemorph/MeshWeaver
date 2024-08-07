using Microsoft.Extensions.Logging;
using MeshWeaver.ServiceProvider;

namespace MeshWeaver.Messaging;

public class RoutePlugin : MessageHubPlugin<RouteConfiguration>
{
    private readonly IMessageHub parentHub;

    [Inject] private ILogger<RoutePlugin> logger;

    public RoutePlugin(IMessageHub hub, RouteConfiguration routeConfiguration, IMessageHub parentHub) : base(hub)
    {
        this.parentHub = parentHub;
        InitializeState(routeConfiguration);

        Register(RouteMessageAsync);
    }

    public override bool Filter(IMessageDelivery d) => d.State == MessageDeliveryState.Submitted;

    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IMessageDelivery> RouteMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {

        // TODO V10: This should probably also react upon disconnect. (02.02.2024, Roland Bürgi)
        if (State.RoutedMessageAddresses.TryGetValue(delivery.Sender, out var originalSenders))
        {
            foreach (var originalSender in originalSenders)
            {
                logger.LogDebug("Routing message {id} of type {type} from address {sender} to address {target} to original address {originalSender}", delivery.Id, delivery.Message.GetType().Name, delivery.Sender,  delivery.Target, originalSender);
                var delivery1 = delivery;
                Hub.Post(delivery.Message, o => o.WithTarget(originalSender).WithProperties(delivery1.Properties));
            }

        }

        foreach (var handler in State.Handlers)
            delivery = await handler(delivery, cancellationToken);

        if (delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || Hub.Address.Equals(delivery.Target))
            return delivery;

        return RouteAlongHostingHierarchy(delivery, delivery.Target, null);
    }
    private IMessageDelivery RouteAlongHostingHierarchy(IMessageDelivery delivery, object address, object hostedSegment)
    {

        if (Hub.Address.Equals(address))
            // TODO V10: This works only if the hub has been instantiated before. Consider re-implementing setting hosted hub configs at config time. (31.01.2024, Roland Bürgi)
            return Hub.GetHostedHub(hostedSegment, null).DeliverMessage(delivery);

        if (address is IHostedAddress hosted)
        {
            logger.LogDebug("Routing delivery {id} of type {type} to host with address {target}", delivery.Id, delivery.Message.GetType().Name, hosted.Host);
            return RouteAlongHostingHierarchy(delivery, hosted.Host, address);
        }


        if (parentHub == null)
            return delivery.NotFound();

        logger.LogDebug("Routing delivery {id} of type {type} to parent {target}", delivery.Id, delivery.Message.GetType().Name, parentHub.Address);
        parentHub.DeliverMessage(delivery);
        return delivery.Forwarded();
    }

}