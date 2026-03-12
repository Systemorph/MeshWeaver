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


        var resolution = await pathResolver.ResolvePathAsync(address.ToString());
        var grainKey = resolution?.Prefix;

        if (grainKey != null)
        {
            var grain = GrainFactory.GetGrain<IMessageHubGrain>(grainKey);
            return await grain.DeliverMessage(delivery);
        }

        try
        {
            var grain = GrainFactory.GetGrain<IMessageHubGrain>(address.ToString());
            return await grain.DeliverMessage(delivery);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Grain delivery failed for {Address}, falling back to stream", address);
            var stream = this.GetStreamProvider(StreamProviders.Memory)
                .GetStream<IMessageDelivery>(address.ToString());
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
