using MeshWeaver.Hosting;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Main initializer that orchestrates loading configuration and calling type-specific initializers.
/// Implements IMeshCatalogInitializer so it runs when the mesh catalog is created.
/// Loads configuration from INodeTypeService using partition-based storage.
/// </summary>
public class GraphConfigurationInitializer(
    INodeTypeService nodeTypeService,
    IEnumerable<IConfigurationInitializer> initializers,
    ILogger<GraphConfigurationInitializer> logger)
    : IMeshCatalogInitializer
{
    /// <summary>
    /// Loads all configuration and runs initializers.
    /// Uses INodeTypeService for partition-based storage.
    /// </summary>
    public async Task InitializeAsync(IMessageHub hub, CancellationToken ct = default)
    {
        var nodePath = hub.Address.ToString();

        logger?.LogInformation("Loading configuration for node {NodePath}...", nodePath);

        // Load configuration objects from INodeTypeService
        var configObjects = await LoadConfigurationObjectsAsync(ct);

        logger?.LogInformation("Loaded {Count} configuration objects", configObjects.Length);

        // Run initializers in priority order
        var orderedInitializers = initializers.OrderBy(i => i.Priority).ToList();

        foreach (var initializer in orderedInitializers)
        {
            try
            {
                await initializer.InitializeAsync(hub, configObjects, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Initializer {Type} failed: {Message}",
                    initializer.GetType().Name, ex.Message);
            }
        }

        logger?.LogInformation("Graph configuration initialized for {NodePath}", nodePath);
    }

    /// <summary>
    /// Loads configuration objects from INodeTypeService.
    /// </summary>
    private async Task<object[]> LoadConfigurationObjectsAsync(CancellationToken ct)
    {
        var objects = new List<object>();

        var dataModels = await nodeTypeService.GetAllDataModelsAsync(ct);
        var layoutAreas = await nodeTypeService.GetAllLayoutAreasAsync(ct);

        objects.AddRange(dataModels);
        objects.AddRange(layoutAreas);

        // Also add NodeTypeDefinitions from NodeType nodes
        await foreach (var node in nodeTypeService.GetAllNodeTypeNodesAsync())
        {
            if (node.Content is NodeTypeDefinition ntd)
            {
                // Create a NodeTypeConfig for compatibility with existing initializers
                // NodeType uses the full path (e.g., "type/story") so nodes can reference it explicitly
                var config = new NodeTypeConfig
                {
                    NodeType = node.Prefix,
                    DataModelId = ntd.Id, // By convention, DataModel.Id matches NodeType.Id
                    DisplayName = ntd.DisplayName,
                    IconName = ntd.IconName,
                    Description = ntd.Description,
                    DisplayOrder = ntd.DisplayOrder,
                    ContentCollections = ntd.ContentCollections
                };
                objects.Add(config);
            }
        }

        logger?.LogDebug("Loaded {Count} objects from INodeTypeService", objects.Count);
        return objects.ToArray();
    }
}
