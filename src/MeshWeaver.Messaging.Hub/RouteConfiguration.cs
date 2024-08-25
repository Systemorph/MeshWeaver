using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Collections;

namespace MeshWeaver.Messaging;


public record RouteConfiguration(IMessageHub Hub)
{
    private readonly ILogger logger = Hub.ServiceProvider.GetRequiredService<ILogger<RouteConfiguration>>();

    internal ImmutableList<AsyncDelivery> Handlers { get; init; } = ImmutableList<AsyncDelivery>.Empty;

    internal readonly Dictionary<object, HashSet<object>> RoutedMessageAddresses = new();

    public RouteConfiguration WithHandler(AsyncDelivery handler) => this with { Handlers = Handlers.Add(handler) };

    public RouteConfiguration RouteAddressToHub<TAddress>(Func<TAddress, IMessageHub> hubFactory) =>
        RouteAddress<TAddress>((routedAddress, d) =>
        {
            var hub = hubFactory(routedAddress);
            hub.DeliverMessage(d);
            return d.Forwarded();
        });

    public RouteConfiguration RouteAddress<TAddress>(SyncRouteDelivery<TAddress> handler) =>
        RouteAddress<TAddress>((routedAddress, d, _) => Task.FromResult(handler(routedAddress, d)));
        
    public RouteConfiguration RouteAddress<TAddress>(AsyncRouteDelivery<TAddress> handler)
        => this with
        {
            Handlers = Handlers.Add(async (delivery,cancellationToken) =>
            {
                if (delivery.State != MessageDeliveryState.Submitted || Hub.Address.Equals(delivery.Target))
                    return delivery;
                var routedAddress = FlattenAddressHierarchy(delivery.Target).OfType<TAddress>().FirstOrDefault();
                if (routedAddress == null)
                    return delivery;
                // TODO: should we take care of result from handler somehow?
                logger.LogDebug("Forwarding delivery {id} of type {type} from {sender} with original target {target} to routed address {routedAddress}", delivery.Id, delivery.Message.GetType().Name
                ,delivery.Sender, delivery.Target, routedAddress);

                return await handler(routedAddress, delivery, cancellationToken);
            }
            ),
        };

    public RouteConfiguration RouteMessage<TMessage>(Func<IMessageDelivery<TMessage>, object> addressMap) =>
        RouteMessage(addressMap, d => Hub.Address.Equals(d.Target));

    public RouteConfiguration RouteMessage<TMessage>(Func<IMessageDelivery<TMessage>, object> addressMap, Func<IMessageDelivery<TMessage>, bool> filter)
    {
        return RouteMessage((delivery, cancellationToken) =>
            {
                var mappedAddress = addressMap((IMessageDelivery<TMessage>)delivery);
                if (mappedAddress == null || mappedAddress.Equals(delivery.Target))
                    return Task.FromResult(delivery);
                if (!delivery.Sender.Equals(Hub.Address))
                    RoutedMessageAddresses.GetOrAdd(mappedAddress, _ => new()).Add(delivery.Sender);
                var forwardedDelivery = Hub.Post(delivery.Message, o => o.WithProperties(delivery.Properties).WithTarget(mappedAddress));
                logger.LogDebug("Forwarding delivery {id}  of type {type} from {sender} with original target {target} to mapped address {routedAddress}", delivery.Id, delivery.Message.GetType().Name, delivery.Sender, delivery.Target, mappedAddress);
                if (delivery.Message is IRequest)
                    Hub.RegisterCallback(forwardedDelivery,
                        response =>
                        {
                            logger.LogDebug("Forwarding response {id} of type {type} from {sender} with original target {target} to original sender {originalSender}", response.Id, delivery.Message.GetType().Name, response.Sender, response.Target, delivery.Sender);
                            return Hub.Post(response.Message,
                                o => o.WithProperties(response.Properties).ResponseFor(delivery));
                        });
                return Task.FromResult(delivery.Forwarded());
            },
            filter);
    }


    private RouteConfiguration RouteMessage<TMessage>(AsyncDelivery handler, Func<IMessageDelivery<TMessage>, bool> filter)
        => this with
        {
            Handlers = Handlers.Add((d,c) =>
                {
                    if(
                            (d.Target != null && !Hub.Address.Equals(d.Target))
                            || d.State != MessageDeliveryState.Submitted 
                            || d is not IMessageDelivery<TMessage> delivery 
                            || !filter(delivery)
                            )
                        return Task.FromResult(d);

                    return handler(delivery, c);
                }
            ),
        };


    public RouteConfiguration RouteAddressToHostedHub<TAddress>(Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        => RouteAddressToHub<TAddress>(a => Hub.GetHostedHub(a, configuration));

    private IEnumerable<object> FlattenAddressHierarchy(object address)
    {
        while (address != null)
        {
            yield return address;
            address = (address as IHostedAddress)?.Host;
        }
    }

}



public interface IForwardConfigurationItem
{

    AsyncDelivery Route { get; }
    bool Filter(IMessageDelivery delivery);
}
public record ForwardConfigurationItem<TMessage> : IForwardConfigurationItem
{


    AsyncDelivery IForwardConfigurationItem.Route => (d,c) => Route((IMessageDelivery<TMessage>)d, c);


    bool IForwardConfigurationItem.Filter(IMessageDelivery delivery)
    {
        if (delivery.Message is not TMessage)
            return false;
        return Filter?.Invoke((IMessageDelivery<TMessage>)delivery) ?? true;
    }


    internal AsyncDelivery Route { get; init; }


    public ForwardConfigurationItem<TMessage> WithFilter(DeliveryFilter<TMessage> filter) => this with { Filter = filter };
    internal DeliveryFilter<TMessage> Filter { get; init; }

    public ForwardConfigurationItem<TMessage> WithInheritorsFromAssembliesOf(params Type[] types)
    {
        return this with { InheritorsFrom = types };
    }

    public ForwardConfigurationItem<TMessage> WithInheritance(bool inherit = true) => this with { ForwardInheritors = inherit };
    internal bool ForwardInheritors { get; init; } = true;
    public IReadOnlyCollection<Type> InheritorsFrom { get; init; } = Array.Empty<Type>();

}

