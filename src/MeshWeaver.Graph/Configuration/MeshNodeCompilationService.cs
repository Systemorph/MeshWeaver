using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service that compiles and caches MeshNode assemblies on-demand.
/// Returns the assembly location (DLL path) for nodes with NodeType.
/// Supports multiple DataModels and LayoutAreas per type node.
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

        // Get type node partition with ALL objects (DataModels, LayoutAreas, HubFeatures)
        var partition = await nodeTypeService.GetTypeNodePartitionAsync(node.NodeType, node.Path, ct);
        if (partition == null)
        {
            logger.LogDebug("No NodeType definition found for {NodeType}", node.NodeType);
            return null;
        }

        // Check if cache is valid against the partition's newest timestamp
        // (includes all DataModels, LayoutAreas, HubFeatures, and the node itself)
        if (cacheService.IsCacheValid(nodeName, partition.NewestTimestamp))
        {
            logger.LogDebug("Using cached assembly for {NodePath}", node.Path);
            return dllPath;
        }

        // Build NodeTypeConfig from node and first DataModel (if any)
        var firstDataModel = partition.DataModels.FirstOrDefault();
        var nodeTypeConfig = new NodeTypeConfig
        {
            NodeType = node.NodeType,
            DataModelId = firstDataModel?.Id,
            DisplayName = node.Name ?? firstDataModel?.DisplayName,
            IconName = node.IconName ?? firstDataModel?.IconName,
            Description = node.Description ?? firstDataModel?.Description,
            DisplayOrder = node.DisplayOrder
        };

        try
        {
            // Compile using all partition objects
            // Note: Compiles even without DataModels for HubConfiguration-only scenarios
            await typeCompiler.CompileTypeWithCacheAsync(
                partition.DataModels,
                partition.LayoutAreas,
                node,
                nodeTypeConfig,
                partition.HubFeatures,
                ct);

            // Return the DLL path if it exists
            if (File.Exists(dllPath))
            {
                logger.LogInformation(
                    "Compiled assembly for node {NodePath} at {DllPath} with {DataModelCount} DataModels, {LayoutAreaCount} LayoutAreas",
                    node.Path, dllPath, partition.DataModels.Count, partition.LayoutAreas.Count);
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

    /// <inheritdoc />
    public async Task<NodeCompilationResult?> CompileAndGetConfigurationsAsync(MeshNode node, CancellationToken ct = default)
    {
        var assemblyLocation = await GetAssemblyLocationAsync(node, ct);
        if (string.IsNullOrEmpty(assemblyLocation))
            return null;

        var nodeName = cacheService.SanitizeNodeName(node.Path);

        try
        {
            // Load assembly using isolated context
            var assembly = cacheService.LoadAssembly(nodeName);
            if (assembly == null)
            {
                logger.LogWarning("Failed to load assembly for {NodePath}", node.Path);
                return new NodeCompilationResult(assemblyLocation, []);
            }

            // Extract NodeTypeConfigurations from MeshNodeAttribute
            var configurations = new List<NodeTypeConfiguration>();
            foreach (var type in assembly.GetTypes())
            {
                if (typeof(MeshNodeAttribute).IsAssignableFrom(type) && !type.IsAbstract)
                {
                    var attribute = (MeshNodeAttribute?)Activator.CreateInstance(type);
                    if (attribute != null)
                    {
                        configurations.AddRange(attribute.NodeTypeConfigurations);
                    }
                }
            }

            logger.LogDebug("Extracted {Count} NodeTypeConfigurations from {AssemblyLocation}",
                configurations.Count, assemblyLocation);

            return new NodeCompilationResult(assemblyLocation, configurations);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract NodeTypeConfigurations from {AssemblyLocation}", assemblyLocation);
            return new NodeCompilationResult(assemblyLocation, []);
        }
    }
}
