using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Implementation of INodeTypeService using IPersistenceService for storage.
///
/// NodeTypes are scoped hierarchically - for a node at path "a/b/c", applicable NodeTypes
/// are found by walking up the path: a/b/c/*, a/b/*, a/*, root level, and _types/*.
///
/// A NodeType node is identified by having NodeType = "NodeType".
/// Its partition folder contains:
/// - dataModel.json: The DataModel definition
/// - layoutAreas/*.json: Layout area configurations
/// - hubFeatures.json: Optional hub feature configuration
/// </summary>
public class NodeTypeService : INodeTypeService
{
    private readonly IPersistenceService _persistence;
    private readonly ILogger<NodeTypeService>? _logger;

    /// <summary>
    /// The NodeType value used for NodeType definition nodes.
    /// </summary>
    public const string NodeTypeNodeType = "NodeType";

    /// <summary>
    /// Sub-path for layout areas within a NodeType's partition.
    /// </summary>
    public const string LayoutAreasSubPath = "layoutAreas";

    public NodeTypeService(IPersistenceService persistence, ILogger<NodeTypeService>? logger = null)
    {
        _persistence = persistence;
        _logger = logger;
    }

    /// <summary>
    /// Gets the path segments to search for NodeTypes, from most local to most global.
    /// For "a/b/c": returns ["a/b/c", "a/b", "a", "", "_types"]
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

        return null;
    }

    /// <inheritdoc/>
    public async Task<DataModel?> GetDataModelAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        var nodeTypeNode = await GetNodeTypeNodeAsync(nodeType, contextPath, ct);
        if (nodeTypeNode == null)
            return null;

        return await GetDataModelFromPartitionAsync(nodeTypeNode.Prefix, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LayoutAreaConfig>> GetLayoutAreasAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        var nodeTypeNode = await GetNodeTypeNodeAsync(nodeType, contextPath, ct);
        if (nodeTypeNode == null)
            return Array.Empty<LayoutAreaConfig>();

        return await GetLayoutAreasFromPartitionAsync(nodeTypeNode.Prefix, ct);
    }

    /// <inheritdoc/>
    public async Task<HubFeatureConfig?> GetHubFeaturesAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        var nodeTypeNode = await GetNodeTypeNodeAsync(nodeType, contextPath, ct);
        if (nodeTypeNode == null)
            return null;

        return await GetHubFeaturesFromPartitionAsync(nodeTypeNode.Prefix, ct);
    }

    /// <inheritdoc/>
    public async Task SaveDataModelAsync(string nodeTypePath, DataModel dataModel, CancellationToken ct = default)
    {
        await _persistence.SavePartitionObjectsAsync(nodeTypePath, null, [dataModel], ct);
        _logger?.LogDebug("Saved DataModel to partition {Path}", nodeTypePath);
    }

    /// <inheritdoc/>
    public async Task SaveLayoutAreaAsync(string nodeTypePath, LayoutAreaConfig layoutArea, CancellationToken ct = default)
    {
        // Load existing, update or add, save back
        var existing = await GetLayoutAreasFromPartitionAsync(nodeTypePath, ct);
        var layouts = existing.Where(l => l.Id != layoutArea.Id).ToList();
        layouts.Add(layoutArea);

        await _persistence.SavePartitionObjectsAsync(nodeTypePath, LayoutAreasSubPath, layouts.Cast<object>().ToList(), ct);
        _logger?.LogDebug("Saved LayoutAreaConfig '{Id}' to partition {Path}", layoutArea.Id, nodeTypePath);
    }

    /// <inheritdoc/>
    public async Task DeleteLayoutAreaAsync(string nodeTypePath, string layoutAreaId, CancellationToken ct = default)
    {
        // Load existing, filter out the one to delete, save back
        var existing = await GetLayoutAreasFromPartitionAsync(nodeTypePath, ct);
        var filtered = existing.Where(la => la.Id != layoutAreaId).Cast<object>().ToList();
        await _persistence.SavePartitionObjectsAsync(nodeTypePath, LayoutAreasSubPath, filtered, ct);
        _logger?.LogDebug("Deleted LayoutAreaConfig '{Id}' from partition {Path}", layoutAreaId, nodeTypePath);
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
    public async Task<IReadOnlyList<DataModel>> GetAllDataModelsAsync(CancellationToken ct = default)
    {
        var dataModels = new List<DataModel>();

        await foreach (var nodeTypeNode in GetAllNodeTypeNodesAsync())
        {
            var dataModel = await GetDataModelFromPartitionAsync(nodeTypeNode.Prefix, ct);
            if (dataModel != null)
            {
                dataModels.Add(dataModel);
            }
        }

        return dataModels;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LayoutAreaConfig>> GetAllLayoutAreasAsync(CancellationToken ct = default)
    {
        var layoutAreas = new List<LayoutAreaConfig>();

        await foreach (var nodeTypeNode in GetAllNodeTypeNodesAsync())
        {
            var areas = await GetLayoutAreasFromPartitionAsync(nodeTypeNode.Prefix, ct);
            layoutAreas.AddRange(areas);
        }

        return layoutAreas;
    }

    /// <summary>
    /// Gets the DataModel from a NodeType's partition.
    /// </summary>
    private async Task<DataModel?> GetDataModelFromPartitionAsync(string nodeTypePath, CancellationToken ct)
    {
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(nodeTypePath, null))
        {
            if (obj is DataModel dm)
                return dm;
        }
        return null;
    }

    /// <summary>
    /// Gets all LayoutAreaConfigs from a NodeType's partition.
    /// </summary>
    private async Task<IReadOnlyList<LayoutAreaConfig>> GetLayoutAreasFromPartitionAsync(string nodeTypePath, CancellationToken ct)
    {
        var layouts = new List<LayoutAreaConfig>();
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(nodeTypePath, LayoutAreasSubPath))
        {
            if (obj is LayoutAreaConfig lac)
                layouts.Add(lac);
        }
        return layouts;
    }

    /// <summary>
    /// Gets the HubFeatureConfig from a NodeType's partition.
    /// </summary>
    private async Task<HubFeatureConfig?> GetHubFeaturesFromPartitionAsync(string nodeTypePath, CancellationToken ct)
    {
        await foreach (var obj in _persistence.GetPartitionObjectsAsync(nodeTypePath, null))
        {
            if (obj is HubFeatureConfig hfc)
                return hfc;
        }
        return null;
    }
}
