using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting
{
    public abstract class RoutingServiceBase(IMessageHub hub) : IRoutingService
    {
        protected readonly ITypeRegistry TypeRegistry = hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        protected readonly IMessageHub Mesh = hub;
        protected readonly IMeshCatalog MeshCatalog = hub.ServiceProvider.GetRequiredService<IMeshCatalog>();
        public Task<IMessageDelivery> DeliverMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
        {
            if (delivery.Target == null)
                return Task.FromResult(delivery);

            // Fire-and-forget routing to avoid deadlocks
            RouteInMesh(delivery, cancellationToken);
            return Task.FromResult(delivery.Forwarded(delivery.Target));
        }


        public abstract Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback);



        private async void RouteInMesh(
            IMessageDelivery delivery,
            CancellationToken cancellationToken
            )
        {
            try
            {
                var address = GetHostAddress(delivery.Target!);

                // if we have created the hub ==> route through us.
                var hostedHub = Mesh.GetHostedHub(address, HostedHubCreation.Never);
                if (hostedHub is not null)
                {
                    hostedHub.DeliverMessage(delivery);
                    return;
                }

                var hostAddress = GetHostAddress(address);
                await RouteMessageAsync(delivery, hostAddress, cancellationToken);
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
            }
        }

        protected virtual Task<IMessageDelivery> RouteToKernel(IMessageDelivery delivery, MeshNode node, Address address, CancellationToken ct)
        {
            var kernelId = GetKernelId(delivery, node, address);
            var kernelAddress = AddressExtensions.CreateKernelAddress(kernelId);
            delivery = delivery.WithTarget(delivery.Target!.WithHost(kernelAddress));
            RouteInMesh(delivery, ct);
            return Task.FromResult(delivery.Forwarded(kernelAddress));
        }

        protected virtual string GetKernelId(IMessageDelivery delivery, MeshNode node, Address address)
            => $"{address}".Replace('/', '-');

        private async Task<IMessageDelivery> RouteMessageAsync(
            IMessageDelivery delivery,
            Address address,
            CancellationToken cancellationToken
        )
        {
            var originalAddress = address;

            // Use ResolvePath to find the deepest matching node in persistence
            var resolution = await MeshCatalog.ResolvePathAsync(address.ToString());
            if (resolution != null)
            {
                // Route to the resolved prefix address, preserving segment structure
                address = new Address(resolution.Prefix.Split('/'));

                // If there's a remainder, store it in the delivery context for the hub to use
                if (!string.IsNullOrEmpty(resolution.Remainder))
                {
                    delivery = delivery.WithProperty("UnifiedPath", resolution.Remainder);
                }
            }

            // Get node - HubConfiguration is now an IObservable so no deadlock
            var node = await MeshCatalog.GetNodeAsync(address);

            // If the node is a template and we have a remainder, try to get/create a virtual node at the original address
            // This handles auto-kernel addresses like kernel/app-Kernel that need their own hub
            if (node != null && resolution?.Remainder != null && !originalAddress.Equals(address))
            {
                var virtualNode = await MeshCatalog.GetNodeAsync(originalAddress);
                if (virtualNode != null && virtualNode.IsVirtual)
                {
                    // Use the virtual node and original address instead
                    node = virtualNode;
                    address = originalAddress;
                    // Clear the remainder since we're using the full address
                    delivery = delivery.WithProperty("UnifiedPath", null);
                }
            }

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
