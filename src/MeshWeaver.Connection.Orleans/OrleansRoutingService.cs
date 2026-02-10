using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Streams;

namespace MeshWeaver.Connection.Orleans;

public class OrleansRoutingService(
    IMeshCatalog meshCatalog,
    IGrainFactory grainFactory,
    IServiceProvider serviceProvider,
    ILogger<OrleansRoutingService> logger) : IRoutingService
{
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();

    public Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken = default)
    {
        var target = delivery.Target;
        if (target == null)
            return Task.FromResult(delivery);

        var address = GetHostAddress(target);

        // 1. Check registered local streams (portals, in-process clients)
        if (streams.TryGetValue(address, out var callback))
            return callback(delivery, cancellationToken);

        // 2. Fire-and-forget routing to avoid deadlocks
        RouteAsync(delivery, address);
        return Task.FromResult(delivery.Forwarded(address));
    }

    private async void RouteAsync(IMessageDelivery delivery, Address address)
    {
        try
        {
            // Resolve address to find the best grain key
            var resolution = await meshCatalog.ResolvePathAsync(address.ToString());
            var grainKey = resolution?.Prefix;

            if (grainKey != null)
            {
                // Known mesh node → delegate to grain
                var grain = grainFactory.GetGrain<IMessageHubGrain>(grainKey);
                await grain.DeliverMessage(delivery);
            }
            else
            {
                // Not in local catalog → try grain with address string directly
                // (client may not have all nodes, but silo grain will activate on demand)
                // If this also doesn't work, fall back to Orleans memory stream
                try
                {
                    var grain = grainFactory.GetGrain<IMessageHubGrain>(address.ToString());
                    await grain.DeliverMessage(delivery);
                }
                catch
                {
                    // Grain not found → route via Orleans memory stream (e.g., for client addresses)
                    var stream = GetStreamProvider(StreamProviders.Memory)
                        .GetStream<IMessageDelivery>(address.ToString());
                    await stream.OnNextAsync(delivery);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deliver to {Address}", address);
        }
    }

    public async Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;

        // Also subscribe to Orleans memory stream so cross-process messages arrive
        var stream = GetStreamProvider(StreamProviders.Memory)
            .GetStream<IMessageDelivery>(address.ToString());
        var subscription = await stream.SubscribeAsync((v, _) =>
            callback.Invoke(v, CancellationToken.None));

        return new AnonymousAsyncDisposable(async () =>
        {
            streams.TryRemove(address, out _);
            await subscription.UnsubscribeAsync();
        });
    }

    private IStreamProvider GetStreamProvider(string streamProvider) =>
        serviceProvider.GetRequiredKeyedService<IStreamProvider>(streamProvider);

    private static Address GetHostAddress(Address address)
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
