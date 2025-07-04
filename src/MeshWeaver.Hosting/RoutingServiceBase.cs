using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Hosting
{
    public abstract class RoutingServiceBase(IMessageHub hub) : IRoutingService
    {
        protected readonly ITypeRegistry TypeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        protected readonly IMessageHub Mesh = hub;
        protected readonly IMeshCatalog MeshCatalog = hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        public async Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
        {
            try
            {
                return await RouteInMesh(delivery, cancellationToken);
            }
            catch(Exception e)
            {
                Mesh.Post(new DeliveryFailure(delivery)
                {
                    Message = e.Message, ExceptionType = e.GetType().Name, StackTrace = e.StackTrace!
                },
                    o => o.ResponseFor(delivery));
                return delivery.Failed(e.Message);
            }
        }


        public abstract Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback);



        private async Task<IMessageDelivery> RouteInMesh(
            IMessageDelivery delivery, 
            CancellationToken cancellationToken
            )
        {
            if (delivery.Target == null)
                return delivery;

            if (delivery.Target is MeshAddress)
            {
                Mesh.DeliverMessage(delivery);
                return delivery.Forwarded(Mesh.Address);
            }

            if (delivery.Target is HostedAddress { Host: MeshAddress } hosted)
                delivery = delivery.WithTarget(hosted.Address);

            var address = GetHostAddress(delivery.Target);

            // if we have created the hub ==> route through us.
            var hostedHub = Mesh.GetHostedHub(address, HostedHubCreation.Never);
            if (hostedHub is not null)
            {
                hostedHub.DeliverMessage(delivery);
                return delivery.Forwarded(hostedHub.Address);
            }


            var hostAddress = GetHostAddress(address);

            return await RouteMessageAsync(delivery, hostAddress, cancellationToken);
        }

        protected virtual Task<IMessageDelivery> RouteToKernel(IMessageDelivery delivery, MeshNode node, Address address, CancellationToken ct)
        {
            var kernelId = GetKernelId(delivery, node, address);
            delivery = delivery.WithTarget(new HostedAddress(delivery.Target, new KernelAddress(){Id = kernelId}));
            return RouteInMesh(delivery, ct);
        }

        protected virtual string GetKernelId(IMessageDelivery delivery, MeshNode node, Address address)
        {
            return node.RoutingType switch
            {
                RoutingType.Shared => $"{address}",
                RoutingType.Individual =>
                    $"{address}/{TypeRegistry.GetTypeName(delivery.Sender)}/{delivery.Sender}",
                _ => throw new NotSupportedException($"The routing type {node.RoutingType} is currently not supported.")
            };
        }

        private async Task<IMessageDelivery> RouteMessageAsync(
            IMessageDelivery delivery,
            Address address,
            CancellationToken cancellationToken
        )
        {
            var node = await MeshCatalog.GetNodeAsync(address);

            if (!string.IsNullOrWhiteSpace(node?.StartupScript))
                return await RouteToKernel(delivery, node, address,cancellationToken);



            return await RouteImplAsync(delivery, node, address, cancellationToken);
        }

        protected abstract Task<IMessageDelivery> RouteImplAsync(IMessageDelivery delivery,
            MeshNode? node,
            Address address,
            CancellationToken cancellationToken);


        private Address GetHostAddress(Address address)
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


    }
}
