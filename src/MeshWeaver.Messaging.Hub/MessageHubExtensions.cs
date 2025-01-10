using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

public static class MessageHubExtensions
{
    public static IMessageHub CreateMessageHub(this IServiceProvider serviceProvider, Address address, Func<MessageHubConfiguration, MessageHubConfiguration> configuration = null)
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


    public static ITypeRegistry GetTypeRegistry(this IMessageHub hub)
        => hub.ServiceProvider.GetTypeRegistry();
    public static ITypeRegistry GetTypeRegistry(this IServiceProvider serviceProvider)
        => serviceProvider.GetRequiredService<ITypeRegistry>();

    public static (string AddressType, string AddressId) GetAddressTypeAndId(object instance)
    {
        if(instance is JsonObject jObj)
            return (jObj[EntitySerializationExtensions.TypeProperty]?.ToString(), jObj[EntitySerializationExtensions.TypeProperty]?.ToString());

        var s = instance.ToString();
        var split = s!.Split('/');
        if (split.Length < 2)
            throw new InvalidOperationException($"Address {s} is not in the correct format. Expected format is AddressType/AddressId");

        return (split[0], string.Join('/', split.Skip(1)));
    }

    public static T GetAddressOfType<T>(object address)
    {
        if (address is T ret)
            return ret;
        if (address is HostedAddress hosted)
            return GetAddressOfType<T>(hosted.Address);
        return default;
    }
}
