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
    /// <see cref="IMessageHub.AwaitResponse(object,Func{PostOptions,PostOptions},Func{IMessageDelivery,object?},CancellationToken)"/>
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

    /// <summary>
    /// Creates a new type registry that can be used at the mesh level.
    /// Hub-level type registries will inherit from this registry if registered as ITypeRegistry.
    /// </summary>
    /// <param name="parent">Optional parent registry to inherit from</param>
    /// <returns>A new type registry</returns>
    public static ITypeRegistry CreateTypeRegistry(ITypeRegistry? parent = null)
        => new TypeRegistry(parent);

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
        if (address is Address { Host: not null } hosted)
            return GetAddressOfType<T>(hosted with { Host = null });
        return default;
    }

    public static Address? GetAddressOfType(object address, string addressType)
    {
        if (address is Address addr && addr.Type == addressType)
            return addr;
        if (address is Address { Host: not null } hosted)
            return GetAddressOfType(hosted with { Host = null }, addressType);
        return null;
    }

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

        // Fallback: heartbeat via Observable.Interval (monolith or no grain callback).
        // Stop on the first DeliveryFailure so we don't spam warnings when no handler is registered.
        var cts = new CancellationTokenSource();
        IDisposable? sub = null;
        sub = Observable.Interval(TimeSpan.FromSeconds(25))
            .Subscribe(_ =>
            {
                var delivery = hub.Post(new HeartBeatEvent());
                if (delivery == null) return;
                // Subscribe to the heartbeat response. DeliveryFailure flows via OnError —
                // when there's no handler on the target, kill the heartbeat.
                hub.Observe(delivery).Subscribe(
                    _ => { },
                    _ =>
                    {
                        sub?.Dispose();
                        cts.Cancel();
                    });
            });
        return new CompositeDisposable(sub, Disposable.Create(() => cts.Cancel()));
    }
}
