using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithRoutingService(IMessageHub hub, ILogger<MonolithRoutingService> logger) : RoutingServiceBase(hub)
{
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();


    private Task UnregisterStreamAsync(Address address)
    {
        streams.TryRemove(address, out _);
        return Task.FromResult<Address>(null!);
    }


    public override Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;
        return Task.FromResult<IAsyncDisposable>(new AnonymousAsyncDisposable(() => { streams.TryRemove(address, out _);
            return Task.CompletedTask;
        }));
    }


    protected override async Task<IMessageDelivery> RouteImplAsync(
        IMessageDelivery delivery, 
        MeshNode? node, 
        Address address,
        CancellationToken cancellationToken)
    {
        if (streams.TryGetValue(address, out var stream))
            return await stream.Invoke(delivery, cancellationToken);

        if (node == null)
        {
            logger.LogWarning("No node found for address {Address}", address);
            return delivery.Failed($"No node found for address {address}");
        }

        var hub = CreateHub(node, address);
        if (hub is null)
            return delivery;

        hub.DeliverMessage(delivery); 
        return delivery.Forwarded(hub.Address);
    }

    private IMessageHub CreateHub(MeshNode node, Address address)
    {
        if (node.HubConfiguration is not null)
        {
            var hub = Mesh.GetHostedHub(address, node.HubConfiguration);
            if(hub is not null)
                hub.RegisterForDisposal((_, _) => UnregisterStreamAsync(hub.Address));
            return hub!;
        }
        return null!;
    }
}
