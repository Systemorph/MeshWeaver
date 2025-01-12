using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

internal class HierarchicalRouting
{
    private readonly IMessageHub parentHub;

    private readonly ILogger<HierarchicalRouting> logger;
    private readonly RouteConfiguration configuration;
    private readonly IMessageHub hub;

    internal HierarchicalRouting(IMessageHub hub, IMessageHub parentHub)
    {
        this.parentHub = parentHub;
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<HierarchicalRouting>>();
        this.configuration = hub
            .Configuration
            .GetListOfRouteLambdas()
            .Aggregate(new RouteConfiguration(hub), (c, f) => f.Invoke(c));
    }



    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IMessageDelivery> RouteMessageAsync(IMessageDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.State != MessageDeliveryState.Submitted)
            return delivery;


        // TODO V10: This should probably also react upon disconnect. (02.02.2024, Roland Bürgi)
        if (configuration.RoutedMessageAddresses.TryGetValue(delivery.Sender, out var originalSenders))
        {
            foreach (var originalSender in originalSenders)
            {
                logger.LogDebug(
                    "Routing message {id} of type {type} from address {sender} to address {target} to original address {originalSender}",
                    delivery.Id, delivery.Message.GetType().Name, delivery.Sender, delivery.Target, originalSender);
                var delivery1 = delivery;
                hub.Post(delivery.Message, o => o.WithTarget(originalSender).WithProperties(delivery1.Properties));
            }

        }

        foreach (var handler in configuration.Handlers)
            delivery = await handler(delivery, cancellationToken);

        if (delivery.State != MessageDeliveryState.Submitted)
            return delivery;


        return RouteAlongHostingHierarchy(delivery);
    }

    private IMessageDelivery RouteAlongHostingHierarchy(IMessageDelivery delivery)
    {

        if (delivery.Target is HostedAddress hosted)
        {
            logger.LogDebug("Routing delivery {id} of type {type} to host with address {target}", delivery.Id,
                delivery.Message.GetType().Name, hosted.Host);
            if (hub.Address.Equals(hosted.Host))
            {
                var nextLevelAddress = hosted.Address;
                if(nextLevelAddress is HostedAddress hostedInner)
                    nextLevelAddress = hostedInner.Host;
                var hostedHub = hub.GetHostedHub(nextLevelAddress);
                if (hostedHub is not null)
                {
                    hostedHub.DeliverMessage(delivery.WithTarget(hosted.Address));
                    return delivery.Forwarded();
                }
                logger.LogDebug("No route found for {Address}. Last tried in {Hub}", hosted.Address, hub.Address);
                hub.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = ErrorType.NotFound,
                        Message = $"No route found for host {hosted.Address}. Last tried in {hub.Address}"
                    }, o => o.ResponseFor(delivery)
                );
                return delivery.NotFound();
            }
        }
        else
        {
            var hostedHub = hub.GetHostedHub(delivery.Target, HostedHubCreation.Never);
            if (hostedHub is not null)
            {
                hostedHub.DeliverMessage(delivery);
                return delivery.Forwarded();
            }
        }

        if (parentHub == null)
        {
            logger.LogDebug("No route found for {Address}. Last tried in {Hub}", delivery.Target, hub.Address);
            hub.Post(
                new DeliveryFailure(delivery)
                {
                    ErrorType = ErrorType.NotFound,
                    Message = $"No route found for host {delivery.Target}. Last tried in {hub.Address}"
                }, o => o.ResponseFor(delivery)
            );
            return delivery.NotFound();

        }

        logger.LogDebug("Routing delivery {id} of type {type} to parent {target}", delivery.Id,
            delivery.Message.GetType().Name, parentHub.Address);
        parentHub.DeliverMessage(delivery.WithSender(new HostedAddress(delivery.Sender, parentHub.Address)));
        return delivery.Forwarded();
    }
}

