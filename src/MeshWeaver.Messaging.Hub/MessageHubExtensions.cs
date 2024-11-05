using System.Collections.Immutable;

namespace MeshWeaver.Messaging;

public static class MessageHubExtensions
{
    public static IMessageHub CreateMessageHub(this IServiceProvider serviceProvider, object address, Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
    {
        var hubSetup = new MessageHubConfiguration(serviceProvider, address)
            .WithTypes(address.GetType());
        configuration ??= x => x;
        return configuration(hubSetup).Build(serviceProvider, address);
    }

    public static string GetRequestId(this IMessageDelivery delivery)
        => delivery.Properties.GetValueOrDefault(PostOptions.RequestId) as string;

    public static MessageHubConfiguration WithRoutes(this MessageHubConfiguration config,
        Func<RouteConfiguration, RouteConfiguration> lambda)
        => config.Set(config.GetListOfRouteLambdas().Add(lambda));

    internal static ImmutableList<Func<RouteConfiguration, RouteConfiguration>> GetListOfRouteLambdas(
        this MessageHubConfiguration config)
        => config.Get<ImmutableList<Func<RouteConfiguration, RouteConfiguration>>>() ?? [];

}
