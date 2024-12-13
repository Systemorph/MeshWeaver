using System.Collections.Concurrent;
using MeshWeaver.Disposables;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithRoutingService(IMessageHub meshHub) : RoutingServiceBase(meshHub)
{
    private readonly ConcurrentDictionary<(string Type, string Id), AsyncDelivery> handlers = new();

    protected override async Task<IMessageDelivery> RouteMessage(
        IMessageDelivery delivery,
        MeshNode node,
        string addressId, CancellationToken cancellationToken)
    {
        var key = (node.AddressType, addressId);
        if (handlers.TryGetValue(key, out var handler))
            return await handler.Invoke(delivery, cancellationToken);


        var hub = Mesh.CreateHub(node, addressId);
        if (hub == null)
            return delivery;

        handlers[key] = handler = (d, _) =>
        {
            hub.DeliverMessage(d);
            return Task.FromResult(d.Forwarded());
        };
        return await handler.Invoke(delivery, cancellationToken);
    }
    public override Task<IDisposable> RegisterRouteAsync(string addressType, string id, AsyncDelivery delivery)
    {
        var key = (addressType, id);
        handlers[key] = delivery;
        return Task.FromResult<IDisposable>(new AnonymousDisposable(() => handlers.TryRemove(key, out _)));
    }
}
