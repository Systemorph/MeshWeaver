using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace MeshWeaver.Connection.Orleans;

public class OrleansRoutingService : IRoutingService, IDisposable
{
    private readonly IGrainFactory grainFactory;
    private readonly IServiceProvider serviceProvider;
    private readonly ILogger<OrleansRoutingService> logger;
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();
    private readonly CompositeDisposable inFlight = new();
    private volatile bool disposed;

    public OrleansRoutingService(
        IGrainFactory grainFactory,
        IServiceProvider serviceProvider,
        ILogger<OrleansRoutingService> logger)
    {
        this.grainFactory = grainFactory;
        this.serviceProvider = serviceProvider;
        this.logger = logger;
    }

    public IObservable<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        return Observable.Defer(() =>
        {
            var target = delivery.Target;
            if (target == null)
                return Observable.Return(delivery);

            var address = GetHostAddress(target);

            // 1. Check registered local streams (portals, in-process clients).
            //    Bridge the AsyncDelivery callback (Task-shaped) into the chain
            //    via FromAsync — single-shot, no leak.
            if (streams.TryGetValue(address, out var callback))
                return Observable.FromAsync(ct => callback.Invoke(delivery, ct));

            // 2. Background mesh dispatch via the routing grain. Path resolution
            //    runs INSIDE the grain (silo-side) where the catalog is visible —
            //    on the client, MeshConfiguration.Nodes is empty. Fire-and-forget
            //    Subscribe — errors flow into SendDeliveryFailure inside the
            //    chain. Tracked so Dispose can tear down outstanding work.
            if (!disposed)
            {
                var sub = new SingleAssignmentDisposable();
                inFlight.Add(sub);
                sub.Disposable = DispatchObservable(delivery, address)
                    .Catch<IMessageDelivery, Exception>(ex =>
                    {
                        logger.LogError(ex, "Failed to deliver to {Address}", address);
                        SendDeliveryFailure(delivery, $"Failed to deliver to {address}: {ex.Message}");
                        return Observable.Empty<IMessageDelivery>();
                    })
                    .Finally(() => inFlight.Remove(sub))
                    .Subscribe(_ => { }, ex => logger.LogError(ex, "Background dispatch faulted for {Address}", address));
            }

            return Observable.Return(delivery.Forwarded(address));
        });
    }

    /// <summary>
    /// Dispatches via the Orleans routing grain. The grain runs on the silo,
    /// where the mesh catalog has the seeded nodes; path resolution + per-node
    /// grain routing happen there. Retries with exponential backoff on
    /// transient failures.
    /// </summary>
    private IObservable<IMessageDelivery> DispatchObservable(IMessageDelivery delivery, Address address)
    {
        var addressPath = address.ToString();
        var msgType = delivery.Message.GetType().Name;
        var accessContext = delivery.AccessContext;
        if (accessContext != null)
        {
            RequestContext.Set("UserId", accessContext.ObjectId);
            RequestContext.Set("UserName", accessContext.Name);
        }

        if (accessContext == null || msgType.Contains("Submit", StringComparison.Ordinal))
            logger.LogWarning("Orleans: delivering {MessageType} to {Address}, accessContext={AccessUser}, sender={Sender}",
                msgType, address, accessContext?.ObjectId ?? "(null)", delivery.Sender);
        else
            logger.LogDebug("Orleans: delivering {MessageType} to {Address}, sender={Sender}, target={Target}",
                msgType, address, delivery.Sender, delivery.Target);

        var grain = grainFactory.GetGrain<IRoutingGrain>("default");

        return Observable.FromAsync(() => grain.RouteMessage(delivery))
            .RetryWhen(errors => errors
                .Select((ex, i) => (Exception: ex, Attempt: i))
                .SelectMany(t =>
                {
                    if (t.Attempt >= 5 || !IsTransientFailure(t.Exception))
                        return Observable.Throw<long>(t.Exception);
                    var delay = TimeSpan.FromMilliseconds(Math.Min(200 * Math.Pow(2, t.Attempt), 30_000));
                    logger.LogDebug(t.Exception, "Transient failure delivering to {Address}, attempt {Attempt}/5, retrying in {Delay}ms",
                        address, t.Attempt + 1, delay.TotalMilliseconds);
                    return Observable.Timer(delay);
                }))
            .Do(result =>
            {
                if (result.State == MessageDeliveryState.Failed)
                {
                    // Grain returned a non-transient failure (e.g., node doesn't exist).
                    // Preserve the RoutingGrain's message so the GUI's
                    // IsExpectedUserActionFailure classifier can match it.
                    var failureMessage = result.Properties.TryGetValue("Error", out var errObj) && errObj is string errStr
                        ? errStr
                        : $"Delivery failed to {address}";
                    logger.LogWarning("Orleans: delivery FAILED for {MessageType} to {Address}: {FailureMessage}",
                        msgType, address, failureMessage);
                    SendDeliveryFailure(delivery, failureMessage);
                }
                else
                {
                    logger.LogDebug("Orleans: delivered {MessageType} to {Address}, result={State}",
                        msgType, address, result.State);
                }
            });
    }

    private void SendDeliveryFailure(IMessageDelivery delivery, string message)
    {
        try
        {
            // Route the failure back to the sender so hub.Observe callers get an
            // exception. Use WithRequestIdFrom (NOT ResponseFor — that overrides
            // Target with the request's Sender, which we already set explicitly).
            var meshHub = serviceProvider.GetService<IMessageHub>();
            if (meshHub != null)
            {
                meshHub.Post(
                    new DeliveryFailure(delivery)
                    {
                        ErrorType = ErrorType.Failed,
                        Message = message
                    },
                    o => o.WithTarget(delivery.Sender).WithRequestIdFrom(delivery));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to send delivery failure for {MessageId}", delivery.Id);
        }
    }

    private static bool IsTransientFailure(Exception ex)
    {
        return ex is SocketException
            or HttpRequestException
            or TimeoutException
            or global::Orleans.Runtime.OrleansMessageRejectionException
            || (ex.InnerException != null && IsTransientFailure(ex.InnerException));
    }

    public IAsyncDisposable RegisterStream(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;

        // Subscribe to the Orleans memory stream in the background. The returned
        // disposable holds the subscription Task; DisposeAsync awaits it so we
        // can call UnsubscribeAsync on the resolved handle. A tiny window exists
        // between Register returning and SubscribeAsync completing during which
        // cross-process messages on the stream are buffered by Orleans (memory
        // streams replay-on-subscribe), so no messages are lost.
        var stream = GetStreamProvider(StreamProviders.Memory)
            .GetStream<IMessageDelivery>(address.ToString());
        var subscriptionTask = stream.SubscribeAsync((v, _) =>
            callback.Invoke(v, CancellationToken.None));

        return new AnonymousAsyncDisposable(async () =>
        {
            streams.TryRemove(address, out _);
            try
            {
                var subscription = await subscriptionTask;
                await subscription.UnsubscribeAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to unsubscribe Orleans stream for {Address}", address);
            }
        });
    }

    private IStreamProvider GetStreamProvider(string streamProvider) =>
        serviceProvider.GetRequiredKeyedService<IStreamProvider>(streamProvider);

    internal static Address GetHostAddress(Address address)
    {
        if (address.Host != null)
        {
            var host = GetHostAddress(address.Host);
            if (host.Type == AddressExtensions.MeshType)
                return address with { Host = null };
            return host;
        }
        return address;
    }

    public void Dispose()
    {
        disposed = true;
        inFlight.Dispose();
    }
}
