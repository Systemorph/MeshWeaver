using System.Text.Json;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging.Serialization;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// File system-based configuration storage service.
/// Stores configuration in _config/ subdirectories with camelCase JSON and $type discriminators.
/// Configuration is loaded per namespace/node.
/// </summary>
public class ConfigurationStorageService : IConfigurationStorageService
{
    private readonly string _baseDirectory;
    private readonly ITypeRegistry _typeRegistry;
    private JsonSerializerOptions? _jsonOptions;

    private const string ConfigPrefix = "_config";
    private const string DataModelsPath = "dataModels";
    private const string LayoutAreasPath = "layoutAreas";
    private const string ContentCollectionsPath = "contentCollections";
    private const string HubFeaturesPath = "hubFeatures";
    private const string NodeTypesPath = "nodeTypes";

    private JsonSerializerOptions JsonOptions => _jsonOptions ??= CreateJsonOptions();

    public ConfigurationStorageService(string baseDirectory, ITypeRegistry typeRegistry)
    {
        _baseDirectory = baseDirectory;
        _typeRegistry = typeRegistry;
        EnsureDirectories();
        RegisterConfigTypes();
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(GetPartitionPath(DataModelsPath));
        Directory.CreateDirectory(GetPartitionPath(LayoutAreasPath));
        Directory.CreateDirectory(GetPartitionPath(ContentCollectionsPath));
        Directory.CreateDirectory(GetPartitionPath(HubFeaturesPath));
        Directory.CreateDirectory(GetPartitionPath(NodeTypesPath));
    }

    private void RegisterConfigTypes()
    {
        _typeRegistry.WithType<DataModel>();
        _typeRegistry.WithType<LayoutAreaConfig>();
        _typeRegistry.WithType<ContentCollectionConfig>();
        _typeRegistry.WithType<HubFeatureConfig>();
        _typeRegistry.WithType<NodeTypeConfig>();
    }

    private JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        options.Converters.Add(new ObjectPolymorphicConverter(_typeRegistry));

        return options;
    }

    private string GetPartitionPath(string partition) =>
        Path.Combine(_baseDirectory, ConfigPrefix, partition);

    private string GetFilePath(string partition, string id) =>
        Path.Combine(GetPartitionPath(partition), PathEscaping.Escape(id) + ".json");

    /// <summary>
    /// Loads all configuration objects from all partitions.
    /// </summary>
    public async IAsyncEnumerable<object> LoadAllAsync(
        MeshNode node,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Load from all config partitions
        await foreach (var item in LoadFromPartitionAsync<DataModel>(DataModelsPath, ct))
            yield return item;

        await foreach (var item in LoadFromPartitionAsync<LayoutAreaConfig>(LayoutAreasPath, ct))
            yield return item;

        await foreach (var item in LoadFromPartitionAsync<ContentCollectionConfig>(ContentCollectionsPath, ct))
            yield return item;

        await foreach (var item in LoadFromPartitionAsync<HubFeatureConfig>(HubFeaturesPath, ct))
            yield return item;

        await foreach (var item in LoadFromPartitionAsync<NodeTypeConfig>(NodeTypesPath, ct))
            yield return item;
    }

    private async IAsyncEnumerable<T> LoadFromPartitionAsync<T>(
        string partition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) where T : class
    {
        var directoryPath = GetPartitionPath(partition);
        if (!Directory.Exists(directoryPath))
            yield break;

        foreach (var file in Directory.GetFiles(directoryPath, "*.json"))
        {
            T? item = null;
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                item = JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (JsonException)
            {
                // Skip malformed files
            }

            if (item != null)
                yield return item;
        }
    }

    /// <summary>
    /// Saves a configuration object to the appropriate partition.
    /// </summary>
    public async Task SaveAsync(object config, CancellationToken ct = default)
    {
        var (partition, id) = config switch
        {
            DataModel dm => (DataModelsPath, dm.Id),
            LayoutAreaConfig lac => (LayoutAreasPath, lac.Id),
            ContentCollectionConfig ccc => (ContentCollectionsPath, ccc.Id),
            HubFeatureConfig hfc => (HubFeaturesPath, hfc.Id),
            NodeTypeConfig ntc => (NodeTypesPath, ntc.NodeType),
            _ => throw new ArgumentException($"Unknown configuration type: {config.GetType().Name}")
        };

        var filePath = GetFilePath(partition, id);
        var json = JsonSerializer.Serialize(config, config.GetType(), JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public Task DeleteAsync<T>(string id, CancellationToken ct = default)
    {
        var partition = GetPartitionForType<T>();
        var filePath = GetFilePath(partition, id);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        return Task.CompletedTask;
    }

    private static string GetPartitionForType<T>()
    {
        return typeof(T).Name switch
        {
            nameof(DataModel) => DataModelsPath,
            nameof(LayoutAreaConfig) => LayoutAreasPath,
            nameof(ContentCollectionConfig) => ContentCollectionsPath,
            nameof(HubFeatureConfig) => HubFeaturesPath,
            nameof(NodeTypeConfig) => NodeTypesPath,
            _ => throw new ArgumentException($"Unknown configuration type: {typeof(T).Name}")
        };
    }
}
