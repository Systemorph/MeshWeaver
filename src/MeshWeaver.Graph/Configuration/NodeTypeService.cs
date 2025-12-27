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
/// Statically registered types (via NodeTypeRegistry) take precedence over persistence.
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

        // Ensure built-in types are registered
        BuiltInNodeTypes.EnsureRegistered();
    }

    /// <summary>
    /// Gets the path segments to search for NodeTypes, from most local to most global.
    /// For "a/b/c": returns ["a/b/c", "a/b", "a", "", "type"]
    /// </summary>
    private static IEnumerable<string> GetSearchPaths(string contextPath)
    {
        if (!string.IsNullOrEmpty(contextPath))
        {
            var path = contextPath;
            while (!string.IsNullOrEmpty(path))
            {
                yield return path;
                var lastSlash = path.LastIndexOf('/');
                path = lastSlash >= 0 ? path.Substring(0, lastSlash) : "";
            }
        }

        // Root level
        yield return "";

        // Global types namespace
        yield return "type";
    }

    /// <inheritdoc/>
    public async Task<MeshNode?> GetNodeTypeNodeAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        // Check static registry first (by ID or path)
        if (NodeTypeRegistry.TryGetById(nodeType, out var registration) && registration != null)
            return registration.Node;
        if (NodeTypeRegistry.TryGetByPath(nodeType, out registration) && registration != null)
            return registration.Node;

        // Search in the context path hierarchy
        foreach (var searchPath in GetSearchPaths(contextPath))
        {
            // Get all children at this level
            await foreach (var node in _persistence.GetChildrenAsync(searchPath))
            {
                // Match by node path (e.g., "type/graph") or by NodeTypeDefinition.Id (e.g., "graph")
                if (node.NodeType == NodeTypeNodeType && node.Content is NodeTypeDefinition ntd)
                {
                    // Match either by full path or by short Id
                    if (node.Path == nodeType || ntd.Id == nodeType)
                    {
                        return node;
                    }
                }
            }
        }

        // If nodeType contains a path separator (e.g., "Type/Organizations"),
        // also search in its parent path. This handles cases where the nodeType
        // path doesn't match the GlobalTypesNamespace (e.g., "Type" vs "type").
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
                    if (node.Path == nodeType || ntd.Id == nodeType)
                    {
                        return node;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the NodeTypeData for a node type path.
    /// </summary>
    private async Task<NodeTypeData?> GetNodeTypeDataAsync(string nodeTypePath, CancellationToken ct = default)
    {
        // Check static registry first (by path or ID)
        if (NodeTypeRegistry.TryGetByPath(nodeTypePath, out var registration) && registration != null)
            return registration.ToNodeTypeData();
        if (NodeTypeRegistry.TryGetById(nodeTypePath, out registration) && registration != null)
            return registration.ToNodeTypeData();

        try
        {
            // Get the MeshNode for this NodeType
            var meshNode = await _persistence.GetNodeAsync(nodeTypePath, ct);
            if (meshNode == null)
            {
                _logger?.LogDebug("GetNodeTypeDataAsync: NodeType at '{Path}' not found", nodeTypePath);
                return null;
            }

            var definition = meshNode.Content as NodeTypeDefinition;

            // Get CodeConfiguration from the partition
            CodeConfiguration? codeConfig = null;
            await foreach (var obj in _persistence.GetPartitionObjectsAsync(nodeTypePath, null).WithCancellation(ct))
            {
                if (obj is CodeConfiguration cc)
                {
                    codeConfig = cc;
                    break;
                }
            }

            return new NodeTypeData
            {
                Id = definition?.Id ?? meshNode.Name ?? nodeTypePath,
                Definition = definition,
                Code = codeConfig,
                Path = nodeTypePath
            };
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

        // Get NodeTypeData
        var nodeTypeData = await GetNodeTypeDataAsync(nodeTypeNode.Path, ct);
        return nodeTypeData?.Code;
    }

    /// <inheritdoc/>
    public async Task<string> GetDependencyCodeAsync(IEnumerable<string> dependencyPaths, CancellationToken ct = default)
    {
        if (dependencyPaths == null)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// Dependency types for autocomplete");
        sb.AppendLine();

        foreach (var depPath in dependencyPaths)
        {
            try
            {
                var nodeTypeData = await GetNodeTypeDataAsync(depPath, ct);
                if (nodeTypeData?.Code != null)
                {
                    var code = nodeTypeData.Code.GetCombinedCode();
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        sb.AppendLine($"// From dependency: {depPath}");
                        sb.AppendLine(code);
                        sb.AppendLine();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "GetDependencyCodeAsync: Failed to get code from {Path}", depPath);
            }
        }

        return sb.ToString();
    }
}
