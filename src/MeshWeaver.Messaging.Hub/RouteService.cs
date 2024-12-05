using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

internal class RouteService 
{
    private readonly IMessageHub parentHub;

    private readonly ILogger<RouteService> logger;
    private readonly RouteConfiguration configuration;
    private readonly IMessageHub hub;

    public RouteService(IMessageHub parentHub, IMessageHub hub)
    {
        this.parentHub = parentHub;
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<RouteService>>();
        this.configuration = hub
            .Configuration
            .GetListOfRouteLambdas()
            .Aggregate(new RouteConfiguration(hub), (c, f) => f.Invoke(c));
        hub.Register(RouteMessageAsync);
    }


    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IMessageDelivery> RouteMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        if(delivery.State != MessageDeliveryState.Submitted)
            return delivery;
        // TODO V10: This should probably also react upon disconnect. (02.02.2024, Roland Bürgi)
        if (configuration.RoutedMessageAddresses.TryGetValue(delivery.Sender, out var originalSenders))
        {
            foreach (var originalSender in originalSenders)
            {
                logger.LogDebug("Routing message {id} of type {type} from address {sender} to address {target} to original address {originalSender}", delivery.Id, delivery.Message.GetType().Name, delivery.Sender,  delivery.Target, originalSender);
                var delivery1 = delivery;
                hub.Post(delivery.Message, o => o.WithTarget(originalSender).WithProperties(delivery1.Properties));
            }

        }

        foreach (var handler in configuration.Handlers)
            delivery = await handler(delivery, cancellationToken);

        if (delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || hub.Address.Equals(delivery.Target))
            return delivery;

        return RouteAlongHostingHierarchy(delivery, delivery.Target, null);
    }
    private IMessageDelivery RouteAlongHostingHierarchy(IMessageDelivery delivery, object address, object hostedSegment)
    {
        if (address is JsonObject obj)
            address = obj.Deserialize<object>(hub.JsonSerializerOptions);

        if (hub.Address.Equals(address))
            // TODO V10: This works only if the hub has been instantiated before. Consider re-implementing setting hosted hub configs at config time. (31.01.2024, Roland Bürgi)
            return hub.GetHostedHub(hostedSegment, null)?.DeliverMessage(delivery) ?? delivery.NotFound();

        if (address is HostedAddress hosted)
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

