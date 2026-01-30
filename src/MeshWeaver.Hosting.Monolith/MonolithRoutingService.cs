using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithRoutingService(IMessageHub hub, ILogger<MonolithRoutingService> logger) : RoutingServiceBase(hub)
{
    private readonly ConcurrentDictionary<Address, AsyncDelivery> streams = new();


    private Task UnregisterStreamAsync(Address address)
    {
        streams.TryRemove(address, out _);
        return Task.FromResult<Address>(null!);
    }


    public override Task<IAsyncDisposable> RegisterStreamAsync(Address address, AsyncDelivery callback)
    {
        streams[address] = callback;
        return Task.FromResult<IAsyncDisposable>(new AnonymousAsyncDisposable(() => { streams.TryRemove(address, out _);
            return Task.CompletedTask;
        }));
    }


    protected override async Task<IMessageDelivery> RouteImplAsync(
        IMessageDelivery delivery,
        MeshNode? node,
        Address address,
        CancellationToken cancellationToken)
    {
        if (streams.TryGetValue(address, out var stream))
            return await stream.Invoke(delivery, cancellationToken);

        var hub = CreateHub(node, address);
        if (hub is null)
        {
            logger.LogWarning("No node found for address {Address}. Node: {NodePath}, NodeType: {NodeType}, HubConfig: {HasHubConfig}",
                address, node?.Path, node?.NodeType, node?.HubConfiguration != null);
            return delivery.Failed($"No node found for address {address}");
        }

        hub.DeliverMessage(delivery);
        return delivery.Forwarded(hub.Address);
    }

    private IMessageHub? CreateHub(MeshNode? node, Address address)
    {
        var hubConfig = node?.HubConfiguration
            ?? GetNodeTypeHubConfiguration(node);

        if (hubConfig is not null)
        {
            var hub = Mesh.GetHostedHub(address, hubConfig);
            if(hub is not null)
            {
                hub.RegisterForDisposal((_, _) => UnregisterStreamAsync(hub.Address));
            }
            return hub;
        }
        return null;
    }

    /// <summary>
    /// Gets the HubConfiguration for a node by looking up its NodeType template.
    /// This is used when the node itself doesn't have HubConfiguration.
    /// </summary>
    private Func<MessageHubConfiguration, MessageHubConfiguration>? GetNodeTypeHubConfiguration(MeshNode? node)
    {
        if (node?.NodeType == null)
            return null;

        // Look up the NodeType template in Configuration.Nodes
        if (MeshCatalog.Configuration.Nodes.TryGetValue(node.NodeType, out var templateNode) &&
            templateNode.HubConfiguration is not null)
        {
            logger.LogDebug("Using NodeType HubConfiguration from {NodeType} for node at {Address}",
                node.NodeType, node.Path);
            return templateNode.HubConfiguration;
        }

        return null;
    }
}
