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
    public async Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery)
    {
        var address = GetHostAddress(delivery.Target!);
        var addressPath = address.ToString();

        var resolution = await pathResolver.ResolvePath(addressPath).FirstAsync().ToTask();
        var grainKey = resolution?.Prefix ?? addressPath;

        logger.LogDebug("RouteMessage: {MessageType} â†’ address={Address}, resolved={Prefix}, remainder={Remainder}, grainKey={GrainKey}",
            delivery.Message.GetType().Name, addressPath, resolution?.Prefix ?? "(null)",
            resolution?.Remainder ?? "(null)", grainKey);

        // When resolution splits the path into prefix + remainder, update the delivery
        // to match the resolved grain address. Without this, the grain receives a delivery
        // whose Target doesn't match its hub address â†’ routing loop.
        if (resolution != null && !string.IsNullOrEmpty(resolution.Remainder))
        {
            logger.LogInformation("RouteMessage: updating target for {MessageType}: {Original} â†’ prefix={Prefix}, remainder={Remainder}",
                delivery.Message.GetType().Name, addressPath, resolution.Prefix, resolution.Remainder);
            var resolvedAddress = new Address(resolution.Prefix.Split('/'));
            delivery = delivery.WithProperty("UnifiedPath", resolution.Remainder);
            delivery = delivery.WithTarget(resolvedAddress);
        }

        // Portal/client hubs are not grains â€” deliver via Orleans memory stream.
        // The portal subscribes to this stream in OrleansRoutingService.RegisterStreamAsync.
        if (address.Type == AddressExtensions.PortalType || address.Type == "client")
        {
            logger.LogDebug("RouteMessage: delivering to {Address} via memory stream (not a grain)", addressPath);
            var stream = this.GetStreamProvider(StreamProviders.Memory)
                .GetStream<IMessageDelivery>(addressPath);
            await stream.OnNextAsync(delivery);
            return delivery.Forwarded(address);
        }

        try
        {
            var grain = GrainFactory.GetGrain<IMessageHubGrain>(grainKey);
            return await grain.DeliverMessage(delivery);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grain delivery failed for {MessageType} to {Address} (key={Key}), falling back to stream",
                delivery.Message.GetType().Name, address, grainKey);
            var stream = this.GetStreamProvider(StreamProviders.Memory)
                .GetStream<IMessageDelivery>(addressPath);
            await stream.OnNextAsync(delivery);
            return delivery.Forwarded(address);
        }
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

