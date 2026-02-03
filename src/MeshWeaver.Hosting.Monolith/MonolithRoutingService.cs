using System.Collections.Concurrent;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Monolith;

public class MonolithRoutingService(IMessageHub hub, ILogger<MonolithRoutingService> logger) : RoutingServiceBase(hub)
{
    private readonly INodeTypeService? nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
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
        // Use INodeTypeService if available - it properly combines DefaultNodeHubConfiguration
        // with node type's specific configuration
        var hubConfig = GetHubConfiguration(node);

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
    /// Gets the HubConfiguration for a node, properly combining:
    /// 1. Node's own HubConfiguration (if set)
    /// 2. NodeType's HubConfiguration (from template)
    /// 3. DefaultNodeHubConfiguration (from MeshBuilder.ConfigureDefaultNodeHub)
    /// </summary>
    private Func<MessageHubConfiguration, MessageHubConfiguration>? GetHubConfiguration(MeshNode? node)
    {
        // Get the default config first
        var defaultConfig = MeshCatalog.Configuration.DefaultNodeHubConfiguration;

        logger.LogDebug("GetHubConfiguration for node {NodePath}, NodeType: {NodeType}, HasNodeHubConfig: {HasNodeHubConfig}, HasDefaultConfig: {HasDefaultConfig}, NodeTypeService: {HasNodeTypeService}",
            node?.Path, node?.NodeType, node?.HubConfiguration != null, defaultConfig != null, nodeTypeService != null);

        // If node has its own HubConfiguration, combine with default
        if (node?.HubConfiguration != null)
        {
            logger.LogDebug("Using node's own HubConfiguration for {NodePath}", node.Path);
            if (defaultConfig != null)
            {
                var nodeConfig = node.HubConfiguration;
                return config => nodeConfig(defaultConfig(config));
            }
            return node.HubConfiguration;
        }

        // Use INodeTypeService which properly combines default + node type configs
        if (nodeTypeService != null && node?.NodeType != null)
        {
            var cachedConfig = nodeTypeService.GetCachedHubConfiguration(node.NodeType);
            logger.LogDebug("GetCachedHubConfiguration({NodeType}) returned: {HasConfig}", node.NodeType, cachedConfig != null);
            if (cachedConfig != null)
            {
                logger.LogDebug("Using cached HubConfiguration from INodeTypeService for {NodeType} at {Address}",
                    node.NodeType, node.Path);
                return cachedConfig;
            }
        }

        // Fallback: look up the NodeType template in Configuration.Nodes
        if (node?.NodeType != null &&
            MeshCatalog.Configuration.Nodes.TryGetValue(node.NodeType, out var templateNode) &&
            templateNode.HubConfiguration is not null)
        {
            logger.LogDebug("Using NodeType HubConfiguration from template for {NodeType} at {Address}",
                node.NodeType, node.Path);

            // Combine with default config
            if (defaultConfig != null)
            {
                var templateConfig = templateNode.HubConfiguration;
                return config => templateConfig(defaultConfig(config));
            }
            return templateNode.HubConfiguration;
        }

        logger.LogDebug("Returning defaultConfig only for {NodePath}: {HasDefaultConfig}", node?.Path, defaultConfig != null);
        // No node-specific config - return just the default config
        return defaultConfig;
    }
}
