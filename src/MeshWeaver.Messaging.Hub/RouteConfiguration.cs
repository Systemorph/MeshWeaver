using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
            if (hub == null)
                return d.NotFound();
            hub.DeliverMessage(d);
            return d.Forwarded();
        });

    public RouteConfiguration RouteAddress<TAddress>(SyncRouteDelivery<TAddress> handler) =>
        RouteAddress<TAddress>((routedAddress, d, _) => Task.FromResult(handler(routedAddress, d)));

    public RouteConfiguration RouteAddress<TAddress>(AsyncRouteDelivery<TAddress> handler)
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
        => RouteAddressToHub<TAddress>(a => Hub.GetHostedHub(a, configuration));

    private IEnumerable<object> FlattenAddressHierarchy(object address)
    {
        while (address != null)
        {
            yield return address;
            address = (address as HostedAddress)?.Address;
        }
    }

}



