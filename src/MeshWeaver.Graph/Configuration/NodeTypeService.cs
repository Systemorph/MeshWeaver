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

    /// <inheritdoc/>
    public async Task<MeshNode?> GetNodeTypeNodeAsync(string nodeTypePath, string contextPath, CancellationToken ct = default)
    {
        var node = await _persistence.GetNodeAsync(nodeTypePath, ct);
        if (node == null)
            return null;

        if (node.NodeType != NodeTypeNodeType)
            throw new InvalidOperationException($"Node at '{nodeTypePath}' is not a NodeType (got '{node.NodeType}')");

        return node;
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
