using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
public sealed class MeshCatalog(
    IMessageHub hub,
    MeshConfiguration configuration,
    IPersistenceService persistenceService)
    : IMeshCatalog
{
    public MeshConfiguration Configuration { get; } = configuration;
    public IPersistenceService Persistence { get; } = persistenceService;
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new() { SlidingExpiration = TimeSpan.FromMinutes(5) };
    private readonly IMessageHub persistenceHub = hub.GetHostedHub(AddressExtensions.CreatePersistenceAddress())!;
    private readonly ILogger<MeshCatalog> logger = hub.ServiceProvider.GetRequiredService<ILogger<MeshCatalog>>();

    // Lazy-loaded because INodeTypeService is registered at hub level
    // and may not be available during MeshCatalog construction
    private INodeTypeService? nodeTypeService;
    private bool nodeTypeServiceResolved;

    private INodeTypeService? NodeTypeService
    {
        get
        {
            if (!nodeTypeServiceResolved)
            {
                nodeTypeService = hub.ServiceProvider.GetService<INodeTypeService>();
                nodeTypeServiceResolved = true;
            }
            return nodeTypeService;
        }
    }

    public async Task<MeshNode?> GetNodeAsync(Address address)
    {
        var addressKey = address.ToString();

        // Check cache first
        if (cache.TryGetValue(addressKey, out var ret))
        {
            var cachedNode = (MeshNode?)ret;
            if (cachedNode != null && !await ValidateReadAsync(cachedNode))
                return null;
            return cachedNode;
        }

        // Try exact match in configuration
        if (Configuration.Nodes.TryGetValue(addressKey, out var node))
        {
            // Enrich with HubConfiguration based on NodeType (if not already set)
            if (node.HubConfiguration == null && NodeTypeService != null)
            {
                node = NodeTypeService.EnrichWithNodeType(node);
            }
            cache.Set(node.Path, node, cacheOptions);
            if (!await ValidateReadAsync(node))
                return null;
            return node;
        }

        // Try loading from persistence
        var persistenceNode = await Persistence.GetNodeAsync(address.ToString());
        if (persistenceNode != null)
        {
            // Enrich with HubConfiguration based on NodeType (NOT the address - that would cause circular dependency)
            // EnrichWithNodeType looks up HubConfiguration from compiled NodeType configs
            if (NodeTypeService != null)
            {
                persistenceNode = NodeTypeService.EnrichWithNodeType(persistenceNode);
            }

            cache.Set(persistenceNode.Path, persistenceNode, cacheOptions);
            if (!await ValidateReadAsync(persistenceNode))
                return null;
            return persistenceNode;
        }

        // Try to find a template node that matches this address
        var templateNode = FindTemplateNode(address);
        if (templateNode != null)
        {
            // Create a virtual node based on the template with the requested address
            // Virtual nodes are marked with IsVirtual=true so CreateTransientNodeAsync can skip them
            var virtualNode = CreateVirtualNodeFromTemplate(templateNode, address);
            cache.Set(virtualNode.Path, virtualNode, cacheOptions);
            if (!await ValidateReadAsync(virtualNode))
                return null;
            return virtualNode;
        }

        return null;
    }

    /// <summary>
    /// Finds a template node that matches the given address based on AddressSegments.
    /// </summary>
    private MeshNode? FindTemplateNode(Address address)
    {
        var segments = address.Segments;
        if (segments.Length == 0)
            return null;

        // Look for template nodes in Configuration.Nodes that match the first segment
        // and have AddressSegments >= the address length
        foreach (var configNode in Configuration.Nodes.Values)
        {
            if (configNode.AddressSegments > 0 &&
                configNode.Segments.Count > 0 &&
                segments[0].Equals(configNode.Segments[0], StringComparison.OrdinalIgnoreCase) &&
                configNode.AddressSegments >= segments.Length)
            {
                return configNode;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a virtual node from a template, inheriting key properties.
    /// </summary>
    private MeshNode CreateVirtualNodeFromTemplate(MeshNode template, Address address)
    {
        var segments = address.Segments;
        var id = segments.Length > 0 ? segments[^1] : template.Id;
        var ns = segments.Length > 1 ? string.Join("/", segments.Take(segments.Length - 1)) : "";

        return new MeshNode(id, ns)
        {
            Name = template.Name,
            Description = template.Description,
            Icon = template.Icon,
            DisplayOrder = template.DisplayOrder,
            HubConfiguration = template.HubConfiguration,
            AddressSegments = template.AddressSegments,
            AssemblyLocation = template.AssemblyLocation,
            IsVirtual = true
        };
    }

    /// <summary>
    /// Validates a node read operation using unified validators.
    /// </summary>
    /// <returns>True if valid, false if read should be rejected</returns>
    private async Task<bool> ValidateReadAsync(MeshNode node, CancellationToken ct = default)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var context = new NodeValidationContext
        {
            Operation = NodeOperation.Read,
            Node = node,
            AccessContext = accessService?.Context
        };

        // Run unified validators from DI
        var validators = hub.ServiceProvider.GetServices<INodeValidator>();
        foreach (var validator in validators)
        {
            // Check if validator handles Read operations
            if (validator.SupportedOperations.Count > 0 &&
                !validator.SupportedOperations.Contains(NodeOperation.Read))
            {
                continue;
            }

            var result = await validator.ValidateAsync(context, ct);
            if (!result.IsValid)
            {
                logger.LogDebug("Validator {Validator} rejected read on node {Path}: {Error}",
                    validator.GetType().Name, node.Path, result.ErrorMessage);
                return false;
            }
        }

        return true;
    }


    public Task UpdateAsync(MeshNode node) =>
        Persistence.SaveNodeAsync(node);

    /// <inheritdoc />
    public async Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
    {
        // Create transient node and immediately confirm it
        var transientNode = await CreateTransientNodeAsync(node, createdBy, ct);
        return await ConfirmNodeAsync(transientNode.Path, ct);
    }

    /// <inheritdoc />
    public async Task<MeshNode> CreateTransientNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
    {
        // 1. Check if node already exists in cache (skip virtual nodes - they're just templates)
        if (cache.TryGetValue(node.Path, out var cachedValue) && cachedValue is MeshNode cachedNode && !cachedNode.IsVirtual)
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

        // 6. Save to persistence
        var savedNode = await Persistence.SaveNodeAsync(transientNode, ct);

        // 7. Update cache with transient node
        cache.Set(savedNode.Path, savedNode, cacheOptions);

        logger.LogInformation("Created transient node at path {Path} by {CreatedBy}", savedNode.Path, createdBy ?? "system");

        return savedNode;
    }

    /// <inheritdoc />
    public async Task<MeshNode> ConfirmNodeAsync(string path, CancellationToken ct = default)
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

        // Update cache
        cache.Set(confirmedNode.Path, confirmedNode, cacheOptions);

        logger.LogInformation("Confirmed node at path {Path}", confirmedNode.Path);

        return confirmedNode;
    }

    /// <inheritdoc />
    public async Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        // Remove from cache - for recursive, also remove all descendants
        if (recursive)
        {
            await foreach (var descendant in Persistence.GetDescendantsAsync(path).WithCancellation(ct))
            {
                cache.Remove(descendant.Path);
            }
        }
        cache.Remove(path);

        // Delete from persistence
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
            // Use the node's actual Path as the address
            var matchedPath = persistenceMatch.Path;
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

    private async Task<AddressResolution> ResolveFromConfigNodeAsync(MeshNode matchedNode, string[] segments, string _)
    {
        // For graph-style nodes (where the path IS the address), use all segments as address
        // This is determined by checking if NodeTypeService is available
        // (which means the node supports dynamic children via persistence)
        if (NodeTypeService != null &&
            matchedNode.Segments.Count > 0 &&
            segments[0].Equals(matchedNode.Segments[0], StringComparison.OrdinalIgnoreCase))
        {
            // For graph-style nodes, find the deepest existing node in persistence
            // This allows proper remainder handling when path goes beyond existing nodes
            var (persistenceMatch, matchedSegmentCount) = await FindBestPersistenceMatchAsync(segments);
            if (persistenceMatch != null)
            {
                var matchedPath = persistenceMatch.Path;
                var persistenceRemainder = matchedSegmentCount < segments.Length
                    ? string.Join("/", segments.Skip(matchedSegmentCount))
                    : null;
                return new AddressResolution(matchedPath, persistenceRemainder);
            }

            // Fallback to using just the config node Namespace if nothing in persistence
            var configRemainder = segments.Length > matchedNode.Segments.Count
                ? string.Join("/", segments.Skip(matchedNode.Segments.Count))
                : null;
            return new AddressResolution(matchedNode.Path, configRemainder);
        }

        // Legacy behavior for nodes with AddressSegments
        var addressSegmentCount = matchedNode.AddressSegments > 0
            ? matchedNode.AddressSegments
            : matchedNode.Segments.Count;

        // Build the full address: use node's Namespace (preserves case) + additional segments from path
        var nodeSegments = matchedNode.Segments;
        var addressParts = new List<string>(nodeSegments); // Start with node's Namespace segments

        // Add additional segments from path (beyond the Namespace match)
        for (int i = nodeSegments.Count; i < addressSegmentCount && i < segments.Length; i++)
        {
            addressParts.Add(segments[i]);
        }

        var addressNamespace = string.Join("/", addressParts);

        var remainder = addressSegmentCount < segments.Length
            ? string.Join("/", segments.Skip(addressSegmentCount))
            : null;

        return new AddressResolution(addressNamespace, remainder);
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
        await foreach (var child in Persistence.GetChildrenAsync(parentPath).WithCancellation(ct))
        {
            // Filter by query if provided
            if (!string.IsNullOrWhiteSpace(query))
            {
                if (!(child.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(child.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(child.Namespace?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
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

}
