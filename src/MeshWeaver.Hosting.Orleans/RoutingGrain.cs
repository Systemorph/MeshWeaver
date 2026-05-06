using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

[StatelessWorker(1)]
internal class RoutingGrain(
    IPathResolver pathResolver,
    ILogger<RoutingGrain> logger) : Grain, IRoutingGrain
{
    public Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery)
    {
        var address = GetHostAddress(delivery.Target!);
        var addressPath = address.ToString();

        logger.LogDebug("[ROUTE] RouteMessage: {MessageType} → {Address}",
            delivery.Message.GetType().Name, addressPath);

        // 🚨 Pre-capture grain services on the activation thread.
        // After the SelectMany hops to Scheduler.Default (because IPathResolver
        // emits via Observable.FromAsync(..., Scheduler.Default) after the
        // persistence refactor), accessing `this.GetStreamProvider` or
        // `this.GrainFactory` directly throws "Activation access violation".
        // IStreamProvider and IGrainFactory references are themselves thread-safe
        // — they don't require the activation thread once obtained.
        var streamProvider = this.GetStreamProvider(StreamProviders.Memory);
        var grainFactory = GrainFactory;

        // Portal/client hubs are not grains — deliver via Orleans memory stream directly.
        if (address.Type == AddressExtensions.PortalType || address.Type == "client")
        {
            logger.LogDebug("[ROUTE] {Address} is portal/client → memory stream", addressPath);
            var s = streamProvider.GetStream<IMessageDelivery>(addressPath);
            return s.OnNextAsync(delivery)
                .ContinueWith(_ => delivery.Forwarded(address));
        }

        return pathResolver.ResolvePath(addressPath)
            .SelectMany(resolution =>
            {
                var grainKey = resolution?.Prefix ?? addressPath;

                logger.LogDebug("[ROUTE] {MessageType} → resolved={Prefix} remainder={Remainder} grainKey={GrainKey}",
                    delivery.Message.GetType().Name, resolution?.Prefix ?? "(null)",
                    resolution?.Remainder ?? "(null)", grainKey);

                if (resolution == null || !string.IsNullOrEmpty(resolution.Remainder))
                {
                    var failureMessage = resolution == null
                        ? $"No node found at '{addressPath}'."
                        : $"No node found at '{addressPath}'. Closest ancestor is '{resolution.Prefix}' (remainder='{resolution.Remainder}').";
                    logger.LogWarning("[ROUTE] NotFound: {FailureMessage}", failureMessage);
                    return Observable.Return(delivery.Failed(failureMessage));
                }

                logger.LogDebug("[ROUTE] Delivering {MessageType} to grain {GrainKey}", delivery.Message.GetType().Name, grainKey);
                // Use pre-captured grainFactory so this lambda can run on Scheduler.Default
                // (where the path-resolver observable schedules its emissions) without
                // touching the Grain instance from a non-activation thread.
                var grain = grainFactory.GetGrain<IMessageHubGrain>(grainKey);
                return Observable.FromAsync(() => grain.DeliverMessage(delivery))
                    .Do(result => logger.LogDebug("[ROUTE] Grain {GrainKey} returned state={State} for {MessageType}",
                        grainKey, result.State, delivery.Message.GetType().Name))
                    .Catch<IMessageDelivery, Exception>(ex =>
                    {
                        logger.LogWarning(ex, "[ROUTE] Grain {GrainKey} threw for {MessageType} → stream fallback",
                            grainKey, delivery.Message.GetType().Name);
                        var stream = streamProvider.GetStream<IMessageDelivery>(addressPath);
                        return Observable.FromAsync(() => stream.OnNextAsync(delivery))
                            .Select(_ => delivery.Forwarded(address));
                    });
            })
            .FirstAsync()
            .ToTask();
    }

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
}
