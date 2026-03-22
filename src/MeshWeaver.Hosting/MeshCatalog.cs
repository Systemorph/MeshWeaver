using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Connection.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Test")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.PostgreSql.Test")]

namespace MeshWeaver.Hosting;

/// <summary>
/// Mesh catalog implementation that uses IMeshStorage for storage.
/// Configure with AddFileSystemPersistence() for file-backed storage
/// or AddInMemoryPersistence() for transient storage.
/// </summary>
internal sealed class MeshCatalog(
    IMessageHub hub,
    MeshConfiguration configuration,
    IMeshStorage persistenceService,
    IEnumerable<IMeshQueryProvider> queryProviders,
    IEnumerable<IStaticNodeProvider> staticNodeProviders,
    ILogger<MeshCatalog>? logger = null)
    : IMeshCatalog
{
    public MeshConfiguration Configuration { get; } = configuration;
    internal IMeshStorage Persistence { get; } = persistenceService;
    internal Address MeshAddress => hub.Address;
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new() { SlidingExpiration = TimeSpan.FromMinutes(15) };
    private readonly Lazy<IMessageHub> _persistenceHub = new(() => hub.GetHostedHub(AddressExtensions.CreatePersistenceAddress())!);
    private IMessageHub PersistenceHub => _persistenceHub.Value;
    private readonly MeshQuery meshQuery = new(queryProviders, hub);
    private readonly Lazy<INodeConfigurationResolver?> _configResolver = new(() => hub.ServiceProvider.GetService<INodeConfigurationResolver>());
    private INodeConfigurationResolver? ConfigResolver => _configResolver.Value;

    public async Task<MeshNode?> GetNodeAsync(Address address)
    {
        var addressKey = address.ToString();

        // Check cache first
        if (cache.TryGetValue(addressKey, out var ret))
        {
            return (MeshNode?)ret;
        }

        // Try exact match in configuration
        if (Configuration.Nodes.TryGetValue(addressKey, out var node))
        {
            if (node.HubConfiguration == null && ConfigResolver != null)
            {
                node = await ConfigResolver.ResolveConfigurationAsync(node);
            }
            cache.Set(node.Path, node, cacheOptions);
            return node;
        }

        // Try loading from persistence
        var persistenceNode = await Persistence.GetNodeAsync(address.ToString());

        // Fallback to static node providers (e.g., DocumentationNodeProvider, BuiltInAgentProvider)
        persistenceNode ??= staticNodeProviders
            .SelectMany(p => p.GetStaticNodes())
            .FirstOrDefault(n => string.Equals(n.Path, address.ToString(), StringComparison.OrdinalIgnoreCase));

        if (persistenceNode != null)
        {
            // Enrich with HubConfiguration based on NodeType (NOT the address - that would cause circular dependency)
            // EnrichWithNodeTypeAsync looks up HubConfiguration from compiled NodeType configs, triggering compilation if needed
            if (ConfigResolver != null)
            {
                persistenceNode = await ConfigResolver.ResolveConfigurationAsync(persistenceNode);
            }

            cache.Set(persistenceNode.Path, persistenceNode, cacheOptions);
            return persistenceNode;
        }

        return null;
    }

    public Task UpdateAsync(MeshNode node) =>
        Persistence.SaveNodeAsync(node);

    // IMeshCatalog — delegate to HubNodePersistence
    private HubNodePersistence NodePersistence => new(hub, this);

    public Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
        => NodePersistence.CreateNodeAsync(node, ct);

    public Task<MeshNode> CreateTransientAsync(MeshNode node, CancellationToken ct = default)
        => NodePersistence.CreateTransientAsync(node, ct);


    /// <summary>
    /// Creates a new node in Transient state without confirming it.
    /// This is internal - used by handlers that need direct node creation after validation.
    /// </summary>
    internal async Task<MeshNode> CreateTransientNodeAsync(MeshNode node, CancellationToken ct = default)
    {
        // 1. Check if node already exists in cache
        if (cache.TryGetValue(node.Path, out var cachedValue) && cachedValue is MeshNode cachedNode)
        {
            throw new InvalidOperationException($"Node already exists at path: {node.Path}");
        }

        // 2. Check if node exists in persistence
        if (await Persistence.ExistsAsync(node.Path, ct))
        {
            throw new InvalidOperationException($"Node already exists at path: {node.Path}");
        }

        // 3. Validate NodeType exists (if specified) - check in MeshNodes or persistence
        if (!string.IsNullOrEmpty(node.NodeType))
        {
            // NodeType is valid if it exists in Configuration.Nodes or in persistence
            var nodeTypeExists = Configuration.Nodes.ContainsKey(node.NodeType)
                || await Persistence.ExistsAsync(node.NodeType, ct);
            if (!nodeTypeExists)
            {
                throw new InvalidOperationException($"NodeType '{node.NodeType}' is not registered");
            }
        }

        // 4. Validate path structure
        if (!ValidatePath(node))
        {
            throw new InvalidOperationException($"Invalid path structure for node: {node.Path}");
        }

        // 5. Create node with Transient state
        var transientNode = node with { State = MeshNodeState.Transient };

        // 5a. Auto-set MainNode for satellite types: point to parent node, not self
        if (!string.IsNullOrEmpty(transientNode.NodeType)
            && !string.IsNullOrEmpty(transientNode.Namespace)
            && Configuration.IsSatelliteNodeType(transientNode.NodeType)
            && transientNode.MainNode == transientNode.Path)
        {
            transientNode = transientNode with { MainNode = transientNode.Namespace };
        }

        // 6. Enrich with HubConfiguration based on NodeType
        if (ConfigResolver != null)
        {
            transientNode = await ConfigResolver.ResolveConfigurationAsync(transientNode, ct);
        }

        // 7. Save to persistence
        var savedNode = await Persistence.SaveNodeAsync(transientNode, ct);

        // 8. Update cache with enriched transient node
        cache.Set(savedNode.Path, savedNode, cacheOptions);

        logger.LogInformation("Created transient node at path {Path}", savedNode.Path);

        return savedNode;
    }

    /// <summary>
    /// Confirms a transient node, updating its state to Active.
    /// This is internal - used by handlers after validation.
    /// </summary>
    internal async Task<MeshNode> ConfirmNodeAsync(string path, CancellationToken ct = default)
    {
        // Get the current node
        var node = await Persistence.GetNodeAsync(path, ct);
        if (node == null)
        {
            throw new InvalidOperationException($"Node not found at path: {path}");
        }

        if (node.State != MeshNodeState.Transient)
        {
            throw new InvalidOperationException($"Node at path '{path}' is not in Transient state (current state: {node.State})");
        }

        // Update to Confirmed state
        var confirmedNode = node with { State = MeshNodeState.Active };
        await Persistence.SaveNodeAsync(confirmedNode, ct);

        // Enrich with HubConfiguration based on NodeType (same as cold start in GetNodeAsync)
        if (ConfigResolver != null)
        {
            confirmedNode = await ConfigResolver.ResolveConfigurationAsync(confirmedNode, ct);
        }

        // Update cache with enriched node
        cache.Set(confirmedNode.Path, confirmedNode, cacheOptions);

        logger.LogInformation("Confirmed node at path {Path}", confirmedNode.Path);

        return confirmedNode;
    }

    /// <summary>
    /// IMeshCatalog.DeleteNodeAsync — internal, called by the DeleteNodeRequest handler.
    /// Deletes directly from persistence and cache.
    /// </summary>
    async Task IMeshCatalog.DeleteNodeAsync(string path, bool recursive, CancellationToken ct)
    {
        if (recursive)
        {
            await foreach (var descendant in Persistence.GetDescendantsAsync(path).WithCancellation(ct))
                cache.Remove(descendant.Path);
        }
        cache.Remove(path);
        await Persistence.DeleteNodeAsync(path, recursive, ct);
        logger.LogInformation("Deleted node at path {Path}, recursive: {Recursive}", path, recursive);
    }

    private static bool ValidatePath(MeshNode node)
    {
        // Path validation rules:
        // 1. Id cannot be empty
        if (string.IsNullOrWhiteSpace(node.Id))
            return false;

        // 2. Id cannot contain invalid characters
        if (node.Id.Contains("..") || node.Id.StartsWith('/') || node.Id.EndsWith('/'))
            return false;

        // 3. Namespace segments should be valid
        if (!string.IsNullOrEmpty(node.Namespace))
        {
            var segments = node.Namespace.Split('/');
            foreach (var segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment))
                    return false;
            }
        }

        return true;
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
        // Skip satellite types (e.g., Portal) — they are local-only ephemeral hubs
        // and should never be routing targets for grain activation.
        var configMatch = Configuration.Nodes.Values
            .Where(node => !node.IsSatelliteType)
            .Select(node => (Node: node, Score: ScoreMatch(node, segments)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .FirstOrDefault();

        if (configMatch.Node != null)
        {
            return await ResolveFromConfigNodeAsync(configMatch.Node, segments, path);
        }

        // 2. Try IMeshStorage - walk UP the path hierarchy to find best match
        var (persistenceMatch, matchedSegmentCount) = await FindBestPersistenceMatchAsync(segments);
        if (persistenceMatch != null)
        {
            // Use the node's actual Path as the address
            var matchedPath = persistenceMatch.Path;
            var remainder = matchedSegmentCount < segments.Length
                ? string.Join("/", segments.Skip(matchedSegmentCount))
                : null;
            return new AddressResolution(matchedPath, remainder);
        }

        // 3. Fallback: check the in-memory cache (covers transient nodes not yet visible to persistence queries)
        var firstSegment = segments[0];
        if (cache.TryGetValue(firstSegment, out var cachedValue) && cachedValue is MeshNode cachedNode)
        {
            var remainder = segments.Length > 1
                ? string.Join("/", segments.Skip(1))
                : null;
            logger.LogDebug("ResolvePathAsync: cache fallback found node at path={Path} for input={Input}",
                cachedNode.Path, path);
            return new AddressResolution(cachedNode.Path, remainder);
        }

        logger.LogDebug("ResolvePathAsync: no match found for path={Path}", path);
        return null;
    }

    private async Task<(MeshNode? Node, int MatchedSegments)> FindBestPersistenceMatchAsync(string[] segments)
    {
        var fullPath = string.Join("/", segments);

        // 1. Try dedicated prefix match query (single SQL call in PostgreSQL)
        var (prefixMatch, prefixSegments) = await Persistence.FindBestPrefixMatchAsync(fullPath);
        if (prefixMatch != null)
        {
            logger.LogDebug("FindBestPersistenceMatchAsync: prefix match found node at path={Path}", prefixMatch.Path);
            return (prefixMatch, prefixSegments);
        }

        // 2. Check for virtual namespaces (paths with children but no explicit node)
        for (int depth = segments.Length; depth >= 1; depth--)
        {
            var testPath = string.Join("/", segments.Take(depth));

            await using var enumerator = Persistence.GetChildrenAsync(testPath).GetAsyncEnumerator();
            if (await enumerator.MoveNextAsync())
            {
                logger.LogDebug("FindBestPersistenceMatchAsync: found virtual namespace at path={Path}", testPath);
                var ns = depth > 1 ? string.Join("/", segments.Take(depth - 1)) : null;
                var virtualNode = new MeshNode(segments[depth - 1], ns)
                {
                    Name = segments[depth - 1]
                };
                return (virtualNode, depth);
            }
        }

        // 3. Fallback: check static node providers (e.g., DocumentationNodeProvider, BuiltInAgentProvider)
        var staticNodes = staticNodeProviders
            .SelectMany(p => p.GetStaticNodes())
            .ToArray();

        for (int depth = segments.Length; depth >= 1; depth--)
        {
            var testPath = string.Join("/", segments.Take(depth));
            var node = staticNodes.FirstOrDefault(n =>
                string.Equals(n.Path, testPath, StringComparison.OrdinalIgnoreCase));
            if (node != null)
            {
                logger.LogDebug("FindBestPersistenceMatchAsync: found static node at path={Path}", testPath);
                return (node, depth);
            }
        }

        return (null, 0);
    }

    private async Task<AddressResolution?> ResolveFromConfigNodeAsync(MeshNode matchedNode, string[] segments, string _)
    {
        // When path goes deeper than the config node, check persistence for a deeper match
        // This handles dynamically created child nodes (e.g., kernel/test-kernel via CreateNodeRequest)
        if (segments.Length > matchedNode.Segments.Count &&
            matchedNode.Segments.Count > 0 &&
            segments[0].Equals(matchedNode.Segments[0], StringComparison.OrdinalIgnoreCase))
        {
            // Find the deepest existing node in persistence
            var (persistenceMatch, matchedSegmentCount) = await FindBestPersistenceMatchAsync(segments);
            if (persistenceMatch != null && matchedSegmentCount > matchedNode.Segments.Count)
            {
                // Found a deeper match in persistence
                var matchedPath = persistenceMatch.Path;
                var persistenceRemainder = matchedSegmentCount < segments.Length
                    ? string.Join("/", segments.Skip(matchedSegmentCount))
                    : null;
                return new AddressResolution(matchedPath, persistenceRemainder);
            }

            // No deeper persistence match — fall through to use the config node itself.
            // The extra segments are the layout area/id remainder (e.g., Organization/Search).
        }

        // Exact match or config node covers the full path - use it directly
        var remainder = segments.Length > matchedNode.Segments.Count
            ? string.Join("/", segments.Skip(matchedNode.Segments.Count))
            : null;

        return new AddressResolution(matchedNode.Path, remainder);
    }

    private static int ScoreMatch(MeshNode node, string[] pathSegments)
    {
        var nodeSegments = node.Segments;

        // Score = number of matching segments from start
        // Must match ALL node segments to count
        if (nodeSegments.Count > pathSegments.Length)
            return 0;

        for (int i = 0; i < nodeSegments.Count; i++)
        {
            if (!nodeSegments[i].Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase))
                return 0;
        }

        return nodeSegments.Count;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MeshNode> QueryAsync(string? parentPath, string? query = null, int? maxResults = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var count = 0;

        // Build query string for IMeshQuery
        var queryParts = new List<string>();
        queryParts.Add(string.IsNullOrEmpty(parentPath) ? "namespace:" : $"namespace:{parentPath}");
        if (!string.IsNullOrWhiteSpace(query))
            queryParts.Add(query);

        var fullQuery = string.Join(" ", queryParts);

        {
            var request = new MeshQueryRequest { Query = fullQuery, Limit = maxResults };
            await foreach (var item in meshQuery.QueryAsync(request, ct))
            {
                if (item is MeshNode child)
                {
                    yield return child;
                    count++;

                    // Apply max results if provided
                    if (maxResults.HasValue && maxResults.Value > 0 && count >= maxResults.Value)
                    {
                        yield break;
                    }
                }
            }
        }
    }
}
