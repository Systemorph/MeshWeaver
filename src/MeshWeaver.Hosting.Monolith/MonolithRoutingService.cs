using System.Collections.Concurrent;
using System.Reflection;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;

namespace MeshWeaver.Hosting.Monolith
{
    // TODO V10: need to factor out some base class (02.09.2024, Roland Bürgi)
    public class MonolithRoutingService(IMeshCatalog meshCatalog, IMessageHub parentHub) : IRoutingService
    {
        private readonly ConcurrentDictionary<string, IMessageHub> hubs = new(); 
        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
        {
            if(delivery.Target == null)
                return delivery;

            var targetId = SerializationExtensions.GetId(delivery.Target);
            if (hubs.TryGetValue(targetId, out var hub))
                return hub.DeliverMessage(delivery);

            var node = await meshCatalog.GetNodeAsync(targetId);
            if (node == null)
                return delivery;

            var assembly = Assembly.LoadFrom(Path.Combine(node.BasePath, node.AssemblyLocation));
            if (assembly == null)
                throw new InvalidOperationException($"Assembly {node.AssemblyLocation} not found in {node.BasePath}");

            var att = assembly.GetCustomAttributes<MeshNodeAttribute>().FirstOrDefault(x => x.Node.Id == targetId);
            if (att == null)
                throw new InvalidOperationException($"Node {targetId} not found in {node.AssemblyLocation}");
            hub = att.Create(parentHub.ServiceProvider, delivery.Target);
            hubs[targetId] = hub;
            return hub.DeliverMessage(delivery);
        }

        public Task RegisterHubAsync(IMessageHub hub)
        {
            hubs[hub.Address.ToString()!] = hub;
            return Task.CompletedTask;
        }
    }
}
