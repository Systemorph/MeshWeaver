using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.Caching.Memory;
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
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    public async Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken = default)
    {
        var target = delivery.Target;
        var streamInfo = await GetStreamInfoAsync(target);

        switch (streamInfo?.Type)
        {
            case StreamType.Channel:
                var grain = grainFactory.GetGrain<IMessageHubGrain>(target.ToString());
                await grain.DeliverMessage(delivery);
                return delivery.Forwarded();
            case StreamType.Stream:
                var stream = GetStreamProvider(streamInfo.Provider)
                    .GetStream<IMessageDelivery>(streamInfo.Namespace);
                await stream.OnNextAsync(delivery);
                return delivery.Forwarded();

            default:
                logger.LogError("No stream info found for {Delivery}", delivery);
                return delivery.Failed($"No route found for {delivery.Target}");
        }

    }

    private async Task<StreamInfo> GetStreamInfoAsync(Address target)
    {
        var streamInfo = cache.TryGetValue(target, out var cached)
            ? cached as StreamInfo
            : await GetStreamInfoFromRoutingGrainAsync(target);
        return streamInfo;
    }

    private async Task<StreamInfo> GetStreamInfoFromRoutingGrainAsync(Address target)
    {
        var ret = await meshCatalog
            .GetStreamInfoAsync(target.ToString());
        cache.Set(target, ret);
        return ret;
    }

    private IStreamProvider GetStreamProvider(string streamProvider) =>
        serviceProvider.GetRequiredKeyedService<IStreamProvider>(streamProvider);

    public async Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
    {
        var streamInfo = await GetStreamInfoAsync(address);
        if (streamInfo is null)
            return null;

        var stream = serviceProvider.GetRequiredKeyedService<IStreamProvider>(streamInfo.Provider)
            .GetStream<IMessageDelivery>(address.ToString());
        var subscription = await stream.SubscribeAsync((v, _) => 
            callback.Invoke(v, CancellationToken.None));
        return new AnonymousAsyncDisposable(async () =>
        {
            await subscription.UnsubscribeAsync();
        });
    }

}
