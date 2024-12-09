using System.Collections.Concurrent;
using MeshWeaver.Disposables;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith
{
    public class MonolithRoutingService(IMessageHub meshHub) : IRoutingService
    {
        private readonly ITypeRegistry typeRegistry = meshHub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        private readonly ConcurrentDictionary<(string Type, string Id), AsyncDelivery> handlers = new();

        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, CancellationToken cancellationToken)
        {
            if (delivery.Target == null)
                return delivery;

            if (delivery.Target is MeshAddress)
            {
                meshHub.DeliverMessage(delivery);
                return delivery.Forwarded();
            }

            var address = GetRoutedAddress(delivery.Target);

            var targetId = SerializationExtensions.GetId(address);
            var targetType = typeRegistry.GetTypeName(address);
            var key = (targetType, targetId);
            if (handlers.TryGetValue(key, out var handler))
                return await handler.Invoke(delivery, cancellationToken);

            if(targetType == null)
                throw new MeshException($"Cannot determine the address type of {delivery.Target}");

            var hub = await meshHub.CreateHub(targetType, targetId);
            if (hub == null)
                return delivery;

            handlers[key] = handler = (d, _) =>
            {
                hub.DeliverMessage(d);
                return Task.FromResult(d.Forwarded());
            };
            return await handler.Invoke(delivery, cancellationToken);
        }

        private object GetRoutedAddress(object address)
        {
            if (address is HostedAddress hosted)
            {
                var host = GetRoutedAddress(hosted.Host);
                if (host is MeshAddress)
                    return hosted.Address;
                return host;
            }
            return address;
        }
        public Task<IDisposable> RegisterRouteAsync(string addressType, string id, AsyncDelivery delivery)
        {
            var key = (addressType, id);
            handlers[key] = delivery;
            return Task.FromResult<IDisposable>(new AnonymousDisposable(() => handlers.TryRemove(key, out _)));
        }
    }
}
