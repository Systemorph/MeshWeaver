using System.Collections.Immutable;

namespace OpenSmc.Messaging;

public record ForwardConfiguration(IMessageHub Hub)
{

    internal ImmutableList<AsyncDelivery> Handlers { get; init; } = ImmutableList<AsyncDelivery>.Empty;



    public ForwardConfiguration RouteAddressToHub<TAddress>(Func<TAddress, IMessageHub> hubFactory) =>
        RouteAddress<TAddress>((routedAddress, d) => hubFactory(routedAddress).DeliverMessage(d));

    public ForwardConfiguration RouteAddress<TAddress>(SyncRouteDelivery<TAddress> handler) =>
        RouteAddress<TAddress>((routedAddress, d) =>
        {
            handler(routedAddress, d);
            return Task.CompletedTask;
        });

    public ForwardConfiguration RouteAddress<TAddress>(AsyncRouteDelivery<TAddress> handler)
        => this with
        {
            Handlers = Handlers.Add(async delivery =>
            {
                if (delivery.State != MessageDeliveryState.Submitted)
                    return delivery;
                var routedAddress = FlattenAddressHierarchy(delivery.Target).OfType<TAddress>().FirstOrDefault();
                if (routedAddress == null)
                    return delivery;
                await handler(routedAddress, delivery);
                // TODO: should we take care of result from handler somehow?
                return delivery.Forwarded();
            }
            ),
        };

    public ForwardConfiguration RouteAddressToHostedHub<TAddress>(Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
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

