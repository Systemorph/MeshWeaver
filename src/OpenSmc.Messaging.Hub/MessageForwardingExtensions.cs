using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenSmc.Messaging.Hub;

public static class MessageForwardingExtensions
{
    public static MessageHubConfiguration WithMessageForwarding(this MessageHubConfiguration configuration, Func<RoutedHubConfiguration, RoutedHubConfiguration> routedHubBuilder)
    {

        return configuration.WithBuildupAction(hub =>
        {
            hub.WithMessageForwarding(routedHubBuilder);
        });
    }
    public static void WithMessageForwarding(this IMessageHub hub, Func<RoutedHubConfiguration, RoutedHubConfiguration> routedHubBuilder)
    {
        var routedHubConfiguration = routedHubBuilder.Invoke(new());
        routedHubConfiguration = routedHubConfiguration.Buildup(hub);
        hub.Post(new UpdateRequest<RoutedHubConfiguration>(routedHubConfiguration));
    }

    public static MessageHubConfiguration WithHostedHub<TAddress>(this MessageHubConfiguration configuration,
        Func<MessageHubConfiguration, MessageHubConfiguration> configureHostedHub)
    {
        return configuration
            .WithServices(s => s
                .Replace(ServiceDescriptor.Singleton(new HostedHubConfigurationSettings<TAddress>
                {
                    Configure = configureHostedHub
                })))
            .WithForwards(f => f.RouteAddressToHub<TAddress>(d => f.Hub.GetHostedHub((TAddress)d.Target)));
            //.WithMessageForwarding(f => f.RouteAddress<TAddress>(a => a, c => c.WithHost(f.Hub.GetHostedHub((TAddress)d.Target))  d => f.Hub.GetHostedHub((TAddress)d.Target))); // TODO: change this to support object as input
            //.WithRoutedAddress()
        // TODO: add test for object-oriented addresses implementations. i.e. (a is TAddress)
        // NonDeserializedAddress : IHostedAddress
    }


    public static bool IsAddress<TAddress>(this object address)
    {
        if (address is TAddress)
            return true;

        if (address is IHostedAddress ha)
            return IsAddress<TAddress>(ha.Host);

        return false;
    }
}


public record MessageForwardingDefinition
{
    private ImmutableList<(SyncDelivery Delivery, Predicate<object> Filter)> Routes { get; init; } = ImmutableList<(SyncDelivery, Predicate<object>)>.Empty;
    private SyncDelivery DefaultDelivery { get; set; }

    public MessageForwardingDefinition WithRoutedAddress<TRoutedAddress>(SyncDelivery delivery)
    {
        return WithRoutedAddress(delivery, a => a.IsAddress<TRoutedAddress>());
    }
    public MessageForwardingDefinition WithRoutedAddress(SyncDelivery delivery, Predicate<object> addressFilter)
    {
        return this with { Routes = Routes.Add((delivery, addressFilter)) };
    }

    public MessageForwardingDefinition WithDefaultRoute(SyncDelivery delivery)
    {
        return this with { DefaultDelivery = delivery };
    }

    public MessageHubConfiguration Build<TAddress>(MessageHubConfiguration configuration)
    {

        if (DefaultDelivery == null)
        {
            var mainRouterHub = configuration.ServiceProvider.GetService<IMessageHub>();

            if (mainRouterHub != null)
            {
                DefaultDelivery = mainRouterHub.DeliverMessage;
            }
        }

        return configuration;

    }
}
