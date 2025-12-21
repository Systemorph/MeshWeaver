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
        if (collection.SourceType != "FileSystem")
        {
            // TODO: Support other source types (AzureBlob, etc.)
            return config;
        }

        return config.AddFileSystemContentCollection(
            collection.Name,
            sp => GetContentPath(sp, collection));
    }

    private string GetContentPath(IServiceProvider sp, ContentCollectionConfig collection)
    {
        // First try configuration key if specified
        if (!string.IsNullOrEmpty(collection.ConfigurationKey))
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var configPath = config.GetSection("Graph")[collection.ConfigurationKey];
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
