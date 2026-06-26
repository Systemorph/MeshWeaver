using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeshWeaver.Domain;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Messaging;

/// <summary>
/// Convenience extensions over <see cref="IMessageHub"/>,
/// <see cref="IServiceProvider"/>, and addresses — hub construction, typed
/// response observation, route and type-registry access, and address parsing.
/// </summary>
public static class MessageHubExtensions
{
    /// <summary>
    /// Builds a new <see cref="IMessageHub"/> for <paramref name="address"/> from
    /// the given service provider, applying the optional configuration transform.
    /// The address's type is registered with the hub automatically.
    /// </summary>
    /// <param name="serviceProvider">Service provider used to resolve hub dependencies.</param>
    /// <param name="address">The address that identifies the new hub.</param>
    /// <param name="configuration">Optional configuration transform; identity if omitted.</param>
    /// <returns>The constructed hub.</returns>
    public static IMessageHub CreateMessageHub(this IServiceProvider serviceProvider, Address address, Func<MessageHubConfiguration, MessageHubConfiguration>? configuration = null)
    {
        var hubSetup = new MessageHubConfiguration(serviceProvider, address)
            .WithTypes(address.GetType());
        configuration ??= x => x;
        return configuration(hubSetup).Build(serviceProvider, address);
    }

    /// <summary>
    /// Reads the originating request id stamped on a delivery's properties, if any.
    /// </summary>
    /// <param name="delivery">The delivery to inspect.</param>
    /// <returns>The request id, or null when the delivery is not a response to a tracked request.</returns>
    public static string? GetRequestId(this IMessageDelivery delivery)
        => delivery.Properties.GetValueOrDefault(PostOptions.RequestId) as string;

    /// <summary>
    /// Subscribes to the response for an already-posted <paramref name="delivery"/> as an observable.
    /// Emits exactly one IMessageDelivery on the response (or an error). Replaces the
    /// Task-returning RegisterCallback overloads — IObservable composes cleanly with Subscribe(onNext, onError),
    /// so DeliveryFailure flows through onError as a DeliveryFailureException without the
    /// Task-await temptation that deadlocks hub handlers.
    /// </summary>
    public static IObservable<IMessageDelivery> Observe(this IMessageHub hub, IMessageDelivery delivery)
        => hub.Observe(delivery);

    /// <summary>
    /// Posts <paramref name="request"/> and observes the typed response.
    /// <para>
    /// Pre-registers the callback BEFORE posting (via the framework's
    /// <c>AwaitResponse(object, Func&lt;PostOptions,PostOptions&gt;, Func&lt;IMessageDelivery,object?&gt;, CancellationToken)</c>
    /// primitive which uses <see cref="PostOptions.WithMessageId"/>) so a synchronously-handled
    /// response can't slip through before the callback is registered. Emits exactly one
    /// <see cref="IMessageDelivery{TResponse}"/> on the response, or <c>OnError</c> for
    /// <see cref="DeliveryFailureException"/> / <see cref="TimeoutException"/>.
    /// </para>
    /// </summary>
    public static IObservable<IMessageDelivery<TResponse>> Observe<TResponse>(
        this IMessageHub hub,
        IRequest<TResponse> request,
        Func<PostOptions, PostOptions>? options = null)
        => hub.Observe((object)request, options ?? (o => o))
            .Select(d => d is IMessageDelivery<TResponse> typed
                ? typed
                : throw new InvalidOperationException(
                    $"Observe<{typeof(TResponse).Name}>: unexpected response type {d.Message?.GetType().Name ?? "null"}"));

    /// <summary>
    /// Appends a routing-configuration lambda to the hub configuration. The lambda
    /// is applied to the hub's <see cref="RouteConfiguration"/> at build time,
    /// letting callers register address routes and handlers.
    /// </summary>
    /// <param name="config">The hub configuration to extend.</param>
    /// <param name="lambda">Transform that adds routes to the route configuration.</param>
    /// <returns>The updated hub configuration, for chaining.</returns>
    public static MessageHubConfiguration WithRoutes(this MessageHubConfiguration config,
        Func<RouteConfiguration, RouteConfiguration> lambda)
        => config.Set(config.GetListOfRouteLambdas().Add(lambda));

    internal static ImmutableList<Func<RouteConfiguration, RouteConfiguration>> GetListOfRouteLambdas(
        this MessageHubConfiguration config)
        => config.Get<ImmutableList<Func<RouteConfiguration, RouteConfiguration>>>() ?? [];


    /// <summary>
    /// Gets the <see cref="ITypeRegistry"/> associated with the hub (resolved from
    /// its service provider).
    /// </summary>
    /// <param name="hub">The hub whose type registry to retrieve.</param>
    /// <returns>The hub's type registry.</returns>
    public static ITypeRegistry GetTypeRegistry(this IMessageHub hub)
        => hub.ServiceProvider.GetTypeRegistry();
    /// <summary>
    /// Resolves the <see cref="ITypeRegistry"/> from a service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider to resolve from.</param>
    /// <returns>The registered type registry.</returns>
    public static ITypeRegistry GetTypeRegistry(this IServiceProvider serviceProvider)
        => serviceProvider.GetRequiredService<ITypeRegistry>();

