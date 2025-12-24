using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service that compiles and caches MeshNode assemblies on-demand.
/// Returns the assembly location (DLL path) for nodes with DataModel types.
/// Implements IMeshNodeCompilationService from MeshWeaver.Mesh.Contract.
/// </summary>
internal class MeshNodeCompilationService(
    INodeTypeService nodeTypeService,
    ICompilationCacheService cacheService,
    ITypeCompilationService typeCompiler,
    ILogger<MeshNodeCompilationService> logger)
    : IMeshNodeCompilationService
{
    /// <inheritdoc />
    public async Task<string?> GetAssemblyLocationAsync(MeshNode node, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(node.NodeType))
        {
            logger.LogDebug("Node {NodePath} has no NodeType, skipping assembly compilation", node.Path);
            return null;
        }

        var nodeName = cacheService.SanitizeNodeName(node.Path);
        var dllPath = cacheService.GetDllPath(nodeName);

        // Check if cache is valid
        if (cacheService.IsCacheValid(nodeName, node.LastModified))
        {
            logger.LogDebug("Using cached assembly for {NodePath}", node.Path);
            return dllPath;
        }

        // Get DataModel for this node type (uses node.Path as context)
        var dataModel = await nodeTypeService.GetDataModelAsync(node.NodeType, node.Path, ct);
        if (dataModel == null)
        {
            logger.LogDebug("No DataModel found for NodeType {NodeType}", node.NodeType);
            return null;
        }

        // Get HubFeatureConfig for hub configuration options
        var hubFeatures = await nodeTypeService.GetHubFeaturesAsync(node.NodeType, node.Path, ct);

        // Build NodeTypeConfig from node information
        var nodeTypeConfig = new NodeTypeConfig
        {
            NodeType = node.NodeType,
            DataModelId = dataModel.Id,
            DisplayName = node.Name ?? dataModel.DisplayName,
            IconName = node.IconName ?? dataModel.IconName,
            Description = node.Description ?? dataModel.Description,
            DisplayOrder = node.DisplayOrder
        };

        try
        {
            // Compile (or load from cache) using the cached compilation method
            await typeCompiler.CompileTypeWithCacheAsync(
                dataModel,
                node,
                nodeTypeConfig,
                hubFeatures,
                ct);

            // Return the DLL path if it exists
            if (File.Exists(dllPath))
            {
                logger.LogInformation("Compiled assembly for node {NodePath} at {DllPath}", node.Path, dllPath);
                return dllPath;
            }

            logger.LogWarning("Assembly compilation succeeded but DLL not found at {DllPath}", dllPath);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to compile assembly for node {NodePath}", node.Path);
            throw;
        }
    }
}
