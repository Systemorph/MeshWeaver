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
        // Search in the context path hierarchy
        foreach (var searchPath in GetSearchPaths(contextPath))
        {
            // Get all children at this level
            await foreach (var child in _persistence.GetChildrenAsync(searchPath))
            {
                // Match by node path (e.g., "type/graph") or by NodeTypeDefinition.Id (e.g., "graph")
                if (child.NodeType == NodeTypeNodeType && child.Content is NodeTypeDefinition ntd)
                {
                    // Match either by full path or by short Id
                    if (child.Path == nodeType || ntd.Id == nodeType)
                    {
                        return child;
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
            await foreach (var child in _persistence.GetChildrenAsync(nodeTypeParent))
            {
                if (child.NodeType == NodeTypeNodeType && child.Content is NodeTypeDefinition ntd)
                {
                    if (child.Path == nodeType || ntd.Id == nodeType)
                    {
                        return child;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the CodeFile for a node type path.
    /// </summary>
    private async Task<CodeFile?> GetCodeFileAsync(string nodeTypePath, CancellationToken ct = default)
    {
        try
        {
            // Get CodeFile from the partition
            await foreach (var obj in _persistence.GetPartitionObjectsAsync(nodeTypePath, null).WithCancellation(ct))
            {
                if (obj is CodeFile cf)
                    return cf;
            }

            _logger?.LogDebug("GetCodeFileAsync: CodeFile at '{Path}' not found", nodeTypePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "GetCodeFileAsync: Failed to get CodeFile for {Path}", nodeTypePath);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<CodeFile?> GetCodeFileAsync(string nodeType, string contextPath, CancellationToken ct = default)
    {
        // First find the NodeType node to get its path
        var nodeTypeNode = await GetNodeTypeNodeAsync(nodeType, contextPath, ct);
        if (nodeTypeNode == null)
            return null;

        return await GetCodeFileAsync(nodeTypeNode.Path, ct);
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
                var codeFile = await GetCodeFileAsync(depPath, ct);
                if (!string.IsNullOrWhiteSpace(codeFile?.Code))
                {
                    sb.AppendLine($"// From dependency: {depPath}");
                    sb.AppendLine(codeFile.Code);
                    sb.AppendLine();
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
