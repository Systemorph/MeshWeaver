using System.Collections.Concurrent;
using MeshWeaver.Disposables;
using MeshWeaver.Domain;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting.Monolith
{
    public class MonolithRoutingService(IMessageHub parent) : IRoutingService
    {
        private readonly ITypeRegistry typeRegistry = parent.ServiceProvider.GetRequiredService<ITypeRegistry>();
        private readonly ConcurrentDictionary<(string Type, string Id), AsyncDelivery> handlers = new();
        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, CancellationToken cancellationToken)
        {
            if (delivery.Target == null)
                return delivery;

            var targetId = SerializationExtensions.GetId(delivery.Target);
            var targetType = typeRegistry.GetTypeName(delivery.Target);
            var key = (targetType, targetId);
            if (handlers.TryGetValue(key, out var handler))
                return await handler.Invoke(delivery, cancellationToken);

            if(targetType == null)
                throw new SignalRException($"Cannot determine the address type of {delivery.Target}");

            var hub = await parent.ServiceProvider.CreateHub(targetType, targetId);
            handlers[key] = handler = (d, _) =>
            {
                hub.DeliverMessage(d); 
                return Task.FromResult(d.Forwarded());
            };
            return await handler.Invoke(delivery, cancellationToken);
        }

        public Task<IDisposable> RegisterRouteAsync(string addressType, string id, AsyncDelivery delivery)
        {
            var key = (addressType, id);
            handlers[key] = delivery;
            return Task.FromResult<IDisposable>(new AnonymousDisposable(() => handlers.TryRemove(key, out _)));
        }
    }
}
