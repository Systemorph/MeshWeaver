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

        // Portal/client hubs are not grains — deliver via Orleans memory stream directly.
        if (address.Type == AddressExtensions.PortalType || address.Type == "client")
        {
            logger.LogDebug("[ROUTE] {Address} is portal/client → memory stream", addressPath);
            var s = this.GetStreamProvider(StreamProviders.Memory).GetStream<IMessageDelivery>(addressPath);
            return s.OnNextAsync(delivery).ToObservable()
                .Select(_ => delivery.Forwarded(address))
                .FirstAsync().ToTask();
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
                var grain = GrainFactory.GetGrain<IMessageHubGrain>(grainKey);
                return grain.DeliverMessage(delivery).ToObservable()
                    .Do(result => logger.LogDebug("[ROUTE] Grain {GrainKey} returned state={State} for {MessageType}", grainKey, result.State, delivery.Message.GetType().Name))
                    .Catch<IMessageDelivery, Exception>(ex =>
                    {
                        logger.LogWarning(ex, "[ROUTE] Grain {GrainKey} threw for {MessageType} → stream fallback", grainKey, delivery.Message.GetType().Name);
                        var stream = this.GetStreamProvider(StreamProviders.Memory).GetStream<IMessageDelivery>(addressPath);
                        return stream.OnNextAsync(delivery).ToObservable()
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
