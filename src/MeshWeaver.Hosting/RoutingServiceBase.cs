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
            catch (Exception e)
            {
                Mesh.Post(new DeliveryFailure(delivery)
                {
                    Message = e.Message,
                    ExceptionType = e.GetType().Name,
                    StackTrace = e.StackTrace!
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

            if (delivery.Target.Type == AddressExtensions.MeshType)
            {
                Mesh.DeliverMessage(delivery);
                return delivery.Forwarded(Mesh.Address);
            }

            if (delivery.Target is { Host.Type: AddressExtensions.MeshType })
                delivery = delivery.WithTarget(delivery.Target with { Host = null });

            var address = GetHostAddress(delivery.Target!);

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
            var kernelAddress = AddressExtensions.CreateKernelAddress(kernelId);
            delivery = delivery.WithTarget(delivery.Target!.WithHost(kernelAddress));
            return RouteInMesh(delivery, ct);
        }

        protected virtual string GetKernelId(IMessageDelivery delivery, MeshNode node, Address address)
        {
            return node.RoutingType switch
            {
                RoutingType.Shared => $"{address}".Replace('/', '-'),
                RoutingType.Individual =>
                    $"{address}/{TypeRegistry.GetTypeName(delivery.Sender)}/{delivery.Sender}".Replace('/', '-'),
                _ => throw new NotSupportedException($"The routing type {node.RoutingType} is currently not supported.")
            };
        }

        private async Task<IMessageDelivery> RouteMessageAsync(
            IMessageDelivery delivery,
            Address address,
            CancellationToken cancellationToken
        )
        {
            // Use ResolvePath to find the deepest matching node in persistence
            var resolution = MeshCatalog.ResolvePath(address.ToString());
            if (resolution != null)
            {
                // Route to the resolved prefix address
                address = new Address(resolution.Prefix);

                // If there's a remainder, store it in the delivery context for the hub to use
                if (!string.IsNullOrEmpty(resolution.Remainder))
                {
                    delivery = delivery.WithProperty("UnifiedPath", resolution.Remainder);
                }
            }

            var node = await MeshCatalog.GetNodeAsync(address);

            if (!string.IsNullOrWhiteSpace(node?.StartupScript))
                return await RouteToKernel(delivery, node, address, cancellationToken);



            return await RouteImplAsync(delivery, node, address, cancellationToken);
        }

        protected abstract Task<IMessageDelivery> RouteImplAsync(IMessageDelivery delivery,
            MeshNode? node,
            Address address,
            CancellationToken cancellationToken);


        private Address GetHostAddress(Address address)
        {
            if (address.Host != null)
            {
                var host = GetHostAddress(address.Host);
                if (host.Type == AddressExtensions.MeshType)
                    return address with { Host = null };
                return host;
            }

            return address;
        }


    }
}
