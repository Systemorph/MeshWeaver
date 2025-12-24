using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service that compiles and caches MeshNode assemblies on-demand.
/// Returns the assembly location (DLL path) for nodes with DataModel types.
/// Implements IMeshNodeCompilationService from MeshWeaver.Mesh.Contract.
/// </summary>
public class MeshNodeCompilationService : IMeshNodeCompilationService
{
    private readonly ICompilationCacheService _cacheService;
    private readonly ITypeCompilationService _typeCompiler;
    private readonly INodeTypeService _nodeTypeService;
    private readonly ILogger<MeshNodeCompilationService>? _logger;

    public MeshNodeCompilationService(
        INodeTypeService nodeTypeService,
        ITypeRegistry typeRegistry,
        IOptions<CompilationCacheOptions>? cacheOptions = null,
        ILogger<MeshNodeCompilationService>? logger = null)
    {
        _nodeTypeService = nodeTypeService;
        _logger = logger;

        var options = cacheOptions ?? Options.Create(new CompilationCacheOptions());

        _cacheService = new CompilationCacheService(options);
        _typeCompiler = new TypeCompilationService(
            typeRegistry,
            logger: null,
            cacheService: _cacheService,
            cacheOptions: options);
    }

    /// <inheritdoc />
    public async Task<string?> GetAssemblyLocationAsync(MeshNode node, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(node.NodeType))
        {
            _logger?.LogDebug("Node {NodePath} has no NodeType, skipping assembly compilation", node.Path);
            return null;
        }

        var nodeName = _cacheService.SanitizeNodeName(node.Path);
        var dllPath = _cacheService.GetDllPath(nodeName);

        // Check if cache is valid
        if (_cacheService.IsCacheValid(nodeName, node.LastModified))
        {
            _logger?.LogDebug("Using cached assembly for {NodePath}", node.Path);
            return dllPath;
        }

        // Get DataModel for this node type (uses node.Path as context)
        var dataModel = await _nodeTypeService.GetDataModelAsync(node.NodeType, node.Path, ct);
        if (dataModel == null)
        {
            _logger?.LogDebug("No DataModel found for NodeType {NodeType}", node.NodeType);
            return null;
        }

        // Get HubFeatureConfig for hub configuration options
        var hubFeatures = await _nodeTypeService.GetHubFeaturesAsync(node.NodeType, node.Path, ct);

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
            await _typeCompiler.CompileTypeWithCacheAsync(
                dataModel,
                node,
                nodeTypeConfig,
                hubFeatures,
                ct);

            // Return the DLL path if it exists
            if (File.Exists(dllPath))
            {
                _logger?.LogInformation("Compiled assembly for node {NodePath} at {DllPath}", node.Path, dllPath);
                return dllPath;
            }

            _logger?.LogWarning("Assembly compilation succeeded but DLL not found at {DllPath}", dllPath);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Failed to compile assembly for node {NodePath}", node.Path);
            throw;
        }
    }
}
