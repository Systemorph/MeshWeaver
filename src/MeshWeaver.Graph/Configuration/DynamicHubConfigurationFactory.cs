using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Creates hub configurations dynamically from JSON-based configuration.
/// Replaces the static methods in GraphDomainExtensions.
/// </summary>
internal class DynamicHubConfigurationFactory
{
    private readonly ITypeCompilationService _typeCompiler;
    private readonly IConfiguration _appConfig;
    private readonly string _dataDirectory;
    private readonly IReadOnlyDictionary<string, DataModel> _dataModels;
    private readonly IReadOnlyDictionary<string, HubFeatureConfig> _hubFeatures;

    public DynamicHubConfigurationFactory(
        ITypeCompilationService typeCompiler,
        IConfiguration appConfig,
        string dataDirectory,
        IEnumerable<DataModel> dataModels,
        IEnumerable<HubFeatureConfig> hubFeatures)
    {
        _typeCompiler = typeCompiler;
        _appConfig = appConfig;
        _dataDirectory = dataDirectory;
        _dataModels = dataModels.ToDictionary(m => m.Id);
        _hubFeatures = hubFeatures.ToDictionary(f => f.Id);
    }

    /// <summary>
    /// Creates a HubConfiguration function for the given node type configuration.
    /// </summary>
    public Func<MessageHubConfiguration, MessageHubConfiguration> CreateHubConfiguration(
        NodeTypeConfig nodeTypeConfig)
    {
        var dataModel = _dataModels.GetValueOrDefault(nodeTypeConfig.DataModelId);
        var hubFeatures = nodeTypeConfig.HubFeatureId != null
            ? _hubFeatures.GetValueOrDefault(nodeTypeConfig.HubFeatureId)
            : null;

        return config => ConfigureHub(config, dataModel, hubFeatures, nodeTypeConfig.ContentCollections);
    }

    /// <summary>
    /// Creates a HubConfiguration function for a node type with explicit features.
    /// </summary>
    public Func<MessageHubConfiguration, MessageHubConfiguration> CreateHubConfiguration(
        NodeTypeConfig nodeTypeConfig,
        DataModel? dataModel,
        HubFeatureConfig? hubFeatures)
    {
        return config => ConfigureHub(config, dataModel, hubFeatures, nodeTypeConfig.ContentCollections);
    }

    private MessageHubConfiguration ConfigureHub(
        MessageHubConfiguration config,
        DataModel? dataModel,
        HubFeatureConfig? hubFeatures,
        List<ContentCollectionMapping>? contentCollections)
    {
        var builder = config.ConfigureMeshHub();

        // Set data type if available
        if (dataModel?.CompiledType != null)
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
                result = AddContentCollectionMapping(result, mapping);
            }
        }

        return result;
    }

    /// <summary>
    /// Adds a content collection based on ContentCollectionMapping from NodeTypeConfig.
    /// The SubPath can include {id} placeholder which will be replaced with the node's address id.
    /// </summary>
    private MessageHubConfiguration AddContentCollectionMapping(
        MessageHubConfiguration config,
        ContentCollectionMapping mapping)
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
                    BasePath = Path.Combine(_dataDirectory, resolvedSubPath)
                };
            }
        });
    }
}
