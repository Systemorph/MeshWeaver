using System.Collections.Immutable;

namespace MeshWeaver.Messaging;


public record RouteConfiguration(IMessageHub Hub)
{
    internal ImmutableList<AsyncDelivery> Handlers { get; init; } = ImmutableList<AsyncDelivery>.Empty;

    internal readonly Dictionary<Address, HashSet<Address>> RoutedMessageAddresses = new();

    public RouteConfiguration WithHandler(AsyncDelivery handler) => this with { Handlers = Handlers.Add(handler) };

    public RouteConfiguration RouteAddressToHub<TAddress>(Func<TAddress, IMessageHub?> hubFactory)
        where TAddress : Address =>
        RouteAddress<TAddress>((routedAddress, d) =>
        {
            // Check if the parent hub is disposing before attempting to create/route to hosted hubs
            if (Hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
            {
                // During disposal, reject messages to hosted hubs to prevent deadlocks
                Hub.Post(
                    new DeliveryFailure(d)
                    {
                        ErrorType = ErrorType.Rejected,
                        Message = $"Cannot route to hosted hub {routedAddress} - parent hub {Hub.Address} is disposing"
                    }, o => o.ResponseFor(d)
                );
                return d.Failed("Parent hub disposing");
            }

            var hub = hubFactory.Invoke(routedAddress);
            if (hub == null)
                return d.NotFound();
            hub.DeliverMessage(d);
            return d.Forwarded();
        });

    public RouteConfiguration RouteAddress<TAddress>(SyncRouteDelivery<TAddress> handler)
        where TAddress : Address =>
        RouteAddress<TAddress>((routedAddress, d, _) => Task.FromResult(handler(routedAddress, d)));

    public RouteConfiguration RouteAddress<TAddress>(AsyncRouteDelivery<TAddress> handler)
    where TAddress : Address
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
        => RouteAddressToHub<TAddress>(a =>
        {
            // During disposal, only try to get existing hubs, don't create new ones
            var creation = Hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs ? HostedHubCreation.Never : HostedHubCreation.Always;
            return Hub.GetHostedHub(a, configuration, creation);
        });

}



