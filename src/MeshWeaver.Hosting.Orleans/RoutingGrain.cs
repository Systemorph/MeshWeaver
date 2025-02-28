using System.Collections.Concurrent;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Providers;
using Orleans.Streams;

namespace MeshWeaver.Hosting.Orleans;

[StorageProvider(ProviderName = StorageProviders.AddressRegistry)]
public class StreamRegistryGrain : Grain<StreamInfo>, IStreamRegistryGrain
{
    public Task<StreamInfo> Get() => Task.FromResult(State);

    public Task Register(StreamInfo streamInfo)
    {
        State = streamInfo;
        return WriteStateAsync();
    }

    public Task Unregister()
    {
        State = null;
        return ClearStateAsync();
    }
}
[PreferLocalPlacement]
public class RoutingGrain(ILogger<RoutingGrain> logger, IRoutingService routingService) : Grain, IRoutingGrain
{
    public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
    {
        logger.LogInformation("Delivering message from {Sender} to {Target}", delivery.Sender, delivery.Target);
        await routingService.DeliverMessageAsync(delivery);
        return delivery.Forwarded();
    }

    private readonly ConcurrentDictionary<string, IAsyncDisposable> subscriptions = new();

}
