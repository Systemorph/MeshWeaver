using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Domain;
using MeshWeaver.Kernel;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting
{
    internal abstract class RoutingServiceBase(IMessageHub hub) : IRoutingService
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
            // Don't route during shutdown - recipients are likely also disposing
            if (Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                return;

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

                // Kernel addresses are always handled by local hosted hubs — never delegate to grains.
                if (address.Type == AddressExtensions.KernelType)
                {
                    hostedHub = Mesh.GetHostedHub(address,
                        config => config.AddKernelSubHubHandlers(),
                        HostedHubCreation.Always);
                    hostedHub?.DeliverMessage(delivery);
                    return;
                }

                var hostAddress = GetHostAddress(address);
                await RouteMessageAsync(delivery, hostAddress, cancellationToken);
            }
            catch (Exception e)
            {
                // Guard: don't post DeliveryFailure for DeliveryFailure messages or during shutdown
                if (delivery.Message is not DeliveryFailure && Mesh.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
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

            // Use ResolvePath to find a node at the requested address.
            // Routing-framework boundary — bridges IPathResolver IObservable to Task.
            var resolution = await MeshCatalog.ResolvePath(address.ToString())
                .FirstAsync().ToTask(cancellationToken);

            // ============================================================================
            // 🚨🚨🚨  NO  FUCKING  FALLBACK  🚨🚨🚨
            // ============================================================================
            // If `resolution.Remainder` is non-empty, the exact requested address has NO
            // hub of its own — only an ancestor exists. DO NOT FALL BACK to that ancestor.
            //
            // A non-empty remainder almost always means the node is broken — no NodeType,
            // an invalid NodeType, or the node simply doesn't exist. Forwarding the
            // delivery to the closest ancestor would let that ancestor's handlers respond
            // (e.g. MeshNodeReference returns the ancestor's OWN MeshNode), and callers
            // would get back the wrong data instead of seeing absence/failure.
            //
            // ⛔️ DO NOT add an "exception" here. DO NOT redirect to the prefix. DO NOT
            // ⛔️ store the remainder as `UnifiedPath`. The mesh must surface the broken
            // ⛔️ node honestly so it can be fixed at its source. Every "small" fallback
            // ⛔️ added here has caused silent data corruption downstream — copy ops that
            // ⛔️ skip writes thinking the target exists, reads that return ancestor data
            // ⛔️ as if it were the requested node, etc.
            //
            // The right response is NotFound. Period.
            // ============================================================================
            if (resolution != null && !string.IsNullOrEmpty(resolution.Remainder))
            {
                var failureMessage = $"No node found at '{originalAddress}'. " +
                    $"Closest ancestor is '{resolution.Prefix}' (remainder='{resolution.Remainder}'). " +
                    $"This usually means the node is missing, has no NodeType, or has an invalid NodeType.";

                var logger = Mesh.ServiceProvider.GetService<ILogger<RoutingServiceBase>>();
                logger?.LogWarning(
                    "RouteMessageAsync: NotFound for {MessageType} → {Address}. {FailureMessage}",
                    delivery.Message.GetType().Name, originalAddress, failureMessage);

                if (delivery.Message is not DeliveryFailure && Mesh.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
                {
                    Mesh.Post(
                        new DeliveryFailure(delivery)
                        {
                            ErrorType = ErrorType.NotFound,
                            Message = failureMessage
                        }, o => o.ResponseFor(delivery));
                }
                return delivery.Failed(failureMessage);
            }

            if (resolution != null)
            {
                // Exact match — route to the resolved address (segment structure preserved).
                address = new Address(resolution.Prefix.Split('/'));
            }

            // Get node — routing-layer call. NOT a hub round-trip (would recurse —
            // routing.MeshCatalog → catalog → routing). Bridges Persistence I/O at
            // the routing-framework boundary; application code uses hub.GetMeshNode.
            // See Doc/Architecture/CqrsAndContentAccess.md.
            var node = await ((MeshCatalog)MeshCatalog).GetNodeForRouting(address)
                .FirstAsync().ToTask(cancellationToken);

            var routeLogger = Mesh.ServiceProvider.GetService<ILogger<RoutingServiceBase>>();
            routeLogger?.LogDebug("RouteMessageAsync: {MessageType} to {Address} (original={OriginalAddress}). Resolution={Resolution}, Node={NodeFound}, NodeType={NodeType}, HubConfig={HasHubConfig}",
                delivery.Message.GetType().Name, address, originalAddress,
                resolution?.Prefix, node != null, node?.NodeType, node?.HubConfiguration != null);

            return await RouteImplAsync(delivery, node, address, cancellationToken);
        }

        protected abstract Task<IMessageDelivery> RouteImplAsync(IMessageDelivery delivery,
            MeshNode? node,
            Address address,
            CancellationToken cancellationToken);


        private Address GetHostAddress(Address address, int depth = 0)
        {
            if (depth > 50)
                throw new InvalidOperationException($"GetHostAddress recursion depth exceeded 50. Address: {address}");
            if (address.Host != null)
            {
                var host = GetHostAddress(address.Host, depth + 1);
                if (host.Type == AddressExtensions.MeshType)
                    return address with { Host = null };
                return host;
            }

            return address;
        }


    }
}
