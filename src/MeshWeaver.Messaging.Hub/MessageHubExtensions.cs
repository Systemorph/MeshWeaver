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

    /// <summary>
    /// Sends a request deserialized from JSON and awaits the response.
    /// This is useful when working with JSON-based messaging without direct type references.
    /// </summary>
    /// <param name="hub">The message hub</param>
    /// <param name="request">The request object (deserialized from JSON)</param>
    /// <param name="options">Post options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The response message</returns>
    public static async Task<object> AwaitResponse(
        this IMessageHub hub,
        object request,
        Func<PostOptions, PostOptions> options,
        CancellationToken cancellationToken = default)
    {
        // Find the IRequest<TResponse> interface to get the response type
        var requestType = request.GetType();
        var requestInterface = requestType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>));

        if (requestInterface == null)
            throw new InvalidOperationException($"Request type {requestType.Name} does not implement IRequest<TResponse>");

        var responseType = requestInterface.GetGenericArguments()[0];

        // Use reflection to call AwaitResponse<TResponse, TResult>
        var awaitResponseMethod = typeof(IMessageHub).GetMethods()
            .FirstOrDefault(m =>
                m.Name == nameof(IMessageHub.AwaitResponse) &&
                m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 2 &&
                m.GetParameters().Length == 3);

        if (awaitResponseMethod == null)
            throw new InvalidOperationException("Could not find AwaitResponse method");

        var genericMethod = awaitResponseMethod.MakeGenericMethod(responseType, responseType);

        // Create the result selector lambda: (IMessageDelivery<TResponse> d) => d.Message
        var deliveryParam = System.Linq.Expressions.Expression.Parameter(typeof(IMessageDelivery<>).MakeGenericType(responseType), "d");
        var messageProperty = System.Linq.Expressions.Expression.Property(deliveryParam, "Message");
        var lambda = System.Linq.Expressions.Expression.Lambda(messageProperty, deliveryParam);
        var resultSelector = lambda.Compile();

        var task = (Task)genericMethod.Invoke(hub, new object[] { request, options, resultSelector })!;
        await task.ConfigureAwait(false);

        var resultProperty = task.GetType().GetProperty("Result");
        return resultProperty!.GetValue(task)!;
    }
}
