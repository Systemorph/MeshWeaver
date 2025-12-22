using MeshWeaver.ContentCollections;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Creates hub configurations dynamically from JSON-based configuration.
/// Replaces the static methods in GraphDomainExtensions.
/// </summary>
public class DynamicHubConfigurationFactory
{
    private readonly ITypeCompilationService _typeCompiler;
    private readonly IConfiguration _appConfig;
    private readonly IReadOnlyDictionary<string, DataModel> _dataModels;
    private readonly IReadOnlyDictionary<string, HubFeatureConfig> _hubFeatures;
    private readonly IReadOnlyDictionary<string, ContentCollectionConfig> _contentCollections;

    public DynamicHubConfigurationFactory(
        ITypeCompilationService typeCompiler,
        IConfiguration appConfig,
        IEnumerable<DataModel> dataModels,
        IEnumerable<HubFeatureConfig> hubFeatures,
        IEnumerable<ContentCollectionConfig> contentCollections)
    {
        _typeCompiler = typeCompiler;
        _appConfig = appConfig;
        _dataModels = dataModels.ToDictionary(m => m.Id);
        _hubFeatures = hubFeatures.ToDictionary(f => f.Id);
        _contentCollections = contentCollections.ToDictionary(c => c.Id);
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

        return config => ConfigureHub(config, dataModel, hubFeatures);
    }

    /// <summary>
    /// Creates a HubConfiguration function for a node type with explicit features.
    /// </summary>
    public Func<MessageHubConfiguration, MessageHubConfiguration> CreateHubConfiguration(
        NodeTypeConfig nodeTypeConfig,
        DataModel? dataModel,
        HubFeatureConfig? hubFeatures)
    {
        return config => ConfigureHub(config, dataModel, hubFeatures);
    }

    private MessageHubConfiguration ConfigureHub(
        MessageHubConfiguration config,
        DataModel? dataModel,
        HubFeatureConfig? hubFeatures)
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

        // Add content collections if specified
        if (hubFeatures?.ContentCollections != null)
        {
            foreach (var collectionId in hubFeatures.ContentCollections)
            {
                if (_contentCollections.TryGetValue(collectionId, out var collection))
                {
                    result = AddContentCollection(result, collection);
                }
            }
        }

        return result;
    }

    private MessageHubConfiguration AddContentCollection(
        MessageHubConfiguration config,
        ContentCollectionConfig collection)
    {
        return collection.SourceType switch
        {
            "FileSystem" => config.AddFileSystemContentCollection(
                collection.Name,
                sp => GetContentPath(sp, collection)),

            "AzureBlob" => AddAzureBlobContentCollection(config, collection),

            _ => config // Unknown source type, skip
        };
    }

    private MessageHubConfiguration AddAzureBlobContentCollection(
        MessageHubConfiguration config,
        ContentCollectionConfig collection)
    {
        // Use AddContentCollection with a factory that resolves Azure settings at runtime
        return config.AddContentCollection(sp =>
        {
            var containerName = GetContainerName(sp, collection);
            var clientName = collection.ClientName ?? "default";

            return new MeshWeaver.ContentCollections.ContentCollectionConfig
            {
                Name = collection.Name,
                SourceType = "AzureBlob",
                Settings = new Dictionary<string, string>
                {
                    ["ContainerName"] = containerName,
                    ["ClientName"] = clientName
                }
            };
        });
    }

    private string GetContainerName(IServiceProvider sp, ContentCollectionConfig collection)
    {
        // First try configuration key if specified
        if (!string.IsNullOrEmpty(collection.ConfigurationKey))
        {
            var appConfig = sp.GetRequiredService<IConfiguration>();
            var containerName = appConfig.GetSection("Graph")[collection.ConfigurationKey];
            if (!string.IsNullOrEmpty(containerName))
                return containerName;
        }

        // Then try direct ContainerName
        if (!string.IsNullOrEmpty(collection.ContainerName))
            return collection.ContainerName;

        // Default to collection name
        return collection.Name;
    }

    private string GetContentPath(IServiceProvider sp, ContentCollectionConfig collection)
    {
        // First try configuration key if specified
        if (!string.IsNullOrEmpty(collection.ConfigurationKey))
        {
            var appConfig = sp.GetRequiredService<IConfiguration>();
            var configPath = appConfig.GetSection("Graph")[collection.ConfigurationKey];
            if (!string.IsNullOrEmpty(configPath))
                return configPath;
        }

        // Then try base path
        if (!string.IsNullOrEmpty(collection.BasePath))
        {
            return Path.IsPathRooted(collection.BasePath)
                ? collection.BasePath
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), collection.BasePath));
        }

        // Default fallback
        return Path.Combine(Directory.GetCurrentDirectory(), "Data", collection.Name);
    }
}
