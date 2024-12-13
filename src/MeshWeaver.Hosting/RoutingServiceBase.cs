using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting
{
    public abstract class RoutingServiceBase(IMessageHub meshHub) : IRoutingService
    {
        private readonly ITypeRegistry typeRegistry = meshHub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        protected readonly IMessageHub Mesh = meshHub;
        private readonly IMeshCatalog meshCatalog = meshHub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        public async Task<IMessageDelivery> DeliverMessage(IMessageDelivery delivery, CancellationToken cancellationToken)
        {
            if (delivery.Target == null)
                return delivery;

            if (delivery.Target is MeshAddress)
            {
                Mesh.DeliverMessage(delivery);
                return delivery.Forwarded();
            }
            if (delivery.Target is HostedAddress { Host: MeshAddress } hosted)
                delivery = delivery.WithTarget(hosted.Address);

            var address = GetHostAddress(delivery.Target);

            // if we have created the hub ==> route through us.
            var hostedHub = Mesh.GetHostedHub(delivery.Target, true);
            if (hostedHub is not null)
                return hostedHub.DeliverMessage(delivery);


            var hostAddress = GetHostAddress(address);
            var addressId = SerializationExtensions.GetId(hostAddress);
            var addressType = typeRegistry.GetTypeName(hostAddress);

            var node = await meshCatalog.GetNodeAsync(addressType, addressId);
            if (!string.IsNullOrWhiteSpace(node.StartupScript))
                return RouteToKernel(delivery, node, addressId);
            return await RouteMessage(delivery, node, addressId, cancellationToken);
        }

        protected virtual IMessageDelivery RouteToKernel(IMessageDelivery delivery, MeshNode node, string addressId)
        {
            var kernelId = GetKernelId(delivery, node, addressId);
            delivery = delivery.WithTarget(new HostedAddress(delivery.Target, new KernelAddress(){Id = kernelId}));
            return delivery.Forwarded();
        }

        protected virtual string GetKernelId(IMessageDelivery delivery, MeshNode node, string addressId)
        {
            return node.RoutingType switch
            {
                RoutingType.Shared => $"{node.AddressType}/{addressId}",
                RoutingType.Individual =>
                    $"{node.AddressType}/{addressId}/{typeRegistry.GetTypeName(delivery.Sender)}/{delivery.Sender}",
                _ => throw new NotSupportedException($"The routing type {node.RoutingType} is currently not supported.")
            };
        }


        protected abstract Task<IMessageDelivery> RouteMessage(IMessageDelivery delivery, MeshNode node,
            string addressId, CancellationToken cancellationToken);




        private object GetHostAddress(object address)
        {
            if (address is HostedAddress hosted)
            {
                var host = GetHostAddress(hosted.Host);
                if (host is MeshAddress)
                    return hosted.Address;
                return host;
            }

            return address;
        }

        public abstract Task<IDisposable> RegisterRouteAsync(string addressType, string id, AsyncDelivery delivery);

    }
}
