﻿using System.Collections.Immutable;
using OpenSmc.Collections;

namespace OpenSmc.Messaging;


public record RouteConfiguration(IMessageHub Hub)
{


    internal ImmutableList<AsyncDelivery> Handlers { get; init; } = ImmutableList<AsyncDelivery>.Empty;

    internal readonly Dictionary<object, HashSet<object>> RoutedMessageAddresses = new();


    public RouteConfiguration RouteAddressToHub<TAddress>(Func<TAddress, IMessageHub> hubFactory) =>
        RouteAddress<TAddress>((routedAddress, d) =>
        {
            var hub = hubFactory(routedAddress);
            hub.DeliverMessage(d);
            return d.Forwarded();
        });

    public RouteConfiguration RouteAddress<TAddress>(SyncRouteDelivery<TAddress> handler) =>
        RouteAddress<TAddress>((routedAddress, d) => Task.FromResult(handler(routedAddress, d)));
        
    public RouteConfiguration RouteAddress<TAddress>(AsyncRouteDelivery<TAddress> handler)
        => this with
        {
            Handlers = Handlers.Add(async delivery =>
            {
                if (delivery.State != MessageDeliveryState.Submitted || Hub.Address.Equals(delivery.Target))
                    return delivery;
                var routedAddress = FlattenAddressHierarchy(delivery.Target).OfType<TAddress>().FirstOrDefault();
                if (routedAddress == null)
                    return delivery;
                // TODO: should we take care of result from handler somehow?
                return await handler(routedAddress, delivery);
            }
            ),
        };

    public RouteConfiguration RouteMessage<TMessage>(Func<IMessageDelivery<TMessage>, object> addressMap) =>
        RouteMessage(addressMap, _ => true);

    public RouteConfiguration RouteMessage<TMessage>(Func<IMessageDelivery<TMessage>, object> addressMap, Func<IMessageDelivery<TMessage>, bool> filter)
    {
        return RouteMessage(delivery =>
            {
                var mappedAddress = addressMap((IMessageDelivery<TMessage>)delivery);
                if (!delivery.Sender.Equals(Hub.Address))
                    RoutedMessageAddresses.GetOrAdd(mappedAddress, _ => new()).Add(delivery.Sender);
                Hub.Post(delivery.Message, o => o.WithProperties(delivery.Properties).WithTarget(mappedAddress));
                return Task.FromResult(delivery.Forwarded());
            },
            filter);
    }


    private RouteConfiguration RouteMessage<TMessage>(AsyncDelivery handler, Func<IMessageDelivery<TMessage>, bool> filter)
        => this with
        {
            Handlers = Handlers.Add(d =>
                {
                    if(
                            (d.Target != null && !Hub.Address.Equals(d.Target))
                            || d.State != MessageDeliveryState.Submitted 
                            || d is not IMessageDelivery<TMessage> delivery 
                            || !filter(delivery)
                            )
                        return Task.FromResult(d);

                    return handler(delivery);
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


    AsyncDelivery IForwardConfigurationItem.Route => d => Route((IMessageDelivery<TMessage>)d);


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

