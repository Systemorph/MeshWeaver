using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Mesh catalog implementation that uses IPersistenceService for storage.
/// Configure with AddFileSystemPersistence() for file-backed storage
/// or AddInMemoryPersistence() for transient storage.
/// </summary>
public sealed class MeshCatalog : IMeshCatalog
{
    public MeshConfiguration Configuration { get; }
    public IUnifiedPathRegistry PathRegistry { get; }
    public IPersistenceService Persistence { get; }
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new(){SlidingExpiration = TimeSpan.FromMinutes(5)};
    private readonly IMessageHub persistenceHub;
    private readonly IMessageHub meshHub;
    private readonly ILogger<MeshCatalog> logger;

    // Lazy-loaded because IMeshNodeCompilationService is registered at hub level
    // and may not be available during MeshCatalog construction
    private IMeshNodeCompilationService? _compilationService;
    private bool _compilationServiceResolved;

    private IMeshNodeCompilationService? CompilationService
    {
        get
        {
            if (!_compilationServiceResolved)
            {
                _compilationService = meshHub.ServiceProvider.GetService<IMeshNodeCompilationService>();
                _compilationServiceResolved = true;
                if (_compilationService == null)
                {
                    logger.LogWarning("IMeshNodeCompilationService not available. On-demand node type compilation disabled.");
                }
            }
            return _compilationService;
        }
    }

