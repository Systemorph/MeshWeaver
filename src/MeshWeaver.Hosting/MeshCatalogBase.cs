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
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new(){SlidingExpiration = TimeSpan.FromMinutes(5)};
    private readonly IMessageHub persistence;
    private readonly ILogger<MeshCatalogBase> logger;

    protected MeshCatalogBase(IMessageHub hub, MeshConfiguration configuration, IUnifiedPathRegistry pathRegistry)
    {
        Configuration = configuration;
        PathRegistry = pathRegistry;
        logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshCatalogBase>>();
        persistence = hub.GetHostedHub(AddressExtensions.CreatePersistenceAddress())!;
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

        // Try to find a template node using score-based matching
        // This handles cases like pricing/Microsoft/2026 where "pricing" is registered with AddressSegments=3
        var templateNode = FindTemplateNode(address);
        if (templateNode != null)
        {
            // Create a new node from the template with the full address
            node = templateNode with
            {
                Key = addressKey
            };
            cache.Set(node.Key, node, cacheOptions);
            return UpdateNode(node);
        }

        // Finally, try loading from persistence
        node = await LoadMeshNode(address);
        if (node is null)
            return null;
        cache.Set(node.Key, node, cacheOptions);
        return UpdateNode(node);
    }

    /// <summary>
    /// Finds a template node that matches the address using score-based matching.
    /// Returns null if no template matches or if the address doesn't require expansion.
    /// </summary>
    private MeshNode? FindTemplateNode(Address address)
    {
        var segments = address.Segments;
        if (segments.Length == 0)
            return null;

        // Find best matching node by score
        var bestMatch = Configuration.Nodes.Values
            .Where(n => n.AddressSegments > n.Segments.Length) // Only consider template nodes
            .Select(node => (Node: node, Score: ScoreMatch(node, segments)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .FirstOrDefault();

        if (bestMatch.Node == null)
            return null;

        // Check if the address has the required number of segments
        if (segments.Length < bestMatch.Node.AddressSegments)
            return null; // Not enough segments for this template

        return bestMatch.Node;
    }

    private MeshNode UpdateNode(MeshNode node)
    {
        cache.Set(node.Key, node, cacheOptions);
        persistence.InvokeAsync(_ => UpdateNodeAsync(node), ex =>
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

        // Determine how many segments belong to the address
        // AddressSegments > 0 means the node expects more segments than its prefix
        // e.g., prefix "pricing" with AddressSegments=3 means address is "pricing/company/year"
        var addressSegmentCount = bestMatch.Node.AddressSegments > 0
            ? bestMatch.Node.AddressSegments
            : bestMatch.Node.Segments.Length;

        // Build the full address: use node's prefix (preserves case) + additional segments from path
        var nodeSegments = bestMatch.Node.Segments;
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
}
