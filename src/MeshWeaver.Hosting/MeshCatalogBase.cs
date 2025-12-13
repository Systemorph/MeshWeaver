using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

public abstract class MeshCatalogBase : IMeshCatalog
{
    public MeshConfiguration Configuration { get; }
    public IUnifiedPathRegistry PathRegistry { get; }
    public IPersistenceService Persistence { get; }
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new(){SlidingExpiration = TimeSpan.FromMinutes(5)};
    private readonly IMessageHub persistenceHub;
    private readonly ILogger<MeshCatalogBase> logger;

    protected MeshCatalogBase(
        IMessageHub hub,
        MeshConfiguration configuration,
        IUnifiedPathRegistry pathRegistry,
        IPersistenceService persistenceService)
    {
        Configuration = configuration;
        PathRegistry = pathRegistry;
        Persistence = persistenceService;
        logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshCatalogBase>>();
        persistenceHub = hub.GetHostedHub(AddressExtensions.CreatePersistenceAddress())!;
        foreach (var node in Configuration.Nodes.Values)
                UpdateNode(node);
    }

    public async Task<MeshNode?> GetNodeAsync(Address address)
    {
        var addressKey = address.ToString();

        // Check cache first
        if (cache.TryGetValue(addressKey, out var ret))
            return (MeshNode?)ret;

        // Try exact match in configuration
        if (Configuration.Nodes.TryGetValue(addressKey, out var node))
        {
            cache.Set(node.Key, node, cacheOptions);
            return UpdateNode(node);
        }

        // Try loading from persistence
        var persistedNode = await LoadMeshNode(address);

        if (persistedNode != null)
        {
            // Look up NodeTypeConfiguration based on the persisted node's NodeType
            var nodeTypeConfig = Configuration.GetNodeTypeConfiguration(persistedNode.NodeType);
            if (nodeTypeConfig != null)
            {
                // Merge node type configuration with persisted data
                // NodeTypeConfig provides: HubConfiguration
                // Persisted provides: Name, Description, Content, IconName, DisplayOrder, etc.
                node = persistedNode with
                {
                    HubConfiguration = nodeTypeConfig.HubConfiguration
                };
            }
            else
            {
                node = persistedNode;
            }

            cache.Set(node.Key, node, cacheOptions);
            return UpdateNode(node);
        }

        return null;
    }

    private MeshNode UpdateNode(MeshNode node)
    {
        cache.Set(node.Key, node, cacheOptions);
        persistenceHub.InvokeAsync(_ => UpdateNodeAsync(node), ex =>
        {
            logger.LogError(ex, "unable to update mesh catalog");
            return Task.CompletedTask;
        });
        return node;
    }

    protected abstract Task<MeshNode?> LoadMeshNode(Address address);


    public abstract Task UpdateAsync(MeshNode node);

    private readonly Dictionary<string, StreamInfo> channelTypes = new()
    {
        { AddressExtensions.AppType, new(StreamType.Channel, StreamProviders.Hub, ChannelNames.Hub) },
        { AddressExtensions.KernelType, new(StreamType.Channel, StreamProviders.Hub, ChannelNames.Hub) }
    };
    public Task<StreamInfo> GetStreamInfoAsync(Address address)
    {
        return Task.FromResult(channelTypes.GetValueOrDefault(address.Type) ?? new StreamInfo(StreamType.Stream, StreamProviders.Memory, address.ToString()));
    }


    protected abstract Task UpdateNodeAsync(MeshNode node);

    /// <inheritdoc />
    public AddressResolution? ResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // Normalize path - remove leading slash if present
        path = path.TrimStart('/');
        if (string.IsNullOrEmpty(path))
            return null;

        var segments = path.Split('/');

        // Find best matching node by score (number of matching segments)
        var bestMatch = Configuration.Nodes.Values
            .Select(node => (Node: node, Score: ScoreMatch(node, segments)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .FirstOrDefault();

        if (bestMatch.Node == null)
            return null;

        var matchedNode = bestMatch.Node;

        // For graph-style nodes (where the path IS the address), use all segments as address
        // This is determined by checking if there are NodeTypeConfigurations registered
        // (which means the node supports dynamic children via persistence)
        if (Configuration.NodeTypeConfigurations.Count > 0 &&
            matchedNode.Segments.Length > 0 &&
            segments[0].Equals(matchedNode.Segments[0], StringComparison.OrdinalIgnoreCase))
        {
            // Use the full path as the address (the path IS the address for graph nodes)
            return new AddressResolution(path, null);
        }

        // Legacy behavior for nodes with AddressSegments
        var addressSegmentCount = matchedNode.AddressSegments > 0
            ? matchedNode.AddressSegments
            : matchedNode.Segments.Length;

        // Build the full address: use node's prefix (preserves case) + additional segments from path
        var nodeSegments = matchedNode.Segments;
        var addressParts = new List<string>(nodeSegments); // Start with node's prefix segments

        // Add additional segments from path (beyond the prefix match)
        for (int i = nodeSegments.Length; i < addressSegmentCount && i < segments.Length; i++)
        {
            addressParts.Add(segments[i]);
        }

        var addressPrefix = string.Join("/", addressParts);

        var remainder = addressSegmentCount < segments.Length
            ? string.Join("/", segments.Skip(addressSegmentCount))
            : null;

        return new AddressResolution(addressPrefix, remainder);
    }

    private static int ScoreMatch(MeshNode node, string[] pathSegments)
    {
        var nodeSegments = node.Segments;

        // Score = number of matching segments from start
        // Must match ALL node segments to count
        if (nodeSegments.Length > pathSegments.Length)
            return 0;

        for (int i = 0; i < nodeSegments.Length; i++)
        {
            if (!nodeSegments[i].Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase))
                return 0;
        }

        return nodeSegments.Length;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MeshNode> QueryAsync(string? parentPath, string? query = null, int? maxResults = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var children = await Persistence.GetChildrenAsync(parentPath, ct);

        // Filter by query if provided
        if (!string.IsNullOrWhiteSpace(query))
        {
            children = children.Where(n =>
                (n.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (n.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                n.Prefix.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        // Apply max results if provided
        if (maxResults.HasValue && maxResults.Value > 0)
        {
            children = children.Take(maxResults.Value);
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            yield return child;
        }
    }
}
