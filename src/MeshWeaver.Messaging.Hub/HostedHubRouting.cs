using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

internal class HostedHubRouting
{
    private readonly IMessageHub parentHub;

    private readonly ILogger<HostedHubRouting> logger;
    private readonly RouteConfiguration configuration;
    private readonly IMessageHub hub;

    private HostedHubRouting(IMessageHub parentHub, IMessageHub hub)
    {
        this.parentHub = parentHub;
        this.hub = hub;
        this.logger = hub.ServiceProvider.GetRequiredService<ILogger<HostedHubRouting>>();
        this.configuration = hub
            .Configuration
            .GetListOfRouteLambdas()
            .Aggregate(new RouteConfiguration(hub), (c, f) => f.Invoke(c));
    }

    public static void Setup(IMessageHub parentHub, IMessageHub hub)
    {
        var instance = new HostedHubRouting(parentHub, hub);
        hub.Register(instance.RouteMessageAsync);
    }


    /// <summary>
    /// Loops through forward rules in a sequence. Each forward rule either applies and returns delivery.Forwarded() or doesn't apply and returns delivery.
    /// </summary>
    /// <param name="delivery"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IMessageDelivery> RouteMessageAsync(IMessageDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.State != MessageDeliveryState.Submitted)
            return delivery;

        if(delivery.Sender is JsonObject obj)
            delivery = delivery.WithSender(obj.Deserialize<object>(hub.JsonSerializerOptions));
        if(delivery.Target is JsonObject obj2)
            delivery = delivery.WithTarget(obj2.Deserialize<object>(hub.JsonSerializerOptions));
        if (delivery.Target is HostedAddress hosted && hosted.Host.Equals(parentHub.Address))
            delivery = delivery.WithTarget(hosted.Address);

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

        if (delivery.State != MessageDeliveryState.Submitted || delivery.Target == null || hub.Address.Equals(delivery.Target))
            return delivery.NotFound();

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

                return delivery.NotFound();
            }
        }

        if (parentHub == null)
            return delivery.NotFound();

        logger.LogDebug("Routing delivery {id} of type {type} to parent {target}", delivery.Id,
            delivery.Message.GetType().Name, parentHub.Address);
        delivery = delivery.WithSender(new HostedAddress(delivery.Sender, parentHub.Address));
        parentHub.DeliverMessage(delivery);
        return delivery.Forwarded();
    }
}

