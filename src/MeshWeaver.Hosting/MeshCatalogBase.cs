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
        if (cache.TryGetValue(address.ToString(), out var ret))
            return (MeshNode?)ret;
        var node = Configuration.Nodes.GetValueOrDefault(address.ToString())
               ?? await LoadMeshNode(address);
        if (node is null)
            return null;
        cache.Set(node.Key, node, cacheOptions);
        return UpdateNode(node);
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

        var matchedSegments = bestMatch.Score;
        var remainder = matchedSegments < segments.Length
            ? string.Join("/", segments.Skip(matchedSegments))
            : null;

        return new AddressResolution(bestMatch.Node.Prefix, remainder);
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
