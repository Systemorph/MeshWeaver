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
            try
            {
                return await RouteInMesh(delivery, cancellationToken);
            }
            catch(Exception e)
            {
                Mesh.Post(new DeliveryFailure(delivery)
                {
                    Message = e.Message, ExceptionType = e.GetType().Name, StackTrace = e.StackTrace
                },
                    o => o.ResponseFor(delivery));
                return delivery.Failed(e.Message);
            }
        }

        private async Task<IMessageDelivery> RouteInMesh(IMessageDelivery delivery, CancellationToken cancellationToken)
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
            var hostedHub = Mesh.GetHostedHub(address, true);
            if (hostedHub is not null)
                return hostedHub.DeliverMessage(delivery);


            var hostAddress = GetHostAddress(address);
            var (addressType,addressId) = MessageHubExtensions.GetAddressTypeAndId(hostAddress);

            var node = await meshCatalog.GetNodeAsync(addressType, addressId);

            if (!string.IsNullOrWhiteSpace(node?.StartupScript))
                return await RouteToKernel(delivery, node, addressId, cancellationToken);
            return await RouteMessage(delivery, addressType, addressId, node, cancellationToken);
        }

        protected virtual Task<IMessageDelivery> RouteToKernel(IMessageDelivery delivery, MeshNode node, string addressId, CancellationToken ct)
        {
            var kernelId = GetKernelId(delivery, node, addressId);
            delivery = delivery.WithTarget(new HostedAddress(delivery.Target, new KernelAddress(){Id = kernelId}));
            return RouteInMesh(delivery, ct);
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


        protected abstract Task<IMessageDelivery> RouteMessage(
                IMessageDelivery delivery,
                string addressType,
                string addressId,
                MeshNode node,
                CancellationToken cancellationToken
            );




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