    /// <summary>
    /// Creates a new type registry that can be used at the mesh level.
    /// Hub-level type registries will inherit from this registry if registered as ITypeRegistry.
    /// </summary>
    /// <param name="parent">Optional parent registry to inherit from</param>
    /// <returns>A new type registry</returns>
    public static ITypeRegistry CreateTypeRegistry(ITypeRegistry? parent = null)
        => new TypeRegistry(parent);

    /// <summary>
    /// Splits an address-shaped instance into its type and id parts. Accepts a
    /// <c>JsonObject</c> (reads the serialized type property) or an
    /// address string in <c>AddressType/AddressId</c> form.
    /// </summary>
    /// <param name="instance">The address instance or JSON object to parse.</param>
    /// <returns>A tuple of the address type and the (possibly multi-segment) address id.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a string address is not in <c>AddressType/AddressId</c> form.</exception>
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

    /// <summary>
    /// Returns the address as type <typeparamref name="T"/> if it is (or its
    /// host-stripped form is) of that type; otherwise the default.
    /// </summary>
    /// <typeparam name="T">The target address type.</typeparam>
    /// <param name="address">The address to match.</param>
    /// <returns>The matching address of type <typeparamref name="T"/>, or default if none matches.</returns>
    public static T? GetAddressOfType<T>(object address)
    {
        if (address is T ret)
            return ret;
        if (address is Address { Host: not null } hosted)
            return GetAddressOfType<T>(hosted with { Host = null });
        return default;
    }

    /// <summary>
    /// Returns the address whose <c>Type</c> matches <paramref name="addressType"/>,
    /// checking the address and its host-stripped form; otherwise null.
    /// </summary>
    /// <param name="address">The address to match.</param>
    /// <param name="addressType">The address type string to match against.</param>
    /// <returns>The matching <see cref="Address"/>, or null if none matches.</returns>
    public static Address? GetAddressOfType(object address, string addressType)
    {
        if (address is Address addr && addr.Type == addressType)
            return addr;
        if (address is Address { Host: not null } hosted)
            return GetAddressOfType(hosted with { Host = null }, addressType);
        return null;
    }

    /// <summary>
    /// Parses an address string into an <see cref="Address"/>, honouring the
    /// <c>@</c> separator for hosted addresses (via the implicit string-to-address
    /// conversion).
    /// </summary>
    /// <param name="hub">The hub the call is made against (not otherwise used).</param>
    /// <param name="address">The address string to parse.</param>
    /// <returns>The parsed address.</returns>
    public static Address GetAddress(this IMessageHub hub, string address)
    {
        // Use implicit conversion which handles @ separator for hosted addresses
        return address;
    }


    /// <summary>
    /// Starts a long-running operation scope that keeps the Orleans grain alive.
    /// Uses GrainLongRunningOperationCallback (RegisterGrainTimer + DelayDeactivation)
    /// when running in Orleans, falls back to Observable.Interval heartbeat otherwise.
    /// Dispose the returned IDisposable when the operation completes.
    /// In monolith mode, returns a no-op disposable.
    /// </summary>
    public static IDisposable BeginAsyncOperation(this IMessageHub hub)
    {
        // Walk parent chain to find the grain-level callback
        var current = hub;
        while (current != null)
        {
            var callback = current.Configuration.Get<GrainLongRunningOperationCallback>();
            if (callback != null)
                return callback.BeginOperation();
            var parent = current.Configuration.ParentHub;
            if (parent == current) break;
            current = parent;
        }

        // No grain callback anywhere in the parent chain ⇒ we're in monolith
        // mode (or in a hub kind that has no long-running-operation support).
        // The XML doc above documents this returns a no-op disposable. The
        // previous shape posted HeartBeatEvent on a 25s Observable.Interval
        // intending to "stop on first DeliveryFailure when no handler is
        // registered" — but the heartbeat timer runs on a background thread
        // with no user identity, so the post pipeline records null
        // AccessContext on HeartBeatEvent. The receiving hub's "no handler"
        // path posts DeliveryFailure with PostOptions.ResponseFor(delivery),
        // which inherits that null AccessContext. The pipeline then fails
        // closed dropping the DeliveryFailure → the sender's OnError never
        // fires → the timer loops forever. Symptom: testhost stays alive
        // 5+ minutes after every test in the project finished, emitting
        // "No handler found for HeartBeatEvent" + "DeliveryFailure posted
        // with no AccessContext" every 25 s until the runner kills the host.
        // In monolith there's nothing for the heartbeat to keep alive anyway
        // (no grain to delay-deactivate), so a no-op is the correct shape.
        return Disposable.Empty;
    }
}
