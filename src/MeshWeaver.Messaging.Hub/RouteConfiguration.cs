using System.Collections.Immutable;

namespace MeshWeaver.Messaging;


public record RouteConfiguration(IMessageHub Hub)
{
    internal ImmutableList<AsyncDelivery> Handlers { get; init; } = ImmutableList<AsyncDelivery>.Empty;

    internal readonly Dictionary<Address, HashSet<Address>> RoutedMessageAddresses = new();

    public RouteConfiguration WithHandler(AsyncDelivery handler) => this with { Handlers = Handlers.Add(handler) };

    /// <summary>
    /// Routes addresses of a specific type (by type string) to a hub created by the factory.
    /// </summary>
    public RouteConfiguration RouteAddressToHub(string addressType, Func<Address, IMessageHub?> hubFactory)
        => RouteAddress(addressType, (routedAddress, d) =>
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

    /// <summary>
    /// Routes addresses of a specific type to a handler.
    /// </summary>
    public RouteConfiguration RouteAddress(string addressType, SyncRouteDelivery handler)
        => RouteAddress(addressType, (routedAddress, d, _) => Task.FromResult(handler(routedAddress, d)));

    /// <summary>
    /// Routes addresses of a specific type to an async handler.
    /// </summary>
    public RouteConfiguration RouteAddress(string addressType, AsyncRouteDelivery handler)
        => this with
        {
            Handlers = Handlers.Add(async (delivery, cancellationToken) =>
                {
                    if (delivery.State != MessageDeliveryState.Submitted || Hub.Address.Equals(delivery.Target))
                        return delivery;

                    // Match by type string (first segment)
                    if (delivery.Target?.Type == addressType)
                    {
                        return await handler(delivery.Target, delivery, cancellationToken);
                    }

                    if (delivery.Target?.Host?.Type == addressType)
                    {
                        return await handler(delivery.Target.Host, delivery.WithTarget(delivery.Target with { Host = null }), cancellationToken);
                    }

                    return delivery;
                }
            ),
        };

    /// <summary>
    /// Routes addresses of a specific type (by type string) to a hosted hub.
    /// </summary>
    public RouteConfiguration RouteAddressToHostedHub(string addressType, Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
        => RouteAddressToHub(addressType, a =>
        {
            // During disposal, only try to get existing hubs, don't create new ones
            var creation = Hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs ? HostedHubCreation.Never : HostedHubCreation.Always;
            return Hub.GetHostedHub(a, configuration, creation);
        });
}
