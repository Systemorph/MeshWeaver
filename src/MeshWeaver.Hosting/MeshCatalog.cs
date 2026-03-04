using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith.Test")]

namespace MeshWeaver.Hosting;

/// <summary>
/// Mesh catalog implementation that uses IPersistenceService for storage.
/// Configure with AddFileSystemPersistence() for file-backed storage
/// or AddInMemoryPersistence() for transient storage.
/// </summary>
public sealed class MeshCatalog(
    IMessageHub hub,
    MeshConfiguration configuration,
    IPersistenceService persistenceService,
    IMeshQuery? meshQuery = null)
    : IMeshCatalog
{
    public MeshConfiguration Configuration { get; } = configuration;
    public IPersistenceService Persistence { get; } = persistenceService;
    private readonly IMeshQuery? _meshQuery = meshQuery;
    private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
    private readonly MemoryCacheEntryOptions cacheOptions = new() { SlidingExpiration = TimeSpan.FromMinutes(15) };
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
                node = await NodeTypeService.EnrichWithNodeTypeAsync(node);
            }
            cache.Set(node.Path, node, cacheOptions);
            if (!await ValidateReadAsync(node))
                return null;
            return node;
        }

        // Try loading from persistence
        var persistenceNode = await Persistence.GetNodeAsync(address.ToString());

        // Fallback to static node providers (e.g., DocumentationNodeProvider, BuiltInAgentProvider)
        persistenceNode ??= hub.ServiceProvider.GetServices<IStaticNodeProvider>()
            .SelectMany(p => p.GetStaticNodes())
            .FirstOrDefault(n => string.Equals(n.Path, address.ToString(), StringComparison.OrdinalIgnoreCase));

        if (persistenceNode != null)
        {
            // Enrich with HubConfiguration based on NodeType (NOT the address - that would cause circular dependency)
            // EnrichWithNodeTypeAsync looks up HubConfiguration from compiled NodeType configs, triggering compilation if needed
            if (NodeTypeService != null)
            {
                persistenceNode = await NodeTypeService.EnrichWithNodeTypeAsync(persistenceNode);
            }

            cache.Set(persistenceNode.Path, persistenceNode, cacheOptions);
            if (!await ValidateReadAsync(persistenceNode))
                return null;
            return persistenceNode;
        }

        return null;
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
    public Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<MeshNode>();

        // Post CreateNodeRequest to the mesh hub where handlers are registered
        var request = new CreateNodeRequest(node) { CreatedBy = createdBy };
        var delivery = hub.Post(request, o => o.WithTarget(hub.Address));

        if (delivery == null)
        {
            tcs.SetException(new InvalidOperationException("Failed to post CreateNodeRequest"));
            return tcs.Task;
        }

        // Use typed callback for proper response handling
        hub.RegisterCallback<CreateNodeResponse>(delivery, response =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return response;
            }

            var createResponse = response.Message;
            if (createResponse.Success && createResponse.Node != null)
            {
                cache.Set(createResponse.Node.Path, createResponse.Node, cacheOptions);
                tcs.TrySetResult(createResponse.Node);
            }
            else
            {
                Exception exception = createResponse.RejectionReason switch
                {
                    NodeCreationRejectionReason.ValidationFailed =>
                        new UnauthorizedAccessException(createResponse.Error ?? "Access denied"),
                    NodeCreationRejectionReason.NodeAlreadyExists =>
                        new InvalidOperationException($"Node already exists: {node.Path}"),
                    _ => new InvalidOperationException(createResponse.Error ?? "Node creation failed")
                };
                tcs.TrySetException(exception);
            }
            return response;
        });

        return tcs.Task;
    }

    /// <inheritdoc />
    public async Task<MeshNode> CreateTransientAsync(MeshNode node, CancellationToken ct = default)
    {
        var accessService = hub.ServiceProvider.GetService<AccessService>();
        var currentUser = accessService?.Context?.Name;
        return await CreateTransientNodeAsync(node, currentUser, ct);
    }

    /// <summary>
    /// Creates a new node in Transient state without confirming it.
    /// This is internal - used by handlers that need direct node creation after validation.
    /// </summary>
    internal async Task<MeshNode> CreateTransientNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default)
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

        // 6. Enrich with HubConfiguration based on NodeType
        if (NodeTypeService != null)
        {
            transientNode = await NodeTypeService.EnrichWithNodeTypeAsync(transientNode, ct);
        }

        // 7. Save to persistence
        var savedNode = await Persistence.SaveNodeAsync(transientNode, ct);

        // 8. Update cache with enriched transient node
        cache.Set(savedNode.Path, savedNode, cacheOptions);

        logger.LogInformation("Created transient node at path {Path} by {CreatedBy}", savedNode.Path, createdBy ?? "system");

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
        if (NodeTypeService != null)
        {
            confirmedNode = await NodeTypeService.EnrichWithNodeTypeAsync(confirmedNode, ct);
        }

        // Update cache with enriched node
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
            {
                logger.LogDebug("FindBestPersistenceMatchAsync: found node at path={Path}", testPath);
                return (node, depth);
            }

            // Check if this path is a virtual namespace (has children but no explicit node).
            // This handles directories like FutuRe/EuropeRe/LineOfBusiness which contain
            // instance nodes but have no node file themselves.
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

        // Fallback: check static node providers (e.g., DocumentationNodeProvider, BuiltInAgentProvider)
        var staticNodes = hub.ServiceProvider.GetServices<IStaticNodeProvider>()
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

            // Path goes deeper than config node but nothing exists in persistence.
            // Config nodes are type templates (e.g. "User"), not routing targets for
            // arbitrary child paths. Return null so the caller gets a proper error.
            return null;
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
        if (!string.IsNullOrEmpty(parentPath))
            queryParts.Add($"path:{parentPath}");
        queryParts.Add("scope:children");
        if (!string.IsNullOrWhiteSpace(query))
            queryParts.Add(query);

        var fullQuery = string.Join(" ", queryParts);

        if (_meshQuery != null)
        {
            var request = new MeshQueryRequest { Query = fullQuery, Limit = maxResults };
            await foreach (var item in _meshQuery.QueryAsync(request, ct))
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
