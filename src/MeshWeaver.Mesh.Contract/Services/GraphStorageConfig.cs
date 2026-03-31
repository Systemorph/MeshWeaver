namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Configuration for graph storage.
/// Supports FileSystem, AzureBlob, and Cosmos storage types.
/// </summary>
public record GraphStorageConfig
{
    /// <summary>
    /// Storage type: "FileSystem", "AzureBlob", or "Cosmos"
    /// </summary>
    public string Type { get; init; } = "FileSystem";

    /// <summary>
    /// Base path for file system storage or container path for blob storage.
    /// </summary>
    public string? BasePath { get; init; }

    /// <summary>
    /// Connection string for Azure Blob or Cosmos DB storage.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Container name for Azure Blob storage.
    /// </summary>
    public string? ContainerName { get; init; }

    /// <summary>
    /// Database name for Cosmos DB storage.
    /// </summary>
    public string? DatabaseName { get; init; }

    /// <summary>
    /// Additional settings specific to the storage type.
    /// </summary>
    public Dictionary<string, string>? Settings { get; init; }
}

/// <summary>
/// Factory interface for creating storage adapters based on configuration.
/// Implementations are registered as keyed services by storage type.
/// </summary>
public interface IStorageAdapterFactory
{
    /// <summary>
    /// Creates a storage adapter from the provided configuration.
    /// </summary>
    /// <param name="config">Storage configuration</param>
    /// <param name="serviceProvider">Service provider for resolving dependencies</param>
    /// <returns>The configured storage adapter</returns>
    IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider);
}
