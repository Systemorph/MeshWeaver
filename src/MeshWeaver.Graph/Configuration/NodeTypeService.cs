using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Implementation of INodeTypeService using hub messaging for data operations.
///
/// NodeTypes are scoped hierarchically - for a node at path "a/b/c", applicable NodeTypes
/// are found by walking up the path: a/b/c/*, a/b/*, a/*, root level, and type/*.
///
/// A NodeType node is identified by having NodeType = "NodeType".
/// NodeTypeData is accessed via GetDataRequest/Response to the NodeType hub.
/// CodeConfiguration is modified via DataChangeRequest to the NodeType hub.
/// </summary>
public class NodeTypeService : INodeTypeService
{
    private readonly IPersistenceService _persistence;
    private readonly IMessageHub _hub;
    private readonly ILogger<NodeTypeService>? _logger;

    /// <summary>
    /// The NodeType value used for NodeType definition nodes.
    /// </summary>
    public const string NodeTypeNodeType = "NodeType";

    public NodeTypeService(IPersistenceService persistence, IMessageHub hub, ILogger<NodeTypeService>? logger = null)
    {
        _persistence = persistence;
        _hub = hub;
        _logger = logger;
    }

    /// <summary>
    /// Gets the path segments to search for NodeTypes, from most local to most global.
    /// For "a/b/c": returns ["a/b/c", "a/b", "a", "", "type"]
    /// </summary>
    private static IEnumerable<string> GetSearchPaths(string contextPath)
    {
        if (string.IsNullOrEmpty(contextPath))
        {
            yield return "";
            yield return INodeTypeService.GlobalTypesPrefix;
            yield break;
        }

        var path = contextPath;
        while (!string.IsNullOrEmpty(path))
        {
            yield return path;
            var lastSlash = path.LastIndexOf('/');
            path = lastSlash >= 0 ? path.Substring(0, lastSlash) : "";
        }

        // Root level
        yield return "";

        // Global types prefix
        yield return INodeTypeService.GlobalTypesPrefix;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<MeshNode> GetNodeTypeNodesAsync(string contextPath)
    {
        var seenTypes = new HashSet<string>();

        foreach (var searchPath in GetSearchPaths(contextPath))
        {
            // Get all children at this level
            await foreach (var node in _persistence.GetChildrenAsync(searchPath))
            {
                if (node.NodeType == NodeTypeNodeType && node.Content is NodeTypeDefinition ntd)
                {
                    // Only yield if we haven't seen this type identifier yet (most local wins)
                    if (seenTypes.Add(ntd.Id))
                    {
                        yield return node;
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<MeshNode?> GetNodeTypeNodeAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        // First, search in the context path hierarchy
        foreach (var searchPath in GetSearchPaths(contextPath))
        {
            // Get all children at this level
            await foreach (var node in _persistence.GetChildrenAsync(searchPath))
            {
                // Match by node path (e.g., "type/graph") or by NodeTypeDefinition.Id (e.g., "graph")
                if (node.NodeType == NodeTypeNodeType && node.Content is NodeTypeDefinition ntd)
                {
                    // Match either by full path or by short Id
                    if (node.Prefix == nodeType || ntd.Id == nodeType)
                    {
                        return node;
                    }
                }
            }
        }

        // If nodeType contains a path separator (e.g., "Type/Organizations"),
        // also search in its parent path. This handles cases where the nodeType
        // path doesn't match the GlobalTypesPrefix (e.g., "Type" vs "type").
        if (nodeType.Contains('/'))
        {
            var lastSlash = nodeType.LastIndexOf('/');
            var nodeTypeParent = nodeType.Substring(0, lastSlash);

            // Only search if this parent wasn't already in the search paths
            // (to avoid duplicate searches)
            await foreach (var node in _persistence.GetChildrenAsync(nodeTypeParent))
            {
                if (node.NodeType == NodeTypeNodeType && node.Content is NodeTypeDefinition ntd)
                {
                    if (node.Prefix == nodeType || ntd.Id == nodeType)
                    {
                        return node;
                    }
                }
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<NodeTypeData?> GetNodeTypeDataAsync(string nodeTypePath, CancellationToken ct = default)
    {
        try
        {
            // Send GetDataRequest to the NodeType hub with NodeTypeReference
            var response = await _hub.AwaitResponse(
                new GetDataRequest(new NodeTypeReference()),
                o => o.WithTarget(new Address(nodeTypePath)),
                ct);

            if (response.Message.Error != null)
            {
                _logger?.LogWarning("GetNodeTypeDataAsync: Error getting data for {Path}: {Error}",
                    nodeTypePath, response.Message.Error);
                return null;
            }

            return response.Message.Data as NodeTypeData;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GetNodeTypeDataAsync: Failed to get data for {Path}", nodeTypePath);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<CodeConfiguration?> GetCodeConfigurationAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        // First find the NodeType node to get its path
        var nodeTypeNode = await GetNodeTypeNodeAsync(nodeType, contextPath, ct);
        if (nodeTypeNode == null)
            return null;

        // Get NodeTypeData via messaging
        var nodeTypeData = await GetNodeTypeDataAsync(nodeTypeNode.Prefix, ct);
        return nodeTypeData?.Code;
    }

    /// <inheritdoc/>
    public async Task SaveCodeConfigurationAsync(string nodeTypePath, CodeConfiguration config, CancellationToken ct = default)
    {
        // Use DataChangeRequest to update CodeConfiguration on the NodeType hub
        var request = new DataChangeRequest { Updates = [config] };

        try
        {
            var response = await _hub.AwaitResponse(
                request,
                o => o.WithTarget(new Address(nodeTypePath)),
                ct);

            if (response.Message.Status != DataChangeStatus.Committed)
            {
                _logger?.LogWarning("SaveCodeConfigurationAsync: Failed to save to {Path}, status: {Status}",
                    nodeTypePath, response.Message.Status);
            }
            else
            {
                _logger?.LogDebug("SaveCodeConfigurationAsync: Saved CodeConfiguration to {Path}", nodeTypePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "SaveCodeConfigurationAsync: Failed to save to {Path}", nodeTypePath);
            throw;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<MeshNode> GetAllNodeTypeNodesAsync()
    {
        // Search all nodes for NodeType = "NodeType"
        await foreach (var node in _persistence.GetDescendantsAsync(null))
        {
            if (node.NodeType == NodeTypeNodeType)
            {
                yield return node;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CodeConfiguration>> GetAllCodeConfigurationsAsync(CancellationToken ct = default)
    {
        var configurations = new List<CodeConfiguration>();

        await foreach (var nodeTypeNode in GetAllNodeTypeNodesAsync())
        {
            // Get CodeConfiguration via messaging
            var nodeTypeData = await GetNodeTypeDataAsync(nodeTypeNode.Prefix, ct);
            if (nodeTypeData?.Code != null)
            {
                configurations.Add(nodeTypeData.Code);
            }
        }

        return configurations;
    }
}
