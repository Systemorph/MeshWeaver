using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.Loader;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

internal class MonolithRoutingService(
    IMessageHub hub,
    ILogger<MonolithRoutingService> logger) : RoutingServiceBase(hub)
{
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();


    private void UnregisterStream(Address address)
    {
        streams.TryRemove(address, out _);
    }


    public override IAsyncDisposable RegisterStream(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;
        return new AnonymousAsyncDisposable(() =>
        {
            streams.TryRemove(address, out _);
            return Task.CompletedTask;
        });
    }


    protected override IObservable<IMessageDelivery> RouteImpl(
        IMessageDelivery delivery,
        MeshNode? node,
        Address address)
    {
        logger.LogDebug("[ROUTE-IMPL] enter {MessageType} → {Address} (node={NodePath} streamFound={StreamFound})",
            delivery.Message.GetType().Name, address, node?.Path ?? "(null)", streams.ContainsKey(address));

        if (streams.TryGetValue(address, out var stream))
        {
            logger.LogDebug("[ROUTE-IMPL] delivering via existing stream for {Address}", address);
            // Bridge the AsyncDelivery callback (Task-shaped) into the chain via FromAsync —
            // single-shot, no leak. The base RouteMessageAsync re-bridges to Task at the
            // framework boundary.
            return Observable.FromAsync(ct => stream.Invoke(delivery, ct));
        }

        // 100% reactive: CreateHub returns IObservable<IMessageHub?>. Compose
        // delivery on the same chain inside Select. No await, no inner ToTask
        // — the only Task bridge is at the framework boundary in the base
        // class's RouteMessageAsync. Per Doc/Architecture/AsynchronousCalls.md:
        // "no async, 100% reactive. async deadlocks".
        return CreateHub(node, address)
            .Select(hub =>
            {
                if (hub is null)
                    return PostNotFound(delivery, node, address);

                logger.LogDebug("[ROUTE-IMPL] delivering {MessageType} to created hub {HubAddr}",
                    delivery.Message.GetType().Name, hub.Address);
                hub.DeliverMessage(delivery);
                return delivery.Forwarded(hub.Address);
            });
    }

    private IMessageDelivery PostNotFound(IMessageDelivery delivery, MeshNode? node, Address address)
    {
        var isShuttingDown = Mesh.Disposal is not null;
        string errorMessage;
        if (isShuttingDown)
            errorMessage = $"Mesh is shutting down, cannot route to {address}";
        else if (node is null)
            errorMessage = $"No node found for address {address}";
        else
            errorMessage = $"No hub configuration for node '{node.Path}' (NodeType: {node.NodeType ?? "null"}). Ensure the node type is registered via AddGraph() or a custom builder extension.";

        logger.LogWarning("No route found for {MessageType} → {Address}. Node: {NodePath}, NodeType: {NodeType}, Sender: {Sender}, ShuttingDown: {ShuttingDown}",
            delivery.Message.GetType().Name, address, node?.Path, node?.NodeType, delivery.Sender, isShuttingDown);

        if (delivery.Message is not DeliveryFailure && Mesh.RunLevel < MessageHubRunLevel.DisposeHostedHubs)
        {
            Mesh.Post(
                new DeliveryFailure(delivery)
                {
                    ErrorType = isShuttingDown ? ErrorType.Failed : ErrorType.NotFound,
                    Message = errorMessage
                }, o => o.ResponseFor(delivery)
            );
        }
        return delivery.Failed(errorMessage);
    }

    /// <summary>
    /// Returns an <see cref="IObservable{T}"/> that emits the per-node hub
    /// once <c>ResolveHubConfiguration</c> settles. 100% reactive composition —
    /// no <c>await</c>, no inner <c>.ToTask()</c>. The caller bridges to <see cref="Task"/>
    /// once at the framework boundary (<see cref="RouteImplAsync"/>).
    /// </summary>
    private IObservable<IMessageHub?> CreateHub(MeshNode? node, Address address)
    {
        if (Mesh.Disposal is not null || node is null)
            return Observable.Return<IMessageHub?>(null);

        var hubFactory = Mesh.ServiceProvider.GetRequiredService<IMeshNodeHubFactory>();

        logger.LogDebug("[ROUTE-CREATE] CreateHub entering for {Address} (NodeType={NodeType})",
            address, node.NodeType);

        return hubFactory.ResolveHubConfiguration(node)
            .Select(enriched =>
            {
                logger.LogDebug("[ROUTE-CREATE] ResolveHubConfiguration returned for {Address}: HubConfig={HasHubConfig}",
                    address, enriched.HubConfiguration is not null);

                if (enriched.HubConfiguration is null)
                    throw new InvalidOperationException(
                        $"No hub configuration for node '{enriched.Path}' (NodeType: {enriched.NodeType}). " +
                        "Ensure the node type is registered via AddGraph() or has a HubConfiguration set.");

                // No explicit ALC scope. The dynamic assembly is already loaded
                // into a per-release AssemblyLoadContext by
                // CompilationCacheService.GetOrCreateLoadContextForPath during
                // enrichment, and the HubConfiguration delegate's closure
                // captured types from that ALC — they resolve correctly when
                // the delegate is invoked. EnterContextualReflection mattered
                // for string-based Type.GetType lookups, which the canonical
                // DI / lambda composition patterns don't use.
                IDisposable? alcScope = null;

                try
                {
                    // Pass the resolved node back into the hub config as the
                    // routing-supplied own-node observable so MeshDataSource can
                    // seed the workspace from it without issuing a duplicate
                    // persistence read on init. One-shot is fine on Monolith —
                    // cross-hub MeshNode updates flow through IDataChangeNotifier
                    // / IMeshChangeFeed already.
                    var ownStream = Observable.Return(enriched);
                    var createdHub = Mesh.GetHostedHub(address, c =>
                        enriched.HubConfiguration!(c.WithOwnNodeStream(ownStream)));
                    logger.LogDebug("[ROUTE-CREATE] GetHostedHub returned {HubAddr} for {Address}",
                        createdHub?.Address.ToString() ?? "(null)", address);

                    createdHub?.RegisterForDisposal(_ => UnregisterStream(createdHub.Address));
                    return createdHub;
                }
                finally
                {
                    alcScope?.Dispose();
                }
            });
    }
}
