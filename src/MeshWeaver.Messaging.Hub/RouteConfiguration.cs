using System.Collections.Immutable;

namespace MeshWeaver.Messaging;


public record RouteConfiguration(IMessageHub Hub)
{
    internal ImmutableList<AsyncDelivery> Handlers { get; init; } = ImmutableList<AsyncDelivery>.Empty;

    internal readonly Dictionary<Address, HashSet<Address>> RoutedMessageAddresses = new();

    public RouteConfiguration WithHandler(AsyncDelivery handler) => this with { Handlers = Handlers.Add(handler) };

    public RouteConfiguration RouteAddressToHub<TAddress>(Func<TAddress, IMessageHub> hubFactory)
        where TAddress:Address=>
        RouteAddress<TAddress>(async (routedAddress, d, ct) =>
        {
            var hub = hubFactory(routedAddress);
            if (hub == null)
                return d.NotFound();
            await hub.DeliverMessageAsync(d, ct);
            return d.Forwarded();
        });

    public RouteConfiguration RouteAddress<TAddress>(SyncRouteDelivery<TAddress> handler) 
        where TAddress:Address=>
        RouteAddress<TAddress>((routedAddress, d, _) => Task.FromResult(handler(routedAddress, d)));

    public RouteConfiguration RouteAddress<TAddress>(AsyncRouteDelivery<TAddress> handler)
    where TAddress:Address
        => this with
        {
            Handlers = Handlers.Add(async (delivery, cancellationToken) =>
                {
                    if (delivery.State != MessageDeliveryState.Submitted || Hub.Address.Equals(delivery.Target))
                        return delivery;

                    if (delivery.Target is TAddress routedAddress)
                        return await handler(routedAddress, delivery, cancellationToken);

                    if (delivery.Target is HostedAddress { Host: TAddress hostRoutedAddress, Address: { } address })
                        return await handler(hostRoutedAddress, delivery.WithTarget(address), cancellationToken);

                    return delivery;
                }
            ),
        };




    public RouteConfiguration RouteAddressToHostedHub<TAddress>(Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        where TAddress : Address
        => RouteAddressToHub<TAddress>(a => Hub.GetHostedHub(a, configuration));

}



