using System.Collections.Concurrent;
using System.Reflection;
using MeshWeaver.Disposables;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Monolith
{
    public class MonolithRoutingService(IMeshCatalog meshCatalog, IMessageHub parentHub) : IRoutingService
    {
        private readonly ConcurrentDictionary<(string Type, string Id), AsyncDelivery> handlers = new();
        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, CancellationToken cancellationToken)
        {
            if (delivery.Target == null)
                return delivery;

            var targetId = SerializationExtensions.GetId(delivery.Target);
            var targetType = SerializationExtensions.GetTypeName(delivery.Target);
            var key = (targetType, targetId);
            if (handlers.TryGetValue(key, out var handler))
                return await handler.Invoke(delivery, cancellationToken);

            var node = await meshCatalog.GetNodeAsync(targetId);
            if (node == null)
                return delivery;

            var assembly = Assembly.LoadFrom(Path.Combine(node.BasePath, node.AssemblyLocation));
            if (assembly == null)
                throw new InvalidOperationException($"Assembly {node.AssemblyLocation} not found in {node.BasePath}");

            var att = assembly.GetCustomAttributes<MeshNodeAttribute>().FirstOrDefault(x => x.Node.Id == targetId);
            if (att == null)
                throw new InvalidOperationException($"Node {targetId} not found in {node.AssemblyLocation}");
            var hub = att.Create(parentHub.ServiceProvider, delivery.Target);
            handlers[key] = handler = (d, _) => { hub.DeliverMessage(d); return Task.FromResult(d.Forwarded()); };
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