    public MeshCatalog(
        IMessageHub hub,
        MeshConfiguration configuration,
        IUnifiedPathRegistry pathRegistry,
        IPersistenceService persistenceService)
    {
        Configuration = configuration;
        PathRegistry = pathRegistry;
        Persistence = persistenceService;
        meshHub = hub;
        logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshCatalog>>();

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
        var persistedNode = await Persistence.GetNodeAsync(address.ToString());

        if (persistedNode != null)
        {
            // Look up NodeTypeConfiguration - may already be registered
            var nodeTypeConfig = Configuration.GetNodeTypeConfiguration(persistedNode.NodeType);
            string? assemblyLocation = null;

            // If not found and we have a compilation service, compile on-demand
            if (nodeTypeConfig == null && CompilationService != null && !string.IsNullOrEmpty(persistedNode.NodeType))
            {
                var compilationResult = await CompilationService.CompileAndGetConfigurationsAsync(persistedNode);
                if (compilationResult != null)
                {
                    assemblyLocation = compilationResult.AssemblyLocation;

                    // Register all NodeTypeConfigurations from the compiled assembly
                    foreach (var config in compilationResult.NodeTypeConfigurations)
                    {
                        Configuration.RegisterNodeTypeConfiguration(config);
                        logger.LogDebug("Registered NodeTypeConfiguration for {NodeType}", config.NodeType);
                    }

                    nodeTypeConfig = Configuration.GetNodeTypeConfiguration(persistedNode.NodeType);
                }
            }

            if (nodeTypeConfig != null)
            {
                // Merge node type configuration with persisted data
                // NodeTypeConfig provides: HubConfiguration
                // Persisted provides: Name, Description, Content, IconName, DisplayOrder, etc.
                node = persistedNode with
                {
                    HubConfiguration = nodeTypeConfig.HubConfiguration,
                    AssemblyLocation = assemblyLocation ?? persistedNode.AssemblyLocation
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
        persistenceHub.InvokeAsync(_ => Persistence.SaveNodeAsync(node), ex =>
        {
            logger.LogError(ex, "unable to update mesh catalog");
            return Task.CompletedTask;
        });
        return node;
    }

    public Task UpdateAsync(MeshNode node) =>
        Persistence.SaveNodeAsync(node);

    private readonly Dictionary<string, StreamInfo> channelTypes = new()
    {
        { AddressExtensions.AppType, new(StreamType.Channel, StreamProviders.Hub, ChannelNames.Hub) },
        { AddressExtensions.KernelType, new(StreamType.Channel, StreamProviders.Hub, ChannelNames.Hub) }
    };
    public Task<StreamInfo> GetStreamInfoAsync(Address address)
    {
        return Task.FromResult(channelTypes.GetValueOrDefault(address.Type) ?? new StreamInfo(StreamType.Stream, StreamProviders.Memory, address.ToString()));
    }

    /// <inheritdoc />
    public async Task<AddressResolution?> ResolvePathAsync(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // Normalize path - remove leading slash if present
        path = path.TrimStart('/');
        if (string.IsNullOrEmpty(path))
            return null;

        var segments = path.Split('/');

        // 1. Try configuration first (existing behavior)
        var configMatch = Configuration.Nodes.Values
            .Select(node => (Node: node, Score: ScoreMatch(node, segments)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .FirstOrDefault();

        if (configMatch.Node != null)
        {
            return await ResolveFromConfigNodeAsync(configMatch.Node, segments, path);
        }

        // 2. Try IPersistenceService - walk UP the path hierarchy to find best match
        var (persistenceMatch, matchedSegmentCount) = await FindBestPersistenceMatchAsync(segments);
        if (persistenceMatch != null)
        {
            // Use the node's actual prefix as the address (handles Root namespace mapping)
            var matchedPath = persistenceMatch.Prefix;
            var remainder = matchedSegmentCount < segments.Length
                ? string.Join("/", segments.Skip(matchedSegmentCount))
                : null;
            return new AddressResolution(matchedPath, remainder);
        }

        return null;
    }

    private async Task<(MeshNode? Node, int MatchedSegments)> FindBestPersistenceMatchAsync(string[] segments)
    {
        // Walk from full path down to single segment, finding deepest existing node
        for (int depth = segments.Length; depth >= 1; depth--)
        {
            var testPath = string.Join("/", segments.Take(depth));

            var node = await Persistence.GetNodeAsync(testPath);
            if (node != null)
                return (node, depth);
        }

        return (null, 0);
    }

    private async Task<AddressResolution> ResolveFromConfigNodeAsync(MeshNode matchedNode, string[] segments, string path)
    {
        // For graph-style nodes (where the path IS the address), use all segments as address
        // This is determined by checking if there are NodeTypeConfigurations registered
        // (which means the node supports dynamic children via persistence)
        if (Configuration.NodeTypeConfigurations.Count > 0 &&
            matchedNode.Segments.Length > 0 &&
            segments[0].Equals(matchedNode.Segments[0], StringComparison.OrdinalIgnoreCase))
        {
            // For graph-style nodes, find the deepest existing node in persistence
            // This allows proper remainder handling when path goes beyond existing nodes
            var (persistenceMatch, matchedSegmentCount) = await FindBestPersistenceMatchAsync(segments);
            if (persistenceMatch != null)
            {
                var matchedPath = persistenceMatch.Prefix;
                var persistenceRemainder = matchedSegmentCount < segments.Length
                    ? string.Join("/", segments.Skip(matchedSegmentCount))
                    : null;
                return new AddressResolution(matchedPath, persistenceRemainder);
            }

            // Fallback to using just the config node prefix if nothing in persistence
            var configRemainder = segments.Length > matchedNode.Segments.Length
                ? string.Join("/", segments.Skip(matchedNode.Segments.Length))
                : null;
            return new AddressResolution(matchedNode.Prefix, configRemainder);
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
        var count = 0;
        await foreach (var child in Persistence.GetChildrenAsync(parentPath).WithCancellation(ct))
        {
            // Filter by query if provided
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!(child.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(child.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !child.Prefix.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            yield return child;
            count++;

            // Apply max results if provided
            if (maxResults.HasValue && maxResults.Value > 0 && count >= maxResults.Value)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public IEnumerable<NodeTypeInfo> GetNodeTypes()
    {
        return Configuration.NodeTypeConfigurations.Values
            .Select(config => new NodeTypeInfo(
                config.NodeType,
                config.DisplayName,
                config.Description,
                config.IconName,
                config.DataType.Name,
                config.DisplayOrder))
            .OrderBy(t => t.DisplayOrder)
            .ThenBy(t => t.NodeType);
    }

    /// <inheritdoc />
    public NodeTypeConfiguration? GetNodeTypeConfiguration(string nodeType)
    {
        return Configuration.GetNodeTypeConfiguration(nodeType);
    }
}
