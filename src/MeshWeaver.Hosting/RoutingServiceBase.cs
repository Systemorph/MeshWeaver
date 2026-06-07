using System.Reactive.Linq;
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
        protected readonly IPathResolver PathResolver = hub.ServiceProvider.GetRequiredService<IPathResolver>();

        public IObservable<IMessageDelivery> DeliverMessage(IMessageDelivery delivery)
        {
            return Observable.Defer(() =>
            {
                if (delivery.Target == null)
                    return Observable.Return(delivery);

                // Fire-and-forget background routing — emit Forwarded immediately
                // so the caller's hub action block isn't held waiting for the
                // actual mesh dispatch (which can hop hubs / silos / persistence).
                RouteInMesh(delivery);
                return Observable.Return(delivery.Forwarded(delivery.Target));
            });
        }


        public abstract IDisposable RegisterStream(Address address, AsyncDelivery callback);



        private void RouteInMesh(IMessageDelivery delivery)
        {
            // Don't route during shutdown - recipients are likely also disposing
            if (Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                return;

            var address = GetHostAddress(delivery.Target!);

            // if we have created the hub ==> route through us.
            var hostedHub = Mesh.GetHostedHub(address, HostedHubCreation.Never);
            if (hostedHub is not null)
            {
                hostedHub.DeliverMessage(delivery);
                return;
            }

            var hostAddress = GetHostAddress(address);
            RouteMessage(delivery, hostAddress).Subscribe(
                _ => { },
                ex =>
                {
                    if (delivery.Message is DeliveryFailure || Mesh.RunLevel >= MessageHubRunLevel.DisposeHostedHubs)
                        return;
                    Mesh.Post(new DeliveryFailure(delivery)
                    {
                        Message = ex.Message,
                        ExceptionType = ex.GetType().Name,
                        StackTrace = ex.StackTrace!
                    },
                        o => o.ResponseFor(delivery));
                });
        }

        private IObservable<IMessageDelivery> RouteMessage(
            IMessageDelivery delivery,
            Address address
        )
        {
            var originalAddress = address;
            var entryLogger = Mesh.ServiceProvider.GetService<ILogger<RoutingServiceBase>>();
            entryLogger?.LogDebug("[ROUTE] enter {MessageType} → {Address}", delivery.Message.GetType().Name, address);

            // 100% reactive composition. ResolvePath → GetNodeForRouting → RouteImpl
            // compose via SelectMany. Per Doc/Architecture/AsynchronousCalls.md.
            return PathResolver.ResolvePath(address.ToString())
                .Take(1)
                .SelectMany(resolution =>
                {
                    entryLogger?.LogDebug("[ROUTE] resolved {Address} → prefix={Prefix} remainder={Remainder}",
                        address, resolution?.Prefix, resolution?.Remainder);

                    // ============================================================================
                    // 🚨🚨🚨  NO  FALLBACK  🚨🚨🚨
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
                    if (resolution == null || !string.IsNullOrEmpty(resolution.Remainder))
                        return Observable.Return(PostNotFound(delivery, originalAddress, resolution));

                    var resolved = new Address(resolution.Prefix.Split('/'));

                    // The matched MeshNode is carried on AddressResolution.Node — same
                    // cached observable PathResolver populated. NO second query: a
                    // separate path:X round-trip used to fire here, which doubled
                    // routing latency and duplicated the cache key set. The cache is
                    // the single source of truth.
                    var node = resolution.Node;
                    var routeLogger = Mesh.ServiceProvider.GetService<ILogger<RoutingServiceBase>>();
                    routeLogger?.LogDebug("RouteMessage: {MessageType} to {Address} (original={OriginalAddress}). Resolution={Resolution}, Node={NodeFound}, NodeType={NodeType}, HubConfig={HasHubConfig}",
                        delivery.Message.GetType().Name, resolved, originalAddress,
                        resolution.Prefix, node != null, node?.NodeType, node?.HubConfiguration != null);
                    return RouteImpl(delivery, node, resolved);
                });
        }

        private IMessageDelivery PostNotFound(IMessageDelivery delivery, Address originalAddress, AddressResolution? resolution)
        {
            var failureMessage = resolution == null
                ? $"No node found at '{originalAddress}'."
                : $"No node found at '{originalAddress}'. " +
                  $"Closest ancestor is '{resolution.Prefix}' (remainder='{resolution.Remainder}'). " +
                  $"This usually means the node is missing, has no NodeType, or has an invalid NodeType.";

            var logger = Mesh.ServiceProvider.GetService<ILogger<RoutingServiceBase>>();
            logger?.LogWarning(
                "RouteMessage: NotFound for {MessageType} → {Address}. {FailureMessage}",
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

        /// <summary>
        /// Reactive subclass hook. Returns an <see cref="IObservable{T}"/> that
        /// emits the delivery's terminal state (Forwarded / Failed). 100%
        /// reactive — no <c>await</c>, no inner <c>.ToTask()</c>.
        /// Per Doc/Architecture/AsynchronousCalls.md.
        /// </summary>
        protected abstract IObservable<IMessageDelivery> RouteImpl(IMessageDelivery delivery,
            MeshNode? node,
            Address address);


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
