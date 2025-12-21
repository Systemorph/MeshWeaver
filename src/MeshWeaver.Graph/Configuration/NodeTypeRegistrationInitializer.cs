using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Initializer that registers NodeTypeConfiguration objects with MeshConfiguration.
/// Runs after DataModelInitializer to ensure compiled types are available.
/// </summary>
public class NodeTypeRegistrationInitializer : IConfigurationInitializer
{
    public int Priority => 150; // Run after DataModelInitializer (100) but before LayoutAreaInitializer (200)

    public Task InitializeAsync(IMessageHub hub, object[] configObjects, CancellationToken ct)
    {
        var logger = hub.ServiceProvider.GetService<ILogger<NodeTypeRegistrationInitializer>>();
        var meshConfig = hub.ServiceProvider.GetService<MeshConfiguration>();

        if (meshConfig == null)
        {
            logger?.LogWarning("MeshConfiguration not available - cannot register node types");
            return Task.CompletedTask;
        }

        var nodeTypes = configObjects.OfType<NodeTypeConfig>().ToList();
        var dataModels = configObjects.OfType<DataModel>().ToDictionary(m => m.Id);

        if (nodeTypes.Count == 0)
        {
            logger?.LogDebug("No node types to register");
            return Task.CompletedTask;
        }

        logger?.LogInformation("Registering {Count} node type configurations...", nodeTypes.Count);

        foreach (var nodeType in nodeTypes)
        {
            try
            {
                // Get the compiled type from the data model
                if (!dataModels.TryGetValue(nodeType.DataModelId, out var dataModel))
                {
                    logger?.LogWarning("DataModel '{DataModelId}' not found for node type '{NodeType}'",
                        nodeType.DataModelId, nodeType.NodeType);
                    continue;
                }

                if (dataModel.CompiledType == null)
                {
                    logger?.LogWarning("DataModel '{DataModelId}' has no compiled type for node type '{NodeType}'",
                        nodeType.DataModelId, nodeType.NodeType);
                    continue;
                }

                // Create NodeTypeConfiguration
                var config = new NodeTypeConfiguration
                {
                    NodeType = nodeType.NodeType,
                    DataType = dataModel.CompiledType,
                    HubConfiguration = c => c, // Default hub configuration (identity)
                    DisplayName = nodeType.DisplayName ?? dataModel.DisplayName ?? nodeType.NodeType,
                    Description = nodeType.Description ?? dataModel.Description,
                    IconName = nodeType.IconName ?? dataModel.IconName,
                    DisplayOrder = nodeType.DisplayOrder ?? dataModel.DisplayOrder
                };

                meshConfig.RegisterNodeTypeConfiguration(config);

                logger?.LogDebug("Registered node type '{NodeType}' with data type '{DataType}'",
                    nodeType.NodeType, dataModel.CompiledType.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "Failed to register node type '{NodeType}'", nodeType.NodeType);
            }
        }

        logger?.LogInformation("Node type registration complete. Total registered: {Count}",
            meshConfig.NodeTypeConfigurations.Count);

        return Task.CompletedTask;
    }
}
