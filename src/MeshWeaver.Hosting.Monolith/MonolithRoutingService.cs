using System.Collections.Concurrent;
using MeshWeaver.Disposables;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithRoutingService(IMessageHub hub) : RoutingServiceBase(hub)
{
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();


    protected override Task UnsubscribeAsync(Address address)
    {
        streams.TryRemove(address, out _);
        return Task.FromResult<Address>(null);
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
        MeshNode node, 
        Address address,
        CancellationToken cancellationToken)
    {
        if (streams.TryGetValue(address, out var stream))
            return await stream.Invoke(delivery, cancellationToken);

        if (node == null)
            throw new MeshException($"No Mesh node was found for {address}");

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
            hub.RegisterForDisposal((_, _) => UnsubscribeAsync(hub.Address));
            return hub;
        }
        return null;
    }
}
