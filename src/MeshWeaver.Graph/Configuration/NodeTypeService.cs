using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Implementation of INodeTypeService using IPersistenceService for storage.
///
/// NodeTypes are scoped hierarchically - for a node at path "a/b/c", applicable NodeTypes
/// are found by walking up the path: a/b/c/*, a/b/*, a/*, root level, and type/*.
///
/// A NodeType node is identified by having NodeType = "NodeType".
/// Its partition folder contains:
/// - codeConfiguration.json: The CodeConfiguration with C# code
/// HubConfiguration is stored as a property on the NodeTypeDefinition content.
/// </summary>
public class NodeTypeService : INodeTypeService
{
    private readonly IPersistenceService _persistence;
    private readonly ILogger<NodeTypeService>? _logger;

    /// <summary>
    /// The NodeType value used for NodeType definition nodes.
    /// </summary>
    public const string NodeTypeNodeType = "NodeType";


    public NodeTypeService(IPersistenceService persistence, ILogger<NodeTypeService>? logger = null)
    {
        _persistence = persistence;
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
    public async Task<CodeConfiguration?> GetCodeConfigurationAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        var nodeTypeNode = await GetNodeTypeNodeAsync(nodeType, contextPath, ct);
        if (nodeTypeNode == null)
            return null;

        return await GetCodeConfigurationFromPartitionAsync(nodeTypeNode.Prefix, ct);
    }

    /// <inheritdoc/>
    public async Task SaveCodeConfigurationAsync(string nodeTypePath, CodeConfiguration config, CancellationToken ct = default)
    {
        await _persistence.SavePartitionObjectsAsync(nodeTypePath, null, [config], ct);
        _logger?.LogDebug("Saved CodeConfiguration to partition {Path}", nodeTypePath);
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
            var config = await GetCodeConfigurationFromPartitionAsync(nodeTypeNode.Prefix, ct);
            if (config != null)
            {
                configurations.Add(config);
            }
        }

        return configurations;
    }

    /// <summary>
    /// Gets the CodeConfiguration from a NodeType's partition.
    /// </summary>
    private async Task<CodeConfiguration?> GetCodeConfigurationFromPartitionAsync(string nodeTypePath, CancellationToken ct)
    {
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(nodeTypePath, null))
        {
            if (obj is CodeConfiguration cc)
                return cc;
        }
        return null;
    }
}
