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
    /// Only matches when Target.Type equals addressType directly.
    /// Does NOT match based on Target.Host.Type - use RouteAddressToHostedHub for that.
    /// </summary>
    public RouteConfiguration RouteAddress(string addressType, AsyncRouteDelivery handler)
        => this with
        {
            Handlers = Handlers.Add(async (delivery, cancellationToken) =>
                {
                    if (delivery.State != MessageDeliveryState.Submitted || Hub.Address.Equals(delivery.Target))
                        return delivery;

                    // Match by type string - only when Target.Type matches directly
                    // Do NOT match based on Target.Host.Type to avoid routing loops
                    if (delivery.Target?.Type == addressType)
                    {
                        // Extract just the inner address (without Host) for routing
                        // The inner address is what we route to; Host tracks the routing path
                        var innerAddress = delivery.Target with { Host = null };
                        return await handler(innerAddress, delivery, cancellationToken);
                    }

                    return delivery;
                }
            ),
        };

    /// <summary>
    /// Routes addresses of a specific type (by type string) to a hosted hub.
    /// Also handles messages where Target.Host.Type matches - routing to the Host address first.
    /// </summary>
    public RouteConfiguration RouteAddressToHostedHub(string addressType, Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
    {
        // Create a hub factory
        IMessageHub? GetOrCreateHub(Address address)
        {
            var creation = Hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs ? HostedHubCreation.Never : HostedHubCreation.Always;
            return Hub.GetHostedHub(address, configuration, creation);
        }

        // First add the direct Target.Type matching via RouteAddressToHub
        var result = RouteAddressToHub(addressType, GetOrCreateHub);

        // Then add a handler for Target.Host.Type matching
        return result with
        {
            Handlers = result.Handlers.Add(async (delivery, cancellationToken) =>
            {
                if (delivery.State != MessageDeliveryState.Submitted || Hub.Address.Equals(delivery.Target))
                    return delivery;

                // Match when Target.Host.Type equals addressType - route to the Host hub first
                if (delivery.Target?.Host?.Type == addressType)
                {
                    if (Hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                    {
                        Hub.Post(
                            new DeliveryFailure(delivery)
                            {
                                ErrorType = ErrorType.Rejected,
                                Message = $"Cannot route to hosted hub {delivery.Target.Host} - parent hub {Hub.Address} is disposing"
                            }, o => o.ResponseFor(delivery)
                        );
                        return delivery.Failed("Parent hub disposing");
                    }

                    var hub = GetOrCreateHub(delivery.Target.Host);
                    if (hub == null)
                        return delivery.NotFound();

                    // Deliver with Target stripped of its Host
                    hub.DeliverMessage(delivery.WithTarget(delivery.Target with { Host = null }));
                    return delivery.Forwarded();
                }

                return delivery;
            })
        };
    }
}
