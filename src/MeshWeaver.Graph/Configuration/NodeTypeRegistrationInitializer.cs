using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
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
        var configuration = hub.ServiceProvider.GetService<IConfiguration>();

        if (meshConfig == null)
        {
            logger?.LogWarning("MeshConfiguration not available - cannot register node types");
            return Task.CompletedTask;
        }

        // Register built-in NodeType configuration for nodes that define types
        RegisterBuiltInNodeTypeConfiguration(meshConfig, logger);

        var nodeTypes = configObjects.OfType<NodeTypeConfig>().ToList();
        var dataModels = configObjects.OfType<DataModel>().ToDictionary(m => m.Id);
        var hubFeatures = configObjects.OfType<HubFeatureConfig>().ToDictionary(f => f.Id);

        if (nodeTypes.Count == 0)
        {
            logger?.LogDebug("No node types to register");
            return Task.CompletedTask;
        }

        // Get the data directory from configuration
        var graphSection = configuration?.GetSection("Graph");
        var dataDirectoryConfig = graphSection?["DataDirectory"] ?? "Data";
        var dataDirectory = Path.IsPathRooted(dataDirectoryConfig)
            ? dataDirectoryConfig
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), dataDirectoryConfig));

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

                // Get hub features if specified
                var features = nodeType.HubFeatureId != null
                    ? hubFeatures.GetValueOrDefault(nodeType.HubFeatureId)
                    : null;

                // Create NodeTypeConfiguration with HubConfiguration that includes content collections
                var config = new NodeTypeConfiguration
                {
                    NodeType = nodeType.NodeType,
                    DataType = dataModel.CompiledType,
                    HubConfiguration = hubConfig => ConfigureHub(hubConfig, dataModel, features, nodeType.ContentCollections, dataDirectory),
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

    /// <summary>
    /// Registers the built-in NodeType configuration for nodes that define other types.
    /// These are nodes with NodeType="NodeType" that store type definitions.
    /// </summary>
    private static void RegisterBuiltInNodeTypeConfiguration(
        MeshConfiguration meshConfig,
        ILogger<NodeTypeRegistrationInitializer>? logger)
    {
        const string nodeTypeName = "NodeType";

        // Check if already registered
        if (meshConfig.NodeTypeConfigurations.ContainsKey(nodeTypeName))
        {
            logger?.LogDebug("NodeType configuration already registered");
            return;
        }

        var config = new NodeTypeConfiguration
        {
            NodeType = nodeTypeName,
            DataType = typeof(NodeTypeDefinition),
            HubConfiguration = hubConfig => hubConfig
                .AddMeshCatalogView() // Include standard node views
                .AddNodeTypeView(), // Add NodeType-specific views
            DisplayName = "Node Type",
            Description = "Defines a node type with data model and layout areas",
            IconName = "DocumentSettings",
            DisplayOrder = -100 // Show at top of type lists
        };

        meshConfig.RegisterNodeTypeConfiguration(config);
        logger?.LogDebug("Registered built-in NodeType configuration");
    }

    /// <summary>
    /// Configures a hub for a node type, including content collections and features.
    /// </summary>
    private static MessageHubConfiguration ConfigureHub(
        MessageHubConfiguration config,
        DataModel dataModel,
        HubFeatureConfig? hubFeatures,
        List<ContentCollectionMapping>? contentCollections,
        string dataDirectory)
    {
        var builder = config.ConfigureMeshHub();

        // Set data type if available
        if (dataModel.CompiledType != null)
        {
            builder = builder.WithDataType(dataModel.CompiledType);
        }

        var result = builder.Build();

        // Add dynamic node type areas if enabled (default: true)
        if (hubFeatures?.EnableDynamicNodeTypeAreas ?? true)
        {
            result = result.AddDynamicNodeTypeAreas();
        }

        // Add content collections from node type configuration
        if (contentCollections != null)
        {
            foreach (var mapping in contentCollections)
            {
                result = AddContentCollectionMapping(result, mapping, dataDirectory);
            }
        }

        return result;
    }

    /// <summary>
    /// Adds a content collection based on ContentCollectionMapping from NodeTypeConfig.
    /// The SubPath can include {id} placeholder which will be replaced with the node's address id.
    /// </summary>
    private static MessageHubConfiguration AddContentCollectionMapping(
        MessageHubConfiguration config,
        ContentCollectionMapping mapping,
        string dataDirectory)
    {
        // Capture the address id from the hub config for path resolution
        var addressId = config.Address.Id;

        return config.AddContentCollection(sp =>
        {
            var appConfig = sp.GetService<IConfiguration>();
            var storageProvider = appConfig?.GetSection("Graph")["StorageProvider"] ?? "FileSystem";

            // Resolve the sub-path, replacing {id} placeholder with the node's address id
            var resolvedSubPath = mapping.SubPath.Replace("{id}", addressId);

            if (storageProvider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
            {
                // For Azure Blob: use container + blob prefix
                var containerName = appConfig?.GetSection("Graph")["ContainerName"] ?? "graph";
                return new ContentCollections.ContentCollectionConfig
                {
                    Name = mapping.Name,
                    SourceType = "AzureBlob",
                    BasePath = resolvedSubPath, // Blob prefix
                    Settings = new Dictionary<string, string>
                    {
                        ["ContainerName"] = containerName,
                        ["ClientName"] = "default"
                    }
                };
            }
            else
            {
                // For FileSystem: use dataDirectory + subPath
                return new ContentCollections.ContentCollectionConfig
                {
                    Name = mapping.Name,
                    SourceType = "FileSystem",
                    BasePath = Path.Combine(dataDirectory, resolvedSubPath)
                };
            }
        });
    }
}
