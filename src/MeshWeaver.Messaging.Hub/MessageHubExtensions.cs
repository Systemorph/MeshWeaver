using System.Collections.Immutable;
using System.Text.Json.Nodes;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

public static class MessageHubExtensions
{
    public static IMessageHub CreateMessageHub(this IServiceProvider serviceProvider, Address address, Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = null)
    {
        var hubSetup = new MessageHubConfiguration(serviceProvider, address)
            .WithTypes(address.GetType());
        configuration ??= x => x;
        return configuration(hubSetup).Build(serviceProvider, address);
    }

    public static string? GetRequestId(this IMessageDelivery delivery)
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
        if (instance is JsonObject jObj)
            return (jObj[EntitySerializationExtensions.TypeProperty]?.ToString() ?? string.Empty, jObj[EntitySerializationExtensions.TypeProperty]?.ToString() ?? string.Empty);

        var s = instance.ToString();
        var split = s!.Split('/');
        if (split.Length < 2)
            throw new InvalidOperationException($"Address {s} is not in the correct format. Expected format is AddressType/AddressId");

        return (split[0], string.Join('/', split.Skip(1)));
    }

    public static T? GetAddressOfType<T>(object address)
    {
        if (address is T ret)
            return ret;
        if (address is HostedAddress hosted)
            return GetAddressOfType<T>(hosted.Address);
        return default;
    }

    public static Address GetAddress(this IMessageHub hub, string address)
    {
        var split = address.Split('/');
        if (split.Length < 2)
            throw new InvalidOperationException($"Address {address} is not in the correct format. Expected format is AddressType/AddressId");
        var type = hub.GetTypeRegistry().GetType(split[0]);

        if (type is null)
            throw new InvalidOperationException($"Unknown address type {split[0]} for {address}. Expected format is AddressType/AddressId");

        return (Address)Activator.CreateInstance(type, [string.Join('/', split.Skip(1))])!;
    }
}
